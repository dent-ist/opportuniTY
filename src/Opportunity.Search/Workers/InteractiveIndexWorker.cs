using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;

#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Application.Audit;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.SearchWork;
using Opportunity.Core.Workspaces;
using Opportunity.Search.Projection;
using Opportunity.Search.Writing;

namespace Opportunity.Search.Workers;

/// <summary>Settings of the interactive index worker, section <c>Search:Interactive</c>.</summary>
public sealed class InteractiveIndexWorkerOptions
{
    public const string SectionName = "Search:Interactive";

    /// <summary>ADR-001 §4 R3: a write built from an older PostgreSQL read is never sent (≪ <c>gc_deletes</c> = 10 min).</summary>
    public TimeSpan MaxReadToWriteAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Reads attempted when each one ages past <see cref="MaxReadToWriteAge"/> before it could be written.</summary>
    public int MaxReads { get; set; } = 3;

    public void Validate()
    {
        if (MaxReadToWriteAge <= TimeSpan.Zero || MaxReadToWriteAge > TimeSpan.FromMinutes(5) || MaxReads < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}: MaxReadToWriteAge must be positive and at most 5 minutes (gc_deletes is 10 minutes), MaxReads at least 1.");
        }
    }
}

/// <summary>
/// The version-safe interactive index worker (E07-T03, ADR-001 §3–§5): consumes SearchOutbox messages from the security
/// (L0) and interactive (L1) lanes — each lane is its own queue with its own consumers, so security work never waits
/// behind coding — and makes the index reflect the document's <em>current</em> PostgreSQL state:
/// <list type="number">
/// <item>The PostgreSQL row is the authority (the envelope's workspace is a hint, ADR-015 D9): a row invisible under RLS
/// with the hinted workspace, or one of another document, is rejected (E05-T07: <c>Integrity.MessageRejected</c> audit
/// event, dead-lettered, nothing written) unless the hinted workspace is being closed or deleted (fenced, dropped); an
/// Applied row is a duplicate (acked, nothing written).</item>
/// <item>A workspace that is not Active is fenced (ADR-001 §4 R5): the work is dropped.</item>
/// <item>Current state and DocumentVersion are read in one snapshot (<see cref="IProjectionService"/>) and written with
/// external versioning through the shared <see cref="IProjectionIndexWriter"/>; a missing row becomes an unconditional
/// delete, a soft-deleted one an external delete (§4 R1). The message's version is never what is written. A read older
/// than <see cref="InteractiveIndexWorkerOptions.MaxReadToWriteAge"/> is never sent: it is read again (§4 R3).</item>
/// <item>Applied or 409 (a newer or equal version is already there, counted on
/// <c>opportunity.search.stale_version_rejections</c>) → every outbox row of the document up to the version written is
/// marked Applied in one statement (coalescing, §5.2).</item>
/// <item>Transient failures return the row to Pending with backoff, permanent ones mark it Failed (§6.4); the message is
/// acked either way. Only when PostgreSQL itself cannot record the outcome does the handler throw (transport retry).</item>
/// </list>
/// Redelivery is idempotent by construction: a repeated message rewrites the current version (a 409 no-op) or finds the
/// row Applied. Ordering is neither needed nor assumed (§5.1).
/// </summary>
public sealed partial class InteractiveIndexWorker(
    ISearchOutboxRepository outbox,
    IWorkspaceReader workspaces,
    IProjectionService projections,
    IProjectionIndexWriter writer,
    InteractiveIndexWorkerOptions options,
    TimeProvider time,
    ILogger<InteractiveIndexWorker> logger,
    IAuditEventWriter audit,
    OpportunityMetrics? metrics = null
#if OPPORTUNITY_FAILPOINTS
    , IFaultInjector? faults = null
#endif
    ) : IMessageHandler<SearchOutboxMessage>
{
    private static readonly string WorkerId = string.Create(
        System.Globalization.CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}");

    private readonly Counter<long>? _staleRejections = metrics?.Counter(OpportunityMetricCatalog.SearchStaleVersionRejections);

    public async Task HandleAsync(SearchOutboxMessage payload, ReceivedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(message);
        var workspaceId = message.Envelope.WorkspaceId
            ?? throw new PermanentMessageException("A SearchOutbox message must name its workspace.");

        var row = await outbox.GetAsync(workspaceId, payload.OutboxId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            if (await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false) is { Status: not WorkspaceStatus.Active })
            {
                // Work of a workspace being closed or deleted: fenced like below, not a forgery.
                LogFenced(logger, payload.OutboxId, workspaceId);
                return;
            }

            LogUnknownRow(logger, payload.OutboxId, workspaceId);
            throw await RejectAsync(payload, message, "workspaceId", "No such SearchOutbox row in the hinted workspace.").ConfigureAwait(false);
        }

        if (row.DocumentId != payload.DocumentId)
        {
            throw await RejectAsync(payload, message, "documentId", "The SearchOutbox row belongs to another document.").ConfigureAwait(false);
        }

        if (row.Status == SearchOutboxStatus.Applied)
        {
            LogDuplicate(logger, row.OutboxId, workspaceId);
            return;
        }

        if (await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false) is not { Status: WorkspaceStatus.Active })
        {
            LogFenced(logger, row.OutboxId, workspaceId);
            return;
        }

        ProjectionWriteResult? result = null;
        for (var read = 1; read <= options.MaxReads && result is null; read++)
        {
            var readAt = time.GetTimestamp();
            var documents = await projections.BuildAsync(workspaceId, [row.DocumentId], cancellationToken).ConfigureAwait(false);
            if (time.GetElapsedTime(readAt) > options.MaxReadToWriteAge)
            {
                continue;
            }

#if OPPORTUNITY_FAILPOINTS
            await HitAsync(Failpoints.OutboxBeforeBulk, message, workspaceId, row.OutboxId, read, cancellationToken).ConfigureAwait(false);
#endif
            result = (await writer.WriteAsync(workspaceId, documents, cancellationToken).ConfigureAwait(false)).Documents.Single();
#if OPPORTUNITY_FAILPOINTS
            await HitAsync(Failpoints.OutboxAfterBulk, message, workspaceId, row.OutboxId, read, cancellationToken).ConfigureAwait(false);
#endif
        }

        if (result is null)
        {
            const string expired = "Every read aged past the read-to-write bound before it could be written.";
            LogTransient(logger, row.OutboxId, workspaceId, expired);
            await outbox.ReturnUnappliedAsync(workspaceId, row.OutboxId, expired, permanent: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (result.Status)
        {
            case ProjectionWriteStatus.Applied or ProjectionWriteStatus.StaleNoOp:
                if (result.Status == ProjectionWriteStatus.StaleNoOp)
                {
                    _staleRejections?.Add(1, new KeyValuePair<string, object?>(TelemetryAttributes.Lane, DispatchMetrics.LaneName(row.Lane)));
                }

                // A purged document (no version) can never come back: every row of it is done.
                await outbox.MarkAppliedThroughAsync(workspaceId, row.DocumentId, result.Version ?? long.MaxValue, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ProjectionWriteStatus.Permanent:
                LogPermanent(logger, row.OutboxId, workspaceId, result.Error);
                await outbox.ReturnUnappliedAsync(workspaceId, row.OutboxId, result.Error ?? "Rejected by OpenSearch.", permanent: true, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                var reason = result.Error ?? "OpenSearch write failed.";
                LogTransient(logger, row.OutboxId, workspaceId, reason);
                await outbox.ReturnUnappliedAsync(workspaceId, row.OutboxId, reason, permanent: false, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>ADR-015 D9.3: installation-level audit event; the returned exception dead-letters the message.</summary>
    private async Task<PermanentMessageException> RejectAsync(SearchOutboxMessage payload, ReceivedMessage message, string field, string detail)
    {
        await audit.WriteAsync(
            MessageRejection.AuditEvent(
                message.Envelope,
                message.Queue,
                MessageRejectionReasons.EnvelopeMismatch,
                WorkerId,
                "SearchOutbox",
                payload.OutboxId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                time.GetUtcNow(),
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["mismatch"] = field, ["claimedDocumentId"] = payload.DocumentId.ToString() }),
            CancellationToken.None).ConfigureAwait(false);
        return new PermanentMessageException($"{MessageRejectionReasons.EnvelopeMismatch} ({field}): {detail}");
    }

#if OPPORTUNITY_FAILPOINTS
    private ValueTask HitAsync(string failpoint, ReceivedMessage message, Guid workspaceId, long outboxId, int sequence, CancellationToken cancellationToken) =>
        faults?.HitAsync(failpoint, new FailpointContext(message, null)
        {
            WorkspaceId = workspaceId,
            Subject = outboxId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sequence = sequence,
        }, cancellationToken) ?? ValueTask.CompletedTask;
#endif

    [LoggerMessage(Level = LogLevel.Warning, Message = "SearchOutbox row {OutboxId} does not exist in workspace {WorkspaceId}; rejecting the message")]
    private static partial void LogUnknownRow(ILogger logger, long outboxId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SearchOutbox row {OutboxId} of workspace {WorkspaceId} is already applied; duplicate dropped")]
    private static partial void LogDuplicate(ILogger logger, long outboxId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} is not active; SearchOutbox row {OutboxId} dropped")]
    private static partial void LogFenced(ILogger logger, long outboxId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Indexing SearchOutbox row {OutboxId} of workspace {WorkspaceId} failed and is retried: {Reason}")]
    private static partial void LogTransient(ILogger logger, long outboxId, Guid workspaceId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "OpenSearch rejected SearchOutbox row {OutboxId} of workspace {WorkspaceId} for good: {Reason}")]
    private static partial void LogPermanent(ILogger logger, long outboxId, Guid workspaceId, string? reason);
}
