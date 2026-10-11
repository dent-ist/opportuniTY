-- Upgrade test checks (E01-T06), run after the release under test migrated a database loaded with seed.sql.
-- Raises an exception (non-zero psql exit with ON_ERROR_STOP) when the upgrade lost or changed seeded data.
-- Runs as a superuser or the owning migrator login; reads set the workspace context first, as RLS is forced.
DO $$
DECLARE
    failures text[] := '{}';
    actual bigint;
    check_row record;
BEGIN
    PERFORM set_config('app.workspace_id', '00000000-0000-4000-8000-00000000a0a0', true);
    FOR check_row IN
        SELECT * FROM (VALUES
            ('workspace A', 'SELECT count(*) FROM opportunity.workspace WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND name = ''Upgrade test A''', 1),
            ('documents in A', 'SELECT count(*) FROM opportunity.document WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0''', 20),
            ('live projection states in A', 'SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND NOT is_deleted AND document_version = 2', 20),
            ('family member UPG000002', 'SELECT count(*) FROM opportunity.document WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND control_number = ''UPG000002'' AND parent_document_id = ''00000000-0000-4000-8000-00000000d001'' AND family_sequence = 1', 1),
            ('coding fields in A', 'SELECT count(*) FROM opportunity.field_definition WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND storage = 3 AND field_id IN (1000, 1001, 1002) AND NOT is_deleted', 3),
            ('choices in A', 'SELECT count(*) FROM opportunity.choice WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND field_id IN (1000, 1001)', 4),
            ('coded field values in A', 'SELECT count(*) FROM opportunity.document_coding_field WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0''', 26),
            ('Responsive documents', 'SELECT count(*) FROM opportunity.document_coding_choice WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND field_id = 1000 AND choice_id = 1', 10),
            ('Issues choices', 'SELECT count(*) FROM opportunity.document_coding_choice WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND field_id = 1001', 10),
            ('reviewer comment', 'SELECT count(*) FROM opportunity.document_coding_field WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND field_id = 1002 AND value = to_jsonb(''Reviewed before the upgrade''::text)', 1),
            ('saved search', 'SELECT count(*) FROM opportunity.saved_search WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND name = ''Responsive documents''', 1),
            ('role assignment', 'SELECT count(*) FROM opportunity.workspace_role_assignment WHERE workspace_id = ''00000000-0000-4000-8000-00000000a0a0'' AND group_name = ''upgrade-reviewers'' AND role = ''Reviewer''', 1)
        ) AS c(label, query, expected)
    LOOP
        EXECUTE check_row.query INTO actual;
        IF actual IS DISTINCT FROM check_row.expected THEN
            failures := failures || format('%s: expected %s, found %s', check_row.label, check_row.expected, actual);
        END IF;
    END LOOP;

    PERFORM set_config('app.workspace_id', '00000000-0000-4000-8000-00000000b0b0', true);
    SELECT count(*) INTO actual FROM opportunity.document WHERE workspace_id = '00000000-0000-4000-8000-00000000b0b0';
    IF actual <> 3 THEN
        failures := failures || format('documents in B: expected 3, found %s', actual);
    END IF;

    IF cardinality(failures) > 0 THEN
        RAISE EXCEPTION 'upgrade smoke check failed: %', array_to_string(failures, '; ');
    END IF;
    RAISE NOTICE 'upgrade smoke check passed: seeded workspaces, documents, families, coding and saved searches intact';
END
$$;
