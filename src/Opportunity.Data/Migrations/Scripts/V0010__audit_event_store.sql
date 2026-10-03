-- V0010: append-only, monthly-partitioned audit store (E14-T01). Binding: ADR-013 §1-§4 (store, write path, reserved
-- tamper-evidence columns, envelope), ADR-014 (retention), Q-16 (full search text, retained for the life of the matter
-- plus 7 years by default), Q-17 (hash chain and checkpoints in M3, E14-T03).
--
-- Schema `audit` is separate from `opportunity` on purpose: installation-level events (WorkspaceId null, the system
-- chain) share the table with workspace events, so the tenant-key primary-key rule cannot apply, and the table has its
-- own privilege model:
--   opportunity_app              SELECT, INSERT                       (no UPDATE, DELETE or TRUNCATE)
--   opportunity_audit_sealer     SELECT, UPDATE of the reserved chain columns only, null -> value once (E14-T03)
--   opportunity_audit_retention  SELECT, and EXECUTE on audit.drop_expired_partition (the only way rows go away)
--   opportunity_readonly         nothing: audit holds search text, readable only with Audit.ReadSearchText (Q-16)
-- Triggers back the privileges up for every role, the owner included: rows are never updated outside the sealer rule
-- and never deleted or truncated; a partition leaves only through drop_expired_partition.

-- ---------------------------------------------------------------------------------------------------------------
-- Roles (cluster-wide NOLOGIN group roles, like V0001). Deployments grant them to the sealer / retention logins.
-- ---------------------------------------------------------------------------------------------------------------
DO $$
DECLARE
    role_name text;
BEGIN
    FOREACH role_name IN ARRAY ARRAY['opportunity_audit_sealer', 'opportunity_audit_retention'] LOOP
        IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = role_name) THEN
            BEGIN
                EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', role_name);
            EXCEPTION WHEN duplicate_object OR unique_violation THEN
                NULL; -- created concurrently by a migrator for another database in the same cluster
            END;
        END IF;
    END LOOP;

    EXECUTE format('GRANT CONNECT ON DATABASE %I TO opportunity_audit_sealer, opportunity_audit_retention', current_database());
END
$$;

CREATE SCHEMA audit;
REVOKE ALL ON SCHEMA audit FROM PUBLIC;
GRANT USAGE ON SCHEMA audit TO opportunity_app, opportunity_audit_sealer, opportunity_audit_retention;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- ---------------------------------------------------------------------------------------------------------------
-- The closed taxonomy (ADR-013 §5). Unknown Category/Action pairs are rejected by the foreign key. A reference table
-- rather than a CHECK list: later tickets add actions with an INSERT instead of re-validating every audit partition.
-- Opportunity.Application.Audit.AuditTaxonomy mirrors this list (contract test).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE audit.audit_action (
    category    text NOT NULL,
    action      text NOT NULL,
    CONSTRAINT audit_action_pk PRIMARY KEY (category, action)
);

COMMENT ON TABLE audit.audit_action IS 'Closed audit taxonomy (ADR-013 §5); changed only by migrations.';

INSERT INTO audit.audit_action (category, action)
SELECT c.category, a.action
FROM (VALUES
    ('Auth',       ARRAY['SignIn', 'SignInFailed', 'SignOut', 'SessionExpired', 'SessionRevoked', 'StepUp']),
    ('AuthZ',      ARRAY['Denied']),
    ('Document',   ARRAY['Retrieved', 'Viewed', 'NativeDownloaded', 'Printed', 'TextDownloaded']),
    ('Search',     ARRAY['Executed', 'ResultsPageServed', 'CountExact', 'TermReportGenerated',
                         'SavedSearch.Created', 'SavedSearch.Modified', 'SavedSearch.Deleted']),
    ('Coding',     ARRAY['Changed', 'FamilyApplied', 'BulkSubmitted', 'BulkChunkApplied', 'BulkCompleted', 'OverlayEnabled']),
    ('Privilege',  ARRAY['LogGenerated', 'ConflictOverride', 'ClawbackRecorded']),
    ('Redaction',  ARRAY['Added', 'Modified', 'Removed']),
    ('Export',     ARRAY['Created', 'Completed', 'DocumentsExcluded', 'Downloaded']),
    ('Production', ARRAY['Created', 'SpecFrozen', 'Run', 'VerificationFailed', 'QcOverride', 'Finalized', 'Voided',
                         'Downloaded', 'Rerun']),
    ('Import',     ARRAY['Started', 'Completed', 'MalwareDetected', 'HashMismatch']),
    ('Security',   ARRAY['RoleAssigned', 'RoleRevoked', 'PermissionChanged', 'RestrictionChanged', 'WallCreated',
                         'WallChanged', 'WallDeleted', 'WallMemberAdded', 'WallMemberRemoved', 'BreakGlassActivated',
                         'BreakGlassEnded', 'AcknowledgmentAccepted']),
    ('Workspace',  ARRAY['Created', 'SettingsChanged', 'Closed', 'Reopened', 'HoldPlaced', 'HoldReleaseRequested',
                         'HoldReleased', 'DeletionRequested', 'DeletionApproved', 'DeletionCancelled', 'DeletionStarted',
                         'DeletionStepCompleted', 'DeletionHalted', 'Deleted']),
    ('Admin',      ARRAY['ConfigChanged', 'UserProvisioned', 'UserDeactivated', 'KeyCreated', 'KeyRotated',
                         'KeyDestroyed', 'SecretRotated']),
    ('Audit',      ARRAY['Queried', 'Exported', 'CheckpointCreated', 'Verified', 'Purged']),
    ('Job',        ARRAY['Created', 'Cancelled', 'Failed', 'CompletedWithErrors', 'Replayed']),
    ('Integrity',  ARRAY['HashMismatch', 'ChainBroken', 'FenceViolation', 'EnvelopeMismatch', 'BatesConflict'])
) AS c(category, actions)
CROSS JOIN LATERAL unnest(c.actions) AS a(action);

-- ---------------------------------------------------------------------------------------------------------------
-- Installation retention setting (Q-16, ADR-014): audit is kept for the life of the matter plus this many years after
-- the workspace closes. Changed by the installation owner only (not by any application role).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE audit.retention_policy (
    singleton           boolean     NOT NULL DEFAULT true,
    years_after_close   integer     NOT NULL DEFAULT 7,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT retention_policy_pk PRIMARY KEY (singleton),
    CONSTRAINT retention_policy_singleton_ck CHECK (singleton),
    CONSTRAINT retention_policy_years_ck CHECK (years_after_close BETWEEN 1 AND 100)
);

COMMENT ON TABLE audit.retention_policy IS 'Audit retention after matter close (Q-16; default 7 years), one row per installation.';

INSERT INTO audit.retention_policy DEFAULT VALUES;

-- ---------------------------------------------------------------------------------------------------------------
-- The event table (ADR-013 §4 envelope), range-partitioned by UTC month of occurred_at.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE audit.audit_event (
    event_id            uuid        NOT NULL,
    schema_version      smallint    NOT NULL,
    workspace_id        uuid        NULL,
    sequence            bigint      NULL,
    occurred_at         timestamptz NOT NULL,
    recorded_at         timestamptz NOT NULL DEFAULT clock_timestamp(),
    category            text        NOT NULL,
    action              text        NOT NULL,
    actor_type          text        NOT NULL,
    actor_id            text        NOT NULL,
    actor_display       text        NOT NULL,
    on_behalf_of        uuid        NULL,
    access_path         text        NOT NULL DEFAULT 'Normal',
    client_ip           inet        NULL,
    user_agent          text        NULL,
    session_id_hash     bytea       NULL,
    resource_type       text        NULL,
    resource_id         text        NULL,
    outcome             text        NOT NULL,
    reason_code         text        NULL,
    correlation_id      text        NOT NULL,
    causation_id        text        NULL,
    job_id              uuid        NULL,
    chunk_sequence      integer     NULL,
    snapshot_id         uuid        NULL,
    search_generation   bigint      NULL,
    details             jsonb       NOT NULL DEFAULT '{}',
    restricted_details  jsonb       NULL,
    prev_hash           bytea       NULL,
    event_hash          bytea       NULL,
    sealed_at           timestamptz NULL,
    CONSTRAINT audit_event_pk PRIMARY KEY (event_id, occurred_at),
    CONSTRAINT audit_event_action_fk FOREIGN KEY (category, action) REFERENCES audit.audit_action (category, action),
    CONSTRAINT audit_event_schema_version_ck CHECK (schema_version >= 1),
    CONSTRAINT audit_event_actor_type_ck CHECK (actor_type IN ('User', 'Service', 'System')),
    CONSTRAINT audit_event_actor_id_ck CHECK (length(actor_id) BETWEEN 1 AND 256),
    CONSTRAINT audit_event_actor_display_ck CHECK (length(actor_display) BETWEEN 1 AND 512),
    CONSTRAINT audit_event_access_path_ck CHECK (access_path IN ('Normal', 'BreakGlass', 'Impersonation')),
    CONSTRAINT audit_event_user_agent_ck CHECK (length(user_agent) <= 512),
    CONSTRAINT audit_event_session_hash_ck CHECK (octet_length(session_id_hash) = 32),
    CONSTRAINT audit_event_resource_ck CHECK (length(resource_type) <= 64 AND length(resource_id) <= 256),
    CONSTRAINT audit_event_outcome_ck CHECK (outcome IN ('Success', 'Denied', 'Failure')),
    CONSTRAINT audit_event_reason_ck CHECK (CASE WHEN reason_code IS NULL THEN outcome = 'Success' ELSE length(reason_code) BETWEEN 1 AND 128 END),
    CONSTRAINT audit_event_correlation_ck CHECK (length(correlation_id) BETWEEN 1 AND 128 AND length(causation_id) <= 128),
    CONSTRAINT audit_event_chunk_ck CHECK (chunk_sequence IS NULL OR job_id IS NOT NULL),
    CONSTRAINT audit_event_details_ck CHECK (jsonb_typeof(details) = 'object' AND octet_length(details::text) <= 8192),
    -- Q-16: only Search.* carries the query text and AST, readable with Audit.ReadSearchText only.
    CONSTRAINT audit_event_restricted_ck CHECK (restricted_details IS NULL
        OR (category = 'Search' AND jsonb_typeof(restricted_details) = 'object' AND octet_length(restricted_details::text) <= 65536)),
    CONSTRAINT audit_event_hash_ck CHECK (octet_length(prev_hash) = 32 AND octet_length(event_hash) = 32)
) PARTITION BY RANGE (occurred_at);

COMMENT ON TABLE audit.audit_event IS
    'Append-only audit (ADR-013). Monthly partitions; chain columns reserved for the M3 sealer; drop via audit.drop_expired_partition only.';

CREATE INDEX audit_event_workspace_ix ON audit.audit_event (workspace_id, occurred_at);
CREATE INDEX audit_event_resource_ix ON audit.audit_event (workspace_id, resource_type, resource_id, occurred_at);
CREATE INDEX audit_event_actor_ix ON audit.audit_event (actor_id, occurred_at);
CREATE INDEX audit_event_correlation_ix ON audit.audit_event (correlation_id);

-- ---------------------------------------------------------------------------------------------------------------
-- Append-only enforcement. Privileges stop the application roles; these triggers also bind the owner and any role
-- that is granted more later. Disabling them needs table ownership, which no runtime role has.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION audit.audit_event_before_insert()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF NEW.sequence IS NOT NULL OR NEW.prev_hash IS NOT NULL OR NEW.event_hash IS NOT NULL OR NEW.sealed_at IS NOT NULL THEN
        RAISE EXCEPTION 'audit: the chain columns are assigned by the sealer, not on insert' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.occurred_at > clock_timestamp() + interval '5 minutes' THEN
        RAISE EXCEPTION 'audit: occurred_at % is in the future', NEW.occurred_at USING ERRCODE = 'check_violation';
    END IF;
    NEW.recorded_at := clock_timestamp();
    RETURN NEW;
END
$$;

CREATE FUNCTION audit.audit_event_before_update()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    reserved constant text[] := ARRAY['sequence', 'prev_hash', 'event_hash', 'sealed_at'];
BEGIN
    IF (to_jsonb(NEW) - reserved) IS DISTINCT FROM (to_jsonb(OLD) - reserved) THEN
        RAISE EXCEPTION 'audit: events are append-only; only the reserved chain columns may be set' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (OLD.sequence IS NOT NULL AND NEW.sequence IS DISTINCT FROM OLD.sequence)
        OR (OLD.prev_hash IS NOT NULL AND NEW.prev_hash IS DISTINCT FROM OLD.prev_hash)
        OR (OLD.event_hash IS NOT NULL AND NEW.event_hash IS DISTINCT FROM OLD.event_hash)
        OR (OLD.sealed_at IS NOT NULL AND NEW.sealed_at IS DISTINCT FROM OLD.sealed_at) THEN
        RAISE EXCEPTION 'audit: a sealed chain column cannot change' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END
$$;

CREATE FUNCTION audit.audit_event_reject_removal()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'audit: events are append-only (%); partitions leave only through audit.drop_expired_partition', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END
$$;

CREATE TRIGGER audit_event_before_insert BEFORE INSERT ON audit.audit_event
    FOR EACH ROW EXECUTE FUNCTION audit.audit_event_before_insert();
CREATE TRIGGER audit_event_before_update BEFORE UPDATE ON audit.audit_event
    FOR EACH ROW EXECUTE FUNCTION audit.audit_event_before_update();
CREATE TRIGGER audit_event_before_delete BEFORE DELETE ON audit.audit_event
    FOR EACH ROW EXECUTE FUNCTION audit.audit_event_reject_removal();
CREATE TRIGGER audit_event_before_truncate BEFORE TRUNCATE ON audit.audit_event
    FOR EACH STATEMENT EXECUTE FUNCTION audit.audit_event_reject_removal();

-- ---------------------------------------------------------------------------------------------------------------
-- Privileges and row-level security. A workspace transaction (app.workspace_id set) reads and writes only its
-- workspace's events; an installation transaction (no context) only the system chain (workspace_id null). RLS is
-- enabled but not forced: the owner (migrator login, and the definer functions below) is trusted with every chain.
-- ---------------------------------------------------------------------------------------------------------------
REVOKE ALL ON audit.audit_event, audit.audit_action, audit.retention_policy FROM PUBLIC;
GRANT SELECT, INSERT ON audit.audit_event TO opportunity_app;
GRANT SELECT, UPDATE (sequence, prev_hash, event_hash, sealed_at) ON audit.audit_event TO opportunity_audit_sealer;
GRANT SELECT ON audit.audit_event TO opportunity_audit_retention;
GRANT SELECT ON audit.audit_action, audit.retention_policy
    TO opportunity_app, opportunity_audit_sealer, opportunity_audit_retention;

ALTER TABLE audit.audit_event ENABLE ROW LEVEL SECURITY;

CREATE POLICY audit_scope_read ON audit.audit_event
    AS PERMISSIVE FOR SELECT TO PUBLIC
    USING (workspace_id IS NOT DISTINCT FROM NULLIF(current_setting('app.workspace_id', true), '')::uuid);
CREATE POLICY audit_scope_insert ON audit.audit_event
    AS PERMISSIVE FOR INSERT TO PUBLIC
    WITH CHECK (workspace_id IS NOT DISTINCT FROM NULLIF(current_setting('app.workspace_id', true), '')::uuid);
-- The sealer chains every workspace and the system chain; retention reports on what it may purge.
CREATE POLICY audit_chain_read ON audit.audit_event
    AS PERMISSIVE FOR SELECT TO opportunity_audit_sealer, opportunity_audit_retention
    USING (true);
CREATE POLICY audit_chain_seal ON audit.audit_event
    AS PERMISSIVE FOR UPDATE TO opportunity_audit_sealer
    USING (true) WITH CHECK (true);

-- ---------------------------------------------------------------------------------------------------------------
-- Partitions, created ahead by AuditPartitionMaintenanceService (ADR-013 §1.1: at least 3 months). Without a partition
-- an insert fails, so the action fails with it: an event is never dropped. No default partition, so a late partition
-- can always be created. Runtime roles hold no privilege on any child.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION audit.ensure_partitions(p_through timestamptz)
    RETURNS integer
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    month_start date := (date_trunc('month', now() AT TIME ZONE 'UTC') - interval '1 month')::date;
    last_month  date := date_trunc('month', least(p_through, now() + interval '5 years') AT TIME ZONE 'UTC')::date;
    partition   text;
    created     integer := 0;
BEGIN
    -- Concurrent maintainers (API and dispatcher replicas) serialize here instead of racing on CREATE TABLE.
    PERFORM pg_advisory_xact_lock(hashtextextended('audit.audit_event partitions', 0));
    WHILE month_start <= last_month LOOP
        partition := 'audit_event_p' || to_char(month_start, 'YYYYMM');
        IF to_regclass('audit.' || partition) IS NULL THEN
            EXECUTE format(
                'CREATE TABLE audit.%I PARTITION OF audit.audit_event FOR VALUES FROM (%L) TO (%L)',
                partition,
                to_char(month_start, 'YYYY-MM-DD') || ' 00:00:00+00',
                to_char((month_start + interval '1 month')::date, 'YYYY-MM-DD') || ' 00:00:00+00');
            EXECUTE format('REVOKE ALL ON audit.%I FROM PUBLIC, opportunity_app, opportunity_readonly, '
                || 'opportunity_audit_sealer, opportunity_audit_retention', partition);
            EXECUTE format('ALTER TABLE audit.%I ENABLE ROW LEVEL SECURITY', partition);
            created := created + 1;
        END IF;
        month_start := (month_start + interval '1 month')::date;
    END LOOP;
    RETURN created;
END
$$;

COMMENT ON FUNCTION audit.ensure_partitions(timestamptz) IS
    '@security-definer creates audit_event partitions ahead (capped at 5 years) for the maintenance service; DDL only.';
GRANT EXECUTE ON FUNCTION audit.ensure_partitions(timestamptz) TO opportunity_app;

-- First UTC month without a partition after the newest one (the horizon the maintenance service watches); null if none.
CREATE FUNCTION audit.partition_horizon()
    RETURNS date
    LANGUAGE sql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT (max(to_date(substring(c.relname FROM 'audit_event_p(\d{6})$'), 'YYYYMM')) + interval '1 month')::date
    FROM pg_inherits i
    JOIN pg_class c ON c.oid = i.inhrelid
    WHERE i.inhparent = 'audit.audit_event'::regclass
$$;

GRANT EXECUTE ON FUNCTION audit.partition_horizon() TO opportunity_app, opportunity_audit_retention;

SELECT audit.ensure_partitions(now() + interval '3 months');

-- ---------------------------------------------------------------------------------------------------------------
-- Retention (ADR-014, Q-16, Q-23). The only path by which audit leaves the database: the retention role drops one
-- whole monthly partition, and only when every event in it is past retention: the month ended more than the installation
-- period ago, and every workspace with events in it is closed (not Active) since longer than that period. Workspaces
-- unknown to the registry (e.g. a denied request for a non-existent id) follow the age rule. The purge itself is
-- recorded as Audit.Purged on the system chain in the same transaction. Two-person approval of a deletion (Q-23) is
-- the job's concern (ADR-014 §6); this function is the database guard underneath it.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION audit.drop_expired_partition(p_month date)
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
        || 'WHERE w.status = ''Active'' OR w.closed_at > $1 LIMIT 1', partition)
        INTO blocking USING cutoff;
    IF blocking IS NOT NULL THEN
        RAISE EXCEPTION 'audit: partition % holds events of workspace %, which is open or closed less than % years ago',
            partition, blocking, years USING ERRCODE = 'insufficient_privilege';
    END IF;

    EXECUTE format('SELECT count(*) FROM audit.%I', partition) INTO row_count;

    INSERT INTO audit.audit_event (event_id, schema_version, workspace_id, occurred_at, category, action, actor_type,
                                   actor_id, actor_display, resource_type, resource_id, outcome, correlation_id, details)
    VALUES (gen_random_uuid(), 1, NULL, now(), 'Audit', 'Purged', 'Service', session_user, session_user,
            'AuditPartition', partition, 'Success', gen_random_uuid()::text,
            jsonb_build_object('Partition', partition, 'Events', row_count::text, 'RetentionYears', years::text));

    EXECUTE format('DROP TABLE audit.%I', partition);
    RETURN row_count;
END
$$;

COMMENT ON FUNCTION audit.drop_expired_partition(date) IS
    '@security-definer the only removal path for audit (ADR-014): drops one monthly partition past retention; retention role only.';
REVOKE EXECUTE ON FUNCTION audit.drop_expired_partition(date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION audit.drop_expired_partition(date) TO opportunity_audit_retention;
