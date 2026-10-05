using System.Globalization;
using System.Threading.Channels;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Data.Snapshots;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Snapshots;

/// <summary>
/// Frozen sets (E10-T02): <c>POST …/snapshots</c> materializes a document set and returns its frozen count and
/// generation for the bulk confirmation (201 when frozen in the request; 202 with <c>Location</c> of the snapshot while
/// a large set materializes in the background); <c>GET …/snapshots/{id}</c> and <c>GET …/snapshots</c> read headers.
/// Creating needs the purpose's permission (<c>Coding.Bulk</c>, <c>Export.Create</c>, <c>Production.Create</c> or
/// <c>Search.Execute</c> for reports); users see their own snapshots, others need <c>Job.ViewAll</c>. Membership is not
/// served over HTTP: consumers (bulk coding, export) read it server-side by ordinal range.
/// </summary>
public sealed class SnapshotEndpoints : IApiEndpointModule
{
    public const string Path = "/snapshots";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.Workspace.MapGroup(Path).RequireWorkspaceMember();

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateSnapshot")
            .WithTags("Snapshots")
            .WithSummary("Freeze a document set (query, document IDs or another snapshot) and return its frozen count.")
            .WithDescription(
                "201 with the Ready snapshot when it was frozen in the request; 202 with Location of the snapshot while a large " +
                "set materializes. Documents you may not act on for the purpose are never included. Needs Coding.Bulk, " +
                "Export.Create, Production.Create or Search.Execute (Report), by purpose.")
            .RequireIdempotencyKey()
            .Produces<SnapshotResource>(StatusCodes.Status201Created)
            .Produces<SnapshotResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{snapshotId}", GetAsync)
            .WithName("GetSnapshot")
            .WithTags("Snapshots")
            .WithSummary("A snapshot's status, frozen count, generation and verification hash.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListSnapshots")
            .WithTags("Snapshots")
            .WithSummary("Frozen sets, newest first: your own, or everyone's with Job.ViewAll.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Created<SnapshotResource>, Accepted<SnapshotResource>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        string workspaceId,
        CreateSnapshotRequest request,
        HttpContext context,
        DocumentSetSnapshotService snapshots,
        SnapshotMaterializationSignal queue,
        SnapshotOptions options,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (Caller(context) is not { } caller)
        {
            return Problems.NotFound();
        }

        if (request is null || !Enum.IsDefined(request.Purpose))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["purpose"] = ["Purpose must be BulkCoding, Export, Production or Report."] });
        }

        var query = request.Query;
        var expansion = request.Expand.ToExpansion();
        if (request.SavedSearchId is { } savedSearchId)
        {
            if (request.Query is not null || request.DocumentIds is not null || request.SnapshotId is not null)
            {
                return Problems.Validation(new Dictionary<string, string[]>
                {
                    ["savedSearchId"] = ["Give exactly one source: query, documentIds, snapshotId or savedSearchId."],
                });
            }

            // E07-T09: a saved search the caller can see freezes as its reference, so the snapshot records which search it
            // came from and its criteria are expanded (and filtered for the caller) when the set is selected.
            if (context.RequestServices.GetService<ISavedSearchQueries>() is not { } savedSearches
                || await savedSearches.FindForRunAsync(caller.Principal, caller.WorkspaceId, savedSearchId, cancellationToken).ConfigureAwait(false)
                    is not { } saved)
            {
                return Problems.NotFound("No such saved search.");
            }

            // E09-T03: the saved search's stored "Include family / duplicates / email thread" unless the request gives its own.
            if (request.Expand is null)
            {
                expansion = saved.Expansion;
            }

            query = $"{SavedSearchQuerySyntax.FieldName}:{savedSearchId:D}";
        }

        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await snapshots.CreateAsync(caller, new SnapshotCreateRequest(
            Enum.Parse<SnapshotPurpose>(request.Purpose.ToString()),
            request.Name,
            query,
            request.DocumentIds,
            request.SnapshotId,
            key.Length > 0 ? key : null,
            expansion), cancellationToken).ConfigureAwait(false);

        var ws = caller.WorkspaceId.ToString();
        switch (outcome.Status)
        {
            case SnapshotCreateStatus.Ready:
                return TypedResults.Created(Location(ws, outcome.Snapshot!.SnapshotId), ToResource(outcome.Snapshot, caller.Principal.UserId, options));
            case SnapshotCreateStatus.Accepted:
                queue.Nudge(caller.WorkspaceId, outcome.Snapshot!.SnapshotId);
                return TypedResults.Accepted(Location(ws, outcome.Snapshot.SnapshotId), ToResource(outcome.Snapshot, caller.Principal.UserId, options));
            case SnapshotCreateStatus.InvalidRequest:
                return Problems.Validation(outcome.RequestErrors.ToDictionary());
            case SnapshotCreateStatus.InvalidQuery:
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: "The query cannot be run; see queryErrors.",
                    type: ProblemCodes.TypeFor(ProblemCodes.InvalidQuery),
                    extensions: new Dictionary<string, object?>
                    {
                        [Problems.CodeExtension] = ProblemCodes.InvalidQuery,
                        ["queryErrors"] = outcome.QueryErrors,
                    });
            case SnapshotCreateStatus.Forbidden:
                return Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");
            case SnapshotCreateStatus.TooLarge:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.UnprocessableContent,
                    string.Create(CultureInfo.InvariantCulture, $"The selection has {outcome.Hits:N0} documents, more than a snapshot may hold; narrow the selection."));
            case SnapshotCreateStatus.Failed:
                return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable,
                    "The selection changed while it was being frozen; try again.");
            default:
                return Problems.NotFound();
        }
    }

    internal static async Task<Results<Ok<SnapshotResource>, ProblemHttpResult>> GetAsync(
        string workspaceId, string snapshotId, HttpContext context, IDocumentSetSnapshotStore store, SnapshotOptions options,
        IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(snapshotId, out var id)
            || await store.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } snapshot)
        {
            return Problems.NotFound("No such snapshot.");
        }

        if (snapshot.CreatedBy != access.Principal.UserId)
        {
            var decision = await authorization.AuthorizeAsync(access.Principal, access.WorkspaceId, Permission.JobViewAll, cancellationToken)
                .ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return AuthorizationResults.Problem(decision);
            }
        }

        return TypedResults.Ok(ToResource(snapshot, access.Principal.UserId, options));
    }

    internal static async Task<Results<Ok<CursorPage<SnapshotResource>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, IDocumentSetSnapshotStore store,
        IAuthorizationService authorization, SnapshotOptions options, CancellationToken cancellationToken)
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

        var user = access.Principal.UserId;
        SnapshotListCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, user, access.WorkspaceId, 2) is not { } position
                || !long.TryParse(position[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParseExact(position[1], "N", out var lastId))
            {
                return PageCursor.Invalid();
            }

            after = new SnapshotListCursor(new DateTimeOffset(ticks, TimeSpan.Zero), lastId);
        }

        var viewAll = (await authorization.AuthorizeAsync(access.Principal, access.WorkspaceId, Permission.JobViewAll, cancellationToken)
            .ConfigureAwait(false)).IsAllowed;
        var limit = page.EffectiveLimit;
        var records = await store.ListAsync(access.WorkspaceId, viewAll ? null : user, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = records.Take(limit).Select(r => ToResource(r, user, options)).ToList();
        var next = records.Count > limit
            ? PageCursor.Encode(user, access.WorkspaceId,
                records[limit - 1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), records[limit - 1].SnapshotId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<SnapshotResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static SnapshotResource ToResource(SnapshotRecord s, Guid viewer, SnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(options);
        var mine = s.CreatedBy == viewer;
        return new SnapshotResource(
            s.SnapshotId,
            s.Name,
            Enum.Parse<SnapshotResourcePurpose>(s.Purpose.ToString()),
            Enum.Parse<SnapshotResourceStatus>(s.Status.ToString()),
            s.StatusReason,
            new SnapshotSourceResource(
                Enum.Parse<SnapshotResourceSourceKind>(s.SourceKind.ToString()),
                mine ? s.QueryText : null,
                mine ? s.NormalizedQuery : null,
                s.SourceSnapshotId,
                s.RequestedCount),
            s.DocumentCount,
            s.InclusionCounts.ToDictionary(c => c.Key.ToString(), c => c.Value),
            s.SearchGeneration,
            s.ProjectionGeneration,
            s.SelectedWhileIndexing,
            s.SelectedAt,
            s.Strategy.ToString(),
            s.PageSize,
            s.PageCount,
            s.RootSha256 is { } root ? Convert.ToHexStringLower(root) : null,
            s.CreatedBy,
            s.CreatedAt,
            s.MaterializedAt,
            s.Status is SnapshotStatus.Expired || s.Referenced ? null : s.CreatedAt + options.UnreferencedLifetime,
            s.ExpiredAt,
            s.Expansion.ToContract());
    }

    private static string Location(string workspaceId, Guid snapshotId) =>
        $"{ApiRoutes.V1Prefix}/workspaces/{Uri.EscapeDataString(workspaceId)}{Path}/{snapshotId}";

    private static SearchCaller? Caller(HttpContext context)
    {
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return null;
        }

        var session = Guid.TryParse(context.User.FindFirst(OpportunityClaimTypes.SessionId)?.Value, out var id) ? id : (Guid?)null;
        return new SearchCaller(access.Principal, access.WorkspaceId, session);
    }
}

/// <summary>Wakes the background materializer for a snapshot that answered 202 (the poll is the crash-recovery path).</summary>
public sealed class SnapshotMaterializationSignal
{
    private readonly Channel<(Guid WorkspaceId, Guid SnapshotId)> _channel =
        Channel.CreateBounded<(Guid, Guid)>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest });

    internal ChannelReader<(Guid WorkspaceId, Guid SnapshotId)> Reader => _channel.Reader;

    public void Nudge(Guid workspaceId, Guid snapshotId) => _channel.Writer.TryWrite((workspaceId, snapshotId));
}

public static class SnapshotEndpointRegistration
{
    /// <summary>Snapshot endpoints, the snapshot service and store, and the background materializer and retention loop.</summary>
    public static IServiceCollection AddSnapshotEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new SnapshotOptions();
        configuration.GetSection(SnapshotOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresSnapshotStore();
        services.TryAddScoped<DocumentSetSnapshotService>();
        services.TryAddSingleton<SnapshotMaterializationSignal>();
        services.AddHostedService<SnapshotBackgroundService>();
        services.AddSingleton<IApiEndpointModule, SnapshotEndpoints>();
        return services;
    }
}
