-- V0004: interim coding current-state and CodingEvent provenance (E04-T04).
-- §27 conceptual model, interim until the PostgreSQL coding spike (E18-T03 / ADR-004a) picks the final shape; only
-- Opportunity.Data.Coding.CodingRepository reads or writes these tables (Application sees ICodingRepository).
-- Binding: ADR-003 R2 (coding never in document.metadata), ADR-001 §2 (every coding change bumps DocumentVersion in
-- document_projection_state, never the wide document tuple), ADR-010 §5.4/§8 (state-based writes; events only for
-- real changes; per-field ChangedAtVersion + ChangedByJobId for Q-07 skips), ADR-005 R2/P1-P10 (CodingEvent is
-- range-partitioned monthly from day one), ADR-013 (provenance is not audit), Q-08 (values reproducible from history).

-- ---------------------------------------------------------------------------------------------------------------
-- Current state: one row per (document, coding field) that has ever been written. A cleared value keeps its row
-- (value NULL, no choice rows) so ChangedAtVersion still records when it was cleared (Q-07).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.document_coding_field (
    workspace_id        uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    field_id            integer     NOT NULL,
    -- Always 3 (Coding); part of the FK so a coding row can only reference a Coding-storage field.
    field_storage       smallint    NOT NULL DEFAULT 3,
    -- Canonical ADR-003 value for non-choice types; NULL for choice types (see document_coding_choice) or when cleared.
    value               jsonb       NULL,
    -- ADR-010 §8.2: the DocumentVersion produced by the change, and the job that made it (NULL = interactive).
    changed_at_version  bigint      NOT NULL,
    changed_by_job_id   uuid        NULL,
    changed_by          uuid        NOT NULL,
    changed_at          timestamptz NOT NULL,
    CONSTRAINT document_coding_field_pk PRIMARY KEY (workspace_id, document_id, field_id),
    CONSTRAINT document_coding_field_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT document_coding_field_field_fk FOREIGN KEY (workspace_id, field_id, field_storage)
        REFERENCES opportunity.field_definition (workspace_id, field_id, storage),
    CONSTRAINT document_coding_field_storage_ck CHECK (field_storage = 3),
    CONSTRAINT document_coding_field_value_ck CHECK (value IS NULL OR jsonb_typeof(value) NOT IN ('null', 'object')),
    CONSTRAINT document_coding_field_version_ck CHECK (changed_at_version >= 1)
) WITH (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

-- "Does any document hold this field" (retype guard) and per-field scans.
CREATE INDEX document_coding_field_field_ix ON opportunity.document_coding_field (workspace_id, field_id, document_id);

-- Choice rows of SingleChoice/MultiChoice coding fields. The composite FK to choice guarantees the choice belongs to
-- this field and workspace and blocks deleting a choice that is in use (ADR-003 R8).
CREATE TABLE opportunity.document_coding_choice (
    workspace_id    uuid    NOT NULL,
    document_id     uuid    NOT NULL,
    field_id        integer NOT NULL,
    choice_id       integer NOT NULL,
    CONSTRAINT document_coding_choice_pk PRIMARY KEY (workspace_id, document_id, field_id, choice_id),
    CONSTRAINT document_coding_choice_field_fk FOREIGN KEY (workspace_id, document_id, field_id)
        REFERENCES opportunity.document_coding_field (workspace_id, document_id, field_id) ON DELETE CASCADE,
    CONSTRAINT document_coding_choice_choice_fk FOREIGN KEY (workspace_id, field_id, choice_id)
        REFERENCES opportunity.choice (workspace_id, field_id, choice_id)
) WITH (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.01);

CREATE INDEX document_coding_choice_choice_ix ON opportunity.document_coding_choice (workspace_id, field_id, choice_id);

-- Coding values now count as "values" for the ADR-003 R7 retype guard (V0003).
CREATE OR REPLACE FUNCTION opportunity.field_has_values(p_workspace_id uuid, p_field_id integer, p_storage smallint)
    RETURNS boolean
    LANGUAGE sql
    STABLE
RETURN CASE p_storage
    WHEN 2 THEN EXISTS (SELECT FROM opportunity.document d
                        WHERE d.workspace_id = p_workspace_id AND d.metadata ? ('f' || p_field_id::text))
    WHEN 3 THEN EXISTS (SELECT FROM opportunity.document_coding_field c
                        WHERE c.workspace_id = p_workspace_id AND c.field_id = p_field_id)
    ELSE false
END;

-- ---------------------------------------------------------------------------------------------------------------
-- CodingWrite: one row per applied coding mutation (an interactive save or one bulk chunk). Its unique
-- IdempotencyKey makes a re-applied chunk a no-op (ADR-010 §5); the request hash detects key reuse. Append-only.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.coding_write (
    workspace_id        uuid        NOT NULL,
    write_id            uuid        NOT NULL,
    idempotency_key     text        NOT NULL,
    request_hash        bytea       NOT NULL,
    actor_id            uuid        NOT NULL,
    actor_type          smallint    NOT NULL,
    job_id              uuid        NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT coding_write_pk PRIMARY KEY (workspace_id, write_id),
    CONSTRAINT coding_write_idempotency_uq UNIQUE (workspace_id, idempotency_key),
    -- FK target that pins each event's actor and key to its write.
    CONSTRAINT coding_write_event_uq UNIQUE (workspace_id, write_id, actor_id, actor_type, idempotency_key),
    CONSTRAINT coding_write_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT coding_write_key_ck CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    CONSTRAINT coding_write_hash_ck CHECK (octet_length(request_hash) = 32),
    -- ActorType: 1 Human (interactive, no job), 2 BulkHuman (job), 3 SystemRule, 4 Model (reserved).
    CONSTRAINT coding_write_actor_ck CHECK (
        actor_type BETWEEN 1 AND 4
        AND (actor_type <> 1 OR job_id IS NULL)
        AND (actor_type <> 2 OR job_id IS NOT NULL))
);

REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.coding_write FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- CodingEvent (§27): append-only provenance, one row per field change of one document, written in the same
-- transaction as the current-state change. prior/new hold canonical values (NULL = absent), so any field's value at
-- any DocumentVersion can be reconstructed (Q-08). Kind 2 records a Q-07 skip (no value change, no version bump).
-- Range-partitioned monthly on occurred_at (ADR-005 R2); partitions are created ahead by
-- coding_event_ensure_partitions. Retention follows the matter, not a rolling window: partitions are never dropped
-- by age, only by workspace purge (ADR-014).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.coding_event (
    workspace_id        uuid        NOT NULL,
    occurred_at         timestamptz NOT NULL,
    event_id            uuid        NOT NULL,
    write_id            uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    field_id            integer     NOT NULL,
    event_kind          smallint    NOT NULL,
    prior_value         jsonb       NULL,
    new_value           jsonb       NULL,
    -- ValueChanged: the DocumentVersion this change produced. Skip: the document's version when it was skipped.
    document_version    bigint      NOT NULL,
    actor_id            uuid        NOT NULL,
    actor_type          smallint    NOT NULL,
    job_id              uuid        NULL,
    idempotency_key     text        NOT NULL,
    CONSTRAINT coding_event_pk PRIMARY KEY (workspace_id, occurred_at, event_id),
    CONSTRAINT coding_event_write_fk FOREIGN KEY (workspace_id, write_id, actor_id, actor_type, idempotency_key)
        REFERENCES opportunity.coding_write (workspace_id, write_id, actor_id, actor_type, idempotency_key),
    CONSTRAINT coding_event_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT coding_event_field_fk FOREIGN KEY (workspace_id, field_id)
        REFERENCES opportunity.field_definition (workspace_id, field_id),
    CONSTRAINT coding_event_kind_ck CHECK (event_kind BETWEEN 1 AND 2),
    CONSTRAINT coding_event_change_ck CHECK (event_kind <> 1 OR prior_value IS DISTINCT FROM new_value),
    CONSTRAINT coding_event_skip_ck CHECK (event_kind <> 2 OR job_id IS NOT NULL),
    CONSTRAINT coding_event_actor_ck CHECK (
        actor_type BETWEEN 1 AND 4
        AND (actor_type <> 1 OR job_id IS NULL)
        AND (actor_type <> 2 OR job_id IS NOT NULL)),
    CONSTRAINT coding_event_version_ck CHECK (document_version >= 1)
) PARTITION BY RANGE (occurred_at);

-- Document history and as-of-version reconstruction (Q-08).
CREATE INDEX coding_event_document_ix ON opportunity.coding_event (workspace_id, document_id, field_id, document_version);
-- Reports by actor type (legal finding 15: automated coding is distinguishable).
CREATE INDEX coding_event_actor_type_ix ON opportunity.coding_event (workspace_id, actor_type, occurred_at);
-- Job provenance and skip reports (ADR-010 §8.4).
CREATE INDEX coding_event_job_ix ON opportunity.coding_event (workspace_id, job_id, document_id, field_id) WHERE job_id IS NOT NULL;
CREATE INDEX coding_event_write_ix ON opportunity.coding_event (workspace_id, write_id);

REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.coding_event FROM opportunity_app;

-- Creates the monthly partitions (UTC month boundaries) from the previous month through p_through. Idempotent. Runs as
-- the schema owner so a scheduled maintenance job (E19) can call it with the application role; this migration covers
-- the next 24 months. New partitions are append-only for the application role as well.
CREATE FUNCTION opportunity.coding_event_ensure_partitions(p_through timestamptz)
    RETURNS integer
    LANGUAGE plpgsql
    SECURITY DEFINER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    month_start date := (date_trunc('month', now() AT TIME ZONE 'UTC') - interval '1 month')::date;
    last_month  date := date_trunc('month', p_through AT TIME ZONE 'UTC')::date;
    partition   text;
    created     integer := 0;
BEGIN
    WHILE month_start <= last_month LOOP
        partition := 'coding_event_p' || to_char(month_start, 'YYYYMM');
        IF to_regclass('opportunity.' || partition) IS NULL THEN
            EXECUTE format(
                'CREATE TABLE opportunity.%I PARTITION OF opportunity.coding_event FOR VALUES FROM (%L) TO (%L)',
                partition,
                to_char(month_start, 'YYYY-MM-DD') || ' 00:00:00+00',
                to_char((month_start + interval '1 month')::date, 'YYYY-MM-DD') || ' 00:00:00+00');
            EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON opportunity.%I FROM opportunity_app', partition);
            created := created + 1;
        END IF;
        month_start := (month_start + interval '1 month')::date;
    END LOOP;
    RETURN created;
END
$$;

SELECT opportunity.coding_event_ensure_partitions(now() + interval '24 months');
