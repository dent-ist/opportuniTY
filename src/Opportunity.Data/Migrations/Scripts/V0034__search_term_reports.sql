-- V0034: search term reports (E07-T10, #72, Q-30).
-- Binding: ADR-002 (a saved search term report runs over a materialized Report snapshot: X and A of §4), ADR-010 (a
-- SearchTermReport job of SnapshotRange chunks, PostgreSQL as the ledger), ADR-013 (Search.TermReport* audit),
-- ADR-015 D7 (forced RLS), Q-16 (term expressions are search text: opportunity_readonly gets no access).
--
-- A report keeps its named terms and scope; each run (the first and every rerun) is one job. A chunk re-authorizes its
-- snapshot members for the executor (Document.View) and records, in one row per visible member, the member's family_id
-- (search_term_report_document) and one row per term it hits (search_term_report_hit). When the job finishes, the per-term
-- and total counts (hits, with family, unique, unique with family) are computed from those rows in SQL and stored on the
-- report and its terms; the rows stay as the term's hit set ("open the term as a search") until the next run replaces
-- them or the report is deleted. Counts are the executor's: documents the executor may not see never get a row.

ALTER TABLE opportunity.job DROP CONSTRAINT job_type_ck;
ALTER TABLE opportunity.job ADD CONSTRAINT job_type_ck CHECK (job_type IN (
    'Import', 'BulkCoding', 'RelationshipFixup', 'Reindex', 'Export', 'Production', 'Render', 'SearchTermReport'));
ALTER TABLE opportunity.job DROP CONSTRAINT job_operation_kind_ck;
ALTER TABLE opportunity.job ADD CONSTRAINT job_operation_kind_ck CHECK (operation_kind IN (
    'ImportChunk', 'BulkCodingChunk', 'RelationshipChunk', 'IndexChunk', 'ReindexChunk', 'ExportChunk',
    'ProductionChunk', 'RenderChunk', 'SearchTermReportChunk'));

CREATE TABLE opportunity.search_term_report (
    workspace_id                        uuid        NOT NULL,
    report_id                           uuid        NOT NULL,
    name                                text        NOT NULL,
    -- Workspace, SavedSearch or Snapshot; scope_name is the saved search's or snapshot's name when the report was made.
    scope_kind                          text        NOT NULL,
    scope_id                            uuid        NULL,
    scope_name                          text        NULL,
    -- Queued, Running, Completed or Failed (the state of the current run).
    status                              text        NOT NULL DEFAULT 'Queued',
    status_reason                       text        NULL,
    -- The current run's job; earlier runs' jobs stay in the job table.
    job_id                              uuid        NOT NULL,
    run_count                           integer     NOT NULL DEFAULT 1,
    -- The Report snapshot the counts were computed over (fixed by the first run; reruns reuse it).
    snapshot_id                         uuid        NULL,
    -- The refresh-aware search watermark when the run started, and whether the index was current then and at the end.
    search_generation                   bigint      NULL,
    index_current                       boolean     NULL,
    documents_in_scope                  bigint      NULL,
    documents_with_hits                 bigint      NULL,
    documents_with_hits_family          bigint      NULL,
    documents_without_hits              bigint      NULL,
    -- Snapshot members the executor could not see when the run read them (not counted anywhere).
    excluded_no_access                  bigint      NULL,
    created_by                          uuid        NOT NULL,
    created_by_display                  text        NOT NULL,
    created_at                          timestamptz NOT NULL DEFAULT now(),
    -- The executor of the current run (the creator, or whoever reran it) and the groups the run is authorized with.
    executed_by                         uuid        NOT NULL,
    executed_by_display                 text        NOT NULL,
    executed_by_groups                  text[]      NOT NULL DEFAULT '{}',
    executed_at                         timestamptz NULL,
    completed_at                        timestamptz NULL,
    client_idempotency_key              text        NULL,
    claimed_by                          text        NULL,
    claimed_until                       timestamptz NULL,
    CONSTRAINT search_term_report_pk PRIMARY KEY (workspace_id, report_id),
    CONSTRAINT search_term_report_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT search_term_report_snapshot_fk FOREIGN KEY (workspace_id, snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT search_term_report_name_ck CHECK (length(name) BETWEEN 1 AND 200 AND name = btrim(name) AND name !~ '[[:cntrl:]]'),
    CONSTRAINT search_term_report_scope_ck CHECK (scope_kind IN ('Workspace', 'SavedSearch', 'Snapshot')
        AND (scope_kind = 'Workspace') = (scope_id IS NULL) AND length(scope_name) <= 200),
    CONSTRAINT search_term_report_status_ck CHECK (status IN ('Queued', 'Running', 'Completed', 'Failed')
        AND length(status_reason) <= 2000),
    CONSTRAINT search_term_report_counts_ck CHECK (status <> 'Completed' OR (documents_in_scope >= 0 AND documents_with_hits >= 0
        AND documents_with_hits_family >= documents_with_hits AND documents_without_hits >= 0 AND excluded_no_access >= 0)),
    CONSTRAINT search_term_report_completed_ck CHECK ((status IN ('Completed', 'Failed')) = (completed_at IS NOT NULL)),
    CONSTRAINT search_term_report_display_ck CHECK (length(created_by_display) BETWEEN 1 AND 512 AND length(executed_by_display) BETWEEN 1 AND 512),
    CONSTRAINT search_term_report_run_count_ck CHECK (run_count >= 1),
    CONSTRAINT search_term_report_key_ck CHECK (length(client_idempotency_key) BETWEEN 1 AND 128),
    CONSTRAINT search_term_report_claim_ck CHECK ((claimed_by IS NULL) = (claimed_until IS NULL) AND length(claimed_by) <= 200)
);

CREATE UNIQUE INDEX search_term_report_job_uq ON opportunity.search_term_report (workspace_id, job_id);
CREATE UNIQUE INDEX search_term_report_key_uq ON opportunity.search_term_report (workspace_id, created_by, client_idempotency_key)
    WHERE client_idempotency_key IS NOT NULL;
-- The list (newest first) and the reports the runner still has to prepare, run or finish.
CREATE INDEX search_term_report_created_ix ON opportunity.search_term_report (workspace_id, created_at DESC, report_id);
CREATE INDEX search_term_report_active_ix ON opportunity.search_term_report (workspace_id, created_at)
    WHERE status IN ('Queued', 'Running');

SELECT opportunity.enable_workspace_rls('opportunity.search_term_report');
REVOKE ALL ON opportunity.search_term_report FROM opportunity_readonly;

CREATE TABLE opportunity.search_term_report_term (
    workspace_id                        uuid        NOT NULL,
    report_id                           uuid        NOT NULL,
    -- 1…N in the order given; the hit rows use it. term_id is the stable public identifier.
    term_no                             smallint    NOT NULL,
    term_id                             uuid        NOT NULL,
    name                                text        NOT NULL,
    expression                          text        NOT NULL,
    -- A term that does not parse, bind or run has a positioned error and no counts; the report still completes.
    error_code                          text        NULL,
    error_message                       text        NULL,
    error_position                      integer     NULL,
    documents_with_hits                 bigint      NULL,
    documents_with_hits_family          bigint      NULL,
    unique_hits                         bigint      NULL,
    unique_hits_family                  bigint      NULL,
    CONSTRAINT search_term_report_term_pk PRIMARY KEY (workspace_id, report_id, term_no),
    CONSTRAINT search_term_report_term_report_fk FOREIGN KEY (workspace_id, report_id)
        REFERENCES opportunity.search_term_report (workspace_id, report_id) ON DELETE CASCADE,
    CONSTRAINT search_term_report_term_ck CHECK (term_no BETWEEN 1 AND 1000 AND length(name) BETWEEN 1 AND 200
        AND name !~ '[[:cntrl:]]' AND length(expression) <= 10000 AND length(error_code) <= 100 AND length(error_message) <= 2000
        AND (error_code IS NULL OR documents_with_hits IS NULL))
);

CREATE UNIQUE INDEX search_term_report_term_id_uq ON opportunity.search_term_report_term (workspace_id, report_id, term_id);

SELECT opportunity.enable_workspace_rls('opportunity.search_term_report_term');
REVOKE ALL ON opportunity.search_term_report_term FROM opportunity_readonly;

-- One row per snapshot member the run's executor could see (the report's scope), with its family.
CREATE TABLE opportunity.search_term_report_document (
    workspace_id        uuid        NOT NULL,
    report_id           uuid        NOT NULL,
    job_id              uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    family_id           uuid        NOT NULL,
    CONSTRAINT search_term_report_document_pk PRIMARY KEY (workspace_id, report_id, job_id, document_id),
    CONSTRAINT search_term_report_document_report_fk FOREIGN KEY (workspace_id, report_id)
        REFERENCES opportunity.search_term_report (workspace_id, report_id) ON DELETE CASCADE
);

CREATE INDEX search_term_report_document_family_ix ON opportunity.search_term_report_document (workspace_id, report_id, job_id, family_id);

SELECT opportunity.enable_workspace_rls('opportunity.search_term_report_document');
REVOKE UPDATE, TRUNCATE ON opportunity.search_term_report_document FROM opportunity_app;

-- One row per (term, visible member) hit.
CREATE TABLE opportunity.search_term_report_hit (
    workspace_id        uuid        NOT NULL,
    report_id           uuid        NOT NULL,
    job_id              uuid        NOT NULL,
    term_no             smallint    NOT NULL,
    document_id         uuid        NOT NULL,
    CONSTRAINT search_term_report_hit_pk PRIMARY KEY (workspace_id, report_id, job_id, term_no, document_id),
    CONSTRAINT search_term_report_hit_report_fk FOREIGN KEY (workspace_id, report_id)
        REFERENCES opportunity.search_term_report (workspace_id, report_id) ON DELETE CASCADE
);

SELECT opportunity.enable_workspace_rls('opportunity.search_term_report_hit');
REVOKE UPDATE, TRUNCATE ON opportunity.search_term_report_hit FROM opportunity_app;

-- A search opened on a term's hit set (POST /searches {searchTermReportId, termId}) remembers it for every page.
ALTER TABLE opportunity.search_session ADD COLUMN scope jsonb NULL;
ALTER TABLE opportunity.search_session ADD CONSTRAINT search_session_scope_ck
    CHECK (scope IS NULL OR (jsonb_typeof(scope) = 'object' AND octet_length(scope::text) <= 1024));

INSERT INTO audit.audit_action (category, action) VALUES
    ('Search', 'TermReportExported'),
    ('Search', 'TermReportDeleted');
