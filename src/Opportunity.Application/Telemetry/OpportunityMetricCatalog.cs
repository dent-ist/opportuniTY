namespace Opportunity.Application.Telemetry;

public enum MetricKind
{
    Counter,
    UpDownCounter,
    Histogram,
    Gauge,
}

/// <summary>
/// One metric instrument: OpenTelemetry name and UCUM unit (exported to Prometheus as
/// <c>name_with_underscores_{unit}</c>, e.g. <c>opportunity_search_index_lag_seconds</c>), the only attribute keys it
/// may carry, and the plan ticket that records it.
/// </summary>
public sealed record MetricDefinition(
    string Name,
    MetricKind Kind,
    string Unit,
    string Description,
    IReadOnlyList<string> Attributes,
    string RecordedBy,
    string Meter = OpportunityTelemetry.MeterName,
    IReadOnlyList<double>? Buckets = null);

/// <summary>
/// The application metric catalog (ADR-017 §5). Instruments are defined here once, before the epics that record
/// them exist, so names, units and attribute sets are reviewed together and dashboards/alerts (E19-T05) can be
/// written against them. Workspace-valued attributes are bounded (<see cref="BoundedAttributeValues"/>).
/// </summary>
public static class OpportunityMetricCatalog
{
    /// <summary>Lag/latency buckets in seconds: covers the 1 s interactive, 5 s security (Q-10) and 2 min bulk targets.</summary>
    public static IReadOnlyList<double> LagBuckets { get; } =
        [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600];

    /// <summary>Request/processing duration buckets in seconds (§17: simple search &lt; 1 s, complex &lt; 3 s).</summary>
    public static IReadOnlyList<double> DurationBuckets { get; } =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300];

    // --- Workers (recorded today) ----------------------------------------------------------------------------------

    public static MetricDefinition WorkerHeartbeatAge { get; } = new(
        "opportunity.worker.heartbeat.age", MetricKind.Gauge, "s",
        "Seconds since the worker type last reported a heartbeat.",
        [TelemetryAttributes.WorkerType], "E01-T02", Meter: "Opportunity.Workers");

    public static MetricDefinition WorkerLastConsumedAge { get; } = new(
        "opportunity.worker.last_consumed.age", MetricKind.Gauge, "s",
        "Seconds since the worker type last consumed a message.",
        [TelemetryAttributes.WorkerType], "E01-T02", Meter: "Opportunity.Workers");

    public static MetricDefinition MessagingProcessDuration { get; } = new(
        "messaging.process.duration", MetricKind.Histogram, "s",
        "Duration of handling one message in a worker (OpenTelemetry messaging semantic convention).",
        [TelemetryAttributes.MessagingDestinationName, TelemetryAttributes.MessageType, TelemetryAttributes.WorkerType, TelemetryAttributes.ErrorType],
        "E19-T04", Buckets: DurationBuckets);

    public static MetricDefinition MessagingConsumedMessages { get; } = new(
        "messaging.client.consumed.messages", MetricKind.Counter, "{message}",
        "Messages handed to a worker consumer (OpenTelemetry messaging semantic convention).",
        [TelemetryAttributes.MessagingDestinationName, TelemetryAttributes.MessageType, TelemetryAttributes.WorkerType, TelemetryAttributes.ErrorType],
        "E19-T04");

    // --- Outbox and broker (ADR-001 §6, ADR-010 §7) -----------------------------------------------------------------

    public static MetricDefinition OutboxPending { get; } = new(
        "opportunity.outbox.pending", MetricKind.Gauge, "{record}",
        "SearchOutbox rows not yet Applied, by lane.",
        [TelemetryAttributes.Lane], "E06-T04");

    public static MetricDefinition OutboxOldestAge { get; } = new(
        "opportunity.outbox.oldest_age", MetricKind.Gauge, "s",
        "Age of the oldest SearchOutbox row not yet dispatched, by lane (alert > 60 s).",
        [TelemetryAttributes.Lane], "E06-T04");

    public static MetricDefinition QueueDepth { get; } = new(
        "opportunity.queue.depth", MetricKind.Gauge, "{message}",
        "Messages in a queue by state (ready, unacked, dlq); DLQ depth > 0 alerts (ADR-010 §7.6).",
        [TelemetryAttributes.MessagingDestinationName, TelemetryAttributes.QueueState], "E06-T01");

    public static MetricDefinition QueueConsumers { get; } = new(
        "opportunity.queue.consumers", MetricKind.Gauge, "{consumer}",
        "Consumers attached to a queue; 0 on a lane queue is an outage.",
        [TelemetryAttributes.MessagingDestinationName], "E06-T01");

    public static MetricDefinition DeadLetteredMessages { get; } = new(
        "opportunity.dlq.messages", MetricKind.Counter, "{message}",
        "Messages recorded by the dead-letter recorder.",
        [TelemetryAttributes.MessagingDestinationName, TelemetryAttributes.ErrorType], "E06-T06");

    // --- Search projection, generation and watermark (ADR-001 §5/§7, baseline §28) ----------------------------------

    public static MetricDefinition SearchGenerationCommitted { get; } = new(
        "opportunity.search.generation.committed", MetricKind.Gauge, "{generation}",
        "Latest committed SearchGeneration per workspace (bounded: top workspaces + other).",
        [TelemetryAttributes.WorkspaceId], "E07-T08");

    public static MetricDefinition SearchGenerationIndexed { get; } = new(
        "opportunity.search.generation.indexed", MetricKind.Gauge, "{generation}",
        "Visible watermark indexedThroughGeneration per workspace (refresh-aware, never moves backwards).",
        [TelemetryAttributes.WorkspaceId], "E07-T08");

    public static MetricDefinition SearchIndexLag { get; } = new(
        "opportunity.search.index_lag", MetricKind.Gauge, "s",
        "now - CommittedAt of the oldest work record above the watermark (ADR-001 §7.4 search.index_lag_seconds).",
        [TelemetryAttributes.WorkspaceId], "E07-T08");

    public static MetricDefinition SearchCommitToSearchable { get; } = new(
        "opportunity.search.commit_to_searchable", MetricKind.Histogram, "s",
        "Commit to first refresh observation, per work record, by lane (interactive p95 < 1 s, bulk < 2 min).",
        [TelemetryAttributes.Lane], "E07-T06", Buckets: LagBuckets);

    public static MetricDefinition SecurityProjectionLag { get; } = new(
        "opportunity.search.security_projection_lag", MetricKind.Histogram, "s",
        "Security-lane lag: first refresh observation after AppliedAt - CommittedAt (Q-10 SLO <= 5 s p95; ADR-001 §5.4 security_projection_lag_seconds).",
        [TelemetryAttributes.Lane], "E05-T06", Buckets: LagBuckets);

    public static MetricDefinition SearchStaleVersionRejections { get; } = new(
        "opportunity.search.stale_version_rejections", MetricKind.Counter, "{document}",
        "OpenSearch external-version conflicts treated as applied no-ops (ADR-001 §3).",
        [TelemetryAttributes.Lane], "E07-T06");

    public static MetricDefinition IndexChunkTasks { get; } = new(
        "opportunity.index.chunk_tasks", MetricKind.Gauge, "{task}",
        "IndexChunkTask backlog by status and lane.",
        [TelemetryAttributes.Status, TelemetryAttributes.Lane], "E07-T06");

    public static MetricDefinition IndexChunkTaskOldestAge { get; } = new(
        "opportunity.index.chunk_task.oldest_age", MetricKind.Gauge, "s",
        "Age of the oldest IndexChunkTask per non-terminal status.",
        [TelemetryAttributes.Status, TelemetryAttributes.Lane], "E07-T06");

    public static MetricDefinition SearchRequestDuration { get; } = new(
        "opportunity.search.request.duration", MetricKind.Histogram, "s",
        "Search execution time by query class (§17: simple p95 < 1 s, complex p95 < 3 s).",
        ["opportunity.search.class", TelemetryAttributes.Outcome], "E07-T07", Buckets: DurationBuckets);

    // --- Jobs and chunks (ADR-010) ----------------------------------------------------------------------------------

    public static MetricDefinition Jobs { get; } = new(
        "opportunity.jobs", MetricKind.Counter, "{job}",
        "Job status transitions (created, completed, failed, cancelled, paused) by job type.",
        [TelemetryAttributes.JobType, TelemetryAttributes.Status], "E06-T05");

    public static MetricDefinition JobsActive { get; } = new(
        "opportunity.jobs.active", MetricKind.UpDownCounter, "{job}",
        "Jobs not in a terminal status, by job type.",
        [TelemetryAttributes.JobType], "E06-T05");

    public static MetricDefinition JobChunks { get; } = new(
        "opportunity.job.chunks", MetricKind.Counter, "{chunk}",
        "Chunk attempts by outcome (committed, retry, failed, skipped) and job type.",
        [TelemetryAttributes.JobType, TelemetryAttributes.Outcome, TelemetryAttributes.ErrorType], "E06-T05");

    public static MetricDefinition JobChunkDuration { get; } = new(
        "opportunity.job.chunk.duration", MetricKind.Histogram, "s",
        "Claim to commit duration of one chunk attempt, by job type and outcome.",
        [TelemetryAttributes.JobType, TelemetryAttributes.Outcome], "E06-T05", Buckets: DurationBuckets);

    // --- Audit (E14-T01) ------------------------------------------------------------------------------------------------

    /// <summary>Insert buckets in seconds around the ADR-013 §2.5 budget (≤ 2 ms p95 per insert).</summary>
    public static IReadOnlyList<double> AuditWriteBuckets { get; } =
        [0.0005, 0.001, 0.002, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1];

    public static MetricDefinition AuditWriteDuration { get; } = new(
        "opportunity.audit.write.duration", MetricKind.Histogram, "s",
        "Duration of one standalone audit write (insert and commit), by outcome; ADR-013 budget ≤ 2 ms p95.",
        [TelemetryAttributes.Outcome], "E14-T01", Buckets: AuditWriteBuckets);

    public static IReadOnlyList<MetricDefinition> All { get; } =
    [
        WorkerHeartbeatAge, WorkerLastConsumedAge, MessagingProcessDuration, MessagingConsumedMessages,
        OutboxPending, OutboxOldestAge, QueueDepth, QueueConsumers, DeadLetteredMessages,
        SearchGenerationCommitted, SearchGenerationIndexed, SearchIndexLag, SearchCommitToSearchable,
        SecurityProjectionLag, SearchStaleVersionRejections, IndexChunkTasks, IndexChunkTaskOldestAge,
        SearchRequestDuration, Jobs, JobsActive, JobChunks, JobChunkDuration, AuditWriteDuration,
    ];
}
