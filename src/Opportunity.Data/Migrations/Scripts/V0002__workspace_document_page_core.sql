-- V0002: workspace, document, object registry and page core schema (E04-T02).
-- Binding: ADR-003 (structural columns, Metadata JSONB), ADR-005 P1-P10 (partition-ready keys), ADR-009 (ControlNumber,
-- family, dedupe, thread, dates), ADR-011 (StoredObject registry; rows reference ObjectId, never a key string),
-- ADR-012 (PageSet / Page / PageImage), ADR-001 (DocumentVersion in DocumentProjectionState), ADR-014 (status).
--
-- Every tenant table: PK, UNIQUE and secondary indexes lead with workspace_id; FKs are composite (workspace_id, ...),
-- so a row can never reference another workspace's row. Ids are application-generated UUIDv7 (P5).
-- Row-level security policies are added by E05-T03 (ADR-015 D7) for every table here.
-- Enumerations on high-volume tables are smallint; the code values live in Opportunity.Core.

-- ---------------------------------------------------------------------------------------------------------------
-- Workspace (ADR-014: Active -> Closed -> Deleting -> Purged; preservation locks are separate records, E20-T01)
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.workspace (
    workspace_id                    uuid        NOT NULL,
    name                            text        NOT NULL,
    matter_number                   text        NULL,
    display_time_zone               text        NOT NULL,
    status                          text        NOT NULL DEFAULT 'Active',
    closed_at                       timestamptz NULL,
    epoch                           bigint      NOT NULL DEFAULT 1,
    control_number_case_sensitive   boolean     NOT NULL DEFAULT false,
    created_at                      timestamptz NOT NULL DEFAULT now(),
    updated_at                      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workspace_pk PRIMARY KEY (workspace_id),
    CONSTRAINT workspace_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    CONSTRAINT workspace_matter_number_ck CHECK (matter_number IS NULL OR length(matter_number) <= 100),
    CONSTRAINT workspace_display_time_zone_ck CHECK (length(display_time_zone) BETWEEN 1 AND 64),
    CONSTRAINT workspace_status_ck CHECK (status IN ('Active', 'Closed', 'Deleting', 'Purged')),
    -- ClosedAt starts the audit retention clock; reopen clears it (ADR-014 §1.2).
    CONSTRAINT workspace_closed_at_ck CHECK ((status = 'Active') = (closed_at IS NULL)),
    CONSTRAINT workspace_epoch_ck CHECK (epoch >= 1)
);

-- ---------------------------------------------------------------------------------------------------------------
-- ControlNumber natural sort key (ADR-009 R5): every maximal ASCII digit run left-padded to 20 digits (longer runs
-- kept), compared with COLLATE "C". Computed by the database so it can never drift from ControlNumberNorm;
-- Opportunity.Core.Documents.ControlNumber.SortKey is the identical C# function used for search projection.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.control_number_sort_key(norm text)
    RETURNS text
    LANGUAGE sql
    IMMUTABLE STRICT PARALLEL SAFE
RETURN (
    SELECT string_agg(
               CASE WHEN m[1] ~ '^[0-9]' AND length(m[1]) < 20 THEN lpad(m[1], 20, '0') ELSE m[1] END,
               '' ORDER BY ord)
    FROM regexp_matches(norm, '([0-9]+|[^0-9]+)', 'g') WITH ORDINALITY AS t(m, ord)
);

-- ---------------------------------------------------------------------------------------------------------------
-- Document (ADR-003 §1 structural columns)
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.document (
    workspace_id            uuid        NOT NULL,
    document_id             uuid        NOT NULL,

    -- Identity (ADR-009 §1). Normalization (NFC, trim, collapse, optional upper-case) happens in the application.
    control_number          text        NOT NULL,
    control_number_norm     text        NOT NULL,
    control_number_sort_key text        COLLATE "C" NOT NULL
        GENERATED ALWAYS AS (opportunity.control_number_sort_key(control_number_norm)) STORED,
    beg_bates               text        NULL,
    end_bates               text        NULL,
    beg_attach              text        NULL,
    end_attach              text        NULL,

    -- Relationships (ADR-009 §2-§4). A standalone document is a family of one: family_id = document_id, sequence 0.
    family_id               uuid        NOT NULL,
    parent_document_id      uuid        NULL,
    family_sequence         integer     NOT NULL DEFAULT 0,
    family_status           smallint    NOT NULL DEFAULT 0,
    duplicate_group_id      uuid        NULL,
    is_duplicate_primary    boolean     NOT NULL DEFAULT false,
    email_thread_id         uuid        NULL,
    email_thread_source     smallint    NULL,

    -- Hashes (ADR-009 R17): computed hashes as bytea, upstream hash as lower-case hex text.
    md5                     bytea       NULL,
    sha1                    bytea       NULL,
    sha256                  bytea       NULL,
    upstream_dedupe_hash    text        NULL,

    -- File
    file_name               text        NULL,
    file_extension          text        NULL,
    file_type               text        NULL,
    mime_type               text        NULL,
    file_size               bigint      NULL,
    page_count              integer     NULL,

    -- Dates (ADR-009 §5): UTC instants; raw strings, formats and zones are kept in metadata_raw.
    date_sent               timestamptz NULL,
    date_received           timestamptz NULL,
    date_created            timestamptz NULL,
    date_last_modified      timestamptz NULL,
    document_date           timestamptz NULL,
    document_date_source    smallint    NULL,
    family_date             timestamptz NULL,

    -- Artifacts (ADR-011: ObjectId references into stored_object; ADR-012: active page set)
    native_object_id        uuid        NULL,
    text_object_id          uuid        NULL,
    text_length             bigint      NULL,
    active_page_set_id      uuid        NULL,
    text_truncated          boolean     NOT NULL DEFAULT false,
    text_missing            boolean     NOT NULL DEFAULT false,
    native_missing          boolean     NOT NULL DEFAULT false,
    images_incomplete       boolean     NOT NULL DEFAULT false,
    text_encoding_warning   boolean     NOT NULL DEFAULT false,

    -- Values (ADR-003 §2-§3): keys are "f" + FieldId; canonical values only. No GIN index (ADR-003 R12).
    metadata                jsonb       NOT NULL DEFAULT '{}',
    metadata_raw            jsonb       NULL,

    -- Bookkeeping. DocumentVersion lives in document_projection_state (ADR-001 §2).
    first_import_batch_id   uuid        NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT document_pk PRIMARY KEY (workspace_id, document_id),
    CONSTRAINT document_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    -- ADR-009 R3: unique on the normalized form within a workspace.
    CONSTRAINT document_control_number_norm_uq UNIQUE (workspace_id, control_number_norm),
    -- FK target for family integrity: lets a child require that its parent / family root sits in the same family.
    CONSTRAINT document_family_member_uq UNIQUE (workspace_id, document_id, family_id),
    -- ADR-009 R8: sequence 0 is the parent, 1..n the other members. Deferrable so family resolution can re-sequence.
    CONSTRAINT document_family_sequence_uq UNIQUE (workspace_id, family_id, family_sequence)
        DEFERRABLE INITIALLY IMMEDIATE,

    CONSTRAINT document_control_number_ck CHECK (length(control_number) BETWEEN 1 AND 255),
    CONSTRAINT document_control_number_norm_ck CHECK (
        length(control_number_norm) BETWEEN 1 AND 255
        AND control_number_norm = btrim(control_number_norm)
        AND control_number_norm !~ '[[:cntrl:]]'
        AND control_number_norm !~ '\s\s'
        AND control_number_norm IS NFC NORMALIZED),
    -- ADR-009 R7/R8: the family root has sequence 0, is its own family and has no parent; every other member has a
    -- parent (the immediate one) in the same family.
    CONSTRAINT document_family_root_ck CHECK (
        (family_sequence = 0) = (family_id = document_id)
        AND (family_sequence = 0) = (parent_document_id IS NULL)),
    CONSTRAINT document_family_sequence_ck CHECK (family_sequence >= 0),
    CONSTRAINT document_parent_not_self_ck CHECK (parent_document_id <> document_id),
    CONSTRAINT document_family_status_ck CHECK (family_status BETWEEN 0 AND 5),
    CONSTRAINT document_email_thread_source_ck CHECK (
        email_thread_source BETWEEN 1 AND 2 AND (email_thread_id IS NULL) = (email_thread_source IS NULL)),
    CONSTRAINT document_date_source_ck CHECK (
        document_date_source BETWEEN 1 AND 5 AND (document_date IS NULL) = (document_date_source IS NULL)),
    CONSTRAINT document_md5_ck CHECK (octet_length(md5) = 16),
    CONSTRAINT document_sha1_ck CHECK (octet_length(sha1) = 20),
    CONSTRAINT document_sha256_ck CHECK (octet_length(sha256) = 32),
    CONSTRAINT document_upstream_dedupe_hash_ck CHECK (upstream_dedupe_hash ~ '^[0-9a-f]{1,128}$'),
    CONSTRAINT document_file_size_ck CHECK (file_size >= 0),
    CONSTRAINT document_page_count_ck CHECK (page_count >= 0),
    CONSTRAINT document_text_length_ck CHECK (text_length >= 0),
    CONSTRAINT document_metadata_ck CHECK (jsonb_typeof(metadata) = 'object'),
    CONSTRAINT document_metadata_raw_ck CHECK (jsonb_typeof(metadata_raw) = 'object')
) WITH (fillfactor = 90, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- Family integrity (ADR-009 R7/R8). The family root must be a root (its own family_id equals itself), and the
-- immediate parent must belong to the same family. Deferrable: family resolution (E09-T01) re-links whole families
-- in one transaction under SET CONSTRAINTS ... DEFERRED.
ALTER TABLE opportunity.document
    ADD CONSTRAINT document_family_root_fk
        FOREIGN KEY (workspace_id, family_id, family_id)
        REFERENCES opportunity.document (workspace_id, document_id, family_id)
        DEFERRABLE INITIALLY IMMEDIATE,
    ADD CONSTRAINT document_parent_fk
        FOREIGN KEY (workspace_id, parent_document_id, family_id)
        REFERENCES opportunity.document (workspace_id, document_id, family_id)
        DEFERRABLE INITIALLY IMMEDIATE;

-- Natural ControlNumber order (ADR-009 R5), with DocumentId as the final tie-breaker; also serves family Mode A ranges.
CREATE INDEX document_control_number_sort_ix
    ON opportunity.document (workspace_id, control_number_sort_key, document_id);
CREATE INDEX document_parent_ix
    ON opportunity.document (workspace_id, parent_document_id) WHERE parent_document_id IS NOT NULL;
CREATE INDEX document_duplicate_group_ix
    ON opportunity.document (workspace_id, duplicate_group_id) WHERE duplicate_group_id IS NOT NULL;
CREATE INDEX document_email_thread_ix
    ON opportunity.document (workspace_id, email_thread_id) WHERE email_thread_id IS NOT NULL;

-- ADR-009 R4: ControlNumber is immutable. Identity columns are fixed with it.
CREATE FUNCTION opportunity.document_identity_immutable()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'ControlNumber and document identity are immutable (document %)', OLD.document_id
        USING ERRCODE = 'integrity_constraint_violation',
              HINT = 'ADR-009 R4: control numbers are never renamed; retire the document instead.';
END
$$;

CREATE TRIGGER document_identity_immutable
    BEFORE UPDATE OF workspace_id, document_id, control_number, control_number_norm ON opportunity.document
    FOR EACH ROW
    WHEN (OLD.workspace_id IS DISTINCT FROM NEW.workspace_id
          OR OLD.document_id IS DISTINCT FROM NEW.document_id
          OR OLD.control_number IS DISTINCT FROM NEW.control_number
          OR OLD.control_number_norm IS DISTINCT FROM NEW.control_number_norm)
    EXECUTE FUNCTION opportunity.document_identity_immutable();

-- ---------------------------------------------------------------------------------------------------------------
-- Retired control numbers (ADR-009 R4): a removed document's number cannot be reused. The application inserts a row
-- when it removes a document, except for an import batch rolled back before anything touched it.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.retired_control_number (
    workspace_id        uuid        NOT NULL,
    control_number_norm text        NOT NULL,
    control_number      text        NOT NULL,
    document_id         uuid        NOT NULL,
    retired_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT retired_control_number_pk PRIMARY KEY (workspace_id, control_number_norm),
    CONSTRAINT retired_control_number_workspace_fk FOREIGN KEY (workspace_id)
        REFERENCES opportunity.workspace (workspace_id)
);

CREATE FUNCTION opportunity.document_reject_retired_control_number()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (SELECT FROM opportunity.retired_control_number r
               WHERE r.workspace_id = NEW.workspace_id AND r.control_number_norm = NEW.control_number_norm) THEN
        RAISE EXCEPTION 'Control number % is retired in workspace % and cannot be reused',
            NEW.control_number, NEW.workspace_id
            USING ERRCODE = 'unique_violation', CONSTRAINT = 'retired_control_number_pk';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER document_reject_retired_control_number
    BEFORE INSERT ON opportunity.document
    FOR EACH ROW EXECUTE FUNCTION opportunity.document_reject_retired_control_number();

-- ADR-009 R2: ControlNumberCaseSensitive is fixed once the workspace holds a document.
CREATE FUNCTION opportunity.workspace_case_sensitivity_fixed()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (SELECT FROM opportunity.document d WHERE d.workspace_id = OLD.workspace_id) THEN
        RAISE EXCEPTION 'ControlNumberCaseSensitive cannot change once workspace % holds documents', OLD.workspace_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER workspace_case_sensitivity_fixed
    BEFORE UPDATE OF control_number_case_sensitive ON opportunity.workspace
    FOR EACH ROW
    WHEN (OLD.control_number_case_sensitive IS DISTINCT FROM NEW.control_number_case_sensitive)
    EXECUTE FUNCTION opportunity.workspace_case_sensitivity_fixed();

-- ---------------------------------------------------------------------------------------------------------------
-- DocumentProjectionState (ADR-001 §2): the authoritative, strictly increasing DocumentVersion, kept narrow so that
-- version bumps by coding writes never rewrite the wide document tuple. Final placement is ADR-004a's call.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.document_projection_state (
    workspace_id        uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    document_version    bigint      NOT NULL DEFAULT 1,
    is_deleted          boolean     NOT NULL DEFAULT false,
    deleted_at          timestamptz NULL,
    CONSTRAINT document_projection_state_pk PRIMARY KEY (workspace_id, document_id),
    CONSTRAINT document_projection_state_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT document_projection_state_version_ck CHECK (document_version >= 1),
    CONSTRAINT document_projection_state_deleted_ck CHECK (is_deleted = (deleted_at IS NOT NULL))
) WITH (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- ---------------------------------------------------------------------------------------------------------------
-- StoredObject registry (ADR-011 §2.3). PostgreSQL is the authoritative inventory of object storage.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.stored_object (
    workspace_id        uuid        NOT NULL,
    object_id           uuid        NOT NULL,
    logical_key         text        NOT NULL,
    area                smallint    NOT NULL,
    document_id         uuid        NULL,
    sha256              bytea       NOT NULL,
    size_bytes          bigint      NOT NULL,
    content_type        text        NULL,
    key_id              text        NOT NULL,
    encryption_scheme   smallint    NOT NULL,
    wrapped_dek         bytea       NULL,
    state               smallint    NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by_job_id   uuid        NULL,
    CONSTRAINT stored_object_pk PRIMARY KEY (workspace_id, object_id),
    CONSTRAINT stored_object_logical_key_uq UNIQUE (workspace_id, logical_key),
    -- FK target that pins a document's artifact to that same document; also the per-document lookup index.
    CONSTRAINT stored_object_document_uq UNIQUE (workspace_id, document_id, object_id),
    CONSTRAINT stored_object_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    -- Deferred: the write protocol registers the object and its referencing document in one transaction (§2.4).
    CONSTRAINT stored_object_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id) DEFERRABLE INITIALLY DEFERRED,
    -- ADR-011 §1.2/§1.4: lowercase grammar, workspace objects live under ws/{workspaceId:N}/.
    CONSTRAINT stored_object_logical_key_ck CHECK (
        octet_length(logical_key) <= 512
        AND logical_key ~ '^[a-z0-9._-]+(/[a-z0-9._-]+)*$'
        AND logical_key !~ '(^|/)\.{1,2}(/|$)'
        AND starts_with(logical_key, 'ws/' || replace(workspace_id::text, '-', '') || '/')),
    CONSTRAINT stored_object_area_ck CHECK (area BETWEEN 1 AND 11),
    CONSTRAINT stored_object_sha256_ck CHECK (octet_length(sha256) = 32),
    CONSTRAINT stored_object_size_ck CHECK (size_bytes >= 0),
    CONSTRAINT stored_object_encryption_ck CHECK (
        (encryption_scheme = 1 AND wrapped_dek IS NULL) OR (encryption_scheme = 2 AND wrapped_dek IS NOT NULL)),
    CONSTRAINT stored_object_state_ck CHECK (state BETWEEN 1 AND 2)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- A document artifact must be an object registered for that same document.
ALTER TABLE opportunity.document
    ADD CONSTRAINT document_native_object_fk
        FOREIGN KEY (workspace_id, document_id, native_object_id)
        REFERENCES opportunity.stored_object (workspace_id, document_id, object_id),
    ADD CONSTRAINT document_text_object_fk
        FOREIGN KEY (workspace_id, document_id, text_object_id)
        REFERENCES opportunity.stored_object (workspace_id, document_id, object_id);

-- ---------------------------------------------------------------------------------------------------------------
-- Page model (ADR-012 §1): Document 1-* PageSet 1-* Page 1-* PageImage
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.page_set (
    workspace_id            uuid        NOT NULL,
    page_set_id             uuid        NOT NULL,
    document_id             uuid        NOT NULL,
    source                  smallint    NOT NULL,
    import_job_id           uuid        NULL,
    renderer_name           text        NULL,
    renderer_version        text        NULL,
    render_settings_hash    bytea       NULL,
    page_count              integer     NOT NULL DEFAULT 0,
    status                  smallint    NOT NULL DEFAULT 0,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT page_set_pk PRIMARY KEY (workspace_id, page_set_id),
    CONSTRAINT page_set_document_uq UNIQUE (workspace_id, document_id, page_set_id),
    CONSTRAINT page_set_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    -- Imported sets come from an import job; rendered sets record the renderer that made them.
    CONSTRAINT page_set_source_ck CHECK (
        (source = 1 AND renderer_name IS NULL)
        OR (source = 2 AND renderer_name IS NOT NULL AND renderer_version IS NOT NULL
            AND render_settings_hash IS NOT NULL)),
    CONSTRAINT page_set_page_count_ck CHECK (page_count >= 0),
    CONSTRAINT page_set_status_ck CHECK (status BETWEEN 0 AND 3)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- The active page set must be one of the document's own sets. Deferred so a chunk can insert the document and its
-- imported page set in either order.
ALTER TABLE opportunity.document
    ADD CONSTRAINT document_active_page_set_fk
        FOREIGN KEY (workspace_id, document_id, active_page_set_id)
        REFERENCES opportunity.page_set (workspace_id, document_id, page_set_id)
        DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE opportunity.page (
    workspace_id    uuid          NOT NULL,
    page_set_id     uuid          NOT NULL,
    ordinal         integer       NOT NULL,
    document_id     uuid          NOT NULL,
    image_key       text          NULL,
    width_pt        numeric(8, 2) NOT NULL,
    height_pt       numeric(8, 2) NOT NULL,
    rotation        smallint      NOT NULL DEFAULT 0,
    color_mode      smallint      NOT NULL,
    source_frame    integer       NOT NULL DEFAULT 0,
    image_missing   boolean       NOT NULL DEFAULT false,
    CONSTRAINT page_pk PRIMARY KEY (workspace_id, page_set_id, ordinal),
    CONSTRAINT page_page_set_fk FOREIGN KEY (workspace_id, document_id, page_set_id)
        REFERENCES opportunity.page_set (workspace_id, document_id, page_set_id),
    CONSTRAINT page_ordinal_ck CHECK (ordinal >= 1),
    CONSTRAINT page_geometry_ck CHECK (width_pt > 0 AND height_pt > 0),
    CONSTRAINT page_rotation_ck CHECK (rotation IN (0, 90, 180, 270)),
    CONSTRAINT page_color_mode_ck CHECK (color_mode BETWEEN 1 AND 3),
    CONSTRAINT page_source_frame_ck CHECK (source_frame >= 0)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

CREATE INDEX page_page_set_ix ON opportunity.page (workspace_id, document_id, page_set_id);

CREATE TABLE opportunity.page_image (
    workspace_id    uuid     NOT NULL,
    page_set_id     uuid     NOT NULL,
    ordinal         integer  NOT NULL,
    purpose         smallint NOT NULL,
    object_id       uuid     NOT NULL,
    width_px        integer  NOT NULL,
    height_px       integer  NOT NULL,
    dpi_x           integer  NOT NULL,
    dpi_y           integer  NOT NULL,
    format          smallint NOT NULL,
    CONSTRAINT page_image_pk PRIMARY KEY (workspace_id, page_set_id, ordinal, purpose),
    CONSTRAINT page_image_page_fk FOREIGN KEY (workspace_id, page_set_id, ordinal)
        REFERENCES opportunity.page (workspace_id, page_set_id, ordinal),
    CONSTRAINT page_image_object_fk FOREIGN KEY (workspace_id, object_id)
        REFERENCES opportunity.stored_object (workspace_id, object_id),
    CONSTRAINT page_image_purpose_ck CHECK (purpose BETWEEN 1 AND 4),
    CONSTRAINT page_image_size_ck CHECK (width_px > 0 AND height_px > 0 AND dpi_x > 0 AND dpi_y > 0),
    CONSTRAINT page_image_format_ck CHECK (format BETWEEN 1 AND 4)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

CREATE INDEX page_image_object_ix ON opportunity.page_image (workspace_id, object_id);
