-- V0013: duplicate groups and email threads imported from upstream processing (E09-T02).
-- Binding: ADR-009 R13 (upstream group first, UUIDv5 ids), R15 (DuplicateGroup records which hash drove grouping;
-- primary = earliest FamilyDate, then lowest ControlNumberSortKey), R16 (label, never suppress), R17 (upstream hashes
-- as lower-case hex), R18-R19 (EmailThreadId from an upstream thread group or, opt-in, the ConversationIndex root;
-- EmailThreadSource), Q-09; ADR-005 P1-P10; ADR-015 D7 (RLS).
--
-- Group and thread ids are deterministic UUIDv5 values computed in the application (Opportunity.Core.Documents.
-- RelationshipIds), so a re-import or rebuild gives identical ids. The rows are written in the same transaction as
-- the documents that reference them (Opportunity.Data.Relationships.RelationshipWriter); the document foreign keys are
-- deferred to the commit so that order inside the transaction does not matter.

-- ---------------------------------------------------------------------------------------------------------------
-- DuplicateGroup (ADR-009 R15). Source 1 Upstream, 2 Computed (E09-T04). HashKind 1 UpstreamGroup, 2 UpstreamDedupeHash,
-- 3 UpstreamEmailHash, 4 Sha256Native. HashValue is the upstream group value verbatim, or the lower-case hex hash.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.duplicate_group (
    workspace_id        uuid        NOT NULL,
    duplicate_group_id  uuid        NOT NULL,
    source              smallint    NOT NULL,
    hash_kind           smallint    NOT NULL,
    hash_value          text        NOT NULL,
    -- Recomputed by the writer for every group a transaction touches; null while the group has no live member.
    primary_family_id   uuid        NULL,
    primary_document_id uuid        NULL,
    member_count        integer     NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT duplicate_group_pk PRIMARY KEY (workspace_id, duplicate_group_id),
    CONSTRAINT duplicate_group_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT duplicate_group_source_ck CHECK (
        (source = 1 AND hash_kind = 1) OR (source = 2 AND hash_kind BETWEEN 2 AND 4)),
    CONSTRAINT duplicate_group_hash_value_ck CHECK (
        length(hash_value) BETWEEN 1 AND 255
        AND hash_value !~ '[[:cntrl:]]'
        AND (hash_kind = 1 OR hash_value ~ '^[0-9a-f]{1,128}$')),
    CONSTRAINT duplicate_group_primary_ck CHECK ((primary_family_id IS NULL) = (primary_document_id IS NULL)),
    CONSTRAINT duplicate_group_member_count_ck CHECK (member_count >= 0)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

SELECT opportunity.enable_workspace_rls('opportunity.duplicate_group');

-- Consistency report: the same hash value grouped under several ids.
CREATE INDEX duplicate_group_hash_ix ON opportunity.duplicate_group (workspace_id, hash_kind, md5(hash_value));

-- ---------------------------------------------------------------------------------------------------------------
-- EmailThread (ADR-009 R18-R19). Source = EmailThreadSource: 1 Upstream (thread group value verbatim),
-- 2 ConversationIndex (thread_key = upper-case hex of the 22-byte ConversationIndex header).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.email_thread (
    workspace_id        uuid        NOT NULL,
    email_thread_id     uuid        NOT NULL,
    source              smallint    NOT NULL,
    thread_key          text        NOT NULL,
    member_count        integer     NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT email_thread_pk PRIMARY KEY (workspace_id, email_thread_id),
    CONSTRAINT email_thread_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT email_thread_source_ck CHECK (source BETWEEN 1 AND 2),
    CONSTRAINT email_thread_key_ck CHECK (
        length(thread_key) BETWEEN 1 AND 255
        AND thread_key !~ '[[:cntrl:]]'
        AND (source = 1 OR thread_key ~ '^[0-9A-F]{44}$')),
    CONSTRAINT email_thread_member_count_ck CHECK (member_count >= 0)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

SELECT opportunity.enable_workspace_rls('opportunity.email_thread');

-- ---------------------------------------------------------------------------------------------------------------
-- Document: which upstream hash upstream_dedupe_hash holds (2 UpstreamDedupeHash, 3 UpstreamEmailHash), and the
-- references to the group and thread rows. Deferred so a chunk can write documents and groups in either order.
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE opportunity.document
    ADD COLUMN upstream_dedupe_hash_kind smallint NULL,
    ADD CONSTRAINT document_upstream_dedupe_hash_kind_ck CHECK (
        upstream_dedupe_hash_kind BETWEEN 2 AND 3
        AND (upstream_dedupe_hash IS NULL) = (upstream_dedupe_hash_kind IS NULL)) NOT VALID,
    ADD CONSTRAINT document_duplicate_group_fk FOREIGN KEY (workspace_id, duplicate_group_id)
        REFERENCES opportunity.duplicate_group (workspace_id, duplicate_group_id)
        DEFERRABLE INITIALLY DEFERRED NOT VALID,
    ADD CONSTRAINT document_email_thread_fk FOREIGN KEY (workspace_id, email_thread_id)
        REFERENCES opportunity.email_thread (workspace_id, email_thread_id)
        DEFERRABLE INITIALLY DEFERRED NOT VALID;

-- Validated as separate steps so the scan of existing rows runs without an ACCESS EXCLUSIVE lock (Migrations/README.md).
ALTER TABLE opportunity.document VALIDATE CONSTRAINT document_upstream_dedupe_hash_kind_ck;
ALTER TABLE opportunity.document VALIDATE CONSTRAINT document_duplicate_group_fk;
ALTER TABLE opportunity.document VALIDATE CONSTRAINT document_email_thread_fk;
