-- V0041: render requests (E11-T02, #96). ADR-010 (render jobs and chunks), ADR-012 §1 (Rendered page sets, Review and
-- Thumbnail rasters), ADR-015 D7 (RLS).
--
-- The render worker's coordinator turns every finished import job into at most one render job (page rasters for the
-- documents the import created, changed or gave page images). render_request records that an import was handled, so
-- the coordinator never creates a second job for it; render_job_id is null when the import needed no rendering.
-- The job itself is created with a client idempotency key derived from the import job, so a crash between creating the
-- job and inserting this row cannot create two jobs either.

CREATE TABLE opportunity.render_request (
    workspace_id    uuid        NOT NULL,
    source_job_id   uuid        NOT NULL,
    render_job_id   uuid        NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT render_request_pk PRIMARY KEY (workspace_id, source_job_id),
    CONSTRAINT render_request_source_job_fk FOREIGN KEY (workspace_id, source_job_id)
        REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT render_request_render_job_fk FOREIGN KEY (workspace_id, render_job_id)
        REFERENCES opportunity.job (workspace_id, job_id)
);

SELECT opportunity.enable_workspace_rls('opportunity.render_request');
REVOKE UPDATE, DELETE ON opportunity.render_request FROM opportunity_app;

-- The coordinator looks up the finished imports of a workspace that have no request yet.
CREATE INDEX job_type_status_ix ON opportunity.job (workspace_id, job_type, status, created_at);
