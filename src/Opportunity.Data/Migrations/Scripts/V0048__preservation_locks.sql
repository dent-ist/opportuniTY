-- V0048: workspace preservation locks (legal holds) and their enforcement in the database (E20-T01, ADR-014 §2, Q-23).
--
-- preservation_lock: one row per hold; several may overlap (ADR-014 §1: a hold is a record, not a status). Placing a
--   hold takes effect at once; releasing one either happens at once or, when release_requires_approval, needs a release
--   request by one person and the approval of a different one. Rows are never deleted and only the release columns
--   change, once (preservation_lock_guard). placed_by, release_requested_by and release_approved_by reference the
--   installation-level app_user without a foreign key (ADR-005 P3).
-- workspace.active_preservation_locks: the number of unreleased holds, kept by a trigger on preservation_lock. It lives
--   on the read-open workspace registry so every guard can read it under any context, and every change to it updates the
--   workspace row, which serializes with the FOR SHARE lock each guard takes on that row.
--
-- Enforcement (ADR-014 §2.3) is in the database, so a new code path cannot bypass it: a DELETE of preserved records
-- (documents and their artifacts, coding and overlay history, redaction revisions, snapshots, productions, the Bates
-- ledger, exports, import batches and search term reports) of a held workspace fails with SQLSTATE O0423, as does a
-- TRUNCATE of those tables while any workspace is held, deleting the workspace row, moving the workspace to Deleting or
-- Purged (the E20-T02 hook), and dropping an audit partition that holds the workspace's events. The API answers O0423
-- with 423 Locked and an audit event. The list of guarded tables, and the tables deliberately left out, is pinned by
-- PreservationLockEnforcementTests: a new tenant table must be classified there.

ALTER TABLE opportunity.workspace
    ADD COLUMN active_preservation_locks integer NOT NULL DEFAULT 0,
    ADD CONSTRAINT workspace_active_preservation_locks_ck CHECK (active_preservation_locks >= 0);

CREATE TABLE opportunity.preservation_lock (
    workspace_id                uuid        NOT NULL,
    lock_id                     uuid        NOT NULL,
    scope                       smallint    NOT NULL DEFAULT 1,
    reason                      text        NOT NULL,
    matter_reference            text        NULL,
    release_requires_approval   boolean     NOT NULL,
    placed_by                   uuid        NOT NULL,
    placed_at                   timestamptz NOT NULL DEFAULT now(),
    release_requested_by        uuid        NULL,
    release_requested_at        timestamptz NULL,
    release_reason              text        NULL,
    release_approved_by         uuid        NULL,
    released_at                 timestamptz NULL,
    version                     bigint      NOT NULL DEFAULT 1,
    CONSTRAINT preservation_lock_pk PRIMARY KEY (workspace_id, lock_id),
    CONSTRAINT preservation_lock_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    -- 1 = Workspace; 2 = DocumentSet is reserved by ADR-014 §2.1 and not accepted yet.
    CONSTRAINT preservation_lock_scope_ck CHECK (scope = 1),
    CONSTRAINT preservation_lock_reason_ck CHECK (length(reason) BETWEEN 1 AND 2000 AND reason = btrim(reason)),
    CONSTRAINT preservation_lock_matter_reference_ck CHECK (
        matter_reference IS NULL OR (length(matter_reference) BETWEEN 1 AND 200 AND matter_reference = btrim(matter_reference))),
    CONSTRAINT preservation_lock_release_reason_ck CHECK (
        release_reason IS NULL OR (length(release_reason) BETWEEN 1 AND 2000 AND release_reason = btrim(release_reason))),
    CONSTRAINT preservation_lock_request_ck CHECK (
        (release_requested_by IS NULL) = (release_requested_at IS NULL)
        AND (release_requested_by IS NULL) = (release_reason IS NULL)),
    CONSTRAINT preservation_lock_released_ck CHECK (
        released_at IS NULL
        OR (release_requested_by IS NOT NULL
            AND (NOT release_requires_approval
                 OR (release_approved_by IS NOT NULL AND release_approved_by <> release_requested_by)))),
    CONSTRAINT preservation_lock_approval_ck CHECK (release_approved_by IS NULL OR released_at IS NOT NULL),
    CONSTRAINT preservation_lock_version_ck CHECK (version >= 1)
);

SELECT opportunity.enable_workspace_rls('opportunity.preservation_lock');

CREATE INDEX preservation_lock_active_ix ON opportunity.preservation_lock (workspace_id, placed_at) WHERE released_at IS NULL;

-- Holds are evidence of the preservation duty: nobody deletes them, not even the lifecycle run of E20-T02.
REVOKE DELETE, TRUNCATE ON opportunity.preservation_lock FROM opportunity_app;

CREATE FUNCTION opportunity.preservation_lock_guard()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF TG_OP <> 'UPDATE' THEN
        RAISE EXCEPTION 'preservation locks are never deleted' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.released_at IS NOT NULL THEN
        RAISE EXCEPTION 'preservation lock % is released and cannot change', OLD.lock_id USING ERRCODE = 'check_violation';
    END IF;
    IF (NEW.workspace_id, NEW.lock_id, NEW.scope, NEW.reason, NEW.matter_reference, NEW.release_requires_approval,
        NEW.placed_by, NEW.placed_at)
       IS DISTINCT FROM
       (OLD.workspace_id, OLD.lock_id, OLD.scope, OLD.reason, OLD.matter_reference, OLD.release_requires_approval,
        OLD.placed_by, OLD.placed_at) THEN
        RAISE EXCEPTION 'only the release of preservation lock % can change', OLD.lock_id USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.version <> OLD.version + 1 THEN
        RAISE EXCEPTION 'every change of preservation lock % increments its version', OLD.lock_id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER preservation_lock_guard
    BEFORE UPDATE OR DELETE ON opportunity.preservation_lock
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.preservation_lock_guard();

CREATE TRIGGER preservation_lock_truncate_refused
    BEFORE TRUNCATE ON opportunity.preservation_lock
    FOR EACH STATEMENT
    EXECUTE FUNCTION opportunity.preservation_lock_guard();

-- Recounts the workspace's unreleased holds after every placement and release (in the same transaction).
CREATE FUNCTION opportunity.preservation_lock_count()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    UPDATE opportunity.workspace w
       SET active_preservation_locks = (SELECT count(*) FROM opportunity.preservation_lock l
                                         WHERE l.workspace_id = NEW.workspace_id AND l.released_at IS NULL)
     WHERE w.workspace_id = NEW.workspace_id;
    RETURN NULL;
END
$$;

CREATE TRIGGER preservation_lock_count
    AFTER INSERT OR UPDATE OF released_at ON opportunity.preservation_lock
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.preservation_lock_count();

-- ---------------------------------------------------------------------------------------------------------------
-- The check every guard runs: lock the workspace row FOR SHARE (a placement or release waits for this transaction, and
-- this transaction sees a placement committed while it waited) and refuse when a hold is active. The row is locked under
-- the workspace's own RLS context (the registry's update policy needs it), restoring the caller's context afterwards, so
-- the check works for deletions run without a context too. Also the E20-T02 hook: a deletion step calls it in the
-- transaction that records the step's start (ADR-014 §2.4).
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.assert_workspace_not_preserved(p_workspace_id uuid, p_target text)
    RETURNS void
    LANGUAGE plpgsql
    VOLATILE
    SECURITY INVOKER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    caller_context text := coalesce(current_setting('app.workspace_id', true), '');
    held integer;
BEGIN
    IF p_workspace_id IS NULL THEN
        RETURN;
    END IF;

    PERFORM set_config('app.workspace_id', p_workspace_id::text, true);
    SELECT w.active_preservation_locks INTO held
      FROM opportunity.workspace w
     WHERE w.workspace_id = p_workspace_id
       FOR SHARE;
    PERFORM set_config('app.workspace_id', caller_context, true);

    IF coalesce(held, 0) > 0 THEN
        RAISE EXCEPTION 'workspace % is under a preservation lock (legal hold); % cannot be deleted', p_workspace_id, p_target
            USING ERRCODE = 'O0423',
                  DETAIL = p_workspace_id::text,
                  HINT = p_target;
    END IF;
END
$$;

REVOKE EXECUTE ON FUNCTION opportunity.assert_workspace_not_preserved(uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION opportunity.assert_workspace_not_preserved(uuid, text) TO opportunity_app;

COMMENT ON FUNCTION opportunity.assert_workspace_not_preserved(uuid, text) IS
    'Raises SQLSTATE O0423 (DETAIL = workspace id, HINT = what was refused) while the workspace has an active preservation '
    'lock; locks the workspace row FOR SHARE first. SECURITY INVOKER; restores the caller''s app.workspace_id. E20-T01.';

-- Statement-level guard: one check per workspace among the deleted rows (transition table old_rows).
CREATE FUNCTION opportunity.preservation_delete_guard()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    ws uuid;
BEGIN
    FOR ws IN SELECT DISTINCT o.workspace_id FROM old_rows o LOOP
        PERFORM opportunity.assert_workspace_not_preserved(ws, TG_TABLE_NAME);
    END LOOP;
    RETURN NULL;
END
$$;

-- The object registry: rows of documents that do not exist are left alone. The import writer unregisters the objects of
-- rows its transaction ended up not inserting (the deferred document foreign key would refuse the commit otherwise);
-- every artifact of an existing document, and every object without a document (exports, reports), is preserved.
CREATE FUNCTION opportunity.preservation_artifact_delete_guard()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    ws uuid;
BEGIN
    FOR ws IN
        SELECT DISTINCT o.workspace_id FROM old_rows o
         WHERE o.document_id IS NULL
            OR EXISTS (SELECT FROM opportunity.document d WHERE d.workspace_id = o.workspace_id AND d.document_id = o.document_id)
    LOOP
        PERFORM opportunity.assert_workspace_not_preserved(ws, TG_TABLE_NAME);
    END LOOP;
    RETURN NULL;
END
$$;

-- Row-level guard for partitioned tables: row triggers are cloned to every partition (statement triggers are not).
CREATE FUNCTION opportunity.preservation_row_delete_guard()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    PERFORM opportunity.assert_workspace_not_preserved(OLD.workspace_id, TG_TABLE_NAME);
    RETURN OLD;
END
$$;

-- TRUNCATE removes every workspace's rows: refused while any workspace is held.
CREATE FUNCTION opportunity.preservation_truncate_guard()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    held uuid;
BEGIN
    SELECT w.workspace_id INTO held FROM opportunity.workspace w WHERE w.active_preservation_locks > 0 LIMIT 1;
    IF held IS NOT NULL THEN
        RAISE EXCEPTION 'workspace % is under a preservation lock (legal hold); % cannot be truncated', held, TG_TABLE_NAME
            USING ERRCODE = 'O0423', DETAIL = held::text, HINT = TG_TABLE_NAME;
    END IF;
    RETURN NULL;
END
$$;

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'document', 'page_set', 'page', 'page_image', 'document_overlay_event', 'redaction_revision',
        'document_set_snapshot', 'document_set_snapshot_page', 'production', 'bates_range',
        'export', 'export_document', 'export_file', 'import_batch', 'search_term_report']
    LOOP
        EXECUTE format(
            'CREATE TRIGGER preservation_delete_guard AFTER DELETE ON opportunity.%I '
            || 'REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_delete_guard()', t);
    END LOOP;

    FOREACH t IN ARRAY ARRAY[
        'document', 'page_set', 'page', 'page_image', 'stored_object', 'coding_event', 'document_overlay_event',
        'redaction_revision', 'document_set_snapshot', 'document_set_snapshot_page', 'production', 'bates_range',
        'export', 'export_document', 'export_file', 'import_batch', 'search_term_report']
    LOOP
        EXECUTE format(
            'CREATE TRIGGER preservation_truncate_guard BEFORE TRUNCATE ON opportunity.%I '
            || 'FOR EACH STATEMENT EXECUTE FUNCTION opportunity.preservation_truncate_guard()', t);
    END LOOP;
END
$$;

CREATE TRIGGER preservation_delete_guard
    AFTER DELETE ON opportunity.stored_object
    REFERENCING OLD TABLE AS old_rows
    FOR EACH STATEMENT
    EXECUTE FUNCTION opportunity.preservation_artifact_delete_guard();

CREATE TRIGGER preservation_delete_guard
    BEFORE DELETE ON opportunity.coding_event
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.preservation_row_delete_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- The workspace itself: its row is never deleted while held (Purged is a tombstone anyway), and it cannot enter the
-- deletion lifecycle (E20-T02) while held. A hold placed during a run is the run's business (Deleting(Halted)).
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.workspace_preservation_guard()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF OLD.active_preservation_locks > 0 AND (TG_OP = 'DELETE' OR NEW.active_preservation_locks > 0) THEN
        RAISE EXCEPTION 'workspace % is under a preservation lock (legal hold); %', OLD.workspace_id,
            CASE WHEN TG_OP = 'DELETE' THEN 'it cannot be deleted' ELSE 'it cannot become ' || NEW.status END
            USING ERRCODE = 'O0423', DETAIL = OLD.workspace_id::text, HINT = 'workspace';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$$;

CREATE TRIGGER workspace_preservation_delete_guard
    BEFORE DELETE ON opportunity.workspace
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.workspace_preservation_guard();

CREATE TRIGGER workspace_preservation_status_guard
    BEFORE UPDATE OF status ON opportunity.workspace
    FOR EACH ROW
    WHEN (NEW.status IN ('Deleting', 'Purged') AND OLD.status NOT IN ('Deleting', 'Purged'))
    EXECUTE FUNCTION opportunity.workspace_preservation_guard();

CREATE TRIGGER workspace_preservation_truncate_guard
    BEFORE TRUNCATE ON opportunity.workspace
    FOR EACH STATEMENT
    EXECUTE FUNCTION opportunity.preservation_truncate_guard();

-- ---------------------------------------------------------------------------------------------------------------
-- Audit purge (ADR-014 §2.3, §6): the only removal path for audit refuses a partition holding events of a held workspace.
-- Same function as V0010 with that check added.
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

INSERT INTO audit.audit_action (category, action) VALUES
    ('Workspace', 'HoldReleaseCancelled'),
    ('Workspace', 'DeletionBlocked');
