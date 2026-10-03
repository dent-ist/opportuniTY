-- V0008: authoritative authorization state read by the policy decision point (E05-T02, ADR-015 D1.1, D5, D6).
-- Roles and their permission grants are fixed in code (Opportunity.Core.Security.RoleCatalog); PostgreSQL holds the
-- workspace-scoped relations: role assignments (user or IdP group -> role), restriction classes and their grants
-- (Q-11), document restrictions, ethical walls with members and materialized coverage (Q-13), and break-glass
-- activations (Q-45). Every table is workspace-owned with forced RLS (ADR-015 D7): the PDP reads them inside the
-- requested workspace's context, so a membership lookup can never see another workspace's rows.
-- user_id columns reference app_user (installation-level) without a foreign key, like job.initiated_by: tenant FKs are
-- composite on workspace_id (ADR-005 P3).
-- Management APIs for walls, classes and break-glass activation arrive with E05-T06 / E05-T08; this script only adds
-- the state and its invariants.

-- ---------------------------------------------------------------------------------------------------------------
-- Role assignments (ADR-015 D5.6 RoleAssignment(Workspace, User|Group -> Role)). Groups are IdP group names (D3.4).
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.workspace_role_assignment (
    workspace_id        uuid        NOT NULL,
    assignment_id       uuid        NOT NULL,
    role                text        NOT NULL,
    user_id             uuid        NULL,
    group_name          text        NULL,
    assigned_at         timestamptz NOT NULL DEFAULT now(),
    assigned_by         uuid        NULL,
    CONSTRAINT workspace_role_assignment_pk PRIMARY KEY (workspace_id, assignment_id),
    CONSTRAINT workspace_role_assignment_workspace_fk FOREIGN KEY (workspace_id)
        REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT workspace_role_assignment_role_ck CHECK (role IN (
        'WorkspaceAdmin', 'Reviewer', 'QcReviewer', 'PrivilegeReviewer', 'ProductionManager', 'Auditor', 'BreakGlass')),
    CONSTRAINT workspace_role_assignment_principal_ck CHECK ((user_id IS NULL) <> (group_name IS NULL)),
    CONSTRAINT workspace_role_assignment_group_ck CHECK (group_name IS NULL OR length(group_name) BETWEEN 1 AND 256)
);

SELECT opportunity.enable_workspace_rls('opportunity.workspace_role_assignment');

CREATE UNIQUE INDEX workspace_role_assignment_user_uq
    ON opportunity.workspace_role_assignment (workspace_id, user_id, role) WHERE user_id IS NOT NULL;
CREATE UNIQUE INDEX workspace_role_assignment_group_uq
    ON opportunity.workspace_role_assignment (workspace_id, group_name, role) WHERE group_name IS NOT NULL;

-- ---------------------------------------------------------------------------------------------------------------
-- Restriction classes (Q-11, ADR-015 D6.1) and RestrictionClassGrant(Workspace, Class -> Role). A class with no grant
-- row is visible to nobody (default deny); break-glass is never granted a class, it lifts classes while active.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.restriction_class (
    workspace_id        uuid        NOT NULL,
    class_key           text        NOT NULL,
    display_name        text        NOT NULL,
    is_builtin          boolean     NOT NULL DEFAULT false,
    created_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT restriction_class_pk PRIMARY KEY (workspace_id, class_key),
    CONSTRAINT restriction_class_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT restriction_class_key_ck CHECK (class_key ~ '^[A-Za-z][A-Za-z0-9]{0,63}$'),
    CONSTRAINT restriction_class_display_name_ck CHECK (length(btrim(display_name)) BETWEEN 1 AND 100)
);

SELECT opportunity.enable_workspace_rls('opportunity.restriction_class');

CREATE TABLE opportunity.restriction_class_grant (
    workspace_id        uuid        NOT NULL,
    class_key           text        NOT NULL,
    role                text        NOT NULL,
    CONSTRAINT restriction_class_grant_pk PRIMARY KEY (workspace_id, class_key, role),
    CONSTRAINT restriction_class_grant_class_fk FOREIGN KEY (workspace_id, class_key)
        REFERENCES opportunity.restriction_class (workspace_id, class_key) ON DELETE CASCADE,
    CONSTRAINT restriction_class_grant_role_ck CHECK (role IN (
        'WorkspaceAdmin', 'Reviewer', 'QcReviewer', 'PrivilegeReviewer', 'ProductionManager', 'Auditor'))
);

SELECT opportunity.enable_workspace_rls('opportunity.restriction_class_grant');

-- DocumentRestriction (D6.1): changed only in the same transaction as the coding that drives it (§24 rule 1).
CREATE TABLE opportunity.document_restriction (
    workspace_id        uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    class_key           text        NOT NULL,
    applied_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT document_restriction_pk PRIMARY KEY (workspace_id, document_id, class_key),
    CONSTRAINT document_restriction_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT document_restriction_class_fk FOREIGN KEY (workspace_id, class_key)
        REFERENCES opportunity.restriction_class (workspace_id, class_key)
);

SELECT opportunity.enable_workspace_rls('opportunity.document_restriction');

CREATE INDEX document_restriction_class_ix ON opportunity.document_restriction (workspace_id, class_key);

-- ---------------------------------------------------------------------------------------------------------------
-- Ethical walls (Q-13, ADR-015 D6.2): members are users and IdP groups; coverage is materialized per document by the
-- scope maintenance of E05-T06. A wall denies its members every covered document and overrides every grant.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.ethical_wall (
    workspace_id        uuid        NOT NULL,
    wall_id             uuid        NOT NULL,
    name                text        NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    created_by          uuid        NULL,
    CONSTRAINT ethical_wall_pk PRIMARY KEY (workspace_id, wall_id),
    CONSTRAINT ethical_wall_workspace_fk FOREIGN KEY (workspace_id) REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT ethical_wall_name_uq UNIQUE (workspace_id, name),
    CONSTRAINT ethical_wall_name_ck CHECK (length(btrim(name)) BETWEEN 1 AND 200)
);

SELECT opportunity.enable_workspace_rls('opportunity.ethical_wall');

CREATE TABLE opportunity.ethical_wall_member (
    workspace_id        uuid        NOT NULL,
    wall_id             uuid        NOT NULL,
    member_id           uuid        NOT NULL,
    user_id             uuid        NULL,
    group_name          text        NULL,
    added_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ethical_wall_member_pk PRIMARY KEY (workspace_id, wall_id, member_id),
    CONSTRAINT ethical_wall_member_wall_fk FOREIGN KEY (workspace_id, wall_id)
        REFERENCES opportunity.ethical_wall (workspace_id, wall_id) ON DELETE CASCADE,
    CONSTRAINT ethical_wall_member_principal_ck CHECK ((user_id IS NULL) <> (group_name IS NULL)),
    CONSTRAINT ethical_wall_member_group_ck CHECK (group_name IS NULL OR length(group_name) BETWEEN 1 AND 256)
);

SELECT opportunity.enable_workspace_rls('opportunity.ethical_wall_member');

CREATE UNIQUE INDEX ethical_wall_member_user_uq
    ON opportunity.ethical_wall_member (workspace_id, user_id, wall_id) WHERE user_id IS NOT NULL;
CREATE UNIQUE INDEX ethical_wall_member_group_uq
    ON opportunity.ethical_wall_member (workspace_id, group_name, wall_id) WHERE group_name IS NOT NULL;

CREATE TABLE opportunity.document_wall (
    workspace_id        uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    wall_id             uuid        NOT NULL,
    CONSTRAINT document_wall_pk PRIMARY KEY (workspace_id, document_id, wall_id),
    CONSTRAINT document_wall_document_fk FOREIGN KEY (workspace_id, document_id)
        REFERENCES opportunity.document (workspace_id, document_id),
    CONSTRAINT document_wall_wall_fk FOREIGN KEY (workspace_id, wall_id)
        REFERENCES opportunity.ethical_wall (workspace_id, wall_id) ON DELETE CASCADE
);

SELECT opportunity.enable_workspace_rls('opportunity.document_wall');

CREATE INDEX document_wall_wall_ix ON opportunity.document_wall (workspace_id, wall_id);

-- ---------------------------------------------------------------------------------------------------------------
-- Break-glass activations (Q-45, ADR-015 D6.4): reason required, at most 4 hours (default 60 minutes is applied by
-- the activation use case). Effective only together with a BreakGlass role assignment; read-only by permission.
-- ---------------------------------------------------------------------------------------------------------------
CREATE TABLE opportunity.break_glass_activation (
    workspace_id        uuid        NOT NULL,
    activation_id       uuid        NOT NULL,
    user_id             uuid        NOT NULL,
    reason              text        NOT NULL,
    activated_at        timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL,
    ended_at            timestamptz NULL,
    ended_reason        text        NULL,
    CONSTRAINT break_glass_activation_pk PRIMARY KEY (workspace_id, activation_id),
    CONSTRAINT break_glass_activation_workspace_fk FOREIGN KEY (workspace_id)
        REFERENCES opportunity.workspace (workspace_id),
    CONSTRAINT break_glass_activation_reason_ck CHECK (length(btrim(reason)) BETWEEN 1 AND 2000),
    CONSTRAINT break_glass_activation_duration_ck CHECK (
        expires_at > activated_at AND expires_at <= activated_at + interval '4 hours'),
    CONSTRAINT break_glass_activation_ended_ck CHECK ((ended_at IS NULL) = (ended_reason IS NULL)),
    CONSTRAINT break_glass_activation_ended_reason_ck CHECK (ended_reason IS NULL OR ended_reason IN ('Ended', 'Revoked'))
);

SELECT opportunity.enable_workspace_rls('opportunity.break_glass_activation');

CREATE INDEX break_glass_activation_user_ix
    ON opportunity.break_glass_activation (workspace_id, user_id, expires_at) WHERE ended_at IS NULL;

-- An activation is evidence (Q-13 break-glass report): it can be ended, never rewritten or removed.
REVOKE DELETE ON opportunity.break_glass_activation FROM opportunity_app;

CREATE FUNCTION opportunity.break_glass_activation_immutable()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.ended_at IS NOT NULL
       OR NEW.workspace_id IS DISTINCT FROM OLD.workspace_id OR NEW.activation_id IS DISTINCT FROM OLD.activation_id
       OR NEW.user_id IS DISTINCT FROM OLD.user_id OR NEW.reason IS DISTINCT FROM OLD.reason
       OR NEW.activated_at IS DISTINCT FROM OLD.activated_at OR NEW.expires_at > OLD.expires_at THEN
        RAISE EXCEPTION 'Break-glass activation % can only be ended or shortened', OLD.activation_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER break_glass_activation_immutable
    BEFORE UPDATE ON opportunity.break_glass_activation
    FOR EACH ROW EXECUTE FUNCTION opportunity.break_glass_activation_immutable();

-- ---------------------------------------------------------------------------------------------------------------
-- Built-in classes and default grants (ADR-015 D6.1; mirrors Opportunity.Core.Security.RestrictionClasses, checked by
-- a test). Seeded when a workspace is created, under that workspace's context, and backfilled here.
-- ---------------------------------------------------------------------------------------------------------------
CREATE FUNCTION opportunity.seed_workspace_security(p_workspace_id uuid)
    RETURNS void
    LANGUAGE sql
AS $$
    INSERT INTO opportunity.restriction_class (workspace_id, class_key, display_name, is_builtin)
    VALUES (p_workspace_id, 'Privileged', 'Privileged', true),
           (p_workspace_id, 'Confidential', 'Confidential', true),
           (p_workspace_id, 'AttorneysEyesOnly', 'Attorneys'' Eyes Only', true)
    ON CONFLICT DO NOTHING;

    INSERT INTO opportunity.restriction_class_grant (workspace_id, class_key, role)
    SELECT p_workspace_id, g.class_key, g.role
    FROM (VALUES
        ('Privileged', 'WorkspaceAdmin'), ('Privileged', 'Reviewer'), ('Privileged', 'QcReviewer'),
        ('Privileged', 'PrivilegeReviewer'), ('Privileged', 'ProductionManager'), ('Privileged', 'Auditor'),
        ('Confidential', 'WorkspaceAdmin'), ('Confidential', 'Reviewer'), ('Confidential', 'QcReviewer'),
        ('Confidential', 'PrivilegeReviewer'), ('Confidential', 'ProductionManager'), ('Confidential', 'Auditor'),
        ('AttorneysEyesOnly', 'WorkspaceAdmin'), ('AttorneysEyesOnly', 'PrivilegeReviewer'),
        ('AttorneysEyesOnly', 'ProductionManager')
    ) AS g (class_key, role)
    ON CONFLICT DO NOTHING;
$$;

CREATE FUNCTION opportunity.workspace_seed_security()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM opportunity.seed_workspace_security(NEW.workspace_id);
    RETURN NULL;
END
$$;

CREATE TRIGGER workspace_seed_security
    AFTER INSERT ON opportunity.workspace
    FOR EACH ROW EXECUTE FUNCTION opportunity.workspace_seed_security();

-- FORCE RLS binds the migrator too, so the backfill sets each workspace's context (transaction-local).
DO $$
DECLARE
    ws uuid;
BEGIN
    FOR ws IN SELECT workspace_id FROM opportunity.workspace LOOP
        PERFORM set_config('app.workspace_id', ws::text, true);
        PERFORM opportunity.seed_workspace_security(ws);
    END LOOP;
    PERFORM set_config('app.workspace_id', '', true);
END
$$;
