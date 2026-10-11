-- Upgrade test data (E01-T06), loaded into the N-1 schema before the release under test migrates it. Synthetic only.
-- Used by tools/release/upgrade-test.sh (psql) and tests/Opportunity.IntegrationTests/Migrations/UpgradeTests.cs.
--
-- Written against the oldest schema in tools/release/upgrade/baseline.env and kept to long-lived core tables
-- (workspaces, role assignments, fields/choices, documents and families, coding, saved searches), so it loads into
-- every later schema as well: expand/contract migrations only add columns with defaults (Migrations/README.md).
-- Runs as a superuser or the owning migrator login. RLS is forced, so each block sets the workspace context first.
-- Not idempotent: load it into a fresh database.

-- Workspace A: 20 documents (one family), three coding fields, coding on every document, a saved search.
BEGIN;
SELECT set_config('app.workspace_id', '00000000-0000-4000-8000-00000000a0a0', true);

INSERT INTO opportunity.workspace (workspace_id, name, matter_number, display_time_zone)
VALUES ('00000000-0000-4000-8000-00000000a0a0', 'Upgrade test A', 'UPG-0001', 'UTC');

INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, group_name)
VALUES ('00000000-0000-4000-8000-00000000a0a0', '00000000-0000-4000-8000-0000000a0001', 'Reviewer', 'upgrade-reviewers');

INSERT INTO opportunity.field_catalog_counter (workspace_id, next_field_id, next_choice_id)
VALUES ('00000000-0000-4000-8000-00000000a0a0', 1003, 5);

-- field_type 7 SingleChoice, 8 MultiChoice, 1 Text; storage 3 Coding; slots and capabilities as the field catalogue
-- assigns them (ADR-007 R8, deploy/docker-compose/seed/demo-coding.sql).
INSERT INTO opportunity.field_definition (workspace_id, field_id, name, field_type, storage, is_multi_value,
    text_analysis, search_slot, capabilities)
VALUES ('00000000-0000-4000-8000-00000000a0a0', 1000, 'Responsiveness', 7, 3, false, NULL, 'ch.s001', 266),
       ('00000000-0000-4000-8000-00000000a0a0', 1001, 'Issues', 8, 3, true, NULL, 'ch.s002', 266),
       ('00000000-0000-4000-8000-00000000a0a0', 1002, 'Reviewer Comments', 1, 3, false, 1, 'txt.s001', 435);

INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order)
VALUES ('00000000-0000-4000-8000-00000000a0a0', 1000, 1, 'Responsive', 0),
       ('00000000-0000-4000-8000-00000000a0a0', 1000, 2, 'Not Responsive', 1),
       ('00000000-0000-4000-8000-00000000a0a0', 1001, 3, 'Pricing', 0),
       ('00000000-0000-4000-8000-00000000a0a0', 1001, 4, 'Contracts', 1);

-- UPG000001 is an email whose attachment is UPG000002; the others are standalone (a family of one).
INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id,
    parent_document_id, family_sequence, file_name, file_extension, page_count, date_sent, metadata)
SELECT '00000000-0000-4000-8000-00000000a0a0', d.id, d.cn, d.cn,
       CASE WHEN n = 2 THEN '00000000-0000-4000-8000-00000000d001'::uuid ELSE d.id END,
       CASE WHEN n = 2 THEN '00000000-0000-4000-8000-00000000d001'::uuid END,
       CASE WHEN n = 2 THEN 1 ELSE 0 END,
       'upgrade-' || n || '.txt', 'txt', 1, timestamptz '2024-01-01 09:00:00+00' + n * interval '1 day', '{}'
FROM generate_series(1, 20) AS n,
     LATERAL (SELECT ('00000000-0000-4000-8000-00000000d' || lpad(to_hex(n), 3, '0'))::uuid AS id,
                     'UPG' || lpad(n::text, 6, '0') AS cn) AS d;

INSERT INTO opportunity.document_projection_state (workspace_id, document_id, document_version)
SELECT workspace_id, document_id, 2 FROM opportunity.document
WHERE workspace_id = '00000000-0000-4000-8000-00000000a0a0';

-- Responsiveness on every document (1-10 Responsive, 11-20 Not Responsive), Issues on 1-5, a comment on 1.
INSERT INTO opportunity.document_coding_field (workspace_id, document_id, field_id, value, changed_at_version,
    changed_by, changed_at)
SELECT d.workspace_id, d.document_id, f.field_id,
       CASE WHEN f.field_id = 1002 THEN to_jsonb('Reviewed before the upgrade'::text) END,
       2, '00000000-0000-4000-8000-0000000000e1', timestamptz '2024-02-01 12:00:00+00'
FROM opportunity.document d
CROSS JOIN (VALUES (1000), (1001), (1002)) AS f(field_id)
WHERE d.workspace_id = '00000000-0000-4000-8000-00000000a0a0'
  AND (f.field_id = 1000
       OR (f.field_id = 1001 AND d.control_number <= 'UPG000005')
       OR (f.field_id = 1002 AND d.control_number = 'UPG000001'));

INSERT INTO opportunity.document_coding_choice (workspace_id, document_id, field_id, choice_id)
SELECT c.workspace_id, c.document_id, c.field_id, x.choice_id
FROM opportunity.document_coding_field c
JOIN opportunity.document d ON d.workspace_id = c.workspace_id AND d.document_id = c.document_id
CROSS JOIN LATERAL (
    SELECT CASE WHEN d.control_number <= 'UPG000010' THEN 1 ELSE 2 END AS choice_id WHERE c.field_id = 1000
    UNION ALL
    SELECT unnest(ARRAY[3, 4]) WHERE c.field_id = 1001) AS x
WHERE c.workspace_id = '00000000-0000-4000-8000-00000000a0a0';

INSERT INTO opportunity.saved_search (workspace_id, saved_search_id, name, owner_id, query_text, ast_version)
VALUES ('00000000-0000-4000-8000-00000000a0a0', '00000000-0000-4000-8000-0000000a5501', 'Responsive documents',
        '00000000-0000-4000-8000-0000000000e1', 'Responsiveness = "Responsive"', 1);
COMMIT;

-- Workspace B: three documents that workspace A must never see.
BEGIN;
SELECT set_config('app.workspace_id', '00000000-0000-4000-8000-00000000b0b0', true);

INSERT INTO opportunity.workspace (workspace_id, name, matter_number, display_time_zone)
VALUES ('00000000-0000-4000-8000-00000000b0b0', 'Upgrade test B', 'UPG-0002', 'America/New_York');

INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id)
SELECT '00000000-0000-4000-8000-00000000b0b0', d.id, 'OTHER' || n, 'OTHER' || n, d.id
FROM generate_series(1, 3) AS n,
     LATERAL (SELECT ('00000000-0000-4000-8000-00000000e' || lpad(to_hex(n), 3, '0'))::uuid AS id) AS d;

INSERT INTO opportunity.document_projection_state (workspace_id, document_id)
SELECT workspace_id, document_id FROM opportunity.document
WHERE workspace_id = '00000000-0000-4000-8000-00000000b0b0';
COMMIT;
