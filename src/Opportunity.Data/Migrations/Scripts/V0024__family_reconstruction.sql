-- V0024: family reconstruction from BegAttach/EndAttach, ParentID and GroupIdentifier (E09-T01, #84).
-- Binding: ADR-009 §2 (three family modes, precedence pointer > group > range, R7 FamilyId = top-level parent, R8
-- FamilySequence and immediate ParentDocumentId, R10 conflicts flagged never silent, R11 version bump + reindex,
-- R25 FamilyDate), ADR-005 P1-P10, ADR-015 D7 (RLS).
--
-- The family sources are stored normalized like control_number_norm (import prefix applied) so resolution is a pure
-- function of the workspace's current documents and can always be re-run (Opportunity.Core.Documents.FamilyResolver,
-- Opportunity.Data.Relationships.FamilyWriter). Each import chunk resolves the families its documents touch inside its
-- own transaction, serialized per workspace, so families spanning chunks and volumes converge to the same result.

ALTER TABLE opportunity.document
    ADD COLUMN beg_attach_norm      text        NULL,
    ADD COLUMN end_attach_norm      text        NULL,
    ADD COLUMN parent_id_norm       text        NULL,
    ADD COLUMN group_identifier     text        NULL,
    ADD COLUMN attachment_ids_norm  text[]      NULL,
    -- A mapped upstream FamilyDate; family_date itself is derived (the top-level parent's value, R25).
    ADD COLUMN upstream_family_date timestamptz NULL,
    ADD CONSTRAINT document_beg_attach_norm_ck CHECK (length(beg_attach_norm) BETWEEN 1 AND 255),
    ADD CONSTRAINT document_end_attach_norm_ck CHECK (length(end_attach_norm) BETWEEN 1 AND 255),
    ADD CONSTRAINT document_parent_id_norm_ck CHECK (length(parent_id_norm) BETWEEN 1 AND 255),
    ADD CONSTRAINT document_group_identifier_ck CHECK (
        length(group_identifier) BETWEEN 1 AND 255 AND group_identifier !~ '[[:cntrl:]]'),
    ADD CONSTRAINT document_attachment_ids_norm_ck CHECK (cardinality(attachment_ids_norm) BETWEEN 1 AND 100000);

-- Mode A range bounds as natural sort keys (stored, so lookups never recompute them), and the range as a tenant-keyed
-- value: both bounds start with the workspace id, so the GiST expression index below leads with workspace_id (ADR-005
-- P1) without btree_gist. FamilyWriter queries the same expression ("which ranges contain this control number").
ALTER TABLE opportunity.document
    ADD COLUMN family_beg_key text COLLATE "C"
        GENERATED ALWAYS AS (opportunity.control_number_sort_key(coalesce(beg_attach_norm, end_attach_norm))) STORED,
    ADD COLUMN family_end_key text COLLATE "C"
        GENERATED ALWAYS AS (opportunity.control_number_sort_key(coalesce(end_attach_norm, beg_attach_norm))) STORED;

CREATE TYPE opportunity.control_number_key_range AS RANGE (subtype = text, collation = "C");

-- Neighbour lookups of family resolution (who points at / groups with / ranges over / lists a document).
CREATE INDEX document_parent_id_norm_ix
    ON opportunity.document (workspace_id, parent_id_norm) WHERE parent_id_norm IS NOT NULL;
CREATE INDEX document_group_identifier_ix
    ON opportunity.document (workspace_id, group_identifier) WHERE group_identifier IS NOT NULL;
CREATE INDEX document_beg_attach_norm_ix
    ON opportunity.document (workspace_id, beg_attach_norm) WHERE beg_attach_norm IS NOT NULL;
CREATE INDEX document_family_range_ix
    ON opportunity.document USING gist (
        opportunity.control_number_key_range(workspace_id::text || ' ' || family_beg_key, workspace_id::text || ' ' || family_end_key, '[]'))
    WHERE family_beg_key <= family_end_key;

-- "Who lists this control number in AttachmentIDs": a btree satellite of document.attachment_ids_norm (no GIN indexes,
-- ADR-003 R12), kept in step by FamilyWriter for every document it resolves from the chunk's writes.
CREATE TABLE opportunity.document_family_attachment (
    workspace_id    uuid    NOT NULL,
    attachment_norm text    NOT NULL,
    document_id     uuid    NOT NULL,
    CONSTRAINT document_family_attachment_pk PRIMARY KEY (workspace_id, attachment_norm, document_id),
    CONSTRAINT document_family_attachment_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id) ON DELETE CASCADE
);

CREATE INDEX document_family_attachment_document_ix ON opportunity.document_family_attachment (workspace_id, document_id);

SELECT opportunity.enable_workspace_rls('opportunity.document_family_attachment');

-- ---------------------------------------------------------------------------------------------------------------
-- Family report (ADR-009 R10): orphan attachments, ranges spanning missing control numbers, documents claimed by two
-- families, invalid ranges, pointer cycles and AttachmentIDs mismatches. Replaced for every document a resolution
-- touches, so it always describes the current families. Kind = Opportunity.Core.Documents.FamilyIssueKind.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.family_issue (
    workspace_id    uuid        NOT NULL,
    document_id     uuid        NOT NULL,
    issue_no        smallint    NOT NULL,
    kind            smallint    NOT NULL,
    message         text        NOT NULL,
    related         text[]      NOT NULL DEFAULT '{}',
    missing_count   bigint      NULL,
    detected_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT family_issue_pk PRIMARY KEY (workspace_id, document_id, issue_no),
    CONSTRAINT family_issue_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id) ON DELETE CASCADE,
    CONSTRAINT family_issue_no_ck CHECK (issue_no >= 1),
    CONSTRAINT family_issue_kind_ck CHECK (kind BETWEEN 1 AND 6),
    CONSTRAINT family_issue_message_ck CHECK (length(message) BETWEEN 1 AND 2000),
    CONSTRAINT family_issue_missing_count_ck CHECK (missing_count > 0)
);

CREATE INDEX family_issue_kind_ix ON opportunity.family_issue (workspace_id, kind);

SELECT opportunity.enable_workspace_rls('opportunity.family_issue');
