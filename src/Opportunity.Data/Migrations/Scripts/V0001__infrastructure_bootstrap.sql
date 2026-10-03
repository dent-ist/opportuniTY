-- V0001: infrastructure bootstrap. Roles, the application schema and default privileges only;
-- domain tables arrive in later scripts (E04-T02 onwards).
--
-- Roles are cluster-wide NOLOGIN group roles. Deployments create their own LOGIN users and grant membership:
--   opportunity_app       API/worker runtime: DML on application tables, no DDL, no ownership, no BYPASSRLS
--   opportunity_readonly  support/reporting: SELECT only
-- Application objects are owned by the login that runs the migrator, so RLS (forced per table) applies to the app.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'opportunity_app') THEN
        BEGIN
            CREATE ROLE opportunity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
        EXCEPTION WHEN duplicate_object OR unique_violation THEN
            NULL; -- created concurrently by a migrator for another database in the same cluster
        END;
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'opportunity_readonly') THEN
        BEGIN
            CREATE ROLE opportunity_readonly NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
        EXCEPTION WHEN duplicate_object OR unique_violation THEN
            NULL;
        END;
    END IF;

    -- Only the roles above (and the owner) may connect; TEMPORARY is needed for COPY-staging tables.
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO opportunity_app', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO opportunity_readonly', current_database());
END
$$;

-- Nobody but the owner creates objects in public (already the default from PostgreSQL 15).
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA IF NOT EXISTS opportunity;
REVOKE ALL ON SCHEMA opportunity FROM PUBLIC;
GRANT USAGE ON SCHEMA opportunity TO opportunity_app, opportunity_readonly;

-- Privileges for objects that later migrations create (as the migrator login) in the application schema.
-- No TRUNCATE/REFERENCES/TRIGGER for the app; append-only tables (audit, CodingEvent) revoke UPDATE/DELETE explicitly.
ALTER DEFAULT PRIVILEGES IN SCHEMA opportunity GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO opportunity_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA opportunity GRANT SELECT ON TABLES TO opportunity_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA opportunity GRANT USAGE, SELECT ON SEQUENCES TO opportunity_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA opportunity REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA opportunity GRANT EXECUTE ON FUNCTIONS TO opportunity_app;

-- Readiness checks in API/worker hosts compare the applied version with the version they were built for.
REVOKE ALL ON SCHEMA opportunity_migrations FROM PUBLIC;
GRANT USAGE ON SCHEMA opportunity_migrations TO opportunity_app, opportunity_readonly;
REVOKE ALL ON opportunity_migrations.schema_history FROM PUBLIC;
GRANT SELECT ON opportunity_migrations.schema_history TO opportunity_app, opportunity_readonly;
