-- V0070: backup, disaster recovery and restore consistency (E19-T08, #164; ADR-016, docs/architecture/restore-consistency.md).
--
-- * Search snapshot anchors (ADR-016 §3.2, design §3): before the snapshot job starts an OpenSearch snapshot it records
--   the snapshot (installation-level row) and, per workspace, the visible search watermark G_s plus the placement
--   state. The anchors are committed before the snapshot starts, so every snapshot usable for a restore (end_time + ε
--   <= T_r) still has its anchors after a point-in-time restore of PostgreSQL. Catch-up replays the work above G_s.
-- * Restore runs (ADR-016 §8): one installation-level row per restore, written by the `restore` operations CLI, so the
--   reconcile phase is resumable and the restore record names what was done.
-- * Audit taxonomy (ADR-016 §5.3): Integrity.Restored is written to the system chain and to every workspace chain
--   before reopening, and the sealer then signs a checkpoint with the new reason AfterRestore.
-- * A point-in-time restore revokes every browser session with the new reason 'Restore' (ADR-016 §7.1).

INSERT INTO audit.audit_action (category, action) VALUES
    ('Integrity', 'Restored');

ALTER TABLE audit.checkpoint DROP CONSTRAINT checkpoint_reason_ck;
ALTER TABLE audit.checkpoint ADD CONSTRAINT checkpoint_reason_ck
    CHECK (reason IN ('Scheduled', 'Manual', 'BeforePurge', 'BeforeDeletion', 'MatterClosed', 'AfterRestore'));

ALTER TABLE opportunity.user_session DROP CONSTRAINT user_session_revoked_reason_ck;
ALTER TABLE opportunity.user_session ADD CONSTRAINT user_session_revoked_reason_ck CHECK (revoked_reason IS NULL OR revoked_reason IN
    ('SignOut', 'IdleTimeout', 'AbsoluteTimeout', 'BackChannelLogout', 'PrincipalRefreshFailed', 'Replaced', 'Restore'));

-- ---------------------------------------------------------------------------------------------------------------
-- One row per OpenSearch snapshot the snapshot job requested. The repository stays the authority on the outcome: a
-- database restored to a time before the outcome was recorded still finds the snapshot by name in the repository.
-- state: Requested (anchors written, snapshot not yet confirmed), Success, Partial, Failed, Deleted.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.search_snapshot (
    snapshot_id     uuid        NOT NULL,
    snapshot_name   text        NOT NULL,
    repository      text        NOT NULL,
    requested_at    timestamptz NOT NULL DEFAULT now(),
    state           text        NOT NULL DEFAULT 'Requested',
    started_at      timestamptz NULL,
    ended_at        timestamptz NULL,
    indices         integer     NULL,
    -- Workspaces anchored by this snapshot (rows in search_snapshot_anchor).
    workspaces      integer     NOT NULL DEFAULT 0,
    error           text        NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT search_snapshot_pk PRIMARY KEY (snapshot_id),
    CONSTRAINT search_snapshot_name_uq UNIQUE (repository, snapshot_name),
    CONSTRAINT search_snapshot_name_ck CHECK (snapshot_name ~ '^[a-z0-9][a-z0-9._-]{0,199}$'),
    CONSTRAINT search_snapshot_repository_ck CHECK (repository ~ '^[a-z0-9][a-z0-9._-]{0,99}$'),
    CONSTRAINT search_snapshot_state_ck CHECK (state IN ('Requested', 'Success', 'Partial', 'Failed', 'Deleted')),
    CONSTRAINT search_snapshot_times_ck CHECK (ended_at IS NULL OR started_at IS NULL OR ended_at >= started_at),
    CONSTRAINT search_snapshot_error_ck CHECK (length(error) <= 2000)
);

COMMENT ON TABLE opportunity.search_snapshot IS
    '@global OpenSearch snapshots requested by the snapshot job (E19-T08, ADR-016 §3); names and times only, the anchors are per workspace';

CREATE INDEX search_snapshot_requested_ix ON opportunity.search_snapshot (requested_at DESC);

-- ---------------------------------------------------------------------------------------------------------------
-- Per-workspace anchor of a snapshot: the visible watermark (indexed_through_generation) and the placement at capture
-- time. A workspace created after the snapshot started has no row (its anchor reads as 0). Rows expire with the
-- snapshot (ON DELETE CASCADE).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.search_snapshot_anchor (
    workspace_id                uuid        NOT NULL,
    snapshot_id                 uuid        NOT NULL,
    indexed_through_generation  bigint      NOT NULL,
    -- The workspace's SearchGeneration counter at capture (informational: the replay size is roughly value - anchor).
    search_generation           bigint      NULL,
    -- workspace_index_placement at capture: 1 Shared / 2 Dedicated; 1 Active, 2 Building, 3 Moving, 4 other.
    placement_kind              smallint    NULL,
    placement_state             smallint    NULL,
    placement_generation        integer     NULL,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT search_snapshot_anchor_pk PRIMARY KEY (workspace_id, snapshot_id),
    CONSTRAINT search_snapshot_anchor_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT search_snapshot_anchor_snapshot_fk FOREIGN KEY (snapshot_id)
        REFERENCES opportunity.search_snapshot (snapshot_id) ON DELETE CASCADE,
    CONSTRAINT search_snapshot_anchor_generation_ck CHECK (indexed_through_generation >= 0
        AND (search_generation IS NULL OR search_generation >= 0)),
    CONSTRAINT search_snapshot_anchor_placement_ck CHECK ((placement_kind IS NULL OR placement_kind IN (1, 2))
        AND (placement_state IS NULL OR placement_state BETWEEN 1 AND 4)
        AND (placement_generation IS NULL OR placement_generation >= 1))
);

SELECT opportunity.enable_workspace_rls('opportunity.search_snapshot_anchor');

-- No index leads with snapshot_id (tenant-key rule P1): expiry cascades scan the table, which holds one row per
-- workspace per retained snapshot.

REVOKE UPDATE, TRUNCATE ON opportunity.search_snapshot_anchor FROM opportunity_app;

-- ---------------------------------------------------------------------------------------------------------------
-- Restore runs (ADR-016 §8). path: EndOfWal (path 0), Snapshot (path A), KeepLive (path B), Rebuild (path C).
-- phase: Reconciling -> Reconciled -> Recorded -> Verified. Never deleted.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.restore_run (
    restore_id          uuid        NOT NULL,
    path                text        NOT NULL,
    target_time         timestamptz NULL,
    backup_label        text        NULL,
    snapshot_name       text        NULL,
    operator_name       text        NOT NULL,
    phase               text        NOT NULL DEFAULT 'Reconciling',
    started_at          timestamptz NOT NULL DEFAULT now(),
    reconciled_at       timestamptz NULL,
    recorded_at         timestamptz NULL,
    verified_at         timestamptz NULL,
    record_key          text        NULL,
    record_sha256       text        NULL,
    -- Counts per workspace and the lost-window findings (identifiers and counts only, never content).
    report              jsonb       NULL,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT restore_run_pk PRIMARY KEY (restore_id),
    CONSTRAINT restore_run_path_ck CHECK (path IN ('EndOfWal', 'Snapshot', 'KeepLive', 'Rebuild')),
    CONSTRAINT restore_run_phase_ck CHECK (phase IN ('Reconciling', 'Reconciled', 'Recorded', 'Verified')),
    CONSTRAINT restore_run_snapshot_ck CHECK ((path = 'Snapshot') = (snapshot_name IS NOT NULL)),
    CONSTRAINT restore_run_text_ck CHECK (length(operator_name) BETWEEN 1 AND 200 AND coalesce(length(backup_label), 0) <= 200
        AND coalesce(length(snapshot_name), 0) <= 200 AND coalesce(length(record_key), 0) <= 512
        AND (record_sha256 IS NULL OR record_sha256 ~ '^[0-9a-f]{64}$')),
    CONSTRAINT restore_run_report_ck CHECK (report IS NULL OR (jsonb_typeof(report) = 'object' AND pg_column_size(report) <= 1048576))
);

COMMENT ON TABLE opportunity.restore_run IS
    '@global restores of the installation (E19-T08, ADR-016 §8): target time, backup label, snapshot and the reconcile report';

REVOKE DELETE, TRUNCATE ON opportunity.restore_run FROM opportunity_app;
