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
- **RLS** policies (`ENABLE` + `FORCE ROW LEVEL SECURITY`) live in the migration that creates the table. The
  core tables of V0002 (`E04-T02`) predate the RLS ticket and get their policies in `E05-T03`'s migration.
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
| migrator login (deployment-provided) | owns schema `opportunity` and all objects | DDL; needs `CREATEROLE` for V0001 only |
| `opportunity_app` (NOLOGIN) | API and workers; deployment grants it to their login | CONNECT/TEMP, DML, read `schema_history`; no DDL, no TRUNCATE, no BYPASSRLS |
| `opportunity_readonly` (NOLOGIN) | support and reporting | CONNECT, SELECT |

V0001 revokes `CONNECT` on the database from `PUBLIC`, so every login must be a member of one of these roles or own
the database.
