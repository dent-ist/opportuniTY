-- V0026: import pre-flight results and the frozen import report (E08-T06).
-- Binding: ADR-015 D7 (forced RLS), D12 (downloads only through the protected-content gateway), ADR-013 §5 (closed audit
-- taxonomy: new actions only by migration). A pre-flight writes no document, object or index entry: only its own summary
-- and issue rows here, kept for their creator for at least 24 hours (the application keeps 48) and purged afterwards.

-- The report of a finished import, frozen in the transaction that completes it (ImportReportData JSON).
ALTER TABLE opportunity.import_batch
    ADD COLUMN report jsonb NULL,
    ADD CONSTRAINT import_batch_report_ck CHECK (report IS NULL OR (jsonb_typeof(report) = 'object' AND octet_length(report::text) <= 262144));

CREATE TABLE opportunity.import_preflight (
    workspace_id        uuid        NOT NULL,
    preflight_id        uuid        NOT NULL,
    created_by          uuid        NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    expires_at          timestamptz NOT NULL,
    -- 1 Append, 2 Overlay, 3 AppendOverlay (Opportunity.Contracts.Import.ImportMode + 1).
    mode                smallint    NOT NULL,
    source_file_name    text        NOT NULL,
    rows_read           bigint      NOT NULL,
    error_count         bigint      NOT NULL,
    warning_count       bigint      NOT NULL,
    -- Issues past the stored maximum: counted in error_count / warning_count, not kept.
    issues_dropped      bigint      NOT NULL DEFAULT 0,
    CONSTRAINT import_preflight_pk PRIMARY KEY (workspace_id, preflight_id),
    CONSTRAINT import_preflight_ck CHECK (mode BETWEEN 1 AND 3 AND expires_at > created_at
        AND length(source_file_name) BETWEEN 1 AND 255 AND rows_read >= 0 AND error_count >= 0 AND warning_count >= 0
        AND issues_dropped >= 0)
);

CREATE INDEX import_preflight_expires_ix ON opportunity.import_preflight (workspace_id, expires_at);

SELECT opportunity.enable_workspace_rls('opportunity.import_preflight');
REVOKE UPDATE, TRUNCATE ON opportunity.import_preflight FROM opportunity_app;

CREATE TABLE opportunity.import_preflight_issue (
    workspace_id        uuid        NOT NULL,
    preflight_id        uuid        NOT NULL,
    issue_no            integer     NOT NULL,
    row_no              bigint      NOT NULL,
    -- 1 Error, 2 Warning.
    severity            smallint    NOT NULL,
    control_number      text        NULL,
    column_name         text        NULL,
    code                text        NOT NULL,
    message             text        NOT NULL,
    CONSTRAINT import_preflight_issue_pk PRIMARY KEY (workspace_id, preflight_id, issue_no),
    CONSTRAINT import_preflight_issue_preflight_fk FOREIGN KEY (workspace_id, preflight_id)
        REFERENCES opportunity.import_preflight (workspace_id, preflight_id) ON DELETE CASCADE,
    CONSTRAINT import_preflight_issue_ck CHECK (issue_no >= 1 AND row_no >= 0 AND severity BETWEEN 1 AND 2
        AND length(code) BETWEEN 1 AND 100 AND length(message) <= 2000 AND length(control_number) <= 1000
        AND length(column_name) <= 1000)
);

SELECT opportunity.enable_workspace_rls('opportunity.import_preflight_issue');
REVOKE UPDATE, TRUNCATE ON opportunity.import_preflight_issue FROM opportunity_app;

-- Closed audit taxonomy (ADR-013 §5): a pre-flight run, and every download of an import report, error file or
-- pre-flight issue list.
INSERT INTO audit.audit_action (category, action) VALUES
    ('Import', 'PreflightRun'),
    ('Import', 'ReportDownloaded');

-- The report counts the pages each import linked (page_set.import_job_id).
CREATE INDEX page_set_import_job_ix ON opportunity.page_set (workspace_id, import_job_id) WHERE import_job_id IS NOT NULL;
