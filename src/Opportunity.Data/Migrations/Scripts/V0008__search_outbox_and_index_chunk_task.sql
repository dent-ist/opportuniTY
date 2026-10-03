-- V0008: SearchOutbox (interactive edits) and IndexChunkTask (bulk/import chunks), the two payload-free search work
-- records, plus the per-workspace SearchGeneration counter (E06-T03).
-- Binding: ADR-001 §1 R1-R4 (one work record per transaction, payload-free, columns, time-partitioned retention), §5
-- (lanes; no ordering guarantee), §6 (claim columns, attempts in PostgreSQL), §7.1 (late-lock generation counter stamps
-- SearchGeneration and CommittedAt as the transaction's last statement); ADR-010 §2 (IndexChunkTask states), §3
-- (leases + fencing token), §4 (membership reference, nullable SnapshotId), §5.1 (idempotency key); ADR-015 D7 (RLS).
--
-- Enumerations are smallint; the code values live in Opportunity.Core.SearchWork (statuses, change mask, task kind)
-- and Opportunity.Application.Messaging.MessageLane (lane). Both tables are range-partitioned by created_at in UTC days
-- so that retention drops whole partitions (search_work_drop_expired_partitions), never DELETEs rows.

-- The millisecond timestamp of a UUIDv7 (RFC 9562), exact. PostgreSQL 17's uuid_extract_timestamp() reads only v1.
CREATE FUNCTION opportunity.uuid_v7_timestamp(p_id uuid)
    RETURNS timestamptz
    LANGUAGE sql
    IMMUTABLE STRICT PARALLEL SAFE
RETURN timestamptz '1970-01-01 00:00:00+00'
    + ('x' || left(replace(p_id::text, '-', ''), 12))::bit(48)::bigint * interval '1 millisecond';

-- ---------------------------------------------------------------------------------------------------------------
-- WorkspaceSearchGeneration (ADR-001 §2, §7.1): commit-ordered, gap-free sequence of search work per workspace. The
-- increment is the last statement of a work-creating transaction, so its row lock is held only until the commit and a
-- rollback undoes the increment. Interactive and bulk work share it.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.workspace_search_generation (
    workspace_id    uuid    NOT NULL,
    value           bigint  NOT NULL,
    -- Per-workspace OutboxId counter (ADR-005 P5: no identity/serial on tenant tables), advanced in the same statement.
    last_outbox_id  bigint  NOT NULL DEFAULT 0,
    CONSTRAINT workspace_search_generation_pk PRIMARY KEY (workspace_id),
    CONSTRAINT workspace_search_generation_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT workspace_search_generation_value_ck CHECK (value >= 1 AND last_outbox_id >= 0)
) WITH (fillfactor = 50);

SELECT opportunity.enable_workspace_rls('opportunity.workspace_search_generation');
REVOKE DELETE, TRUNCATE ON opportunity.workspace_search_generation FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- SearchOutbox: one row per document per interactive transaction (ADR-001 §1 R3). Identifiers and versions only; the
-- index worker rebuilds the projection from current PostgreSQL state. Pending -> Claimed -> Dispatched -> Applied
-- (or straight to Applied when coalesced), plus Failed.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.search_outbox (
    workspace_id        uuid        NOT NULL,
    -- Partition key: transaction start (now()). CommittedAt is the lag measurement point.
    created_at          timestamptz NOT NULL DEFAULT now(),
    -- Unique per workspace, allocated from workspace_search_generation.last_outbox_id (ADR-005 P5).
    outbox_id           bigint      NOT NULL,
    document_id         uuid        NOT NULL,
    -- DocumentVersion written by this transaction: a hint for coalescing, not a payload (ADR-001 §3).
    document_version    bigint      NOT NULL,
    -- What changed (the "EventType" of §7), flags of Opportunity.Core.SearchWork.SearchChangeMask.
    change_mask         smallint    NOT NULL,
    -- Priority lane (ADR-001 §5.3): 1 Security (L0), 2 Interactive (L1).
    lane                smallint    NOT NULL,
    search_generation   bigint      NOT NULL,
    -- clock_timestamp() of the generation increment, the transaction's last statement (ADR-001 §7.1).
    committed_at        timestamptz NOT NULL,
    status              smallint    NOT NULL DEFAULT 1,
    -- Publish attempts; the envelope attempt (ADR-010 §5.2).
    attempt_count       integer     NOT NULL DEFAULT 0,
    available_at        timestamptz NOT NULL DEFAULT now(),
    claim_owner         text        NULL,
    claim_expires_at    timestamptz NULL,
    dispatched_at       timestamptz NULL,
    applied_at          timestamptz NULL,
    last_error          text        NULL,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT search_outbox_pk PRIMARY KEY (workspace_id, created_at, outbox_id),
    CONSTRAINT search_outbox_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT search_outbox_version_ck CHECK (document_version >= 1 AND outbox_id >= 1),
    CONSTRAINT search_outbox_change_mask_ck CHECK (change_mask BETWEEN 1 AND 63),
    CONSTRAINT search_outbox_lane_ck CHECK (lane IN (1, 2)),
    CONSTRAINT search_outbox_generation_ck CHECK (search_generation >= 1),
    CONSTRAINT search_outbox_status_ck CHECK (status BETWEEN 1 AND 5),
    CONSTRAINT search_outbox_attempts_ck CHECK (attempt_count >= 0),
    -- A claim exists exactly while the row is Claimed.
    CONSTRAINT search_outbox_claim_ck CHECK ((status = 2) = (claim_owner IS NOT NULL)
        AND (claim_owner IS NULL) = (claim_expires_at IS NULL)),
    CONSTRAINT search_outbox_claim_owner_ck CHECK (length(claim_owner) BETWEEN 1 AND 200),
    CONSTRAINT search_outbox_applied_ck CHECK ((status = 4) = (applied_at IS NOT NULL)),
    CONSTRAINT search_outbox_error_ck CHECK (length(last_error) <= 2000)
) PARTITION BY RANGE (created_at);

-- Dispatcher claim: open rows by lane, then generation (ADR-001 §6.1).
CREATE INDEX search_outbox_claim_ix ON opportunity.search_outbox (workspace_id, lane, search_generation) WHERE status IN (1, 2);
-- Worker-side coalescing: every non-applied row of a document up to a version (ADR-001 §5.2).
CREATE INDEX search_outbox_document_ix ON opportunity.search_outbox (workspace_id, document_id, document_version) WHERE status <> 4;
-- Watermark and lag: the oldest non-applied generation (ADR-001 §7.2, §7.4).
CREATE INDEX search_outbox_unapplied_ix ON opportunity.search_outbox (workspace_id, search_generation) WHERE status <> 4;
-- Message lookup by OutboxId (the payload carries no created_at).
CREATE INDEX search_outbox_id_ix ON opportunity.search_outbox (workspace_id, outbox_id);

SELECT opportunity.enable_workspace_rls('opportunity.search_outbox');
REVOKE DELETE, TRUNCATE ON opportunity.search_outbox FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- IndexChunkTask (§21 + ADR-001 §1 R3, ADR-010): one row per committed job chunk, identifiers and membership reference
-- only. Pending -> Dispatched -> Running (lease) -> Applied, plus RetryWait and Failed; never cancelled. task_id is a
-- UUIDv7 and created_at is its embedded timestamp, so a lookup by task id alone prunes to one partition.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.index_chunk_task (
    workspace_id            uuid        NOT NULL,
    created_at              timestamptz NOT NULL,
    task_id                 uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    chunk_id                uuid        NOT NULL,
    -- 1 Import, 2 BulkCoding, 3 Relationship (family/duplicate/thread fix-ups), 4 Reindex, 5 Repair.
    task_kind               smallint    NOT NULL,

    -- Membership reference (ADR-010 §4), same shapes as job_chunk; snapshot_id is null except for SnapshotRange.
    membership_kind         smallint    NOT NULL,
    snapshot_id             uuid        NULL,
    import_batch_id         uuid        NULL,
    range_from              bigint      NULL,
    range_to                bigint      NULL,
    projection_generation   bigint      NULL,
    document_id_from        uuid        NULL,
    document_id_to          uuid        NULL,
    document_ids            uuid[]      NULL,

    change_mask             smallint    NOT NULL,
    -- 3 SecurityBulk (L2), 4 Bulk (L3).
    lane                    smallint    NOT NULL,
    -- Null for Reindex tasks, which target a new projection generation and do not move the watermark (ADR-001 §7.5).
    search_generation       bigint      NULL,
    committed_at            timestamptz NOT NULL,
    status                  smallint    NOT NULL DEFAULT 1,
    idempotency_key         text        NOT NULL,
    attempt_count           integer     NOT NULL DEFAULT 0,
    max_attempts            smallint    NOT NULL DEFAULT 5,
    available_at            timestamptz NOT NULL DEFAULT now(),
    -- Dispatcher publish claim (ADR-001 §6.1); not a status.
    claim_owner             text        NULL,
    claim_expires_at        timestamptz NULL,
    -- Worker lease and fencing token (ADR-010 §3).
    lease_owner             text        NULL,
    lease_expires_at        timestamptz NULL,
    lease_token             bigint      NOT NULL DEFAULT 0,
    last_error              text        NULL,
    error_class             smallint    NULL,
    replay_count            integer     NOT NULL DEFAULT 0,
    dispatched_at           timestamptz NULL,
    started_at              timestamptz NULL,
    completed_at            timestamptz NULL,
    updated_at              timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT index_chunk_task_pk PRIMARY KEY (workspace_id, created_at, task_id),
    CONSTRAINT index_chunk_task_chunk_fk FOREIGN KEY (workspace_id, chunk_id)
        REFERENCES opportunity.job_chunk (workspace_id, chunk_id),
    CONSTRAINT index_chunk_task_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT index_chunk_task_created_ck CHECK (created_at = opportunity.uuid_v7_timestamp(task_id)),
    CONSTRAINT index_chunk_task_kind_ck CHECK (task_kind BETWEEN 1 AND 5),
    CONSTRAINT index_chunk_task_membership_ck CHECK (CASE membership_kind
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
    CONSTRAINT index_chunk_task_change_mask_ck CHECK (change_mask BETWEEN 1 AND 63),
    CONSTRAINT index_chunk_task_lane_ck CHECK (lane IN (3, 4)),
    CONSTRAINT index_chunk_task_generation_ck CHECK (CASE WHEN task_kind = 4 THEN search_generation IS NULL
        ELSE search_generation IS NOT NULL AND search_generation >= 1 END),
    CONSTRAINT index_chunk_task_status_ck CHECK (status BETWEEN 1 AND 6),
    CONSTRAINT index_chunk_task_idempotency_key_ck CHECK (idempotency_key ~ '^[0-9a-f]{64}$'),
    CONSTRAINT index_chunk_task_attempts_ck CHECK (max_attempts >= 1 AND attempt_count BETWEEN 0 AND max_attempts),
    CONSTRAINT index_chunk_task_claim_ck CHECK ((claim_owner IS NULL) = (claim_expires_at IS NULL)
        AND (claim_owner IS NULL OR status IN (1, 4))),
    CONSTRAINT index_chunk_task_claim_owner_ck CHECK (length(claim_owner) BETWEEN 1 AND 200),
    -- A lease exists exactly while the task is Running.
    CONSTRAINT index_chunk_task_lease_ck CHECK ((status = 3) = (lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL)
        AND (lease_owner IS NULL) = (lease_expires_at IS NULL)),
    CONSTRAINT index_chunk_task_lease_owner_ck CHECK (length(lease_owner) BETWEEN 1 AND 200),
    CONSTRAINT index_chunk_task_error_ck CHECK (error_class BETWEEN 1 AND 3 AND length(last_error) <= 2000),
    CONSTRAINT index_chunk_task_completed_ck CHECK ((status = 5) = (completed_at IS NOT NULL))
) PARTITION BY RANGE (created_at);

-- Dispatcher claim: open tasks by lane, then generation.
CREATE INDEX index_chunk_task_claim_ix ON opportunity.index_chunk_task (workspace_id, lane, search_generation) WHERE status IN (1, 4);
-- Watermark, lag and backlog: non-applied tasks.
CREATE INDEX index_chunk_task_unapplied_ix ON opportunity.index_chunk_task (workspace_id, search_generation) WHERE status <> 5;
-- Recovery: Running tasks by lease expiry, Dispatched tasks by dispatch time.
CREATE INDEX index_chunk_task_lease_ix ON opportunity.index_chunk_task (workspace_id, lease_expires_at) WHERE status = 3;
-- One task per job chunk (ADR-010 §5.1). A unique index on a partitioned table must contain the partition key, so the
-- per-workspace uniqueness of the key is guaranteed by fence F3 (a chunk commits once) and this index serves lookups.
CREATE INDEX index_chunk_task_idempotency_ix ON opportunity.index_chunk_task (workspace_id, idempotency_key);
CREATE INDEX index_chunk_task_job_ix ON opportunity.index_chunk_task (workspace_id, job_id, chunk_id);

SELECT opportunity.enable_workspace_rls('opportunity.index_chunk_task');
REVOKE DELETE, TRUNCATE ON opportunity.index_chunk_task FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Daily partitions (UTC) for both tables, from yesterday through p_through. Idempotent; runs as the schema owner so
-- the maintenance service can call it with the application role. Runtime roles get no privilege on a partition and
-- every partition carries the isolation policy (ADR-015 D7.4.1).
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.search_work_ensure_partitions(p_through timestamptz)
    RETURNS integer
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    day_start   date := (now() AT TIME ZONE 'UTC')::date - 1;
    last_day    date := (p_through AT TIME ZONE 'UTC')::date;
    parent      text;
    partition   text;
    created     integer := 0;
BEGIN
    WHILE day_start <= last_day LOOP
        FOREACH parent IN ARRAY ARRAY['search_outbox', 'index_chunk_task'] LOOP
            partition := parent || '_p' || to_char(day_start, 'YYYYMMDD');
            IF to_regclass('opportunity.' || partition) IS NULL THEN
                EXECUTE format(
                    'CREATE TABLE opportunity.%I PARTITION OF opportunity.%I FOR VALUES FROM (%L) TO (%L)',
                    partition, parent,
                    to_char(day_start, 'YYYY-MM-DD') || ' 00:00:00+00',
                    to_char(day_start + 1, 'YYYY-MM-DD') || ' 00:00:00+00');
                EXECUTE format('REVOKE ALL ON opportunity.%I FROM opportunity_app, opportunity_readonly', partition);
                PERFORM opportunity.enable_workspace_rls(format('opportunity.%I', partition)::regclass);
                created := created + 1;
            END IF;
        END LOOP;
        day_start := day_start + 1;
    END LOOP;
    RETURN created;
END
$$;

COMMENT ON FUNCTION opportunity.search_work_ensure_partitions(timestamptz) IS
    '@security-definer creates search_outbox/index_chunk_task day partitions ahead for the maintenance service; DDL only, reads no tenant rows.';

-- ---------------------------------------------------------------------------------------------------------------
-- Retention (ADR-001 §1 R4): drops every day partition that ended at or before p_cutoff and holds only Applied rows.
-- A partition that still holds other rows is kept and reported (the caller alerts); rows are never DELETEd.
-- FORCE ROW LEVEL SECURITY binds the owner too, so the count runs with FORCE lifted on that one partition under its
-- ACCESS EXCLUSIVE lock: no other session can observe the partition in that state, and it is dropped or re-forced
-- before the function returns. Returns one row per examined partition.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.search_work_drop_expired_partitions(p_cutoff timestamptz)
    RETURNS TABLE (parent_table text, partition_name text, range_end timestamptz, unapplied_rows bigint, dropped boolean)
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
    SET lock_timeout = '5s'
AS $$
DECLARE
    part        record;
    applied     smallint;
    remaining   bigint;
BEGIN
    FOR part IN
        SELECT p.relname::text AS parent, c.relname::text AS child,
               (to_date(right(c.relname, 8), 'YYYYMMDD') + 1)::timestamp AT TIME ZONE 'UTC' AS upper_bound
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        JOIN pg_class p ON p.oid = i.inhparent
        WHERE p.oid IN ('opportunity.search_outbox'::regclass, 'opportunity.index_chunk_task'::regclass)
          AND c.relname ~ '_p[0-9]{8}$'
        ORDER BY 3, 1
    LOOP
        CONTINUE WHEN part.upper_bound > p_cutoff;
        applied := CASE part.parent WHEN 'search_outbox' THEN 4 ELSE 5 END;
        EXECUTE format('LOCK TABLE opportunity.%I IN ACCESS EXCLUSIVE MODE', part.child);
        EXECUTE format('ALTER TABLE opportunity.%I NO FORCE ROW LEVEL SECURITY', part.child);
        EXECUTE format('SELECT count(*) FROM opportunity.%I WHERE status <> $1', part.child) INTO remaining USING applied;
        IF remaining = 0 THEN
            EXECUTE format('DROP TABLE opportunity.%I', part.child);
        ELSE
            EXECUTE format('ALTER TABLE opportunity.%I FORCE ROW LEVEL SECURITY', part.child);
        END IF;
        parent_table := part.parent;
        partition_name := part.child;
        range_end := part.upper_bound;
        unapplied_rows := remaining;
        dropped := remaining = 0;
        RETURN NEXT;
    END LOOP;
END
$$;

COMMENT ON FUNCTION opportunity.search_work_drop_expired_partitions(timestamptz) IS
    '@security-definer drops expired search work partitions whose rows are all Applied (ADR-001 R4); returns counts only, no tenant rows.';

-- Thirty-one days ahead; the maintenance service (SearchWorkRetentionService) keeps the horizon moving.
SELECT opportunity.search_work_ensure_partitions(now() + interval '31 days');
