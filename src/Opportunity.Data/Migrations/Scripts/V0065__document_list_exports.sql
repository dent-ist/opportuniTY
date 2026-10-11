-- V0065: document list export to CSV (#181; familiarity guide "Export List (CSV)"). Binding: ADR-002 (the selection is
-- frozen into a materialized snapshot, purpose ListExport), ADR-010 (chunked, resumable job with leases and fences),
-- ADR-015 D7 (RLS), Q-15 (documents re-authorized when each chunk runs; denied ones are left out, counted and audited).
--
-- A list export is a ListExport job over a ListExport snapshot (job_chunk membership kind 1, snapshot ordinal ranges),
-- run in the API host like audit exports. Each chunk writes its rows as one CSV part (list_export_part, keyed by chunk
-- sequence, so a retried chunk rewrites the same part); when the job ends the header and the parts are hashed in order
-- and the export records rows, exclusions, bytes and SHA-256. The file is downloaded through the protected-content
-- gateway as the parts in order, by its initiator only (Export.ListDownloaded before the first byte).
--
-- Both tables are preserved records under a legal hold (delete/truncate guards) and are purged with the workspace.
-- ListExport snapshots expire like BulkCoding ones, a retention period after their jobs end.

ALTER TABLE opportunity.job DROP CONSTRAINT job_type_ck;
ALTER TABLE opportunity.job ADD CONSTRAINT job_type_ck CHECK (job_type IN (
    'Import', 'BulkCoding', 'RelationshipFixup', 'Reindex', 'Export', 'Production', 'Render', 'SearchTermReport',
    'AuditExport', 'ListExport'));
ALTER TABLE opportunity.job DROP CONSTRAINT job_operation_kind_ck;
ALTER TABLE opportunity.job ADD CONSTRAINT job_operation_kind_ck CHECK (operation_kind IN (
    'ImportChunk', 'BulkCodingChunk', 'RelationshipChunk', 'IndexChunk', 'ReindexChunk', 'ExportChunk',
    'ProductionChunk', 'RenderChunk', 'SearchTermReportChunk', 'ProductionVolumeChunk', 'AuditExportChunk',
    'ListExportChunk'));

ALTER TABLE opportunity.document_set_snapshot DROP CONSTRAINT document_set_snapshot_purpose_ck;
ALTER TABLE opportunity.document_set_snapshot ADD CONSTRAINT document_set_snapshot_purpose_ck
    CHECK (purpose IN ('BulkCoding', 'Export', 'Production', 'Report', 'ReviewBatch', 'ListExport'));

CREATE TABLE opportunity.list_export (
    workspace_id            uuid        NOT NULL,
    export_id               uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    snapshot_id             uuid        NOT NULL,
    name                    text        NOT NULL,
    -- [{"fieldId": 1, "header": "Control Number"}, …]: Control Number first, then the view's columns in order.
    columns                 jsonb       NOT NULL,
    status                  text        NOT NULL DEFAULT 'Queued',
    status_reason           text        NULL,
    created_by              uuid        NOT NULL,
    created_by_display      text        NOT NULL,
    created_by_groups       text[]      NOT NULL DEFAULT '{}',
    created_at              timestamptz NOT NULL DEFAULT now(),
    completed_at            timestamptz NULL,
    row_count               bigint      NULL,
    excluded_count          bigint      NULL,
    byte_count              bigint      NULL,
    content_sha256          text        NULL,
    claimed_by              text        NULL,
    claimed_until           timestamptz NULL,
    CONSTRAINT list_export_pk PRIMARY KEY (workspace_id, export_id),
    CONSTRAINT list_export_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT list_export_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT list_export_snapshot_fk FOREIGN KEY (workspace_id, snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT list_export_job_uq UNIQUE (workspace_id, job_id),
    CONSTRAINT list_export_name_ck CHECK (length(name) BETWEEN 1 AND 200),
    CONSTRAINT list_export_columns_ck CHECK (jsonb_typeof(columns) = 'array' AND jsonb_array_length(columns) BETWEEN 1 AND 501
        AND octet_length(columns::text) <= 262144),
    CONSTRAINT list_export_status_ck CHECK (status IN ('Queued', 'Running', 'Completed', 'Failed')
        AND length(status_reason) <= 2000),
    CONSTRAINT list_export_completed_ck CHECK ((status IN ('Completed', 'Failed')) = (completed_at IS NOT NULL)),
    CONSTRAINT list_export_result_ck CHECK (status <> 'Completed' OR (row_count >= 0 AND excluded_count >= 0 AND byte_count >= 0
        AND content_sha256 ~ '^[0-9a-f]{64}$')),
    CONSTRAINT list_export_display_ck CHECK (length(created_by_display) BETWEEN 1 AND 512),
    CONSTRAINT list_export_claim_ck CHECK ((claimed_by IS NULL) = (claimed_until IS NULL) AND length(claimed_by) <= 200)
);

SELECT opportunity.enable_workspace_rls('opportunity.list_export');

CREATE INDEX list_export_creator_ix ON opportunity.list_export (workspace_id, created_by, created_at DESC, export_id);
CREATE INDEX list_export_active_ix ON opportunity.list_export (workspace_id, created_at) WHERE status IN ('Queued', 'Running');
CREATE INDEX list_export_snapshot_ix ON opportunity.list_export (workspace_id, snapshot_id);

CREATE TABLE opportunity.list_export_part (
    workspace_id    uuid        NOT NULL,
    export_id       uuid        NOT NULL,
    chunk_sequence  integer     NOT NULL,
    row_count       integer     NOT NULL,
    excluded_count  integer     NOT NULL,
    content         bytea       NOT NULL,
    written_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT list_export_part_pk PRIMARY KEY (workspace_id, export_id, chunk_sequence),
    CONSTRAINT list_export_part_export_fk FOREIGN KEY (workspace_id, export_id)
        REFERENCES opportunity.list_export (workspace_id, export_id),
    CONSTRAINT list_export_part_ck CHECK (chunk_sequence >= 1 AND row_count >= 0 AND excluded_count >= 0
        AND octet_length(content) <= 67108864)
);

SELECT opportunity.enable_workspace_rls('opportunity.list_export_part');

-- Only the deletion purge (SECURITY DEFINER, refused while a preservation lock is active) removes rows.
REVOKE DELETE, TRUNCATE ON opportunity.list_export, opportunity.list_export_part FROM opportunity_app;

CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.list_export
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.list_export
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();
CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.list_export_part
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.list_export_part
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();

INSERT INTO audit.audit_action (category, action) VALUES
    ('Export', 'ListRequested'),
    ('Export', 'ListDownloaded');
