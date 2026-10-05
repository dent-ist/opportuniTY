-- V0034: Highlight Sets (E16-T12, #138): workspace term lists that reviewers toggle in the viewer to highlight the
-- extracted text (persistent highlighting). terms is a JSON array of {termId, expression, color|null}: each expression
-- is a word, "phrase", wildcard, W/n proximity or an OR of these (ADR-008 syntax), validated by the API. color is a
-- palette name of the web design tokens (never a raw colour value). Highlight terms are search text (Q-16):
-- opportunity_readonly gets no access, like saved_search.
-- highlight_set_selection holds each reviewer's toggles per workspace (sets switched off, and whether the hits of the
-- current search are shown); no row means everything is on. created_by, modified_by and user_id reference the
-- installation-level app_user without a foreign key (ADR-005 P3).

CREATE TABLE opportunity.highlight_set (
    workspace_id        uuid        NOT NULL,
    highlight_set_id    uuid        NOT NULL,
    name                text        NOT NULL,
    description         text        NULL,
    color               text        NOT NULL,
    terms               jsonb       NOT NULL,
    created_by          uuid        NOT NULL,
    modified_by         uuid        NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    modified_at         timestamptz NOT NULL DEFAULT now(),
    version             bigint      NOT NULL DEFAULT 1,
    CONSTRAINT highlight_set_pk PRIMARY KEY (workspace_id, highlight_set_id),
    CONSTRAINT highlight_set_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT highlight_set_name_ck CHECK (length(name) BETWEEN 1 AND 200 AND name = btrim(name)),
    CONSTRAINT highlight_set_description_ck CHECK (description IS NULL OR length(description) <= 2000),
    CONSTRAINT highlight_set_color_ck CHECK (color ~ '^[a-z][a-z0-9-]{0,31}$'),
    CONSTRAINT highlight_set_terms_ck CHECK (jsonb_typeof(terms) = 'array' AND octet_length(terms::text) <= 262144),
    CONSTRAINT highlight_set_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.highlight_set');

CREATE UNIQUE INDEX highlight_set_name_uq ON opportunity.highlight_set (workspace_id, lower(name));

REVOKE ALL ON opportunity.highlight_set FROM opportunity_readonly;

CREATE TABLE opportunity.highlight_set_selection (
    workspace_id        uuid        NOT NULL,
    user_id             uuid        NOT NULL,
    disabled_set_ids    uuid[]      NOT NULL DEFAULT '{}',
    search_hits         boolean     NOT NULL DEFAULT true,
    modified_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT highlight_set_selection_pk PRIMARY KEY (workspace_id, user_id),
    CONSTRAINT highlight_set_selection_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT highlight_set_selection_size_ck CHECK (cardinality(disabled_set_ids) <= 1000)
);

SELECT opportunity.enable_workspace_rls('opportunity.highlight_set_selection');

INSERT INTO audit.audit_action (category, action) VALUES
    ('Search', 'HighlightSet.Created'),
    ('Search', 'HighlightSet.Modified'),
    ('Search', 'HighlightSet.Deleted');
