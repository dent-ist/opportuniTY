-- V0045: non-destructive, versioned redactions (E11-T04, #98; ADR-012 §2-§3, Q-08, Q-22).
--
-- redaction_set: the workspace's Redaction Sets (ticket review E11-T04: the viewer selects one, a production chooses
--   one). A set is retired, never deleted, so productions that froze it stay reproducible.
-- redaction_reason: the configurable reason picklist (ADR-012 §3.2), keyed by an immutable code. category drives the
--   privilege log (Privilege) and privacy handling (Privacy); box_label is the text a labelled box prints at
--   production. A reason is deactivated, never deleted (revisions reference it).
-- redaction_revision: insert-only history, one row per added, modified or removed rectangle, all rows of one save
--   sharing redaction_version (ADR-012 §3.3). State as of version V = per redaction_id the row with the highest
--   redaction_version <= V, dropped when its operation is Remove (§3.5). Geometry is in normalized page space at
--   rotation 0: integers in millionths of the page width/height (§2.1). The application role may only INSERT and
--   SELECT; UPDATE and DELETE belong to the lifecycle role (ADR-014).
-- document_redaction_state: the per document and set cache of that query (current_version, active_count), locked by
--   every save; If-Match is checked against current_version under that lock (§3.4).
-- created_by, actor_id and modified_by reference the installation-level app_user without a foreign key (ADR-005 P3).

CREATE TABLE opportunity.redaction_set (
    workspace_id        uuid        NOT NULL,
    redaction_set_id    uuid        NOT NULL,
    name                text        NOT NULL,
    description         text        NULL,
    is_retired          boolean     NOT NULL DEFAULT false,
    created_by          uuid        NULL,
    modified_by         uuid        NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    modified_at         timestamptz NOT NULL DEFAULT now(),
    version             bigint      NOT NULL DEFAULT 1,
    CONSTRAINT redaction_set_pk PRIMARY KEY (workspace_id, redaction_set_id),
    CONSTRAINT redaction_set_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT redaction_set_name_ck CHECK (length(name) BETWEEN 1 AND 200 AND name = btrim(name)),
    CONSTRAINT redaction_set_description_ck CHECK (description IS NULL OR length(description) <= 2000),
    CONSTRAINT redaction_set_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.redaction_set');

CREATE UNIQUE INDEX redaction_set_name_uq ON opportunity.redaction_set (workspace_id, lower(name));

CREATE TABLE opportunity.redaction_reason (
    workspace_id        uuid        NOT NULL,
    code                text        NOT NULL,
    name                text        NOT NULL,
    category            smallint    NOT NULL,
    box_label           text        NOT NULL,
    is_active           boolean     NOT NULL DEFAULT true,
    sort_order          integer     NOT NULL DEFAULT 0,
    modified_by         uuid        NULL,
    modified_at         timestamptz NOT NULL DEFAULT now(),
    version             bigint      NOT NULL DEFAULT 1,
    CONSTRAINT redaction_reason_pk PRIMARY KEY (workspace_id, code),
    CONSTRAINT redaction_reason_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT redaction_reason_code_ck CHECK (code ~ '^[A-Za-z][A-Za-z0-9]{0,39}$'),
    CONSTRAINT redaction_reason_name_ck CHECK (length(name) BETWEEN 1 AND 100 AND name = btrim(name)),
    CONSTRAINT redaction_reason_category_ck CHECK (category BETWEEN 1 AND 3),
    CONSTRAINT redaction_reason_label_ck CHECK (length(box_label) BETWEEN 1 AND 60 AND box_label = btrim(box_label)),
    CONSTRAINT redaction_reason_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.redaction_reason');

CREATE UNIQUE INDEX redaction_reason_name_uq ON opportunity.redaction_reason (workspace_id, lower(name));

CREATE TABLE opportunity.redaction_revision (
    workspace_id        uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    redaction_set_id    uuid        NOT NULL,
    redaction_version   bigint      NOT NULL,
    redaction_id        uuid        NOT NULL,
    operation           smallint    NOT NULL,
    page_set_id         uuid        NOT NULL,
    ordinal             integer     NOT NULL,
    x                   integer     NOT NULL,
    y                   integer     NOT NULL,
    w                   integer     NOT NULL,
    h                   integer     NOT NULL,
    redaction_type      smallint    NOT NULL,
    reason_code         text        NOT NULL,
    note                text        NULL,
    actor_id            uuid        NOT NULL,
    actor_type          smallint    NOT NULL DEFAULT 1,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT redaction_revision_pk PRIMARY KEY (workspace_id, document_id, redaction_set_id, redaction_version, redaction_id),
    CONSTRAINT redaction_revision_set_fk FOREIGN KEY (workspace_id, redaction_set_id)
        REFERENCES opportunity.redaction_set (workspace_id, redaction_set_id),
    CONSTRAINT redaction_revision_page_set_fk FOREIGN KEY (workspace_id, document_id, page_set_id)
        REFERENCES opportunity.page_set (workspace_id, document_id, page_set_id),
    CONSTRAINT redaction_revision_page_fk FOREIGN KEY (workspace_id, page_set_id, ordinal)
        REFERENCES opportunity.page (workspace_id, page_set_id, ordinal),
    CONSTRAINT redaction_revision_reason_fk FOREIGN KEY (workspace_id, reason_code)
        REFERENCES opportunity.redaction_reason (workspace_id, code),
    CONSTRAINT redaction_revision_version_ck CHECK (redaction_version >= 1),
    CONSTRAINT redaction_revision_operation_ck CHECK (operation BETWEEN 1 AND 3),
    CONSTRAINT redaction_revision_geometry_ck CHECK (
        x >= 0 AND y >= 0 AND w >= 1 AND h >= 1 AND x + w <= 1000000 AND y + h <= 1000000),
    CONSTRAINT redaction_revision_type_ck CHECK (redaction_type BETWEEN 1 AND 2),
    CONSTRAINT redaction_revision_note_ck CHECK (note IS NULL OR length(note) <= 1000),
    CONSTRAINT redaction_revision_actor_type_ck CHECK (actor_type BETWEEN 1 AND 3)
);

SELECT opportunity.enable_workspace_rls('opportunity.redaction_revision');

CREATE INDEX redaction_revision_redaction_ix
    ON opportunity.redaction_revision (workspace_id, document_id, redaction_set_id, redaction_id, redaction_version DESC);

REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.redaction_revision FROM opportunity_app;

CREATE TABLE opportunity.document_redaction_state (
    workspace_id        uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    redaction_set_id    uuid        NOT NULL,
    current_version     bigint      NOT NULL DEFAULT 0,
    active_count        integer     NOT NULL DEFAULT 0,
    page_set_id         uuid        NULL,
    modified_by         uuid        NULL,
    modified_at         timestamptz NULL,
    CONSTRAINT document_redaction_state_pk PRIMARY KEY (workspace_id, document_id, redaction_set_id),
    CONSTRAINT document_redaction_state_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT document_redaction_state_set_fk FOREIGN KEY (workspace_id, redaction_set_id)
        REFERENCES opportunity.redaction_set (workspace_id, redaction_set_id),
    CONSTRAINT document_redaction_state_version_ck CHECK (current_version >= 0),
    CONSTRAINT document_redaction_state_count_ck CHECK (active_count >= 0)
);

SELECT opportunity.enable_workspace_rls('opportunity.document_redaction_state');

CREATE INDEX document_redaction_state_set_ix
    ON opportunity.document_redaction_state (workspace_id, redaction_set_id) WHERE active_count > 0;

INSERT INTO audit.audit_action (category, action) VALUES
    ('Redaction', 'RedactionSet.Created'),
    ('Redaction', 'RedactionSet.Modified'),
    ('Redaction', 'Reason.Created'),
    ('Redaction', 'Reason.Modified');
