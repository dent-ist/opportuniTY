-- migrator:no-transaction
-- V0030: a search cursor that misses its handle is looked up by id alone, to tell a cursor replayed from another search
-- (audited AuthZ.Denied) from an expired or unknown one (not audited, Q-71); that lookup must not scan the workspace.
DROP INDEX CONCURRENTLY IF EXISTS opportunity.search_cursor_id_ix;
-- migrator:statement-break
CREATE INDEX CONCURRENTLY search_cursor_id_ix ON opportunity.search_cursor (workspace_id, cursor_id);
