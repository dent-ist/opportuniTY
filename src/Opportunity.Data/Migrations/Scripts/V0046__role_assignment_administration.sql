-- V0046: role assignment administration (E05-T08, ADR-015 D5.6, D6.5).
-- The role assignments of a workspace are edited as one versioned set: every change locks the workspace row, checks
-- this version (If-Match), re-checks that at least one Workspace Admin assignment remains and bumps the version, all in
-- the transaction that writes the assignments and their audit events. The settings version (row_version) is separate,
-- so a role change never invalidates an open Workspace Settings form.
ALTER TABLE opportunity.workspace
    ADD COLUMN assignment_set_version bigint NOT NULL DEFAULT 1,
    ADD CONSTRAINT workspace_assignment_set_version_ck CHECK (assignment_set_version >= 1);

-- Assignment lookups by role (the last-administrator check) stay on the workspace's own rows.
CREATE INDEX workspace_role_assignment_role_ix ON opportunity.workspace_role_assignment (workspace_id, role);
