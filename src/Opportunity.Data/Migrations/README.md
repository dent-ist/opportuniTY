# PostgreSQL migrations

Plain, versioned, forward-only SQL in `Scripts/`, embedded in `Opportunity.Data` and applied by the one-shot
`Opportunity.Migrator` host (`E04-T01`). Migrations never run on API or worker startup; those hosts use
`SchemaVersionHealthCheck` to report not-ready while the schema is behind the version they were built for.

## Why a small custom runner

DbUp, grate and Evolve were considered. We need exactly: a per-script SHA-256 checksum that blocks edited scripts, a
session-level advisory lock held across the whole run (including non-transactional steps), opt-out of the transaction
per script for `CREATE INDEX CONCURRENTLY`, and a lint run inside each script's transaction. DbUp journals names only
and has no lock; grate is a CLI tool with a larger dependency tree; Evolve covers most of it but adds a dependency for
roughly 250 lines of code we can test directly. `PostgresMigrator` needs only Npgsql.

## How a run works

1. Open one connection and take `pg_advisory_lock(SchemaHistory.AdvisoryLockKey)` (polled; waits up to
   `Migrator:LockWaitTimeout`). Concurrent migrators queue; the later ones find nothing pending and exit 0.
2. Create `opportunity_migrations.schema_history` if missing.
3. Refuse to run (exit 1) if an applied script's checksum changed, an applied version is unknown to this build (the
   database is newer), or a pending script is numbered below the current version.
4. Apply each pending script in version order: script + tenant-key lint + history row in one transaction.
5. Run every registered `IInfrastructureBootstrapStep` (OpenSearch, RabbitMQ, buckets; owned by their modules).

Exit codes: 0 success or no-op, 1 migration refused/failed, 2 configuration error, 3 bootstrap step failed.

```sh
ConnectionStrings__Migrator="Host=...;Database=opportunity;Username=opportunity_owner;Password=..." \
  dotnet run --project src/Opportunity.Migrator
```

## Writing a script

- Name it `V<version>__<snake_case_description>.sql` with the next free version, zero-padded to 4 digits.
- Never edit or renumber a script after it is merged. Fix forward with a new script.
- Schema-qualify every object (`opportunity.document`). Application tables live in schema `opportunity`.
- **Tenant key:** every table in `opportunity` has a primary key that leads with `workspace_id`, and its indexes should
  lead with it too. The migrator rejects a script that leaves a violating table. Installation-level tables opt out
  with `COMMENT ON TABLE ... IS '@global <reason>'`.
- **Partition-agnostic DDL:** the partitioning scheme is still open (`E18-T08`), so keys and queries must work whether
  a table is plain or hash-partitioned by `workspace_id`; partition children are checked through their parent.
- **RLS** (ADR-015 D7, V0005): every table with a `workspace_id` column gets `ENABLE` + `FORCE ROW LEVEL SECURITY`
  and the single isolation policy in the migration that creates it: call
  `SELECT opportunity.enable_workspace_rls('opportunity.<table>');` right after `CREATE TABLE`. From V0005 on the
  migrator also lints (`RowLevelSecurityLint`): a tenant table without forced RLS and a policy on `app.workspace_id`,
  a child partition that `opportunity_app`/`opportunity_readonly` can access directly (`REVOKE ALL` on every partition;
  grants live on the parent only), a view without `security_invoker = true`, a materialized view with `workspace_id`,
  or a `SECURITY DEFINER` function without a pinned `search_path` and a `@security-definer <reason>` comment. Tables
  marked `@global` are exempt; the workspace registry is the one table with deviating (read-open) policies.
- **Workspace context:** only `Opportunity.Data`'s `WorkspaceTransaction` sets `app.workspace_id`
  (`set_config(..., true)` as the first statement of an explicit transaction) for raw SQL, Dapper, EF Core
  (`CreateDbContext`) and bulk loads. `COPY FROM` into an RLS table is rejected: stage in a temp table, then
  `INSERT ... SELECT`. `FORCE` binds the owning migrator login too, so a data migration must set the context itself.
- **Privileges:** default privileges from V0001 give `opportunity_app` SELECT/INSERT/UPDATE/DELETE and
  `opportunity_readonly` SELECT on new tables. Append-only tables must `REVOKE UPDATE, DELETE ... FROM opportunity_app`.
  Run the migrator as one stable owner login: default privileges only apply to objects that login creates.
- **Time-partitioned append-only tables** (ADR-005 R2, e.g. `coding_event` in V0004): `PARTITION BY RANGE` on the
  timestamp, primary key `(workspace_id, <timestamp>, <id>)`, and a `SECURITY DEFINER` `<table>_ensure_partitions`
  function that creates UTC-month partitions ahead and revokes UPDATE/DELETE on each. The migration pre-creates 24
  months; a scheduled maintenance job (E19) must keep calling the function, or inserts past the horizon fail.
- **Non-transactional steps** (`CREATE INDEX CONCURRENTLY`, `REINDEX CONCURRENTLY`): put `-- migrator:no-transaction` on its own line, keep the script to that step, and separate statements with
  `-- migrator:statement-break` lines. Make it re-runnable: a failed concurrent build leaves an INVALID index, so
  `DROP INDEX CONCURRENTLY IF EXISTS` first. The history row is written only after every statement succeeds.
- No extensions are installed yet; `gen_random_uuid()` is built in since PostgreSQL 13. Add `CREATE EXTENSION IF NOT
  EXISTS` in the script that first needs one.

## Expand / contract (zero-downtime upgrades, Q-43)

Version N of the app must run against schema N and N+1, so the readiness check accepts a newer schema.

1. **Expand** (release N): add nullable columns, new tables, new indexes concurrently; backfill in batches from a job,
   not in the migration. Old code keeps working.
2. **Migrate code** (release N or N+1): write both shapes, read the new one.
3. **Contract** (a later release, once no N-1 instances remain): drop old columns/tables, add `NOT NULL` via a
   `NOT VALID` check constraint followed by `VALIDATE CONSTRAINT`.

Never rename or re-type a column in place; add, backfill, switch, drop.

## Roles (V0001)

| Role | Purpose | Privileges |
|---|---|---|
| migrator login (deployment-provided) | owns schema `opportunity` and all objects | DDL; needs `CREATEROLE` only if the NOLOGIN group roles below do not exist yet (V0001, V0010); otherwise pre-create them as a DBA, as `deploy/docker-compose/postgres/init/20-group-roles.sql` does |
| `opportunity_app` (NOLOGIN) | API and workers; deployment grants it to their login | CONNECT/TEMP, DML on parents (not partitions), read `schema_history`; no DDL, no TRUNCATE, no BYPASSRLS; subject to RLS |
| `opportunity_readonly` (NOLOGIN) | support and reporting | CONNECT, SELECT; subject to RLS (sets a context to see rows) |
| `opportunity_audit_sealer` (NOLOGIN, V0010) | audit hash-chain sealer (E14-T03) | SELECT and UPDATE of the reserved chain columns of `audit.audit_event`, once per row (trigger-enforced) |
| `opportunity_audit_retention` (NOLOGIN, V0010) | audit retention job (ADR-014) | SELECT audit; EXECUTE `audit.drop_expired_partition`, the only removal path for audit |

The audit store (V0010) lives in schema `audit`, outside the tenant-key lint: installation-level events have no
workspace. `opportunity_app` has INSERT/SELECT only there, scoped by RLS to the transaction's chain (workspace, or the
system chain without a context); `opportunity_readonly` has no access (Q-16 search text). `PartitionMaintenanceService`
(dispatcher worker) keeps audit partitions 3 months and `coding_event` partitions 24 months ahead.

V0001 revokes `CONNECT` on the database from `PUBLIC`, so every login must be a member of one of these roles or own
the database.
