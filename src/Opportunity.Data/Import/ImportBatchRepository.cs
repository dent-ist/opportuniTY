using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Documents;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.SearchWork;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Core.Storage;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;
using Opportunity.Data.Documents;
using Opportunity.Data.Jobs;
using Opportunity.Data.Relationships;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Security;
using Opportunity.Data.Storage;

namespace Opportunity.Data.Import;

/// <summary>
/// PostgreSQL implementation of <see cref="IImportBatchStore"/> over <c>import_batch</c> and its satellite tables (V0018).
/// The chunk write follows the bulk-coding pattern (<c>CodingRepository.ApplyChunkAsync</c>): every write of the chunk,
/// fence F3 and the chunk's one IndexChunkTask (the transaction's last statement) commit together or not at all.
/// </summary>
/// <remarks>
/// Overlays (E08-T07) change only the values a row supplies (blanks too when blank values overwrite), never coding
/// unless the import enabled coding fields (Q-31). Every changed document gets a DocumentVersion bump, a
/// <c>document_overlay_event</c> row with its old and new values, and is reindexed by the chunk's IndexChunkTask; the
/// chunk's <c>Import.Overlaid</c> audit event references those rows (ADR-013 §6.2; §7: audit never holds metadata values).
/// Security-affecting coding values recompute the restriction classes through <paramref name="restrictions"/> in the
/// chunk's transaction (§24 rule 1).
/// </remarks>
public sealed partial class ImportBatchRepository(NpgsqlDataSource dataSource, IRestrictionClassBinding? restrictions = null) : IImportBatchStore
{
    /// <summary>Actor of chunk-level audit events: the import worker, on behalf of the job's initiator.</summary>
    public const string ImportWorkerActor = "service:import";

    private const short ActionImported = 1;
    private const short ActionOverlaid = 2;
    private const short ActionSkipped = 3;

    private const string Columns =
        """
        workspace_id, import_batch_id, job_id, name, mode, source_file_name, source_object_key, source_sha256, source_size,
        profile_id, profile_version, profile::text, coding_overlay_field_ids, header::text, dat_encoding, dat_encoding_fallback,
        data_offset, rows_total, fields_created, choices_created, prepared_at, rows_imported, rows_overlaid, rows_skipped,
        rows_errored, created_by, created_at, completed_at, may_create_fields, opt_file_name, opt_object_key, opt_sha256, opt_size,
        images_only, opt_rows_total
        """;

    // Overlay may set these document columns; identity, family and page-set columns belong to other tickets. A new
    // native or text (E08-T04) replaces the reference; the earlier object stays registered (ADR-011 §4.2).
    private static readonly Dictionary<string, DocumentColumns.Column> OverlayColumns = DocumentColumns.MutableColumns
        .Where(c => c.Name is not ("metadata" or "metadata_raw" or "family_id" or "parent_document_id" or "family_sequence"
            or "family_status" or "active_page_set_id"))
        .ToDictionary(c => c.Name, StringComparer.Ordinal);

    public async Task<ImportBatchCreation> CreateAsync(NewImportBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.AuditTemplate);
        if (batch.SourceSha256 is not { Length: 32 })
        {
            throw new ArgumentException("The source digest is a SHA-256.", nameof(batch));
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, batch.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var job = await JobRepository.CreateInTransactionAsync(tx, new NewJob
        {
            WorkspaceId = batch.WorkspaceId,
            JobId = batch.JobId,
            JobType = JobType.Import,
            InitiatedBy = batch.InitiatedBy,
            ImportBatchId = batch.ImportBatchId,
            Parameters = new JsonObject
            {
                ["importBatchId"] = batch.ImportBatchId.ToString(),
                ["mode"] = batch.Mode.ToString(),
                ["profileId"] = batch.ProfileId?.ToString(),
            },
            ClientIdempotencyKey = batch.ClientIdempotencyKey,
            CorrelationId = batch.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);

        if (!job.Created)
        {
            // A retried start: the job (and its batch) already exist.
            var existing = await ReadByJobAsync(tx, batch.WorkspaceId, job.Job.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Job {job.Job.JobId} has no import batch.");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ImportBatchCreation(existing, job.Job, false);
        }

        ImportBatchRecord record;
        await using (var insert = tx.Command(
            $"""
            INSERT INTO opportunity.import_batch
                (workspace_id, import_batch_id, job_id, name, mode, source_file_name, source_object_key, source_sha256, source_size,
                 profile_id, profile_version, profile, coding_overlay_field_ids, may_create_fields, created_by,
                 opt_file_name, opt_object_key, opt_sha256, opt_size, images_only)
            VALUES (@ws, @id, @job, @name, @mode, @file, @key, @sha, @size, @profile_id, @profile_version, @profile::jsonb, @coding,
                    @may_create_fields, @by, @opt_file, @opt_key, @opt_sha, @opt_size, @images_only)
            RETURNING {Columns}
            """))
        {
            insert.Parameters.AddWithValue("ws", batch.WorkspaceId);
            insert.Parameters.AddWithValue("id", batch.ImportBatchId);
            insert.Parameters.AddWithValue("job", batch.JobId);
            insert.Parameters.AddWithValue("name", batch.Name.Trim());
            insert.Parameters.AddWithValue("mode", (short)(batch.Mode + 1));
            insert.Parameters.AddWithValue("file", batch.SourceFileName);
            insert.Parameters.AddWithValue("key", batch.SourceObjectKey);
            insert.Parameters.AddWithValue("sha", batch.SourceSha256);
            insert.Parameters.AddWithValue("size", batch.SourceSize);
            insert.Parameters.Add(Nullable("profile_id", NpgsqlDbType.Uuid, batch.ProfileId));
            insert.Parameters.Add(Nullable("profile_version", NpgsqlDbType.Bigint, batch.ProfileVersion));
            insert.Parameters.AddWithValue("profile", batch.ProfileJson);
            insert.Parameters.AddWithValue("coding", batch.CodingOverlayFieldIds.Distinct().Order().ToArray());
            insert.Parameters.AddWithValue("may_create_fields", batch.MayCreateFields);
            insert.Parameters.AddWithValue("by", batch.InitiatedBy);
            insert.Parameters.Add(Nullable("opt_file", NpgsqlDbType.Text, batch.Opt?.FileName));
            insert.Parameters.Add(Nullable("opt_key", NpgsqlDbType.Text, batch.Opt?.ObjectKey));
            insert.Parameters.Add(Nullable("opt_sha", NpgsqlDbType.Bytea, batch.Opt?.Sha256));
            insert.Parameters.Add(Nullable("opt_size", NpgsqlDbType.Bigint, batch.Opt?.Size));
            insert.Parameters.AddWithValue("images_only", batch.ImagesOnly);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            record = Read(reader);
        }

        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ImportBatchId"] = record.ImportBatchId.ToString(),
            ["Name"] = Truncate(record.Name, 200),
            ["Mode"] = record.Mode.ToString(),
            ["SourceFileName"] = Truncate(record.SourceFileName, 255),
            ["SourceSha256"] = Convert.ToHexStringLower(record.SourceSha256),
            ["SourceSize"] = Invariant(record.SourceSize),
            ["ProfileId"] = record.ProfileId?.ToString(),
            ["CodingOverlayFields"] = string.Join(',', record.CodingOverlayFieldIds),
            ["MayCreateFields"] = record.MayCreateFields ? "true" : "false",
            ["OptFileName"] = record.Opt is { } opt ? Truncate(opt.FileName, 255) : null,
            ["OptSha256"] = record.Opt is { } o ? Convert.ToHexStringLower(o.Sha256) : null,
            ["ImagesOnly"] = record.ImagesOnly ? "true" : null,
        };
        await AuditSql.InsertAsync(tx, batch.AuditTemplate with
        {
            EventId = Guid.CreateVersion7(),
            WorkspaceId = batch.WorkspaceId,
            Category = AuditTaxonomy.Import.Category,
            Action = AuditTaxonomy.Import.Started,
            ResourceType = "ImportBatch",
            ResourceId = record.ImportBatchId.ToString(),
            JobId = record.JobId,
            Outcome = AuditOutcome.Success,
            Details = details,
        }, cancellationToken).ConfigureAwait(false);

        if (record.CodingOverlayFieldIds.Count > 0)
        {
            // Q-31: the per-import, per-field switch is itself audited, listing the fields it enabled.
            await AuditSql.InsertAsync(tx, batch.AuditTemplate with
            {
                EventId = Guid.CreateVersion7(),
                WorkspaceId = batch.WorkspaceId,
                Category = AuditTaxonomy.Coding.Category,
                Action = AuditTaxonomy.Coding.OverlayEnabled,
                ResourceType = "ImportBatch",
                ResourceId = record.ImportBatchId.ToString(),
                JobId = record.JobId,
                Outcome = AuditOutcome.Success,
                Details = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ImportBatchId"] = record.ImportBatchId.ToString(),
                    ["Fields"] = string.Join(',', record.CodingOverlayFieldIds),
                },
            }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ImportBatchCreation(record, job.Job, true);
    }

    public async Task<ImportBatchRecord?> GetAsync(Guid workspaceId, Guid importBatchId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await ReadAsync(tx, workspaceId, importBatchId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<ImportBatchRecord>> ListAsync(
        Guid workspaceId, ImportBatchCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.import_batch
            WHERE workspace_id = @ws
              AND (@after_at::timestamptz IS NULL OR (created_at, import_batch_id) < (@after_at, @after_id))
            ORDER BY created_at DESC, import_batch_id DESC
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(Nullable("after_at", NpgsqlDbType.TimestampTz, after?.CreatedAt.ToUniversalTime()));
        command.Parameters.Add(Nullable("after_id", NpgsqlDbType.Uuid, after?.ImportBatchId));
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<ImportBatchRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(Read(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<Guid>> GetPreparableAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT b.import_batch_id
            FROM opportunity.import_batch b
            JOIN opportunity.job j ON j.workspace_id = b.workspace_id AND j.job_id = b.job_id
            WHERE b.workspace_id = @ws AND j.status IN ('Created', 'Preparing')
              AND (b.prepare_claimed_until IS NULL OR b.prepare_claimed_until < now())
            ORDER BY b.created_at
            LIMIT @limit
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

    public async Task<bool> TryClaimPreparationAsync(
        Guid workspaceId, Guid importBatchId, string owner, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.import_batch
            SET prepare_claimed_by = @owner, prepare_claimed_until = now() + @duration
            WHERE workspace_id = @ws AND import_batch_id = @id
              AND (prepare_claimed_by IS NULL OR prepare_claimed_by = @owner OR prepare_claimed_until < now())
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("duration", duration);
        var claimed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task AddKeysAsync(Guid workspaceId, Guid importBatchId, IReadOnlyList<ImportKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return;
        }

        // First occurrence wins inside the batch too; ON CONFLICT keeps the row of an earlier batch (rows arrive in order).
        var first = keys.GroupBy(k => k.ControlNumberNorm, StringComparer.Ordinal).Select(g => g.MinBy(k => k.RowNo)!).ToList();
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            INSERT INTO opportunity.import_batch_key (workspace_id, import_batch_id, control_number_norm, row_no)
            SELECT @ws, @id, u.norm, u.row_no FROM unnest(@norms, @rows) AS u(norm, row_no)
            ON CONFLICT (workspace_id, import_batch_id, control_number_norm) DO NOTHING
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", importBatchId);
            command.Parameters.AddWithValue("norms", first.Select(k => k.ControlNumberNorm).ToArray());
            command.Parameters.AddWithValue("rows", first.Select(k => k.RowNo).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CompletePreparationAsync(
        Guid workspaceId, Guid importBatchId, ImportPreparation preparation, IReadOnlyList<ImportChunkRange> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(chunks);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.import_batch
            SET header = @header::jsonb, dat_encoding = @encoding, dat_encoding_fallback = @fallback, data_offset = @offset,
                rows_total = @rows, fields_created = @fields, choices_created = @choices, opt_rows_total = @opt_rows, prepared_at = now()
            WHERE workspace_id = @ws AND import_batch_id = @id AND prepared_at IS NULL
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", importBatchId);
            update.Parameters.AddWithValue("header", JsonSerializer.Serialize(preparation.Header));
            update.Parameters.AddWithValue("encoding", preparation.DatEncoding);
            update.Parameters.AddWithValue("fallback", preparation.EncodingFallback);
            update.Parameters.AddWithValue("offset", preparation.DataOffset);
            update.Parameters.AddWithValue("rows", preparation.RowsTotal);
            update.Parameters.AddWithValue("fields", preparation.FieldsCreated);
            update.Parameters.AddWithValue("choices", preparation.ChoicesCreated);
            update.Parameters.Add(Nullable("opt_rows", NpgsqlDbType.Bigint, preparation.OptRowsTotal));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        if (chunks.Count > 0)
        {
            await using var insert = tx.Command(
                """
                INSERT INTO opportunity.import_batch_chunk (workspace_id, import_batch_id, row_from, row_to, byte_from, byte_to, line_from)
                SELECT @ws, @id, u.row_from, u.row_to, u.byte_from, u.byte_to, u.line_from
                FROM unnest(@row_from, @row_to, @byte_from, @byte_to, @line_from) AS u(row_from, row_to, byte_from, byte_to, line_from)
                """);
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", importBatchId);
            insert.Parameters.AddWithValue("row_from", chunks.Select(c => c.RowFrom).ToArray());
            insert.Parameters.AddWithValue("row_to", chunks.Select(c => c.RowTo).ToArray());
            insert.Parameters.AddWithValue("byte_from", chunks.Select(c => c.ByteFrom).ToArray());
            insert.Parameters.AddWithValue("byte_to", chunks.Select(c => c.ByteTo).ToArray());
            insert.Parameters.AddWithValue("line_from", chunks.Select(c => c.LineFrom).ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ImportChunkRange?> GetChunkRangeAsync(
        Guid workspaceId, Guid importBatchId, long rowFrom, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT row_from, row_to, byte_from, byte_to, line_from FROM opportunity.import_batch_chunk
            WHERE workspace_id = @ws AND import_batch_id = @id AND row_from = @from
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        command.Parameters.AddWithValue("from", rowFrom);
        ImportChunkRange? range = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                range = new ImportChunkRange(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return range;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindDocumentIdsAsync(
        Guid workspaceId, IReadOnlyCollection<string> controlNumberNorms, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlNumberNorms);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (controlNumberNorms.Count == 0)
        {
            return ids;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT u.n, d.document_id
            FROM unnest(@norms) AS u(n)
            JOIN opportunity.document d ON d.workspace_id = @ws AND d.control_number_norm = normalize(u.n, NFC)
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("norms", controlNumberNorms.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids[reader.GetString(0)] = reader.GetGuid(1);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public async Task<ImportChunkResult> ApplyChunkAsync(ClaimedChunk chunk, ImportChunkWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(write);
        var lease = chunk.Lease;
        var membership = chunk.Membership;
        if (membership.Kind != ChunkMembershipKind.ImportRows || membership.ImportBatchId != write.ImportBatchId
            || chunk.JobType != JobType.Import)
        {
            throw new ArgumentException("An import chunk write belongs to a leased ImportRows chunk of the batch's job.", nameof(write));
        }

        if (write.Rows.Any(r => r.RowNo < membership.RangeFrom || r.RowNo > membership.RangeTo)
            || write.Rows.Select(r => r.RowNo).Distinct().Count() != write.Rows.Count)
        {
            throw new ArgumentException("Every row must be a distinct row of the chunk's range.", nameof(write));
        }

        ChunkCommitResult commit;
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            // Family resolution sees every chunk committed before it (E09-T01); taken before any row lock of the chunk.
            await FamilyWriter.LockAsync(tx, cancellationToken).ConfigureAwait(false);
            var batch = await ReadAsync(tx, lease.WorkspaceId, write.ImportBatchId, cancellationToken).ConfigureAwait(false);
            if (batch is null || batch.JobId != lease.JobId)
            {
                throw new ArgumentException("The chunk's import batch does not exist or belongs to another job.", nameof(write));
            }

            var plan = await PlanAsync(tx, batch, chunk, write, cancellationToken).ConfigureAwait(false);
            await WriteAsync(tx, batch, chunk, plan, restrictions, cancellationToken).ConfigureAwait(false);

            // Fence F3 after the chunk's own writes; the chunk always gets exactly one IndexChunkTask (§21).
            var relationshipTasks = RelationshipTasks(chunk, plan.OtherChangedDocuments).ToList();
            commit = await JobChunkRepository.CommitInTransactionAsync(
                tx, lease, plan.Completion() with { IndexTasks = 1 + relationshipTasks.Count }, cancellationToken).ConfigureAwait(false);
            if (commit.Committed)
            {
                await UpdateCountersAsync(tx, batch, chunk, plan, commit.JobStatus, cancellationToken).ConfigureAwait(false);
                var (taskId, _) = await SearchWorkSql.AddIndexChunkTaskAsync(tx, new NewIndexChunkTask
                {
                    JobId = lease.JobId,
                    ChunkId = lease.ChunkId,
                    Kind = IndexTaskKind.Import,
                    Membership = membership,
                    ChangeMask = plan.ChangeMask,
                    IdempotencyKey = ChunkIdempotencyKey.ForChunk(lease.WorkspaceId, lease.JobId, chunk.Sequence, ChunkOperationKind.IndexChunk, 0),
                }, cancellationToken).ConfigureAwait(false);
                foreach (var relationshipTask in relationshipTasks)
                {
                    await SearchWorkSql.AddIndexChunkTaskAsync(tx, relationshipTask, cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ImportChunkResult(commit, plan.Imported, plan.Overlaid, plan.Skipped, plan.Errored, taskId);
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

        return new ImportChunkResult(commit, 0, 0, 0, 0, null);
    }

    public async Task RecordCompletedAsync(Guid workspaceId, Guid importBatchId, string? reasonCode = null, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var batch = await ReadAsync(tx, workspaceId, importBatchId, cancellationToken).ConfigureAwait(false);
        var job = batch is null ? null : await JobSql.ReadJobAsync(tx, workspaceId, batch.JobId, cancellationToken).ConfigureAwait(false);
        if (batch is null || job is null || batch.CompletedAt is not null || !JobStateMachine.IsFinished(job.Status))
        {
            return;
        }

        await using (var update = tx.Command(
            "UPDATE opportunity.import_batch SET completed_at = now() WHERE workspace_id = @ws AND import_batch_id = @id AND completed_at IS NULL"))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", importBatchId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var completed = await ReadAsync(tx, workspaceId, importBatchId, cancellationToken).ConfigureAwait(false) ?? batch;
        var report = await ImportReportSql.FreezeAsync(tx, completed, cancellationToken).ConfigureAwait(false);
        await AuditCompletedAsync(tx, completed, job.Status, job.CorrelationId, cancellationToken, reasonCode, report).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImportRowIssueRecord>> GetRowIssuesAsync(
        Guid workspaceId, Guid importBatchId, ImportIssueSeverity? severity, ImportRowIssueCursor? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT row_no, issue_no, severity, line_no, control_number, column_name, code, message, source
            FROM opportunity.import_row_issue
            WHERE workspace_id = @ws AND import_batch_id = @id
              AND (@severity::smallint IS NULL OR severity = @severity)
              AND (@after_row::bigint IS NULL OR (source, row_no, issue_no) > (@after_source, @after_row, @after_issue))
            ORDER BY source, row_no, issue_no
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        command.Parameters.Add(Nullable("severity", NpgsqlDbType.Smallint, (short?)severity));
        command.Parameters.Add(Nullable("after_row", NpgsqlDbType.Bigint, after?.RowNo));
        command.Parameters.Add(Nullable("after_issue", NpgsqlDbType.Integer, after?.IssueNo));
        command.Parameters.Add(Nullable("after_source", NpgsqlDbType.Smallint, (short?)after?.Source));
        command.Parameters.AddWithValue("limit", limit);
        var issues = new List<ImportRowIssueRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new ImportRowIssueRecord(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    (ImportIssueSeverity)reader.GetInt16(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    (ImportIssueSource)reader.GetInt16(8)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return issues;
    }

    public async Task<IReadOnlyList<ImportFamilyIssueRecord>> GetFamilyIssuesAsync(
        Guid workspaceId, Guid importBatchId, ImportRowIssueCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT m.row_no, i.issue_no, d.document_id, d.control_number, i.kind, d.family_status, i.message, i.related, i.missing_count
            FROM opportunity.import_batch_member m
            JOIN opportunity.family_issue i ON i.workspace_id = m.workspace_id AND i.document_id = m.document_id
            JOIN opportunity.document d ON d.workspace_id = m.workspace_id AND d.document_id = m.document_id
            WHERE m.workspace_id = @ws AND m.import_batch_id = @id
              AND (@after_row::bigint IS NULL OR (m.row_no, i.issue_no) > (@after_row, @after_issue))
            ORDER BY m.row_no, i.issue_no
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        command.Parameters.Add(Nullable("after_row", NpgsqlDbType.Bigint, after?.RowNo));
        command.Parameters.Add(Nullable("after_issue", NpgsqlDbType.Smallint, (short?)after?.IssueNo));
        command.Parameters.AddWithValue("limit", limit);
        var issues = new List<ImportFamilyIssueRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new ImportFamilyIssueRecord(
                    reader.GetInt64(0),
                    reader.GetInt16(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    (FamilyIssueKind)reader.GetInt16(4),
                    (FamilyStatus)reader.GetInt16(5),
                    reader.GetString(6),
                    reader.GetFieldValue<string[]>(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return issues;
    }

    // ---- the chunk transaction --------------------------------------------------------------------------------

    /// <summary>Decides every row of the chunk: create, overlay, skip or error.</summary>
    private static async Task<ChunkPlan> PlanAsync(
        WorkspaceTransaction tx, ImportBatchRecord batch, ClaimedChunk chunk, ImportChunkWrite write, CancellationToken cancellationToken)
    {
        var ws = tx.WorkspaceId;
        var plan = new ChunkPlan();
        var rows = write.Rows.OrderBy(r => r.RowNo).ToList();
        foreach (var row in rows)
        {
            plan.Rows[row.RowNo] = row;
            if (row.Issues.Count > 0)
            {
                plan.Issues[row.RowNo] = [.. row.Issues];
            }
        }

        // Rows a committed attempt already wrote (defence in depth: fence F3 lets a chunk commit only once).
        var done = new HashSet<long>();
        await using (var command = tx.Command(
            """
            SELECT row_no FROM opportunity.import_batch_member
            WHERE workspace_id = @ws AND import_batch_id = @id AND row_no BETWEEN @from AND @to
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", batch.ImportBatchId);
            command.Parameters.AddWithValue("from", chunk.Membership.RangeFrom!.Value);
            command.Parameters.AddWithValue("to", chunk.Membership.RangeTo!.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                done.Add(reader.GetInt64(0));
            }
        }

        rows.RemoveAll(r => done.Contains(r.RowNo));
        plan.DoneRows.UnionWith(done);
        foreach (var row in done)
        {
            plan.Issues.Remove(row);
        }

        var candidates = rows.Where(r => !r.HasErrors && r.ControlNumberNorm is not null).ToList();
        var norms = candidates.Select(r => r.ControlNumberNorm!).Distinct(StringComparer.Ordinal).ToArray();

        // Duplicates across the whole file: the preparation pass recorded each number's first row.
        var firstRows = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var command = tx.Command(
            """
            SELECT control_number_norm, row_no FROM opportunity.import_batch_key
            WHERE workspace_id = @ws AND import_batch_id = @id AND control_number_norm = ANY(@norms)
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", batch.ImportBatchId);
            command.Parameters.AddWithValue("norms", norms);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                firstRows[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        var retired = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = tx.Command(
            """
            SELECT u.n FROM unnest(@norms) AS u(n)
            JOIN opportunity.retired_control_number r ON r.workspace_id = @ws AND r.control_number_norm = normalize(u.n, NFC)
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("norms", norms);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                retired.Add(reader.GetString(0));
            }
        }

        // Existing documents with these numbers, locked in DocumentId order (the bulk lock order, ADR-010 §6). NO KEY UPDATE:
        // the import never changes key columns, and a FOR UPDATE lock here would block the FK KEY SHARE that concurrent
        // coding writes take on the document row while holding its projection-state lock (a lock-order deadlock).
        var existing = new Dictionary<string, (Guid DocumentId, bool Deleted)>(StringComparer.Ordinal);
        await using (var command = tx.Command(
            """
            SELECT u.n, d.document_id, s.is_deleted, d.duplicate_group_id, d.email_thread_id
            FROM unnest(@norms) AS u(n)
            JOIN opportunity.document d ON d.workspace_id = @ws AND d.control_number_norm = normalize(u.n, NFC)
            JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
            ORDER BY d.document_id
            FOR NO KEY UPDATE OF d, s
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("norms", norms);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing[reader.GetString(0)] = (reader.GetGuid(1), reader.GetBoolean(2));
                plan.PreviousRelationships[reader.GetGuid(1)] =
                    (reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in candidates)
        {
            var norm = row.ControlNumberNorm!;
            if (firstRows.TryGetValue(norm, out var first) ? first != row.RowNo : !seen.Add(norm))
            {
                plan.Error(row, "duplicate-control-number",
                    $"Control number {row.ControlNumber} is already used by row {(firstRows.TryGetValue(norm, out var f) ? f : 0)} of this load file.");
                continue;
            }

            seen.Add(norm);
            if (retired.Contains(norm))
            {
                plan.Error(row, "control-number-retired", $"Control number {row.ControlNumber} belonged to a removed document and cannot be reused.");
                continue;
            }

            if (existing.TryGetValue(norm, out var document))
            {
                if (document.Deleted)
                {
                    plan.Error(row, "document-deleted", $"The document with control number {row.ControlNumber} was deleted.");
                }
                else if (ImportKeyRules.Issue(ImportKeyRules.Decide(write.Mode, exists: true), row.ControlNumber) is { } refused)
                {
                    plan.Error(row, refused.Code, refused.Message);
                }
                else if (row.Objects.Any(o => o.DocumentId != document.DocumentId))
                {
                    // The chunk stored this row's files before the document existed (another load created it since).
                    plan.Error(row, "document-changed",
                        $"The document with control number {row.ControlNumber} was created by another load while its files were stored; load the row again.");
                }
                else
                {
                    plan.Overlays.Add((row, document.DocumentId));
                }
            }
            else if (ImportKeyRules.Issue(ImportKeyRules.Decide(write.Mode, exists: false), row.ControlNumber) is { } missing)
            {
                plan.Error(row, missing.Code, missing.Message);
            }
            else
            {
                plan.Inserts.Add(row);
            }
        }

        foreach (var row in rows.Where(r => r.HasErrors))
        {
            plan.ErroredRows.Add(row);
        }

        foreach (var row in rows.Where(r => !r.HasErrors && r.ControlNumberNorm is null))
        {
            plan.Error(row, "control-number-missing", "The row has no control number.");
        }

        var enabled = batch.CodingOverlayFieldIds.ToHashSet();
        if (rows.SelectMany(r => r.Coding).Any(c => !enabled.Contains(c.FieldId)))
        {
            throw new ArgumentException("A row carries a coding field that this import did not enable for overlay (Q-31).", nameof(write));
        }

        return plan;
    }

    private static async Task WriteAsync(
        WorkspaceTransaction tx, ImportBatchRecord batch, ClaimedChunk chunk, ChunkPlan plan, IRestrictionClassBinding? restrictions,
        CancellationToken cancellationToken)
    {
        var ws = tx.WorkspaceId;

        // New documents (version 1). A number another load created meanwhile is skipped by the insert: report it.
        var documents = plan.Inserts.Select(row =>
        {
            var d = row.Document!;
            d.WorkspaceId = ws;
            d.FamilyId = d.DocumentId;
            d.ParentDocumentId = null;
            d.FamilySequence = 0;
            d.FirstImportBatchId = batch.ImportBatchId;
            return d;
        }).ToList();

        // Natives and text first (E08-T04): a document references registry rows of its own (ADR-011 §2.4).
        await LinkObjectsAsync(tx, chunk, plan.Inserts.Select(r => (r, r.Document!.DocumentId)).Concat(plan.Overlays), cancellationToken).ConfigureAwait(false);
        var inserted = await DocumentRepository.InsertNewAsync(tx, documents, cancellationToken).ConfigureAwait(false);
        var notInserted = new List<Guid>();
        foreach (var row in plan.Inserts)
        {
            if (inserted.Contains(row.Document!.DocumentId))
            {
                plan.Members.Add((row, row.Document.DocumentId, ActionImported));
            }
            else
            {
                if (row.Objects.Count > 0)
                {
                    notInserted.Add(row.Document.DocumentId);
                }

                plan.Error(row, ImportKeyRules.KeyExists, $"A document with control number {row.ControlNumber} was created by another load meanwhile.");
            }
        }

        await StoredObjectSql.UnregisterDocumentsAsync(tx, notInserted, cancellationToken).ConfigureAwait(false);

        // Overlays (E08-T07): only the supplied values (and, when blank values overwrite, the blanked ones); each changed
        // document's old and new values are kept as an overlay event with the version the change created.
        var changes = await OverlayAsync(tx, plan.Overlays, cancellationToken).ConfigureAwait(false);
        var changed = changes.Keys.ToHashSet();
        if (changed.Count > 0)
        {
            var versions = new Dictionary<Guid, long>();
            await using (var bump = tx.Command(
                """
                UPDATE opportunity.document_projection_state SET document_version = document_version + 1
                WHERE workspace_id = @ws AND document_id = ANY(@ids)
                RETURNING document_id, document_version
                """))
            {
                bump.Parameters.AddWithValue("ws", ws);
                bump.Parameters.AddWithValue("ids", changed.ToArray());
                await using var reader = await bump.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    versions[reader.GetGuid(0)] = reader.GetInt64(1);
                }
            }

            await RecordOverlayEventsAsync(tx, batch, chunk, plan, changes, versions, cancellationToken).ConfigureAwait(false);
        }

        // New documents start at version 1 and changed overlays were bumped above, so the derived date needs no bump.
        await DeriveDocumentDatesAsync(tx, [.. inserted, .. changed], cancellationToken).ConfigureAwait(false);

        // ADR-015 D6.2: walls scoped to custodians cover new and overlaid documents in this transaction. The chunk's
        // index task re-projects them; a changed coverage of an existing document puts it on the security lane.
        var walled = await DocumentSecuritySql.SyncWallsAsync(tx, [.. inserted, .. changed], cancellationToken).ConfigureAwait(false);
        plan.SecurityChanged |= walled.Any(changed.Contains);

        // Q-31 coding values of the rows that made it, through the coding store (CodingEvents, version bumps).
        var coding = plan.Members.Select(m => (m.Row, m.DocumentId))
            .Concat(plan.Overlays.Select(o => (o.Row, o.DocumentId)))
            .Where(x => x.Row.Coding.Count > 0)
            .Select(x => (x.DocumentId, x.Row.Coding))
            .ToList();
        var codingChanged = new HashSet<Guid>();
        if (coding.Count > 0)
        {
            var outcome = await CodingRepository.ApplyImportValuesAsync(
                tx, chunk.Lease.JobId, chunk.InitiatedBy, "import:" + chunk.IdempotencyKey, coding, cancellationToken, restrictions).ConfigureAwait(false);
            codingChanged.UnionWith(outcome.ChangedDocuments);
            plan.CodingChanged = outcome.ChangedDocuments.Count > 0;
            plan.SecurityChanged |= outcome.TouchesSecurityAffectingField;
            if (outcome.EventsWritten > 0)
            {
                await AuditSql.InsertAsync(tx, ServiceEvent(batch, chunk, AuditTaxonomy.Coding.Category, AuditTaxonomy.Coding.BulkChunkApplied,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["ImportBatchId"] = batch.ImportBatchId.ToString(),
                        ["Fields"] = string.Join(',', outcome.FieldIds.Order()),
                        ["Documents"] = Invariant(coding.Count),
                        ["Changed"] = Invariant(outcome.ChangedDocuments.Count),
                        ["CodingEvents"] = Invariant(outcome.EventsWritten),
                        ["SecurityAffecting"] = outcome.TouchesSecurityAffectingField ? "true" : "false",
                        ["RestrictionClasses.Added"] = Classes(outcome.RestrictionChanges, added: true),
                        ["RestrictionClasses.Removed"] = Classes(outcome.RestrictionChanges, added: false),
                    }), cancellationToken).ConfigureAwait(false);
            }
        }

        var covered = plan.Members.Select(m => m.DocumentId).Concat(plan.Overlays.Select(o => o.DocumentId)).ToHashSet();

        // Families of the chunk's documents, before the duplicate groups: a group's primary depends on FamilyId/FamilyDate.
        var families = await FamilyWriter.ResolveAsync(
            tx, [.. covered], plan.Members.Select(m => m.DocumentId).ToHashSet(), cancellationToken).ConfigureAwait(false);
        plan.FamiliesChanged = families.ChangedDocuments.Any(covered.Contains);
        plan.OtherChangedDocuments.AddRange(families.ChangedDocuments.Where(d => !covered.Contains(d)));
        plan.OtherChangedDocuments.AddRange(
            await SyncRelationshipsAsync(tx, plan, covered, families.DuplicateGroupIds, cancellationToken).ConfigureAwait(false));

        // OPT page sets (E08-T05) of the documents the chunk created or overlaid; a replaced page set is an overlay change.
        var pagesChanged = await WritePageSetsAsync(tx, batch, plan, changed, cancellationToken).ConfigureAwait(false);

        foreach (var (row, documentId) in plan.Overlays)
        {
            plan.Members.Add((row, documentId,
                changed.Contains(documentId) || codingChanged.Contains(documentId) || pagesChanged.Contains(documentId) ? ActionOverlaid : ActionSkipped));
        }

        if (plan.Members.Count > 0)
        {
            await using var members = tx.Command(
                """
                INSERT INTO opportunity.import_batch_member (workspace_id, import_batch_id, row_no, document_id, action)
                SELECT @ws, @id, u.row_no, u.document_id, u.action FROM unnest(@rows, @documents, @actions) AS u(row_no, document_id, action)
                """);
            members.Parameters.AddWithValue("ws", ws);
            members.Parameters.AddWithValue("id", batch.ImportBatchId);
            members.Parameters.AddWithValue("rows", plan.Members.Select(m => m.Row.RowNo).ToArray());
            members.Parameters.AddWithValue("documents", plan.Members.Select(m => m.DocumentId).ToArray());
            members.Parameters.AddWithValue("actions", plan.Members.Select(m => m.Action).ToArray());
            await members.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertIssuesAsync(tx, batch.ImportBatchId, plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-009 R25: DocumentDate is derived from the document's stored dates (first non-null of DateSent, DateReceived,
    /// DateLastModified, DateCreated; DocumentDateSource names which) after the chunk's inserts and overlays, so an
    /// overlay that changes one date recomputes it from all of them. An upstream document date (source Upstream) is
    /// kept as loaded. Rows whose derived value is already stored are not touched.
    /// </summary>
    private static async Task DeriveDocumentDatesAsync(WorkspaceTransaction tx, Guid[] documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Length == 0)
        {
            return;
        }

        await using var derive = tx.Command(
            """
            UPDATE opportunity.document d SET document_date = x.value, document_date_source = x.source
            FROM (SELECT document_id,
                         coalesce(date_sent, date_received, date_last_modified, date_created) AS value,
                         CASE WHEN date_sent IS NOT NULL THEN 1 WHEN date_received IS NOT NULL THEN 2
                              WHEN date_last_modified IS NOT NULL THEN 3 WHEN date_created IS NOT NULL THEN 4 END::smallint AS source
                  FROM opportunity.document
                  WHERE workspace_id = @ws AND document_id = ANY(@ids)
                    AND document_date_source IS DISTINCT FROM @upstream) x
            WHERE d.workspace_id = @ws AND d.document_id = x.document_id
              AND (d.document_date, d.document_date_source) IS DISTINCT FROM (x.value, x.source)
            """);
        derive.Parameters.AddWithValue("ws", tx.WorkspaceId);
        derive.Parameters.AddWithValue("ids", documentIds);
        derive.Parameters.AddWithValue("upstream", (short)DocumentDateSource.Upstream);
        await derive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers the rows' stored files (<see cref="ImportRow.Objects"/>) and points each document at its native and
    /// text object, before the document rows are written (their foreign keys are not deferred).
    /// </summary>
    private static async Task LinkObjectsAsync(
        WorkspaceTransaction tx, ClaimedChunk chunk, IEnumerable<(ImportRow Row, Guid DocumentId)> rows, CancellationToken cancellationToken)
    {
        var linked = rows.Where(r => r.Row.Objects.Count > 0).ToList();
        if (linked.Count == 0)
        {
            return;
        }

        if (linked.Any(r => r.Row.Objects.Any(o => o.DocumentId != r.DocumentId)))
        {
            throw new ArgumentException("A row's stored objects belong to another document.", nameof(rows));
        }

        var ids = await StoredObjectSql.RegisterAsync(tx, [.. linked.SelectMany(r => r.Row.Objects)], chunk.Lease.JobId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (row, _) in linked)
        {
            foreach (var o in row.Objects)
            {
                switch (o.Area)
                {
                    case ObjectArea.Native:
                        row.Document!.NativeObjectId = ids[o.LogicalKey];
                        break;
                    case ObjectArea.Text:
                        row.Document!.TextObjectId = ids[o.LogicalKey];
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Duplicate groups and email threads of the chunk's documents (E09-T02), after the document writes and before the
    /// search work, through <see cref="RelationshipWriter"/>: the group and thread rows the documents now reference (the
    /// deferred foreign keys need them at commit), plus, for overlays, the ones they referenced before, so every touched
    /// group is recounted and its primary re-elected, and the groups of documents whose family changed (the primary is
    /// family-level). Documents outside the chunk whose primary flag changed are returned:
    /// they get search work of their own (<see cref="RelationshipTasks"/>).
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SyncRelationshipsAsync(
        WorkspaceTransaction tx, ChunkPlan plan, IReadOnlySet<Guid> coveredDocumentIds, IReadOnlyList<Guid> familyChangedGroups,
        CancellationToken cancellationToken)
    {
        var rows = plan.Members.Select(m => m.Row).Concat(plan.Overlays.Select(o => o.Row)).ToList();
        var previous = plan.Overlays.Select(o => plan.PreviousRelationships.GetValueOrDefault(o.DocumentId)).ToList();
        var groups = rows.Select(r => r.DuplicateGroup).OfType<DuplicateGroupKey>().DistinctBy(g => g.DuplicateGroupId).ToList();
        var threads = rows.Select(r => r.EmailThread).OfType<EmailThreadKey>().DistinctBy(t => t.EmailThreadId).ToList();
        var previousGroups = previous.Select(p => p.DuplicateGroupId).OfType<Guid>().Concat(familyChangedGroups).Distinct().ToList();
        var previousThreads = previous.Select(p => p.EmailThreadId).OfType<Guid>().Distinct().ToList();
        if (groups.Count == 0 && threads.Count == 0 && previousGroups.Count == 0 && previousThreads.Count == 0)
        {
            return [];
        }

        var result = await RelationshipWriter.SyncAsync(
            tx, new RelationshipSync(groups, threads, coveredDocumentIds, previousGroups, previousThreads), cancellationToken).ConfigureAwait(false);
        return [.. result.OtherChangedDocuments.Select(d => d.DocumentId)];
    }

    /// <summary>
    /// Search work for documents outside the chunk's rows whose relationship flags the chunk changed (a new duplicate
    /// primary): Relationship IndexChunkTasks over explicit ids, next to the chunk's one Import task.
    /// </summary>
    private static IEnumerable<NewIndexChunkTask> RelationshipTasks(ClaimedChunk chunk, IReadOnlyList<Guid> documents)
    {
        var lease = chunk.Lease;
        var baseKey = ChunkIdempotencyKey.ForChunk(lease.WorkspaceId, lease.JobId, chunk.Sequence, ChunkOperationKind.RelationshipChunk, 0);
        var batches = documents.Distinct().Order().Chunk(ChunkMembership.MaxExplicitIds).ToList();
        for (var i = 0; i < batches.Count; i++)
        {
            yield return new NewIndexChunkTask
            {
                JobId = lease.JobId,
                ChunkId = lease.ChunkId,
                Kind = IndexTaskKind.Relationship,
                Membership = ChunkMembership.ExplicitIds(batches[i]),
                ChangeMask = SearchChangeMask.Relationships,
                IdempotencyKey = i == 0
                    ? baseKey
                    : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{baseKey}|{i}"))),
            };
        }
    }

    /// <summary>
    /// One UPDATE per overlaid row, sent as one batch: supplied columns are replaced (blanked ones set to null), supplied
    /// metadata keys merged in, blanked keys removed and multi-value merge keys united with the stored choices. The row
    /// is touched only when something differs, so an identical re-load changes nothing and bumps nothing. Returns each
    /// changed document's changes (<c>{ key: { old, new } }</c>, column names and <c>f{FieldId}</c> keys), read from the
    /// pre-update row the UPDATE joins (the rows are locked by the plan).
    /// </summary>
    private static async Task<Dictionary<Guid, JsonObject>> OverlayAsync(
        WorkspaceTransaction tx, List<(ImportRow Row, Guid DocumentId)> overlays, CancellationToken cancellationToken)
    {
        var changes = new Dictionary<Guid, JsonObject>();
        if (overlays.Count == 0)
        {
            return changes;
        }

        await using var batch = tx.Batch();
        foreach (var (row, documentId) in overlays)
        {
            var document = row.Document!;
            var columns = row.SuppliedColumns.Order(StringComparer.Ordinal).Select(name => OverlayColumns.TryGetValue(name, out var c)
                ? c
                : throw new ArgumentException($"Column '{name}' cannot be overlaid.", nameof(overlays))).ToList();
            var command = new NpgsqlBatchCommand();
            var sets = new List<string>();
            var olds = new List<string>();
            var news = new List<string>();
            var oldValues = new List<string>();
            var newValues = new List<string>();
            for (var i = 0; i < columns.Count; i++)
            {
                command.Parameters.Add(new NpgsqlParameter("p" + Invariant(i), columns[i].Type) { Value = columns[i].Get(document) ?? DBNull.Value });
                sets.Add($"{columns[i].Name} = @p{Invariant(i)}");
                olds.Add("d." + columns[i].Name);
                news.Add("@p" + Invariant(i));
                oldValues.Add($"'{columns[i].Name}', to_jsonb(o.{columns[i].Name})");
                newValues.Add($"'{columns[i].Name}', to_jsonb(d.{columns[i].Name})");
            }

            var metadata = "(d.metadata - @clear::text[]) || @meta::jsonb";
            if (row.MergedMetadataKeys.Count > 0)
            {
                // Multi-value merge: the stored choices plus the load file's, canonical (distinct, ascending).
                metadata +=
                    """
                     || coalesce((SELECT jsonb_object_agg(k.key, (
                            SELECT jsonb_agg(DISTINCT e.value ORDER BY e.value)
                            FROM (SELECT jsonb_array_elements(CASE WHEN jsonb_typeof(d.metadata -> k.key) = 'array' THEN d.metadata -> k.key ELSE '[]'::jsonb END)
                                  UNION ALL SELECT jsonb_array_elements(@meta::jsonb -> k.key)) AS e(value)))
                        FROM unnest(@merge::text[]) AS k(key)), '{}'::jsonb)
                    """;
            }

            const string raw =
                "CASE WHEN @raw::jsonb IS NULL THEN d.metadata_raw - @clear::text[] ELSE (coalesce(d.metadata_raw, '{}') - @clear::text[]) || @raw::jsonb END";
            sets.Add("metadata = " + metadata);
            sets.Add("metadata_raw = " + raw);
            olds.Add("d.metadata");
            olds.Add("d.metadata_raw");
            news.Add(metadata);
            news.Add(raw);
            command.CommandText =
                $"""
                UPDATE opportunity.document d SET {string.Join(", ", sets)}, updated_at = now()
                FROM opportunity.document o
                WHERE d.workspace_id = @ws AND d.document_id = @id AND o.workspace_id = @ws AND o.document_id = @id
                  AND ({string.Join(", ", olds)}) IS DISTINCT FROM ({string.Join(", ", news)})
                RETURNING d.document_id, jsonb_build_object({string.Join(", ", oldValues)})::text, jsonb_build_object({string.Join(", ", newValues)})::text,
                          o.metadata::text, d.metadata::text
                """;
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.AddWithValue("id", documentId);
            command.Parameters.Add(new NpgsqlParameter("meta", NpgsqlDbType.Jsonb) { Value = document.Metadata ?? "{}" });
            command.Parameters.Add(new NpgsqlParameter("raw", NpgsqlDbType.Jsonb) { Value = (object?)document.MetadataRaw ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("clear", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = row.ClearedMetadataKeys.Order(StringComparer.Ordinal).ToArray() });
            if (row.MergedMetadataKeys.Count > 0)
            {
                command.Parameters.Add(new NpgsqlParameter("merge", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = row.MergedMetadataKeys.Order(StringComparer.Ordinal).ToArray() });
            }

            batch.BatchCommands.Add(command);
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changes[reader.GetGuid(0)] = Diff(
                    JsonNode.Parse(reader.GetString(1))!.AsObject(), JsonNode.Parse(reader.GetString(2))!.AsObject(),
                    JsonNode.Parse(reader.GetString(3))!.AsObject(), JsonNode.Parse(reader.GetString(4))!.AsObject());
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        return changes;
    }

    /// <summary>The keys whose value differs, as <c>{ key: { old, new } }</c>: document columns, then metadata keys.</summary>
    private static JsonObject Diff(JsonObject oldColumns, JsonObject newColumns, JsonObject oldMetadata, JsonObject newMetadata)
    {
        var diff = new JsonObject();
        void Add(string key, JsonNode? before, JsonNode? after)
        {
            if (!JsonNode.DeepEquals(before, after))
            {
                diff[key] = new JsonObject { ["old"] = before?.DeepClone(), ["new"] = after?.DeepClone() };
            }
        }

        foreach (var (key, value) in newColumns)
        {
            Add(key, oldColumns[key], value);
        }

        foreach (var key in oldMetadata.Select(p => p.Key).Union(newMetadata.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Add(key, oldMetadata[key], newMetadata[key]);
        }

        return diff;
    }

    /// <summary>
    /// The old and new values of every document the chunk's overlay changed (<c>document_overlay_event</c>), and one
    /// <c>Import.Overlaid</c> audit event for the chunk that references them by import and rows (ADR-013 §6.2): audit
    /// itself carries only IDs, counts and the changed keys, never metadata values (§7).
    /// </summary>
    private static async Task RecordOverlayEventsAsync(
        WorkspaceTransaction tx, ImportBatchRecord batch, ClaimedChunk chunk, ChunkPlan plan, Dictionary<Guid, JsonObject> changes,
        Dictionary<Guid, long> versions, CancellationToken cancellationToken)
    {
        var rows = plan.Overlays.Where(o => changes.ContainsKey(o.DocumentId)).OrderBy(o => o.Row.RowNo).ToList();
        await using (var command = tx.Command(
            """
            INSERT INTO opportunity.document_overlay_event
                (workspace_id, import_batch_id, row_no, document_id, document_version, job_id, actor_id, changes)
            SELECT @ws, @batch, u.row_no, u.document_id, u.version, @job, @actor, u.changes::jsonb
            FROM unnest(@rows, @documents, @versions, @changes) AS u(row_no, document_id, version, changes)
            ON CONFLICT DO NOTHING
            """))
        {
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.AddWithValue("batch", batch.ImportBatchId);
            command.Parameters.AddWithValue("job", chunk.Lease.JobId);
            command.Parameters.AddWithValue("actor", chunk.InitiatedBy);
            command.Parameters.AddWithValue("rows", rows.Select(r => r.Row.RowNo).ToArray());
            command.Parameters.AddWithValue("documents", rows.Select(r => r.DocumentId).ToArray());
            command.Parameters.AddWithValue("versions", rows.Select(r => versions[r.DocumentId]).ToArray());
            command.Parameters.Add(new NpgsqlParameter("changes", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = rows.Select(r => changes[r.DocumentId].ToJsonString()).ToArray(),
            });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var keys = string.Join(',', changes.Values.SelectMany(c => c.Select(p => p.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        await AuditSql.InsertAsync(tx, ServiceEvent(batch, chunk, AuditTaxonomy.Import.Category, AuditTaxonomy.Import.Overlaid,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ImportBatchId"] = batch.ImportBatchId.ToString(),
                ["Documents"] = Invariant(rows.Count),
                ["RowFrom"] = Invariant(chunk.Membership.RangeFrom!.Value),
                ["RowTo"] = Invariant(chunk.Membership.RangeTo!.Value),
                ["Changed"] = Truncate(keys, 2_000),
                ["OldNewValues"] = "document_overlay_event",
            }), cancellationToken).ConfigureAwait(false);
    }

    private static string? Classes(IReadOnlyList<RestrictionClassChange> changes, bool added) =>
        changes.Any(c => c.Added == added)
            ? string.Join(',', changes.Where(c => c.Added == added).Select(c => c.ClassKey).Distinct().Order(StringComparer.Ordinal))
            : null;

    private static async Task InsertIssuesAsync(WorkspaceTransaction tx, Guid importBatchId, ChunkPlan plan, CancellationToken cancellationToken)
    {
        // (file, row) -> issues in order: DAT rows, the OPT rows of OPT-only documents, then the OPT rows' own warnings.
        var entries = new SortedDictionary<(short Source, long Row), (long? Line, string? ControlNumber, List<ImportRowIssue> Issues)>();
        void Add(ImportIssueSource source, long rowNo, long? line, string? controlNumber, ImportRowIssue issue)
        {
            if (!entries.TryGetValue(((short)source, rowNo), out var entry))
            {
                entry = (line, controlNumber, []);
                entries[((short)source, rowNo)] = entry;
            }

            entry.Issues.Add(issue);
        }

        foreach (var (rowNo, issues) in plan.Issues)
        {
            var row = plan.Rows.GetValueOrDefault(rowNo);
            foreach (var issue in issues)
            {
                if (row?.OptRowNo is { } optRow)
                {
                    Add(ImportIssueSource.Opt, optRow, row.LineNo, row.ControlNumber, issue);
                }
                else
                {
                    Add(ImportIssueSource.Dat, rowNo, row?.LineNo, row?.ControlNumber, issue);
                }
            }
        }

        foreach (var issue in plan.Rows.Values.Where(r => !plan.DoneRows.Contains(r.RowNo)).SelectMany(r => r.OptIssues).Concat(plan.OptIssues))
        {
            Add(ImportIssueSource.Opt, issue.OptRow, issue.LineNo, issue.ImageKey, issue.Issue);
        }

        var sources = new List<short>();
        var rows = new List<long>();
        var numbers = new List<int>();
        var severities = new List<short>();
        var lines = new List<long?>();
        var controlNumbers = new List<string?>();
        var columns = new List<string?>();
        var codes = new List<string>();
        var messages = new List<string>();
        foreach (var ((source, rowNo), (line, controlNumber, issues)) in entries)
        {
            for (var i = 0; i < issues.Count; i++)
            {
                sources.Add(source);
                rows.Add(rowNo);
                numbers.Add(i + 1);
                severities.Add((short)issues[i].Severity);
                lines.Add(line);
                controlNumbers.Add(Truncate(controlNumber, 1000));
                columns.Add(Truncate(issues[i].Column, 1000));
                codes.Add(Truncate(issues[i].Code, ImportRowIssue.MaxCodeLength)!);
                messages.Add(Truncate(issues[i].Message, ImportRowIssue.MaxMessageLength)!);
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        await using var command = tx.Command(
            """
            INSERT INTO opportunity.import_row_issue
                (workspace_id, import_batch_id, source, row_no, issue_no, severity, line_no, control_number, column_name, code, message)
            SELECT @ws, @id, u.source, u.row_no, u.issue_no, u.severity, u.line_no, u.control_number, u.column_name, u.code, u.message
            FROM unnest(@sources, @rows, @numbers, @severities, @lines, @control_numbers, @columns, @codes, @messages)
                AS u(source, row_no, issue_no, severity, line_no, control_number, column_name, code, message)
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        command.Parameters.AddWithValue("sources", sources.ToArray());
        command.Parameters.AddWithValue("rows", rows.ToArray());
        command.Parameters.AddWithValue("numbers", numbers.ToArray());
        command.Parameters.AddWithValue("severities", severities.ToArray());
        command.Parameters.Add(new NpgsqlParameter("lines", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = lines.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("control_numbers", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = controlNumbers.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("columns", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = columns.ToArray() });
        command.Parameters.AddWithValue("codes", codes.ToArray());
        command.Parameters.AddWithValue("messages", messages.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Report counters after fence F3 (job row first, then the batch row); completes the batch with its job.</summary>
    private static async Task UpdateCountersAsync(
        WorkspaceTransaction tx, ImportBatchRecord batch, ClaimedChunk chunk, ChunkPlan plan, JobStatus? jobStatus, CancellationToken cancellationToken)
    {
        var finished = jobStatus is { } status && JobStateMachine.IsFinished(status);
        ImportBatchRecord updated;
        await using (var command = tx.Command(
            $"""
            UPDATE opportunity.import_batch
            SET rows_imported = rows_imported + @imported, rows_overlaid = rows_overlaid + @overlaid,
                rows_skipped = rows_skipped + @skipped, rows_errored = rows_errored + @errored,
                completed_at = CASE WHEN @finished THEN coalesce(completed_at, now()) ELSE completed_at END
            WHERE workspace_id = @ws AND import_batch_id = @id
            RETURNING {Columns}
            """))
        {
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            command.Parameters.AddWithValue("id", batch.ImportBatchId);
            command.Parameters.AddWithValue("imported", (long)plan.Imported);
            command.Parameters.AddWithValue("overlaid", (long)plan.Overlaid);
            command.Parameters.AddWithValue("skipped", (long)plan.Skipped);
            command.Parameters.AddWithValue("errored", (long)plan.Errored);
            command.Parameters.AddWithValue("finished", finished);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            updated = Read(reader);
        }

        if (finished && batch.CompletedAt is null)
        {
            var report = await ImportReportSql.FreezeAsync(tx, updated, cancellationToken).ConfigureAwait(false);
            await AuditCompletedAsync(tx, updated, jobStatus!.Value, chunk.CorrelationId, cancellationToken, report: report).ConfigureAwait(false);
        }
    }

    private static Task AuditCompletedAsync(
        WorkspaceTransaction tx, ImportBatchRecord batch, JobStatus status, string? correlationId, CancellationToken cancellationToken,
        string? reasonCode = null, ImportReportData? report = null) =>
        AuditSql.InsertAsync(tx, new AuditEvent
        {
            WorkspaceId = batch.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Import.Category,
            Action = AuditTaxonomy.Import.Completed,
            ActorType = AuditActorType.Service,
            ActorId = ImportWorkerActor,
            ActorDisplay = "Import",
            OnBehalfOf = batch.CreatedBy,
            ResourceType = "ImportBatch",
            ResourceId = batch.ImportBatchId.ToString(),
            Outcome = status == JobStatus.Completed ? AuditOutcome.Success : AuditOutcome.Failure,
            ReasonCode = status == JobStatus.Completed ? null : reasonCode ?? status.ToString(),
            CorrelationId = string.IsNullOrEmpty(correlationId) ? null : correlationId,
            JobId = batch.JobId,
            Details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ImportBatchId"] = batch.ImportBatchId.ToString(),
                ["JobStatus"] = status.ToString(),
                ["RowsRead"] = batch.Preparation is { } p ? Invariant(p.RowsTotal) : null,
                ["RowsImported"] = Invariant(batch.RowsImported),
                ["RowsOverlaid"] = Invariant(batch.RowsOverlaid),
                ["RowsSkipped"] = Invariant(batch.RowsSkipped),
                ["RowsErrored"] = Invariant(batch.RowsErrored),
            }.Concat(report is null ? [] : ImportReportSql.AuditDetails(report)).ToDictionary(StringComparer.Ordinal),
        }, cancellationToken);

    private static AuditEvent ServiceEvent(
        ImportBatchRecord batch, ClaimedChunk chunk, string category, string action, IReadOnlyDictionary<string, string?> details) => new()
        {
            WorkspaceId = batch.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = category,
            Action = action,
            ActorType = AuditActorType.Service,
            ActorId = ImportWorkerActor,
            ActorDisplay = "Import",
            OnBehalfOf = chunk.InitiatedBy,
            ResourceType = "Job",
            ResourceId = chunk.Lease.JobId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = string.IsNullOrEmpty(chunk.CorrelationId) ? null : chunk.CorrelationId,
            JobId = chunk.Lease.JobId,
            ChunkSequence = chunk.Sequence,
            Details = details,
        };

    // ---- reading ----------------------------------------------------------------------------------------------

    private static async Task<ImportBatchRecord?> ReadAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid importBatchId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.import_batch WHERE workspace_id = @ws AND import_batch_id = @id");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async Task<ImportBatchRecord?> ReadByJobAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.import_batch WHERE workspace_id = @ws AND job_id = @job");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static ImportBatchRecord Read(NpgsqlDataReader reader)
    {
        ImportPreparation? preparation = null;
        if (!reader.IsDBNull(20))
        {
            preparation = new ImportPreparation(
                JsonSerializer.Deserialize<string[]>(reader.GetString(13)) ?? [],
                reader.GetString(14),
                reader.GetBoolean(15),
                reader.GetInt64(16),
                reader.GetInt64(17),
                reader.GetInt32(18),
                reader.GetInt32(19),
                reader.IsDBNull(34) ? null : reader.GetInt64(34));
        }

        return new ImportBatchRecord
        {
            WorkspaceId = reader.GetGuid(0),
            ImportBatchId = reader.GetGuid(1),
            JobId = reader.GetGuid(2),
            Name = reader.GetString(3),
            Mode = (ImportMode)(reader.GetInt16(4) - 1),
            SourceFileName = reader.GetString(5),
            SourceObjectKey = reader.GetString(6),
            SourceSha256 = reader.GetFieldValue<byte[]>(7),
            SourceSize = reader.GetInt64(8),
            ProfileId = reader.IsDBNull(9) ? null : reader.GetGuid(9),
            ProfileVersion = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            ProfileJson = reader.GetString(11),
            CodingOverlayFieldIds = reader.GetFieldValue<int[]>(12),
            Preparation = preparation,
            PreparedAt = reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
            RowsImported = reader.GetInt64(21),
            RowsOverlaid = reader.GetInt64(22),
            RowsSkipped = reader.GetInt64(23),
            RowsErrored = reader.GetInt64(24),
            CreatedBy = reader.GetGuid(25),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(26),
            CompletedAt = reader.IsDBNull(27) ? null : reader.GetFieldValue<DateTimeOffset>(27),
            MayCreateFields = reader.GetBoolean(28),
            Opt = reader.IsDBNull(30)
                ? null
                : new ImportOptSource(reader.GetString(29), reader.GetString(30), reader.GetFieldValue<byte[]>(31), reader.GetInt64(32)),
            ImagesOnly = reader.GetBoolean(33),
        };
    }

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    /// <summary>The decisions and outcomes of one chunk.</summary>
    private sealed class ChunkPlan
    {
        public Dictionary<long, List<ImportRowIssue>> Issues { get; } = [];

        public Dictionary<long, ImportRow> Rows { get; } = [];

        /// <summary>Rows an earlier committed attempt already wrote.</summary>
        public HashSet<long> DoneRows { get; } = [];

        /// <summary>OPT warnings the chunk itself found (images of rows that were not loaded).</summary>
        public List<ImportOptIssue> OptIssues { get; } = [];

        public List<ImportRow> Inserts { get; } = [];

        public List<(ImportRow Row, Guid DocumentId)> Overlays { get; } = [];

        public List<(ImportRow Row, Guid DocumentId, short Action)> Members { get; } = [];

        public List<ImportRow> ErroredRows { get; } = [];

        /// <summary>Duplicate group and email thread of each existing document before this chunk (overlay recounts).</summary>
        public Dictionary<Guid, (Guid? DuplicateGroupId, Guid? EmailThreadId)> PreviousRelationships { get; } = [];

        /// <summary>Documents outside the chunk whose relationship flags the chunk changed (E09-T02).</summary>
        public List<Guid> OtherChangedDocuments { get; } = [];

        public bool CodingChanged { get; set; }

        public bool SecurityChanged { get; set; }

        public int Imported => Members.Count(m => m.Action == ActionImported);

        public int Overlaid => Members.Count(m => m.Action == ActionOverlaid);

        public int Skipped => Members.Count(m => m.Action == ActionSkipped);

        public int Errored => ErroredRows.Count;

        public bool FamiliesChanged { get; set; }

        public SearchChangeMask ChangeMask =>
            SearchChangeMask.Content | SearchChangeMask.Metadata
            | (FamiliesChanged ? SearchChangeMask.Relationships : SearchChangeMask.None)
            | (CodingChanged ? SearchChangeMask.Coding : SearchChangeMask.None)
            | (SecurityChanged ? SearchChangeMask.Security : SearchChangeMask.None);

        public void Error(ImportRow row, string code, string message)
        {
            if (!Issues.TryGetValue(row.RowNo, out var issues))
            {
                issues = [];
                Issues[row.RowNo] = issues;
            }

            issues.Add(new ImportRowIssue(ImportIssueSeverity.Error, code, message));
            ErroredRows.Add(row);
        }

        public ChunkCompletion Completion() => new()
        {
            ItemsApplied = Imported + Overlaid,
            ItemsUnchanged = Skipped,
            ItemResults = [.. ErroredRows.OrderBy(r => r.RowNo).Select(r =>
            {
                var first = Issues.TryGetValue(r.RowNo, out var issues) ? issues.FirstOrDefault(i => i.Severity == ImportIssueSeverity.Error) : null;
                return new JobItemResult(
                    JobItemResultKind.Failed, null, r.RowNo, null,
                    Truncate(first?.Code ?? "row-error", JobItemResult.MaxReasonCodeLength)!,
                    Truncate(first?.Message, JobItemResult.MaxDetailLength));
            })],
        };
    }
}
