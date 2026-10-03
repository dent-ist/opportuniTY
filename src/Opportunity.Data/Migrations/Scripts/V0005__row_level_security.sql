-- V0005: PostgreSQL row-level security on every workspace-owned table (E05-T03).
-- Binding: ADR-015 D7. The workspace context is app.workspace_id, set transaction-locally by Opportunity.Data's
-- WorkspaceTransaction only. RLS is FORCEd, so it binds the owning (migrator) login too; only superusers and BYPASSRLS
-- roles skip it, and runtime roles are neither. Foreign-key checks and cascades are not subject to RLS, so composite
-- FKs keep working across the policy boundary. From this version on the migrator lints that every table with a
-- workspace_id column has RLS enabled, forced and keyed on app.workspace_id (RowLevelSecurityLint).

-- ---------------------------------------------------------------------------------------------------------------
-- The one isolation policy (ADR-015 D7.1). An unset or empty context matches no row and admits no write. Later
-- migrations call this for each new tenant table, right after CREATE TABLE. Owner-only: runtime roles cannot run it.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.enable_workspace_rls(p_table regclass)
    RETURNS void
    LANGUAGE plpgsql
    SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY, FORCE ROW LEVEL SECURITY', p_table);
    IF NOT EXISTS (SELECT FROM pg_policy WHERE polrelid = p_table AND polname = 'workspace_isolation') THEN
        EXECUTE format(
            'CREATE POLICY workspace_isolation ON %s AS PERMISSIVE FOR ALL TO PUBLIC '
            || 'USING (workspace_id = NULLIF(current_setting(''app.workspace_id'', true), '''')::uuid) '
            || 'WITH CHECK (workspace_id = NULLIF(current_setting(''app.workspace_id'', true), '''')::uuid)',
            p_table);
    END IF;
END
$$;

REVOKE EXECUTE ON FUNCTION opportunity.enable_workspace_rls(regclass) FROM PUBLIC, opportunity_app;

SELECT opportunity.enable_workspace_rls(t)
FROM unnest(ARRAY[
    -- V0002
    'opportunity.document', 'opportunity.retired_control_number', 'opportunity.document_projection_state',
    'opportunity.stored_object', 'opportunity.page_set', 'opportunity.page', 'opportunity.page_image',
    -- V0003
    'opportunity.field_catalog_counter', 'opportunity.field_definition', 'opportunity.choice',
    'opportunity.coding_layout', 'opportunity.coding_layout_section', 'opportunity.coding_layout_field',
    'opportunity.coding_layout_role',
    -- V0004 (coding_event partitions below)
    'opportunity.document_coding_field', 'opportunity.document_coding_choice', 'opportunity.coding_write',
    'opportunity.coding_event'
]::regclass[]) AS t;

-- ---------------------------------------------------------------------------------------------------------------
-- Workspace registry (ADR-015 D7.1 installation-level allow-list): readable without a context so the PDP can resolve
-- a user's workspaces before one is chosen; every write needs the context of the workspace being written. That also
-- keeps the case-sensitivity trigger honest: it counts documents under the same workspace's context.
-- ---------------------------------------------------------------------------------------------------------------
ALTER TABLE opportunity.workspace ENABLE ROW LEVEL SECURITY, FORCE ROW LEVEL SECURITY;

CREATE POLICY workspace_registry_read ON opportunity.workspace
    AS PERMISSIVE FOR SELECT TO PUBLIC
    USING (true);
CREATE POLICY workspace_isolation_insert ON opportunity.workspace
    AS PERMISSIVE FOR INSERT TO PUBLIC
    WITH CHECK (workspace_id = NULLIF(current_setting('app.workspace_id', true), '')::uuid);
CREATE POLICY workspace_isolation_update ON opportunity.workspace
    AS PERMISSIVE FOR UPDATE TO PUBLIC
    USING (workspace_id = NULLIF(current_setting('app.workspace_id', true), '')::uuid)
    WITH CHECK (workspace_id = NULLIF(current_setting('app.workspace_id', true), '')::uuid);
CREATE POLICY workspace_isolation_delete ON opportunity.workspace
    AS PERMISSIVE FOR DELETE TO PUBLIC
    USING (workspace_id = NULLIF(current_setting('app.workspace_id', true), '')::uuid);

COMMENT ON TABLE opportunity.workspace IS
    'Workspace registry. RLS: reads are open (installation-level, ADR-015 D7.1), writes need the workspace context.';

-- ---------------------------------------------------------------------------------------------------------------
-- Partitions (ADR-015 D7.4.1): runtime roles reach partitioned data only through the parent, whose policy applies;
-- a child partition would skip it, so they hold no privilege on any child. Children also carry the policy.
-- ---------------------------------------------------------------------------------------------------------------
DO $$
DECLARE
    child regclass;
BEGIN
    FOR child IN
        SELECT c.oid::regclass
        FROM pg_class c
        WHERE c.relnamespace = 'opportunity'::regnamespace AND c.relispartition AND c.relkind IN ('r', 'p')
    LOOP
        EXECUTE format('REVOKE ALL ON %s FROM opportunity_app, opportunity_readonly', child);
        PERFORM opportunity.enable_workspace_rls(child);
    END LOOP;
END
$$;

CREATE OR REPLACE FUNCTION opportunity.coding_event_ensure_partitions(p_through timestamptz)
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
            EXECUTE format('REVOKE ALL ON opportunity.%I FROM opportunity_app, opportunity_readonly', partition);
            PERFORM opportunity.enable_workspace_rls(format('opportunity.%I', partition)::regclass);
            created := created + 1;
        END IF;
        month_start := (month_start + interval '1 month')::date;
    END LOOP;
    RETURN created;
END
$$;

-- ADR-015 D7.4.4: SECURITY DEFINER functions are allow-listed by this marker and must pin search_path (linted).
COMMENT ON FUNCTION opportunity.coding_event_ensure_partitions(timestamptz) IS
    '@security-definer creates coding_event partitions ahead for the maintenance job; DDL only, reads no tenant rows.';
