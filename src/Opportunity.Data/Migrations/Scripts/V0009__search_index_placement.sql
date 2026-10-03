-- V0009: search index placement (E07-T01, ADR-006 R2/R6).
-- PostgreSQL is authoritative for where each workspace's projection lives. Rows hold placement facts only (tier, shared
-- pool number, projection generation, shard count); Opportunity.Search derives physical index and alias names from them,
-- so no physical name is ever stored or leaves the search module (ADR-006 R1).
-- Enumerations are smallint; the code values live in Opportunity.Application.Search.Indexing.

-- ---------------------------------------------------------------------------------------------------------------
-- Shared index pool (ADR-006 R6). Installation-level: one row per shared index family {prefix}-shared-{nnn}.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.search_shared_index (
    pool_number         integer     NOT NULL,
    generation          integer     NOT NULL,
    primary_shards      integer     NOT NULL,
    closed              boolean     NOT NULL DEFAULT false,
    workspace_count     integer     NOT NULL DEFAULT 0,
    assigned_bytes      bigint      NOT NULL DEFAULT 0,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT search_shared_index_pk PRIMARY KEY (pool_number),
    CONSTRAINT search_shared_index_pool_ck CHECK (pool_number BETWEEN 1 AND 999),
    CONSTRAINT search_shared_index_generation_ck CHECK (generation >= 1),
    CONSTRAINT search_shared_index_shards_ck CHECK (primary_shards >= 1),
    CONSTRAINT search_shared_index_counters_ck CHECK (workspace_count >= 0 AND assigned_bytes >= 0)
);

COMMENT ON TABLE opportunity.search_shared_index IS '@global shared OpenSearch index pool, spans workspaces (ADR-006 R6)';

-- ---------------------------------------------------------------------------------------------------------------
-- Workspace placement (ADR-006 R2). kind 1 Shared / 2 Dedicated; state 1 Active / 2 Building / 3 Moving / 4 Deleting.
-- pending_* describe the target of a running rebuild or move (dual-target writes, ADR-006 R12/R13).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.workspace_index_placement (
    workspace_id            uuid        NOT NULL,
    kind                    smallint    NOT NULL,
    shared_pool             integer     NULL,
    generation              integer     NOT NULL,
    primary_shards          integer     NOT NULL DEFAULT 1,
    state                   smallint    NOT NULL DEFAULT 1,
    pending_kind            smallint    NULL,
    pending_shared_pool     integer     NULL,
    pending_generation      integer     NULL,
    pending_primary_shards  integer     NULL,
    dedicated_requested     boolean     NOT NULL DEFAULT false,
    estimated_documents     bigint      NOT NULL DEFAULT 0,
    estimated_bytes         bigint      NOT NULL DEFAULT 0,
    row_version             bigint      NOT NULL DEFAULT 1,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT workspace_index_placement_pk PRIMARY KEY (workspace_id),
    CONSTRAINT workspace_index_placement_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT workspace_index_placement_pool_fk FOREIGN KEY (shared_pool) REFERENCES opportunity.search_shared_index (pool_number),
    CONSTRAINT workspace_index_placement_pending_pool_fk
        FOREIGN KEY (pending_shared_pool) REFERENCES opportunity.search_shared_index (pool_number),
    CONSTRAINT workspace_index_placement_kind_ck CHECK (kind IN (1, 2) AND (kind = 1) = (shared_pool IS NOT NULL)),
    CONSTRAINT workspace_index_placement_state_ck CHECK (state BETWEEN 1 AND 4),
    CONSTRAINT workspace_index_placement_generation_ck CHECK (generation >= 1 AND primary_shards >= 1),
    -- A rebuild or move always names its complete target; any other state has none. (COALESCE: a CHECK passes on NULL.)
    CONSTRAINT workspace_index_placement_pending_ck CHECK (
        CASE WHEN state IN (2, 3)
            THEN COALESCE(pending_kind IN (1, 2) AND (pending_kind = 1) = (pending_shared_pool IS NOT NULL)
                 AND pending_generation >= 1 AND pending_primary_shards >= 1, false)
            ELSE pending_kind IS NULL AND pending_shared_pool IS NULL AND pending_generation IS NULL
                 AND pending_primary_shards IS NULL
        END),
    CONSTRAINT workspace_index_placement_estimates_ck CHECK (estimated_documents >= 0 AND estimated_bytes >= 0)
);

SELECT opportunity.enable_workspace_rls('opportunity.workspace_index_placement');
