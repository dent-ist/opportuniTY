using System.Buffers;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;

namespace Opportunity.Search.Writing;

/// <summary>
/// Bounds of one OpenSearch <c>_bulk</c> sub-request (ADR-010 §6): at most <see cref="MaxRequestActions"/> actions and
/// <see cref="MaxRequestBytes"/> body bytes; one action larger than that is sent alone, and one larger than
/// <see cref="MaxActionBytes"/> is never sent (a permanent item failure, below <c>http.max_content_length</c>).
/// </summary>
public sealed class ProjectionWriterOptions
{
    public const string SectionName = "Search:Bulk";

    public long MaxRequestBytes { get; set; } = 10L * 1024 * 1024;

    public int MaxRequestActions { get; set; } = 500;

    public long MaxActionBytes { get; set; } = 90L * 1024 * 1024;

    public void Validate()
    {
        if (MaxRequestBytes < 1024 || MaxRequestActions < 1 || MaxActionBytes < MaxRequestBytes)
        {
            throw new InvalidOperationException(
                $"{SectionName}: MaxRequestBytes must be at least 1 KiB, MaxRequestActions at least 1 and MaxActionBytes at least MaxRequestBytes.");
        }
    }
}

/// <summary>What happened to one document's projection writes (ADR-001 §3 response handling).</summary>
public enum ProjectionWriteStatus
{
    /// <summary>Every write was accepted (or a delete found nothing to delete).</summary>
    Applied,

    /// <summary><c>409 version_conflict</c>: an equal or newer version is already indexed. A success no-op.</summary>
    StaleNoOp,

    /// <summary>429, 5xx, a lost connection or a placement race: retry after re-reading PostgreSQL (ADR-001 §4 R3).</summary>
    Transient,

    /// <summary>e.g. <c>mapper_parsing_exception</c> or an oversized projection: retrying cannot fix it.</summary>
    Permanent,
}

/// <param name="Version">The DocumentVersion written (null for an unconditional delete).</param>
/// <param name="Error">OpenSearch error type and reason of the worst failed write; null on success.</param>
public sealed record ProjectionWriteResult(Guid DocumentId, long? Version, ProjectionWriteStatus Status, string? Error)
{
    public bool Succeeded => Status is ProjectionWriteStatus.Applied or ProjectionWriteStatus.StaleNoOp;
}

/// <param name="Documents">One result per input document, in input order.</param>
/// <param name="Throttled">OpenSearch answered 429 to a request or an item: callers back off before retrying.</param>
/// <param name="RequestBytes">Body size of every <c>_bulk</c> request sent, in order.</param>
public sealed record ProjectionWriteReport(IReadOnlyList<ProjectionWriteResult> Documents, bool Throttled, IReadOnlyList<long> RequestBytes);

/// <summary>Which write targets of the placement a write goes to.</summary>
public enum ProjectionWriteScope
{
    /// <summary>Every write target: the current index and, while a rebuild runs, its target (dual-target, ADR-006 R12).</summary>
    AllTargets,

    /// <summary>
    /// Only the target of a running rebuild: the reindex backfill (ADR-001 §7.5). Without a running rebuild nothing is
    /// written and every document reports <see cref="ProjectionWriteStatus.Applied"/> (the rebuild it was for has ended).
    /// </summary>
    RebuildTarget,
}

/// <summary>
/// The one OpenSearch write path of the index workers (ADR-001 R1): sends <see cref="ProjectionDocument"/> writes to
/// every write target of the workspace's placement (two while a rebuild dual-writes) in byte-bounded <c>_bulk</c>
/// sub-requests with <c>version_type=external</c>. It never retries: it classifies every item, so the caller re-reads
/// PostgreSQL for exactly the documents that failed transiently. Shared by the chunk (E07-T04) and interactive
/// (E07-T03) index workers; each keeps its own consumer, leases and retry policy. A projection goes to the targets of its
/// own generation (the mapping it conforms to) when the placement has one; while a generation switch dual-writes, the
/// older-generation index is not written (its strict mapping may reject the new body) and serves its last state until
/// the alias switch.
/// </summary>
public interface IProjectionIndexWriter
{
    ProjectionWriterOptions Options { get; }

    Task<ProjectionWriteReport> WriteAsync(Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, CancellationToken cancellationToken = default);

    Task<ProjectionWriteReport> WriteAsync(
        Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, ProjectionWriteScope scope, CancellationToken cancellationToken = default);
}

internal sealed class ProjectionIndexWriter(
    IIndexManager indexes, OpenSearchConnection connection, ProjectionWriterOptions options
#if OPPORTUNITY_FAILPOINTS
    , IFaultInjector? faults = null
#endif
    )
    : IProjectionIndexWriter
{
    // Only what classification needs, so a 500-item response stays small.
    private const string BulkPath = "_bulk?filter_path=errors,items.*.status,items.*.result,items.*.error.type,items.*.error.reason";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public ProjectionWriterOptions Options => options;

    public Task<ProjectionWriteReport> WriteAsync(
        Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, CancellationToken cancellationToken = default) =>
        WriteAsync(workspaceId, documents, ProjectionWriteScope.AllTargets, cancellationToken);

    public async Task<ProjectionWriteReport> WriteAsync(
        Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, ProjectionWriteScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var outcomes = new Outcome[documents.Count];
        if (documents.Count == 0)
        {
            return new ProjectionWriteReport([], false, []);
        }

        foreach (var document in documents)
        {
            if (document.WorkspaceId != workspaceId)
            {
                throw new ArgumentException("Every projection must belong to the workspace written.", nameof(documents));
            }
        }

        var placement = await indexes.ResolveAsync(workspaceId, IndexPurpose.Write, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<IndexTarget> targets = scope == ProjectionWriteScope.RebuildTarget
            ? placement.RebuildTarget is { } rebuild ? [rebuild] : []
            : placement.WriteTargets;
        if (targets.Count == 0)
        {
            return new ProjectionWriteReport(
                [.. documents.Select(d => new ProjectionWriteResult(d.DocumentId, d.Version, ProjectionWriteStatus.Applied, null))], false, []);
        }

        var actions = Serialize(documents, targets, outcomes);

        var throttled = false;
        var sizes = new List<long>();
        var batch = new List<BulkAction>();
        long batchBytes = 0;
        foreach (var action in actions)
        {
            if (batch.Count > 0 && (batchBytes + action.Bytes > options.MaxRequestBytes || batch.Count >= options.MaxRequestActions))
            {
                throttled |= await SendAsync(batch, batchBytes, outcomes, sizes, cancellationToken).ConfigureAwait(false);
                batch.Clear();
                batchBytes = 0;
            }

            batch.Add(action);
            batchBytes += action.Bytes;
        }

        if (batch.Count > 0)
        {
            throttled |= await SendAsync(batch, batchBytes, outcomes, sizes, cancellationToken).ConfigureAwait(false);
        }

        var results = new ProjectionWriteResult[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            var outcome = outcomes[i];
            results[i] = new ProjectionWriteResult(documents[i].DocumentId, documents[i].Version, outcome.Status, outcome.Error);
        }

        return new ProjectionWriteReport(results, throttled, sizes);
    }

    /// <summary>One action per write and target; an oversized write fails its document instead of being queued.</summary>
    private List<BulkAction> Serialize(IReadOnlyList<ProjectionDocument> documents, IReadOnlyList<IndexTarget> targets, Outcome[] outcomes)
    {
        var actions = new List<BulkAction>();
#if OPPORTUNITY_FAILPOINTS
        // Test builds only (E17-T07): a deliberately unversioned write the shadow-ledger oracle must catch.
        var versioned = faults?.IsArmed(FaultFlags.UnversionedProjectionWrite) != true;
#else
        const bool versioned = true;
#endif
        for (var i = 0; i < documents.Count; i++)
        {
            var pending = new List<BulkAction>();
            var generation = documents[i].Generation;
            var ownGeneration = targets.Where(t => t.Generation == generation).ToList();
            var documentTargets = ownGeneration.Count > 0 ? ownGeneration : targets;
            foreach (var write in documents[i].Writes)
            {
                var body = write.Kind == ProjectionWriteKind.Index
                    ? Line(write.Body ?? throw new ArgumentException($"Index write {write.Id} has no body.", nameof(documents)))
                    : null;
                foreach (var target in documentTargets)
                {
                    var metadata = Metadata(write, target, versioned);
                    pending.Add(new BulkAction(i, write.Kind, metadata, body, metadata.Length + (body?.Length ?? 0)));
                }
            }

            if (pending.Find(a => a.Bytes > options.MaxActionBytes) is { } oversized)
            {
                outcomes[i] = outcomes[i].Merge(ProjectionWriteStatus.Permanent,
                    $"projection_too_large: {oversized.Bytes} bytes exceed the {options.MaxActionBytes}-byte limit of one bulk action");
                continue;
            }

            actions.AddRange(pending);
        }

        return actions;
    }

    /// <returns>Whether OpenSearch throttled (429).</returns>
    private async Task<bool> SendAsync(List<BulkAction> batch, long bytes, Outcome[] outcomes, List<long> sizes, CancellationToken cancellationToken)
    {
        var lines = new List<byte[]>(batch.Count * 2);
        foreach (var action in batch)
        {
            lines.Add(action.Metadata);
            if (action.Body is not null)
            {
                lines.Add(action.Body);
            }
        }

        sizes.Add(bytes);
        OpenSearchResponse response;
        try
        {
            response = await connection.SendNdjsonAsync(BulkPath, lines, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Applied or not is unknown; a version-guarded retry is harmless either way.
            MarkAll(batch, outcomes, ProjectionWriteStatus.Transient, $"bulk_request_failed: {ex.GetType().Name}");
            return false;
        }

        var status = (int)response.Status;
        if (response.Status == HttpStatusCode.TooManyRequests || status >= 500)
        {
            MarkAll(batch, outcomes, ProjectionWriteStatus.Transient, $"bulk_rejected: {status} {OpenSearchConnection.ErrorType(response.Body)}");
            return response.Status == HttpStatusCode.TooManyRequests;
        }

        if (status is < 200 or >= 300 || response.Body?["items"] is not JsonArray items || items.Count != batch.Count)
        {
            MarkAll(batch, outcomes, ProjectionWriteStatus.Permanent, $"bulk_failed: {status} {OpenSearchConnection.ErrorType(response.Body)}");
            return false;
        }

        var throttled = false;
        for (var k = 0; k < batch.Count; k++)
        {
            var action = batch[k];
            var item = items[k] is JsonObject wrapper && wrapper.Count == 1 ? wrapper.First().Value as JsonObject : null;
            var (itemStatus, error) = Classify(action, item);
            throttled |= item?["status"]?.GetValue<int>() == 429;
            outcomes[action.Document] = outcomes[action.Document].Merge(itemStatus, error);
        }

        return throttled;
    }

    private static (ProjectionWriteStatus Status, string? Error) Classify(BulkAction action, JsonObject? item)
    {
        if (item?["status"]?.GetValue<int>() is not { } status)
        {
            return (ProjectionWriteStatus.Permanent, "bulk_item_unreadable");
        }

        var errorType = item["error"]?["type"]?.GetValue<string>();
        var error = errorType is null ? null : $"{errorType}: {item["error"]?["reason"]?.GetValue<string>()}";
        return status switch
        {
            >= 200 and < 300 => (ProjectionWriteStatus.Applied, null),
            409 => (ProjectionWriteStatus.StaleNoOp, null),

            // A delete of a document that is not there leaves the index as wanted (and records the tombstone).
            404 when action.Kind != ProjectionWriteKind.Index && errorType is null => (ProjectionWriteStatus.Applied, null),

            // The target index disappeared under a placement change: re-resolve and retry.
            404 => (ProjectionWriteStatus.Transient, error ?? "not_found"),

            // A retired generation is write-blocked (E07-T11); a writer whose cached placement still names it re-resolves.
            403 when errorType == "cluster_block_exception" => (ProjectionWriteStatus.Transient, error),
            429 or >= 500 => (ProjectionWriteStatus.Transient, error ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            _ => (ProjectionWriteStatus.Permanent, error ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    private static void MarkAll(List<BulkAction> batch, Outcome[] outcomes, ProjectionWriteStatus status, string error)
    {
        foreach (var action in batch)
        {
            outcomes[action.Document] = outcomes[action.Document].Merge(status, error);
        }
    }

    private static byte[] Metadata(ProjectionWrite write, IndexTarget target, bool versioned)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(write.Kind == ProjectionWriteKind.Index ? "index" : "delete");
            writer.WriteString("_index", target.Index);
            writer.WriteString("_id", write.Id);
            if (target.Routing is { } routing)
            {
                writer.WriteString("routing", routing);
            }

            if (write.Kind != ProjectionWriteKind.DeleteUnconditional && versioned)
            {
                writer.WriteNumber("version", write.Version ?? throw new ArgumentException($"Write {write.Id} carries no version."));
                writer.WriteString("version_type", "external");
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        buffer.Write(NewLine);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Line(JsonObject body)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            body.WriteTo(writer);
        }

        buffer.Write(NewLine);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed record BulkAction(int Document, ProjectionWriteKind Kind, byte[] Metadata, byte[]? Body, long Bytes);

    /// <summary>The worst status over a document's writes and targets: Permanent, then Transient, then success.</summary>
    private readonly record struct Outcome(ProjectionWriteStatus? Worst, string? Error)
    {
        public ProjectionWriteStatus Status => Worst ?? ProjectionWriteStatus.Applied;

        public Outcome Merge(ProjectionWriteStatus status, string? error)
        {
            if (Worst is null || Rank(status) > Rank(Worst.Value))
            {
                return new Outcome(status, error ?? Error);
            }

            return this;
        }

        // Applied beats StaleNoOp: a document is a no-op only if no write of it was applied.
        private static int Rank(ProjectionWriteStatus status) => status switch
        {
            ProjectionWriteStatus.StaleNoOp => 0,
            ProjectionWriteStatus.Applied => 1,
            ProjectionWriteStatus.Transient => 2,
            _ => 3,
        };
    }
}
