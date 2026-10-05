-- V0030: when a search's point-in-time reader was opened (E10-T03, ADR-002 §8).
-- Interactive readers have a maximum age (default 30 min) and a per-user cap (default 3 per workspace); a page asked
-- for after the reader aged out, or after a newer search closed it (pit_id NULL), re-establishes the reader from the
-- cursor position and reports "results refreshed" (Q-33). Nullable (expand): older rows fall back to created_at.

ALTER TABLE opportunity.search_session ADD COLUMN pit_opened_at timestamptz NULL;
