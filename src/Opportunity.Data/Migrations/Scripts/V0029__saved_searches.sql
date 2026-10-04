-- V0028: saved searches, their sharing and folders (E07-T09, #71, Q-65).
-- A saved search keeps query-language text (re-parsed at every run, never a stored DSL or result set), the AST schema
-- version it was validated against, the grid columns and sort, the include-family option, its owner and the saved
-- searches it references directly (savedsearch:<id>, for cycle checks). It is a candidate set, never a grant: every run
-- is filtered for the person running it (ADR-015 D5.8). Sharing is per user or IdP group (group names as in
-- workspace_role_assignment). Folders form one tree per workspace. last_run_* is the most recent run by anyone.
-- query_text is search text (Q-16): opportunity_readonly gets no access to saved_search, like query_history.
-- owner_id and created_by reference the installation-level app_user without a foreign key (ADR-005 P3).

CREATE TABLE opportunity.saved_search_folder (
    workspace_id        uuid        NOT NULL,
    folder_id           uuid        NOT NULL,
    name                text        NOT NULL,
    parent_folder_id    uuid        NULL,
    created_by          uuid        NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    modified_at         timestamptz NOT NULL DEFAULT now(),
    version             bigint      NOT NULL DEFAULT 1,
    CONSTRAINT saved_search_folder_pk PRIMARY KEY (workspace_id, folder_id),
    CONSTRAINT saved_search_folder_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT saved_search_folder_parent_fk FOREIGN KEY (workspace_id, parent_folder_id)
        REFERENCES opportunity.saved_search_folder (workspace_id, folder_id),
    CONSTRAINT saved_search_folder_name_ck CHECK (length(name) BETWEEN 1 AND 200 AND name = btrim(name)),
    CONSTRAINT saved_search_folder_parent_ck CHECK (parent_folder_id IS DISTINCT FROM folder_id),
    CONSTRAINT saved_search_folder_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.saved_search_folder');

-- Sibling names are unique, case-insensitively (top-level folders share the all-zero parent key).
CREATE UNIQUE INDEX saved_search_folder_name_uq ON opportunity.saved_search_folder
    (workspace_id, coalesce(parent_folder_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));

CREATE TABLE opportunity.saved_search (
    workspace_id        uuid        NOT NULL,
    saved_search_id     uuid        NOT NULL,
    name                text        NOT NULL,
    folder_id           uuid        NULL,
    owner_id            uuid        NOT NULL,
    query_text          text        NOT NULL,
    ast_version         integer     NOT NULL,
    columns             jsonb       NOT NULL DEFAULT '[]'::jsonb,
    sort                jsonb       NOT NULL DEFAULT '[]'::jsonb,
    include_family      boolean     NOT NULL DEFAULT false,
    referenced_ids      uuid[]      NOT NULL DEFAULT '{}',
    created_at          timestamptz NOT NULL DEFAULT now(),
    modified_at         timestamptz NOT NULL DEFAULT now(),
    version             bigint      NOT NULL DEFAULT 1,
    last_run_at         timestamptz NULL,
    last_hit_count      bigint      NULL,
    last_hit_exact      boolean     NULL,
    last_run_current    boolean     NULL,
    last_run_generation bigint      NULL,
    CONSTRAINT saved_search_pk PRIMARY KEY (workspace_id, saved_search_id),
    CONSTRAINT saved_search_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT saved_search_folder_fk FOREIGN KEY (workspace_id, folder_id)
        REFERENCES opportunity.saved_search_folder (workspace_id, folder_id),
    CONSTRAINT saved_search_name_ck CHECK (length(name) BETWEEN 1 AND 200 AND name = btrim(name)),
    CONSTRAINT saved_search_query_ck CHECK (length(query_text) <= 100000),
    CONSTRAINT saved_search_ast_version_ck CHECK (ast_version >= 1),
    CONSTRAINT saved_search_columns_ck CHECK (jsonb_typeof(columns) = 'array'),
    CONSTRAINT saved_search_sort_ck CHECK (jsonb_typeof(sort) = 'array'),
    CONSTRAINT saved_search_version_ck CHECK (version >= 1),
    CONSTRAINT saved_search_last_run_ck CHECK ((last_run_at IS NULL) = (last_hit_count IS NULL) AND (last_hit_count IS NULL) = (last_hit_exact IS NULL))
);

SELECT opportunity.enable_workspace_rls('opportunity.saved_search');

CREATE INDEX saved_search_name_ix ON opportunity.saved_search (workspace_id, name, saved_search_id);
CREATE INDEX saved_search_owner_ix ON opportunity.saved_search (workspace_id, owner_id);
CREATE INDEX saved_search_folder_ix ON opportunity.saved_search (workspace_id, folder_id);

REVOKE ALL ON opportunity.saved_search FROM opportunity_readonly;

CREATE TABLE opportunity.saved_search_share (
    workspace_id        uuid        NOT NULL,
    saved_search_id     uuid        NOT NULL,
    share_id            uuid        NOT NULL,
    user_id             uuid        NULL,
    group_name          text        NULL,
    shared_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT saved_search_share_pk PRIMARY KEY (workspace_id, saved_search_id, share_id),
    CONSTRAINT saved_search_share_search_fk FOREIGN KEY (workspace_id, saved_search_id)
        REFERENCES opportunity.saved_search (workspace_id, saved_search_id) ON DELETE CASCADE,
    CONSTRAINT saved_search_share_principal_ck CHECK ((user_id IS NULL) <> (group_name IS NULL)),
    CONSTRAINT saved_search_share_group_ck CHECK (group_name IS NULL OR length(group_name) BETWEEN 1 AND 256)
);

SELECT opportunity.enable_workspace_rls('opportunity.saved_search_share');

CREATE UNIQUE INDEX saved_search_share_user_uq
    ON opportunity.saved_search_share (workspace_id, saved_search_id, user_id) WHERE user_id IS NOT NULL;
CREATE UNIQUE INDEX saved_search_share_group_uq
    ON opportunity.saved_search_share (workspace_id, saved_search_id, group_name) WHERE group_name IS NOT NULL;
CREATE INDEX saved_search_share_user_ix ON opportunity.saved_search_share (workspace_id, user_id) WHERE user_id IS NOT NULL;
CREATE INDEX saved_search_share_group_ix ON opportunity.saved_search_share (workspace_id, group_name) WHERE group_name IS NOT NULL;

INSERT INTO audit.audit_action (category, action) VALUES ('Search', 'SavedSearch.Shared');
