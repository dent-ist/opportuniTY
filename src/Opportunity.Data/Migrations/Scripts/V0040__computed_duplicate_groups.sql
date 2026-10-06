-- V0040: computed duplicate groups with a family-level, scoped dedupe policy (E09-T04, #87).
-- Binding: Q-09 (optional computed grouping is family-level, labels only, never suppresses), Q-63 (primary = earliest
-- Family Date then lowest control number; the upstream dedupe hash wins over the email hash; upstream groups are never
-- rewritten), ADR-009 R14-R16; ADR-005 P1-P10; ADR-015 D7 (RLS).
--
-- duplicate_group gains the scope that formed a computed group (1 Global, 2 Custodial) and, for Custodial groups, the
-- custodian value; hash_kind gains 5 Md5 and 6 Sha1 (computed groups may key on the load file's MD5/SHA-1). Group ids
-- stay deterministic UUIDv5 values from the application (Opportunity.Core.Documents.RelationshipIds), so a re-run or a
-- re-import gives identical ids.
--
-- dedupe_policy holds one row per workspace that saved a policy (no row = the default: disabled, Auto, Global). A run
-- (a RelationshipFixup job) records its summary on the row in the transaction that applies it.

ALTER TABLE opportunity.duplicate_group
    ADD COLUMN scope     smallint NOT NULL DEFAULT 1,
    ADD COLUMN custodian text     NULL,
    DROP CONSTRAINT duplicate_group_source_ck,
    ADD CONSTRAINT duplicate_group_source_ck CHECK (
        (source = 1 AND hash_kind = 1 AND scope = 1) OR (source = 2 AND hash_kind BETWEEN 2 AND 6 AND scope BETWEEN 1 AND 2)) NOT VALID,
    ADD CONSTRAINT duplicate_group_custodian_ck CHECK (
        (scope = 2) = (custodian IS NOT NULL)
        AND (custodian IS NULL OR (length(custodian) BETWEEN 1 AND 255 AND custodian !~ '[[:cntrl:]]'))) NOT VALID;

-- Validated as separate steps so the scan of existing rows runs without an ACCESS EXCLUSIVE lock (Migrations/README.md).
ALTER TABLE opportunity.duplicate_group VALIDATE CONSTRAINT duplicate_group_source_ck;
ALTER TABLE opportunity.duplicate_group VALIDATE CONSTRAINT duplicate_group_custodian_ck;

CREATE TABLE opportunity.dedupe_policy (
    workspace_id        uuid        NOT NULL,
    enabled             boolean     NOT NULL DEFAULT false,
    -- DedupeHashSource: 1 Auto (upstream dedupe/email hash, else SHA-256), 2 Sha256, 3 Md5, 4 Sha1, 5 UpstreamHash.
    hash_source         smallint    NOT NULL DEFAULT 1,
    -- DuplicateGroupScope: 1 Global, 2 Custodial.
    scope               smallint    NOT NULL DEFAULT 1,
    custodian_field_id  integer     NULL,
    version             bigint      NOT NULL DEFAULT 1,
    modified_by         uuid        NOT NULL,
    modified_at         timestamptz NOT NULL DEFAULT now(),
    -- The last run that applied a policy (job and counts); null until the first run commits.
    last_run_job_id     uuid        NULL,
    last_run_at         timestamptz NULL,
    last_run_summary    jsonb       NULL,
    CONSTRAINT dedupe_policy_pk PRIMARY KEY (workspace_id),
    CONSTRAINT dedupe_policy_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT dedupe_policy_hash_source_ck CHECK (hash_source BETWEEN 1 AND 5),
    CONSTRAINT dedupe_policy_scope_ck CHECK (scope BETWEEN 1 AND 2),
    CONSTRAINT dedupe_policy_custodian_field_ck CHECK (custodian_field_id IS NULL OR custodian_field_id >= 1),
    CONSTRAINT dedupe_policy_version_ck CHECK (version >= 1),
    CONSTRAINT dedupe_policy_last_run_ck CHECK (
        (last_run_job_id IS NULL) = (last_run_at IS NULL)
        AND (last_run_job_id IS NULL) = (last_run_summary IS NULL)
        AND (last_run_summary IS NULL OR jsonb_typeof(last_run_summary) = 'object'))
);

SELECT opportunity.enable_workspace_rls('opportunity.dedupe_policy');
