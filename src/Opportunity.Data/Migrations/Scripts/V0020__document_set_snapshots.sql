-- V0020: materialized DocumentSetSnapshot (E10-T02, #90).
-- Binding: ADR-002 §5 (materialization: stage → freeze in one REPEATABLE READ transaction → Ready; immutable once
-- Ready), §6 interim choice (b) PG member pages of 1,000 members with SHA-256 per page and a root hash, §7
-- (SearchGeneration = watermark read before the reader opened, a lower bound), §9 retention; ADR-010 §8 (BaselineVersion
-- per member for the Q-07 skip rule); ADR-005 R1/R3 (snapshot membership is not partitioned; partition-ready keys);
-- ADR-015 D7 (forced RLS) and D5.8 (a snapshot is a candidate set, never a grant).
--
-- Lifecycle: the API inserts the header in Materializing; candidates are staged (search pages, explicit IDs or another
-- snapshot); the freeze transaction orders the members, drops those the creator may not access, writes the pages and
-- flips the header to Ready. Pages can be inserted only while the header is Materializing and deleted only once it is
-- Failed or Expired; they are never updated. The header keeps its counts and hashes forever (Expired is a tombstone).

CREATE TABLE opportunity.document_set_snapshot (
    workspace_id            uuid        NOT NULL,
    snapshot_id             uuid        NOT NULL,
    status                  text        NOT NULL DEFAULT 'Materializing',
    status_reason           text        NULL,
    -- "Frozen set" name shown in the browser pane and job pages (ticket review E10-T02).
    name                    text        NOT NULL,
    purpose                 text        NOT NULL,
    source_kind             text        NOT NULL,
    -- Query sources: {"text", "normalized", "astVersion"} (the full text is also audited, Q-16).
    query_definition        jsonb       NULL,
    source_snapshot_id      uuid        NULL,
    requested_count         integer     NULL,
    materialization_strategy text       NOT NULL DEFAULT 'PgMemberPages',
    page_size               integer     NOT NULL DEFAULT 1000,

    -- Provenance of the selection (ADR-002 §7): applied watermark read before the reader opened, and the projection
    -- generation of the index it was read from. Null for sources that never touch the index.
    search_generation       bigint      NULL,
    projection_generation   integer     NULL,
    selected_while_indexing boolean     NULL,
    selected_at             timestamptz NULL,

    -- Frozen counts and hashes, set by the freeze transaction.
    document_count          bigint      NULL,
    candidate_count         bigint      NULL,
    excluded_no_access      bigint      NULL,
    excluded_missing        bigint      NULL,
    -- Members per inclusion reason, e.g. {"Hit": 812}.
    inclusion_counts        jsonb       NULL,
    page_count              integer     NULL,
    root_sha256             bytea       NULL,
    materialized_at         timestamptz NULL,

    -- Execution identity of the creator: the background materializer authorizes members as this principal
    -- (ADR-015 D9.4); groups are the IdP group snapshot of the creating request.
    created_by              uuid        NOT NULL,
    created_by_display      text        NOT NULL,
    created_by_groups       text[]      NOT NULL DEFAULT '{}',
    correlation_id          text        NULL,
    client_idempotency_key  text        NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),

    -- Materialization claim (one materializer at a time; taken over once the lease expires).
    claimed_by              text        NULL,
    claimed_until           timestamptz NULL,
    attempt_count           smallint    NOT NULL DEFAULT 0,

    expired_at              timestamptz NULL,

    CONSTRAINT document_set_snapshot_pk PRIMARY KEY (workspace_id, snapshot_id),
    CONSTRAINT document_set_snapshot_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT document_set_snapshot_source_fk FOREIGN KEY (workspace_id, source_snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT document_set_snapshot_status_ck CHECK (status IN ('Materializing', 'Ready', 'Failed', 'Expired')),
    CONSTRAINT document_set_snapshot_status_reason_ck CHECK (length(status_reason) <= 2000),
    CONSTRAINT document_set_snapshot_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT document_set_snapshot_purpose_ck CHECK (purpose IN ('BulkCoding', 'Export', 'Production', 'Report')),
    CONSTRAINT document_set_snapshot_source_ck CHECK (CASE source_kind
        WHEN 'Query' THEN query_definition IS NOT NULL AND source_snapshot_id IS NULL
        WHEN 'DocumentIds' THEN query_definition IS NULL AND source_snapshot_id IS NULL AND requested_count >= 0
        WHEN 'Snapshot' THEN query_definition IS NULL AND source_snapshot_id IS NOT NULL
        ELSE false END),
    CONSTRAINT document_set_snapshot_query_ck CHECK (jsonb_typeof(query_definition) = 'object'
        AND octet_length(query_definition::text) <= 65536),
    CONSTRAINT document_set_snapshot_strategy_ck CHECK (materialization_strategy IN ('PgMemberPages')),
    CONSTRAINT document_set_snapshot_page_size_ck CHECK (page_size BETWEEN 1 AND 10000),
    CONSTRAINT document_set_snapshot_ready_ck CHECK (status NOT IN ('Ready', 'Expired') OR (
        document_count >= 0 AND candidate_count >= document_count AND excluded_no_access >= 0 AND excluded_missing >= 0
        AND page_count >= 0 AND octet_length(root_sha256) = 32 AND materialized_at IS NOT NULL
        AND jsonb_typeof(inclusion_counts) = 'object')),
    CONSTRAINT document_set_snapshot_claim_ck CHECK ((claimed_by IS NULL) = (claimed_until IS NULL) AND length(claimed_by) <= 200),
    CONSTRAINT document_set_snapshot_attempt_ck CHECK (attempt_count BETWEEN 0 AND 100),
    CONSTRAINT document_set_snapshot_expired_ck CHECK ((status = 'Expired') = (expired_at IS NOT NULL)),
    CONSTRAINT document_set_snapshot_display_ck CHECK (length(created_by_display) <= 256),
    CONSTRAINT document_set_snapshot_correlation_ck CHECK (length(correlation_id) <= 128),
    CONSTRAINT document_set_snapshot_client_key_ck CHECK (length(client_idempotency_key) BETWEEN 1 AND 128)
) WITH (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- Creation is idempotent per (workspace, creator, Idempotency-Key), like jobs.
CREATE UNIQUE INDEX document_set_snapshot_client_key_uq
    ON opportunity.document_set_snapshot (workspace_id, created_by, client_idempotency_key) WHERE client_idempotency_key IS NOT NULL;
-- "Frozen sets" lists, newest first; the materializer's queue; retention.
CREATE INDEX document_set_snapshot_created_ix ON opportunity.document_set_snapshot (workspace_id, created_at DESC, snapshot_id);
CREATE INDEX document_set_snapshot_pending_ix ON opportunity.document_set_snapshot (workspace_id, created_at)
    WHERE status = 'Materializing';
CREATE INDEX document_set_snapshot_source_ix ON opportunity.document_set_snapshot (workspace_id, source_snapshot_id)
    WHERE source_snapshot_id IS NOT NULL;
-- Jobs referencing a snapshot (retention and the pin trigger below).
CREATE INDEX job_target_snapshot_ix ON opportunity.job (workspace_id, target_snapshot_id) WHERE target_snapshot_id IS NOT NULL;

SELECT opportunity.enable_workspace_rls('opportunity.document_set_snapshot');
-- Headers are permanent records (tombstones after expiry); removal belongs to workspace purge (ADR-014).
REVOKE DELETE, TRUNCATE ON opportunity.document_set_snapshot FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Membership pages (ADR-002 §6 (b)): members 1…N in order (FamilySortKey, FamilySequence, DocumentId), page_size per
-- page. sha256 = SHA-256 of the concatenated int8send(ordinal) || uuid_send(document_id) || int8send(baseline_version)
-- || int2send(reason) of the page (Opportunity.Core.Snapshots.SnapshotHashing).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.document_set_snapshot_page (
    workspace_id        uuid        NOT NULL,
    snapshot_id         uuid        NOT NULL,
    page_no             integer     NOT NULL,
    first_ordinal       bigint      NOT NULL,
    member_count        integer     NOT NULL,
    document_ids        uuid[]      NOT NULL,
    baseline_versions   bigint[]    NOT NULL,
    inclusion_reasons   smallint[]  NOT NULL,
    sha256              bytea       NOT NULL,
    CONSTRAINT document_set_snapshot_page_pk PRIMARY KEY (workspace_id, snapshot_id, page_no),
    CONSTRAINT document_set_snapshot_page_header_fk FOREIGN KEY (workspace_id, snapshot_id)
        REFERENCES opportunity.document_set_snapshot (workspace_id, snapshot_id),
    CONSTRAINT document_set_snapshot_page_no_ck CHECK (page_no >= 1 AND first_ordinal >= 1),
    CONSTRAINT document_set_snapshot_page_shape_ck CHECK (member_count >= 1
        AND cardinality(document_ids) = member_count
        AND cardinality(baseline_versions) = member_count
        AND cardinality(inclusion_reasons) = member_count),
    CONSTRAINT document_set_snapshot_page_sha256_ck CHECK (octet_length(sha256) = 32)
) WITH (autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

SELECT opportunity.enable_workspace_rls('opportunity.document_set_snapshot_page');
REVOKE UPDATE, TRUNCATE ON opportunity.document_set_snapshot_page FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Staging of candidates while a snapshot materializes (ADR-002 §5.1: unlogged; a crash restarts the selection). Rows
-- leave in the freeze transaction, or when a failed or restarted materialization is cleaned up.
-- ---------------------------------------------------------------------------------------------------------------
CREATE UNLOGGED TABLE opportunity.document_set_snapshot_stage (
    workspace_id        uuid        NOT NULL,
    snapshot_id         uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    inclusion_reason    smallint    NOT NULL,
    CONSTRAINT document_set_snapshot_stage_pk PRIMARY KEY (workspace_id, snapshot_id, document_id),
    CONSTRAINT document_set_snapshot_stage_reason_ck CHECK (inclusion_reason BETWEEN 1 AND 5)
);

SELECT opportunity.enable_workspace_rls('opportunity.document_set_snapshot_stage');

-- ---------------------------------------------------------------------------------------------------------------
-- Immutability (ADR-002 §5.5). Enforced in the database, not only by the repository.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.document_set_snapshot_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT ((OLD.status = NEW.status)
            OR (OLD.status = 'Materializing' AND NEW.status IN ('Ready', 'Failed'))
            OR (OLD.status IN ('Ready', 'Failed') AND NEW.status = 'Expired')) THEN
        RAISE EXCEPTION 'snapshot % cannot move from % to %', OLD.snapshot_id, OLD.status, NEW.status
            USING ERRCODE = 'check_violation';
    END IF;

    -- Identity and selection never change.
    IF (NEW.workspace_id, NEW.snapshot_id, NEW.name, NEW.purpose, NEW.source_kind, NEW.query_definition,
        NEW.source_snapshot_id, NEW.requested_count, NEW.materialization_strategy, NEW.page_size, NEW.created_by,
        NEW.created_by_display, NEW.created_by_groups, NEW.correlation_id, NEW.client_idempotency_key, NEW.created_at)
       IS DISTINCT FROM
       (OLD.workspace_id, OLD.snapshot_id, OLD.name, OLD.purpose, OLD.source_kind, OLD.query_definition,
        OLD.source_snapshot_id, OLD.requested_count, OLD.materialization_strategy, OLD.page_size, OLD.created_by,
        OLD.created_by_display, OLD.created_by_groups, OLD.correlation_id, OLD.client_idempotency_key, OLD.created_at) THEN
        RAISE EXCEPTION 'snapshot % identity and selection are immutable', OLD.snapshot_id USING ERRCODE = 'check_violation';
    END IF;

    -- Once frozen, the result and its provenance never change either.
    IF OLD.status <> 'Materializing'
       AND (NEW.search_generation, NEW.projection_generation, NEW.selected_while_indexing, NEW.selected_at,
            NEW.document_count, NEW.candidate_count, NEW.excluded_no_access, NEW.excluded_missing, NEW.inclusion_counts,
            NEW.page_count, NEW.root_sha256, NEW.materialized_at, NEW.status_reason, NEW.claimed_by, NEW.claimed_until,
            NEW.attempt_count)
           IS DISTINCT FROM
           (OLD.search_generation, OLD.projection_generation, OLD.selected_while_indexing, OLD.selected_at,
            OLD.document_count, OLD.candidate_count, OLD.excluded_no_access, OLD.excluded_missing, OLD.inclusion_counts,
            OLD.page_count, OLD.root_sha256, OLD.materialized_at, OLD.status_reason, OLD.claimed_by, OLD.claimed_until,
            OLD.attempt_count) THEN
        RAISE EXCEPTION 'snapshot % is frozen', OLD.snapshot_id USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END
$$;

CREATE TRIGGER document_set_snapshot_guard
    BEFORE UPDATE ON opportunity.document_set_snapshot
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.document_set_snapshot_guard();

CREATE FUNCTION opportunity.document_set_snapshot_page_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
DECLARE
    header_status text;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        RAISE EXCEPTION 'snapshot membership is immutable' USING ERRCODE = 'check_violation';
    END IF;

    SELECT s.status INTO header_status
      FROM opportunity.document_set_snapshot s
     WHERE s.workspace_id = CASE WHEN TG_OP = 'INSERT' THEN NEW.workspace_id ELSE OLD.workspace_id END
       AND s.snapshot_id = CASE WHEN TG_OP = 'INSERT' THEN NEW.snapshot_id ELSE OLD.snapshot_id END;

    IF TG_OP = 'INSERT' THEN
        IF header_status IS DISTINCT FROM 'Materializing' THEN
            RAISE EXCEPTION 'membership can be written only while the snapshot materializes (status %)', header_status
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;

    IF header_status IS NOT NULL AND header_status NOT IN ('Failed', 'Expired') THEN
        RAISE EXCEPTION 'membership of a % snapshot cannot be deleted', header_status USING ERRCODE = 'check_violation';
    END IF;
    RETURN OLD;
END
$$;

CREATE TRIGGER document_set_snapshot_page_guard
    BEFORE INSERT OR UPDATE OR DELETE ON opportunity.document_set_snapshot_page
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.document_set_snapshot_page_guard();

-- A job can target only a Ready snapshot of its workspace, and pins it against the retention sweep: the FOR SHARE lock
-- serializes with the sweep's status change (READ COMMITTED re-reads the row after waiting). Rows whose ID names no
-- snapshot are left alone: jobs created before V0019 (and test fixtures) carry IDs without headers.
CREATE FUNCTION opportunity.job_target_snapshot_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
DECLARE
    header_status text;
BEGIN
    IF NEW.target_snapshot_id IS NULL
       OR (TG_OP = 'UPDATE' AND NEW.target_snapshot_id IS NOT DISTINCT FROM OLD.target_snapshot_id) THEN
        RETURN NEW;
    END IF;

    SELECT s.status INTO header_status
      FROM opportunity.document_set_snapshot s
     WHERE s.workspace_id = NEW.workspace_id AND s.snapshot_id = NEW.target_snapshot_id
       FOR SHARE;

    IF FOUND AND header_status <> 'Ready' THEN
        RAISE EXCEPTION 'job target snapshot % is %', NEW.target_snapshot_id, header_status USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER job_target_snapshot_guard
    BEFORE INSERT OR UPDATE OF target_snapshot_id ON opportunity.job
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.job_target_snapshot_guard();
