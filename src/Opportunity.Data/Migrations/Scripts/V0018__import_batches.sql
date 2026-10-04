-- V0018: import batches, their row membership and row-level outcomes (E08-T03).
-- Binding: ADR-010 §4 (ImportRows membership: ImportBatchMember rows written by the import chunk's own transaction),
-- §5 (idempotency), §6 (500 rows per chunk); ADR-009 R3/R4 (control numbers unique per workspace, never reused);
-- ADR-011 (the DAT lives in object storage under ws/{id}/imports/...; rows hold its logical key, no file content);
-- ADR-015 D7 (forced RLS). Ticket review E08-T03: every import has a user-visible name and report counters in the
-- vocabulary read / imported / overlaid / skipped / errored.
--
-- Lifecycle: the API creates the batch and its Import job (Created) in one transaction; the import worker prepares it
-- (one streaming pass: duplicate keys of the whole file, fields and choices to create, chunk byte ranges), plans the
-- chunks and starts the job; each chunk transaction writes documents, members, row outcomes, the batch counters, its
-- IndexChunkTask and the chunk commit (fence F3) together.

CREATE TABLE opportunity.import_batch (
    workspace_id            uuid        NOT NULL,
    import_batch_id         uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    -- "Import name" (ticket review E08-T03): default "<DAT file name> <yyyy-mm-dd>", editable at start.
    name                    text        NOT NULL,
    -- 1 Append, 2 Overlay, 3 AppendOverlay (Opportunity.Contracts.Import.ImportMode + 1).
    mode                    smallint    NOT NULL,
    source_file_name        text        NOT NULL,
    source_object_key       text        NOT NULL,
    source_sha256           bytea       NOT NULL,
    source_size             bigint      NOT NULL,
    profile_id              uuid        NULL,
    profile_version         bigint      NULL,
    -- The effective import profile (Contracts ImportProfileDefinition JSON) frozen when the import started.
    profile                 jsonb       NOT NULL,
    -- Q-31: coding/privilege fields an administrator enabled for overlay by this import; empty when none.
    coding_overlay_field_ids integer[]  NOT NULL DEFAULT '{}',
    -- Workspace.ManageFields was checked and granted at start because the mapping creates fields or choices; the
    -- preparation pass (which recompiles the mapping against the then-current catalog) creates none without it.
    may_create_fields       boolean     NOT NULL DEFAULT false,

    -- Facts of the preparation pass, fixed before the job starts.
    prepare_claimed_by      text        NULL,
    prepare_claimed_until   timestamptz NULL,
    header                  jsonb       NULL,
    dat_encoding            text        NULL,
    dat_encoding_fallback   boolean     NULL,
    -- Bytes before the first data row (byte-order mark, header record, blank lines): every chunk re-reads them.
    data_offset             bigint      NULL,
    rows_total              bigint      NULL,
    fields_created          integer     NOT NULL DEFAULT 0,
    choices_created         integer     NOT NULL DEFAULT 0,
    prepared_at             timestamptz NULL,

    -- Report counters, maintained by each chunk's commit transaction.
    rows_imported           bigint      NOT NULL DEFAULT 0,
    rows_overlaid           bigint      NOT NULL DEFAULT 0,
    rows_skipped            bigint      NOT NULL DEFAULT 0,
    rows_errored            bigint      NOT NULL DEFAULT 0,

    created_by              uuid        NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    completed_at            timestamptz NULL,

    CONSTRAINT import_batch_pk PRIMARY KEY (workspace_id, import_batch_id),
    CONSTRAINT import_batch_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT import_batch_job_uq UNIQUE (workspace_id, job_id),
    CONSTRAINT import_batch_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT import_batch_mode_ck CHECK (mode BETWEEN 1 AND 3),
    CONSTRAINT import_batch_source_file_name_ck CHECK (length(source_file_name) BETWEEN 1 AND 255),
    CONSTRAINT import_batch_source_key_ck CHECK (
        starts_with(source_object_key, 'ws/' || replace(workspace_id::text, '-', '') || '/imports/')),
    CONSTRAINT import_batch_source_sha256_ck CHECK (octet_length(source_sha256) = 32),
    CONSTRAINT import_batch_source_size_ck CHECK (source_size >= 0),
    CONSTRAINT import_batch_profile_ck CHECK (jsonb_typeof(profile) = 'object' AND octet_length(profile::text) <= 1048576),
    CONSTRAINT import_batch_prepare_claim_ck CHECK ((prepare_claimed_by IS NULL) = (prepare_claimed_until IS NULL)
        AND length(prepare_claimed_by) <= 200),
    CONSTRAINT import_batch_prepared_ck CHECK ((prepared_at IS NULL) OR (header IS NOT NULL AND dat_encoding IS NOT NULL
        AND data_offset >= 0 AND rows_total >= 0)),
    CONSTRAINT import_batch_counters_ck CHECK (rows_imported >= 0 AND rows_overlaid >= 0 AND rows_skipped >= 0
        AND rows_errored >= 0 AND fields_created >= 0 AND choices_created >= 0)
);

-- The import list (newest first) and the batches still to prepare.
CREATE INDEX import_batch_created_ix ON opportunity.import_batch (workspace_id, created_at DESC, import_batch_id);
CREATE INDEX import_batch_unprepared_ix ON opportunity.import_batch (workspace_id, created_at) WHERE prepared_at IS NULL;

SELECT opportunity.enable_workspace_rls('opportunity.import_batch');
REVOKE DELETE, TRUNCATE ON opportunity.import_batch FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Chunk byte ranges found by the preparation pass: a chunk re-reads [0, data_offset) + [byte_from, byte_to) of the DAT,
-- so it never parses the rows before it.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.import_batch_chunk (
    workspace_id        uuid    NOT NULL,
    import_batch_id     uuid    NOT NULL,
    row_from            bigint  NOT NULL,
    row_to              bigint  NOT NULL,
    byte_from           bigint  NOT NULL,
    byte_to             bigint  NOT NULL,
    line_from           bigint  NOT NULL,
    CONSTRAINT import_batch_chunk_pk PRIMARY KEY (workspace_id, import_batch_id, row_from),
    CONSTRAINT import_batch_chunk_batch_fk FOREIGN KEY (workspace_id, import_batch_id)
        REFERENCES opportunity.import_batch (workspace_id, import_batch_id),
    CONSTRAINT import_batch_chunk_range_ck CHECK (row_from >= 1 AND row_to >= row_from AND byte_from >= 0
        AND byte_to >= byte_from AND line_from >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.import_batch_chunk');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.import_batch_chunk FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- First row of each normalized control number in the file, written by the preparation pass in row order: a later row
-- with the same number is a duplicate of the WHOLE file, whichever chunk it lands in.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.import_batch_key (
    workspace_id        uuid    NOT NULL,
    import_batch_id     uuid    NOT NULL,
    control_number_norm text    NOT NULL,
    row_no              bigint  NOT NULL,
    CONSTRAINT import_batch_key_pk PRIMARY KEY (workspace_id, import_batch_id, control_number_norm),
    CONSTRAINT import_batch_key_batch_fk FOREIGN KEY (workspace_id, import_batch_id)
        REFERENCES opportunity.import_batch (workspace_id, import_batch_id),
    CONSTRAINT import_batch_key_row_ck CHECK (row_no >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.import_batch_key');
REVOKE UPDATE, TRUNCATE ON opportunity.import_batch_key FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- ImportBatchMember (ADR-010 §4): the document each committed row created or overlaid. The import IndexChunkTask
-- resolves its rows here; the "Import" browser lists a batch's documents from here.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.import_batch_member (
    workspace_id        uuid        NOT NULL,
    import_batch_id     uuid        NOT NULL,
    row_no              bigint      NOT NULL,
    document_id         uuid        NOT NULL,
    -- 1 Imported (created), 2 Overlaid (changed), 3 Skipped (overlay without changes).
    action              smallint    NOT NULL,
    CONSTRAINT import_batch_member_pk PRIMARY KEY (workspace_id, import_batch_id, row_no),
    CONSTRAINT import_batch_member_batch_fk FOREIGN KEY (workspace_id, import_batch_id)
        REFERENCES opportunity.import_batch (workspace_id, import_batch_id),
    CONSTRAINT import_batch_member_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT import_batch_member_row_ck CHECK (row_no >= 1),
    CONSTRAINT import_batch_member_action_ck CHECK (action BETWEEN 1 AND 3)
);

CREATE INDEX import_batch_member_document_ix ON opportunity.import_batch_member (workspace_id, document_id);

SELECT opportunity.enable_workspace_rls('opportunity.import_batch_member');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.import_batch_member FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Row-level errors and warnings of an import (the downloadable error detail): written by the chunk's commit
-- transaction only, so a rolled-back attempt leaves none. Errored rows also get one JobChunkItemResult (job counters).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.import_row_issue (
    workspace_id        uuid        NOT NULL,
    import_batch_id     uuid        NOT NULL,
    row_no              bigint      NOT NULL,
    issue_no            integer     NOT NULL,
    -- 1 Error (row not loaded), 2 Warning (row loaded).
    severity            smallint    NOT NULL,
    line_no             bigint      NULL,
    control_number      text        NULL,
    column_name         text        NULL,
    code                text        NOT NULL,
    message             text        NOT NULL,
    CONSTRAINT import_row_issue_pk PRIMARY KEY (workspace_id, import_batch_id, row_no, issue_no),
    CONSTRAINT import_row_issue_batch_fk FOREIGN KEY (workspace_id, import_batch_id)
        REFERENCES opportunity.import_batch (workspace_id, import_batch_id),
    CONSTRAINT import_row_issue_ck CHECK (row_no >= 1 AND issue_no >= 1 AND severity BETWEEN 1 AND 2
        AND length(code) BETWEEN 1 AND 100 AND length(message) <= 2000 AND length(control_number) <= 1000
        AND length(column_name) <= 1000)
);

CREATE INDEX import_row_issue_severity_ix ON opportunity.import_row_issue (workspace_id, import_batch_id, severity, row_no, issue_no);

SELECT opportunity.enable_workspace_rls('opportunity.import_row_issue');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.import_row_issue FROM opportunity_app;
