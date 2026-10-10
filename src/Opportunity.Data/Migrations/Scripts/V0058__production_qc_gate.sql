-- V0058: the production QC gate before finalization (E12-T07, #106).
-- Binding: Q-08 (a finalized production is reproducible and its manifest records how it was checked); Q-15 (a production
-- re-checks every member's authorization); Q-22 and Q-80 (the burn-in rules apply before finalizing too: a redacted
-- document is never produced natively, its redactions must lie on the pages it is produced from); Q-52 (documents the
-- caller may not see are never listed; the QC report counts them as the Bates lookup does); Q-77/Q-78 (withheld members
-- and privilege conflicts block, the latter with an audited override); ADR-015 D7 (RLS); ADR-014 §2.3 (preserved records).
--
-- production_qc_run: one row per run of the QC gate over an allocated draft: a check on request (purpose 1) or the run
--   inside a finalization (purpose 2). report is the canonical JSON of the run (every check with its status, document
--   count and, at finalization, the override reasons and the warning acknowledgement) and report_sha256 its SHA-256;
--   the finalization's passed run is the one the production's manifest names. Runs are evidence of the QC: append-only
--   for the application and preserved under a legal hold.
-- production_qc_exception: the document-level exceptions of a run, one row per check and member (production sequence).
--   Which of them a caller sees is decided when the report is read (documents the caller may not view are counted).
--   The exceptions are written set-based first and the run row, whose report counts them, last in the same
--   transaction (the reference is deferred), because neither table accepts updates.
-- production_document.placeholder_reason: 1 when the allocation made the member a placeholder because it was coded
--   Privilege Status = Withhold (specification withheldDocuments = placeholder).

ALTER TABLE opportunity.production_document
    ADD COLUMN placeholder_reason smallint NULL,
    ADD CONSTRAINT production_document_placeholder_reason_ck CHECK (placeholder_reason IS NULL OR (placeholder_reason = 1 AND output = 3));

CREATE TABLE opportunity.production_qc_run (
    workspace_id    uuid        NOT NULL,
    qc_run_id       uuid        NOT NULL,
    production_id   uuid        NOT NULL,
    -- 1 Check (on request), 2 Finalization.
    purpose         smallint    NOT NULL,
    -- 1 Passed, 2 Blocked.
    outcome         smallint    NOT NULL,
    run_by          uuid        NOT NULL,
    run_at          timestamptz NOT NULL,
    report          text        NOT NULL,
    report_sha256   bytea       NOT NULL,
    CONSTRAINT production_qc_run_pk PRIMARY KEY (workspace_id, qc_run_id),
    CONSTRAINT production_qc_run_production_fk FOREIGN KEY (workspace_id, production_id)
        REFERENCES opportunity.production (workspace_id, production_id),
    CONSTRAINT production_qc_run_ck CHECK (purpose IN (1, 2) AND outcome IN (1, 2) AND length(report) BETWEEN 2 AND 1048576
        AND octet_length(report_sha256) = 32)
);

CREATE INDEX production_qc_run_production_ix ON opportunity.production_qc_run (workspace_id, production_id, run_at DESC, qc_run_id);
-- A production is finalized once: one passed finalization run.
CREATE UNIQUE INDEX production_qc_run_finalization_uq ON opportunity.production_qc_run (workspace_id, production_id)
    WHERE purpose = 2 AND outcome = 1;

SELECT opportunity.enable_workspace_rls('opportunity.production_qc_run');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.production_qc_run FROM opportunity_app;

CREATE TABLE opportunity.production_qc_exception (
    workspace_id    uuid        NOT NULL,
    qc_run_id       uuid        NOT NULL,
    -- Opportunity.Application.Productions.ProductionQcCheck.
    check_code      smallint    NOT NULL,
    sequence        bigint      NOT NULL,
    document_id     uuid        NOT NULL,
    detail          text        NULL,
    CONSTRAINT production_qc_exception_pk PRIMARY KEY (workspace_id, qc_run_id, check_code, sequence),
    CONSTRAINT production_qc_exception_run_fk FOREIGN KEY (workspace_id, qc_run_id)
        REFERENCES opportunity.production_qc_run (workspace_id, qc_run_id) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT production_qc_exception_ck CHECK (check_code BETWEEN 1 AND 100 AND sequence >= 1 AND coalesce(length(detail), 0) <= 500)
);

SELECT opportunity.enable_workspace_rls('opportunity.production_qc_exception');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.production_qc_exception FROM opportunity_app;

-- ADR-014 §2.3: QC runs are records of the matter; a hold refuses their deletion like the production's own.
CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.production_qc_run
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.production_qc_run
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();
CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.production_qc_exception
    REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard();
CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.production_qc_exception
    FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard();

-- Q-23 / ADR-014 §5: the RetainRecords profile of a workspace deletion keeps the QC runs with their productions.
-- Same function as V0057 with the two QC tables added.
CREATE OR REPLACE FUNCTION opportunity.workspace_purge_retained(p_table text)
    RETURNS text
    LANGUAGE sql
    IMMUTABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT CASE p_table
        WHEN 'production' THEN 'true'
        WHEN 'production_document' THEN 'true'
        WHEN 'production_qc_run' THEN 'true'
        WHEN 'production_qc_exception' THEN 'true'
        WHEN 'bates_range' THEN 'true'
        WHEN 'export' THEN 't.production_id IS NOT NULL'
        WHEN 'export_document' THEN
            'EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.export_id = t.export_id AND e.production_id IS NOT NULL)'
        WHEN 'export_file' THEN
            'EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.export_id = t.export_id AND e.production_id IS NOT NULL)'
        WHEN 'stored_object' THEN
            'EXISTS (SELECT FROM opportunity.export_file f JOIN opportunity.export e ON e.workspace_id = f.workspace_id AND e.export_id = f.export_id '
            || 'WHERE f.workspace_id = $1 AND f.object_id = t.object_id AND e.production_id IS NOT NULL)'
        WHEN 'document_set_snapshot' THEN
            't.snapshot_id IN (WITH RECURSIVE kept(id) AS ('
            || 'SELECT p.snapshot_id FROM opportunity.production p WHERE p.workspace_id = $1 '
            || 'UNION SELECT e.snapshot_id FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL '
            || 'UNION SELECT s.source_snapshot_id FROM opportunity.document_set_snapshot s JOIN kept k ON s.snapshot_id = k.id '
            || 'WHERE s.workspace_id = $1) SELECT id FROM kept WHERE id IS NOT NULL)'
        WHEN 'document_set_snapshot_page' THEN
            't.snapshot_id IN (WITH RECURSIVE kept(id) AS ('
            || 'SELECT p.snapshot_id FROM opportunity.production p WHERE p.workspace_id = $1 '
            || 'UNION SELECT e.snapshot_id FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL '
            || 'UNION SELECT s.source_snapshot_id FROM opportunity.document_set_snapshot s JOIN kept k ON s.snapshot_id = k.id '
            || 'WHERE s.workspace_id = $1) SELECT id FROM kept WHERE id IS NOT NULL)'
        WHEN 'job' THEN
            '(EXISTS (SELECT FROM opportunity.production p WHERE p.workspace_id = $1 AND p.bates_job_id = t.job_id) '
            || 'OR EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL AND e.job_id = t.job_id))'
        WHEN 'redaction_set' THEN
            'EXISTS (SELECT FROM opportunity.production_document pd WHERE pd.workspace_id = $1 AND pd.redaction_set_id = t.redaction_set_id)'
    END;
$$;

-- Production.QcOverride exists since V0010 (ADR-013); QcRun records every run of the gate.
INSERT INTO audit.audit_action (category, action) VALUES
    ('Production', 'QcRun');
