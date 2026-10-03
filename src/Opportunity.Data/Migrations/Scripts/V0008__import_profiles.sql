-- V0008: saved import profiles (E08-T02; ticket review: "Import profile", guide §5.1).
-- A profile is delimiters + encodings + field mapping + per-column parsing + mode + overlay and path settings, stored
-- as the JSON the API exchanges (Opportunity.Contracts.Import.ImportProfileDefinition). It references fields by id and
-- name only and never holds document content. Optimistic concurrency: version is the ETag (ADR-019 §2.7).

CREATE TABLE opportunity.import_profile (
    workspace_id    uuid        NOT NULL,
    profile_id      uuid        NOT NULL,
    name            text        NOT NULL,
    name_norm       text        NOT NULL GENERATED ALWAYS AS (lower(btrim(name))) STORED,
    description     text        NULL,
    definition      jsonb       NOT NULL,
    version         bigint      NOT NULL DEFAULT 1,
    created_by      uuid        NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid        NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT import_profile_pk PRIMARY KEY (workspace_id, profile_id),
    CONSTRAINT import_profile_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT import_profile_name_uq UNIQUE (workspace_id, name_norm),
    CONSTRAINT import_profile_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT import_profile_description_ck CHECK (description IS NULL OR length(description) <= 2000),
    CONSTRAINT import_profile_definition_ck CHECK (jsonb_typeof(definition) = 'object' AND octet_length(definition::text) <= 1048576),
    CONSTRAINT import_profile_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.import_profile');
