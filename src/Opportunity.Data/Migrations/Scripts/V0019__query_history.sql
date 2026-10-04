-- V0019: per-user query history of the query bar (#186, E16-T01, Q-60).
-- One row per distinct query text a user ran in a workspace; re-running a query moves it to the top (last_run_at).
-- The store keeps the newest 50 rows per (workspace, user) and trims on every write. Rows are workspace-owned under
-- forced RLS and go with the workspace; the service additionally filters on the caller's user_id.
-- user_id references the installation-level app_user without a foreign key (ADR-005 P3, like search_session.user_id).
-- query_text is full search text (Q-16): opportunity_readonly gets no access, exactly like search_session; the
-- authoritative record of executed searches remains the Search.Executed audit event.

CREATE TABLE opportunity.query_history (
    workspace_id        uuid        NOT NULL,
    user_id             uuid        NOT NULL,
    -- Dedupe key: query text can exceed a btree entry, so the key is the SHA-256 of its UTF-8 bytes (set by the store;
    -- convert_to is not immutable, so it cannot be a generated column).
    query_key           bytea       NOT NULL,
    query_text          text        NOT NULL,
    last_run_at         timestamptz NOT NULL,
    CONSTRAINT query_history_pk PRIMARY KEY (workspace_id, user_id, query_key),
    CONSTRAINT query_history_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT query_history_key_ck CHECK (length(query_key) = 32),
    CONSTRAINT query_history_query_ck CHECK (length(query_text) BETWEEN 1 AND 100000 AND query_text = btrim(query_text))
);

SELECT opportunity.enable_workspace_rls('opportunity.query_history');

CREATE INDEX query_history_recent_ix ON opportunity.query_history (workspace_id, user_id, last_run_at DESC);

REVOKE ALL ON opportunity.query_history FROM opportunity_readonly;
