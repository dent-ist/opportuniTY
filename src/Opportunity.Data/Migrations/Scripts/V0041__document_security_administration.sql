-- V0041: document- and field-level security administration (E05-T06, #51; ADR-015 D6, Q-11, Q-13, Q-45).
-- * restriction_class_rule binds a restriction class to choices of security-affecting coding fields: a document carries
--   the class while it is coded with any of the class's choices. Coding, overlays and rule changes keep
--   document_restriction (V0012) in line in their own transaction (ADR-015 D6.1, §24 rule 1).
-- * ethical_wall gains a description and a version (If-Match); ethical_wall_scope holds a wall's scope: explicit
--   documents (kind 1), custodian values (kind 2, matched case-insensitively against the custodian fields named by the
--   caller) and choices of security-affecting wall fields (kind 3). Coverage stays materialized in document_wall and is
--   maintained by opportunity.sync_document_walls in the transaction that changes the scope, the coding or the imported
--   documents (ADR-015 D6.2).
-- * field_security restricts a custom field to roles: hidden from principals holding none of visible_roles, read-only
--   for those holding none of editable_roles (editable_roles is a subset of visible_roles). No row: unrestricted.
-- user ids reference the installation-level app_user without a foreign key (ADR-005 P3).

CREATE TABLE opportunity.restriction_class_rule (
    workspace_id        uuid        NOT NULL,
    class_key           text        NOT NULL,
    field_id            integer     NOT NULL,
    choice_id           integer     NOT NULL,
    CONSTRAINT restriction_class_rule_pk PRIMARY KEY (workspace_id, class_key, field_id, choice_id),
    CONSTRAINT restriction_class_rule_class_fk FOREIGN KEY (workspace_id, class_key)
        REFERENCES opportunity.restriction_class (workspace_id, class_key) ON DELETE CASCADE,
    CONSTRAINT restriction_class_rule_choice_fk FOREIGN KEY (workspace_id, field_id, choice_id)
        REFERENCES opportunity.choice (workspace_id, field_id, choice_id)
);

SELECT opportunity.enable_workspace_rls('opportunity.restriction_class_rule');

CREATE INDEX restriction_class_rule_choice_ix ON opportunity.restriction_class_rule (workspace_id, field_id, choice_id);

ALTER TABLE opportunity.restriction_class
    ADD COLUMN version     bigint      NOT NULL DEFAULT 1,
    ADD COLUMN updated_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN updated_by  uuid        NULL;

ALTER TABLE opportunity.ethical_wall
    ADD COLUMN description text        NULL,
    ADD COLUMN version     bigint      NOT NULL DEFAULT 1,
    ADD COLUMN updated_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN updated_by  uuid        NULL,
    ADD CONSTRAINT ethical_wall_description_ck CHECK (description IS NULL OR length(description) <= 2000),
    ADD CONSTRAINT ethical_wall_version_ck CHECK (version >= 1);

CREATE TABLE opportunity.ethical_wall_scope (
    workspace_id        uuid        NOT NULL,
    wall_id             uuid        NOT NULL,
    scope_id            uuid        NOT NULL,
    -- 1 Document, 2 Custodian, 3 Choice.
    kind                smallint    NOT NULL,
    document_id         uuid        NULL,
    custodian           text        NULL,
    field_id            integer     NULL,
    choice_id           integer     NULL,
    CONSTRAINT ethical_wall_scope_pk PRIMARY KEY (workspace_id, wall_id, scope_id),
    CONSTRAINT ethical_wall_scope_wall_fk FOREIGN KEY (workspace_id, wall_id)
        REFERENCES opportunity.ethical_wall (workspace_id, wall_id) ON DELETE CASCADE,
    CONSTRAINT ethical_wall_scope_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT ethical_wall_scope_choice_fk FOREIGN KEY (workspace_id, field_id, choice_id)
        REFERENCES opportunity.choice (workspace_id, field_id, choice_id),
    CONSTRAINT ethical_wall_scope_kind_ck CHECK (
        (kind = 1 AND document_id IS NOT NULL AND custodian IS NULL AND field_id IS NULL AND choice_id IS NULL)
        OR (kind = 2 AND document_id IS NULL AND custodian IS NOT NULL AND field_id IS NULL AND choice_id IS NULL)
        OR (kind = 3 AND document_id IS NULL AND custodian IS NULL AND field_id IS NOT NULL AND choice_id IS NOT NULL)),
    CONSTRAINT ethical_wall_scope_custodian_ck CHECK (
        custodian IS NULL OR (length(custodian) BETWEEN 1 AND 255 AND custodian = lower(btrim(custodian))))
);

SELECT opportunity.enable_workspace_rls('opportunity.ethical_wall_scope');

CREATE UNIQUE INDEX ethical_wall_scope_document_uq
    ON opportunity.ethical_wall_scope (workspace_id, wall_id, document_id) WHERE kind = 1;
CREATE UNIQUE INDEX ethical_wall_scope_custodian_uq
    ON opportunity.ethical_wall_scope (workspace_id, wall_id, custodian) WHERE kind = 2;
CREATE UNIQUE INDEX ethical_wall_scope_choice_uq
    ON opportunity.ethical_wall_scope (workspace_id, wall_id, field_id, choice_id) WHERE kind = 3;
CREATE INDEX ethical_wall_scope_kind_ix ON opportunity.ethical_wall_scope (workspace_id, kind);

CREATE INDEX document_wall_document_ix ON opportunity.document_wall (workspace_id, document_id);

CREATE TABLE opportunity.field_security (
    workspace_id        uuid        NOT NULL,
    field_id            integer     NOT NULL,
    visible_roles       text[]      NOT NULL,
    editable_roles      text[]      NOT NULL,
    version             bigint      NOT NULL DEFAULT 1,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    updated_by          uuid        NULL,
    CONSTRAINT field_security_pk PRIMARY KEY (workspace_id, field_id),
    CONSTRAINT field_security_field_fk FOREIGN KEY (workspace_id, field_id)
        REFERENCES opportunity.field_definition (workspace_id, field_id),
    CONSTRAINT field_security_custom_ck CHECK (field_id >= 1000),
    CONSTRAINT field_security_roles_ck CHECK (
        visible_roles <@ ARRAY['WorkspaceAdmin', 'Reviewer', 'QcReviewer', 'PrivilegeReviewer', 'ProductionManager', 'Auditor']::text[]
        AND editable_roles <@ visible_roles),
    CONSTRAINT field_security_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.field_security');

-- Wall coverage (ADR-015 D6.2) for p_doc_ids (NULL: every document of the workspace), from the scopes of every wall:
-- inserts missing and deletes stale document_wall rows and returns the documents whose coverage changed. p_custodian_fields
-- are the metadata fields whose values are custodians (the custodian field and All Custodians); a value matches a custodian
-- scope after lower(btrim()). SECURITY INVOKER: it runs under the caller's workspace context and RLS.
CREATE FUNCTION opportunity.sync_document_walls(p_ws uuid, p_doc_ids uuid[], p_custodian_fields integer[])
    RETURNS TABLE (changed_document_id uuid)
    LANGUAGE sql
AS $$
    WITH docs AS (
        SELECT d.document_id, d.metadata
        FROM opportunity.document d
        WHERE d.workspace_id = p_ws AND (p_doc_ids IS NULL OR d.document_id = ANY (p_doc_ids))
    ),
    custodians AS (
        SELECT DISTINCT docs.document_id, lower(btrim(v.value)) AS custodian
        FROM docs
        CROSS JOIN unnest(coalesce(p_custodian_fields, '{}'::integer[])) AS f(field_id)
        CROSS JOIN LATERAL (SELECT docs.metadata -> ('f' || f.field_id::text) AS node) n
        CROSS JOIN LATERAL jsonb_array_elements_text(
            CASE jsonb_typeof(n.node) WHEN 'array' THEN n.node WHEN 'string' THEN jsonb_build_array(n.node) ELSE '[]'::jsonb END) AS v(value)
        WHERE EXISTS (SELECT FROM opportunity.ethical_wall_scope s WHERE s.workspace_id = p_ws AND s.kind = 2)
    ),
    desired AS (
        SELECT s.wall_id, s.document_id
        FROM opportunity.ethical_wall_scope s
        JOIN docs ON docs.document_id = s.document_id
        WHERE s.workspace_id = p_ws AND s.kind = 1
        UNION
        SELECT s.wall_id, c.document_id
        FROM opportunity.ethical_wall_scope s
        JOIN custodians c ON c.custodian = s.custodian
        WHERE s.workspace_id = p_ws AND s.kind = 2
        UNION
        SELECT s.wall_id, cc.document_id
        FROM opportunity.ethical_wall_scope s
        JOIN opportunity.document_coding_choice cc
          ON cc.workspace_id = p_ws AND cc.field_id = s.field_id AND cc.choice_id = s.choice_id
        WHERE s.workspace_id = p_ws AND s.kind = 3 AND (p_doc_ids IS NULL OR cc.document_id = ANY (p_doc_ids))
    ),
    removed AS (
        DELETE FROM opportunity.document_wall w
        WHERE w.workspace_id = p_ws AND (p_doc_ids IS NULL OR w.document_id = ANY (p_doc_ids))
          AND NOT EXISTS (SELECT FROM desired x WHERE x.wall_id = w.wall_id AND x.document_id = w.document_id)
        RETURNING w.document_id
    ),
    added AS (
        INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id)
        SELECT p_ws, x.document_id, x.wall_id FROM desired x
        ON CONFLICT DO NOTHING
        RETURNING document_id
    )
    SELECT document_id FROM removed
    UNION
    SELECT document_id FROM added
$$;

REVOKE ALL ON FUNCTION opportunity.sync_document_walls(uuid, uuid[], integer[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION opportunity.sync_document_walls(uuid, uuid[], integer[]) TO opportunity_app;
