using System.Diagnostics;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Telemetry;

namespace Opportunity.Data.Audit;

/// <summary>
/// <see cref="IAuditEventWriter"/> on <c>audit.audit_event</c>: one transaction per event, committed before this
/// returns (ADR-013 §2.2/§2.3). Workspace events go through the workspace's context, installation-level events
/// (WorkspaceId null) through an installation transaction.
/// </summary>
public sealed class PostgresAuditEventWriter : IAuditEventWriter
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;
    private readonly OpportunityMetrics? _metrics;

    public PostgresAuditEventWriter(NpgsqlDataSource dataSource, OpportunityMetrics? metrics = null)
        : this(new Lazy<NpgsqlDataSource>(dataSource), metrics)
    {
    }

    /// <summary>
    /// Resolves the data source on first write, so hosts that construct the authentication pipeline without a database
    /// (health probes, OpenAPI generation) start; a write without one still fails, and so does the audited action.
    /// </summary>
    public PostgresAuditEventWriter(Lazy<NpgsqlDataSource> dataSource, OpportunityMetrics? metrics = null)
    {
        _dataSource = dataSource;
        _metrics = metrics;
    }

    public async ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        AuditEventRules.EnsureValid(AuditEventRules.Normalize(auditEvent));
        var started = Stopwatch.GetTimestamp();
        var outcome = "error";
        try
        {
            await using var tx = await AuditTransaction.BeginAsync(_dataSource.Value, auditEvent.WorkspaceId, cancellationToken).ConfigureAwait(false);
            await AuditSql.InsertAsync(tx, auditEvent, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            outcome = "success";
        }
        finally
        {
            _metrics?.Histogram(OpportunityMetricCatalog.AuditWriteDuration)
                .Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>(TelemetryAttributes.Outcome, outcome));
        }
    }
}

/// <summary><see cref="IAuditEventReader"/> on <c>audit.audit_event</c>; each query writes its <c>Audit.Queried</c> event.</summary>
/// <remarks>The queriedBy event is a template (actor, client, correlation); each query gets a new event ID.</remarks>
public sealed class PostgresAuditEventReader(NpgsqlDataSource dataSource) : IAuditEventReader
{
    public async Task<AuditEventPage> QueryAsync(AuditQuery query, AuditEvent queriedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(queriedBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, AuditQuery.MaxLimit);

        var filters = new Dictionary<string, string?>
        {
            ["From"] = query.From?.ToString("O"),
            ["To"] = query.To?.ToString("O"),
            ["Category"] = query.Category,
            ["Action"] = query.Action,
            ["ActorId"] = query.ActorId,
            ["ResourceType"] = query.ResourceType,
            ["ResourceId"] = query.ResourceId,
            ["CorrelationId"] = query.CorrelationId,
            ["IncludeRestrictedDetails"] = query.IncludeRestrictedDetails ? "true" : "false",
            ["Limit"] = query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await using var tx = await AuditTransaction.BeginAsync(dataSource, query.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await AuditSql.InsertAsync(tx, queriedBy with
        {
            EventId = Guid.CreateVersion7(),
            WorkspaceId = query.WorkspaceId,
            Category = AuditTaxonomy.Audit.Category,
            Action = AuditTaxonomy.Audit.Queried,
            ResourceType = query.WorkspaceId is null ? "Installation" : "Workspace",
            ResourceId = query.WorkspaceId?.ToString(),
            Details = filters.Where(f => f.Value is not null).ToDictionary(),
            RestrictedDetails = null,
        }, cancellationToken).ConfigureAwait(false);
        var page = await AuditSql.QueryAsync(tx, query, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }
}

internal static class AuditTransaction
{
    public static Task<WorkspaceTransaction> BeginAsync(NpgsqlDataSource dataSource, Guid? workspaceId, CancellationToken cancellationToken) =>
        workspaceId is { } ws
            ? WorkspaceTransaction.BeginAsync(dataSource, ws, cancellationToken)
            : WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken);
}
