using System.Globalization;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Snapshots;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.ListExports;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Data.Exports;
using Opportunity.Data.Fields;
using Opportunity.Data.Jobs;
using Opportunity.Data.ListExports;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Snapshots;
using Opportunity.Production.ListExports;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Exports;

/// <summary>
/// Document list export to CSV (#181), under <c>/api/v1/workspaces/{workspaceId}</c>, every route behind
/// <c>Document.ExportList</c>:
/// <list type="bullet">
/// <item><c>POST /list-exports</c>: the list's view columns and its search (or checked rows) are frozen as a
/// <c>ListExport</c> snapshot and written to CSV by a background job (202 with the export; <c>Export.ListRequested</c>).</item>
/// <item><c>GET /list-exports</c> and <c>/list-exports/{exportId}</c>: your own list exports.</item>
/// </list>
/// The file is a gateway endpoint (<see cref="Content.ListExportContentEndpoints"/>). Each chunk re-authorizes every
/// document for the requester (Q-15); documents they can no longer export are left out and counted.
/// </summary>
public sealed class ListExportEndpoints : IApiEndpointModule
{
    public const string ListExportsPath = "/list-exports";

    private const string Tag = "Exports";
    private const string NotFound = "No such list export.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.Workspace.MapGroup(ListExportsPath).WithTags(Tag).RequirePermission(Permission.DocumentExportList);

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateListExport")
            .WithSummary("Export the document list (Control Number and the view's columns) to CSV as a background job; 202 with the export. Audited (Export.ListRequested).")
            .WithDescription(
                "Give the view's field IDs in order and exactly one source: query (the list's effective query; empty lists every document) " +
                "or documentIds (rows checked one by one), with the list's expand. The selection is frozen now; the file has one row per " +
                "document in the frozen order, UTF-8 with a byte-order mark. Every document is re-authorized for you when its part is " +
                "written, and documents you can no longer export are left out (Q-15). Only you can see the export and download its file.")
            .RequireIdempotencyKey()
            .Produces<ListExportResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListListExports")
            .WithSummary("Your list exports, newest first.")
            .Produces<CursorPage<ListExportResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{exportId}", GetAsync)
            .WithName("GetListExport")
            .WithSummary("One of your list exports: status, rows, documents left out (Q-15), bytes and SHA-256 of the file.")
            .Produces<ListExportResource>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> CreateAsync(
        string workspaceId, CreateListExportRequest request, HttpContext context, ListExportService service, ListExportSignal signal,
        SnapshotMaterializationSignal snapshots, CancellationToken cancellationToken)
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

        var session = Guid.TryParse(context.User.FindFirst(OpportunityClaimTypes.SessionId)?.Value, out var sid) ? sid : (Guid?)null;
        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await service.CreateAsync(
            new SearchCaller(access.Principal, access.WorkspaceId, session),
            new ListExportCreateRequest(request.FieldIds, request.Query, request.DocumentIds, request.Expand.ToExpansion(), request.Name),
            key.Length > 0 ? key : null,
            cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case ListExportCreateStatus.Accepted:
                var export = outcome.Creation!.Export;
                if (outcome.Snapshot is { Status: SnapshotStatus.Materializing } pending)
                {
                    snapshots.Nudge(access.WorkspaceId, pending.SnapshotId);
                }

                signal.Nudge(export.WorkspaceId, export.ExportId);
                return ApiResults.JobAccepted(access.WorkspaceId.ToString(), export.JobId.ToString(), ToResource(export));
            case ListExportCreateStatus.Invalid:
                return Problems.Validation(outcome.Errors?.ToDictionary() ?? []);
            case ListExportCreateStatus.InvalidQuery:
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: "The query cannot be run; see queryErrors.",
                    type: ProblemCodes.TypeFor(ProblemCodes.InvalidQuery),
                    extensions: new Dictionary<string, object?>
                    {
                        [Problems.CodeExtension] = ProblemCodes.InvalidQuery,
                        ["queryErrors"] = outcome.QueryErrors,
                    });
            case ListExportCreateStatus.Forbidden:
                return Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");
            case ListExportCreateStatus.TooLarge:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.UnprocessableContent,
                    string.Create(CultureInfo.InvariantCulture, $"The list has {outcome.Hits:N0} documents, more than one export may hold; narrow it with a filter."));
            case ListExportCreateStatus.Failed:
                return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable,
                    "The list changed while it was being frozen; try again.");
            default:
                return Problems.NotFound();
        }
    }

    internal static async Task<IResult> ListAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, IListExportStore store, CancellationToken cancellationToken)
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

        var limit = page.EffectiveLimit;
        var records = await store.ListAsync(access.WorkspaceId, access.Principal.UserId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = records.Take(limit).Select(ToResource).ToList();
        var next = records.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId,
                records[limit - 1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), records[limit - 1].ExportId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<ListExportResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string exportId, HttpContext context, ListExportService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(exportId, out var id)
            || await service.GetOwnAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return Problems.NotFound(NotFound);
        }

        return TypedResults.Ok(ToResource(export));
    }

    internal static ListExportResource ToResource(ListExportRecord export)
    {
        ArgumentNullException.ThrowIfNull(export);
        return new ListExportResource(
            export.ExportId,
            export.JobId,
            export.Name,
            Enum.Parse<ListExportStatusResource>(export.Status.ToString()),
            export.StatusReason,
            export.SnapshotId,
            export.DocumentCount,
            [.. export.Columns.Select(c => new ListExportColumnResource(c.FieldId, c.Header))],
            export.CreatedAt,
            export.CompletedAt,
            export.RowCount,
            export.ExcludedCount,
            export.ByteCount,
            export.ContentSha256);
    }
}

/// <summary>
/// Runs list exports in the API host (it composes the snapshot store, the export document reader and the PDP): polls
/// the workspaces' active exports and advances each with <see cref="ListExportRunner"/>; any number of instances may run
/// (claims and chunk leases are in PostgreSQL).
/// </summary>
internal sealed partial class ListExportBackgroundService(
    IServiceScopeFactory scopes,
    ListExportSignal signal,
    ListExportOptions options,
    ILogger<ListExportBackgroundService> logger) : BackgroundService
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
                var store = scope.ServiceProvider.GetRequiredService<IListExportStore>();
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
            await scope.ServiceProvider.GetRequiredService<ListExportRunner>().ProcessAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LogExportFailed(logger, ex, exportId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "List export pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "List export {ExportId} could not be advanced; retrying on the next poll")]
    private static partial void LogExportFailed(ILogger logger, Exception exception, Guid exportId);
}

public static class ListExportEndpointRegistration
{
    /// <summary>The list export endpoints, their service, store, runner and its background loop; needs the snapshot endpoints' services.</summary>
    public static IServiceCollection AddListExportEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ListExportOptions();
        configuration.GetSection(ListExportOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresListExportStore();
        services.AddPostgresExportStore();
        services.AddPostgresSnapshotStore();
        services.AddPostgresJobChunkStore();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.TryAddSingleton<ListExportSignal>();
        services.TryAddScoped<ListExportService>();
        services.TryAddScoped<ListExportRunner>();
        services.AddHostedService<ListExportBackgroundService>();
        services.AddSingleton<IApiEndpointModule, ListExportEndpoints>();
        return services;
    }
}
