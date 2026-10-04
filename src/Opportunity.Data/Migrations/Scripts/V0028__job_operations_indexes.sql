-- migrator:no-transaction
-- V0028: job operations (E06-T06). The job monitor polls changed jobs (`GET …/jobs?updatedSince=`, the job-events
-- stream) in UpdatedAt order, and names export jobs after their export; neither lookup may scan the workspace.
DROP INDEX CONCURRENTLY IF EXISTS opportunity.job_updated_ix;
-- migrator:statement-break
CREATE INDEX CONCURRENTLY job_updated_ix ON opportunity.job (workspace_id, updated_at, job_id);
-- migrator:statement-break
DROP INDEX CONCURRENTLY IF EXISTS opportunity.export_job_ix;
-- migrator:statement-break
CREATE INDEX CONCURRENTLY export_job_ix ON opportunity.export (workspace_id, job_id);
