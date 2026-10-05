using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Search.TermReports;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchTermReports;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.Data.Relationships;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL implementation of <see cref="ISearchTermReportStore"/> over the V0034 tables. Reports are created with
/// their first job in one transaction; counts are computed in one transaction from the run's rows with
/// <see cref="FamilyHitSql"/>, together with the run's audit event.
/// </summary>
public sealed class SearchTermReportStore(NpgsqlDataSource dataSource) : ISearchTermReportStore
{
    private const string Columns =
        """
        r.workspace_id, r.report_id, r.name, r.scope_kind, r.scope_id, r.scope_name, r.status, r.status_reason, r.job_id, r.run_count,
        r.snapshot_id, r.search_generation, r.index_current, r.documents_in_scope, r.documents_with_hits, r.documents_with_hits_family,
        r.documents_without_hits, r.excluded_no_access, r.created_by, r.created_by_display, r.created_at, r.executed_by,
        r.executed_by_display, r.executed_by_groups, r.executed_at, r.completed_at,
        (SELECT count(*)::int FROM opportunity.search_term_report_term t WHERE t.workspace_id = r.workspace_id AND t.report_id = r.report_id)
        """;

    public async Task<SearchTermReportCreation> CreateAsync(NewSearchTermReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, report.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var job = await JobRepository.CreateInTransactionAsync(tx, new NewJob
        {
            WorkspaceId = report.WorkspaceId,
            JobId = report.JobId,
            JobType = JobType.SearchTermReport,
            InitiatedBy = report.Executor.UserId,
            TargetSnapshotId = report.SnapshotId,
            Parameters = report.JobParameters,
            ClientIdempotencyKey = report.ClientIdempotencyKey,
            CorrelationId = report.Executor.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);
        if (!job.Created)
        {
            var existing = await ReadOneAsync(tx, "r.job_id = @id", job.Job.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Job {job.Job.JobId} has no search term report.");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SearchTermReportCreation(existing, false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.search_term_report
                (workspace_id, report_id, name, scope_kind, scope_id, scope_name, job_id, snapshot_id, created_by, created_by_display,
                 executed_by, executed_by_display, executed_by_groups, client_idempotency_key)
            VALUES (@ws, @id, @name, @kind, @scope, @scopeName, @job, @snapshot, @by, @display, @by, @display, @groups, @key)
            """))
        {
            insert.Parameters.AddWithValue("ws", report.WorkspaceId);
            insert.Parameters.AddWithValue("id", report.ReportId);
            insert.Parameters.AddWithValue("name", report.Name);
            insert.Parameters.AddWithValue("kind", report.ScopeKind.ToString());
            insert.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Uuid) { Value = (object?)report.ScopeId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("scopeName", NpgsqlDbType.Text) { Value = (object?)Truncate(report.ScopeName, 200) ?? DBNull.Value });
            insert.Parameters.AddWithValue("job", report.JobId);
            insert.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Uuid) { Value = (object?)report.SnapshotId ?? DBNull.Value });
            insert.Parameters.AddWithValue("by", report.Executor.UserId);
            insert.Parameters.AddWithValue("display", Truncate(report.Executor.DisplayName, 512)!);
            insert.Parameters.AddWithValue("groups", report.Executor.Groups.ToArray());
            insert.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)report.ClientIdempotencyKey ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var terms = tx.Command(
            """
            INSERT INTO opportunity.search_term_report_term
                (workspace_id, report_id, term_no, term_id, name, expression, error_code, error_message, error_position)
            SELECT @ws, @id, t.no, t.term_id, t.name, t.expression, t.code, t.message, t.position
            FROM unnest(@nos, @termIds, @names, @expressions, @codes, @messages, @positions)
                AS t(no, term_id, name, expression, code, message, position)
            """))
        {
            terms.Parameters.AddWithValue("ws", report.WorkspaceId);
            terms.Parameters.AddWithValue("id", report.ReportId);
            terms.Parameters.AddWithValue("nos", Enumerable.Range(1, report.Terms.Count).Select(n => (short)n).ToArray());
            terms.Parameters.AddWithValue("termIds", report.Terms.Select(_ => Guid.CreateVersion7()).ToArray());
            terms.Parameters.AddWithValue("names", report.Terms.Select(t => t.Name).ToArray());
            terms.Parameters.AddWithValue("expressions", report.Terms.Select(t => t.Expression).ToArray());
            terms.Parameters.Add(new NpgsqlParameter("codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = report.Terms.Select(t => t.Error?.Code).ToArray() });
            terms.Parameters.Add(new NpgsqlParameter("messages", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = report.Terms.Select(t => Truncate(t.Error?.Message, 2000)).ToArray(),
            });
            terms.Parameters.Add(new NpgsqlParameter("positions", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = report.Terms.Select(t => t.Error?.Position).ToArray(),
            });
            await terms.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = (await ReadOneAsync(tx, "r.report_id = @id", report.ReportId, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SearchTermReportCreation(record, true);
    }

    public async Task<SearchTermReportRecord?> GetAsync(Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await ReadOneAsync(tx, "r.report_id = @id", reportId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<SearchTermRecord>> GetTermsAsync(Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT term_no, term_id, name, expression, error_code, error_message, error_position, documents_with_hits,
                   documents_with_hits_family, unique_hits, unique_hits_family
            FROM opportunity.search_term_report_term WHERE workspace_id = @ws AND report_id = @id ORDER BY term_no
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", reportId);
        var terms = new List<SearchTermRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                terms.Add(new SearchTermRecord(
                    reader.GetInt16(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : new SearchTermError(reader.GetString(4), reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6)),
                    Long(reader, 7),
                    Long(reader, 8),
                    Long(reader, 9),
                    Long(reader, 10)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return terms;
    }

    public async Task<IReadOnlyList<SearchTermReportRecord>> ListAsync(
        Guid workspaceId, Guid? createdBy, SearchTermReportListCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.search_term_report r
            WHERE r.workspace_id = @ws AND (@by::uuid IS NULL OR r.created_by = @by)
              AND (@afterAt::timestamptz IS NULL OR (r.created_at, r.report_id) < (@afterAt, @afterId))
            ORDER BY r.created_at DESC, r.report_id DESC
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)createdBy ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("afterAt", NpgsqlDbType.TimestampTz) { Value = (object?)after?.CreatedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("afterId", NpgsqlDbType.Uuid) { Value = (object?)after?.ReportId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var records = new List<SearchTermReportRecord>();
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

    public async Task<SearchTermReportRecord?> RerunAsync(
        Guid workspaceId, Guid reportId, Guid jobId, SearchTermReportExecutor executor, IReadOnlyDictionary<int, SearchTermError> termErrors,
        JsonObject jobParameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(termErrors);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        Guid? snapshot;
        await using (var locked = tx.Command(
            """
            SELECT snapshot_id FROM opportunity.search_term_report
            WHERE workspace_id = @ws AND report_id = @id AND status IN ('Completed', 'Failed') FOR UPDATE
            """))
        {
            locked.Parameters.AddWithValue("ws", workspaceId);
            locked.Parameters.AddWithValue("id", reportId);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            snapshot = reader.IsDBNull(0) ? null : reader.GetGuid(0);
        }

        await JobRepository.CreateInTransactionAsync(tx, new NewJob
        {
            WorkspaceId = workspaceId,
            JobId = jobId,
            JobType = JobType.SearchTermReport,
            InitiatedBy = executor.UserId,
            TargetSnapshotId = snapshot,
            Parameters = jobParameters,
            CorrelationId = executor.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);

        await using (var update = tx.Command(
            """
            UPDATE opportunity.search_term_report
               SET status = 'Queued', status_reason = NULL, job_id = @job, run_count = run_count + 1, search_generation = NULL,
                   index_current = NULL, documents_in_scope = NULL, documents_with_hits = NULL, documents_with_hits_family = NULL,
                   documents_without_hits = NULL, excluded_no_access = NULL, executed_by = @by, executed_by_display = @display,
                   executed_by_groups = @groups, executed_at = NULL, completed_at = NULL, claimed_by = NULL, claimed_until = NULL
             WHERE workspace_id = @ws AND report_id = @id;
            UPDATE opportunity.search_term_report_term t
               SET error_code = e.code, error_message = e.message, error_position = e.position, documents_with_hits = NULL,
                   documents_with_hits_family = NULL, unique_hits = NULL, unique_hits_family = NULL
              FROM opportunity.search_term_report_term t2
              LEFT JOIN unnest(@nos, @codes, @messages, @positions) AS e(no, code, message, position) ON e.no = t2.term_no
             WHERE t.workspace_id = @ws AND t.report_id = @id AND t2.workspace_id = t.workspace_id AND t2.report_id = t.report_id
               AND t2.term_no = t.term_no
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", reportId);
            update.Parameters.AddWithValue("job", jobId);
            update.Parameters.AddWithValue("by", executor.UserId);
            update.Parameters.AddWithValue("display", Truncate(executor.DisplayName, 512)!);
            update.Parameters.AddWithValue("groups", executor.Groups.ToArray());
            var errors = termErrors.OrderBy(e => e.Key).ToList();
            update.Parameters.AddWithValue("nos", errors.Select(e => (short)e.Key).ToArray());
            update.Parameters.AddWithValue("codes", errors.Select(e => e.Value.Code).ToArray());
            update.Parameters.AddWithValue("messages", errors.Select(e => Truncate(e.Value.Message, 2000)!).ToArray());
            update.Parameters.Add(new NpgsqlParameter("positions", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = errors.Select(e => e.Value.Position).ToArray(),
            });
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = await ReadOneAsync(tx, "r.report_id = @id", reportId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<bool> DeleteAsync(Guid workspaceId, Guid reportId, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var delete = tx.Command(
            """
            DELETE FROM opportunity.search_term_report_hit WHERE workspace_id = @ws AND report_id = @id;
            DELETE FROM opportunity.search_term_report_document WHERE workspace_id = @ws AND report_id = @id;
            DELETE FROM opportunity.search_term_report_term WHERE workspace_id = @ws AND report_id = @id;
            """))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("id", reportId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var report = tx.Command("DELETE FROM opportunity.search_term_report WHERE workspace_id = @ws AND report_id = @id"))
        {
            report.Parameters.AddWithValue("ws", workspaceId);
            report.Parameters.AddWithValue("id", reportId);
            if (await report.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        await AuditSql.InsertAsync(tx, audit with { EventId = Guid.CreateVersion7(), WorkspaceId = workspaceId }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<Guid>> GetActiveAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT report_id FROM opportunity.search_term_report
            WHERE workspace_id = @ws AND status IN ('Queued', 'Running') ORDER BY created_at LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("limit", limit);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public async Task<bool> TryClaimAsync(Guid workspaceId, Guid reportId, string owner, TimeSpan lease, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.search_term_report SET claimed_by = @owner, claimed_until = now() + @lease
            WHERE workspace_id = @ws AND report_id = @id AND status IN ('Queued', 'Running')
              AND (claimed_by IS NULL OR claimed_by = @owner OR claimed_until < now())
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", reportId);
                c.Parameters.AddWithValue("owner", owner);
                c.Parameters.AddWithValue("lease", lease);
            }, cancellationToken).ConfigureAwait(false) == 1;

    public Task ReleaseClaimAsync(Guid workspaceId, Guid reportId, string owner, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.search_term_report SET claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND report_id = @id AND claimed_by = @owner
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", reportId);
                c.Parameters.AddWithValue("owner", owner);
            }, cancellationToken);

    public Task SetSnapshotAsync(Guid workspaceId, Guid reportId, Guid jobId, Guid snapshotId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.search_term_report SET snapshot_id = @snapshot WHERE workspace_id = @ws AND report_id = @id AND job_id = @job;
            UPDATE opportunity.job SET target_snapshot_id = @snapshot, updated_at = now()
            WHERE workspace_id = @ws AND job_id = @job AND target_snapshot_id IS DISTINCT FROM @snapshot
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", reportId);
                c.Parameters.AddWithValue("job", jobId);
                c.Parameters.AddWithValue("snapshot", snapshotId);
            }, cancellationToken);

    public Task MarkRunningAsync(
        Guid workspaceId, Guid reportId, Guid jobId, long searchGeneration, bool indexCurrent, DateTimeOffset executedAt,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.search_term_report
               SET status = 'Running', search_generation = @generation, index_current = @current, executed_at = @at
             WHERE workspace_id = @ws AND report_id = @id AND job_id = @job AND status IN ('Queued', 'Running')
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", reportId);
                c.Parameters.AddWithValue("job", jobId);
                c.Parameters.AddWithValue("generation", searchGeneration);
                c.Parameters.AddWithValue("current", indexCurrent);
                c.Parameters.AddWithValue("at", executedAt);
            }, cancellationToken);

    public Task SetTermErrorAsync(Guid workspaceId, Guid reportId, int termNo, SearchTermError termError, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(termError);
        return ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.search_term_report_term
               SET error_code = @code, error_message = @message, error_position = @position
             WHERE workspace_id = @ws AND report_id = @id AND term_no = @no AND error_code IS NULL
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", reportId);
                c.Parameters.AddWithValue("no", (short)termNo);
                c.Parameters.AddWithValue("code", Truncate(termError.Code, 100)!);
                c.Parameters.AddWithValue("message", Truncate(termError.Message, 2000)!);
                c.Parameters.Add(new NpgsqlParameter("position", NpgsqlDbType.Integer) { Value = (object?)termError.Position ?? DBNull.Value });
            }, cancellationToken);
    }

    public async Task WriteChunkAsync(Guid workspaceId, Guid reportId, Guid jobId, SearchTermChunkResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var documents = tx.Command(
            """
            INSERT INTO opportunity.search_term_report_document (workspace_id, report_id, job_id, document_id, family_id)
            SELECT d.workspace_id, @id, @job, d.document_id, d.family_id
            FROM opportunity.document d
            WHERE d.workspace_id = @ws AND d.document_id = ANY (@ids)
            ON CONFLICT DO NOTHING
            """))
        {
            documents.Parameters.AddWithValue("ws", workspaceId);
            documents.Parameters.AddWithValue("id", reportId);
            documents.Parameters.AddWithValue("job", jobId);
            documents.Parameters.AddWithValue("ids", result.VisibleDocuments.ToArray());
            await documents.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var pairs = result.Hits.SelectMany(h => h.Value.Select(d => (Term: (short)h.Key, Document: d))).ToList();
        if (pairs.Count > 0)
        {
            await using var hits = tx.Command(
                """
                INSERT INTO opportunity.search_term_report_hit (workspace_id, report_id, job_id, term_no, document_id)
                SELECT @ws, @id, @job, h.term_no, h.document_id FROM unnest(@terms, @documents) AS h(term_no, document_id)
                ON CONFLICT DO NOTHING
                """);
            hits.Parameters.AddWithValue("ws", workspaceId);
            hits.Parameters.AddWithValue("id", reportId);
            hits.Parameters.AddWithValue("job", jobId);
            hits.Parameters.AddWithValue("terms", pairs.Select(p => p.Term).ToArray());
            hits.Parameters.AddWithValue("documents", pairs.Select(p => p.Document).ToArray());
            await hits.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        Guid workspaceId, Guid reportId, Guid jobId, bool indexCurrentAtEnd, Func<SearchTermReportRecord, AuditEvent> audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var locked = tx.Command(
            """
            SELECT 1 FROM opportunity.search_term_report
            WHERE workspace_id = @ws AND report_id = @id AND job_id = @job AND status = 'Running' FOR UPDATE
            """))
        {
            locked.Parameters.AddWithValue("ws", workspaceId);
            locked.Parameters.AddWithValue("id", reportId);
            locked.Parameters.AddWithValue("job", jobId);
            if (await locked.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }
        }

        var expanded = FamilyHitSql.Expanded("run_scope", "run_hits");
        await using (var counts = tx.Command(
            $"""
            WITH run_scope AS (
                SELECT document_id, family_id FROM opportunity.search_term_report_document
                WHERE workspace_id = @ws AND report_id = @id AND job_id = @job),
            run_hits AS (
                SELECT h.term_no, h.document_id FROM opportunity.search_term_report_hit h
                JOIN opportunity.search_term_report_term t
                  ON t.workspace_id = h.workspace_id AND t.report_id = h.report_id AND t.term_no = h.term_no AND t.error_code IS NULL
                WHERE h.workspace_id = @ws AND h.report_id = @id AND h.job_id = @job),
            {expanded},
            term_counts AS ({FamilyHitSql.TermCounts}),
            terms_updated AS (
                UPDATE opportunity.search_term_report_term t
                   SET documents_with_hits = coalesce(c.with_hits, 0), unique_hits = coalesce(c.unique_hits, 0),
                       documents_with_hits_family = coalesce(c.with_family, 0), unique_hits_family = coalesce(c.unique_with_family, 0)
                  FROM opportunity.search_term_report_term t2
                  LEFT JOIN term_counts c ON c.term_no = t2.term_no
                 WHERE t.workspace_id = @ws AND t.report_id = @id AND t.error_code IS NULL
                   AND t2.workspace_id = t.workspace_id AND t2.report_id = t.report_id AND t2.term_no = t.term_no
                RETURNING 1),
            totals AS ({FamilyHitSql.Totals})
            UPDATE opportunity.search_term_report r
               SET status = 'Completed', status_reason = NULL, completed_at = now(), claimed_by = NULL, claimed_until = NULL,
                   index_current = coalesce(r.index_current, false) AND @currentAtEnd,
                   documents_in_scope = x.in_scope, documents_with_hits = x.with_hits, documents_with_hits_family = x.with_family,
                   documents_without_hits = x.in_scope - x.with_hits,
                   excluded_no_access = (SELECT j.items_excluded_no_access FROM opportunity.job j WHERE j.workspace_id = @ws AND j.job_id = @job)
              FROM totals x
             WHERE r.workspace_id = @ws AND r.report_id = @id AND (SELECT count(*) FROM terms_updated) >= 0
            """))
        {
            counts.Parameters.AddWithValue("ws", workspaceId);
            counts.Parameters.AddWithValue("id", reportId);
            counts.Parameters.AddWithValue("job", jobId);
            counts.Parameters.AddWithValue("currentAtEnd", indexCurrentAtEnd);
            await counts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The hit set of the current run is what "open the term as a search" serves; earlier runs' rows go.
        await using (var cleanup = tx.Command(
            """
            DELETE FROM opportunity.search_term_report_hit WHERE workspace_id = @ws AND report_id = @id AND job_id <> @job;
            DELETE FROM opportunity.search_term_report_document WHERE workspace_id = @ws AND report_id = @id AND job_id <> @job;
            """))
        {
            cleanup.Parameters.AddWithValue("ws", workspaceId);
            cleanup.Parameters.AddWithValue("id", reportId);
            cleanup.Parameters.AddWithValue("job", jobId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = (await ReadOneAsync(tx, "r.report_id = @id", reportId, cancellationToken).ConfigureAwait(false))!;
        await AuditSql.InsertAsync(tx, audit(record) with { EventId = Guid.CreateVersion7(), WorkspaceId = workspaceId }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> FailAsync(Guid workspaceId, Guid reportId, Guid jobId, string reason, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.search_term_report
               SET status = 'Failed', status_reason = @reason, completed_at = now(), claimed_by = NULL, claimed_until = NULL
             WHERE workspace_id = @ws AND report_id = @id AND job_id = @job AND status IN ('Queued', 'Running')
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", reportId);
                c.Parameters.AddWithValue("job", jobId);
                c.Parameters.AddWithValue("reason", Truncate(reason, 2000)!);
            }, cancellationToken).ConfigureAwait(false) == 1;

    public async Task<IReadOnlyList<Guid>> GetHitsAsync(Guid workspaceId, Guid reportId, int termNo, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT h.document_id FROM opportunity.search_term_report_hit h
            JOIN opportunity.search_term_report r ON r.workspace_id = h.workspace_id AND r.report_id = h.report_id AND r.job_id = h.job_id
            WHERE h.workspace_id = @ws AND h.report_id = @id AND h.term_no = @no
            ORDER BY h.document_id LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", reportId);
        command.Parameters.AddWithValue("no", (short)termNo);
        command.Parameters.AddWithValue("limit", limit);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private async Task<int> ExecuteAsync(Guid workspaceId, string sql, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        int rows;
        await using (var command = tx.Command(sql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            parameters(command);
            rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static async Task<SearchTermReportRecord?> ReadOneAsync(WorkspaceTransaction tx, string where, Guid id, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.search_term_report r WHERE r.workspace_id = @ws AND {where}");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static SearchTermReportRecord Read(NpgsqlDataReader reader) => new()
    {
        WorkspaceId = reader.GetGuid(0),
        ReportId = reader.GetGuid(1),
        Name = reader.GetString(2),
        ScopeKind = Enum.Parse<SearchTermReportScopeKind>(reader.GetString(3)),
        ScopeId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
        ScopeName = reader.IsDBNull(5) ? null : reader.GetString(5),
        Status = Enum.Parse<SearchTermReportStatus>(reader.GetString(6)),
        StatusReason = reader.IsDBNull(7) ? null : reader.GetString(7),
        JobId = reader.GetGuid(8),
        RunCount = reader.GetInt32(9),
        SnapshotId = reader.IsDBNull(10) ? null : reader.GetGuid(10),
        SearchGeneration = Long(reader, 11),
        IndexCurrent = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
        DocumentsInScope = Long(reader, 13),
        DocumentsWithHits = Long(reader, 14),
        DocumentsWithHitsIncludingFamily = Long(reader, 15),
        DocumentsWithoutHits = Long(reader, 16),
        ExcludedNoAccess = Long(reader, 17),
        CreatedBy = reader.GetGuid(18),
        CreatedByDisplay = reader.GetString(19),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(20),
        ExecutedBy = reader.GetGuid(21),
        ExecutedByDisplay = reader.GetString(22),
        ExecutedByGroups = reader.GetFieldValue<string[]>(23),
        ExecutedAt = reader.IsDBNull(24) ? null : reader.GetFieldValue<DateTimeOffset>(24),
        CompletedAt = reader.IsDBNull(25) ? null : reader.GetFieldValue<DateTimeOffset>(25),
        TermCount = reader.GetInt32(26),
    };

    private static long? Long(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

public static class SearchTermReportStoreRegistration
{
    public static IServiceCollection AddPostgresSearchTermReportStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISearchTermReportStore, SearchTermReportStore>();
        return services;
    }
}
