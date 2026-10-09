-- V0054: audit hash chain, signed checkpoints and purge records (E14-T03). Binding: ADR-013 §1.2-§1.3 (sealer role,
-- per-workspace chains plus the system chain), §3.2-§3.4 (asynchronous sealer, EventHash = SHA-256(PrevHash || JCS),
-- checkpoints signed ES256 through ISigningKeyProvider, purpose audit-checkpoint; amendment 2026-10-09), ADR-014 §6
-- and V0048 (the audit purge and legal hold).
--
-- Chains are keyed by chain_id: the workspace id, or the nil uuid for the system chain (workspace_id null).
--   audit.chain_head   the sealer's position per chain (a lock target and a hint; verification never trusts it alone)
--   audit.checkpoint   signed checkpoints, append-only for every role
--   audit.chain_gap    runs of chain positions removed by the retention purge, written only by drop_expired_partition
--
-- Interactive writes are unchanged: inserts never wait for the chain. Sealing happens after commit, so a transaction
-- that commits late is simply chained after events that committed before it (the chain order is commit visibility,
-- then (recorded_at, event_id)).

-- ---------------------------------------------------------------------------------------------------------------
-- Indexes: the sealer finds unsealed events per chain; verification and checkpoints walk a chain by sequence.
-- ---------------------------------------------------------------------------------------------------------------
CREATE INDEX audit_event_unsealed_ix ON audit.audit_event (workspace_id, recorded_at, event_id) WHERE sequence IS NULL;
CREATE INDEX audit_event_chain_ix ON audit.audit_event (workspace_id, sequence) WHERE sequence IS NOT NULL;

-- ---------------------------------------------------------------------------------------------------------------
-- Generic append-only guard for the chain tables (the owner included).
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION audit.reject_chain_record_change()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'audit: % records are append-only (%)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'insufficient_privilege';
END
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Chain heads. Only the sealer writes them, and only forwards.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE audit.chain_head (
    chain_id                    uuid        NOT NULL,
    workspace_id                uuid        GENERATED ALWAYS AS (NULLIF(chain_id, '00000000-0000-0000-0000-000000000000'::uuid)) STORED,
    last_sequence               bigint      NOT NULL DEFAULT 0,
    last_hash                   bytea       NOT NULL DEFAULT decode(repeat('00', 32), 'hex'),
    last_checkpoint_sequence    bigint      NOT NULL DEFAULT 0,
    sealed_through              timestamptz NULL,
    updated_at                  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT chain_head_pk PRIMARY KEY (chain_id),
    CONSTRAINT chain_head_sequence_ck CHECK (last_sequence >= 0 AND last_checkpoint_sequence BETWEEN 0 AND last_sequence),
    CONSTRAINT chain_head_hash_ck CHECK (octet_length(last_hash) = 32)
);

COMMENT ON TABLE audit.chain_head IS 'Audit hash-chain sealer position per chain (E14-T03); nil chain_id = system chain.';

CREATE FUNCTION audit.chain_head_forward_only()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF NEW.chain_id <> OLD.chain_id OR NEW.last_sequence < OLD.last_sequence
        OR NEW.last_checkpoint_sequence < OLD.last_checkpoint_sequence
        OR (NEW.last_sequence = OLD.last_sequence AND NEW.last_hash <> OLD.last_hash) THEN
        RAISE EXCEPTION 'audit: a chain head only moves forward' USING ERRCODE = 'insufficient_privilege';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END
$$;

CREATE TRIGGER chain_head_before_update BEFORE UPDATE ON audit.chain_head
    FOR EACH ROW EXECUTE FUNCTION audit.chain_head_forward_only();
CREATE TRIGGER chain_head_before_delete BEFORE DELETE ON audit.chain_head
    FOR EACH ROW EXECUTE FUNCTION audit.reject_chain_record_change();
CREATE TRIGGER chain_head_before_truncate BEFORE TRUNCATE ON audit.chain_head
    FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_chain_record_change();

-- ---------------------------------------------------------------------------------------------------------------
-- Signed checkpoints (ADR-013 §3.4). The signature covers the JCS payload of chain, range, head hash, Merkle root of
-- the range's event hashes (RFC 6962), reason and time (Opportunity.Application.Audit.Chain.AuditChainFormat).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE audit.checkpoint (
    chain_id        uuid        NOT NULL,
    workspace_id    uuid        GENERATED ALWAYS AS (NULLIF(chain_id, '00000000-0000-0000-0000-000000000000'::uuid)) STORED,
    sequence        bigint      NOT NULL,
    from_sequence   bigint      NOT NULL,
    event_hash      bytea       NOT NULL,
    merkle_root     bytea       NOT NULL,
    event_count     bigint      NOT NULL,
    reason          text        NOT NULL,
    created_at      timestamptz NOT NULL,
    key_id          text        NOT NULL,
    algorithm       text        NOT NULL,
    signature       bytea       NOT NULL,
    CONSTRAINT checkpoint_pk PRIMARY KEY (chain_id, sequence),
    CONSTRAINT checkpoint_range_ck CHECK (from_sequence >= 0 AND sequence > from_sequence AND event_count = sequence - from_sequence),
    CONSTRAINT checkpoint_hash_ck CHECK (octet_length(event_hash) = 32 AND octet_length(merkle_root) = 32),
    CONSTRAINT checkpoint_reason_ck CHECK (reason IN ('Scheduled', 'Manual', 'BeforePurge', 'BeforeDeletion', 'MatterClosed')),
    CONSTRAINT checkpoint_key_ck CHECK (key_id ~ '^[a-z0-9-]{1,64}-v[0-9]{1,9}$'),
    CONSTRAINT checkpoint_algorithm_ck CHECK (algorithm = 'ES256'),
    CONSTRAINT checkpoint_signature_ck CHECK (octet_length(signature) BETWEEN 1 AND 512)
);

COMMENT ON TABLE audit.checkpoint IS 'Signed audit hash-chain checkpoints (ADR-013 §3.4, E14-T03); append-only.';

CREATE TRIGGER checkpoint_before_update BEFORE UPDATE ON audit.checkpoint
    FOR EACH ROW EXECUTE FUNCTION audit.reject_chain_record_change();
CREATE TRIGGER checkpoint_before_delete BEFORE DELETE ON audit.checkpoint
    FOR EACH ROW EXECUTE FUNCTION audit.reject_chain_record_change();
CREATE TRIGGER checkpoint_before_truncate BEFORE TRUNCATE ON audit.checkpoint
    FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_chain_record_change();

-- ---------------------------------------------------------------------------------------------------------------
-- Purge records: drop_expired_partition removes whole months, which are runs of positions in each chain. Each run
-- keeps the hash of its last event, so the first surviving event still links, and the purge's own Audit.Purged event
-- (system chain, sealed and checkpointed later) carries a digest of the runs, so a forged run is detected.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE audit.chain_gap (
    chain_id            uuid        NOT NULL,
    first_sequence      bigint      NOT NULL,
    last_sequence       bigint      NOT NULL,
    last_event_hash     bytea       NOT NULL,
    partition_name      text        NOT NULL,
    purged_at           timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT chain_gap_pk PRIMARY KEY (chain_id, first_sequence),
    CONSTRAINT chain_gap_range_ck CHECK (first_sequence >= 1 AND last_sequence >= first_sequence),
    CONSTRAINT chain_gap_hash_ck CHECK (octet_length(last_event_hash) = 32)
);

COMMENT ON TABLE audit.chain_gap IS 'Chain positions removed by the audit retention purge (E14-T03); written only by drop_expired_partition.';

CREATE INDEX chain_gap_partition_ix ON audit.chain_gap (partition_name);

CREATE TRIGGER chain_gap_before_update BEFORE UPDATE ON audit.chain_gap
    FOR EACH ROW EXECUTE FUNCTION audit.reject_chain_record_change();
CREATE TRIGGER chain_gap_before_delete BEFORE DELETE ON audit.chain_gap
    FOR EACH ROW EXECUTE FUNCTION audit.reject_chain_record_change();
CREATE TRIGGER chain_gap_before_truncate BEFORE TRUNCATE ON audit.chain_gap
    FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_chain_record_change();

-- ---------------------------------------------------------------------------------------------------------------
-- Privileges. The sealer moves heads and appends checkpoints; the retention role reads; the application role reads
-- the checkpoints of its own chain (RLS, like audit_event) so a later API can export them for exhibits.
-- ---------------------------------------------------------------------------------------------------------------
REVOKE ALL ON audit.chain_head, audit.checkpoint, audit.chain_gap FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON audit.chain_head TO opportunity_audit_sealer;
GRANT SELECT, INSERT ON audit.checkpoint TO opportunity_audit_sealer;
GRANT SELECT ON audit.chain_gap TO opportunity_audit_sealer;
GRANT SELECT ON audit.chain_head, audit.checkpoint, audit.chain_gap TO opportunity_audit_retention;
GRANT SELECT ON audit.checkpoint TO opportunity_app;

ALTER TABLE audit.checkpoint ENABLE ROW LEVEL SECURITY;
CREATE POLICY checkpoint_scope_read ON audit.checkpoint
    AS PERMISSIVE FOR SELECT TO PUBLIC
    USING (workspace_id IS NOT DISTINCT FROM NULLIF(current_setting('app.workspace_id', true), '')::uuid);
CREATE POLICY checkpoint_chain_read ON audit.checkpoint
    AS PERMISSIVE FOR SELECT TO opportunity_audit_sealer, opportunity_audit_retention
    USING (true);
CREATE POLICY checkpoint_chain_append ON audit.checkpoint
    AS PERMISSIVE FOR INSERT TO opportunity_audit_sealer
    WITH CHECK (true);

-- ---------------------------------------------------------------------------------------------------------------
-- The audit purge (V0048 with the chain rules added). Before a month leaves, every event in it must be sealed and
-- covered by a signed checkpoint of its chain (ADR-013 §3.4: a checkpoint is taken before an audit purge; the
-- retention job runs `audit seal --checkpoint --reason before-purge` first). The removed runs go to audit.chain_gap,
-- and their digest into the Audit.Purged event.
-- ---------------------------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit.drop_expired_partition(p_month date)
    RETURNS bigint
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    month_start date := date_trunc('month', p_month)::date;
    partition   text := 'audit_event_p' || to_char(date_trunc('month', p_month), 'YYYYMM');
    month_end   timestamptz;
    years       integer;
    cutoff      timestamptz;
    blocking    uuid;
    row_count   bigint;
    unsealed    bigint;
    uncovered   text;
    gap_digest  text;
    chains      bigint;
BEGIN
    IF to_regclass('audit.' || partition) IS NULL
        OR NOT EXISTS (SELECT FROM pg_inherits WHERE inhrelid = ('audit.' || partition)::regclass
                                                 AND inhparent = 'audit.audit_event'::regclass) THEN
        RAISE EXCEPTION 'audit: no audit partition for %', to_char(month_start, 'YYYY-MM') USING ERRCODE = 'undefined_table';
    END IF;

    SELECT years_after_close INTO STRICT years FROM audit.retention_policy;
    cutoff := now() - make_interval(years => years);
    month_end := ((month_start + interval '1 month')::date)::timestamp AT TIME ZONE 'UTC';
    IF month_end > cutoff THEN
        RAISE EXCEPTION 'audit: partition % is within the % year retention period', partition, years
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    EXECUTE format(
        'SELECT e.workspace_id FROM audit.%I e JOIN opportunity.workspace w ON w.workspace_id = e.workspace_id '
        || 'WHERE w.active_preservation_locks > 0 LIMIT 1', partition)
        INTO blocking;
    IF blocking IS NOT NULL THEN
        RAISE EXCEPTION 'audit: partition % holds events of workspace %, which is under a preservation lock (legal hold)',
            partition, blocking USING ERRCODE = 'O0423', DETAIL = blocking::text, HINT = 'audit_event';
    END IF;

    EXECUTE format(
        'SELECT e.workspace_id FROM audit.%I e JOIN opportunity.workspace w ON w.workspace_id = e.workspace_id '
        || 'WHERE w.status = ''Active'' OR w.closed_at > $1 LIMIT 1', partition)
        INTO blocking USING cutoff;
    IF blocking IS NOT NULL THEN
        RAISE EXCEPTION 'audit: partition % holds events of workspace %, which is open or closed less than % years ago',
            partition, blocking, years USING ERRCODE = 'insufficient_privilege';
    END IF;

    -- E14-T03: nothing leaves the database before the chain and a signed checkpoint cover it.
    EXECUTE format('SELECT count(*) FROM audit.%I WHERE sequence IS NULL', partition) INTO unsealed;
    IF unsealed > 0 THEN
        RAISE EXCEPTION 'audit: partition % holds % event(s) not yet sealed into their hash chain', partition, unsealed
            USING ERRCODE = 'object_not_in_prerequisite_state', HINT = 'Run `audit seal --checkpoint --reason before-purge` first.';
    END IF;

    EXECUTE format(
        'SELECT coalesce(e.workspace_id::text, ''system'') FROM audit.%I e '
        || 'WHERE e.sequence > coalesce((SELECT max(c.sequence) FROM audit.checkpoint c '
        || '  WHERE c.chain_id = coalesce(e.workspace_id, ''00000000-0000-0000-0000-000000000000''::uuid)), 0) LIMIT 1', partition)
        INTO uncovered;
    IF uncovered IS NOT NULL THEN
        RAISE EXCEPTION 'audit: partition % holds events of chain % that no signed checkpoint covers yet', partition, uncovered
            USING ERRCODE = 'object_not_in_prerequisite_state', HINT = 'Run `audit seal --checkpoint --reason before-purge` first.';
    END IF;

    EXECUTE format('SELECT count(*) FROM audit.%I', partition) INTO row_count;

    -- Runs of consecutive positions per chain (gaps and islands), each with the hash of its last event.
    EXECUTE format(
        'INSERT INTO audit.chain_gap (chain_id, first_sequence, last_sequence, last_event_hash, partition_name) '
        || 'SELECT chain_id, min(sequence), max(sequence), (array_agg(event_hash ORDER BY sequence DESC))[1], %L '
        || 'FROM (SELECT coalesce(workspace_id, ''00000000-0000-0000-0000-000000000000''::uuid) AS chain_id, sequence, event_hash, '
        || '             sequence - row_number() OVER (PARTITION BY workspace_id ORDER BY sequence) AS run '
        || '      FROM audit.%I) s '
        || 'GROUP BY chain_id, run', partition, partition);

    SELECT encode(sha256(convert_to(coalesce(string_agg(
               format('%s:%s:%s:%s', g.chain_id, g.first_sequence, g.last_sequence, encode(g.last_event_hash, 'hex')),
               E'\n' ORDER BY g.chain_id::text COLLATE "C", g.first_sequence), ''), 'UTF8')), 'hex'),
           count(DISTINCT g.chain_id)
    INTO gap_digest, chains
    FROM audit.chain_gap g
    WHERE g.partition_name = partition;

    INSERT INTO audit.audit_event (event_id, schema_version, workspace_id, occurred_at, category, action, actor_type,
                                   actor_id, actor_display, resource_type, resource_id, outcome, correlation_id, details)
    VALUES (gen_random_uuid(), 1, NULL, now(), 'Audit', 'Purged', 'Service', session_user, session_user,
            'AuditPartition', partition, 'Success', gen_random_uuid()::text,
            jsonb_build_object('Partition', partition, 'Events', row_count::text, 'RetentionYears', years::text,
                               'Chains', chains::text, 'GapDigest', gap_digest));

    EXECUTE format('DROP TABLE audit.%I', partition);
    RETURN row_count;
END
$$;

COMMENT ON FUNCTION audit.drop_expired_partition(date) IS
    '@security-definer the only removal path for audit (ADR-014): drops one sealed, checkpointed monthly partition past retention; retention role only.';
REVOKE EXECUTE ON FUNCTION audit.drop_expired_partition(date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION audit.drop_expired_partition(date) TO opportunity_audit_retention;
