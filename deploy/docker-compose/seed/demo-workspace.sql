-- Demo data for the developer profile (./opportunity.sh seed). Idempotent; synthetic data only.
-- Runs as the migrator/owner login after the migrator has created the schema. RLS is forced on every workspace table
-- (V0005, ADR-015 D7), so writes set the workspace context transaction-locally, as WorkspaceTransaction does.
BEGIN;
SELECT set_config('app.workspace_id', '00000000-0000-4000-8000-00000000d3e0', true);

INSERT INTO opportunity.workspace (workspace_id, name, matter_number, display_time_zone)
VALUES ('00000000-0000-4000-8000-00000000d3e0', 'Demo workspace', 'DEMO-0001', 'UTC')
ON CONFLICT (workspace_id) DO NOTHING;

-- Roles for the developer IdP's demo groups (keycloak/opportunity-realm.json), so every demo user sees the workspace.
INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, group_name)
SELECT '00000000-0000-4000-8000-00000000d3e0', gen_random_uuid(), r.role, r.group_name
FROM (VALUES ('WorkspaceAdmin', 'workspace-admins'),
             ('Reviewer', 'reviewers'),
             ('PrivilegeReviewer', 'privilege-reviewers'),
             ('Auditor', 'auditors')) AS r(role, group_name)
ON CONFLICT (workspace_id, group_name, role) WHERE group_name IS NOT NULL DO NOTHING;
COMMIT;

SELECT workspace_id, name, matter_number, status FROM opportunity.workspace
WHERE workspace_id = '00000000-0000-4000-8000-00000000d3e0';
