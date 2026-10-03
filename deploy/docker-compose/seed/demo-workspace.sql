-- Demo data for the developer profile (./opportunity.sh seed). Idempotent; synthetic data only.
-- Runs as the migrator/owner login after the migrator has created the schema.
INSERT INTO opportunity.workspace (workspace_id, name, matter_number, display_time_zone)
VALUES ('00000000-0000-4000-8000-00000000d3e0', 'Demo workspace', 'DEMO-0001', 'UTC')
ON CONFLICT (workspace_id) DO NOTHING;

SELECT workspace_id, name, matter_number, status FROM opportunity.workspace
WHERE workspace_id = '00000000-0000-4000-8000-00000000d3e0';
