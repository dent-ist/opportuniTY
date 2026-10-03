-- V0003: field catalogue, choices and coding layouts (E04-T03).
-- Binding: ADR-003 (one FieldDefinition per field; storage Column / Metadata / Coding; int FieldId keys "f<id>";
-- system ids 1-999, custom ids from 1000, never reused; renames never touch documents; R7 no retype with values;
-- R8 used choices only deactivate), ADR-007 (search slots, capabilities), ADR-005 P1-P10, Q-11 (security classes).
-- Enumerations are smallint; code values live in Opportunity.Core.Fields. RLS policies follow in E05-T03.

-- ---------------------------------------------------------------------------------------------------------------
-- Per-workspace id counters (ADR-005 P5): no sequences on tenant tables; ids are never reused (ADR-003 R4).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.field_catalog_counter (
    workspace_id        uuid    NOT NULL,
    next_field_id       integer NOT NULL DEFAULT 1000,
    next_choice_id      integer NOT NULL DEFAULT 1,
    CONSTRAINT field_catalog_counter_pk PRIMARY KEY (workspace_id),
    CONSTRAINT field_catalog_counter_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT field_catalog_counter_field_ck CHECK (next_field_id >= 1000),
    CONSTRAINT field_catalog_counter_choice_ck CHECK (next_choice_id >= 1)
);

-- ---------------------------------------------------------------------------------------------------------------
-- FieldDefinition (ADR-003 §1-§3, ADR-007 §2/§4)
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.field_definition (
    workspace_id            uuid        NOT NULL,
    field_id                integer     NOT NULL,
    name                    text        NOT NULL,
    -- Case-insensitive uniqueness of display names among live fields.
    name_norm               text        NOT NULL GENERATED ALWAYS AS (lower(btrim(name))) STORED,
    description             text        NULL,
    field_type              smallint    NOT NULL,
    storage                 smallint    NOT NULL,
    is_system               boolean     NOT NULL DEFAULT false,
    is_multi_value          boolean     NOT NULL DEFAULT false,
    date_precision          smallint    NULL,
    decimal_precision       smallint    NULL,
    decimal_scale           smallint    NULL,
    text_analysis           smallint    NULL,
    is_security_affecting   boolean     NOT NULL DEFAULT false,
    security_class          smallint    NULL,
    is_searchable           boolean     NOT NULL DEFAULT true,
    search_slot             text        NULL,
    capabilities            integer     NOT NULL DEFAULT 0,
    column_name             text        NULL,
    is_hidden               boolean     NOT NULL DEFAULT false,
    is_deleted              boolean     NOT NULL DEFAULT false,
    deleted_at              timestamptz NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT field_definition_pk PRIMARY KEY (workspace_id, field_id),
    -- FK target that lets value tables require a particular storage (coding rows may only reference Coding fields).
    CONSTRAINT field_definition_storage_uq UNIQUE (workspace_id, field_id, storage),
    CONSTRAINT field_definition_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT field_definition_id_ck CHECK (field_id >= 1 AND (field_id < 1000) = is_system),
    CONSTRAINT field_definition_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT field_definition_type_ck CHECK (field_type BETWEEN 1 AND 9),
    CONSTRAINT field_definition_storage_ck CHECK (storage BETWEEN 1 AND 3 AND (storage <> 1 OR is_system)),
    CONSTRAINT field_definition_column_ck CHECK ((storage = 1) = (column_name IS NOT NULL)),
    -- Multi-value: Text/Keyword optional, MultiChoice always, others never.
    CONSTRAINT field_definition_multi_value_ck CHECK (
        CASE field_type WHEN 8 THEN is_multi_value WHEN 1 THEN true WHEN 2 THEN true ELSE NOT is_multi_value END),
    CONSTRAINT field_definition_date_precision_ck CHECK (
        (field_type = 5) = (date_precision IS NOT NULL) AND (date_precision IS NULL OR date_precision BETWEEN 1 AND 2)),
    CONSTRAINT field_definition_decimal_ck CHECK (
        (field_type = 4) = (decimal_precision IS NOT NULL AND decimal_scale IS NOT NULL)
        AND (field_type = 4 OR (decimal_precision IS NULL AND decimal_scale IS NULL))
        AND (decimal_precision IS NULL OR decimal_precision BETWEEN 1 AND 18)
        AND (decimal_scale IS NULL OR decimal_scale BETWEEN 0 AND LEAST(6, decimal_precision))),
    CONSTRAINT field_definition_text_analysis_ck CHECK (
        text_analysis IS NULL OR (field_type = 1 AND text_analysis BETWEEN 1 AND 2)),
    -- Q-11: security-affecting fields carry a class and are Coding or Metadata fields.
    CONSTRAINT field_definition_security_ck CHECK (
        is_security_affecting = (security_class IS NOT NULL)
        AND (security_class IS NULL OR (security_class BETWEEN 1 AND 3 AND storage IN (2, 3)))),
    CONSTRAINT field_definition_search_slot_ck CHECK (
        (search_slot IS NULL OR is_searchable)
        AND (search_slot IS NULL OR search_slot = 'overflow' OR search_slot ~ '^(txt|idt|kw|int|dec|dt|bool|ch|usr)\.s[0-9]{3}$')),
    CONSTRAINT field_definition_deleted_ck CHECK (is_deleted = (deleted_at IS NOT NULL) AND NOT (is_deleted AND is_system))
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

CREATE UNIQUE INDEX field_definition_name_uq
    ON opportunity.field_definition (workspace_id, name_norm) WHERE NOT is_deleted;
-- A slot is held by one field per storage namespace (metadata.* vs coding.*) until its definition is purged (ADR-007 R4).
CREATE UNIQUE INDEX field_definition_search_slot_uq
    ON opportunity.field_definition (workspace_id, storage, search_slot) WHERE search_slot IS NOT NULL AND search_slot <> 'overflow';

-- Whether any document holds a value for the field. Metadata values only for now; V0004 adds coding values.
-- No GIN index exists (ADR-003 R12): this scans the workspace's documents and is only used on rare admin changes.
CREATE FUNCTION opportunity.field_has_values(p_workspace_id uuid, p_field_id integer, p_storage smallint)
    RETURNS boolean
    LANGUAGE sql
    STABLE
RETURN CASE p_storage
    WHEN 2 THEN EXISTS (SELECT FROM opportunity.document d
                        WHERE d.workspace_id = p_workspace_id AND d.metadata ? ('f' || p_field_id::text))
    ELSE false
END;

-- ADR-003 R3/R7 backstop for writers that bypass the repository: system fields are never retyped, and no field is
-- retyped once it holds values. Renames, descriptions and visibility are always allowed and touch no document.
CREATE FUNCTION opportunity.field_definition_retype_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.is_system THEN
        RAISE EXCEPTION 'System field % cannot be retyped', OLD.field_id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_definition_retype';
    END IF;
    IF opportunity.field_has_values(OLD.workspace_id, OLD.field_id, OLD.storage) THEN
        RAISE EXCEPTION 'Field % holds values and cannot be retyped', OLD.field_id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_definition_retype',
                  HINT = 'ADR-003 R7: create a new field and copy values instead.';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER field_definition_retype_guard
    BEFORE UPDATE OF field_type, storage, is_multi_value, date_precision, decimal_precision, decimal_scale
    ON opportunity.field_definition
    FOR EACH ROW
    WHEN (OLD.field_type IS DISTINCT FROM NEW.field_type
          OR OLD.storage IS DISTINCT FROM NEW.storage
          OR OLD.is_multi_value IS DISTINCT FROM NEW.is_multi_value
          OR OLD.date_precision IS DISTINCT FROM NEW.date_precision
          OR OLD.decimal_precision IS DISTINCT FROM NEW.decimal_precision
          OR OLD.decimal_scale IS DISTINCT FROM NEW.decimal_scale)
    EXECUTE FUNCTION opportunity.field_definition_retype_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- Choice (ADR-003 R8). Values store choice_id only; names, order and activity live here.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.choice (
    workspace_id    uuid        NOT NULL,
    field_id        integer     NOT NULL,
    choice_id       integer     NOT NULL,
    name            text        NOT NULL,
    name_norm       text        NOT NULL GENERATED ALWAYS AS (lower(btrim(name))) STORED,
    sort_order      integer     NOT NULL,
    is_active       boolean     NOT NULL DEFAULT true,
    -- Set once by the first write that assigns the choice; from then on it can only be deactivated.
    first_used_at   timestamptz NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT choice_pk PRIMARY KEY (workspace_id, field_id, choice_id),
    -- Choice ids come from the workspace counter, so they are unique per workspace as well.
    CONSTRAINT choice_id_uq UNIQUE (workspace_id, choice_id),
    CONSTRAINT choice_field_fk FOREIGN KEY (workspace_id, field_id)
        REFERENCES opportunity.field_definition (workspace_id, field_id),
    CONSTRAINT choice_name_uq UNIQUE (workspace_id, field_id, name_norm),
    -- Deferred so a reorder can permute positions inside one transaction.
    CONSTRAINT choice_sort_order_uq UNIQUE (workspace_id, field_id, sort_order) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT choice_id_ck CHECK (choice_id >= 1),
    CONSTRAINT choice_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT choice_sort_order_ck CHECK (sort_order >= 0)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- Choices only exist on SingleChoice/MultiChoice fields.
CREATE FUNCTION opportunity.choice_field_is_choice_type()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS (SELECT FROM opportunity.field_definition f
                   WHERE f.workspace_id = NEW.workspace_id AND f.field_id = NEW.field_id AND f.field_type IN (7, 8)) THEN
        RAISE EXCEPTION 'Field % is not a choice field', NEW.field_id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'choice_field_type';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER choice_field_is_choice_type
    BEFORE INSERT ON opportunity.choice
    FOR EACH ROW EXECUTE FUNCTION opportunity.choice_field_is_choice_type();

-- R8: a used choice can never be deleted, only deactivated. "Used" is sticky (first_used_at) and, as a backstop for
-- imported metadata, any document still holding it. Coding values reference choices by FK (V0004).
CREATE FUNCTION opportunity.choice_delete_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.first_used_at IS NOT NULL
       OR EXISTS (SELECT FROM opportunity.document d
                  WHERE d.workspace_id = OLD.workspace_id
                    AND d.metadata -> ('f' || OLD.field_id::text) @> to_jsonb(OLD.choice_id)) THEN
        RAISE EXCEPTION 'Choice % has been used and can only be deactivated', OLD.choice_id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'choice_in_use';
    END IF;
    RETURN OLD;
END
$$;

CREATE TRIGGER choice_delete_guard
    BEFORE DELETE ON opportunity.choice
    FOR EACH ROW EXECUTE FUNCTION opportunity.choice_delete_guard();

-- A field with choices cannot become a non-choice field.
CREATE FUNCTION opportunity.field_definition_choice_type_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.field_type NOT IN (7, 8)
       AND EXISTS (SELECT FROM opportunity.choice c WHERE c.workspace_id = OLD.workspace_id AND c.field_id = OLD.field_id) THEN
        RAISE EXCEPTION 'Field % has choices and cannot become a non-choice field', OLD.field_id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_definition_retype';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER field_definition_choice_type_guard
    BEFORE UPDATE OF field_type ON opportunity.field_definition
    FOR EACH ROW
    WHEN (OLD.field_type IS DISTINCT FROM NEW.field_type)
    EXECUTE FUNCTION opportunity.field_definition_choice_type_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- Coding layouts: sections, ordered fields, required flags, conditional visibility, role assignment.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.coding_layout (
    workspace_id    uuid        NOT NULL,
    layout_id       uuid        NOT NULL,
    name            text        NOT NULL,
    name_norm       text        NOT NULL GENERATED ALWAYS AS (lower(btrim(name))) STORED,
    is_default      boolean     NOT NULL DEFAULT false,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT coding_layout_pk PRIMARY KEY (workspace_id, layout_id),
    CONSTRAINT coding_layout_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT coding_layout_name_uq UNIQUE (workspace_id, name_norm),
    CONSTRAINT coding_layout_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200)
);

-- Exactly one default layout per workspace (at most one here; the repository creates it with the workspace).
CREATE UNIQUE INDEX coding_layout_default_uq ON opportunity.coding_layout (workspace_id) WHERE is_default;

CREATE TABLE opportunity.coding_layout_section (
    workspace_id    uuid    NOT NULL,
    layout_id       uuid    NOT NULL,
    section_id      uuid    NOT NULL,
    title           text    NOT NULL,
    sort_order      integer NOT NULL,
    CONSTRAINT coding_layout_section_pk PRIMARY KEY (workspace_id, layout_id, section_id),
    CONSTRAINT coding_layout_section_layout_fk FOREIGN KEY (workspace_id, layout_id)
        REFERENCES opportunity.coding_layout (workspace_id, layout_id) ON DELETE CASCADE,
    CONSTRAINT coding_layout_section_order_uq UNIQUE (workspace_id, layout_id, sort_order),
    CONSTRAINT coding_layout_section_title_ck CHECK (length(btrim(title)) BETWEEN 1 AND 200)
);

CREATE TABLE opportunity.coding_layout_field (
    workspace_id            uuid     NOT NULL,
    layout_id               uuid     NOT NULL,
    field_id                integer  NOT NULL,
    section_id              uuid     NOT NULL,
    sort_order              integer  NOT NULL,
    is_required             boolean  NOT NULL DEFAULT false,
    is_read_only            boolean  NOT NULL DEFAULT false,
    -- Visible only while condition_field_id holds any of condition_choice_ids / equals condition_boolean.
    condition_field_id      integer  NULL,
    condition_choice_ids    integer[] NULL,
    condition_boolean       boolean  NULL,
    CONSTRAINT coding_layout_field_pk PRIMARY KEY (workspace_id, layout_id, field_id),
    CONSTRAINT coding_layout_field_section_fk FOREIGN KEY (workspace_id, layout_id, section_id)
        REFERENCES opportunity.coding_layout_section (workspace_id, layout_id, section_id) ON DELETE CASCADE,
    CONSTRAINT coding_layout_field_field_fk FOREIGN KEY (workspace_id, field_id)
        REFERENCES opportunity.field_definition (workspace_id, field_id),
    -- The controlling field must be placed in the same layout.
    CONSTRAINT coding_layout_field_condition_fk FOREIGN KEY (workspace_id, layout_id, condition_field_id)
        REFERENCES opportunity.coding_layout_field (workspace_id, layout_id, field_id) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT coding_layout_field_order_uq UNIQUE (workspace_id, layout_id, section_id, sort_order),
    CONSTRAINT coding_layout_field_required_ck CHECK (NOT (is_required AND is_read_only)),
    CONSTRAINT coding_layout_field_condition_ck CHECK (
        condition_field_id IS DISTINCT FROM field_id
        AND (condition_field_id IS NULL) = (condition_choice_ids IS NULL AND condition_boolean IS NULL)
        AND (condition_choice_ids IS NULL OR condition_boolean IS NULL)
        AND (condition_choice_ids IS NULL OR cardinality(condition_choice_ids) >= 1))
);

CREATE INDEX coding_layout_field_field_ix ON opportunity.coding_layout_field (workspace_id, field_id);

CREATE TABLE opportunity.coding_layout_role (
    workspace_id    uuid NOT NULL,
    layout_id       uuid NOT NULL,
    role            text NOT NULL,
    CONSTRAINT coding_layout_role_pk PRIMARY KEY (workspace_id, layout_id, role),
    CONSTRAINT coding_layout_role_layout_fk FOREIGN KEY (workspace_id, layout_id)
        REFERENCES opportunity.coding_layout (workspace_id, layout_id) ON DELETE CASCADE,
    CONSTRAINT coding_layout_role_ck CHECK (length(btrim(role)) BETWEEN 1 AND 100)
);

CREATE INDEX coding_layout_role_role_ix ON opportunity.coding_layout_role (workspace_id, role);
