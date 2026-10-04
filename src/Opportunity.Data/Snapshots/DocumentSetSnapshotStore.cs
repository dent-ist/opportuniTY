using System.Data;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Snapshots;
using Opportunity.Core.QueryLanguage;
using Opportunity.Core.Snapshots;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Snapshots;

/// <summary>
/// PostgreSQL implementation of <see cref="IDocumentSetSnapshotStore"/> over V0020: the header, unlogged staging and
/// immutable member pages (ADR-002 §6 (b)). Every statement runs in the workspace's RLS context; the database triggers
/// enforce immutability independently of this class.
/// </summary>
public sealed class DocumentSetSnapshotStore(NpgsqlDataSource dataSource) : IDocumentSetSnapshotStore
{
    private const string Columns =
        """
        s.workspace_id, s.snapshot_id, s.status, s.status_reason, s.name, s.purpose, s.source_kind, s.query_definition::text,
        s.source_snapshot_id, s.requested_count, s.materialization_strategy, s.page_size, s.search_generation,
        s.projection_generation, s.selected_while_indexing, s.selected_at, s.document_count, s.candidate_count,
        s.excluded_no_access, s.excluded_missing, s.inclusion_counts::text, s.page_count, s.root_sha256, s.materialized_at,
        s.created_by, s.created_by_display, s.created_by_groups, s.correlation_id, s.client_idempotency_key, s.created_at,
        s.attempt_count, s.expired_at,
        EXISTS (SELECT 1 FROM opportunity.job j WHERE j.workspace_id = s.workspace_id AND j.target_snapshot_id = s.snapshot_id)
        """;

    public async Task<SnapshotCreation> CreateAsync(NewSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, snapshot.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.document_set_snapshot
                (workspace_id, snapshot_id, name, purpose, source_kind, query_definition, source_snapshot_id, requested_count,
                 page_size, created_by, created_by_display, created_by_groups, correlation_id, client_idempotency_key,
                 claimed_by, claimed_until)
            VALUES (@ws, @id, @name, @purpose, @kind, @query, @source, @requested, @page_size, @by, @display, @groups,
                    @correlation, @client_key, @owner, now() + @lease)
            ON CONFLICT (workspace_id, created_by, client_idempotency_key) WHERE client_idempotency_key IS NOT NULL DO NOTHING
            """))
        {
            JsonObject? query = snapshot.QueryText is null
                ? null
                : new JsonObject { ["text"] = snapshot.QueryText, ["normalized"] = snapshot.NormalizedQuery, ["astVersion"] = QueryNode.AstVersion };
            insert.Parameters.AddWithValue("ws", snapshot.WorkspaceId);
            insert.Parameters.AddWithValue("id", snapshot.SnapshotId);
            insert.Parameters.AddWithValue("name", snapshot.Name);
            insert.Parameters.AddWithValue("purpose", snapshot.Purpose.ToString());
            insert.Parameters.AddWithValue("kind", snapshot.SourceKind.ToString());
            insert.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Jsonb) { Value = (object?)query?.ToJsonString() ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Uuid) { Value = (object?)snapshot.SourceSnapshotId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("requested", NpgsqlDbType.Integer) { Value = (object?)snapshot.RequestedCount ?? DBNull.Value });
            insert.Parameters.AddWithValue("page_size", snapshot.PageSize);
            insert.Parameters.AddWithValue("by", snapshot.CreatedBy);
            insert.Parameters.AddWithValue("display", Truncate(snapshot.CreatedByDisplay, 256));
            insert.Parameters.AddWithValue("groups", snapshot.CreatedByGroups.ToArray());
            insert.Parameters.Add(new NpgsqlParameter("correlation", NpgsqlDbType.Text) { Value = (object?)snapshot.CorrelationId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("client_key", NpgsqlDbType.Text) { Value = (object?)snapshot.ClientIdempotencyKey ?? DBNull.Value });
            insert.Parameters.AddWithValue("owner", snapshot.ClaimOwner);
            insert.Parameters.AddWithValue("lease", snapshot.ClaimLease);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                var existing = await FindByClientKeyAsync(tx, snapshot.CreatedBy, snapshot.ClientIdempotencyKey!, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SnapshotCreation(existing!, Created: false);
            }
        }

        var created = await GetAsync(tx, snapshot.SnapshotId, lockRow: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SnapshotCreation(created!, Created: true);
    }

    public async Task<SnapshotRecord?> GetAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await GetAsync(tx, snapshotId, lockRow: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<SnapshotRecord?> FindByClientKeyAsync(
        Guid workspaceId, Guid createdBy, string clientIdempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await FindByClientKeyAsync(tx, createdBy, clientIdempotencyKey, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<SnapshotRecord>> ListAsync(
        Guid workspaceId, Guid? createdBy, SnapshotListCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns}
              FROM opportunity.document_set_snapshot s
             WHERE s.workspace_id = @ws
               AND (@by::uuid IS NULL OR s.created_by = @by)
               AND (@after_at::timestamptz IS NULL OR (s.created_at, s.snapshot_id) < (@after_at, @after_id))
             ORDER BY s.created_at DESC, s.snapshot_id DESC
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)createdBy ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlDbType.TimestampTz) { Value = (object?)after?.CreatedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = (object?)after?.SnapshotId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var records = new List<SnapshotRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(Read(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async Task<SearchWatermark> ReadWatermarkAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT coalesce((SELECT g.value FROM opportunity.workspace_search_generation g WHERE g.workspace_id = @ws), 0),
                   (SELECT min(o.search_generation) FROM opportunity.search_outbox o WHERE o.workspace_id = @ws AND o.status <> 4),
                   (SELECT min(t.search_generation) FROM opportunity.index_chunk_task t
                     WHERE t.workspace_id = @ws AND t.status <> 5 AND t.search_generation IS NOT NULL)
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        long counter;
        long? outbox, tasks;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            counter = reader.GetInt64(0);
            outbox = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            tasks = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        var oldestPending = Math.Min(outbox ?? long.MaxValue, tasks ?? long.MaxValue);
        return new SearchWatermark(oldestPending == long.MaxValue ? counter : Math.Min(counter, oldestPending - 1), counter);
    }

    public async Task<bool> TryClaimAsync(Guid workspaceId, Guid snapshotId, string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.document_set_snapshot
               SET attempt_count = CASE WHEN claimed_by IS DISTINCT FROM @owner OR claimed_until < now()
                                        THEN least(attempt_count + 1, 100) ELSE attempt_count END,
                   claimed_by = @owner, claimed_until = now() + @lease
             WHERE workspace_id = @ws AND snapshot_id = @id AND status = 'Materializing'
               AND (claimed_by IS NULL OR claimed_until < now() OR claimed_by = @owner)
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("lease", lease);
        var claimed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public Task ReleaseClaimAsync(Guid workspaceId, Guid snapshotId, string owner, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.document_set_snapshot SET claimed_by = NULL, claimed_until = NULL
             WHERE workspace_id = @ws AND snapshot_id = @id AND status = 'Materializing' AND claimed_by = @owner
            """,
            cancellationToken, ("id", snapshotId), ("owner", owner));

    public async Task<IReadOnlyList<Guid>> GetUnclaimedAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT snapshot_id FROM opportunity.document_set_snapshot
             WHERE workspace_id = @ws AND status = 'Materializing' AND (claimed_until IS NULL OR claimed_until < now())
             ORDER BY created_at
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("limit", limit);
        var ids = await ReadGuidsAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public async Task<IReadOnlyList<Guid>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        // The workspace registry is installation-level (ADR-015 D7.1): readable without a workspace context.
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT workspace_id FROM opportunity.workspace WHERE status <> '{nameof(WorkspaceStatus.Purged)}' ORDER BY workspace_id");
        var ids = await ReadGuidsAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public Task ResetStageAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            "DELETE FROM opportunity.document_set_snapshot_stage WHERE workspace_id = @ws AND snapshot_id = @id",
            cancellationToken, ("id", snapshotId));

    public async Task<int> StageAsync(
        Guid workspaceId, Guid snapshotId, IReadOnlyCollection<Guid> documentIds, SnapshotInclusionReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count == 0)
        {
            return 0;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            INSERT INTO opportunity.document_set_snapshot_stage (workspace_id, snapshot_id, document_id, inclusion_reason)
            SELECT @ws, @id, d, @reason FROM unnest(@ids) AS d
            ON CONFLICT DO NOTHING
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("reason", (short)reason);
        command.Parameters.AddWithValue("ids", documentIds.ToArray());
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<long> StageFromSnapshotAsync(Guid workspaceId, Guid snapshotId, Guid sourceSnapshotId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            INSERT INTO opportunity.document_set_snapshot_stage (workspace_id, snapshot_id, document_id, inclusion_reason)
            SELECT @ws, @id, u.document_id, u.reason
              FROM opportunity.document_set_snapshot s
              JOIN opportunity.document_set_snapshot_page p ON p.workspace_id = s.workspace_id AND p.snapshot_id = s.snapshot_id
             CROSS JOIN LATERAL unnest(p.document_ids, p.inclusion_reasons) AS u(document_id, reason)
             WHERE s.workspace_id = @ws AND s.snapshot_id = @source AND s.status = 'Ready'
            ON CONFLICT DO NOTHING
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("source", sourceSnapshotId);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<SnapshotFreezeResult> FreezeAsync(
        SnapshotFreezeRequest request, SnapshotMemberAuthorizer authorize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorize);
        var ws = request.WorkspaceId;

        // One consistent PostgreSQL snapshot for liveness, baselines and the published membership (ADR-002 §5.2).
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, ws, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var header = await GetAsync(tx, request.SnapshotId, lockRow: true, cancellationToken).ConfigureAwait(false);
        if (header is null || header.Status != SnapshotStatus.Materializing || !await ClaimedByAsync(tx, request, cancellationToken).ConfigureAwait(false))
        {
            return new SnapshotFreezeResult(SnapshotFreezeOutcome.ClaimLost, header);
        }

        long staged, live;
        await using (var prepare = tx.Command(
            """
            CREATE TEMP TABLE snapshot_freeze (
                document_id      uuid     PRIMARY KEY,
                baseline_version bigint   NOT NULL,
                reason           smallint NOT NULL,
                family_key       text     COLLATE "C" NOT NULL,
                family_id        uuid     NOT NULL,
                family_sequence  integer  NOT NULL
            ) ON COMMIT DROP;
            INSERT INTO snapshot_freeze
            SELECT s.document_id, ps.document_version, s.inclusion_reason, r.control_number_sort_key, d.family_id, d.family_sequence
              FROM opportunity.document_set_snapshot_stage s
              JOIN opportunity.document d ON d.workspace_id = s.workspace_id AND d.document_id = s.document_id
              JOIN opportunity.document r ON r.workspace_id = d.workspace_id AND r.document_id = d.family_id
              JOIN opportunity.document_projection_state ps
                ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
             WHERE s.workspace_id = @ws AND s.snapshot_id = @id;
            SELECT (SELECT count(*) FROM opportunity.document_set_snapshot_stage WHERE workspace_id = @ws AND snapshot_id = @id),
                   (SELECT count(*) FROM snapshot_freeze);
            """))
        {
            prepare.Parameters.AddWithValue("ws", ws);
            prepare.Parameters.AddWithValue("id", request.SnapshotId);
            await using var reader = await prepare.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            staged = reader.GetInt64(0);
            live = reader.GetInt64(1);
        }

        // Security filter for the creator (ADR-002 §5.2.2). The PDP reads current security state after this
        // transaction's snapshot was taken, so no member is admitted that was hidden from the creator at the freeze point.
        long denied = 0;
        var after = Guid.Empty;
        while (true)
        {
            var batch = new List<Guid>(request.AuthorizationBatchSize);
            await using (var next = tx.Command(
                "SELECT document_id FROM snapshot_freeze WHERE document_id > @after ORDER BY document_id LIMIT @n"))
            {
                next.Parameters.AddWithValue("after", after);
                next.Parameters.AddWithValue("n", request.AuthorizationBatchSize);
                batch.AddRange(await ReadGuidsAsync(next, cancellationToken).ConfigureAwait(false));
            }

            if (batch.Count == 0)
            {
                break;
            }

            var excluded = await authorize(batch, cancellationToken).ConfigureAwait(false);
            if (excluded.Count > 0)
            {
                await using var delete = tx.Command("DELETE FROM snapshot_freeze WHERE document_id = ANY(@ids)");
                delete.Parameters.AddWithValue("ids", excluded.Keys.ToArray());
                denied += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            after = batch[^1];
            if (batch.Count < request.AuthorizationBatchSize)
            {
                break;
            }
        }

        await using (var publish = tx.Command(
            """
            INSERT INTO opportunity.document_set_snapshot_page
                (workspace_id, snapshot_id, page_no, first_ordinal, member_count, document_ids, baseline_versions,
                 inclusion_reasons, sha256)
            SELECT @ws, @id, m.page_no, min(m.ordinal), count(*)::integer,
                   array_agg(m.document_id ORDER BY m.ordinal),
                   array_agg(m.baseline_version ORDER BY m.ordinal),
                   array_agg(m.reason ORDER BY m.ordinal),
                   sha256(string_agg(int8send(m.ordinal) || uuid_send(m.document_id) || int8send(m.baseline_version)
                                     || int2send(m.reason), ''::bytea ORDER BY m.ordinal))
              FROM (SELECT f.*, ((f.ordinal - 1) / @page_size + 1)::integer AS page_no
                      FROM (SELECT x.*, row_number() OVER (ORDER BY x.family_key, x.family_id, x.family_sequence, x.document_id) AS ordinal
                              FROM snapshot_freeze x) f) m
             GROUP BY m.page_no;

            UPDATE opportunity.document_set_snapshot s
               SET status = 'Ready',
                   search_generation = @generation,
                   projection_generation = @projection,
                   selected_while_indexing = @indexing,
                   selected_at = @selected_at,
                   document_count = c.members,
                   candidate_count = @staged + @before,
                   excluded_no_access = @denied + @before,
                   excluded_missing = @staged - @live,
                   inclusion_counts = c.reasons,
                   page_count = c.pages,
                   root_sha256 = c.root,
                   materialized_at = now(),
                   claimed_by = NULL,
                   claimed_until = NULL
              FROM (SELECT (SELECT count(*) FROM snapshot_freeze) AS members,
                           (SELECT coalesce(jsonb_object_agg(reason, n), '{}'::jsonb)
                              FROM (SELECT reason, count(*) AS n FROM snapshot_freeze GROUP BY reason) r) AS reasons,
                           (SELECT count(*)::integer FROM opportunity.document_set_snapshot_page p
                             WHERE p.workspace_id = @ws AND p.snapshot_id = @id) AS pages,
                           (SELECT sha256(convert_to(@prefix, 'UTF8') || int8send((SELECT count(*) FROM snapshot_freeze))
                                          || coalesce(string_agg(p.sha256, ''::bytea ORDER BY p.page_no), ''::bytea))
                              FROM opportunity.document_set_snapshot_page p
                             WHERE p.workspace_id = @ws AND p.snapshot_id = @id) AS root) c
             WHERE s.workspace_id = @ws AND s.snapshot_id = @id;

            DELETE FROM opportunity.document_set_snapshot_stage WHERE workspace_id = @ws AND snapshot_id = @id;
            """))
        {
            publish.Parameters.AddWithValue("ws", ws);
            publish.Parameters.AddWithValue("id", request.SnapshotId);
            publish.Parameters.AddWithValue("page_size", (long)header.PageSize);
            publish.Parameters.Add(new NpgsqlParameter("generation", NpgsqlDbType.Bigint) { Value = (object?)request.SearchGeneration ?? DBNull.Value });
            publish.Parameters.Add(new NpgsqlParameter("projection", NpgsqlDbType.Integer) { Value = (object?)request.ProjectionGeneration ?? DBNull.Value });
            publish.Parameters.Add(new NpgsqlParameter("indexing", NpgsqlDbType.Boolean) { Value = (object?)request.SelectedWhileIndexing ?? DBNull.Value });
            publish.Parameters.Add(new NpgsqlParameter("selected_at", NpgsqlDbType.TimestampTz) { Value = (object?)request.SelectedAt ?? DBNull.Value });
            publish.Parameters.AddWithValue("staged", staged);
            publish.Parameters.AddWithValue("live", live);
            publish.Parameters.AddWithValue("denied", denied);
            publish.Parameters.AddWithValue("before", request.ExcludedBeforeStaging);
            publish.Parameters.AddWithValue("prefix", SnapshotHashing.RootPrefix);
            await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var frozen = await GetAsync(tx, request.SnapshotId, lockRow: false, cancellationToken).ConfigureAwait(false);
        foreach (var auditEvent in request.Audit?.Invoke(frozen!) ?? [])
        {
            await AuditSql.InsertAsync(tx, auditEvent, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SnapshotFreezeResult(SnapshotFreezeOutcome.Frozen, frozen);
    }

    public async Task<bool> FailAsync(Guid workspaceId, Guid snapshotId, string reason, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        bool failed;
        await using (var command = tx.Command(
            """
            UPDATE opportunity.document_set_snapshot
               SET status = 'Failed', status_reason = @reason, claimed_by = NULL, claimed_until = NULL
             WHERE workspace_id = @ws AND snapshot_id = @id AND status = 'Materializing'
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", snapshotId);
            command.Parameters.AddWithValue("reason", Truncate(reason, 2000));
            failed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }

        await using (var cleanup = tx.Command("DELETE FROM opportunity.document_set_snapshot_stage WHERE workspace_id = @ws AND snapshot_id = @id"))
        {
            cleanup.Parameters.AddWithValue("ws", workspaceId);
            cleanup.Parameters.AddWithValue("id", snapshotId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return failed;
    }

    public async Task<IReadOnlyList<SnapshotMember>> ReadMembersAsync(
        Guid workspaceId, Guid snapshotId, long fromOrdinal, long toOrdinal, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT p.first_ordinal + u.i - 1, u.document_id, u.baseline_version, u.reason
              FROM opportunity.document_set_snapshot s
              JOIN opportunity.document_set_snapshot_page p ON p.workspace_id = s.workspace_id AND p.snapshot_id = s.snapshot_id
             CROSS JOIN LATERAL unnest(p.document_ids, p.baseline_versions, p.inclusion_reasons)
                   WITH ORDINALITY AS u(document_id, baseline_version, reason, i)
             WHERE s.workspace_id = @ws AND s.snapshot_id = @id AND s.status = 'Ready'
               AND p.page_no BETWEEN (@from - 1) / s.page_size + 1 AND (@to - 1) / s.page_size + 1
               AND p.first_ordinal + u.i - 1 BETWEEN @from AND @to
             ORDER BY 1
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("from", fromOrdinal);
        command.Parameters.AddWithValue("to", toOrdinal);
        var members = new List<SnapshotMember>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add(new SnapshotMember(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), (SnapshotInclusionReason)reader.GetInt16(3)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return members;
    }

    public async Task<IReadOnlyList<SnapshotPage>> ReadPagesAsync(
        Guid workspaceId, Guid snapshotId, int fromPage, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT page_no, first_ordinal, document_ids, baseline_versions, inclusion_reasons, sha256
              FROM opportunity.document_set_snapshot_page
             WHERE workspace_id = @ws AND snapshot_id = @id AND page_no >= @from
             ORDER BY page_no
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("from", fromPage);
        command.Parameters.AddWithValue("limit", limit);
        var pages = new List<SnapshotPage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var first = reader.GetInt64(1);
                var ids = reader.GetFieldValue<Guid[]>(2);
                var versions = reader.GetFieldValue<long[]>(3);
                var reasons = reader.GetFieldValue<short[]>(4);
                var members = new SnapshotMember[ids.Length];
                for (var i = 0; i < ids.Length; i++)
                {
                    members[i] = new SnapshotMember(first + i, ids[i], versions[i], (SnapshotInclusionReason)reasons[i]);
                }

                pages.Add(new SnapshotPage(reader.GetInt32(0), first, members, reader.GetFieldValue<byte[]>(5)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pages;
    }

    public async Task<IReadOnlyList<Guid>> ExpireAsync(
        Guid workspaceId, TimeSpan unreferencedLifetime, TimeSpan jobRetention, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Guid> expired;
        await using (var command = tx.Command(
            """
            UPDATE opportunity.document_set_snapshot s
               SET status = 'Expired', expired_at = now()
             WHERE s.workspace_id = @ws
               AND s.snapshot_id IN (
                   SELECT c.snapshot_id
                     FROM opportunity.document_set_snapshot c
                    WHERE c.workspace_id = @ws AND c.status IN ('Ready', 'Failed')
                      AND (
                          (NOT EXISTS (SELECT 1 FROM opportunity.job j
                                        WHERE j.workspace_id = @ws AND j.target_snapshot_id = c.snapshot_id)
                           AND c.created_at <= now() - @unreferenced)
                       OR (c.purpose = 'BulkCoding'
                           AND EXISTS (SELECT 1 FROM opportunity.job j
                                        WHERE j.workspace_id = @ws AND j.target_snapshot_id = c.snapshot_id)
                           AND NOT EXISTS (SELECT 1 FROM opportunity.job j
                                            WHERE j.workspace_id = @ws AND j.target_snapshot_id = c.snapshot_id
                                              AND (j.finished_at IS NULL OR j.finished_at > now() - @job_retention))))
                      AND NOT EXISTS (SELECT 1 FROM opportunity.document_set_snapshot d
                                       WHERE d.workspace_id = @ws AND d.source_snapshot_id = c.snapshot_id
                                         AND d.status = 'Materializing')
                    ORDER BY c.created_at
                    LIMIT @limit
                      FOR UPDATE)
            RETURNING s.snapshot_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("unreferenced", unreferencedLifetime);
            command.Parameters.AddWithValue("job_retention", jobRetention);
            command.Parameters.AddWithValue("limit", limit);
            expired = await ReadGuidsAsync(command, cancellationToken).ConfigureAwait(false);
        }

        // Membership of expired snapshots, staging of finished ones, and staging a crashed request left without a
        // header (snapshot IDs are UUIDv7, so their age is known).
        await using (var cleanup = tx.Command(
            """
            DELETE FROM opportunity.document_set_snapshot_page WHERE workspace_id = @ws AND snapshot_id = ANY(@ids);
            DELETE FROM opportunity.document_set_snapshot_stage g
             WHERE g.workspace_id = @ws
               AND NOT EXISTS (SELECT 1 FROM opportunity.document_set_snapshot s
                                WHERE s.workspace_id = @ws AND s.snapshot_id = g.snapshot_id AND s.status = 'Materializing')
               AND (EXISTS (SELECT 1 FROM opportunity.document_set_snapshot s
                             WHERE s.workspace_id = @ws AND s.snapshot_id = g.snapshot_id)
                    OR opportunity.uuid_v7_timestamp(g.snapshot_id) < now() - interval '1 day');
            """))
        {
            cleanup.Parameters.AddWithValue("ws", workspaceId);
            cleanup.Parameters.AddWithValue("ids", expired.ToArray());
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expired;
    }

    private static async Task<bool> ClaimedByAsync(WorkspaceTransaction tx, SnapshotFreezeRequest request, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            "SELECT claimed_by FROM opportunity.document_set_snapshot WHERE workspace_id = @ws AND snapshot_id = @id");
        command.Parameters.AddWithValue("ws", request.WorkspaceId);
        command.Parameters.AddWithValue("id", request.SnapshotId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string owner && owner == request.ClaimOwner;
    }

    private static async Task<SnapshotRecord?> GetAsync(WorkspaceTransaction tx, Guid snapshotId, bool lockRow, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"SELECT {Columns} FROM opportunity.document_set_snapshot s WHERE s.workspace_id = @ws AND s.snapshot_id = @id"
            + (lockRow ? " FOR UPDATE OF s" : string.Empty));
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async Task<SnapshotRecord?> FindByClientKeyAsync(WorkspaceTransaction tx, Guid createdBy, string key, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.document_set_snapshot s
             WHERE s.workspace_id = @ws AND s.created_by = @by AND s.client_idempotency_key = @key
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("by", createdBy);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static SnapshotRecord Read(NpgsqlDataReader r)
    {
        var query = r.IsDBNull(7) ? null : JsonNode.Parse(r.GetString(7));
        var counts = new Dictionary<SnapshotInclusionReason, long>();
        if (!r.IsDBNull(20) && JsonNode.Parse(r.GetString(20)) is JsonObject reasons)
        {
            foreach (var (key, value) in reasons)
            {
                if (short.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var code) && value is not null)
                {
                    counts[(SnapshotInclusionReason)code] = value.GetValue<long>();
                }
            }
        }

        return new SnapshotRecord
        {
            WorkspaceId = r.GetGuid(0),
            SnapshotId = r.GetGuid(1),
            Status = Enum.Parse<SnapshotStatus>(r.GetString(2)),
            StatusReason = r.IsDBNull(3) ? null : r.GetString(3),
            Name = r.GetString(4),
            Purpose = Enum.Parse<SnapshotPurpose>(r.GetString(5)),
            SourceKind = Enum.Parse<SnapshotSourceKind>(r.GetString(6)),
            QueryText = query?["text"]?.GetValue<string>(),
            NormalizedQuery = query?["normalized"]?.GetValue<string>(),
            SourceSnapshotId = r.IsDBNull(8) ? null : r.GetGuid(8),
            RequestedCount = r.IsDBNull(9) ? null : r.GetInt32(9),
            Strategy = Enum.Parse<MaterializationStrategy>(r.GetString(10)),
            PageSize = r.GetInt32(11),
            SearchGeneration = r.IsDBNull(12) ? null : r.GetInt64(12),
            ProjectionGeneration = r.IsDBNull(13) ? null : r.GetInt32(13),
            SelectedWhileIndexing = r.IsDBNull(14) ? null : r.GetBoolean(14),
            SelectedAt = r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
            DocumentCount = r.IsDBNull(16) ? null : r.GetInt64(16),
            CandidateCount = r.IsDBNull(17) ? null : r.GetInt64(17),
            ExcludedNoAccess = r.IsDBNull(18) ? null : r.GetInt64(18),
            ExcludedMissing = r.IsDBNull(19) ? null : r.GetInt64(19),
            InclusionCounts = counts,
            PageCount = r.IsDBNull(21) ? null : r.GetInt32(21),
            RootSha256 = r.IsDBNull(22) ? null : r.GetFieldValue<byte[]>(22),
            MaterializedAt = r.IsDBNull(23) ? null : r.GetFieldValue<DateTimeOffset>(23),
            CreatedBy = r.GetGuid(24),
            CreatedByDisplay = r.GetString(25),
            CreatedByGroups = r.GetFieldValue<string[]>(26),
            CorrelationId = r.IsDBNull(27) ? null : r.GetString(27),
            ClientIdempotencyKey = r.IsDBNull(28) ? null : r.GetString(28),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(29),
            AttemptCount = r.GetInt16(30),
            ExpiredAt = r.IsDBNull(31) ? null : r.GetFieldValue<DateTimeOffset>(31),
            Referenced = r.GetBoolean(32),
        };
    }

    private static async Task<IReadOnlyList<Guid>> ReadGuidsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task ExecuteAsync(Guid workspaceId, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(sql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public static class DocumentSetSnapshotStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IDocumentSetSnapshotStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresSnapshotStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDocumentSetSnapshotStore, DocumentSetSnapshotStore>();
        return services;
    }
}
