-- V0043: alias-based reindex and projection generation switch (E07-T11, ADR-006 R12/R13, ADR-001 §7.5).
--
-- 1. Dedicated index revisions. The physical name of a dedicated index is derived from (generation, revision); a rebuild
--    of a dedicated workspace always gets a fresh revision, so the same mapping generation can be rebuilt into a new
--    physical index (DR rebuild, re-sharding) and an aborted or retained index name is never reused. Revision 0 keeps
--    the original name ({alias}-g{G}); later revisions are {alias}-r{R}-g{G}, which the generation's template pattern
--    still matches. last_revision only grows (an aborted rebuild leaves it). Shared placements always use revision 0.
-- 2. search_reindex: one row per reindex job, the resumable state of the reindex coordinator (indexing worker host).
--    It holds placement facts (tier, pool, generation, revision), never a physical index name (ADR-006 R1).
-- 3. search_reindex_active: installation-level list of the workspaces whose reindex the coordinator still drives, so it
--    does not probe every workspace on every pass. Identifiers only.

ALTER TABLE opportunity.workspace_index_placement
    ADD COLUMN revision          integer NOT NULL DEFAULT 0,
    ADD COLUMN pending_revision  integer NULL,
    ADD COLUMN last_revision     integer NOT NULL DEFAULT 0,
    ADD CONSTRAINT workspace_index_placement_revision_ck CHECK (
        revision >= 0 AND last_revision >= revision
        AND (pending_revision IS NULL OR (pending_revision BETWEEN 0 AND last_revision AND state IN (2, 3)))
        AND (kind = 2 OR revision = 0));

-- Phases (Opportunity.Application.Search.Reindex.ReindexPhase): 1 Pending, 2 Building, 3 Backfilling, 4 Validating,
-- 5 Switching (in flight, at most one per workspace); 6 Switched, 7 Retaining, 8 Completed, 9 Aborting, 10 Aborted.
CREATE TABLE opportunity.search_reindex (
    workspace_id            uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    phase                   smallint    NOT NULL DEFAULT 1,
    requested_kind          smallint    NULL,
    requested_generation    integer     NULL,
    requested_shards        integer     NULL,
    source_kind             smallint    NULL,
    source_pool             integer     NULL,
    source_generation       integer     NULL,
    source_revision         integer     NULL,
    target_kind             smallint    NULL,
    target_pool             integer     NULL,
    target_generation       integer     NULL,
    target_revision         integer     NULL,
    -- Earliest time of the next timed step: backfill start (Building), write block (Switched), deletion (Retaining),
    -- second target drop (Aborting).
    next_step_at            timestamptz NULL,
    retain_until            timestamptz NULL,
    documents_planned       bigint      NULL,
    validation              jsonb       NULL,
    error                   text        NULL,
    lease_owner             text        NULL,
    lease_expires_at        timestamptz NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    switched_at             timestamptz NULL,
    finished_at             timestamptz NULL,
    CONSTRAINT search_reindex_pk PRIMARY KEY (workspace_id, job_id),
    CONSTRAINT search_reindex_job_fk FOREIGN KEY (workspace_id, job_id) REFERENCES opportunity.job (workspace_id, job_id),
    CONSTRAINT search_reindex_phase_ck CHECK (phase BETWEEN 1 AND 10),
    CONSTRAINT search_reindex_kinds_ck CHECK (coalesce(requested_kind, 1) IN (1, 2) AND coalesce(source_kind, 1) IN (1, 2)
        AND coalesce(target_kind, 1) IN (1, 2)),
    CONSTRAINT search_reindex_request_ck CHECK (coalesce(requested_generation, 1) >= 1 AND coalesce(requested_shards, 1) BETWEEN 1 AND 1024),
    CONSTRAINT search_reindex_error_ck CHECK (coalesce(length(error), 0) <= 2000 AND coalesce(length(lease_owner), 0) <= 200),
    CONSTRAINT search_reindex_validation_ck CHECK (validation IS NULL OR (jsonb_typeof(validation) = 'object'
        AND pg_column_size(validation) <= 65536))
);

SELECT opportunity.enable_workspace_rls('opportunity.search_reindex');
REVOKE DELETE, TRUNCATE ON opportunity.search_reindex FROM opportunity_app;

-- At most one reindex in flight per workspace; a switched run may still be retaining its old generation.
CREATE UNIQUE INDEX search_reindex_in_flight_uq ON opportunity.search_reindex (workspace_id) WHERE phase BETWEEN 1 AND 5;
CREATE INDEX search_reindex_created_ix ON opportunity.search_reindex (workspace_id, created_at DESC);

-- The column is not named workspace_id: the table is installation-level by design (no RLS), and every workspace_id
-- column marks a tenant table.
CREATE TABLE opportunity.search_reindex_active (
    reindex_workspace_id    uuid        NOT NULL,
    job_id                  uuid        NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT search_reindex_active_pk PRIMARY KEY (reindex_workspace_id, job_id)
);

COMMENT ON TABLE opportunity.search_reindex_active IS
    '@global workspaces with a reindex the coordinator still drives (E07-T11); identifiers only, the state is in search_reindex';
