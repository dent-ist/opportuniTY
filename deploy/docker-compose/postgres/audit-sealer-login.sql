-- The audit hash-chain sealer login (E14-T03, ADR-013 §1.2), run by `./opportunity.sh up` with psql variables
-- `login` and `password` (from .env). It belongs to opportunity_audit_sealer only: it reads every audit chain and may
-- set the reserved chain columns once; it is never the runtime login, whose audit access RLS confines to one chain.
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS IN ROLE opportunity_audit_sealer', :'login')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'login') \gexec
SELECT format('ALTER ROLE %I PASSWORD %L', :'login', :'password') \gexec
