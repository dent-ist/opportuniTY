-- V0045: privilege designations as system fields (E13-T01, #109; §14, §15, Q-11, Q-20; ADR-015 D6.1).
-- * Every workspace gets five system coding fields (ids 37–41, renameable and hideable, never retyped or deleted):
--   Privilege Status (single choice: Not Privileged / Withhold / Redact / Needs 2L Review), Privilege Basis (multiple
--   choice: Attorney-Client / Work Product / Common Interest / Other, admins may add more), Privilege Description
--   (log-ready long text), Attorneys Involved (multi-value keyword) and Log Category (single choice, choices added by
--   admins). All five are security-affecting (class PrivilegeStatus), so coding them needs Coding.WritePrivilege and
--   takes the security lanes.
-- * choice.system_key names the built-in choices whose meaning the application relies on (Withhold and Redact need a
--   basis; Withhold blocks finalizing a production). A system choice can be renamed and reordered but never deleted,
--   deactivated or re-keyed.
-- * The built-in restriction class Privileged (V0012) is bound to Withhold, Redact and Needs 2L Review through the
--   #51 binding (restriction_class_rule, V0044) when the status field is provisioned; admins may change the binding.
-- * opportunity.provision_privilege_fields(ws) is idempotent: the application calls it whenever it ensures a
--   workspace's system fields, and this migration backfills every existing workspace. A display name a custom field
--   already uses (or whose query name it would take) gets the suffix " (System)"; the custom field is left alone.

ALTER TABLE opportunity.choice
    ADD COLUMN system_key text NULL,
    ADD CONSTRAINT choice_system_key_ck CHECK (system_key IS NULL OR system_key ~ '^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$');

CREATE UNIQUE INDEX choice_system_key_uq ON opportunity.choice (workspace_id, system_key) WHERE system_key IS NOT NULL;

CREATE FUNCTION opportunity.choice_system_guard()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.system_key IS NOT NULL THEN
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
$$;

CREATE TRIGGER choice_system_guard
    BEFORE UPDATE OR DELETE ON opportunity.choice
    FOR EACH ROW EXECUTE FUNCTION opportunity.choice_system_guard();

-- Creates the privilege fields the workspace does not have yet (in the caller's workspace context, SECURITY INVOKER)
-- and returns how many it created. Mirrors Opportunity.Core.Fields.PrivilegeFields (checked by a test). Search slots:
-- a reserved slot at the top of the coding namespace's budget, else the lowest free one, else the overflow (ADR-007
-- R4/R6); capabilities as FieldRules.CapabilitiesForSlot.
CREATE FUNCTION opportunity.provision_privilege_fields(p_ws uuid)
    RETURNS integer
    LANGUAGE plpgsql
AS $$
DECLARE
    f record;
    c record;
    v_candidate text;
    v_name text;
    v_slot text;
    v_choice integer;
    v_created integer := 0;
BEGIN
    INSERT INTO opportunity.field_catalog_counter (workspace_id) VALUES (p_ws) ON CONFLICT (workspace_id) DO NOTHING;
    -- Serializes concurrent provisioning (and choice id allocation) of one workspace.
    PERFORM 1 FROM opportunity.field_catalog_counter WHERE workspace_id = p_ws FOR UPDATE;

    FOR f IN
        SELECT * FROM (VALUES
            (37, 'Privilege Status', 7::smallint, false, NULL::smallint, 'ch', 100, 'ch.s100',
             'Privilege call: Not Privileged, Withhold, Redact or Needs 2L Review. Withhold and Redact need a Privilege Basis.'),
            (38, 'Privilege Basis', 8::smallint, true, NULL::smallint, 'ch', 100, 'ch.s099',
             'Why the document is withheld or redacted. Shown on the privilege log.'),
            (39, 'Privilege Description', 1::smallint, false, 1::smallint, 'txt', 20, 'txt.s020',
             'Log-ready description of the withheld or redacted content.'),
            (40, 'Attorneys Involved', 2::smallint, true, NULL::smallint, 'kw', 20, 'kw.s020',
             'Attorneys whose involvement supports the privilege claim.'),
            (41, 'Log Category', 7::smallint, false, NULL::smallint, 'ch', 100, 'ch.s098',
             'Privilege log category.')
        ) AS t (field_id, name, field_type, is_multi_value, text_analysis, kind, budget, reserved_slot, description)
        ORDER BY field_id
    LOOP
        CONTINUE WHEN EXISTS (SELECT FROM opportunity.field_definition d WHERE d.workspace_id = p_ws AND d.field_id = f.field_id);

        v_name := NULL;
        FOREACH v_candidate IN ARRAY ARRAY[f.name, f.name || ' (System)'] LOOP
            IF NOT EXISTS (
                SELECT FROM opportunity.field_definition d
                WHERE d.workspace_id = p_ws AND NOT d.is_deleted
                  AND (d.name_norm = lower(btrim(v_candidate))
                       OR btrim(regexp_replace(lower(d.name), '[^[:alnum:]]+', '_', 'g'), '_')
                          = btrim(regexp_replace(lower(v_candidate), '[^[:alnum:]]+', '_', 'g'), '_'))) THEN
                v_name := v_candidate;
                EXIT;
            END IF;
        END LOOP;
        IF v_name IS NULL THEN
            RAISE WARNING 'Workspace %: privilege field % not provisioned, its names are taken', p_ws, f.field_id;
            CONTINUE;
        END IF;

        -- The reserved slot at the top of the kind's budget, so lowest-free allocation of custom fields is unaffected;
        -- in a workspace whose custom fields already hold it, the lowest free slot.
        SELECT s.slot INTO v_slot
        FROM generate_series(1, f.budget) AS n
        CROSS JOIN LATERAL (SELECT f.kind || '.s' || lpad(n::text, 3, '0') AS slot) s
        WHERE NOT EXISTS (SELECT FROM opportunity.field_definition d
                          WHERE d.workspace_id = p_ws AND d.storage = 3 AND d.search_slot = s.slot)
        ORDER BY s.slot = f.reserved_slot DESC, n
        LIMIT 1;
        v_slot := coalesce(v_slot, 'overflow');

        INSERT INTO opportunity.field_definition (
            workspace_id, field_id, name, description, field_type, storage, is_system, is_multi_value, text_analysis,
            is_security_affecting, security_class, is_searchable, search_slot, capabilities)
        VALUES (
            p_ws, f.field_id, v_name, f.description, f.field_type, 3, true, f.is_multi_value, f.text_analysis,
            true, 1, true, v_slot,
            CASE
                WHEN v_slot = 'overflow' THEN 2 | 32 | 256
                WHEN f.kind = 'ch' THEN 2 | 256 | 8
                WHEN f.kind = 'txt' THEN 2 | 256 | 1 | 16 | 32 | 128
                ELSE 2 | 256 | 1 | 8 | 32
            END);
        v_created := v_created + 1;

        FOR c IN
            SELECT * FROM (VALUES
                (37, 0, 'Not Privileged', 'privilege-status.not-privileged'),
                (37, 1, 'Withhold', 'privilege-status.withhold'),
                (37, 2, 'Redact', 'privilege-status.redact'),
                (37, 3, 'Needs 2L Review', 'privilege-status.needs-2l-review'),
                (38, 0, 'Attorney-Client', 'privilege-basis.attorney-client'),
                (38, 1, 'Work Product', 'privilege-basis.work-product'),
                (38, 2, 'Common Interest', 'privilege-basis.common-interest'),
                (38, 3, 'Other', 'privilege-basis.other')
            ) AS t (field_id, sort_order, name, system_key)
            WHERE t.field_id = f.field_id
            ORDER BY sort_order
        LOOP
            UPDATE opportunity.field_catalog_counter SET next_choice_id = next_choice_id + 1
            WHERE workspace_id = p_ws RETURNING next_choice_id - 1 INTO v_choice;
            INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order, system_key)
            VALUES (p_ws, f.field_id, v_choice, c.name, c.sort_order, c.system_key);
        END LOOP;

        IF f.field_id = 37 THEN
            INSERT INTO opportunity.restriction_class_rule (workspace_id, class_key, field_id, choice_id)
            SELECT p_ws, 'Privileged', ch.field_id, ch.choice_id
            FROM opportunity.choice ch
            WHERE ch.workspace_id = p_ws AND ch.field_id = 37
              AND ch.system_key IN ('privilege-status.withhold', 'privilege-status.redact', 'privilege-status.needs-2l-review')
              AND EXISTS (SELECT FROM opportunity.restriction_class r WHERE r.workspace_id = p_ws AND r.class_key = 'Privileged')
            ON CONFLICT DO NOTHING;
        END IF;
    END LOOP;

    RETURN v_created;
END
$$;

REVOKE ALL ON FUNCTION opportunity.provision_privilege_fields(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION opportunity.provision_privilege_fields(uuid) TO opportunity_app;

-- Backfill: FORCE RLS binds the migrator too, so each workspace's context is set (transaction-local) first. New
-- fields hold no values, so no document's restriction classes or projection change.
DO $$
DECLARE
    ws uuid;
BEGIN
    FOR ws IN SELECT workspace_id FROM opportunity.workspace ORDER BY workspace_id LOOP
        PERFORM set_config('app.workspace_id', ws::text, true);
        PERFORM opportunity.provision_privilege_fields(ws);
    END LOOP;
    PERFORM set_config('app.workspace_id', '', true);
END
$$;
