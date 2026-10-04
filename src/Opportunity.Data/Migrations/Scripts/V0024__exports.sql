-- V0024: exports of a materialized snapshot to a load-file volume (E12-T01).
-- Binding: ADR-002 (an export targets a Ready, frozen snapshot; the job's target_snapshot_id keeps it from expiring);
-- ADR-010 §4/§6 (SnapshotRange chunks of at most 250 documents; each chunk writes its own rows with fence F3);
-- ADR-011 §1.5/§1.6 (files live under ws/{id}/exports/{exportId}/{runId}/; keys hold generated names only, the
-- delivered volume path is a column here: the manifest maps key → path); ADR-013 (Export.* audit); ADR-015 D9.4 / Q-15
-- (every chunk re-authorizes its documents for the initiator; excluded documents are recorded with the generic reason
-- shown to the requester, the precise one is in audit only); ADR-015 D7 (forced RLS).
--
-- Lifecycle: the API creates the export and its Export job (Created) in one transaction; the export worker plans one
-- chunk per snapshot ordinal range and starts the job; each chunk writes its documents' files to object storage, then
-- registers them with its outcome rows and the chunk commit in one transaction; once every chunk has settled the worker
-- assembles the DAT and OPT from the chunks' parts, writes the manifest and completes the export.

CREATE TABLE opportunity.export (
    workspace_id            uuid        NOT NULL,
    export_id               uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    snapshot_id             uuid        NOT NULL,
    name                    text        NOT NULL,
    -- The validated export settings (field list and order, delimiters, encoding, volume layout, included files),
    -- frozen when the export was created.
    settings                jsonb       NOT NULL,
    -- 1 Running, 2 Completed, 3 Failed, 4 Cancelled.
    status                  smallint    NOT NULL DEFAULT 1,
    status_reason           text        NULL,
    -- The initiator, as at creation; workers re-read the current group snapshot from app_user when it exists.
    created_by              uuid        NOT NULL,
    created_by_display      text        NOT NULL,
    created_by_groups       text[]      NOT NULL DEFAULT '{}',
    created_at              timestamptz NOT NULL DEFAULT now(),
    completed_at            timestamptz NULL,
    -- Claim of the worker that plans or finalizes the export; another worker takes over once it expires.
    claimed_by              text        NULL,
    claimed_until           timestamptz NULL,
    -- Report, written by the finalization.
    documents_exported      bigint      NULL,
    documents_excluded      bigint      NULL,
    natives                 bigint      NULL,
    texts                   bigint      NULL,
    images                  bigint      NULL,
    pages                   bigint      NULL,
    file_count              bigint      NULL,
    total_bytes             bigint      NULL,
    manifest_sha256         bytea       NULL,
    CONSTRAINT export_pk PRIMARY KEY (workspace_id, export_id),
    CONSTRAINT export_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT export_job_uq UNIQUE (workspace_id, job_id),
    CONSTRAINT export_snapshot_fk FOREIGN KEY (workspace_id, snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT export_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT export_settings_ck CHECK (jsonb_typeof(settings) = 'object' AND octet_length(settings::text) <= 262144),
    CONSTRAINT export_status_ck CHECK (status BETWEEN 1 AND 4 AND length(status_reason) <= 2000),
    CONSTRAINT export_created_by_display_ck CHECK (length(created_by_display) BETWEEN 1 AND 512),
    CONSTRAINT export_claim_ck CHECK ((claimed_by IS NULL) = (claimed_until IS NULL) AND length(claimed_by) <= 200),
    CONSTRAINT export_completed_ck CHECK ((status = 1) = (completed_at IS NULL)),
    CONSTRAINT export_report_ck CHECK (status <> 2 OR (documents_exported >= 0 AND documents_excluded >= 0 AND natives >= 0
        AND texts >= 0 AND images >= 0 AND pages >= 0 AND file_count >= 0 AND total_bytes >= 0
        AND octet_length(manifest_sha256) = 32))
);

-- The export list (newest first) and the exports a worker still has to plan or finalize.
CREATE INDEX export_created_ix ON opportunity.export (workspace_id, created_at DESC, export_id);
CREATE INDEX export_running_ix ON opportunity.export (workspace_id, created_at) WHERE status = 1;

SELECT opportunity.enable_workspace_rls('opportunity.export');
REVOKE DELETE, TRUNCATE ON opportunity.export FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- One row per snapshot member a chunk handled: exported, or excluded by the Q-15 re-check. reason is the generic,
-- requester-facing reason (AccessChanged); the precise PDP reason goes to the Export.DocumentsExcluded audit event.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.export_document (
    workspace_id        uuid        NOT NULL,
    export_id           uuid        NOT NULL,
    ordinal             bigint      NOT NULL,
    document_id         uuid        NOT NULL,
    chunk_sequence      integer     NOT NULL,
    -- 1 Exported, 2 Excluded.
    outcome             smallint    NOT NULL,
    control_number      text        NULL,
    reason              text        NULL,
    natives             integer     NOT NULL DEFAULT 0,
    texts               integer     NOT NULL DEFAULT 0,
    images              integer     NOT NULL DEFAULT 0,
    pages               integer     NOT NULL DEFAULT 0,
    CONSTRAINT export_document_pk PRIMARY KEY (workspace_id, export_id, ordinal),
    CONSTRAINT export_document_export_fk FOREIGN KEY (workspace_id, export_id)
        REFERENCES opportunity.export (workspace_id, export_id),
    CONSTRAINT export_document_ck CHECK (ordinal >= 1 AND chunk_sequence >= 1 AND outcome BETWEEN 1 AND 2
        AND (outcome = 1) = (reason IS NULL) AND length(reason) <= 100 AND length(control_number) <= 255
        AND natives >= 0 AND texts >= 0 AND images >= 0 AND pages >= 0)
);

CREATE INDEX export_document_outcome_ix ON opportunity.export_document (workspace_id, export_id, outcome, ordinal);

SELECT opportunity.enable_workspace_rls('opportunity.export_document');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.export_document FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Every object of an export: the delivered volume files (path = the relative path inside the package, which is the
-- legal artifact), the manifest and reports, and the per-chunk DAT/OPT parts the finalization assembles (kind 8/9,
-- never delivered). object_id is the StoredObject registry row of the generated key.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.export_file (
    workspace_id        uuid        NOT NULL,
    export_id           uuid        NOT NULL,
    file_id             uuid        NOT NULL,
    path                text        NOT NULL,
    -- 1 Native, 2 Text, 3 Image, 4 Dat, 5 Opt, 6 Manifest, 7 Report, 8 DatPart, 9 OptPart.
    kind                smallint    NOT NULL,
    object_id           uuid        NOT NULL,
    sha256              bytea       NOT NULL,
    size_bytes          bigint      NOT NULL,
    content_type        text        NOT NULL,
    chunk_sequence      integer     NULL,
    ordinal             bigint      NULL,
    CONSTRAINT export_file_pk PRIMARY KEY (workspace_id, export_id, path),
    CONSTRAINT export_file_id_uq UNIQUE (workspace_id, export_id, file_id),
    CONSTRAINT export_file_export_fk FOREIGN KEY (workspace_id, export_id)
        REFERENCES opportunity.export (workspace_id, export_id),
    CONSTRAINT export_file_object_fk FOREIGN KEY (workspace_id, object_id)
        REFERENCES opportunity.stored_object (workspace_id, object_id),
    CONSTRAINT export_file_ck CHECK (length(path) BETWEEN 1 AND 1000 AND path !~ '[[:cntrl:]]' AND kind BETWEEN 1 AND 9
        AND octet_length(sha256) = 32 AND size_bytes >= 0 AND length(content_type) <= 200
        AND (chunk_sequence IS NULL OR chunk_sequence >= 1) AND (ordinal IS NULL OR ordinal >= 1))
);

SELECT opportunity.enable_workspace_rls('opportunity.export_file');
REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.export_file FROM opportunity_app;
