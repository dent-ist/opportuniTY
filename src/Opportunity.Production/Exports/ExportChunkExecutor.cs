using System.Globalization;
using System.Text;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Application.Storage;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;

using CoreScheme = Opportunity.Core.Storage.EncryptionScheme;

namespace Opportunity.Production.Exports;

/// <summary>
/// The <see cref="ChunkOperationKind.ExportChunk"/> executor (E12-T01), run by the idempotent chunk consumer for one
/// <c>SnapshotRange</c> of the export's frozen set:
/// <list type="number">
/// <item>Rebuilds the initiator's principal from PostgreSQL and re-checks <c>Export.Create</c>; if they lost it, the job
/// is cancelled (ADR-015 D9.4).</item>
/// <item>Re-authorizes every member with <c>Export.Create</c> against current security state (Q-15: restriction classes,
/// walls, deletion). Denied members are excluded, reported with the generic reason <c>AccessChanged</c> and audited
/// with the precise reason (<c>Export.DocumentsExcluded</c>).</item>
/// <item>Copies the natives, extracted text and page images of the remaining members into the volume layout, each read
/// through a hashing reader that fails the chunk on a chain-of-custody mismatch (ADR-011 §2.5), and writes the chunk's
/// DAT and OPT rows as parts that the finalization assembles in chunk order.</item>
/// <item>Commits outcome rows, file registrations, audit events and the chunk (fence F3) in one transaction.</item>
/// </list>
/// Every object key of an attempt carries the chunk's lease token, so a re-run after a crash writes fresh objects and the
/// one committed attempt is the only one registered; its content is the same as any other attempt's on unchanged data.
/// </summary>
public sealed class ExportChunkExecutor(
    IExportStore exports,
    IDocumentSetSnapshotStore snapshots,
    IJobRepository jobs,
    IAuthorizationService authorization,
    IFieldAccessFilter fieldAccess,
    IAuditEventWriter audit,
    IObjectStore store,
    ExportJobOptions options) : IJobChunkExecutor
{
    /// <summary>Requester-facing reason of a Q-15 exclusion; the precise one is in audit only (ADR-015 D9.4).</summary>
    public const string AccessChanged = "AccessChanged";

    /// <summary>Actor of worker-side export audit events, on behalf of the initiator.</summary>
    public const string ExportWorkerActor = "service:export";

    private const int ExcludedPerAuditEvent = 100;

    public ChunkOperationKind OperationKind => ChunkOperationKind.ExportChunk;

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Chunk;
        var membership = chunk.Membership;
        var ws = context.WorkspaceId;
        if (membership.Kind != ChunkMembershipKind.SnapshotRange || membership.SnapshotId is not { } snapshotId)
        {
            throw new PermanentChunkException("NotAnExportChunk", "An export chunk references a snapshot range.");
        }

        var export = await exports.GetByJobAsync(ws, chunk.Lease.JobId, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentChunkException("ExportMissing", "The chunk's job has no export.");
        if (export.SnapshotId != snapshotId)
        {
            throw new PermanentChunkException("ExportSnapshotMismatch", "The chunk's snapshot is not the export's.");
        }

        var settings = ExportSettings.Deserialize(export.SettingsJson);
        var principal = await ExportPrincipal.ResolveAsync(exports, export, cancellationToken).ConfigureAwait(false);

        // The initiator must still hold the job's permission; otherwise the remaining chunks are cancelled (D9.4).
        var allowed = await authorization.AuthorizeAsync(principal, ws, Permission.ExportCreate, cancellationToken).ConfigureAwait(false);
        if (!allowed.IsAllowed)
        {
            await jobs.CancelAsync(ws, chunk.Lease.JobId, export.CreatedBy, "The initiator no longer holds Export.Create.", cancellationToken)
                .ConfigureAwait(false);
            throw new ChunkFencedException(ChunkFence.JobCancelling);
        }

        var members = await snapshots.ReadMembersAsync(ws, snapshotId, membership.RangeFrom!.Value, membership.RangeTo!.Value, cancellationToken)
            .ConfigureAwait(false);
        if (members.Count != membership.KnownCount)
        {
            throw new PermanentChunkException("SnapshotUnavailable", "The frozen set is no longer readable for this range (expired or not Ready).");
        }

        // Q-15: re-authorize every member against current PostgreSQL security state.
        var decisions = await authorization.AuthorizeManyAsync(
            principal, ws, Permission.ExportCreate, [.. members.Select(m => m.DocumentId)], DenialAudit.Caller, cancellationToken)
            .ConfigureAwait(false);
        var permitted = members.Where(m => decisions.TryGetValue(m.DocumentId, out var d) && d.IsAllowed).Select(m => m.DocumentId).ToList();
        var source = await exports.ReadDocumentsAsync(ws, permitted, cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, principal, source.Catalog, cancellationToken).ConfigureAwait(false);

        var excluded = new List<(long Ordinal, Guid DocumentId, string Reason)>();
        var work = new List<(long Ordinal, ExportSourceDocument Document)>();
        foreach (var member in members)
        {
            if (!decisions.TryGetValue(member.DocumentId, out var decision) || !decision.IsAllowed)
            {
                excluded.Add((member.Ordinal, member.DocumentId, decision.Reason ?? AuthorizationReasons.DocumentNotFound));
            }
            else if (source.Documents.TryGetValue(member.DocumentId, out var document))
            {
                work.Add((member.Ordinal, document));
            }
            else
            {
                excluded.Add((member.Ordinal, member.DocumentId, AuthorizationReasons.DocumentNotFound));
            }
        }

        var layout = new ExportLayout(settings);
        var values = new ExportValues(source.Catalog, settings.Profile.MultiValue, restricted);
        var writer = new ChunkFiles(store, ws, export.ExportId, chunk.Lease.JobId, chunk.Sequence, chunk.Lease.LeaseToken);

        // Fence F2 before the object-store writes of this chunk.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var results = new DocumentResult[work.Count];
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, work.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.FileConcurrency), CancellationToken = cancellationToken },
                async (i, ct) => results[i] = await WriteDocumentAsync(writer, layout, settings, values, work[i].Ordinal, work[i].Document, ct)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (ObjectIntegrityException ex)
        {
            await AuditIntegrityAsync(export, chunk, ex, cancellationToken).ConfigureAwait(false);
            throw new PermanentChunkException("IntegrityHashMismatch", "A stored file no longer matches its recorded SHA-256; the export cannot include it.", ex);
        }
        catch (ObjectNotFoundException ex)
        {
            throw new PermanentChunkException("StoredObjectMissing", "A registered file is missing from object storage.", ex);
        }

        var files = new List<NewExportFile>(results.Sum(r => r.Files.Count) + 2);
        var dat = new StringBuilder();
        var opt = new StringBuilder();
        foreach (var result in results)
        {
            files.AddRange(result.Files);
            dat.Append(result.DatRow);
            opt.Append(result.OptRows);
        }

        // The chunk's DAT rows (and OPT rows) in ordinal order; the finalization concatenates the parts in chunk order.
        files.Add(await writer.WriteTextAsync("part.dat", LoadFileText.Encode(settings.DatEncoding, dat.ToString()), ExportFileKind.DatPart,
            ExportLayout.PartPath(chunk.Sequence, "dat"), cancellationToken).ConfigureAwait(false));
        if (settings.IncludeImages)
        {
            files.Add(await writer.WriteTextAsync("part.opt", Encoding.UTF8.GetBytes(opt.ToString()), ExportFileKind.OptPart,
                ExportLayout.PartPath(chunk.Sequence, "opt"), cancellationToken).ConfigureAwait(false));
        }

        var outcomes = new List<ExportDocumentOutcome>(members.Count);
        outcomes.AddRange(results.Select(r => r.Outcome));
        var excludedNumbers = await ControlNumbersAsync(ws, excluded, cancellationToken).ConfigureAwait(false);
        outcomes.AddRange(excluded.Select(e => new ExportDocumentOutcome(e.Ordinal, e.DocumentId, true, excludedNumbers.GetValueOrDefault(e.DocumentId), AccessChanged)));
        var completion = new ChunkCompletion
        {
            ItemsApplied = results.Length,
            ItemResults = [.. excluded.Select(e => new JobItemResult(JobItemResultKind.ExcludedNoAccess, e.DocumentId, null, null, AccessChanged))],
        };

        // Fence F2 again before the PostgreSQL batch; F3 runs inside the store's transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var commit = await exports.ApplyChunkAsync(
            chunk, new ExportChunkWrite(export.ExportId, outcomes, files, ExclusionAudit(export, chunk, excluded)), completion, cancellationToken)
            .ConfigureAwait(false);
        return ChunkExecutionResult.Committed(commit);
    }

    private static async Task<DocumentResult> WriteDocumentAsync(
        ChunkFiles writer, ExportLayout layout, ExportSettings settings, ExportValues values, long ordinal, ExportSourceDocument document,
        CancellationToken cancellationToken)
    {
        var cn = document.Document.ControlNumber;
        var name = ordinal.ToString("D10", CultureInfo.InvariantCulture);
        var files = new List<NewExportFile>();
        string? nativePath = null, textPath = null;
        int natives = 0, texts = 0;
        if (settings.IncludeNatives && document.Native is { } native)
        {
            var path = layout.NativePath(ordinal, cn, ExportLayout.NativeExtension(document.Document.FileExtension, native.ContentType));
            files.Add(await writer.CopyAsync(native, name + "-native", ExportFileKind.Native, path, ordinal, null, cancellationToken).ConfigureAwait(false));
            nativePath = layout.LoadFilePath(path);
            natives = 1;
        }

        if (settings.IncludeText && document.Text is { } text)
        {
            var path = layout.TextPath(ordinal, cn);
            var transcode = settings.TextFileEncoding == Import.LoadFiles.LoadFileEncodingKind.Utf16LE;
            files.Add(await writer.CopyAsync(text, name + "-text", ExportFileKind.Text, path, ordinal, transcode ? "utf-16le" : null, cancellationToken)
                .ConfigureAwait(false));
            textPath = layout.LoadFilePath(path);
            texts = 1;
        }

        var opt = new StringBuilder();
        int images = 0, pages = 0;
        if (settings.IncludeImages)
        {
            // One file per stored image: consecutive pages of one multi-page image (TIFF frames) share one OPT row.
            var withImages = document.Pages.Where(p => p.Image is not null).ToList();
            var groups = new List<List<Application.Exports.ExportSourcePage>>();
            foreach (var page in withImages)
            {
                if (groups.Count > 0 && groups[^1][^1].Image!.ObjectKey == page.Image!.ObjectKey)
                {
                    groups[^1].Add(page);
                }
                else
                {
                    groups.Add([page]);
                }
            }

            for (var g = 0; g < groups.Count; g++)
            {
                var first = groups[g][0];
                var key = ExportLayout.PageKey(cn, first.Ordinal);
                var path = layout.ImagePath(ordinal, key, first.Format);
                files.Add(await writer.CopyAsync(first.Image!, string.Create(CultureInfo.InvariantCulture, $"{name}-p{first.Ordinal:D6}"),
                    ExportFileKind.Image, path, ordinal, null, cancellationToken).ConfigureAwait(false));
                opt.Append(LoadFileText.OptRow(key, layout.Volume, layout.LoadFilePath(path), g == 0, g == 0 ? withImages.Count : null));
                images++;
            }

            pages = withImages.Count;
        }

        var (row, neutralize) = values.Row(settings.Columns, document, nativePath, textPath);
        return new DocumentResult(
            new ExportDocumentOutcome(ordinal, document.Document.DocumentId, false, cn, null, natives, texts, images, pages),
            files,
            LoadFileText.DatRow(settings.Profile, row, neutralize),
            opt.ToString());
    }

    private async Task<Dictionary<Guid, string>> ControlNumbersAsync(
        Guid ws, List<(long Ordinal, Guid DocumentId, string Reason)> excluded, CancellationToken cancellationToken)
    {
        // Excluded documents are named in the requester's report by control number (ADR-015 D9.4); deleted ones have none.
        if (excluded.Count == 0)
        {
            return [];
        }

        var batch = await exports.ReadDocumentsAsync(ws, [.. excluded.Select(e => e.DocumentId)], cancellationToken).ConfigureAwait(false);
        return batch.Documents.ToDictionary(d => d.Key, d => d.Value.Document.ControlNumber);
    }

    private static List<AuditEvent> ExclusionAudit(ExportRecord export, ClaimedChunk chunk, List<(long Ordinal, Guid DocumentId, string Reason)> excluded) =>
    [
        .. excluded.Chunk(ExcludedPerAuditEvent).Select(part => WorkerEvent(export, chunk, AuditTaxonomy.Export.DocumentsExcluded, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ExportId"] = export.ExportId.ToString(),
            ["Count"] = part.Length.ToString(CultureInfo.InvariantCulture),
            ["Documents"] = string.Join(',', part.Select(e => e.DocumentId.ToString("N") + ":" + e.Reason)),
        })),
    ];

    private async Task AuditIntegrityAsync(ExportRecord export, ClaimedChunk chunk, ObjectIntegrityException exception, CancellationToken cancellationToken)
    {
        var integrity = WorkerEvent(export, chunk, "HashMismatch", new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ExportId"] = export.ExportId.ToString(),
            ["Error"] = exception.Message.Length <= 500 ? exception.Message : exception.Message[..500],
        }) with
        {
            Category = "Integrity",
            Outcome = AuditOutcome.Failure,
            ReasonCode = "HashMismatch",
        };
        await audit.WriteAsync(integrity, cancellationToken).ConfigureAwait(false);
    }

    internal static AuditEvent WorkerEvent(ExportRecord export, ClaimedChunk? chunk, string action, IReadOnlyDictionary<string, string?> details) => new()
    {
        WorkspaceId = export.WorkspaceId,
        OccurredAt = DateTimeOffset.UtcNow,
        Category = AuditTaxonomy.Export.Category,
        Action = action,
        ActorType = AuditActorType.Service,
        ActorId = ExportWorkerActor,
        ActorDisplay = "Export worker",
        OnBehalfOf = export.CreatedBy,
        ResourceType = AuditTaxonomy.Export.ResourceType,
        ResourceId = export.ExportId.ToString(),
        Outcome = AuditOutcome.Success,
        JobId = export.JobId,
        ChunkSequence = chunk?.Sequence,
        SnapshotId = export.SnapshotId,
        CorrelationId = chunk?.CorrelationId,
        Details = details,
    };

    private sealed record DocumentResult(ExportDocumentOutcome Outcome, List<NewExportFile> Files, string DatRow, string OptRows);

    /// <summary>Writes the objects of one chunk attempt under its lease-token prefix.</summary>
    private sealed class ChunkFiles(IObjectStore store, Guid ws, Guid exportId, Guid runId, int sequence, long leaseToken)
    {
        public async Task<NewExportFile> CopyAsync(
            ExportSourceObject source, string name, ExportFileKind kind, string path, long ordinal, string? transcodeTo,
            CancellationToken cancellationToken)
        {
            var sourceKey = ObjectKey.Parse(source.ObjectKey);
            var expected = Sha256Digest.FromBytes(source.Sha256);
            var key = ExportLayout.ChunkObjectKey(ws, exportId, runId, sequence, leaseToken, name);
            var input = await store.OpenReadAsync(sourceKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                // Chain of custody (ADR-011 §2.5): the bytes must still hash to the registered SHA-256.
                Stream content = new VerifyingReadStream(input, sourceKey, expected, source.SizeBytes);
                PutObjectResult result;
                if (transcodeTo is null)
                {
                    result = await store.PutAsync(key, content, new PutObjectOptions
                    {
                        ContentType = "application/octet-stream",
                        ExpectedSha256 = expected,
                        ExpectedLength = source.SizeBytes,
                    }, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Stored text is UTF-8; a UTF-16LE text file starts with its byte-order mark.
                    var transcoded = Encoding.CreateTranscodingStream(content, Encoding.UTF8, new UnicodeEncoding(false, false), leaveOpen: true);
                    await using (transcoded.ConfigureAwait(false))
                    {
                        var withBom = new PrefixedStream([0xFF, 0xFE], transcoded);
                        result = await store.PutAsync(key, withBom, new PutObjectOptions { ContentType = "application/octet-stream" }, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                return File(result, kind, path, ordinal);
            }
        }

        public async Task<NewExportFile> WriteTextAsync(string name, byte[] bytes, ExportFileKind kind, string path, CancellationToken cancellationToken)
        {
            var key = ExportLayout.ChunkObjectKey(ws, exportId, runId, sequence, leaseToken, name);
            var result = await store.PutAsync(key, new MemoryStream(bytes, writable: false), new PutObjectOptions
            {
                ContentType = "application/octet-stream",
                ExpectedLength = bytes.Length,
            }, cancellationToken).ConfigureAwait(false);
            return File(result, kind, path, null);
        }

        private static NewExportFile File(PutObjectResult result, ExportFileKind kind, string path, long? ordinal) => new(
            path,
            kind,
            result.Key.Value,
            result.Sha256.ToBytes(),
            result.Length,
            "application/octet-stream",
            result.KeyId,
            result.EncryptionScheme == EncryptionScheme.Envelope ? CoreScheme.Envelope : CoreScheme.ProviderSse,
            ordinal);
    }
}

/// <summary>Builds the principal a worker acts as for an export's initiator (ADR-015 D9.4: from PostgreSQL, never a message).</summary>
internal static class ExportPrincipal
{
    public static async Task<SecurityPrincipal> ResolveAsync(IExportStore exports, ExportRecord export, CancellationToken cancellationToken)
    {
        var current = await exports.ReadInitiatorAsync(export.CreatedBy, cancellationToken).ConfigureAwait(false);
        return new SecurityPrincipal
        {
            UserId = export.CreatedBy,
            DisplayName = current?.DisplayName ?? export.CreatedByDisplay,
            Groups = current?.Groups ?? export.CreatedByGroups,
        };
    }
}
