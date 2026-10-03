-- V0013: server-side search state (E07-T05, ADR-006 R9, ADR-015 D8.6).
-- A running search keeps its point-in-time reader and its search_after positions here; clients only ever hold the
-- opaque search_id and cursor_id. Rows are workspace-owned under forced RLS, and the service additionally checks that
-- the caller and session match the row, so a handle replayed by another user, in another session or in another
-- workspace is simply not found (404).
-- user_id / session_id reference installation-level tables without a foreign key (ADR-005 P3, like job.initiated_by).
-- query_text is the full search text and pit_id a bearer token: neither is readable by opportunity_readonly (Q-16).

CREATE TABLE opportunity.search_session (
    workspace_id        uuid        NOT NULL,
    search_id           uuid        NOT NULL,
    user_id             uuid        NOT NULL,
    session_id          uuid        NULL,
    query_text          text        NOT NULL,
    sort_keys           jsonb       NOT NULL,
    page_size           integer     NOT NULL,
    count_exact         boolean     NOT NULL DEFAULT false,
    highlight           boolean     NOT NULL DEFAULT true,
    pit_id              text        NULL,
    total_value         bigint      NOT NULL DEFAULT 0,
    total_exact         boolean     NOT NULL DEFAULT true,
    created_at          timestamptz NOT NULL DEFAULT now(),
    expires_at          timestamptz NOT NULL,
    CONSTRAINT search_session_pk PRIMARY KEY (workspace_id, search_id),
    CONSTRAINT search_session_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT search_session_page_size_ck CHECK (page_size BETWEEN 1 AND 500),
    CONSTRAINT search_session_query_ck CHECK (length(query_text) <= 100000),
    CONSTRAINT search_session_pit_ck CHECK (pit_id IS NULL OR length(pit_id) BETWEEN 1 AND 8192),
    CONSTRAINT search_session_total_ck CHECK (total_value >= 0),
    CONSTRAINT search_session_expiry_ck CHECK (expires_at > created_at)
);

SELECT opportunity.enable_workspace_rls('opportunity.search_session');

CREATE INDEX search_session_expiry_ix ON opportunity.search_session (workspace_id, expires_at);

-- direction 1 After (next page), 2 Before (previous page, sort reversed).
CREATE TABLE opportunity.search_cursor (
    workspace_id        uuid        NOT NULL,
    search_id           uuid        NOT NULL,
    cursor_id           uuid        NOT NULL,
    direction           smallint    NOT NULL,
    sort_values         jsonb       NOT NULL,
    page_number         integer     NULL,
    expires_at          timestamptz NOT NULL,
    CONSTRAINT search_cursor_pk PRIMARY KEY (workspace_id, search_id, cursor_id),
    CONSTRAINT search_cursor_search_fk FOREIGN KEY (workspace_id, search_id)
        REFERENCES opportunity.search_session (workspace_id, search_id) ON DELETE CASCADE,
    CONSTRAINT search_cursor_direction_ck CHECK (direction IN (1, 2)),
    CONSTRAINT search_cursor_sort_values_ck CHECK (jsonb_typeof(sort_values) = 'array'),
    CONSTRAINT search_cursor_page_ck CHECK (page_number IS NULL OR page_number >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.search_cursor');

REVOKE ALL ON opportunity.search_session FROM opportunity_readonly;
REVOKE ALL ON opportunity.search_cursor FROM opportunity_readonly;
