-- V0036: saved document-list views and each user's list layout (E16-T09, #135).
-- A view is a column set (field query names with width and pin) and a sort, personal to its owner or shared with the
-- workspace (shared views need View.ManageShared; their changes are audited). grid_layout keeps, per user and
-- workspace, the view last used and the unsaved adjustments to it, so the list opens the way it was left. view_id has no
-- foreign key: a deleted or no longer visible view simply falls back to the Default view when the layout is read.
-- owner_id, modified_by and user_id reference the installation-level app_user without a foreign key (ADR-005 P3).
-- search_session.result_fields: the field values every page of a search carries (the grid's metadata and coding columns).

CREATE TABLE opportunity.grid_view (
    workspace_id    uuid        NOT NULL,
    view_id         uuid        NOT NULL,
    name            text        NOT NULL,
    owner_id        uuid        NOT NULL,
    shared          boolean     NOT NULL DEFAULT false,
    columns         jsonb       NOT NULL DEFAULT '[]'::jsonb,
    sort            jsonb       NOT NULL DEFAULT '[]'::jsonb,
    created_at      timestamptz NOT NULL DEFAULT now(),
    modified_at     timestamptz NOT NULL DEFAULT now(),
    modified_by     uuid        NOT NULL,
    version         bigint      NOT NULL DEFAULT 1,
    CONSTRAINT grid_view_pk PRIMARY KEY (workspace_id, view_id),
    CONSTRAINT grid_view_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT grid_view_name_ck CHECK (length(name) BETWEEN 1 AND 200 AND name = btrim(name)),
    CONSTRAINT grid_view_columns_ck CHECK (jsonb_typeof(columns) = 'array'),
    CONSTRAINT grid_view_sort_ck CHECK (jsonb_typeof(sort) = 'array'),
    CONSTRAINT grid_view_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.grid_view');

-- Names are unique among the shared views, and among each user's personal views (case-insensitive).
CREATE UNIQUE INDEX grid_view_shared_name_uq ON opportunity.grid_view (workspace_id, lower(name)) WHERE shared;
CREATE UNIQUE INDEX grid_view_personal_name_uq ON opportunity.grid_view (workspace_id, owner_id, lower(name)) WHERE NOT shared;
CREATE INDEX grid_view_owner_ix ON opportunity.grid_view (workspace_id, owner_id);

CREATE TABLE opportunity.grid_layout (
    workspace_id    uuid        NOT NULL,
    user_id         uuid        NOT NULL,
    view_id         uuid        NULL,
    columns         jsonb       NULL,
    sort            jsonb       NULL,
    modified_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT grid_layout_pk PRIMARY KEY (workspace_id, user_id),
    CONSTRAINT grid_layout_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT grid_layout_columns_ck CHECK (columns IS NULL OR jsonb_typeof(columns) = 'array'),
    CONSTRAINT grid_layout_sort_ck CHECK (sort IS NULL OR jsonb_typeof(sort) = 'array')
);

SELECT opportunity.enable_workspace_rls('opportunity.grid_layout');

ALTER TABLE opportunity.search_session ADD COLUMN result_fields jsonb NULL;

INSERT INTO audit.audit_action (category, action) VALUES
    ('Search', 'GridView.Created'),
    ('Search', 'GridView.Modified'),
    ('Search', 'GridView.Deleted');
