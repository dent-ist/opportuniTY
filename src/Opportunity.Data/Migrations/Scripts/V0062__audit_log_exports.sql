-- V0062: audit log viewer and audit exports (E14-T04, #118). Binding: ADR-013 §5 (closed taxonomy), §7 (audit holds
-- identifiers, never content), §8 (reading audit: Audit.Read for envelopes, Audit.ReadSearchText for search text, walls
-- apply to the reader, every query and export is itself audited); ADR-010 (chunked, resumable jobs with leases and
-- fences); ADR-015 D7 (RLS).
--
-- An audit export is a job (AuditExport, AuditExportChunk chunks) planned over UTC time windows of the matching events
-- (job_chunk membership kind 5, range_from…range_to in Unix microseconds, inclusive). Each chunk writes its window as
-- one CSV part (audit_export_part, keyed by chunk sequence, so a retried chunk rewrites the same part); when the job
-- ends the parts are hashed in order and the export records rows, bytes and SHA-256. The file is downloaded through the
-- protected-content gateway as the parts in order, by its initiator only (Audit.ExportDownloaded before the first byte).
-- The filters and the initiator's group snapshot are stored with the export; every chunk applies the initiator's document
-- visibility (denied classes and walls) as it is when the chunk runs (Q-15's re-check at execution).
--
-- Both tables are preserved records under a legal hold (delete/truncate guards) and are purged with the workspace; the
-- audit trail itself stays in schema audit.

ALTER TABLE opportunity.job DROP CONSTRAINT job_type_ck;
ALTER TABLE opportunity.job ADD CONSTRAINT job_type_ck CHECK (job_type IN (
    'Import', 'BulkCoding', 'RelationshipFixup', 'Reindex', 'Export', 'Production', 'Render', 'SearchTermReport',
    'AuditExport'));
ALTER TABLE opportunity.job DROP CONSTRAINT job_operation_kind_ck;
ALTER TABLE opportunity.job ADD CONSTRAINT job_operation_kind_ck CHECK (operation_kind IN (
    'ImportChunk', 'BulkCodingChunk', 'RelationshipChunk', 'IndexChunk', 'ReindexChunk', 'ExportChunk',
    'ProductionChunk', 'RenderChunk', 'SearchTermReportChunk', 'ProductionVolumeChunk', 'AuditExportChunk'));

ALTER TABLE opportunity.job_chunk DROP CONSTRAINT job_chunk_membership_ck;
ALTER TABLE opportunity.job_chunk ADD CONSTRAINT job_chunk_membership_ck CHECK (CASE membership_kind
    WHEN 1 THEN snapshot_id IS NOT NULL AND range_from >= 1 AND range_to >= range_from
                AND num_nulls(import_batch_id, projection_generation, document_id_from, document_id_to, document_ids) = 5
    WHEN 2 THEN import_batch_id IS NOT NULL AND range_from >= 1 AND range_to >= range_from
                AND num_nulls(snapshot_id, projection_generation, document_id_from, document_id_to, document_ids) = 5
    WHEN 3 THEN projection_generation >= 1 AND document_id_from <= document_id_to
                AND num_nulls(snapshot_id, import_batch_id, range_from, range_to, document_ids) = 5
    WHEN 4 THEN cardinality(document_ids) BETWEEN 1 AND 1000
                AND num_nulls(snapshot_id, import_batch_id, range_from, range_to, projection_generation,
                              document_id_from, document_id_to) = 7
    WHEN 5 THEN range_from >= 0 AND range_to >= range_from
                AND num_nulls(snapshot_id, import_batch_id, projection_generation, document_id_from, document_id_to,
                              document_ids) = 6
    ELSE false END);

CREATE TABLE opportunity.audit_export (
    workspace_id            uuid        NOT NULL,
    export_id               uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    filters                 jsonb       NOT NULL,
    include_search_text     boolean     NOT NULL DEFAULT false,
    -- Events that occurred before this instant (the request time, or the filter's end when earlier).
    occurred_before         timestamptz NOT NULL,
    status                  text        NOT NULL DEFAULT 'Queued',
    status_reason           text        NULL,
    created_by              uuid        NOT NULL,
    created_by_display      text        NOT NULL,
    created_by_groups       text[]      NOT NULL DEFAULT '{}',
    created_at              timestamptz NOT NULL DEFAULT now(),
    completed_at            timestamptz NULL,
    row_count               bigint      NULL,
    byte_count              bigint      NULL,
    content_sha256          text        NULL,
    claimed_by              text        NULL,
    claimed_until           timestamptz NULL,
    CONSTRAINT audit_export_pk PRIMARY KEY (workspace_id, export_id),
    CONSTRAINT audit_export_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT audit_export_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT audit_export_job_uq UNIQUE (workspace_id, job_id),
    CONSTRAINT audit_export_filters_ck CHECK (jsonb_typeof(filters) = 'object' AND octet_length(filters::text) <= 4096),
    CONSTRAINT audit_export_status_ck CHECK (status IN ('Queued', 'Running', 'Completed', 'Failed')
        AND length(status_reason) <= 2000),
    CONSTRAINT audit_export_completed_ck CHECK ((status IN ('Completed', 'Failed')) = (completed_at IS NOT NULL)),
    CONSTRAINT audit_export_result_ck CHECK (status <> 'Completed' OR (row_count >= 0 AND byte_count >= 0
        AND content_sha256 ~ '^[0-9a-f]{64}$')),
    CONSTRAINT audit_export_display_ck CHECK (length(created_by_display) BETWEEN 1 AND 512),
    CONSTRAINT audit_export_claim_ck CHECK ((claimed_by IS NULL) = (claimed_until IS NULL) AND length(claimed_by) <= 200)
);

SELECT opportunity.enable_workspace_rls('opportunity.audit_export');

CREATE INDEX audit_export_creator_ix ON opportunity.audit_export (workspace_id, created_by, created_at DESC, export_id);
CREATE INDEX audit_export_active_ix ON opportunity.audit_export (workspace_id, created_at) WHERE status IN ('Queued', 'Running');

CREATE TABLE opportunity.audit_export_part (
    workspace_id    uuid        NOT NULL,
    export_id       uuid        NOT NULL,
    chunk_sequence  integer     NOT NULL,
    row_count       integer     NOT NULL,
    content         bytea       NOT NULL,
    written_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT audit_export_part_pk PRIMARY KEY (workspace_id, export_id, chunk_sequence),
    CONSTRAINT audit_export_part_export_fk FOREIGN KEY (workspace_id, export_id)
        REFERENCES opportunity.audit_export (workspace_id, export_id),
    CONSTRAINT audit_export_part_ck CHECK (chunk_sequence >= 1 AND row_count >= 0 AND octet_length(content) <= 67108864)
);

SELECT opportunity.enable_workspace_rls('opportunity.audit_export_part');

-- Only the deletion purge (SECURITY DEFINER, refused while a preservation lock is active) removes rows.
REVOKE DELETE, TRUNCATE ON opportunity.audit_export, opportunity.audit_export_part FROM opportunity_app;

CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.audit_export
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.audit_export
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();
CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.audit_export_part
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.audit_export_part
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();

-- The viewer's job filter: events of a job (job_id) of a workspace in time order.
CREATE INDEX audit_event_job_ix ON audit.audit_event (workspace_id, job_id, occurred_at) WHERE job_id IS NOT NULL;

INSERT INTO audit.audit_action (category, action) VALUES ('Audit', 'ExportDownloaded');
