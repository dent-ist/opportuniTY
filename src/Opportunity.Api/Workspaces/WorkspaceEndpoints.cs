using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Opportunity.Api.Conventions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Search;
using Opportunity.Data.Workspaces;
using Opportunity.Hosting.Options;
using Opportunity.Search;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Workspaces;

/// <summary>Workspace settings, section <c>Workspaces</c>.</summary>
public sealed class WorkspaceApiOptions : IValidatableObject
{
    public const string SectionName = "Workspaces";

    /// <summary>The storage profiles a workspace may use. Names only; what each maps to is storage configuration.</summary>
    public string[] StorageProfiles { get; set; } = [WorkspaceRules.DefaultStorageProfile];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StorageProfiles is not { Length: > 0 } || StorageProfiles.Any(p => !WorkspaceRules.IsStorageProfileName(p)))
        {
            yield return new ValidationResult(
                "Workspaces:StorageProfiles needs at least one name of lower-case letters, digits and '-' (starting with a letter).",
                [nameof(StorageProfiles)]);
        }
    }
}

/// <summary>
/// Workspace management (E04-T05): list the caller's workspaces, create (installation admins), read with effective
/// permissions and read-only search placement, update settings, and list role assignments. Every workspace route is
/// authorized by PEP-1 (non-members get 404); every change is audited in its own transaction (ADR-013 §2.1).
/// Deletion is requested through E20-T02, not here.
/// </summary>
public sealed class WorkspaceEndpoints : IApiEndpointModule
{
    public const string CollectionPath = "/workspaces";
    public const string MembersPath = "/members";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Installation-level: the list is filtered to the caller's memberships by the same rule as PEP-1.
        routes.V1.MapGet(CollectionPath, ListWorkspacesAsync)
            .WithName("ListWorkspaces")
            .WithTags("Workspaces")
            .ProducesValidationProblem()
            .WithSummary("The workspaces the current user is a member of, by name.")
            .WithDescription("Only workspaces the user may open are listed: a workspace that would answer 404 to GET is never in the list.")
            .RequireAuthorization();

        routes.V1.MapPost(CollectionPath, CreateWorkspaceAsync)
            .WithName("CreateWorkspace")
            .WithTags("Workspaces")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("Create a workspace; the creator becomes its first Workspace Admin.")
            .WithDescription("Needs Installation.ManageWorkspaces and an MFA session (ADR-015 D3.6).")
            .RequireInstallationPermission(InstallationPermissions.ManageWorkspaces)
            .RequireMfa();

        routes.Workspace.MapGet(string.Empty, GetWorkspaceAsync)
            .WithName("GetWorkspace")
            .WithTags("Workspaces")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("The workspace, the current user's effective permissions in it and its read-only search placement.")
            .WithDescription("A workspace the user is not a member of returns 404, exactly like one that does not exist.")
            .RequireWorkspaceMember();

        // Settings sit with the other workspace administration of the Admin menu (Workspace Settings, Workspace.ManageSecurity).
        routes.Workspace.MapPut(string.Empty, UpdateWorkspaceAsync)
            .WithName("UpdateWorkspace")
            .WithTags("Workspaces")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Replace the workspace settings: name, matter number, display time zone and storage profile (If-Match required).")
            .RequirePermission(Permission.WorkspaceManageSecurity);

        routes.Workspace.MapGet(MembersPath, ListMembersAsync)
            .WithName("ListWorkspaceMembers")
            .WithTags("Workspaces")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Role assignments of the workspace, to users and to IdP groups.")
            .RequirePermission(Permission.WorkspaceManageUsers);
    }

    internal static async Task<Results<Ok<CursorPage<WorkspaceSummary>>, ValidationProblem>> ListWorkspacesAsync(
        [AsParameters] PageQuery page, HttpContext context, IWorkspaceStore store, CancellationToken cancellationToken)
    {
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        var principal = context.ToSecurityPrincipal();
        WorkspaceListPosition? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, principal.UserId, workspaceId: null, positionLength: 2) is not [var name, var id]
                || !Guid.TryParse(id, out var workspaceId))
            {
                return PageCursor.Invalid();
            }

            after = new WorkspaceListPosition(name, workspaceId);
        }

        var result = await store.ListForPrincipalAsync(principal, after, page.EffectiveLimit, cancellationToken).ConfigureAwait(false);
        var next = result.Next is { } n ? PageCursor.Encode(principal.UserId, null, n.SortName, n.WorkspaceId.ToString()) : null;
        var items = result.Items.Select(w => new WorkspaceSummary(w.WorkspaceId, w.Name, w.MatterNumber, w.DisplayTimeZone, Status(w.Status), w.CreatedAt)).ToList();
        return TypedResults.Ok(new CursorPage<WorkspaceSummary>(items, next, new TotalCount(result.Total, TotalRelation.Eq)));
    }

    internal static async Task<Results<Created<WorkspaceResource>, ValidationProblem, ProblemHttpResult>> CreateWorkspaceAsync(
        WorkspaceWrite body,
        HttpContext context,
        IWorkspaceStore store,
        IAuthorizationService authorization,
        IOptions<WorkspaceApiOptions> options,
        CancellationToken cancellationToken)
    {
        if (Validate(body, current: null, options.Value, out var settings) is { } invalid)
        {
            return invalid;
        }

        var principal = context.ToSecurityPrincipal();
        var workspaceId = Guid.CreateVersion7();
        var result = await store.CreateAsync(workspaceId, settings!, principal, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == WorkspaceWriteOutcome.InvalidTimeZone)
        {
            return InvalidTimeZone();
        }

        if (WriteProblem(result.Outcome) is { } problem)
        {
            return problem;
        }

        var resource = await ToResourceAsync(context, result.Workspace!, principal, authorization, cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = EntityTags.ForVersion(result.Workspace!.RowVersion);
        return TypedResults.Created($"{ApiRoutes.V1Prefix}{CollectionPath}/{workspaceId}", resource);
    }

    internal static async Task<Results<Ok<WorkspaceResource>, ProblemHttpResult>> GetWorkspaceAsync(
        string workspaceId, HttpContext context, IWorkspaceReader workspaces, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access
            || await workspaces.GetAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false) is not { } workspace)
        {
            return Problems.NotFound();
        }

        if (await ToResourceAsync(context, workspace, access.Principal, authorization, cancellationToken).ConfigureAwait(false) is not { } resource)
        {
            return Problems.NotFound();
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(workspace.RowVersion);
        return TypedResults.Ok(resource);
    }

    internal static async Task<Results<Ok<WorkspaceResource>, ValidationProblem, ProblemHttpResult>> UpdateWorkspaceAsync(
        string workspaceId,
        WorkspaceWrite body,
        HttpContext context,
        IWorkspaceReader workspaces,
        IWorkspaceStore store,
        IAuthorizationService authorization,
        IOptions<WorkspaceApiOptions> options,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access
            || await workspaces.GetAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound();
        }

        if (EntityTags.CheckIfMatch(context.Request, current.RowVersion) is { } precondition)
        {
            return precondition;
        }

        if (Validate(body, current.StorageProfile, options.Value, out var settings) is { } invalid)
        {
            return invalid;
        }

        var result = await store.UpdateAsync(access.WorkspaceId, current.RowVersion, settings!, access.Principal, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == WorkspaceWriteOutcome.InvalidTimeZone)
        {
            return InvalidTimeZone();
        }

        if (WriteProblem(result.Outcome) is { } problem)
        {
            return problem;
        }

        if (await ToResourceAsync(context, result.Workspace!, access.Principal, authorization, cancellationToken).ConfigureAwait(false) is not { } resource)
        {
            return Problems.NotFound();
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(result.Workspace!.RowVersion);
        return TypedResults.Ok(resource);
    }

    internal static async Task<Results<Ok<CursorPage<WorkspaceMemberResource>>, ValidationProblem, ProblemHttpResult>> ListMembersAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, IWorkspaceStore store, CancellationToken cancellationToken)
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

        Guid? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, positionLength: 1) is not [var id]
                || !Guid.TryParse(id, out var assignmentId))
            {
                return PageCursor.Invalid();
            }

            after = assignmentId;
        }

        var result = await store.ListMembersAsync(access.WorkspaceId, after, page.EffectiveLimit, cancellationToken).ConfigureAwait(false);
        var items = result.Items.Select(m => new WorkspaceMemberResource(
            m.AssignmentId,
            m.UserId is null ? WorkspaceMemberKind.Group : WorkspaceMemberKind.User,
            m.Role.Key(),
            m.UserId,
            m.UserDisplayName,
            m.GroupName,
            m.AssignedAt)).ToList();
        var next = result.NextAfter is { } n ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, n.ToString()) : null;
        return TypedResults.Ok(new CursorPage<WorkspaceMemberResource>(items, next, new TotalCount(result.Total, TotalRelation.Eq)));
    }

    /// <summary>The resource for <paramref name="principal"/>, or null when the PDP no longer admits them.</summary>
    private static async Task<WorkspaceResource?> ToResourceAsync(
        HttpContext context, Workspace workspace, SecurityPrincipal principal, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        var effective = await authorization.GetEffectivePermissionsAsync(principal, workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (!effective.Decision.IsAllowed)
        {
            return null;
        }

        WorkspaceSearchPlacementResource? placement = null;
        if (context.RequestServices.GetService<IWorkspaceSearchPlacement>() is { } search
            && await search.GetAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false) is { } info)
        {
            placement = new WorkspaceSearchPlacementResource(
                info.Kind == IndexPlacementKind.Dedicated ? WorkspaceSearchPlacementKind.Dedicated : WorkspaceSearchPlacementKind.Shared,
                info.ProjectionGeneration,
                info.State switch
                {
                    IndexPlacementState.Building => WorkspaceSearchPlacementState.Building,
                    IndexPlacementState.Moving => WorkspaceSearchPlacementState.Moving,
                    IndexPlacementState.Deleting => WorkspaceSearchPlacementState.Deleting,
                    _ => WorkspaceSearchPlacementState.Active,
                });
        }

        return new WorkspaceResource(
            workspace.WorkspaceId,
            workspace.Name,
            workspace.MatterNumber,
            workspace.DisplayTimeZone,
            Status(workspace.Status),
            workspace.CreatedAt,
            [.. effective.Permissions.Select(p => p.Name())],
            effective.BreakGlass,
            workspace.StorageProfile,
            workspace.RowVersion,
            workspace.UpdatedAt,
            placement);
    }

    private static WorkspaceResourceStatus Status(WorkspaceStatus status) =>
        status == WorkspaceStatus.Active ? WorkspaceResourceStatus.Active : WorkspaceResourceStatus.Closed;

    private static ProblemHttpResult? WriteProblem(WorkspaceWriteOutcome outcome) => outcome switch
    {
        WorkspaceWriteOutcome.Ok => null,
        WorkspaceWriteOutcome.VersionConflict =>
            Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict, "The resource was modified since it was read."),
        WorkspaceWriteOutcome.StorageProfileLocked =>
            Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The storage profile cannot change once the workspace holds stored objects."),
        _ => Problems.NotFound(),
    };

    private static ValidationProblem InvalidTimeZone() =>
        Problems.Validation(new Dictionary<string, string[]> { ["displayTimeZone"] = ["displayTimeZone is not a known IANA time zone ID."] });

    /// <param name="current">The workspace's profile on update (kept when omitted, accepted even if no longer configured).</param>
    private static ValidationProblem? Validate(WorkspaceWrite? body, string? current, WorkspaceApiOptions options, out WorkspaceSettings? settings)
    {
        settings = null;
        if (body is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["The workspace settings are required."] });
        }

        var errors = new Dictionary<string, string[]>();
        var name = body.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > WorkspaceRules.MaxNameLength)
        {
            errors["name"] = [$"name must be 1 to {WorkspaceRules.MaxNameLength} characters."];
        }

        var matter = string.IsNullOrWhiteSpace(body.MatterNumber) ? null : body.MatterNumber.Trim();
        if (matter is { Length: > WorkspaceRules.MaxMatterNumberLength })
        {
            errors["matterNumber"] = [$"matterNumber must be at most {WorkspaceRules.MaxMatterNumberLength} characters."];
        }

        var timeZone = body.DisplayTimeZone?.Trim() ?? string.Empty;
        if (timeZone.Length is 0 or > WorkspaceRules.MaxTimeZoneLength)
        {
            errors["displayTimeZone"] = ["displayTimeZone must be an IANA time zone ID such as Europe/Berlin."];
        }

        var profile = string.IsNullOrWhiteSpace(body.StorageProfile) ? current ?? options.StorageProfiles[0] : body.StorageProfile.Trim();
        if (profile != current && !options.StorageProfiles.Contains(profile, StringComparer.Ordinal))
        {
            errors["storageProfile"] = [$"storageProfile must be one of: {string.Join(", ", options.StorageProfiles)}."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        settings = new WorkspaceSettings(name, matter, timeZone, profile);
        return null;
    }
}

public static class WorkspaceEndpointRegistration
{
    /// <summary>
    /// The workspace endpoints and their stores. The read-only search placement comes from index management
    /// (<see cref="IWorkspaceSearchPlacement"/>) when OpenSearch is configured; without it the placement is omitted.
    /// </summary>
    public static IServiceCollection AddWorkspaceEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddValidatedOptions<WorkspaceApiOptions>(WorkspaceApiOptions.SectionName);
        services.AddPostgresWorkspaceStore();
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(OpenSearchOptions.ConnectionStringName))
            || configuration.GetSection(OpenSearchOptions.SectionName).GetValue<string>(nameof(OpenSearchOptions.Endpoint)) is { Length: > 0 })
        {
            services.AddPostgresIndexPlacementStore();
            services.AddOpenSearchIndexManagement(OpenSearchOptions.Bind(configuration));
        }

        services.AddSingleton<IApiEndpointModule, WorkspaceEndpoints>();
        return services;
    }
}
