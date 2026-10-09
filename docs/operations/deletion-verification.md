# Runbook: deletion verification

**Applies to:** proving, store by store, that a workspace's data is gone after a deletion run (ADR-014 §7), or checking
that nothing of a workspace remains where it should not (e.g. after a failed run, or a shared-index move). **Who:** an
installation operator with a database role that bypasses RLS (ADR-014 §7: a check through the application role would
see nothing and prove nothing) and read access to OpenSearch and object storage.

> **Status (2026-10-09):** workspace deletion (request, two-person approval, fenced run and certificate, E20-T02) runs
> these checks itself and records them on its certificate ([workspace-deletion.md](workspace-deletion.md)); use this
> runbook to investigate a run that ended `CompletedWithResiduals` or to check independently. Records retained by the
> workspace's retention profile (ADR-014 §5, default `RetainRecords`: productions with their members, Bates ranges,
> volume outputs, source snapshots, jobs and Redaction Sets; always the tombstone, legal hold and data key records, the
> certificate and **audit**) are expected to remain.

## 1. PostgreSQL

Run as the schema owner or another BYPASSRLS role (`deploy/docker-compose`: `docker compose exec postgres psql -U postgres
-d opportunity`). The block counts the workspace's rows in every table of schema `opportunity` that has a
`workspace_id` (partition parents only) and prints the non-zero ones:

```sql
DO $$
DECLARE
    ws constant uuid := '<workspace id>';
    t record;
    n bigint;
BEGIN
    FOR t IN
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace s ON s.oid = c.relnamespace AND s.nspname = 'opportunity'
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'workspace_id' AND NOT a.attisdropped
        WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
        ORDER BY c.relname
    LOOP
        EXECUTE format('SELECT count(*) FROM opportunity.%I WHERE workspace_id = $1', t.relname) INTO n USING ws;
        IF n > 0 THEN
            RAISE NOTICE '% rows: %', t.relname, n;
        END IF;
    END LOOP;
END
$$;
```

Expected after a purge: only the tables retained by the profile, and `workspace` itself (the tombstone row, ADR-014
§1.3: status `Purged`). Audit lives in schema `audit` and is retained by default; after an audit purge
(ADR-014 §6) `SELECT count(*) FROM audit.audit_event WHERE workspace_id = '<ws>'` is 0 and the chain's final
sequence and head hash are on the certificate.

## 2. OpenSearch

Across **every** index and alias, shared and dedicated (a moved workspace may have left copies):

```sh
curl -s -X POST "$OPENSEARCH/_all/_count" -H 'Content-Type: application/json' \
  -d '{"query":{"term":{"workspaceId":"<workspace id>"}}}'
```

Expected: `"count": 0`. Also `GET _cat/aliases/*<workspace id>*` and `GET _cat/indices/*<workspace id>*` list nothing
for a dedicated placement.

## 3. Object storage

Workspace objects live under `ws/{workspaceId}/` (ADR-011). With versioning enabled, list versions too:

```sh
aws --endpoint-url "$S3_ENDPOINT" s3api list-object-versions --bucket <bucket> --prefix "ws/<workspace id>/" --max-items 10
```

Expected: no `Versions` and no `DeleteMarkers` outside the retained areas (e.g. `productions/`, privilege logs under the
`RetainRecords` profile). Then, in PostgreSQL (step 1), zero `stored_object` rows for the purged areas.

## 4. Messaging (best effort)

No queued or dead-lettered message should still name the workspace. Dead-lettered and parked messages are recorded in
PostgreSQL: `SELECT count(*) FROM opportunity.dead_letter WHERE workspace_id = '<workspace id>'` (the rows go with the
workspace's other tenant rows) and `… FROM opportunity.dead_letter_installation WHERE claimed_workspace_id = '<workspace
id>'` (messages recorded after the workspace was gone); the broker's own `*.dlq` copies expire after 14 days. Workers
fence on `Workspace.Status` before every side effect (ADR-010 §2 F1–F3, ADR-014 §4), so such a message does no work;
report it on the certificate.

## 5. Record

Write the four results (counts per store, retained items, date, operator) into the deletion record; E20-T02 puts them
on the destruction certificate (ADR-014 §8).
