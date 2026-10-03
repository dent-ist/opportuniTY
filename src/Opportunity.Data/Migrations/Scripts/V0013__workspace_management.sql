-- V0013: workspace management (E04-T05): storage profile, optimistic version and the membership lookup that backs
-- GET /api/v1/workspaces.
--
-- Role assignments are workspace-scoped under FORCED RLS (V0012, Q-59), so "which workspaces may this principal see"
-- cannot be answered by one query. The lookup below answers it without a second copy of the assignments and without
-- elevated privileges: it walks the (read-open) workspace registry and probes each workspace's assignments inside that
-- workspace's own RLS context, exactly as the PDP does for one workspace. It is SECURITY INVOKER: FORCE RLS binds the
-- owner too, so SECURITY DEFINER would add privilege without adding reach. It puts the caller's context (none, for an
-- installation transaction) back before returning; on error the transaction or the caller's savepoint rolls the
-- transaction-local setting back. (A function-level SET clause would do the restore, but on a custom placeholder it
-- needs superuser or GRANT SET ON PARAMETER, which the migrator login does not have.) It returns workspace ids only;
-- the PDP still decides every per-workspace request.

ALTER TABLE opportunity.workspace
    ADD COLUMN storage_profile text NOT NULL DEFAULT 'default',
    ADD COLUMN row_version bigint NOT NULL DEFAULT 1,
    ADD CONSTRAINT workspace_storage_profile_ck CHECK (storage_profile ~ '^[a-z][a-z0-9-]{0,62}$'),
    ADD CONSTRAINT workspace_row_version_ck CHECK (row_version >= 1);

COMMENT ON COLUMN opportunity.workspace.storage_profile IS
    'Named object-storage profile of the installation (configuration Workspaces:StorageProfiles); never a bucket or key.';

-- ---------------------------------------------------------------------------------------------------------------
-- Membership lookup (ADR-015 D5.2 step 1, mirrored from Opportunity.Security.Authorization.PolicyEvaluator):
-- the workspace is not Deleting/Purged, and the principal holds a role in it directly or through one of its IdP
-- groups; a BreakGlass role counts only while the user has a live activation. Ethical walls and restriction classes
-- hide documents, not workspaces (Q-59), so they play no part here. A test keeps this and the PDP in agreement.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.member_workspace_ids(p_user_id uuid, p_groups text[])
    RETURNS SETOF uuid
    LANGUAGE plpgsql
    VOLATILE
    SECURITY INVOKER
    SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    caller_context text := coalesce(current_setting('app.workspace_id', true), '');
    ws uuid;
BEGIN
    IF p_user_id IS NULL THEN
        RETURN;
    END IF;

    FOR ws IN
        SELECT w.workspace_id FROM opportunity.workspace w WHERE w.status IN ('Active', 'Closed')
    LOOP
        PERFORM set_config('app.workspace_id', ws::text, true);
        IF EXISTS (
            SELECT FROM opportunity.workspace_role_assignment a
            WHERE a.workspace_id = ws
              AND (a.user_id = p_user_id OR a.group_name = ANY (coalesce(p_groups, '{}'::text[])))
              AND (a.role <> 'BreakGlass'
                   OR EXISTS (SELECT FROM opportunity.break_glass_activation b
                              WHERE b.workspace_id = ws AND b.user_id = p_user_id
                                AND b.ended_at IS NULL AND b.expires_at > now())))
        THEN
            RETURN NEXT ws;
        END IF;
    END LOOP;

    PERFORM set_config('app.workspace_id', caller_context, true);
END
$$;

REVOKE EXECUTE ON FUNCTION opportunity.member_workspace_ids(uuid, text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION opportunity.member_workspace_ids(uuid, text[]) TO opportunity_app;

COMMENT ON FUNCTION opportunity.member_workspace_ids(uuid, text[]) IS
    'Workspace ids the principal is a member of (PDP step 1), probed per workspace under its own RLS context. '
    'SECURITY INVOKER; restores the caller''s app.workspace_id before returning. Reviewed for E04-T05.';
