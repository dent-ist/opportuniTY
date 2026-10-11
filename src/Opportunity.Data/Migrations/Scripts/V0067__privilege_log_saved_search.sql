-- V0067: privilege logs of a saved search (#182; Q-20, E13-T03).
-- A log series may now have a saved search as its source (source_kind 3): each generation freezes the saved search as
-- the generator sees it into a frozen set (snapshot_id, purpose Report) and logs its members coded Withhold or Redact,
-- like a frozen-set log. The versions of one saved search with one template form a series, so a regeneration after
-- coding changes is the next version and can be compared with the previous one. No foreign key to the saved search:
-- a saved search may be deleted, the versions keep its id and name (metadata) as generated.

ALTER TABLE opportunity.privilege_log ADD COLUMN saved_search_id uuid NULL;

ALTER TABLE opportunity.privilege_log DROP CONSTRAINT privilege_log_source_ck;
ALTER TABLE opportunity.privilege_log ADD CONSTRAINT privilege_log_source_ck CHECK (CASE source_kind
    WHEN 1 THEN production_id IS NOT NULL AND saved_search_id IS NULL
    WHEN 2 THEN production_id IS NULL AND scope_snapshot_id IS NULL AND saved_search_id IS NULL
    WHEN 3 THEN production_id IS NULL AND scope_snapshot_id IS NULL AND saved_search_id IS NOT NULL
    ELSE false END);

CREATE INDEX privilege_log_saved_search_ix ON opportunity.privilege_log (workspace_id, saved_search_id) WHERE saved_search_id IS NOT NULL;
