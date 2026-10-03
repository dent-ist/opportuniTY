-- V0006: Job, JobChunk and JobChunkItemResult with leases and fencing tokens (E06-T02).
-- Binding: ADR-010 (§1 entities, §2 state machines and fences F1-F3, §3 leases, §4 membership reference, §5 idempotency
-- key, §7 PostgreSQL as the retry ledger), ADR-002 (jobs reference a materialized snapshot), ADR-005 R1/R3 (mutable
-- tenant tables: not partitioned, partition-ready keys), ADR-015 D7 (forced RLS on app.workspace_id).
--
-- Enumerations: job status/type are text (low volume, read by operators); chunk and item enums are smallint with the
-- code values in Opportunity.Core.Jobs. Every status change is a conditional UPDATE whose allowed sources come from
-- Opportunity.Core.Jobs.JobStateMachine / JobChunkStateMachine.
-- Not here: snapshot tables (ADR-002, E10) and ImportBatch/ImportBatchMember (E08-T03) do not exist yet, so
-- target_snapshot_id, snapshot_id and import_batch_id carry no foreign key until those tables land.

-- ---------------------------------------------------------------------------------------------------------------
-- Job. Progress counters are maintained by each chunk's settling transaction, so progress is O(1) (ADR-010 §1).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.job (
    workspace_id                    uuid        NOT NULL,
    job_id                          uuid        NOT NULL,
    job_type                        text        NOT NULL,
    status                          text        NOT NULL DEFAULT 'Created',
    status_reason                   text        NULL,
    target_snapshot_id              uuid        NULL,
    import_batch_id                 uuid        NULL,
    -- Validated job parameters (identifiers and settings, no document content).
    parameters                      jsonb       NOT NULL DEFAULT '{}',
    -- Execution identity: workers take workspace and actor from this row, never from a message (ADR-010 §9).
    initiated_by                    uuid        NOT NULL,
    -- HTTP Idempotency-Key that created the job (ADR-019 §2.6); unrelated to chunk keys.
    client_idempotency_key          text        NULL,
    correlation_id                  text        NULL,
    -- Inputs of the chunk idempotency key (ADR-010 §5.1), fixed when the chunks are planned.
    operation_kind                  text        NULL,
    projection_generation           bigint      NOT NULL DEFAULT 0,
    max_attempts                    smallint    NOT NULL DEFAULT 5,
    -- ADR-001 §7: the search generation of the job's last chunk commit; null until then.
    job_generation                  bigint      NULL,

    chunks_total                    integer     NOT NULL DEFAULT 0,
    chunks_committed                integer     NOT NULL DEFAULT 0,
    chunks_failed                   integer     NOT NULL DEFAULT 0,
    chunks_cancelled                integer     NOT NULL DEFAULT 0,
    items_applied                   bigint      NOT NULL DEFAULT 0,
    items_unchanged                 bigint      NOT NULL DEFAULT 0,
    items_skipped_concurrent_edit   bigint      NOT NULL DEFAULT 0,
    items_excluded_no_access        bigint      NOT NULL DEFAULT 0,
    items_failed                    bigint      NOT NULL DEFAULT 0,
    index_tasks_total               bigint      NOT NULL DEFAULT 0,
    index_tasks_applied             bigint      NOT NULL DEFAULT 0,

    -- Circuit breaker (ADR-010 §7.5).
    consecutive_failures            integer     NOT NULL DEFAULT 0,
    last_failure_class              smallint    NULL,

    cancel_requested_by             uuid        NULL,
    cancel_requested_at             timestamptz NULL,
    created_at                      timestamptz NOT NULL DEFAULT now(),
    updated_at                      timestamptz NOT NULL DEFAULT now(),
    started_at                      timestamptz NULL,
    finished_at                     timestamptz NULL,

    CONSTRAINT job_pk PRIMARY KEY (workspace_id, job_id),
    CONSTRAINT job_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT job_type_ck CHECK (job_type IN (
        'Import', 'BulkCoding', 'RelationshipFixup', 'Reindex', 'Export', 'Production', 'Render')),
    CONSTRAINT job_status_ck CHECK (status IN (
        'Created', 'Preparing', 'Running', 'Paused', 'Cancelling', 'Cancelled', 'Completed', 'CompletedWithErrors',
        'Failed')),
    CONSTRAINT job_operation_kind_ck CHECK (operation_kind IN (
        'ImportChunk', 'BulkCodingChunk', 'RelationshipChunk', 'IndexChunk', 'ReindexChunk', 'ExportChunk',
        'ProductionChunk', 'RenderChunk')),
    CONSTRAINT job_planned_ck CHECK (status IN ('Created', 'Preparing', 'Failed', 'Cancelled', 'Cancelling')
        OR operation_kind IS NOT NULL),
    CONSTRAINT job_status_reason_ck CHECK (length(status_reason) <= 2000),
    CONSTRAINT job_parameters_ck CHECK (jsonb_typeof(parameters) = 'object'),
    CONSTRAINT job_client_idempotency_key_ck CHECK (length(client_idempotency_key) BETWEEN 1 AND 128),
    CONSTRAINT job_correlation_id_ck CHECK (length(correlation_id) <= 128),
    CONSTRAINT job_projection_generation_ck CHECK (projection_generation >= 0),
    CONSTRAINT job_max_attempts_ck CHECK (max_attempts BETWEEN 1 AND 100),
    CONSTRAINT job_counters_ck CHECK (
        chunks_committed >= 0 AND chunks_failed >= 0 AND chunks_cancelled >= 0
        AND chunks_committed + chunks_failed + chunks_cancelled <= chunks_total
        AND items_applied >= 0 AND items_unchanged >= 0 AND items_skipped_concurrent_edit >= 0
        AND items_excluded_no_access >= 0 AND items_failed >= 0
        AND index_tasks_applied BETWEEN 0 AND index_tasks_total),
    CONSTRAINT job_consecutive_failures_ck CHECK (consecutive_failures >= 0),
    CONSTRAINT job_finished_ck CHECK (
        (status IN ('Completed', 'CompletedWithErrors', 'Cancelled', 'Failed')) = (finished_at IS NOT NULL))
) WITH (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- Job creation is idempotent per (workspace, initiator, Idempotency-Key).
CREATE UNIQUE INDEX job_client_idempotency_key_uq ON opportunity.job (workspace_id, initiated_by, client_idempotency_key)
    WHERE client_idempotency_key IS NOT NULL;
-- Job lists and the job tray (§28): newest first, and the unfinished ones.
CREATE INDEX job_created_ix ON opportunity.job (workspace_id, created_at DESC, job_id);
CREATE INDEX job_active_ix ON opportunity.job (workspace_id, created_at)
    WHERE status NOT IN ('Completed', 'CompletedWithErrors', 'Cancelled', 'Failed');

-- ---------------------------------------------------------------------------------------------------------------
-- JobChunk. All chunks are planned up front in Preparing. LeaseToken is the fencing token: every claim increments it,
-- and only the holder of the current token may extend, commit or fail the chunk (ADR-010 §3, fence F3).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.job_chunk (
    workspace_id            uuid        NOT NULL,
    chunk_id                uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    chunk_sequence          integer     NOT NULL,
    status                  smallint    NOT NULL DEFAULT 1,

    -- Membership reference (ADR-010 §4), one shape per kind.
    membership_kind         smallint    NOT NULL,
    snapshot_id             uuid        NULL,
    import_batch_id         uuid        NULL,
    range_from              bigint      NULL,
    range_to                bigint      NULL,
    projection_generation   bigint      NULL,
    document_id_from        uuid        NULL,
    document_id_to          uuid        NULL,
    document_ids            uuid[]      NULL,
    item_count              integer     NOT NULL,
    estimated_bytes         bigint      NULL,

    idempotency_key         text        NOT NULL,
    attempt_count           integer     NOT NULL DEFAULT 0,
    max_attempts            smallint    NOT NULL,
    available_at            timestamptz NOT NULL DEFAULT now(),
    lease_owner             text        NULL,
    lease_expires_at        timestamptz NULL,
    lease_token             bigint      NOT NULL DEFAULT 0,
    last_error              text        NULL,
    error_class             smallint    NULL,
    error_code              text        NULL,
    replay_count            integer     NOT NULL DEFAULT 0,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    claimed_at              timestamptz NULL,
    settled_at              timestamptz NULL,

    CONSTRAINT job_chunk_pk PRIMARY KEY (workspace_id, chunk_id),
    CONSTRAINT job_chunk_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT job_chunk_sequence_uq UNIQUE (workspace_id, job_id, chunk_sequence),
    -- ADR-010 §5.1: one chunk per key per workspace.
    CONSTRAINT job_chunk_idempotency_key_uq UNIQUE (workspace_id, idempotency_key),
    CONSTRAINT job_chunk_sequence_ck CHECK (chunk_sequence >= 1),
    CONSTRAINT job_chunk_status_ck CHECK (status BETWEEN 1 AND 7),
    CONSTRAINT job_chunk_membership_ck CHECK (CASE membership_kind
        WHEN 1 THEN snapshot_id IS NOT NULL AND range_from >= 1 AND range_to >= range_from
                    AND num_nulls(import_batch_id, projection_generation, document_id_from, document_id_to, document_ids) = 5
        WHEN 2 THEN import_batch_id IS NOT NULL AND range_from >= 1 AND range_to >= range_from
                    AND num_nulls(snapshot_id, projection_generation, document_id_from, document_id_to, document_ids) = 5
        WHEN 3 THEN projection_generation >= 1 AND document_id_from <= document_id_to
                    AND num_nulls(snapshot_id, import_batch_id, range_from, range_to, document_ids) = 5
        WHEN 4 THEN cardinality(document_ids) BETWEEN 1 AND 1000
                    AND num_nulls(snapshot_id, import_batch_id, range_from, range_to, projection_generation,
                                  document_id_from, document_id_to) = 7
        ELSE false END),
    CONSTRAINT job_chunk_item_count_ck CHECK (item_count >= 0 AND (estimated_bytes IS NULL OR estimated_bytes >= 0)),
    CONSTRAINT job_chunk_idempotency_key_ck CHECK (idempotency_key ~ '^[0-9a-f]{64}$'),
    CONSTRAINT job_chunk_attempts_ck CHECK (max_attempts >= 1 AND attempt_count BETWEEN 0 AND max_attempts),
    -- A lease exists exactly while the chunk is Running.
    CONSTRAINT job_chunk_lease_ck CHECK ((status = 3) = (lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL)
        AND (lease_owner IS NULL) = (lease_expires_at IS NULL)),
    CONSTRAINT job_chunk_lease_owner_ck CHECK (length(lease_owner) BETWEEN 1 AND 200),
    CONSTRAINT job_chunk_lease_token_ck CHECK (lease_token >= 0),
    CONSTRAINT job_chunk_error_ck CHECK (error_class BETWEEN 1 AND 3 AND length(last_error) <= 2000
        AND length(error_code) BETWEEN 1 AND 100),
    CONSTRAINT job_chunk_replay_count_ck CHECK (replay_count >= 0),
    CONSTRAINT job_chunk_settled_ck CHECK ((status IN (5, 6, 7)) = (settled_at IS NOT NULL))
) WITH (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- Lease sweeper: Running chunks by lease expiry.
CREATE INDEX job_chunk_lease_ix ON opportunity.job_chunk (workspace_id, lease_expires_at) WHERE status = 3;
-- Claim-next, dispatch and cancel: open chunks of a job in sequence order (Pending, Dispatched, RetryWait).
CREATE INDEX job_chunk_open_ix ON opportunity.job_chunk (workspace_id, job_id, chunk_sequence) WHERE status IN (1, 2, 4);
-- Replay and failure reports.
CREATE INDEX job_chunk_failed_ix ON opportunity.job_chunk (workspace_id, job_id, chunk_sequence) WHERE status = 6;

-- ---------------------------------------------------------------------------------------------------------------
-- JobChunkItemResult: per-item outcomes that are not plain success (Q-07 skips, Q-15 exclusions, item errors), written
-- by the chunk's commit transaction only, so a rolled-back attempt leaves none. Source of the job's reports.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.job_chunk_item_result (
    workspace_id    uuid        NOT NULL,
    job_id          uuid        NOT NULL,
    chunk_id        uuid        NOT NULL,
    item_no         integer     NOT NULL,
    kind            smallint    NOT NULL,
    document_id     uuid        NULL,
    -- Import rows that never became a document are identified by row number.
    row_no          bigint      NULL,
    field_id        integer     NULL,
    reason_code     text        NOT NULL,
    detail          text        NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT job_chunk_item_result_pk PRIMARY KEY (workspace_id, job_id, chunk_id, item_no),
    CONSTRAINT job_chunk_item_result_chunk_fk FOREIGN KEY (workspace_id, chunk_id)
        REFERENCES opportunity.job_chunk (workspace_id, chunk_id),
    CONSTRAINT job_chunk_item_result_job_fk FOREIGN KEY (workspace_id, job_id)
        REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT job_chunk_item_result_kind_ck CHECK (kind BETWEEN 1 AND 3),
    CONSTRAINT job_chunk_item_result_item_ck CHECK (item_no >= 1 AND (document_id IS NOT NULL OR row_no IS NOT NULL)),
    CONSTRAINT job_chunk_item_result_reason_ck CHECK (length(reason_code) BETWEEN 1 AND 100 AND length(detail) <= 2000)
);

-- Downloadable lists per kind (e.g. Q-07 skipped documents), in chunk order.
CREATE INDEX job_chunk_item_result_kind_ix ON opportunity.job_chunk_item_result (workspace_id, job_id, kind, chunk_id, item_no);

REVOKE UPDATE ON opportunity.job_chunk_item_result FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Row-level security (ADR-015 D7.1): the one workspace isolation policy, forced. Repositories set app.workspace_id
-- per transaction (WorkspaceTransaction).
-- ---------------------------------------------------------------------------------------------------------------
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['opportunity.job', 'opportunity.job_chunk', 'opportunity.job_chunk_item_result'] LOOP
        EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY, FORCE ROW LEVEL SECURITY', t);
        EXECUTE format(
            'CREATE POLICY workspace_isolation ON %s AS PERMISSIVE FOR ALL TO PUBLIC '
            || 'USING (workspace_id = NULLIF(current_setting(''app.workspace_id'', true), '''')::uuid) '
            || 'WITH CHECK (workspace_id = NULLIF(current_setting(''app.workspace_id'', true), '''')::uuid)',
            t);
    END LOOP;
END
$$;
