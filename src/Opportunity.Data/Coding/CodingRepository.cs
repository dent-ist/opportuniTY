using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Audit;
using Opportunity.Data.Fields;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;

namespace Opportunity.Data.Coding;

/// <summary>
/// Interim PostgreSQL coding store (V0004, §27): <c>document_coding_field</c> / <c>document_coding_choice</c> for current
/// state, <c>coding_write</c> for idempotency and the monthly-partitioned <c>coding_event</c> for provenance. This class is
/// the only code that knows those tables, so the E18-T03 spike can replace them behind <see cref="ICodingRepository"/>.
/// </summary>
/// <remarks>
/// One transaction per write: claim the idempotency key, share-lock the field definitions, lock the documents'
/// projection state in DocumentId order (the bulk lock order of ADR-010 §6), compute state-based changes, then write
/// current state, events and version bumps in one round trip. The wide <c>document</c> row is never touched (ADR-003 R2).
/// </remarks>
public sealed class CodingRepository(NpgsqlDataSource dataSource) : ICodingRepository
{
    public async Task<CodingWriteResult> ApplyAsync(CodingWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shapeErrors = ValidateShape(request);
        if (shapeErrors.Count > 0)
        {
            return CodingWriteResult.Failed(CodingWriteOutcome.Invalid, [.. shapeErrors]);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var (result, plan) = await ApplyInTransactionAsync(tx, request, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is not (CodingWriteOutcome.Applied or CodingWriteOutcome.Replayed))
        {
            return result;
        }

        // ADR-001 R1: an interactive write's search work commits with it, one SearchOutbox row per changed document, as
        // the transaction's last statement. Job-originated writes get their IndexChunkTask from ApplyChunkAsync.
        if (result.Outcome == CodingWriteOutcome.Applied && request.JobId is null && plan.BumpedDocuments.Count > 0)
        {
            await SearchWorkSql.AddOutboxRowsAsync(tx, ChangedVersions(result, plan), ChangeMask(plan), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CodingChunkResult> ApplyChunkAsync(
        ClaimedChunk chunk, CodingWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(request);
        var lease = chunk.Lease;
        if (request.WorkspaceId != lease.WorkspaceId || request.JobId != lease.JobId || request.Actor.Type == CodingActorType.Human)
        {
            throw new ArgumentException("A chunk write is a job-originated write of the leased chunk's workspace and job.", nameof(request));
        }

        var shapeErrors = ValidateShape(request);
        if (shapeErrors.Count > 0)
        {
            return new CodingChunkResult(CodingWriteResult.Failed(CodingWriteOutcome.Invalid, [.. shapeErrors]), null, null);
        }

        CodingWriteResult result;
        ChunkCommitResult commit;
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, request.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            (result, var plan) = await ApplyInTransactionAsync(tx, request, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != CodingWriteOutcome.Applied)
            {
                return new CodingChunkResult(result, null, null);
            }

            // §21 / ADR-001 R2: exactly one IndexChunkTask for the chunk and no SearchOutbox rows; none when nothing changed.
            var task = plan.BumpedDocuments.Count == 0 ? null : new NewIndexChunkTask
            {
                JobId = lease.JobId,
                ChunkId = lease.ChunkId,
                Kind = IndexTaskKind.BulkCoding,
                Membership = chunk.Membership,
                ChangeMask = ChangeMask(plan),
                IdempotencyKey = ChunkIdempotencyKey.ForChunk(lease.WorkspaceId, lease.JobId, chunk.Sequence, ChunkOperationKind.IndexChunk, 0),
            };
            (commit, var taskId) = await SearchWorkSql.CommitChunkAsync(tx, lease, Completion(result), task, cancellationToken)
                .ConfigureAwait(false);
            if (commit.Committed)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CodingChunkResult(result, commit, taskId);
            }
        }

        // Fence F3 refused: the coding writes rolled back with the transaction; record the fence outcome on the chunk.
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

        return new CodingChunkResult(result, commit, null);
    }

    public async Task<IReadOnlyList<DocumentCoding>> GetCurrentAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT s.document_id, s.document_version, f.field_id, fd.field_type, f.value::text, f.changed_at_version,
                   f.changed_by_job_id, f.changed_by, f.changed_at,
                   (SELECT array_agg(c.choice_id ORDER BY c.choice_id) FROM opportunity.document_coding_choice c
                    WHERE c.workspace_id = f.workspace_id AND c.document_id = f.document_id AND c.field_id = f.field_id)
            FROM opportunity.document_projection_state s
            LEFT JOIN (opportunity.document_coding_field f
                       JOIN opportunity.field_definition fd
                         ON fd.workspace_id = f.workspace_id AND fd.field_id = f.field_id AND NOT fd.is_deleted)
              ON f.workspace_id = s.workspace_id AND f.document_id = s.document_id
            WHERE s.workspace_id = @ws AND s.document_id = ANY(@ids) AND NOT s.is_deleted
            ORDER BY s.document_id, f.field_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("ids", documentIds.ToArray());
        var documents = new List<DocumentCoding>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            List<FieldCodingState>? fields = null;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var documentId = reader.GetGuid(0);
                if (documents.Count == 0 || documents[^1].DocumentId != documentId)
                {
                    fields = [];
                    documents.Add(new DocumentCoding(documentId, reader.GetInt64(1), fields));
                }

                if (!reader.IsDBNull(2))
                {
                    var type = (FieldType)reader.GetInt16(3);
                    fields!.Add(new FieldCodingState(
                        reader.GetInt32(2),
                        ReadValue(type, reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(9) ? null : reader.GetFieldValue<int[]>(9)),
                        reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetGuid(6),
                        reader.GetGuid(7),
                        reader.GetFieldValue<DateTimeOffset>(8)));
                }
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return documents;
    }

    public async Task<IReadOnlyDictionary<int, JsonNode>> GetValuesAsOfVersionAsync(
        Guid workspaceId, Guid documentId, long documentVersion, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT DISTINCT ON (e.field_id) e.field_id, e.new_value::text
            FROM opportunity.coding_event e
            WHERE e.workspace_id = @ws AND e.document_id = @doc AND e.event_kind = 1 AND e.document_version <= @version
            ORDER BY e.field_id, e.document_version DESC
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("doc", documentId);
        command.Parameters.AddWithValue("version", documentVersion);
        var values = new Dictionary<int, JsonNode>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(1))
                {
                    values.Add(reader.GetInt32(0), JsonNode.Parse(reader.GetString(1))!);
                }
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return values;
    }

    public async Task<CodingEventPage> GetEventsAsync(CodingEventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 1000);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, query.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT event_id, occurred_at, document_id, field_id, event_kind, prior_value::text, new_value::text,
                   document_version, actor_id, actor_type, job_id, idempotency_key
            FROM opportunity.coding_event
            WHERE workspace_id = @ws
              AND (@doc::uuid IS NULL OR document_id = @doc)
              AND (@field::integer IS NULL OR field_id = @field)
              AND (@actor_type::smallint IS NULL OR actor_type = @actor_type)
              AND (@job::uuid IS NULL OR job_id = @job)
              AND (@kind::smallint IS NULL OR event_kind = @kind)
              AND (@from::timestamptz IS NULL OR occurred_at >= @from)
              AND (@to::timestamptz IS NULL OR occurred_at < @to)
              AND (@after_at::timestamptz IS NULL OR (occurred_at, event_id) > (@after_at, @after_id))
            ORDER BY occurred_at, event_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", query.WorkspaceId);
        command.Parameters.Add(FieldCatalogRepository.Nullable("doc", NpgsqlDbType.Uuid, query.DocumentId));
        command.Parameters.Add(FieldCatalogRepository.Nullable("field", NpgsqlDbType.Integer, query.FieldId));
        command.Parameters.Add(FieldCatalogRepository.Nullable("actor_type", NpgsqlDbType.Smallint, (short?)query.ActorType));
        command.Parameters.Add(FieldCatalogRepository.Nullable("job", NpgsqlDbType.Uuid, query.JobId));
        command.Parameters.Add(FieldCatalogRepository.Nullable("kind", NpgsqlDbType.Smallint, (short?)query.Kind));
        command.Parameters.Add(FieldCatalogRepository.Nullable("from", NpgsqlDbType.TimestampTz, query.From?.ToUniversalTime()));
        command.Parameters.Add(FieldCatalogRepository.Nullable("to", NpgsqlDbType.TimestampTz, query.To?.ToUniversalTime()));
        command.Parameters.Add(FieldCatalogRepository.Nullable("after_at", NpgsqlDbType.TimestampTz, query.After?.OccurredAt.ToUniversalTime()));
        command.Parameters.Add(FieldCatalogRepository.Nullable("after_id", NpgsqlDbType.Uuid, query.After?.EventId));
        command.Parameters.AddWithValue("limit", query.Limit + 1);

        var events = new List<CodingEvent>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(new CodingEvent(
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetGuid(2),
                    reader.GetInt32(3),
                    (CodingEventKind)reader.GetInt16(4),
                    reader.IsDBNull(5) ? null : JsonNode.Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)),
                    reader.GetInt64(7),
                    reader.GetGuid(8),
                    (CodingActorType)reader.GetInt16(9),
                    reader.IsDBNull(10) ? null : reader.GetGuid(10),
                    reader.GetString(11)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (events.Count <= query.Limit)
        {
            return new CodingEventPage(events, null);
        }

        events.RemoveAt(events.Count - 1);
        return new CodingEventPage(events, new CodingEventCursor(events[^1].OccurredAt, events[^1].EventId));
    }

    private static async Task<(CodingWriteResult Result, WritePlan Plan)> ApplyInTransactionAsync(
        WorkspaceTransaction tx, CodingWriteRequest request, CancellationToken cancellationToken)
    {
        var plan = new WritePlan();
        var ws = request.WorkspaceId;
        var targets = request.Documents.OrderBy(d => d.DocumentId).ToList();
        var documentIds = targets.Select(d => d.DocumentId).ToArray();
        var fieldIds = request.Operations.Select(o => o.FieldId).ToArray();
        var hash = RequestHash(request);

        // 1. Claim the idempotency key. A concurrent duplicate waits here on the unique index until the first commits.
        var writeId = Guid.CreateVersion7();
        if (!await ClaimAsync(tx, request, writeId, hash, cancellationToken).ConfigureAwait(false))
        {
            return (await ReplayAsync(tx, request, hash, cancellationToken).ConfigureAwait(false), plan);
        }

        // 2. Field definitions, share-locked so they cannot be retyped or deleted while this write runs.
        var catalog = await FieldCatalogRepository.LoadCatalogAsync(tx, ws, false, fieldIds, true, cancellationToken).ConfigureAwait(false);
        var (operations, fieldErrors) = Resolve(request.Operations, catalog);
        if (fieldErrors.Count > 0)
        {
            return (CodingWriteResult.Failed(CodingWriteOutcome.Invalid, [.. fieldErrors]), plan);
        }

        // 3. Lock the documents (projection state) in DocumentId order.
        var versions = await LockDocumentsAsync(tx, ws, documentIds, cancellationToken).ConfigureAwait(false);
        if (request.Actor.Type == CodingActorType.Human && targets.Count == 1)
        {
            if (!versions.TryGetValue(targets[0].DocumentId, out var current))
            {
                return (CodingWriteResult.Failed(CodingWriteOutcome.NotFound), plan);
            }

            if (request.ExpectedVersion is { } expected && expected != current)
            {
                return (new CodingWriteResult(CodingWriteOutcome.VersionConflict,
                    [new DocumentCodingResult(targets[0].DocumentId, DocumentCodingOutcome.Unchanged, current, [])], [], 0, false), plan);
            }
        }

        // 4. Current state of the touched fields.
        var state = await LoadStateAsync(tx, ws, documentIds, fieldIds, catalog, cancellationToken).ConfigureAwait(false);

        // 5. State-based changes and Q-07 skips.
        var results = new List<DocumentCodingResult>(targets.Count);
        foreach (var target in targets)
        {
            if (!versions.TryGetValue(target.DocumentId, out var version))
            {
                results.Add(new DocumentCodingResult(target.DocumentId, DocumentCodingOutcome.NotFound, null, []));
                continue;
            }

            var newVersion = version + 1;
            var changed = false;
            var skipped = new List<int>();
            foreach (var op in operations)
            {
                state.TryGetValue((target.DocumentId, op.Field.FieldId), out var fieldState);
                var currentValue = fieldState?.Value;
                var desired = op.Apply(currentValue);
                if (target.BaselineVersion is { } baseline && fieldState is not null
                    && fieldState.ChangedAtVersion > baseline && fieldState.ChangedByJobId != request.JobId)
                {
                    skipped.Add(op.Field.FieldId);
                    plan.Events.Add(new PlannedEvent(target.DocumentId, op.Field.FieldId, CodingEventKind.BulkSkippedConcurrentEdit,
                        currentValue, desired, version));
                    continue;
                }

                if (FieldValues.AreEqual(currentValue, desired))
                {
                    continue;
                }

                changed = true;
                plan.Fields.Add((target.DocumentId, op.Field.FieldId, op.Field.IsChoice ? null : desired?.ToJsonString(), newVersion));
                if (op.Field.IsChoice)
                {
                    var before = FieldValues.ChoiceIds(currentValue);
                    var after = FieldValues.ChoiceIds(desired);
                    plan.RemovedChoices.AddRange(before.Except(after).Select(c => (target.DocumentId, op.Field.FieldId, c)));
                    plan.AddedChoices.AddRange(after.Except(before).Select(c => (target.DocumentId, op.Field.FieldId, c)));
                }

                plan.Events.Add(new PlannedEvent(target.DocumentId, op.Field.FieldId, CodingEventKind.ValueChanged, currentValue, desired, newVersion));
                if (op.Field.IsSecurityAffecting)
                {
                    plan.TouchesSecurity = true;
                    plan.SecurityFieldIds.Add(op.Field.FieldId);
                }
            }

            if (changed)
            {
                plan.BumpedDocuments.Add(target.DocumentId);
            }

            var outcome = skipped.Count > 0 ? DocumentCodingOutcome.Skipped
                : changed ? DocumentCodingOutcome.Changed
                : DocumentCodingOutcome.Unchanged;
            results.Add(new DocumentCodingResult(target.DocumentId, outcome, changed ? newVersion : version, skipped));
        }

        // 6. The audit event first, then current state + events + version bumps: one transaction, so neither the change
        //    nor its audit can commit alone (ADR-013 §2.1). A job chunk's audit also shares the transaction with its
        //    IndexChunkTask and fence F3 (ApplyChunkAsync), so a refused chunk leaves no audit either.
        if (request.Audit is { } audit)
        {
            await AuditSql.InsertAsync(tx, CodingAudit(audit, request, writeId, plan, results), cancellationToken).ConfigureAwait(false);
        }

        if (plan.Events.Count > 0)
        {
            await WriteAsync(tx, request, writeId, plan, cancellationToken).ConfigureAwait(false);
        }

        return (new CodingWriteResult(CodingWriteOutcome.Applied, results, [], plan.Events.Count, plan.TouchesSecurity), plan);
    }

    /// <summary>The documents whose version this write bumped, with the version it wrote.</summary>
    private static List<(Guid DocumentId, long DocumentVersion)> ChangedVersions(CodingWriteResult result, WritePlan plan)
    {
        var bumped = plan.BumpedDocuments.ToHashSet();
        return [.. result.Documents.Where(d => bumped.Contains(d.DocumentId)).Select(d => (d.DocumentId, d.DocumentVersion!.Value))];
    }

    private static SearchChangeMask ChangeMask(WritePlan plan) =>
        plan.TouchesSecurity ? SearchChangeMask.Coding | SearchChangeMask.Security : SearchChangeMask.Coding;

    /// <summary>
    /// The chunk's job counters and item results (ADR-010 §1): one Q-07 skip per document (first skipped field, all of them
    /// in the detail), missing documents as failed items.
    /// </summary>
    private static ChunkCompletion Completion(CodingWriteResult result)
    {
        var items = new List<JobItemResult>();
        foreach (var document in result.Documents)
        {
            if (document.SkippedFieldIds.Count > 0)
            {
                items.Add(new JobItemResult(JobItemResultKind.SkippedConcurrentEdit, document.DocumentId, null,
                    document.SkippedFieldIds[0], "ConcurrentEdit", "Fields " + string.Join(',', document.SkippedFieldIds)));
            }

            if (document.Outcome == DocumentCodingOutcome.NotFound)
            {
                items.Add(new JobItemResult(JobItemResultKind.Failed, document.DocumentId, null, null, "DocumentNotFound"));
            }
        }

        return new ChunkCompletion
        {
            ItemsApplied = result.Documents.Count(d => d.Outcome == DocumentCodingOutcome.Changed),
            ItemsUnchanged = result.Documents.Count(d => d.Outcome == DocumentCodingOutcome.Unchanged),
            ItemResults = items,
        };
    }

    /// <summary>Creates <c>coding_event</c> partitions <paramref name="monthsAhead"/> months ahead (V0004); returns how many.</summary>
    internal static async Task<int> EnsureEventPartitionsAsync(WorkspaceTransaction tx, int monthsAhead, CancellationToken cancellationToken)
    {
        await using var command = tx.Command("SELECT opportunity.coding_event_ensure_partitions(now() + make_interval(months => @months))");
        command.Parameters.AddWithValue("months", monthsAhead);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// <c>Coding.Changed</c> (interactive) or <c>Coding.BulkChunkApplied</c> (job chunk), ADR-013 §5/§6: counts, the field
    /// IDs and the coding write that links to the CodingEvents; old and new values only for security-affecting fields
    /// of a single-document write.
    /// </summary>
    private static AuditEvent CodingAudit(
        AuditEvent template, CodingWriteRequest request, Guid writeId, WritePlan plan, List<DocumentCodingResult> results)
    {
        var details = new Dictionary<string, string?>(template.Details)
        {
            ["CodingWriteId"] = writeId.ToString(),
            ["Fields"] = string.Join(',', request.Operations.Select(o => o.FieldId).Distinct().Order()),
            ["Documents"] = Invariant(results.Count),
            ["Changed"] = Invariant(results.Count(r => r.Outcome == DocumentCodingOutcome.Changed)),
            ["Skipped"] = Invariant(results.Count(r => r.Outcome == DocumentCodingOutcome.Skipped)),
            ["NotFound"] = Invariant(results.Count(r => r.Outcome == DocumentCodingOutcome.NotFound)),
            ["CodingEvents"] = Invariant(plan.Events.Count),
            ["SecurityAffecting"] = plan.TouchesSecurity ? "true" : "false",
        };
        if (results.Count == 1)
        {
            foreach (var e in plan.Events.Where(e => e.Kind == CodingEventKind.ValueChanged && plan.SecurityFieldIds.Contains(e.FieldId)))
            {
                details[$"Field.{Invariant(e.FieldId)}.Old"] = e.Prior?.ToJsonString();
                details[$"Field.{Invariant(e.FieldId)}.New"] = e.New?.ToJsonString();
            }
        }

        var single = request.Documents.Count == 1;
        return template with
        {
            EventId = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId,
            Category = AuditTaxonomy.Coding.Category,
            Action = request.JobId is null ? AuditTaxonomy.Coding.Changed : AuditTaxonomy.Coding.BulkChunkApplied,
            ResourceType = single ? "Document" : request.JobId is null ? null : "Job",
            ResourceId = single ? request.Documents[0].DocumentId.ToString() : request.JobId?.ToString(),
            JobId = request.JobId ?? template.JobId,
            Details = details,
        };
    }

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static List<FieldError> ValidateShape(CodingWriteRequest request)
    {
        var errors = new List<FieldError>();
        void Add(string field, string code, string message) => errors.Add(new FieldError(field, code, message));

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > CodingWriteRequest.MaxIdempotencyKeyLength)
        {
            Add("idempotencyKey", "invalid-idempotency-key", $"An idempotency key of 1–{CodingWriteRequest.MaxIdempotencyKeyLength} characters is required.");
        }

        if (request.Documents is null || request.Documents.Count is 0 or > CodingWriteRequest.MaxDocuments
            || request.Documents.Select(d => d.DocumentId).Distinct().Count() != request.Documents.Count)
        {
            Add("documents", "invalid-documents", $"Between 1 and {CodingWriteRequest.MaxDocuments} distinct documents are required.");
        }

        if (request.Operations is null || request.Operations.Count is 0 or > CodingWriteRequest.MaxOperations
            || request.Operations.Select(o => o.FieldId).Distinct().Count() != request.Operations.Count)
        {
            Add("operations", "invalid-operations", $"Between 1 and {CodingWriteRequest.MaxOperations} operations on distinct fields are required.");
        }

        switch (request.Actor?.Type)
        {
            case CodingActorType.Human when request.JobId is not null:
                Add("jobId", "unexpected-job", "Interactive coding has no job.");
                break;
            case CodingActorType.BulkHuman when request.JobId is null:
                Add("jobId", "job-required", "Bulk coding runs in a job.");
                break;
            case CodingActorType.Model:
                Add("actor", "actor-type-reserved", "Model coding is reserved and not enabled.");
                break;
            case null or < CodingActorType.Human or > CodingActorType.Model:
                Add("actor", "invalid-actor", "An actor is required.");
                break;
        }

        var documents = request.Documents ?? [];
        if (request.Actor?.Type == CodingActorType.BulkHuman && documents.Any(d => d.BaselineVersion is null))
        {
            Add("documents", "baseline-required", "Bulk coding needs each document's snapshot BaselineVersion (Q-07).");
        }

        if (request.JobId is null && documents.Any(d => d.BaselineVersion is not null))
        {
            Add("documents", "baseline-without-job", "Baseline versions belong to a job.");
        }

        if (request.ExpectedVersion is not null && (documents.Count != 1 || request.Actor?.Type != CodingActorType.Human))
        {
            Add("expectedVersion", "expected-version-single-document", "If-Match applies to a single interactive document.");
        }

        return errors;
    }

    private static (List<ResolvedOperation> Operations, List<FieldError> Errors) Resolve(
        IReadOnlyList<CodingFieldOperation> operations, FieldCatalog catalog)
    {
        var resolved = new List<ResolvedOperation>(operations.Count);
        var errors = new List<FieldError>();
        foreach (var op in operations)
        {
            var key = FieldKey.For(op.FieldId);
            var field = catalog.Find(op.FieldId);
            if (field is null)
            {
                errors.Add(new FieldError(key, "unknown-field", "The field does not exist in this workspace."));
                continue;
            }

            if (field.Storage != FieldStorage.Coding)
            {
                errors.Add(new FieldError(key, "not-coding-field", $"{field.Name} is imported data and cannot be coded (ADR-003 R2)."));
                continue;
            }

            var choices = catalog.ChoicesOf(field.FieldId);
            switch (op.Kind)
            {
                case CodingOperationKind.Set:
                    if (FieldValues.TryCanonicalize(field, op.Value, choices, true, out var canonical, out var error))
                    {
                        resolved.Add(new ResolvedOperation(field, op.Kind, canonical, []));
                    }
                    else
                    {
                        errors.Add(error!);
                    }

                    break;
                case CodingOperationKind.AddChoices or CodingOperationKind.RemoveChoices:
                    if (field.Type != FieldType.MultiChoice)
                    {
                        errors.Add(new FieldError(key, "not-multi-choice", "Adding or removing choices applies to MultiChoice fields."));
                    }
                    else if (FieldValues.TryCanonicalize(field, op.Value, choices, op.Kind == CodingOperationKind.AddChoices, out var ids, out var idError)
                             && ids is not null)
                    {
                        resolved.Add(new ResolvedOperation(field, op.Kind, null, FieldValues.ChoiceIds(ids)));
                    }
                    else
                    {
                        errors.Add(idError ?? new FieldError(key, "invalid-choice", "At least one choice id is required."));
                    }

                    break;
                default:
                    errors.Add(new FieldError(key, "invalid-operation", "Unknown operation."));
                    break;
            }
        }

        return (resolved, errors);
    }

    private static async Task<bool> ClaimAsync(
        WorkspaceTransaction tx, CodingWriteRequest request, Guid writeId, byte[] hash, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            INSERT INTO opportunity.coding_write (workspace_id, write_id, idempotency_key, request_hash, actor_id, actor_type, job_id)
            VALUES (@ws, @id, @key, @hash, @actor, @actor_type, @job)
            ON CONFLICT (workspace_id, idempotency_key) DO NOTHING
            """);
        command.Parameters.AddWithValue("ws", request.WorkspaceId);
        command.Parameters.AddWithValue("id", writeId);
        command.Parameters.AddWithValue("key", request.IdempotencyKey);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("actor", request.Actor.ActorId);
        command.Parameters.AddWithValue("actor_type", (short)request.Actor.Type);
        command.Parameters.Add(FieldCatalogRepository.Nullable("job", NpgsqlDbType.Uuid, request.JobId));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>The key was applied before: report what that write did, from its events, and write nothing.</summary>
    private static async Task<CodingWriteResult> ReplayAsync(
        WorkspaceTransaction tx, CodingWriteRequest request, byte[] hash, CancellationToken cancellationToken)
    {
        Guid writeId;
        await using (var existing = tx.Command(
            "SELECT write_id, request_hash FROM opportunity.coding_write WHERE workspace_id = @ws AND idempotency_key = @key"))
        {
            existing.Parameters.AddWithValue("ws", request.WorkspaceId);
            existing.Parameters.AddWithValue("key", request.IdempotencyKey);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            writeId = reader.GetGuid(0);
            if (!reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(hash))
            {
                return CodingWriteResult.Failed(CodingWriteOutcome.IdempotencyKeyReuse,
                    new FieldError("idempotencyKey", "idempotency-key-reuse", "The key was already used for a different coding request."));
            }
        }

        var byDocument = new Dictionary<Guid, (long Version, List<int> Skipped, bool Changed)>();
        var events = 0;
        await using (var command = tx.Command(
            """
            SELECT document_id, field_id, event_kind, document_version
            FROM opportunity.coding_event WHERE workspace_id = @ws AND write_id = @id ORDER BY document_id, field_id
            """))
        {
            command.Parameters.AddWithValue("ws", request.WorkspaceId);
            command.Parameters.AddWithValue("id", writeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events++;
                var documentId = reader.GetGuid(0);
                var entry = byDocument.GetValueOrDefault(documentId, (0, [], false));
                entry.Version = Math.Max(entry.Version, reader.GetInt64(3));
                if ((CodingEventKind)reader.GetInt16(2) == CodingEventKind.BulkSkippedConcurrentEdit)
                {
                    entry.Skipped.Add(reader.GetInt32(1));
                }
                else
                {
                    entry.Changed = true;
                }

                byDocument[documentId] = entry;
            }
        }

        var documents = byDocument
            .Select(d => new DocumentCodingResult(
                d.Key,
                d.Value.Skipped.Count > 0 ? DocumentCodingOutcome.Skipped : DocumentCodingOutcome.Changed,
                d.Value.Version,
                d.Value.Skipped))
            .ToList();
        return new CodingWriteResult(CodingWriteOutcome.Replayed, documents, [], events, false);
    }

    private static async Task<Dictionary<Guid, long>> LockDocumentsAsync(
        WorkspaceTransaction tx, Guid ws, Guid[] documentIds, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT document_id, document_version, is_deleted FROM opportunity.document_projection_state
            WHERE workspace_id = @ws AND document_id = ANY(@ids)
            ORDER BY document_id
            FOR UPDATE
            """);
        command.Parameters.AddWithValue("ws", ws);
        command.Parameters.AddWithValue("ids", documentIds);
        var versions = new Dictionary<Guid, long>(documentIds.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.GetBoolean(2))
            {
                versions.Add(reader.GetGuid(0), reader.GetInt64(1));
            }
        }

        return versions;
    }

    private static async Task<Dictionary<(Guid, int), FieldCodingState>> LoadStateAsync(
        WorkspaceTransaction tx, Guid ws, Guid[] documentIds, int[] fieldIds, FieldCatalog catalog, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT f.document_id, f.field_id, f.value::text, f.changed_at_version, f.changed_by_job_id, f.changed_by, f.changed_at,
                   (SELECT array_agg(c.choice_id ORDER BY c.choice_id) FROM opportunity.document_coding_choice c
                    WHERE c.workspace_id = f.workspace_id AND c.document_id = f.document_id AND c.field_id = f.field_id)
            FROM opportunity.document_coding_field f
            WHERE f.workspace_id = @ws AND f.document_id = ANY(@ids) AND f.field_id = ANY(@fields)
            """);
        command.Parameters.AddWithValue("ws", ws);
        command.Parameters.AddWithValue("ids", documentIds);
        command.Parameters.AddWithValue("fields", fieldIds);
        var state = new Dictionary<(Guid, int), FieldCodingState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fieldId = reader.GetInt32(1);
            var value = ReadValue(
                catalog.Find(fieldId)!.Type,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<int[]>(7));
            state.Add((reader.GetGuid(0), fieldId), new FieldCodingState(
                fieldId, value, reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetGuid(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return state;
    }

    private static async Task WriteAsync(
        WorkspaceTransaction tx, CodingWriteRequest request, Guid writeId, WritePlan plan, CancellationToken cancellationToken)
    {
        var ws = request.WorkspaceId;
        await using var batch = new NpgsqlBatch(tx.Connection, tx.Transaction);
        if (plan.BumpedDocuments.Count > 0)
        {
            batch.BatchCommands.Add(Command(
                """
                UPDATE opportunity.document_projection_state s SET document_version = s.document_version + 1
                FROM unnest($2::uuid[]) AS d(id)
                WHERE s.workspace_id = $1 AND s.document_id = d.id
                """,
                Uuid(ws), Array(NpgsqlDbType.Uuid, plan.BumpedDocuments.ToArray())));
        }

        if (plan.Fields.Count > 0)
        {
            batch.BatchCommands.Add(Command(
                """
                INSERT INTO opportunity.document_coding_field
                    (workspace_id, document_id, field_id, value, changed_at_version, changed_by_job_id, changed_by, changed_at)
                SELECT $1, u.document_id, u.field_id, u.value::jsonb, u.version, $6, $7, now()
                FROM unnest($2::uuid[], $3::integer[], $4::text[], $5::bigint[]) AS u(document_id, field_id, value, version)
                ON CONFLICT (workspace_id, document_id, field_id) DO UPDATE SET
                    value = EXCLUDED.value, changed_at_version = EXCLUDED.changed_at_version,
                    changed_by_job_id = EXCLUDED.changed_by_job_id, changed_by = EXCLUDED.changed_by, changed_at = EXCLUDED.changed_at
                """,
                Uuid(ws),
                Array(NpgsqlDbType.Uuid, plan.Fields.Select(f => f.DocumentId).ToArray()),
                Array(NpgsqlDbType.Integer, plan.Fields.Select(f => f.FieldId).ToArray()),
                Array(NpgsqlDbType.Text, plan.Fields.Select(f => f.Value).ToArray()),
                Array(NpgsqlDbType.Bigint, plan.Fields.Select(f => f.Version).ToArray()),
                Nullable(NpgsqlDbType.Uuid, request.JobId),
                Uuid(request.Actor.ActorId)));
        }

        if (plan.RemovedChoices.Count > 0)
        {
            batch.BatchCommands.Add(Command(
                """
                DELETE FROM opportunity.document_coding_choice c
                USING unnest($2::uuid[], $3::integer[], $4::integer[]) AS u(document_id, field_id, choice_id)
                WHERE c.workspace_id = $1 AND c.document_id = u.document_id AND c.field_id = u.field_id AND c.choice_id = u.choice_id
                """,
                Uuid(ws),
                Array(NpgsqlDbType.Uuid, plan.RemovedChoices.Select(c => c.DocumentId).ToArray()),
                Array(NpgsqlDbType.Integer, plan.RemovedChoices.Select(c => c.FieldId).ToArray()),
                Array(NpgsqlDbType.Integer, plan.RemovedChoices.Select(c => c.ChoiceId).ToArray())));
        }

        if (plan.AddedChoices.Count > 0)
        {
            batch.BatchCommands.Add(Command(
                """
                INSERT INTO opportunity.document_coding_choice (workspace_id, document_id, field_id, choice_id)
                SELECT $1, u.document_id, u.field_id, u.choice_id
                FROM unnest($2::uuid[], $3::integer[], $4::integer[]) AS u(document_id, field_id, choice_id)
                """,
                Uuid(ws),
                Array(NpgsqlDbType.Uuid, plan.AddedChoices.Select(c => c.DocumentId).ToArray()),
                Array(NpgsqlDbType.Integer, plan.AddedChoices.Select(c => c.FieldId).ToArray()),
                Array(NpgsqlDbType.Integer, plan.AddedChoices.Select(c => c.ChoiceId).ToArray())));

            // ADR-003 R8: an assigned choice can never be deleted again. The predicate skips (and does not lock) rows
            // already marked, so hot choices cost an index probe, not a row update, per write.
            batch.BatchCommands.Add(Command(
                """
                UPDATE opportunity.choice SET first_used_at = now()
                WHERE workspace_id = $1 AND choice_id = ANY($2::integer[]) AND first_used_at IS NULL
                """,
                Uuid(ws), Array(NpgsqlDbType.Integer, plan.AddedChoices.Select(c => c.ChoiceId).Distinct().ToArray())));
        }

        var events = plan.Events;
        batch.BatchCommands.Add(Command(
            """
            INSERT INTO opportunity.coding_event
                (workspace_id, occurred_at, event_id, write_id, document_id, field_id, event_kind, prior_value, new_value,
                 document_version, actor_id, actor_type, job_id, idempotency_key)
            SELECT $1, now(), u.event_id, $2, u.document_id, u.field_id, u.kind, u.prior::jsonb, u.new::jsonb,
                   u.version, $10, $11, $12, $13
            FROM unnest($3::uuid[], $4::uuid[], $5::integer[], $6::smallint[], $7::text[], $8::text[], $9::bigint[])
                AS u(event_id, document_id, field_id, kind, prior, new, version)
            """,
            Uuid(ws),
            Uuid(writeId),
            Array(NpgsqlDbType.Uuid, events.Select(_ => Guid.CreateVersion7()).ToArray()),
            Array(NpgsqlDbType.Uuid, events.Select(e => e.DocumentId).ToArray()),
            Array(NpgsqlDbType.Integer, events.Select(e => e.FieldId).ToArray()),
            Array(NpgsqlDbType.Smallint, events.Select(e => (short)e.Kind).ToArray()),
            Array(NpgsqlDbType.Text, events.Select(e => e.Prior?.ToJsonString()).ToArray()),
            Array(NpgsqlDbType.Text, events.Select(e => e.New?.ToJsonString()).ToArray()),
            Array(NpgsqlDbType.Bigint, events.Select(e => e.Version).ToArray()),
            Uuid(request.Actor.ActorId),
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)request.Actor.Type },
            Nullable(NpgsqlDbType.Uuid, request.JobId),
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.IdempotencyKey }));

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonNode? ReadValue(FieldType type, string? json, int[]? choiceIds) => type switch
    {
        FieldType.MultiChoice => FieldValues.ChoiceArray(choiceIds ?? []),
        FieldType.SingleChoice => choiceIds is [var id] ? JsonValue.Create(id) : null,
        _ => json is null ? null : JsonNode.Parse(json),
    };

    /// <summary>SHA-256 over a canonical rendering of the request, to tell a replay from key reuse.</summary>
    private static byte[] RequestHash(CodingWriteRequest request)
    {
        var builder = new StringBuilder("v1|");
        builder.Append(request.WorkspaceId.ToString("D")).Append('|')
            .Append(request.Actor.ActorId.ToString("D")).Append('|').Append((int)request.Actor.Type).Append('|')
            .Append(request.JobId?.ToString("D")).Append('|').Append(request.ExpectedVersion).Append('|');
        foreach (var document in request.Documents.OrderBy(d => d.DocumentId))
        {
            builder.Append(document.DocumentId.ToString("D")).Append(':').Append(document.BaselineVersion).Append(',');
        }

        builder.Append('|');
        foreach (var op in request.Operations.OrderBy(o => o.FieldId))
        {
            builder.Append(op.FieldId).Append(':').Append((int)op.Kind).Append(':').Append(op.Value?.ToJsonString()).Append(',');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static NpgsqlBatchCommand Command(string sql, params NpgsqlParameter[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static NpgsqlParameter Uuid(Guid value) => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value };

    private static NpgsqlParameter Nullable(NpgsqlDbType type, object? value) =>
        new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };

    private static NpgsqlParameter Array<T>(NpgsqlDbType elementType, T[] values) =>
        new() { NpgsqlDbType = NpgsqlDbType.Array | elementType, Value = values };

    private sealed record ResolvedOperation(FieldDefinition Field, CodingOperationKind Kind, JsonNode? Value, IReadOnlyList<int> ChoiceIds)
    {
        public JsonNode? Apply(JsonNode? current) => Kind switch
        {
            CodingOperationKind.Set => Value?.DeepClone(),
            CodingOperationKind.AddChoices => FieldValues.ChoiceArray(FieldValues.ChoiceIds(current).Union(ChoiceIds)),
            CodingOperationKind.RemoveChoices => FieldValues.ChoiceArray(FieldValues.ChoiceIds(current).Except(ChoiceIds)),
            _ => throw new InvalidOperationException("Unknown operation."),
        };
    }

    private sealed record PlannedEvent(Guid DocumentId, int FieldId, CodingEventKind Kind, JsonNode? Prior, JsonNode? New, long Version);

    private sealed class WritePlan
    {
        public List<Guid> BumpedDocuments { get; } = [];

        public List<(Guid DocumentId, int FieldId, string? Value, long Version)> Fields { get; } = [];

        public List<(Guid DocumentId, int FieldId, int ChoiceId)> RemovedChoices { get; } = [];

        public List<(Guid DocumentId, int FieldId, int ChoiceId)> AddedChoices { get; } = [];

        public List<PlannedEvent> Events { get; } = [];

        public bool TouchesSecurity { get; set; }

        public HashSet<int> SecurityFieldIds { get; } = [];
    }
}
