using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Workspaces;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Workspaces;

/// <summary>
/// Reviewer attestation and protective-order acknowledgment (E20-T03). Members read the current text and accept it
/// (<c>…/acknowledgment</c>; the only workspace routes besides the workspace descriptor that PEP-1 serves before the
/// acceptance); administrators with <c>Workspace.ManageAcknowledgments</c> publish versions and read the roster. The CSV
/// roster export is a gateway endpoint (<c>Content/AcknowledgmentRosterContentEndpoints</c>).
/// </summary>
public sealed class AcknowledgmentEndpoints : IApiEndpointModule
{
    public const string AcknowledgmentPath = "/acknowledgment";
    public const string VersionsPath = "/acknowledgment-versions";
    public const string RosterPath = "/acknowledgment-roster";

    private const string Tag = "Acknowledgments";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(AcknowledgmentPath, GetAsync)
            .WithName("GetAcknowledgment")
            .WithTags(Tag)
            .Produces<AcknowledgmentResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("The workspace's current acknowledgment text and whether the caller accepted it.")
            .WithDescription("Readable before accepting. While required and not accepted, every other workspace route except the "
                + "workspace itself answers 403 acknowledgment-required.")
            .RequireWorkspaceMember()
            .AllowBeforeAcknowledgment();

        ws.MapPost(AcknowledgmentPath + "/acceptances", AcceptAsync)
            .WithName("AcceptAcknowledgment")
            .WithTags(Tag)
            .Produces<AcknowledgmentAcceptanceResource>(StatusCodes.Status201Created)
            .Produces<AcknowledgmentAcceptanceResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Accept the current acknowledgment text (its version and SHA-256 as shown). Audited; accepting again changes nothing.")
            .WithDescription("409 acknowledgment-outdated when the version is no longer current or the hash differs (read it again); "
                + "409 conflict when the workspace requires no acknowledgment. 201 when recorded, 200 when it already was.")
            .RequireWorkspaceMember()
            .AllowBeforeAcknowledgment();

        ws.MapGet(VersionsPath, ListVersionsAsync)
            .WithName("ListAcknowledgmentVersions")
            .WithTags(Tag)
            .Produces<AcknowledgmentVersionList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Every published acknowledgment version (without its text), newest first, with acceptance counts; ETag = current version.")
            .RequirePermission(Permission.WorkspaceManageAcknowledgments);

        ws.MapPost(VersionsPath, PublishAsync)
            .WithName("PublishAcknowledgmentVersion")
            .WithTags(Tag)
            .Produces<AcknowledgmentVersionResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Publish the next acknowledgment version (If-Match: the current version, \"0\" for the first). Audited.")
            .WithDescription("Every member, the publisher included, must accept the new version before using the workspace again.")
            .RequirePermission(Permission.WorkspaceManageAcknowledgments);

        ws.MapGet(VersionsPath + "/{version}", GetVersionAsync)
            .WithName("GetAcknowledgmentVersion")
            .WithTags(Tag)
            .Produces<AcknowledgmentVersionResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("One published acknowledgment version with its text.")
            .RequirePermission(Permission.WorkspaceManageAcknowledgments);

        ws.MapGet(RosterPath, ListRosterAsync)
            .WithName("ListAcknowledgmentRoster")
            .WithTags(Tag)
            .Produces<CursorPage<AcknowledgmentRosterEntryResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Who accepted which acknowledgment version, by name: direct members and everyone who accepted a version.")
            .RequirePermission(Permission.WorkspaceManageAcknowledgments);
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, HttpContext context, AcknowledgmentService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var state = await service.GetStateAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        var current = state.Current;
        return TypedResults.Ok(new AcknowledgmentResource(
            current is not null, current?.Version, current?.Title, current?.Body, current?.TextSha256, current?.PublishedAt,
            current is null || state.Acceptance is not null, state.Acceptance?.AcceptedAt));
    }

    internal static async Task<IResult> AcceptAsync(
        string workspaceId, AcknowledgmentAcceptanceWrite? body, HttpContext context, AcknowledgmentService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.AcceptAsync(access.Principal, access.WorkspaceId, body?.Version, body?.TextSha256, cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case AcknowledgmentStatus.Invalid:
                return Problems.Validation(outcome.Errors.ToDictionary());
            case AcknowledgmentStatus.NotRequired:
                return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "This workspace requires no acknowledgment.");
            case AcknowledgmentStatus.Outdated:
                return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.AcknowledgmentOutdated,
                    "The acknowledgment text changed since it was read. Read the current version and accept that one.");
        }

        var acceptance = outcome.Acceptance!;
        var resource = new AcknowledgmentAcceptanceResource(acceptance.Version, acceptance.TextSha256, acceptance.AcceptedAt);
        return outcome.Created
            ? TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{AcknowledgmentPath}", resource)
            : TypedResults.Ok(resource);
    }

    internal static async Task<IResult> ListVersionsAsync(
        string workspaceId, HttpContext context, AcknowledgmentService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var versions = await service.ListVersionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var current = versions.Count == 0 ? 0 : versions[0].Version;
        context.Response.Headers.ETag = EntityTags.ForVersion(current);
        return TypedResults.Ok(new AcknowledgmentVersionList([.. versions.Select(v => ToResource(v, current))], current));
    }

    internal static async Task<IResult> PublishAsync(
        string workspaceId, AcknowledgmentVersionWrite? body, HttpContext context, AcknowledgmentService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var versions = await service.ListVersionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var current = versions.Count == 0 ? 0 : versions[0].Version;
        if (EntityTags.CheckIfMatch(context.Request, current) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.PublishAsync(access.Principal, access.WorkspaceId, current, body?.Title, body?.Text, cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case AcknowledgmentStatus.Invalid:
                return Problems.Validation(outcome.Errors.ToDictionary());
            case AcknowledgmentStatus.VersionConflict:
                return Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
                    "Another version was published since the versions were read.");
        }

        var published = outcome.Version!;
        context.Response.Headers.ETag = EntityTags.ForVersion(published.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{VersionsPath}/{published.Version}", ToResource(published, published.Version));
    }

    internal static async Task<IResult> GetVersionAsync(
        string workspaceId, string version, HttpContext context, AcknowledgmentService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !int.TryParse(version, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            || await service.GetVersionAsync(access.WorkspaceId, number, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return Problems.NotFound();
        }

        var versions = await service.ListVersionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResource(found, versions.Count == 0 ? 0 : versions[0].Version));
    }

    internal static async Task<IResult> ListRosterAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, AcknowledgmentService service, CancellationToken cancellationToken)
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

        AcknowledgmentRosterPosition? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, positionLength: 2) is not [var name, var id]
                || !Guid.TryParse(id, out var userId))
            {
                return PageCursor.Invalid();
            }

            after = new AcknowledgmentRosterPosition(name, userId);
        }

        var versions = await service.ListVersionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var current = versions.Count == 0 ? 0 : versions[0].Version;
        var result = await service.ListRosterAsync(access.WorkspaceId, after, page.EffectiveLimit, cancellationToken).ConfigureAwait(false);
        var next = result.Next is { } n ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, n.SortName, n.UserId.ToString()) : null;
        return TypedResults.Ok(new CursorPage<AcknowledgmentRosterEntryResource>(
            [.. result.Items.Select(e => ToResource(e, current))], next, new TotalCount(result.Total, TotalRelation.Eq)));
    }

    internal static AcknowledgmentRosterStatus StatusOf(AcknowledgmentRosterEntry entry, int currentVersion) =>
        entry.Acceptances.Any(a => a.Version == currentVersion) ? AcknowledgmentRosterStatus.Current
        : entry.Acceptances.Count > 0 ? AcknowledgmentRosterStatus.Outdated
        : AcknowledgmentRosterStatus.Pending;

    private static AcknowledgmentRosterEntryResource ToResource(AcknowledgmentRosterEntry entry, int currentVersion) => new(
        entry.UserId,
        entry.DisplayName,
        entry.Email,
        entry.DirectMember,
        StatusOf(entry, currentVersion),
        [.. entry.Acceptances.Select(a => new AcknowledgmentAcceptanceResource(a.Version, a.TextSha256, a.AcceptedAt))]);

    private static AcknowledgmentVersionResource ToResource(AcknowledgmentVersion v, int currentVersion) => new(
        v.Version,
        v.Title,
        v.Body,
        v.TextSha256,
        new AcknowledgmentActor(v.PublishedBy, v.PublishedByName),
        v.PublishedAt,
        v.AcceptedCount,
        v.Version == currentVersion);
}

public static class AcknowledgmentEndpointRegistration
{
    /// <summary>The acknowledgment endpoints, their use case and the PostgreSQL store.</summary>
    public static IServiceCollection AddAcknowledgmentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresAcknowledgments();
        services.TryAddScoped<AcknowledgmentService>();
        services.AddSingleton<IApiEndpointModule, AcknowledgmentEndpoints>();
        return services;
    }
}
