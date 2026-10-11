using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Snapshots;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Audit;

/// <summary>
/// The audit log viewer and audit exports (E14-T04, ADR-013 §8), under <c>/api/v1/workspaces/{workspaceId}</c>:
/// <list type="bullet">
/// <item><c>GET /audit-events</c>: the workspace's audit trail, newest first, filtered (date range, category and action,
/// actor, document, job, outcome; they combine) and cursor-paged; never all at once. Each page read is audited
/// (<c>Audit.Queried</c>).</item>
/// <item><c>GET /audit-actions</c>: the closed taxonomy, for the filters.</item>
/// <item><c>POST /audit-exports</c>: export the filtered trail to CSV as a job (202; <c>Audit.Exported</c>);
/// <c>GET /audit-exports</c> and <c>/audit-exports/{exportId}</c>: your exports. The file is a gateway endpoint
/// (<see cref="Content.AuditExportContentEndpoints"/>).</item>
/// </list>
/// Every route requires <c>Audit.Read</c> (Workspace Admin, Auditor; break-glass may read but not export, Q-45); search
/// text needs <c>Audit.ReadSearchText</c> as well (Q-16). Events about documents the caller may not see (walls,
/// restriction classes) are left out entirely.
/// </summary>
public sealed class AuditLogEndpoints : IApiEndpointModule
{
    public const string EventsPath = "/audit-events";
    public const string ExportsPath = "/audit-exports";

    private const string Tag = "Audit";
    private const string ExportNotFound = "No such audit export.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(EventsPath, ListEventsAsync)
            .WithName("ListAuditEvents")
            .WithTags(Tag)
            .WithSummary("The workspace's audit trail, newest first, filtered and cursor-paged (Audit.Read). Each page read is audited.")
            .WithDescription(
                "Filters combine: from/to (to exclusive), category and action (an action needs its category), actorId, controlNumber or " +
                "documentId (events about that document), jobId (events of that job), outcome. includeSearchText adds the executed " +
                "query text of search events and needs Audit.ReadSearchText. Events about documents you may not see are never listed.")
            .Produces<CursorPage<AuditEventResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.AuditRead);

        routes.Workspace.MapGet("/audit-actions", ListActions)
            .WithName("ListAuditActions")
            .WithTags(Tag)
            .WithSummary("The closed audit taxonomy: every category and action an event can have (Audit.Read).")
            .Produces<IReadOnlyList<AuditActionResource>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.AuditRead);

        var exports = routes.Workspace.MapGroup(ExportsPath).WithTags(Tag).RequirePermission(Permission.AuditRead);
        exports.MapPost(string.Empty, CreateExportAsync)
            .WithName("CreateAuditExport")
            .WithSummary("Export the filtered audit trail to CSV as a background job; 202 with the export. Audited (Audit.Exported).")
            .WithDescription(
                "Takes the filters of GET …/audit-events. The file holds the events that occurred before the request (or before 'to'), " +
                "oldest first, without events about documents you may not see. Only you can see the export and download its file. Refused under break-glass access, which is read-only.")
            .RequireIdempotencyKey()
            .Produces<AuditExportResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        exports.MapGet(string.Empty, ListExportsAsync)
            .WithName("ListAuditExports")
            .WithSummary("Your audit exports, newest first.")
            .Produces<CursorPage<AuditExportResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        exports.MapGet("/{exportId}", GetExportAsync)
            .WithName("GetAuditExport")
            .WithSummary("One of your audit exports: status, rows, bytes and SHA-256 of the file.")
            .Produces<AuditExportResource>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> ListEventsAsync(
        string workspaceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? category,
        [FromQuery] string? action,
        [FromQuery] string? actorId,
        [FromQuery] string? controlNumber,
        [FromQuery] Guid? documentId,
        [FromQuery] Guid? jobId,
        [FromQuery] string? outcome,
        [FromQuery] bool? includeSearchText,
        [AsParameters] PageQuery page,
        HttpContext context,
        AuditLogService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        AuditOutcomeResource? outcomeFilter = null;
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (!Enum.TryParse<AuditOutcomeResource>(outcome, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Problems.Validation(new Dictionary<string, string[]> { ["outcome"] = ["The outcome is success, denied or failure."] });
            }

            outcomeFilter = parsed;
        }

        var request = Request(from, to, category, action, actorId, controlNumber, documentId, jobId, outcomeFilter, includeSearchText ?? false);
        var fingerprint = Fingerprint(request);
        AuditCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is not [var filterText, var atText, var eventText]
                || filterText != fingerprint
                || !long.TryParse(atText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParse(eventText, out var eventId))
            {
                return PageCursor.Invalid();
            }

            after = new AuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), eventId);
        }

        var result = await service.QueryAsync(access.Principal, access.WorkspaceId, request, after,
            Math.Min(page.EffectiveLimit, AuditLogService.MaxPageSize), cancellationToken).ConfigureAwait(false);
        if (result.Status != AuditLogStatus.Ok)
        {
            return Problem(result.Status, result.Errors, result.Detail);
        }

        var events = result.Page!;
        var next = events.Next is { } n
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, fingerprint,
                n.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture), n.EventId.ToString("N"))
            : null;
        var items = events.Events.Select(ToResource).ToList();
        return TypedResults.Ok(new CursorPage<AuditEventResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static IResult ListActions(string workspaceId)
    {
        _ = workspaceId;
        return TypedResults.Ok<IReadOnlyList<AuditActionResource>>(
            [.. AuditTaxonomy.All.Select(a => new AuditActionResource(a.Category, a.Action))]);
    }

    internal static async Task<IResult> CreateExportAsync(
        string workspaceId, CreateAuditExportRequest request, HttpContext context, AuditLogService service, AuditExportSignal signal,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send the export request."] });
        }

        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await service.CreateExportAsync(access.Principal, access.WorkspaceId,
            Request(request.From, request.To, request.Category, request.Action, request.ActorId, request.ControlNumber, request.DocumentId,
                request.JobId, request.Outcome, request.IncludeSearchText),
            key.Length > 0 ? key : null, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != AuditLogStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }

        var export = outcome.Creation!.Export;
        signal.Nudge(export.WorkspaceId, export.ExportId);
        return ApiResults.JobAccepted(access.WorkspaceId.ToString(), export.JobId.ToString(), ToResource(export));
    }

    internal static async Task<IResult> ListExportsAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, AuditLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        (DateTimeOffset, Guid)? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var atText, var idText]
                || !long.TryParse(atText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParse(idText, out var id))
            {
                return PageCursor.Invalid();
            }

            after = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        var limit = Math.Min(page.EffectiveLimit, AuditLogService.MaxPageSize);
        var records = await service.ListExportsAsync(access.Principal, access.WorkspaceId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = records.Take(limit).Select(ToResource).ToList();
        var next = records.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId,
                records[limit - 1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), records[limit - 1].ExportId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<AuditExportResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> GetExportAsync(
        string workspaceId, string exportId, HttpContext context, AuditLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(exportId, out var id)
            || await service.GetExportAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return Problems.NotFound(ExportNotFound);
        }

        return TypedResults.Ok(ToResource(export));
    }

    internal static IResult Problem(AuditLogStatus status, IReadOnlyDictionary<string, string[]>? errors, string? detail) => status switch
    {
        AuditLogStatus.Invalid => Problems.Validation(errors?.ToDictionary() ?? []),
        AuditLogStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            detail ?? "You do not have permission for this operation."),
        AuditLogStatus.NotReady => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, detail),
        _ => Problems.NotFound(detail ?? ExportNotFound),
    };

    internal static AuditEventResource ToResource(StoredAuditEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var e = stored.Event;
        return new AuditEventResource(
            e.EventId,
            e.OccurredAt,
            e.Category,
            e.Action,
            Enum.Parse<AuditOutcomeResource>(e.Outcome.ToString()),
            e.ReasonCode,
            new AuditActorResource(Enum.Parse<AuditActorTypeResource>(e.ActorType.ToString()), e.ActorId, e.ActorDisplay),
            e.OnBehalfOf,
            Enum.Parse<AuditAccessPathResource>(e.AccessPath.ToString()),
            e.ResourceType is null && e.ResourceId is null ? null : new AuditEventResourceRef(e.ResourceType ?? string.Empty, e.ResourceId, stored.DocumentControlNumber),
            e.JobId,
            e.ChunkSequence,
            e.SnapshotId,
            e.SearchGeneration,
            e.CorrelationId ?? string.Empty,
            e.ClientIp,
            e.Details,
            e.RestrictedDetails,
            stored.RecordedAt,
            stored.Sequence);
    }

    internal static AuditExportResource ToResource(AuditExportRecord export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var f = export.Filters;
        return new AuditExportResource(
            export.ExportId,
            export.JobId,
            Enum.Parse<AuditExportStatusResource>(export.Status.ToString()),
            export.StatusReason,
            new AuditExportFiltersResource(f.From, f.To, f.Category, f.Action, f.ActorId, f.DocumentId, f.JobId,
                f.Outcome is { } o ? Enum.Parse<AuditOutcomeResource>(o.ToString()) : null),
            export.IncludeSearchText,
            export.OccurredBefore,
            export.CreatedAt,
            export.CompletedAt,
            export.RowCount,
            export.ByteCount,
            export.ContentSha256);
    }

    private static AuditLogRequest Request(
        DateTimeOffset? from, DateTimeOffset? to, string? category, string? action, string? actorId, string? controlNumber, Guid? documentId,
        Guid? jobId, AuditOutcomeResource? outcome, bool includeSearchText) => new(
        new AuditLogFilters
        {
            From = from,
            To = to,
            Category = Blank(category),
            Action = Blank(action),
            ActorId = Blank(actorId),
            DocumentId = documentId,
            JobId = jobId,
            Outcome = outcome is { } o ? Enum.Parse<AuditOutcome>(o.ToString()) : null,
        },
        Blank(controlNumber),
        includeSearchText);

    /// <summary>Binds a cursor to the filters it was issued for.</summary>
    private static string Fingerprint(AuditLogRequest request)
    {
        var f = request.Filters;
        var text = string.Join('\u001f', f.From?.UtcTicks, f.To?.UtcTicks, f.Category, f.Action, f.ActorId, f.DocumentId, f.JobId, f.Outcome,
            request.ControlNumber, request.IncludeSearchText);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Runs audit exports in the API host (it composes the audit store and the PDP): polls the workspaces' active exports and
/// advances each with <see cref="AuditExportRunner"/>; any number of instances may run (claims and chunk leases are in
/// PostgreSQL).
/// </summary>
internal sealed partial class AuditExportBackgroundService(
    IServiceScopeFactory scopes,
    AuditExportSignal signal,
    AuditExportOptions options,
    ILogger<AuditExportBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.BackgroundEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (signal.Reader.TryRead(out var nudge))
                {
                    await ProcessAsync(nudge.WorkspaceId, nudge.ExportId, stoppingToken).ConfigureAwait(false);
                }

                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IAuditLogStore>();
                var workspaces = scope.ServiceProvider.GetRequiredService<IDocumentSetSnapshotStore>();
                foreach (var ws in await workspaces.GetWorkspacesAsync(stoppingToken).ConfigureAwait(false))
                {
                    foreach (var id in await store.GetActiveAsync(ws, 10, stoppingToken).ConfigureAwait(false))
                    {
                        await ProcessAsync(ws, id, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // PostgreSQL unavailable (e.g. a host without it configured): retry on the next pass.
                LogPassFailed(logger, ex);
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(options.PollInterval);
            try
            {
                await signal.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AuditExportRunner>().ProcessAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LogExportFailed(logger, ex, exportId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit export pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit export {ExportId} could not be advanced; retrying on the next poll")]
    private static partial void LogExportFailed(ILogger logger, Exception exception, Guid exportId);
}

public static class AuditLogEndpointRegistration
{
    /// <summary>The audit log viewer and export endpoints, their service, store, runner and its background loop.</summary>
    public static IServiceCollection AddAuditLogEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new AuditExportOptions();
        configuration.GetSection(AuditExportOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresAuditStore();
        services.AddPostgresAuditLogStore();
        services.AddPostgresSnapshotStore();
        services.AddPostgresJobChunkStore();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<AuditExportSignal>();
        services.TryAddScoped<AuditLogService>();
        services.TryAddScoped<AuditExportRunner>();
        services.AddHostedService<AuditExportBackgroundService>();
        services.AddSingleton<IApiEndpointModule, AuditLogEndpoints>();
        return services;
    }
}
