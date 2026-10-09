-- V0057: defensible workspace deletion (E20-T02, #167; ADR-014 §3-§9, Q-23, Q-16, Q-40).
--
-- workspace_deletion: one row per deletion request. A Workspace Admin requests (retention profile, reason, optional
--   external reference); a different person holding the installation permission Installation.ApproveDeletion approves;
--   the run starts once the waiting period after the approval has passed. Requested -> Approved -> Running ->
--   Completed | CompletedWithResiduals, with Halted (a legal hold placed during the run), Cancelled and Expired. The run
--   is driven by the deletion coordinator under a lease on this row and records each step in workspace_deletion_step,
--   so a crashed or restarted worker resumes where the run stood.
-- destruction_certificate: the certificate of a finished run (ADR-014 §8), also stored as a JSON object under
--   sys/certificates/{deletionId}/.
-- These three tables are installation-level (@global) and never deleted, so the record of the deletion survives it;
-- their workspace columns are not named workspace_id because every workspace_id column marks a tenant table. The
-- workspace row itself stays as the Purged tombstone (ADR-014 §1.3) and audit is never purged by a deletion (§5.1).
--
-- Fencing (ADR-014 §4): the run moves the workspace to Deleting (and increments its epoch) in the transaction that
-- locks the workspace row FOR UPDATE. From then on the write fence below refuses new rows in the tables new work starts
-- from (jobs, documents, stored objects, search work, index placements, dead letters) with SQLSTATE O0410; it takes FOR KEY SHARE on
-- the workspace row, so the move to Deleting waits for transactions already inserting and every later one sees it.
--
-- Purge (ADR-014 §4 step 5): workspace_purge_batch deletes one batch of one tenant table for a Running deletion. It is
-- the only path that may remove append-only and frozen records: it runs as the owner (the application role has no
-- DELETE on them), still calls assert_workspace_not_preserved (a legal hold placed during the run halts it), and the
-- immutability guards of choices, snapshot membership and production members let the purge through only for a
-- workspace in Deleting whose purge the transaction has announced (workspace_purge_active).

CREATE TABLE opportunity.workspace_deletion (
    deletion_id             uuid        NOT NULL,
    deletion_workspace_id   uuid        NOT NULL,
    -- Copied at request time: the certificate names them even if the workspace was renamed later.
    workspace_name          text        NOT NULL,
    matter_number           text        NULL,
    -- 1 RetainRecords (Q-23 default: productions, their snapshots and audit are kept), 2 PurgeAll.
    retention_profile       smallint    NOT NULL,
    reason                  text        NOT NULL,
    external_reference      text        NULL,
    status                  text        NOT NULL DEFAULT 'Requested',
    -- The current step of a run (Opportunity.Application.Workspaces.Deletion.DeletionStep).
    step                    text        NULL,
    requested_by            uuid        NOT NULL,
    requested_at            timestamptz NOT NULL DEFAULT now(),
    expires_at              timestamptz NOT NULL,
    approved_by             uuid        NULL,
    approved_at             timestamptz NULL,
    approval_note           text        NULL,
    run_not_before          timestamptz NULL,
    cancelled_by            uuid        NULL,
    cancelled_at            timestamptz NULL,
    fence_epoch             bigint      NULL,
    started_at              timestamptz NULL,
    finished_at             timestamptz NULL,
    -- Earliest time of the next timed step (the second search pass after the resurrection guard delay).
    next_step_at            timestamptz NULL,
    halted_at               timestamptz NULL,
    error                   text        NULL,
    lease_owner             text        NULL,
    lease_expires_at        timestamptz NULL,
    version                 bigint      NOT NULL DEFAULT 1,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workspace_deletion_pk PRIMARY KEY (deletion_id),
    CONSTRAINT workspace_deletion_workspace_fk FOREIGN KEY (deletion_workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT workspace_deletion_status_ck CHECK (status IN (
        'Requested', 'Approved', 'Running', 'Halted', 'Completed', 'CompletedWithResiduals', 'Cancelled', 'Expired')),
    CONSTRAINT workspace_deletion_profile_ck CHECK (retention_profile IN (1, 2)),
    CONSTRAINT workspace_deletion_text_ck CHECK (
        length(reason) BETWEEN 1 AND 2000 AND reason = btrim(reason)
        AND (external_reference IS NULL OR (length(external_reference) BETWEEN 1 AND 200 AND external_reference = btrim(external_reference)))
        AND (approval_note IS NULL OR (length(approval_note) BETWEEN 1 AND 2000 AND approval_note = btrim(approval_note)))
        AND length(workspace_name) BETWEEN 1 AND 200 AND coalesce(length(matter_number), 0) <= 100
        AND coalesce(length(error), 0) <= 2000 AND coalesce(length(lease_owner), 0) <= 200 AND coalesce(length(step), 0) <= 64),
    -- Q-23: the approver is a different person.
    CONSTRAINT workspace_deletion_second_person_ck CHECK (approved_by IS NULL OR approved_by <> requested_by),
    CONSTRAINT workspace_deletion_approval_ck CHECK (
        (approved_by IS NULL) = (approved_at IS NULL) AND (approved_by IS NULL) = (run_not_before IS NULL)
        AND (status IN ('Requested', 'Cancelled', 'Expired') OR approved_by IS NOT NULL)),
    CONSTRAINT workspace_deletion_cancel_ck CHECK ((cancelled_at IS NULL) = (status <> 'Cancelled')
        AND (cancelled_by IS NULL OR cancelled_at IS NOT NULL)),
    CONSTRAINT workspace_deletion_run_ck CHECK (
        (started_at IS NULL) = (status IN ('Requested', 'Approved', 'Cancelled', 'Expired'))
        AND (started_at IS NULL) = (fence_epoch IS NULL)
        AND (finished_at IS NULL) = (status NOT IN ('Completed', 'CompletedWithResiduals', 'Cancelled', 'Expired'))),
    CONSTRAINT workspace_deletion_version_ck CHECK (version >= 1)
);

COMMENT ON TABLE opportunity.workspace_deletion IS
    '@global workspace deletion requests and runs (E20-T02, ADR-014 §3-§4); installation-level so the record survives the deletion';

-- One open request or run per workspace.
CREATE UNIQUE INDEX workspace_deletion_open_uq ON opportunity.workspace_deletion (deletion_workspace_id)
    WHERE status IN ('Requested', 'Approved', 'Running', 'Halted');
CREATE INDEX workspace_deletion_workspace_ix ON opportunity.workspace_deletion (deletion_workspace_id, requested_at DESC);
CREATE INDEX workspace_deletion_active_ix ON opportunity.workspace_deletion (status, run_not_before)
    WHERE status IN ('Requested', 'Approved', 'Running', 'Halted');

CREATE TABLE opportunity.workspace_deletion_step (
    deletion_id     uuid        NOT NULL,
    step            text        NOT NULL,
    attempt         integer     NOT NULL DEFAULT 1,
    started_at      timestamptz NOT NULL DEFAULT now(),
    finished_at     timestamptz NULL,
    -- Success, Residuals (verification found something and the step is re-run), Halted (a legal hold) or Failed.
    outcome         text        NULL,
    -- Per store: {"postgres": {"<table>": n}, "openSearch": {...}, "objects": {...}, "keys": {...}}. Counts and names only.
    counts          jsonb       NULL,
    detail          jsonb       NULL,
    CONSTRAINT workspace_deletion_step_pk PRIMARY KEY (deletion_id, step),
    CONSTRAINT workspace_deletion_step_deletion_fk FOREIGN KEY (deletion_id) REFERENCES opportunity.workspace_deletion (deletion_id),
    CONSTRAINT workspace_deletion_step_ck CHECK (length(step) BETWEEN 1 AND 64 AND attempt >= 1
        AND (outcome IS NULL OR outcome IN ('Success', 'Residuals', 'Halted', 'Failed'))
        AND (counts IS NULL OR (jsonb_typeof(counts) = 'object' AND pg_column_size(counts) <= 262144))
        AND (detail IS NULL OR (jsonb_typeof(detail) = 'object' AND pg_column_size(detail) <= 65536)))
);

COMMENT ON TABLE opportunity.workspace_deletion_step IS
    '@global steps of a workspace deletion run with their counts (ADR-014 §4); counts and names only, never content';

CREATE TABLE opportunity.destruction_certificate (
    deletion_id                 uuid        NOT NULL,
    certificate_workspace_id    uuid        NOT NULL,
    issued_at                   timestamptz NOT NULL,
    -- SHA-256 of the canonical certificate bytes: the UTF-8 of certificate, exactly as stored in object storage.
    sha256                      bytea       NOT NULL,
    certificate                 text        NOT NULL,
    object_key                  text        NULL,
    signature_key_id            text        NULL,
    signature                   bytea       NULL,
    CONSTRAINT destruction_certificate_pk PRIMARY KEY (deletion_id),
    CONSTRAINT destruction_certificate_deletion_fk FOREIGN KEY (deletion_id) REFERENCES opportunity.workspace_deletion (deletion_id),
    CONSTRAINT destruction_certificate_ck CHECK (octet_length(sha256) = 32 AND jsonb_typeof(certificate::jsonb) = 'object'
        AND octet_length(certificate) <= 1048576 AND (signature IS NULL) = (signature_key_id IS NULL))
);

COMMENT ON TABLE opportunity.destruction_certificate IS
    '@global destruction certificates of workspace deletions (ADR-014 §8); retained outside the deleted workspace, never content';

-- The record of a deletion is evidence: nobody deletes it.
REVOKE DELETE, TRUNCATE ON opportunity.workspace_deletion, opportunity.workspace_deletion_step, opportunity.destruction_certificate
    FROM opportunity_app;

CREATE FUNCTION opportunity.destruction_certificate_immutable()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'destruction certificates are immutable' USING ERRCODE = 'insufficient_privilege';
END
$$;

CREATE TRIGGER destruction_certificate_immutable
    BEFORE UPDATE OR DELETE ON opportunity.destruction_certificate
    FOR EACH ROW
    EXECUTE FUNCTION opportunity.destruction_certificate_immutable();

-- ---------------------------------------------------------------------------------------------------------------
-- Write fence (ADR-014 §4 step 1).
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.workspace_write_fence()
    RETURNS trigger
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    caller_context text := coalesce(current_setting('app.workspace_id', true), '');
    ws uuid;
    current_status text;
BEGIN
    FOR ws IN SELECT DISTINCT n.workspace_id FROM new_rows n LOOP
        -- The registry's update policy (needed for the row lock) wants the workspace's own context.
        PERFORM set_config('app.workspace_id', ws::text, true);
        SELECT w.status INTO current_status FROM opportunity.workspace w WHERE w.workspace_id = ws FOR KEY SHARE;
        PERFORM set_config('app.workspace_id', caller_context, true);
        IF current_status IN ('Deleting', 'Purged') THEN
            RAISE EXCEPTION 'workspace % is being deleted; % accepts no new rows', ws, TG_TABLE_NAME
                USING ERRCODE = 'O0410', DETAIL = ws::text, HINT = TG_TABLE_NAME;
        END IF;
    END LOOP;
    RETURN NULL;
END
$$;

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['job', 'document', 'stored_object', 'search_outbox', 'index_chunk_task', 'workspace_index_placement', 'dead_letter']
    LOOP
        EXECUTE format(
            'CREATE TRIGGER workspace_write_fence AFTER INSERT ON opportunity.%I '
            || 'REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION opportunity.workspace_write_fence()', t);
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Purge (ADR-014 §4 step 5, §5, §7).
-- ---------------------------------------------------------------------------------------------------------------

-- True while this transaction purges the workspace (set by workspace_purge_batch) and the workspace is Deleting.
CREATE FUNCTION opportunity.workspace_purge_active(p_workspace_id uuid)
    RETURNS boolean
    LANGUAGE sql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT coalesce(current_setting('opportunity.purge_workspace', true), '') = p_workspace_id::text
       AND EXISTS (SELECT FROM opportunity.workspace w WHERE w.workspace_id = p_workspace_id AND w.status = 'Deleting');
$$;

-- The immutability guards below (V0003, V0020, V0038, V0047, V0051) are unchanged except for their first statement.
CREATE OR REPLACE FUNCTION opportunity.choice_delete_guard()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
BEGIN
    IF opportunity.workspace_purge_active(OLD.workspace_id) THEN
        RETURN OLD;
    END IF;
    IF OLD.first_used_at IS NOT NULL
       OR EXISTS (SELECT FROM opportunity.document d
                  WHERE d.workspace_id = OLD.workspace_id
                    AND d.metadata -> ('f' || OLD.field_id::text) @> to_jsonb(OLD.choice_id)) THEN
        RAISE EXCEPTION 'Choice % has been used and can only be deactivated', OLD.choice_id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'choice_in_use';
    END IF;
    RETURN OLD;
END
$function$;

CREATE OR REPLACE FUNCTION opportunity.choice_system_guard()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.system_key IS NOT NULL AND NOT opportunity.workspace_purge_active(OLD.workspace_id) THEN
            RAISE EXCEPTION 'System choice % (%) cannot be deleted', OLD.choice_id, OLD.system_key
                USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'choice_system';
        END IF;
        RETURN OLD;
    END IF;
    IF NEW.system_key IS DISTINCT FROM OLD.system_key OR (NEW.system_key IS NOT NULL AND NOT NEW.is_active) THEN
        RAISE EXCEPTION 'System choice % (%) cannot be re-keyed or deactivated', OLD.choice_id, OLD.system_key
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'choice_system';
    END IF;
    RETURN NEW;
END
$function$;

CREATE OR REPLACE FUNCTION opportunity.document_set_snapshot_page_guard()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
DECLARE
    header_status text;
BEGIN
    IF TG_OP = 'DELETE' AND opportunity.workspace_purge_active(OLD.workspace_id) THEN
        RETURN OLD;
    END IF;
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
$function$;

CREATE OR REPLACE FUNCTION opportunity.production_designation_override_guard()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_status smallint;
    v_row opportunity.production_designation_override;
BEGIN
    IF TG_OP = 'DELETE' AND opportunity.workspace_purge_active(OLD.workspace_id) THEN
        RETURN OLD;
    END IF;
    v_row := CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
    SELECT p.status INTO v_status
      FROM opportunity.production p
     WHERE p.workspace_id = v_row.workspace_id AND p.production_id = v_row.production_id;
    IF v_status IS DISTINCT FROM 1 THEN
        RAISE EXCEPTION 'The designation overrides of production % are frozen', v_row.production_id
            USING ERRCODE = 'integrity_constraint_violation',
                  HINT = 'E12-T04: a finalized production is frozen; create a new production version instead.';
    END IF;
    RETURN v_row;
END
$function$;

CREATE OR REPLACE FUNCTION opportunity.production_document_frozen_guard()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_status smallint;
BEGIN
    IF TG_OP = 'DELETE' AND opportunity.workspace_purge_active(OLD.workspace_id) THEN
        RETURN OLD;
    END IF;
    SELECT p.status INTO v_status
      FROM opportunity.production p
     WHERE p.workspace_id = OLD.workspace_id AND p.production_id = OLD.production_id;
    IF v_status IS DISTINCT FROM 1 THEN
        RAISE EXCEPTION 'The members of production % are frozen', OLD.production_id
            USING ERRCODE = 'integrity_constraint_violation',
                  HINT = 'E12-T02: a finalized production is frozen; create a new production version instead.';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$function$;

-- Rows the RetainRecords profile keeps (ADR-014 §5, Q-23): productions with their members, Bates ledger and volume runs
-- (export rows with production_id, their files and objects), the snapshots they were made from (with their source
-- chain), the jobs they reference and the Redaction Sets their members name. NULL: nothing of the table is kept. The
-- predicate is on alias t and parameter $1 (the workspace).
CREATE FUNCTION opportunity.workspace_purge_retained(p_table text)
    RETURNS text
    LANGUAGE sql
    IMMUTABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT CASE p_table
        WHEN 'production' THEN 'true'
        WHEN 'production_document' THEN 'true'
        WHEN 'bates_range' THEN 'true'
        WHEN 'export' THEN 't.production_id IS NOT NULL'
        WHEN 'export_document' THEN
            'EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.export_id = t.export_id AND e.production_id IS NOT NULL)'
        WHEN 'export_file' THEN
            'EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.export_id = t.export_id AND e.production_id IS NOT NULL)'
        WHEN 'stored_object' THEN
            'EXISTS (SELECT FROM opportunity.export_file f JOIN opportunity.export e ON e.workspace_id = f.workspace_id AND e.export_id = f.export_id '
            || 'WHERE f.workspace_id = $1 AND f.object_id = t.object_id AND e.production_id IS NOT NULL)'
        WHEN 'document_set_snapshot' THEN
            't.snapshot_id IN (WITH RECURSIVE kept(id) AS ('
            || 'SELECT p.snapshot_id FROM opportunity.production p WHERE p.workspace_id = $1 '
            || 'UNION SELECT e.snapshot_id FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL '
            || 'UNION SELECT s.source_snapshot_id FROM opportunity.document_set_snapshot s JOIN kept k ON s.snapshot_id = k.id '
            || 'WHERE s.workspace_id = $1) SELECT id FROM kept WHERE id IS NOT NULL)'
        WHEN 'document_set_snapshot_page' THEN
            't.snapshot_id IN (WITH RECURSIVE kept(id) AS ('
            || 'SELECT p.snapshot_id FROM opportunity.production p WHERE p.workspace_id = $1 '
            || 'UNION SELECT e.snapshot_id FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL '
            || 'UNION SELECT s.source_snapshot_id FROM opportunity.document_set_snapshot s JOIN kept k ON s.snapshot_id = k.id '
            || 'WHERE s.workspace_id = $1) SELECT id FROM kept WHERE id IS NOT NULL)'
        WHEN 'job' THEN
            '(EXISTS (SELECT FROM opportunity.production p WHERE p.workspace_id = $1 AND p.bates_job_id = t.job_id) '
            || 'OR EXISTS (SELECT FROM opportunity.export e WHERE e.workspace_id = $1 AND e.production_id IS NOT NULL AND e.job_id = t.job_id))'
        WHEN 'redaction_set' THEN
            'EXISTS (SELECT FROM opportunity.production_document pd WHERE pd.workspace_id = $1 AND pd.redaction_set_id = t.redaction_set_id)'
    END;
$$;

-- The tenant tables the purge may touch: every table of schema opportunity with a workspace_id column except the
-- registry (tombstone), the legal holds (never deleted) and the data keys (crypto-shredding keeps their rows as the
-- record of what was destroyed).
CREATE FUNCTION opportunity.workspace_purge_tables()
    RETURNS SETOF text
    LANGUAGE sql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    SELECT c.relname::text
      FROM pg_class c
      JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'workspace_id' AND NOT a.attisdropped
     WHERE c.relnamespace = 'opportunity'::regnamespace AND c.relkind IN ('r', 'p') AND NOT c.relispartition
       AND c.relname NOT IN ('workspace', 'preservation_lock', 'workspace_data_key')
     ORDER BY 1;
$$;

-- The Running deletion of p_deletion_id, its workspace in Deleting and not held; sets the purge context.
CREATE FUNCTION opportunity.workspace_purge_begin(p_deletion_id uuid, p_target text)
    RETURNS TABLE (workspace_id uuid, retention_profile smallint)
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    ws uuid;
    profile smallint;
BEGIN
    SELECT d.deletion_workspace_id, d.retention_profile INTO ws, profile
      FROM opportunity.workspace_deletion d
     WHERE d.deletion_id = p_deletion_id AND d.status = 'Running';
    IF ws IS NULL THEN
        RAISE EXCEPTION 'deletion % is not running', p_deletion_id USING ERRCODE = 'object_not_in_prerequisite_state';
    END IF;
    IF NOT EXISTS (SELECT FROM opportunity.workspace w WHERE w.workspace_id = ws AND w.status = 'Deleting') THEN
        RAISE EXCEPTION 'workspace % is not being deleted', ws USING ERRCODE = 'object_not_in_prerequisite_state';
    END IF;
    PERFORM opportunity.assert_workspace_not_preserved(ws, p_target);
    PERFORM set_config('app.workspace_id', ws::text, true);
    PERFORM set_config('opportunity.purge_workspace', ws::text, true);
    RETURN QUERY SELECT ws, profile;
END
$$;

-- Deletes up to p_batch rows of one tenant table of a Running deletion's workspace, by primary key, skipping rows the
-- retention profile keeps. A self-referencing table loses rows no other remaining row references first. The document
-- step removes each batch of documents together with their page sets and their stored objects (the three reference
-- each other; the back references are deferred). Returns the rows deleted (documents only for the document step).
CREATE FUNCTION opportunity.workspace_purge_batch(p_deletion_id uuid, p_table text, p_batch integer)
    RETURNS bigint
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    ws uuid;
    profile smallint;
    rel regclass;
    pk text;
    pk_t text;
    pk_x text;
    leaf text := '';
    keep text;
    fk record;
    cond text;
    ids uuid[];
    deleted bigint := 0;
BEGIN
    IF p_batch IS NULL OR p_batch < 1 OR p_batch > 100000 THEN
        RAISE EXCEPTION 'batch size % is out of range', p_batch USING ERRCODE = 'invalid_parameter_value';
    END IF;
    IF p_table IS NULL OR p_table NOT IN (SELECT opportunity.workspace_purge_tables()) THEN
        RAISE EXCEPTION 'table % is not purged by a workspace deletion', p_table USING ERRCODE = 'invalid_parameter_value';
    END IF;

    SELECT b.workspace_id, b.retention_profile INTO ws, profile FROM opportunity.workspace_purge_begin(p_deletion_id, p_table) b;
    rel := ('opportunity.' || quote_ident(p_table))::regclass;
    keep := CASE WHEN profile = 1 THEN opportunity.workspace_purge_retained(p_table) END;

    SELECT string_agg(quote_ident(a.attname), ', ' ORDER BY k.ord),
           string_agg('t.' || quote_ident(a.attname), ', ' ORDER BY k.ord),
           string_agg('x.' || quote_ident(a.attname), ', ' ORDER BY k.ord)
      INTO pk, pk_t, pk_x
      FROM pg_constraint c
      CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord)
      JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
     WHERE c.conrelid = rel AND c.contype = 'p';

    FOR fk IN SELECT c.conkey, c.confkey FROM pg_constraint c WHERE c.conrelid = rel AND c.confrelid = rel AND c.contype = 'f' LOOP
        SELECT string_agg(format('x.%I = t.%I', a1.attname, a2.attname), ' AND ')
          INTO cond
          FROM unnest(fk.conkey, fk.confkey) AS u(k1, k2)
          JOIN pg_attribute a1 ON a1.attrelid = rel AND a1.attnum = u.k1
          JOIN pg_attribute a2 ON a2.attrelid = rel AND a2.attnum = u.k2;
        leaf := leaf || format(' AND NOT EXISTS (SELECT FROM opportunity.%I x WHERE %s AND (%s) IS DISTINCT FROM (%s))',
            p_table, cond, pk_x, pk_t);
    END LOOP;

    IF p_table = 'document' THEN
        EXECUTE format('SELECT array_agg(t.document_id) FROM (SELECT t.document_id FROM opportunity.document t '
                       || 'WHERE t.workspace_id = $1 %s LIMIT $2) t', leaf)
            INTO ids USING ws, p_batch;
        IF ids IS NULL THEN
            RETURN 0;
        END IF;
        DELETE FROM opportunity.page_set s WHERE s.workspace_id = ws AND s.document_id = ANY (ids);
        DELETE FROM opportunity.document d WHERE d.workspace_id = ws AND d.document_id = ANY (ids);
        GET DIAGNOSTICS deleted = ROW_COUNT;
        DELETE FROM opportunity.stored_object o WHERE o.workspace_id = ws AND o.document_id = ANY (ids);
        RETURN deleted;
    END IF;

    IF p_table = 'workspace_index_placement' THEN
        -- The shared pool forgets the workspace in the transaction that deletes its placement (once).
        UPDATE opportunity.search_shared_index s
           SET workspace_count = greatest(s.workspace_count - 1, 0),
               assigned_bytes = greatest(s.assigned_bytes - p.estimated_bytes, 0),
               updated_at = now()
          FROM opportunity.workspace_index_placement p
         WHERE p.workspace_id = ws
           AND ((p.kind = 1 AND s.pool_number = p.shared_pool) OR (p.pending_kind = 1 AND s.pool_number = p.pending_shared_pool));
    END IF;

    EXECUTE format('DELETE FROM opportunity.%1$I WHERE workspace_id = $1 AND (%2$s) IN ('
                   || 'SELECT %3$s FROM opportunity.%1$I t WHERE t.workspace_id = $1 %4$s %5$s LIMIT $2)',
                   p_table, pk, pk_t, leaf, CASE WHEN keep IS NULL THEN '' ELSE ' AND NOT (' || keep || ')' END)
        USING ws, p_batch;
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
END
$$;

REVOKE EXECUTE ON FUNCTION opportunity.workspace_purge_batch(uuid, text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION opportunity.workspace_purge_batch(uuid, text, integer) TO opportunity_app;
REVOKE EXECUTE ON FUNCTION opportunity.workspace_purge_begin(uuid, text) FROM PUBLIC;

COMMENT ON FUNCTION opportunity.workspace_purge_batch(uuid, text, integer) IS
    '@security-definer the purge of a Running workspace deletion (E20-T02, ADR-014 §4 step 5): deletes one batch of one '
    'tenant table of a workspace in Deleting, including append-only and frozen records the application role cannot delete; '
    'refuses under a legal hold. Returns a count only.';

-- Rows per tenant table of a Running deletion's workspace, and how many of them the retention profile keeps (ADR-014
-- §7: counted as the owner in the workspace's context, not through the caller's view of the rows).
CREATE FUNCTION opportunity.workspace_purge_counts(p_deletion_id uuid)
    RETURNS TABLE (table_name text, total bigint, retained bigint)
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    ws uuid;
    profile smallint;
    t text;
    keep text;
BEGIN
    SELECT b.workspace_id, b.retention_profile INTO ws, profile FROM opportunity.workspace_purge_begin(p_deletion_id, 'inventory') b;
    FOR t IN SELECT opportunity.workspace_purge_tables() LOOP
        keep := CASE WHEN profile = 1 THEN opportunity.workspace_purge_retained(t) END;
        table_name := t;
        EXECUTE format('SELECT count(*), count(*) FILTER (WHERE %s) FROM opportunity.%I t WHERE t.workspace_id = $1',
                       coalesce(keep, 'false'), t)
            INTO total, retained USING ws;
        RETURN NEXT;
    END LOOP;
END
$$;

REVOKE EXECUTE ON FUNCTION opportunity.workspace_purge_counts(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION opportunity.workspace_purge_counts(uuid) TO opportunity_app;

COMMENT ON FUNCTION opportunity.workspace_purge_counts(uuid) IS
    '@security-definer inventory and verification of a Running workspace deletion (ADR-014 §7): row counts per tenant '
    'table of a workspace in Deleting, counted as the owner; returns counts only.';
