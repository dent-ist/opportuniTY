using System.Data;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Exports;
using Opportunity.Application.Jobs;
using Opportunity.Application.Storage;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;
using Opportunity.Data.Fields;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;

using ObjectArea = Opportunity.Core.Storage.ObjectArea;

namespace Opportunity.Data.Exports;

/// <summary>
/// PostgreSQL implementation of <see cref="IExportStore"/> over <c>export</c>, <c>export_document</c> and
/// <c>export_file</c> (V0024). A chunk's outcome rows, its file registrations (StoredObject, ADR-011 §2.4), its audit
/// events and fence F3 commit together or not at all, like the import chunk.
/// </summary>
public sealed class ExportRepository(NpgsqlDataSource dataSource) : IExportStore
{
    /// <summary>The largest document batch one <see cref="ReadDocumentsAsync"/> reads.</summary>
    public const int MaxDocumentsPerRead = ProjectionSourceReader.MaxDocumentsPerRead;

    private const string Columns =
        """
        e.workspace_id, e.export_id, e.job_id, e.snapshot_id, e.name, e.settings::text, e.status, e.status_reason, e.created_by,
        e.created_by_display, e.created_by_groups, e.created_at, e.completed_at, e.documents_exported, e.documents_excluded,
        e.natives, e.texts, e.images, e.pages, e.file_count, e.total_bytes, e.manifest_sha256, e.production_id
        """;

    private const string FileColumns =
        "f.file_id, f.path, f.kind, o.logical_key, f.sha256, f.size_bytes, f.content_type, f.chunk_sequence, f.ordinal";

    private static readonly short[] PagePurposes = [(short)PageImagePurpose.Original, (short)PageImagePurpose.Review];

    public async Task<ExportCreation> CreateAsync(NewExport request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AuditTemplate);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var job = await JobRepository.CreateInTransactionAsync(tx, new NewJob
        {
            WorkspaceId = request.WorkspaceId,
            JobId = request.JobId,
            JobType = request.ProductionId is null ? JobType.Export : JobType.Production,
            InitiatedBy = request.InitiatedBy,
            TargetSnapshotId = request.SnapshotId,
            Parameters = request.Parameters ?? new JsonObject { ["exportId"] = request.ExportId.ToString() },
            ClientIdempotencyKey = request.ClientIdempotencyKey,
            CorrelationId = request.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);

        if (!job.Created)
        {
            var existing = await ReadOneAsync(tx, "e.job_id = @id", job.Job.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Job {job.Job.JobId} has no request.");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ExportCreation(existing, job.Job, false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.export
                (workspace_id, export_id, job_id, snapshot_id, name, settings, created_by, created_by_display, created_by_groups, production_id)
            VALUES (@ws, @id, @job, @snapshot, @name, @settings::jsonb, @by, @display, @groups, @production)
            """))
        {
            insert.Parameters.Add(Nullable("production", NpgsqlDbType.Uuid, request.ProductionId));
            insert.Parameters.AddWithValue("ws", request.WorkspaceId);
            insert.Parameters.AddWithValue("id", request.ExportId);
            insert.Parameters.AddWithValue("job", request.JobId);
            insert.Parameters.AddWithValue("snapshot", request.SnapshotId);
            insert.Parameters.AddWithValue("name", request.Name.Trim());
            insert.Parameters.AddWithValue("settings", request.SettingsJson);
            insert.Parameters.AddWithValue("by", request.InitiatedBy);
            insert.Parameters.AddWithValue("display", Truncate(request.InitiatedByDisplay, 512));
            insert.Parameters.AddWithValue("groups", request.InitiatedByGroups.ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A production volume run (E12-T05) is audited as the production's event the caller built (Production.Run/Rerun).
        await AuditSql.InsertAsync(tx, request.ProductionId is { } productionId ? request.AuditTemplate with
        {
            EventId = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId,
            ResourceType = AuditTaxonomy.Production.ResourceType,
            ResourceId = productionId.ToString(),
            JobId = request.JobId,
            SnapshotId = request.SnapshotId,
        } : request.AuditTemplate with
        {
            EventId = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId,
            Category = AuditTaxonomy.Export.Category,
            Action = AuditTaxonomy.Export.Created,
            ResourceType = AuditTaxonomy.Export.ResourceType,
            ResourceId = request.ExportId.ToString(),
            JobId = request.JobId,
            SnapshotId = request.SnapshotId,
            Outcome = AuditOutcome.Success,
            Details = request.AuditTemplate.Details.Count > 0
                ? request.AuditTemplate.Details
                : new Dictionary<string, string?>(StringComparer.Ordinal) { ["ExportId"] = request.ExportId.ToString() },
        }, cancellationToken).ConfigureAwait(false);

        var record = (await ReadOneAsync(tx, "e.export_id = @id", request.ExportId, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExportCreation(record, job.Job, true);
    }

    public Task<ExportRecord?> GetAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default) =>
        ReadOneAsync(workspaceId, "e.export_id = @id", exportId, cancellationToken);

    public Task<ExportRecord?> GetByJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default) =>
        ReadOneAsync(workspaceId, "e.job_id = @id", jobId, cancellationToken);

    public async Task<IReadOnlyList<ExportRecord>> ListAsync(
        Guid workspaceId, Guid? createdBy, ExportListCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.export e
            WHERE e.workspace_id = @ws AND e.production_id IS NULL AND (@by::uuid IS NULL OR e.created_by = @by)
              AND (@after_at::timestamptz IS NULL OR (e.created_at, e.export_id) < (@after_at, @after_id))
            ORDER BY e.created_at DESC, e.export_id DESC
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(Nullable("by", NpgsqlDbType.Uuid, createdBy));
        command.Parameters.Add(Nullable("after_at", NpgsqlDbType.TimestampTz, after?.CreatedAt));
        command.Parameters.Add(Nullable("after_id", NpgsqlDbType.Uuid, after?.ExportId));
        command.Parameters.AddWithValue("limit", limit);
        var records = new List<ExportRecord>();
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

    public Task<IReadOnlyList<ActiveExport>> GetActiveAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default) =>
        ReadActiveAsync(workspaceId, volumes: false, limit, cancellationToken);

    public Task<IReadOnlyList<ActiveExport>> GetActiveVolumesAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default) =>
        ReadActiveAsync(workspaceId, volumes: true, limit, cancellationToken);

    public async Task<IReadOnlyList<ExportRecord>> ListVolumesAsync(
        Guid workspaceId, Guid productionId, ExportListCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.export e
            WHERE e.workspace_id = @ws AND e.production_id = @production
              AND (@after_at::timestamptz IS NULL OR (e.created_at, e.export_id) < (@after_at, @after_id))
            ORDER BY e.created_at DESC, e.export_id DESC
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("production", productionId);
        command.Parameters.Add(Nullable("after_at", NpgsqlDbType.TimestampTz, after?.CreatedAt));
        command.Parameters.Add(Nullable("after_id", NpgsqlDbType.Uuid, after?.ExportId));
        command.Parameters.AddWithValue("limit", limit);
        var records = new List<ExportRecord>();
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

    private async Task<IReadOnlyList<ActiveExport>> ReadActiveAsync(Guid workspaceId, bool volumes, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns}, j.status, j.chunks_failed
            FROM opportunity.export e
            JOIN opportunity.job j ON j.workspace_id = e.workspace_id AND j.job_id = e.job_id
            WHERE e.workspace_id = @ws AND e.status = 1 AND (e.production_id IS NOT NULL) = @volumes
            ORDER BY e.created_at, e.export_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("volumes", volumes);
        command.Parameters.AddWithValue("limit", limit);
        var active = new List<ActiveExport>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                active.Add(new ActiveExport(Read(reader), Enum.Parse<JobStatus>(reader.GetString(23)), reader.GetInt32(24)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<bool> TryClaimAsync(Guid workspaceId, Guid exportId, string owner, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.export
            SET claimed_by = @owner, claimed_until = now() + @lease
            WHERE workspace_id = @ws AND export_id = @id AND status = 1
              AND (claimed_by IS NULL OR claimed_by = @owner OR claimed_until < now())
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("owner", Truncate(owner, 200));
        command.Parameters.AddWithValue("lease", lease);
        var claimed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task ReleaseClaimAsync(Guid workspaceId, Guid exportId, string owner, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "UPDATE opportunity.export SET claimed_by = NULL, claimed_until = NULL WHERE workspace_id = @ws AND export_id = @id AND claimed_by = @owner");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("owner", Truncate(owner, 200));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChunkCommitResult> ApplyChunkAsync(
        ClaimedChunk chunk, ExportChunkWrite write, ChunkCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(completion);
        var lease = chunk.Lease;
        var membership = chunk.Membership;
        if (chunk.JobType is not (JobType.Export or JobType.Production) || membership.Kind != ChunkMembershipKind.SnapshotRange
            || write.Documents.Any(d => d.Ordinal < membership.RangeFrom || d.Ordinal > membership.RangeTo)
            || write.Documents.Select(d => d.Ordinal).Distinct().Count() != write.Documents.Count)
        {
            throw new ArgumentException("An export chunk write covers distinct members of a leased SnapshotRange export chunk.", nameof(write));
        }

        ChunkCommitResult commit;
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            var export = await ReadOneAsync(tx, "e.export_id = @id", write.ExportId, cancellationToken).ConfigureAwait(false);
            if (export is null || export.JobId != lease.JobId || (chunk.JobType == JobType.Production) != export.ProductionId.HasValue)
            {
                throw new ArgumentException("The chunk's export does not exist or belongs to another job.", nameof(write));
            }

            await InsertDocumentsAsync(tx, write.ExportId, chunk.Sequence, write.Documents, cancellationToken).ConfigureAwait(false);
            await InsertFilesAsync(tx, write.ExportId, lease.JobId, chunk.Sequence, write.Files, cancellationToken).ConfigureAwait(false);
            foreach (var auditEvent in write.Audit)
            {
                await AuditSql.InsertAsync(tx, auditEvent, cancellationToken).ConfigureAwait(false);
            }

            // Fence F3 after the chunk's own writes.
            commit = await JobChunkRepository.CommitInTransactionAsync(tx, lease, completion, cancellationToken).ConfigureAwait(false);
            if (commit.Committed)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return commit;
            }
        }

        // Fence F3 refused: everything rolled back with the transaction; record the fence outcome on the chunk.
        if (commit.Outcome != ChunkCommitOutcome.LeaseLost)
        {
            var release = await new JobChunkRepository(dataSource).ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
            commit = release switch
            {
                ChunkReleaseOutcome.Cancelled => commit with { Outcome = ChunkCommitOutcome.Cancelled },
                ChunkReleaseOutcome.ReturnedToPending => commit with { Outcome = ChunkCommitOutcome.JobNotRunning },
                _ => commit with { Outcome = ChunkCommitOutcome.LeaseLost },
            };
        }

        return commit;
    }

    public async Task<IReadOnlyList<ExportFileRecord>> GetFilesAsync(
        Guid workspaceId, Guid exportId, IReadOnlyCollection<ExportFileKind> kinds, string? afterPath, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {FileColumns}
            FROM opportunity.export_file f
            JOIN opportunity.stored_object o ON o.workspace_id = f.workspace_id AND o.object_id = f.object_id
            WHERE f.workspace_id = @ws AND f.export_id = @id AND f.kind = ANY(@kinds) AND (@after::text IS NULL OR f.path > @after COLLATE "C")
            ORDER BY f.path COLLATE "C"
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.Add(new NpgsqlParameter<short[]>("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
        {
            TypedValue = [.. kinds.Select(k => (short)k)],
        });
        command.Parameters.Add(Nullable("after", NpgsqlDbType.Text, afterPath));
        command.Parameters.AddWithValue("limit", limit);
        var files = new List<ExportFileRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                files.Add(ReadFile(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return files;
    }

    public async Task<ExportFileRecord?> GetFileAsync(Guid workspaceId, Guid exportId, Guid fileId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {FileColumns}
            FROM opportunity.export_file f
            JOIN opportunity.stored_object o ON o.workspace_id = f.workspace_id AND o.object_id = f.object_id
            WHERE f.workspace_id = @ws AND f.export_id = @id AND f.file_id = @file
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("file", fileId);
        ExportFileRecord? file = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                file = ReadFile(reader);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return file;
    }

    public async Task<IReadOnlyList<ExportDocumentOutcome>> GetExclusionsAsync(
        Guid workspaceId, Guid exportId, long afterOrdinal, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT ordinal, document_id, control_number, reason
            FROM opportunity.export_document
            WHERE workspace_id = @ws AND export_id = @id AND outcome = 2 AND ordinal > @after
            ORDER BY ordinal
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("after", afterOrdinal);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<ExportDocumentOutcome>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ExportDocumentOutcome(
                    reader.GetInt64(0), reader.GetGuid(1), true, reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<bool> CompleteAsync(
        Guid workspaceId, Guid exportId, IReadOnlyList<NewExportFile> files, ExportReport report, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        Guid jobId;
        await using (var update = tx.Command(
            """
            UPDATE opportunity.export
            SET status = 2, completed_at = now(), claimed_by = NULL, claimed_until = NULL,
                documents_exported = @exported, documents_excluded = @excluded, natives = @natives, texts = @texts,
                images = @images, pages = @pages, file_count = @files, total_bytes = @bytes, manifest_sha256 = @manifest
            WHERE workspace_id = @ws AND export_id = @id AND status = 1
            RETURNING job_id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", exportId);
            update.Parameters.AddWithValue("exported", report.DocumentsExported);
            update.Parameters.AddWithValue("excluded", report.DocumentsExcluded);
            update.Parameters.AddWithValue("natives", report.Natives);
            update.Parameters.AddWithValue("texts", report.Texts);
            update.Parameters.AddWithValue("images", report.Images);
            update.Parameters.AddWithValue("pages", report.Pages);
            update.Parameters.AddWithValue("files", report.Files);
            update.Parameters.AddWithValue("bytes", report.TotalBytes);
            update.Parameters.AddWithValue("manifest", report.ManifestSha256);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid job)
            {
                return false;
            }

            jobId = job;
        }

        await InsertFilesAsync(tx, exportId, jobId, null, files, cancellationToken).ConfigureAwait(false);
        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> EndAsync(Guid workspaceId, Guid exportId, ExportStatus status, string reason, CancellationToken cancellationToken = default)
    {
        if (status is not (ExportStatus.Failed or ExportStatus.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "An export ends as Failed or Cancelled here.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.export
            SET status = @status, status_reason = @reason, completed_at = now(), claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND export_id = @id AND status = 1
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("status", (short)status);
        command.Parameters.AddWithValue("reason", Truncate(reason, 2000));
        var ended = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ended;
    }

    public async Task<ExportDocumentTotals> GetDocumentTotalsAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT count(*) FILTER (WHERE outcome = 1), count(*) FILTER (WHERE outcome = 2),
                   coalesce(sum(natives), 0)::bigint, coalesce(sum(texts), 0)::bigint, coalesce(sum(images), 0)::bigint,
                   coalesce(sum(pages), 0)::bigint
            FROM opportunity.export_document WHERE workspace_id = @ws AND export_id = @id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", exportId);
        ExportDocumentTotals totals;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            totals = new ExportDocumentTotals(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return totals;
    }

    public async Task<ExportSourceBatch> ReadDocumentsAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        var ids = documentIds.Distinct().ToArray();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ids.Length, MaxDocumentsPerRead, nameof(documentIds));
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var catalog = await FieldCatalogRepository.LoadCatalogAsync(tx, workspaceId, includeDeleted: false, cancellationToken).ConfigureAwait(false);

        var documents = new Dictionary<Guid, Document>();
        await using (var command = tx.Command(ProjectionSourceReader.DocumentSql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.GetBoolean(2))
                {
                    var document = ProjectionSourceReader.ReadDocument(reader, workspaceId);
                    documents[document.DocumentId] = document;
                }
            }
        }

        var live = documents.Keys.ToArray();
        var artifacts = await ReadArtifactsAsync(tx, workspaceId, live, cancellationToken).ConfigureAwait(false);
        var pages = await ReadPagesAsync(tx, workspaceId, live, cancellationToken).ConfigureAwait(false);
        var related = documents.Values.SelectMany(d => d.ParentDocumentId is { } p ? [p, d.FamilyId] : new[] { d.FamilyId }).Distinct().ToArray();
        var controlNumbers = await ReadControlNumbersAsync(tx, workspaceId, related, cancellationToken).ConfigureAwait(false);
        var families = await ReadFamiliesAsync(tx, workspaceId, [.. documents.Values.Select(d => d.FamilyId).Distinct()], cancellationToken)
            .ConfigureAwait(false);
        var coding = await CodingRepository.ReadCurrentValuesAsync(tx, workspaceId, live, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<Guid, ExportSourceDocument>(documents.Count);
        foreach (var (id, document) in documents)
        {
            var (native, text) = artifacts.GetValueOrDefault(id);
            var family = families.GetValueOrDefault(document.FamilyId);
            var multi = family.Count > 1;
            result[id] = new ExportSourceDocument(
                document,
                coding.GetValueOrDefault(id) ?? [],
                native,
                text,
                pages.GetValueOrDefault(id) ?? [],
                document.ParentDocumentId is { } parent ? controlNumbers.GetValueOrDefault(parent) : null,
                controlNumbers.GetValueOrDefault(document.FamilyId) ?? document.ControlNumber,
                multi ? family.First : null,
                multi ? family.Last : null);
        }

        return new ExportSourceBatch(catalog, result);
    }

    public async Task<ExportInitiator?> ReadInitiatorAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT coalesce(display_name, email, subject), groups FROM opportunity.app_user WHERE user_id = @id");
        command.Parameters.AddWithValue("id", userId);
        ExportInitiator? initiator = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                initiator = new ExportInitiator(reader.GetString(0), reader.GetFieldValue<string[]>(1));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return initiator;
    }

    private static async Task<Dictionary<Guid, (ExportSourceObject? Native, ExportSourceObject? Text)>> ReadArtifactsAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid[] ids, CancellationToken cancellationToken)
    {
        var artifacts = new Dictionary<Guid, (ExportSourceObject?, ExportSourceObject?)>();
        await using var command = tx.Command(
            """
            SELECT d.document_id, n.logical_key, n.sha256, n.size_bytes, n.content_type, t.logical_key, t.sha256, t.size_bytes, t.content_type
            FROM opportunity.document d
            LEFT JOIN opportunity.stored_object n
                   ON n.workspace_id = d.workspace_id AND n.object_id = d.native_object_id AND n.area = @native AND n.state = @committed
            LEFT JOIN opportunity.stored_object t
                   ON t.workspace_id = d.workspace_id AND t.object_id = d.text_object_id AND t.area = @text AND t.state = @committed
            WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids)
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("ids", ids);
        command.Parameters.AddWithValue("native", (short)ObjectArea.Native);
        command.Parameters.AddWithValue("text", (short)ObjectArea.Text);
        command.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            artifacts[reader.GetGuid(0)] = (Source(reader, 1), Source(reader, 5));
        }

        return artifacts;
    }

    private static async Task<Dictionary<Guid, List<ExportSourcePage>>> ReadPagesAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid[] ids, CancellationToken cancellationToken)
    {
        var pages = new Dictionary<Guid, List<ExportSourcePage>>();
        await using var command = tx.Command(
            """
            SELECT d.document_id, p.ordinal, o.logical_key, o.sha256, o.size_bytes, o.content_type, img.format
            FROM opportunity.document d
            JOIN opportunity.page p
              ON p.workspace_id = d.workspace_id AND p.page_set_id = d.active_page_set_id AND p.document_id = d.document_id
            LEFT JOIN LATERAL (
                SELECT pi.object_id, pi.format
                FROM opportunity.page_image pi
                JOIN opportunity.stored_object so
                  ON so.workspace_id = pi.workspace_id AND so.object_id = pi.object_id AND so.state = @committed
                WHERE pi.workspace_id = p.workspace_id AND pi.page_set_id = p.page_set_id AND pi.ordinal = p.ordinal
                  AND pi.purpose = ANY(@purposes)
                ORDER BY array_position(@purposes, pi.purpose)
                LIMIT 1) img ON true
            LEFT JOIN opportunity.stored_object o ON o.workspace_id = d.workspace_id AND o.object_id = img.object_id
            WHERE d.workspace_id = @ws AND d.document_id = ANY(@ids)
            ORDER BY d.document_id, p.ordinal
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("ids", ids);
        command.Parameters.AddWithValue("committed", (short)StoredObjectState.Committed);
        command.Parameters.Add(new NpgsqlParameter<short[]>("purposes", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = PagePurposes });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            if (!pages.TryGetValue(id, out var list))
            {
                pages[id] = list = [];
            }

            var image = Source(reader, 2);
            list.Add(new ExportSourcePage(reader.GetInt32(1), image, image is null || reader.IsDBNull(6) ? null : (PageImageFormat)reader.GetInt16(6)));
        }

        return pages;
    }

    private static async Task<Dictionary<Guid, string>> ReadControlNumbersAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid[] ids, CancellationToken cancellationToken)
    {
        var numbers = new Dictionary<Guid, string>();
        if (ids.Length == 0)
        {
            return numbers;
        }

        await using var command = tx.Command(
            "SELECT document_id, control_number FROM opportunity.document WHERE workspace_id = @ws AND document_id = ANY(@ids)");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("ids", ids);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            numbers[reader.GetGuid(0)] = reader.GetString(1);
        }

        return numbers;
    }

    /// <summary>Live members per family: count and the first and last control number in control-number order.</summary>
    private static async Task<Dictionary<Guid, (int Count, string First, string Last)>> ReadFamiliesAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid[] familyIds, CancellationToken cancellationToken)
    {
        var families = new Dictionary<Guid, (int, string, string)>();
        if (familyIds.Length == 0)
        {
            return families;
        }

        await using var command = tx.Command(
            """
            SELECT m.family_id, count(*)::int,
                   (array_agg(m.control_number ORDER BY m.control_number_sort_key, m.control_number_norm))[1],
                   (array_agg(m.control_number ORDER BY m.control_number_sort_key DESC, m.control_number_norm DESC))[1]
            FROM opportunity.document m
            JOIN opportunity.document_projection_state s
              ON s.workspace_id = m.workspace_id AND s.document_id = m.document_id AND NOT s.is_deleted
            WHERE m.workspace_id = @ws AND m.family_id = ANY(@families)
            GROUP BY m.family_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("families", familyIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            families[reader.GetGuid(0)] = (reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
        }

        return families;
    }

    private static async Task InsertDocumentsAsync(
        WorkspaceTransaction tx, Guid exportId, int chunkSequence, IReadOnlyList<ExportDocumentOutcome> documents, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return;
        }

        await using var insert = tx.Command(
            """
            INSERT INTO opportunity.export_document
                (workspace_id, export_id, ordinal, document_id, chunk_sequence, outcome, control_number, reason, natives, texts, images, pages)
            SELECT @ws, @id, u.ordinal, u.document_id, @sequence, u.outcome, u.control_number, u.reason, u.natives, u.texts, u.images, u.pages
            FROM unnest(@ordinals, @documents, @outcomes, @numbers, @reasons, @natives, @texts, @images, @pages)
                AS u(ordinal, document_id, outcome, control_number, reason, natives, texts, images, pages)
            """);
        insert.Parameters.AddWithValue("ws", tx.WorkspaceId);
        insert.Parameters.AddWithValue("id", exportId);
        insert.Parameters.AddWithValue("sequence", chunkSequence);
        insert.Parameters.AddWithValue("ordinals", documents.Select(d => d.Ordinal).ToArray());
        insert.Parameters.AddWithValue("documents", documents.Select(d => d.DocumentId).ToArray());
        insert.Parameters.AddWithValue("outcomes", documents.Select(d => (short)(d.Excluded ? 2 : 1)).ToArray());
        insert.Parameters.Add(new NpgsqlParameter<string?[]>("numbers", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = [.. documents.Select(d => d.ControlNumber)],
        });
        insert.Parameters.Add(new NpgsqlParameter<string?[]>("reasons", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = [.. documents.Select(d => d.Excluded ? d.Reason ?? "AccessChanged" : null)],
        });
        insert.Parameters.AddWithValue("natives", documents.Select(d => d.Natives).ToArray());
        insert.Parameters.AddWithValue("texts", documents.Select(d => d.Texts).ToArray());
        insert.Parameters.AddWithValue("images", documents.Select(d => d.Images).ToArray());
        insert.Parameters.AddWithValue("pages", documents.Select(d => d.Pages).ToArray());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers generated export objects (area Export) and lists them as files of the export.</summary>
    private static async Task InsertFilesAsync(
        WorkspaceTransaction tx, Guid exportId, Guid jobId, int? chunkSequence, IReadOnlyList<NewExportFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        foreach (var file in files)
        {
            if (!ObjectKey.TryParse(file.ObjectKey, out var key) || key.Area != Application.Storage.ObjectArea.Export
                || key.WorkspaceId != tx.WorkspaceId || file.Sha256 is not { Length: 32 })
            {
                throw new ArgumentException($"{file.ObjectKey} is not an export object of this workspace with a SHA-256.", nameof(files));
            }
        }

        var objectIds = files.Select(_ => Guid.CreateVersion7()).ToArray();
        await using (var register = tx.Command(
            """
            INSERT INTO opportunity.stored_object
                (workspace_id, object_id, logical_key, area, sha256, size_bytes, content_type, key_id, encryption_scheme, state, created_by_job_id)
            SELECT @ws, u.object_id, u.logical_key, @area, u.sha256, u.size_bytes, u.content_type, u.key_id, u.scheme, 1, @job
            FROM unnest(@object_ids, @keys, @shas, @sizes, @types, @key_ids, @schemes)
                AS u(object_id, logical_key, sha256, size_bytes, content_type, key_id, scheme)
            """))
        {
            register.Parameters.AddWithValue("ws", tx.WorkspaceId);
            register.Parameters.AddWithValue("area", (short)ObjectArea.Export);
            register.Parameters.AddWithValue("job", jobId);
            register.Parameters.AddWithValue("object_ids", objectIds);
            register.Parameters.AddWithValue("keys", files.Select(f => f.ObjectKey).ToArray());
            register.Parameters.AddWithValue("shas", files.Select(f => f.Sha256).ToArray());
            register.Parameters.AddWithValue("sizes", files.Select(f => f.SizeBytes).ToArray());
            register.Parameters.AddWithValue("types", files.Select(f => f.ContentType).ToArray());
            register.Parameters.AddWithValue("key_ids", files.Select(f => f.KeyId).ToArray());
            register.Parameters.AddWithValue("schemes", files.Select(f => (short)f.EncryptionScheme).ToArray());
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = tx.Command(
            """
            INSERT INTO opportunity.export_file
                (workspace_id, export_id, file_id, path, kind, object_id, sha256, size_bytes, content_type, chunk_sequence, ordinal)
            SELECT @ws, @id, u.file_id, u.path, u.kind, u.object_id, u.sha256, u.size_bytes, u.content_type, @sequence, u.ordinal
            FROM unnest(@file_ids, @paths, @kinds, @object_ids, @shas, @sizes, @types, @ordinals)
                AS u(file_id, path, kind, object_id, sha256, size_bytes, content_type, ordinal)
            """);
        insert.Parameters.AddWithValue("ws", tx.WorkspaceId);
        insert.Parameters.AddWithValue("id", exportId);
        insert.Parameters.Add(Nullable("sequence", NpgsqlDbType.Integer, chunkSequence));
        insert.Parameters.AddWithValue("file_ids", files.Select(_ => Guid.CreateVersion7()).ToArray());
        insert.Parameters.AddWithValue("paths", files.Select(f => f.Path).ToArray());
        insert.Parameters.AddWithValue("kinds", files.Select(f => (short)f.Kind).ToArray());
        insert.Parameters.AddWithValue("object_ids", objectIds);
        insert.Parameters.AddWithValue("shas", files.Select(f => f.Sha256).ToArray());
        insert.Parameters.AddWithValue("sizes", files.Select(f => f.SizeBytes).ToArray());
        insert.Parameters.AddWithValue("types", files.Select(f => f.ContentType).ToArray());
        insert.Parameters.Add(new NpgsqlParameter<long?[]>("ordinals", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            TypedValue = [.. files.Select(f => f.Ordinal)],
        });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExportRecord?> ReadOneAsync(Guid workspaceId, string condition, Guid id, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await ReadOneAsync(tx, condition, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static async Task<ExportRecord?> ReadOneAsync(WorkspaceTransaction tx, string condition, Guid id, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.export e WHERE e.workspace_id = @ws AND {condition}");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static ExportRecord Read(NpgsqlDataReader r)
    {
        var status = (ExportStatus)r.GetInt16(6);
        return new ExportRecord
        {
            WorkspaceId = r.GetGuid(0),
            ExportId = r.GetGuid(1),
            JobId = r.GetGuid(2),
            SnapshotId = r.GetGuid(3),
            Name = r.GetString(4),
            SettingsJson = r.GetString(5),
            Status = status,
            StatusReason = r.IsDBNull(7) ? null : r.GetString(7),
            CreatedBy = r.GetGuid(8),
            CreatedByDisplay = r.GetString(9),
            CreatedByGroups = r.GetFieldValue<string[]>(10),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(11),
            CompletedAt = r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
            Report = status == ExportStatus.Completed
                ? new ExportReport(
                    r.GetInt64(13), r.GetInt64(14), r.GetInt64(15), r.GetInt64(16), r.GetInt64(17), r.GetInt64(18), r.GetInt64(19),
                    r.GetInt64(20), r.GetFieldValue<byte[]>(21))
                : null,
            ProductionId = r.IsDBNull(22) ? null : r.GetGuid(22),
        };
    }

    private static ExportFileRecord ReadFile(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        (ExportFileKind)r.GetInt16(2),
        r.GetString(3),
        r.GetFieldValue<byte[]>(4),
        r.GetInt64(5),
        r.GetString(6),
        r.IsDBNull(7) ? null : r.GetInt32(7),
        r.IsDBNull(8) ? null : r.GetInt64(8));

    private static ExportSourceObject? Source(NpgsqlDataReader r, int offset) =>
        r.IsDBNull(offset)
            ? null
            : new ExportSourceObject(r.GetString(offset), r.GetFieldValue<byte[]>(offset + 1), r.GetInt64(offset + 2),
                r.IsDBNull(offset + 3) ? null : r.GetString(offset + 3));

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

public static class ExportStoreRegistration
{
    public static IServiceCollection AddPostgresExportStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExportStore, ExportRepository>();
        return services;
    }
}
