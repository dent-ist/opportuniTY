-- migrator:no-transaction
-- V0025: family members by family (E12-T01). Exports compute Begin/End Attachment of every exported family member from
-- its whole family (ticket review E09-T01: computed even when the load file supplied only ParentID or a group id), one
-- chunk at a time, so the family lookup must not scan the workspace.
DROP INDEX CONCURRENTLY IF EXISTS opportunity.document_family_ix;
-- migrator:statement-break
CREATE INDEX CONCURRENTLY document_family_ix ON opportunity.document (workspace_id, family_id);
