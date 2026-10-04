-- V0021: the search generation a search's point-in-time reader was opened at (Q-10 freshness, #193).
-- The applied watermark (ADR-001 §7.2) read before the reader opened. A page reports the result as current only while
-- the workspace's generation counter still equals it: nothing was committed since and everything before was applied.
-- Interim until E07-T08 adds the refresh-aware watermark; nullable (expand), so older rows read as "not known current".

ALTER TABLE opportunity.search_session ADD COLUMN served_generation bigint NULL;
ALTER TABLE opportunity.search_session ADD CONSTRAINT search_session_served_generation_ck CHECK (served_generation >= 0);
