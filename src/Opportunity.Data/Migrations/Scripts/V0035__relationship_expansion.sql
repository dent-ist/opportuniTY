-- V0035: family, duplicate and email-thread expansion (E09-T03, ADR-002 §5.2.1, ADR-009 R20).
-- The expansion asked for is stored as bit flags (1 family, 2 duplicates, 4 thread; Opportunity.Core.Documents.
-- RelationshipExpansion): on a search session so every page re-plans the same expanded query, and on a snapshot header
-- so a background (re-)materialization expands exactly as asked before freezing. Saved searches gain the duplicates
-- and thread choices next to include_family. Constant defaults: older code keeps inserting rows without expansion.

ALTER TABLE opportunity.search_session ADD COLUMN expansion smallint NOT NULL DEFAULT 0;

ALTER TABLE opportunity.document_set_snapshot ADD COLUMN expansion smallint NOT NULL DEFAULT 0;

ALTER TABLE opportunity.saved_search
    ADD COLUMN include_duplicates boolean NOT NULL DEFAULT false,
    ADD COLUMN include_thread     boolean NOT NULL DEFAULT false;

ALTER TABLE opportunity.search_session
    ADD CONSTRAINT search_session_expansion_ck CHECK (expansion BETWEEN 0 AND 7);

ALTER TABLE opportunity.document_set_snapshot
    ADD CONSTRAINT document_set_snapshot_expansion_ck CHECK (expansion BETWEEN 0 AND 7);

-- The expansion is part of a snapshot's selection: like the query, it never changes after the header is written.
CREATE FUNCTION opportunity.document_set_snapshot_expansion_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'snapshot % identity and selection are immutable', OLD.snapshot_id USING ERRCODE = 'check_violation';
END
$$;

CREATE TRIGGER document_set_snapshot_expansion_guard
    BEFORE UPDATE ON opportunity.document_set_snapshot
    FOR EACH ROW
    WHEN (NEW.expansion IS DISTINCT FROM OLD.expansion)
    EXECUTE FUNCTION opportunity.document_set_snapshot_expansion_guard();
