-- Demo coding fields and the default coding layout for the developer profile (./opportunity.sh seed, after
-- demo-workspace.sql). Idempotent; synthetic data only. There is no field or layout administration API yet, so without
-- this the demo workspace has nothing to code in Review mode or Mass Edit (vertical slice, baseline §32; E03-T03).
-- Mirrors FieldCatalogRepository: ids from field_catalog_counter, the lowest free coding search slot of the field's
-- kind, capabilities of that slot (ADR-007 R8), and the default layout the workspace provisioning would create.
BEGIN;
SELECT set_config('app.workspace_id', '00000000-0000-4000-8000-00000000d3e0', true);

DO $$
DECLARE
    ws constant uuid := '00000000-0000-4000-8000-00000000d3e0';
    -- name, field_type (1 Text, 6 Boolean, 7 SingleChoice, 8 MultiChoice), slot kind, capabilities, choices
    fields constant jsonb := '[
        {"name": "Responsiveness", "type": 7, "kind": "ch", "caps": 266,
         "choices": ["Responsive", "Not Responsive", "Needs Further Review"]},
        {"name": "Issues", "type": 8, "kind": "ch", "caps": 266, "choices": ["Pricing", "Contracts", "Personnel"]},
        {"name": "Key Document", "type": 6, "kind": "bool", "caps": 267, "choices": []},
        {"name": "Reviewer Comments", "type": 1, "kind": "txt", "caps": 435, "choices": []}
    ]';
    f jsonb;
    choice text;
    new_field integer;
    new_choice integer;
    slot text;
    layout uuid;
    section uuid;
    pos integer := 0;
BEGIN
    INSERT INTO opportunity.field_catalog_counter (workspace_id) VALUES (ws) ON CONFLICT (workspace_id) DO NOTHING;
    INSERT INTO opportunity.coding_layout (workspace_id, layout_id, name, is_default)
    SELECT ws, gen_random_uuid(), 'Default', true
    WHERE NOT EXISTS (SELECT FROM opportunity.coding_layout WHERE workspace_id = ws AND is_default);
    SELECT layout_id INTO layout FROM opportunity.coding_layout WHERE workspace_id = ws AND is_default;

    -- A layout someone already arranged is left alone.
    IF EXISTS (SELECT FROM opportunity.coding_layout_section WHERE workspace_id = ws AND layout_id = layout) THEN
        RETURN;
    END IF;
    section := gen_random_uuid();
    INSERT INTO opportunity.coding_layout_section (workspace_id, layout_id, section_id, title, sort_order)
    VALUES (ws, layout, section, 'Review', 0);

    FOR f IN SELECT * FROM jsonb_array_elements(fields) LOOP
        SELECT d.field_id INTO new_field FROM opportunity.field_definition d
        WHERE d.workspace_id = ws AND d.name_norm = lower(f ->> 'name') AND NOT d.is_deleted AND d.storage = 3;
        IF new_field IS NULL THEN
            UPDATE opportunity.field_catalog_counter SET next_field_id = next_field_id + 1
            WHERE workspace_id = ws RETURNING next_field_id - 1 INTO new_field;
            SELECT s INTO slot
            FROM generate_series(1, 100) n, format('%s.s%s', f ->> 'kind', lpad(n::text, 3, '0')) s
            WHERE NOT EXISTS (SELECT FROM opportunity.field_definition d
                              WHERE d.workspace_id = ws AND d.storage = 3 AND d.search_slot = s)
            ORDER BY n LIMIT 1;
            INSERT INTO opportunity.field_definition (workspace_id, field_id, name, field_type, storage, is_multi_value,
                text_analysis, search_slot, capabilities)
            VALUES (ws, new_field, f ->> 'name', (f ->> 'type')::smallint, 3, (f ->> 'type')::int = 8,
                CASE WHEN (f ->> 'type')::int = 1 THEN 1 END, slot, (f ->> 'caps')::int);
            FOR choice IN SELECT jsonb_array_elements_text(f -> 'choices') LOOP
                UPDATE opportunity.field_catalog_counter SET next_choice_id = next_choice_id + 1
                WHERE workspace_id = ws RETURNING next_choice_id - 1 INTO new_choice;
                INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order)
                VALUES (ws, new_field, new_choice, choice,
                        (SELECT count(*) FROM opportunity.choice c WHERE c.workspace_id = ws AND c.field_id = new_field));
            END LOOP;
        END IF;
        INSERT INTO opportunity.coding_layout_field (workspace_id, layout_id, field_id, section_id, sort_order)
        VALUES (ws, layout, new_field, section, pos);
        pos := pos + 1;
    END LOOP;
END
$$;
SELECT d.field_id, d.name, d.search_slot, string_agg(c.name, ', ' ORDER BY c.sort_order) AS choices
FROM opportunity.field_definition d
LEFT JOIN opportunity.choice c ON c.workspace_id = d.workspace_id AND c.field_id = d.field_id
WHERE d.workspace_id = '00000000-0000-4000-8000-00000000d3e0' AND d.storage = 3
GROUP BY d.field_id, d.name, d.search_slot ORDER BY d.field_id;
COMMIT;
