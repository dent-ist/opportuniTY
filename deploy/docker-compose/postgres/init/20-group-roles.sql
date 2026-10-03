-- NOLOGIN group roles the migrations grant privileges to. The migrator login has no CREATEROLE, so they are created
-- here as superuser with the attributes the migrations use (V0001, V0010); the migrations skip roles that exist.
-- Idempotent: runs on first start (after 10-opportunity.sh) and again before every `./opportunity.sh up`, so data
-- volumes created before a migration added a role get it too. Production DBAs create the same roles (see
-- src/Opportunity.Data/Migrations/README.md).
DO $$
DECLARE
    role_name text;
BEGIN
    FOREACH role_name IN ARRAY ARRAY['opportunity_app', 'opportunity_readonly', 'opportunity_audit_sealer',
                                     'opportunity_audit_retention'] LOOP
        IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = role_name) THEN
            EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', role_name);
        END IF;
    END LOOP;
END
$$;
