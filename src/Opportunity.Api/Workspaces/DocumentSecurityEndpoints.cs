using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Fields;
using Opportunity.Application.Security;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Workspaces;

/// <summary>
/// Document- and field-level security administration (E05-T06; ADR-015 D6; Q-11, Q-13, Q-45) under
/// <c>/api/v1/workspaces/{workspaceId}/security</c>: restriction classes with their role grants and the coding choices
/// that apply them, ethical walls (members and scope), field restrictions (all <c>Workspace.ManageSecurity</c>, versioned
/// with If-Match, audited), and break-glass activations (opened by the holder of a BreakGlass role assignment with an
/// MFA step-up; the report needs <c>Audit.Read</c>). A change that alters who may see documents is enforced from its
/// commit on and re-projected on the security lane (Q-10).
/// </summary>
public sealed class DocumentSecurityEndpoints : IApiEndpointModule
{
    public const string ClassesPath = "/security/restriction-classes";
    public const string WallsPath = "/security/walls";
    public const string FieldRestrictionsPath = "/security/field-restrictions";
    public const string BreakGlassPath = "/security/break-glass/activations";

    private const string Tag = "Document security";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(ClassesPath, ListClassesAsync)
            .WithName("ListRestrictionClasses")
            .WithTags(Tag)
            .WithSummary("The workspace's restriction classes with the roles that may see them and the coding choices that apply them.")
            .Produces<RestrictionClassList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapPut(ClassesPath + "/{classKey}", PutClassAsync)
            .WithName("PutRestrictionClass")
            .WithTags(Tag)
            .WithSummary("Create or replace a restriction class (If-Match when it exists; audited).")
            .WithDescription(
                "Documents coded with any of the rules' choices carry the class from the commit on; documents that no longer qualify lose it. "
                + "Changing the grant of a role you hold answers 403 self-protection (ADR-015 D6.5).")
            .Produces<RestrictionClassResource>()
            .Produces<RestrictionClassResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapDelete(ClassesPath + "/{classKey}", DeleteClassAsync)
            .WithName("DeleteRestrictionClass")
            .WithTags(Tag)
            .WithSummary("Delete a workspace-defined restriction class; its documents lose it (If-Match; audited). Built-in classes answer 409.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapGet(WallsPath, ListWallsAsync)
            .WithName("ListEthicalWalls")
            .WithTags(Tag)
            .WithSummary("The workspace's ethical walls with members and scope (documents you may not see are left out).")
            .Produces<EthicalWallList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapPost(WallsPath, CreateWallAsync)
            .WithName("CreateEthicalWall")
            .WithTags(Tag)
            .WithSummary("Create an ethical wall: covered documents are hidden from its members on every path from the commit on (audited).")
            .WithDescription("A wall that names you (directly or through a group) answers 403 self-protection: another administrator must create it.")
            .Produces<EthicalWallResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapGet(WallsPath + "/{wallId}", GetWallAsync)
            .WithName("GetEthicalWall")
            .WithTags(Tag)
            .WithSummary("An ethical wall with its ETag.")
            .Produces<EthicalWallResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapPut(WallsPath + "/{wallId}", UpdateWallAsync)
            .WithName("UpdateEthicalWall")
            .WithTags(Tag)
            .WithSummary("Replace an ethical wall's name, members and scope (If-Match; audited; never a wall that names you).")
            .Produces<EthicalWallResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapDelete(WallsPath + "/{wallId}", DeleteWallAsync)
            .WithName("DeleteEthicalWall")
            .WithTags(Tag)
            .WithSummary("Delete an ethical wall (If-Match; audited; never a wall that names you).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapGet(FieldRestrictionsPath, ListFieldRestrictionsAsync)
            .WithName("ListFieldRestrictions")
            .WithTags(Tag)
            .WithSummary("Field-level restrictions: which roles see and edit each restricted custom field.")
            .Produces<FieldRestrictionList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapPut(FieldRestrictionsPath + "/{fieldId}", PutFieldRestrictionAsync)
            .WithName("PutFieldRestriction")
            .WithTags(Tag)
            .WithSummary("Restrict a custom field to roles (If-Match when restricted already; audited).")
            .WithDescription(
                "Principals holding none of visibleTo do not see the field anywhere (metadata, coding, layouts, search fields, result columns, "
                + "exports, productions); principals holding none of editableBy see it read-only.")
            .Produces<FieldRestrictionResource>()
            .Produces<FieldRestrictionResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapDelete(FieldRestrictionsPath + "/{fieldId}", DeleteFieldRestrictionAsync)
            .WithName("DeleteFieldRestriction")
            .WithTags(Tag)
            .WithSummary("Lift a field restriction (If-Match; audited).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageSecurity);

        ws.MapGet(BreakGlassPath, ListBreakGlassAsync)
            .WithName("ListBreakGlassActivations")
            .WithTags(Tag)
            .WithSummary("The break-glass report (Audit.Read: every activation, newest first) or your own activations.")
            .Produces<BreakGlassActivationList>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        ws.MapPost(BreakGlassPath, ActivateBreakGlassAsync)
            .WithName("ActivateBreakGlass")
            .WithTags(Tag)
            .WithSummary("Open break-glass access: read-only (view, search, audit) past restriction classes and walls for 1–240 minutes (default 60).")
            .WithDescription(
                "Needs a BreakGlass role assignment, a stated reason and an MFA step-up (Q-45). The activation and every action during it are "
                + "audited with access path BreakGlass. 409 while one is open.")
            .Produces<BreakGlassActivationResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireBreakGlassHolder()
            .RequireMfa();

        ws.MapPost(BreakGlassPath + "/{activationId}/end", EndBreakGlassAsync)
            .WithName("EndBreakGlass")
            .WithTags(Tag)
            .WithSummary("End your open activation, or revoke another user's with Workspace.ManageSecurity (audited).")
            .Produces<BreakGlassActivationResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireWorkspaceMember();
    }

    // ----- Restriction classes -----

    internal static async Task<IResult> ListClassesAsync(string workspaceId, HttpContext context, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var classes = await service.ListClassesAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new RestrictionClassList([.. classes.Select(ToResource)]));
    }

    internal static async Task<IResult> PutClassAsync(
        string workspaceId, string classKey, RestrictionClassRequest request, HttpContext context, DocumentSecurityService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var current = (await service.ListClassesAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => string.Equals(c.ClassKey, classKey, StringComparison.Ordinal));
        if (current is not null && EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.PutClassAsync(access.Principal, access.WorkspaceId, classKey, request ?? new(null, null, null), current?.Version,
            cancellationToken).ConfigureAwait(false);
        if (outcome.Value is not { } saved)
        {
            return Problem(outcome.Status, outcome.Errors, "No such restriction class.");
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(saved.Version);
        return outcome.Status == SecurityOutcomeStatus.Created
            ? TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{ClassesPath}/{saved.ClassKey}", ToResource(saved))
            : TypedResults.Ok(ToResource(saved));
    }

    internal static async Task<IResult> DeleteClassAsync(
        string workspaceId, string classKey, HttpContext context, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var current = (await service.ListClassesAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => string.Equals(c.ClassKey, classKey, StringComparison.Ordinal));
        if (current is null)
        {
            return Problems.NotFound("No such restriction class.");
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.DeleteClassAsync(access.Principal, access.WorkspaceId, classKey, current.Version, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SecurityOutcomeStatus.Ok
            ? TypedResults.NoContent()
            : Problem(outcome.Status, outcome.Errors, "No such restriction class.", "Built-in restriction classes cannot be deleted.");
    }

    // ----- Ethical walls -----

    internal static async Task<IResult> ListWallsAsync(string workspaceId, HttpContext context, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var walls = await service.ListWallsAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new EthicalWallList([.. walls.Select(ToResource)]));
    }

    internal static async Task<IResult> CreateWallAsync(
        string workspaceId, EthicalWallRequest request, HttpContext context, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.CreateWallAsync(access.Principal, access.WorkspaceId, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Value is not { } saved)
        {
            return Problem(outcome.Status, outcome.Errors, "No such ethical wall.", "An ethical wall with this name already exists.");
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(saved.Version);
        return TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{WallsPath}/{saved.WallId}", ToResource(saved));
    }

    internal static async Task<IResult> GetWallAsync(
        string workspaceId, string wallId, HttpContext context, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentWallAsync(context, wallId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound("No such ethical wall.");
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(current.Wall.Version);
        return TypedResults.Ok(ToResource(current.Wall));
    }

    internal static async Task<IResult> UpdateWallAsync(
        string workspaceId, string wallId, EthicalWallRequest request, HttpContext context, DocumentSecurityService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentWallAsync(context, wallId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound("No such ethical wall.");
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Wall.Version) is { } precondition)
        {
            return precondition;
        }

        // The service keeps the explicit documents the caller cannot see, so it needs the unredacted wall.
        var stored = await service.GetStoredWallAsync(current.Access.WorkspaceId, current.Wall.WallId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Problems.NotFound("No such ethical wall.");
        }

        var outcome = await service.UpdateWallAsync(current.Access.Principal, current.Access.WorkspaceId, stored, current.Wall.Version, request,
            cancellationToken).ConfigureAwait(false);
        if (outcome.Value is not { } saved)
        {
            return Problem(outcome.Status, outcome.Errors, "No such ethical wall.", "An ethical wall with this name already exists.");
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(saved.Version);
        return TypedResults.Ok(ToResource(saved));
    }

    internal static async Task<IResult> DeleteWallAsync(
        string workspaceId, string wallId, HttpContext context, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentWallAsync(context, wallId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound("No such ethical wall.");
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Wall.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.DeleteWallAsync(current.Access.Principal, current.Access.WorkspaceId, current.Wall, current.Wall.Version,
            cancellationToken).ConfigureAwait(false);
        return outcome.Status == SecurityOutcomeStatus.Ok ? TypedResults.NoContent() : Problem(outcome.Status, outcome.Errors, "No such ethical wall.");
    }

    private static async Task<(WorkspaceAccess Access, EthicalWallState Wall)?> CurrentWallAsync(
        HttpContext context, string wallId, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(wallId, out var id))
        {
            return null;
        }

        return await service.GetWallAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is { } wall
            ? (access, wall)
            : null;
    }

    // ----- Field restrictions -----

    internal static async Task<IResult> ListFieldRestrictionsAsync(
        string workspaceId, HttpContext context, DocumentSecurityService service, IFieldCatalogRepository fields, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var catalog = await fields.GetCatalogAsync(access.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restrictions = await service.ListFieldRestrictionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new FieldRestrictionList([.. restrictions
            .Where(r => catalog.Find(r.FieldId) is { IsDeleted: false })
            .Select(r => ToResource(r, catalog.Find(r.FieldId)!.Name))]));
    }

    internal static async Task<IResult> PutFieldRestrictionAsync(
        string workspaceId, string fieldId, FieldRestrictionRequest request, HttpContext context, DocumentSecurityService service,
        IFieldCatalogRepository fields, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id))
        {
            return Problems.NotFound("No such field.");
        }

        var current = (await service.ListFieldRestrictionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.FieldId == id);
        if (current is not null && EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.PutFieldRestrictionAsync(access.Principal, access.WorkspaceId, id, request ?? new(null, null), current?.Version,
            cancellationToken).ConfigureAwait(false);
        if (outcome.Value is not { } saved)
        {
            return Problem(outcome.Status, outcome.Errors, "No such field.");
        }

        var catalog = await fields.GetCatalogAsync(access.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = EntityTags.ForVersion(saved.Version);
        var resource = ToResource(saved, catalog.Find(id)?.Name ?? string.Empty);
        return outcome.Status == SecurityOutcomeStatus.Created
            ? TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{FieldRestrictionsPath}/{id.ToString(CultureInfo.InvariantCulture)}", resource)
            : TypedResults.Ok(resource);
    }

    internal static async Task<IResult> DeleteFieldRestrictionAsync(
        string workspaceId, string fieldId, HttpContext context, DocumentSecurityService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryFieldId(fieldId, out var id))
        {
            return Problems.NotFound("No such field restriction.");
        }

        var current = (await service.ListFieldRestrictionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.FieldId == id);
        if (current is null)
        {
            return Problems.NotFound("No such field restriction.");
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.DeleteFieldRestrictionAsync(access.Principal, access.WorkspaceId, id, current.Version, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SecurityOutcomeStatus.Ok ? TypedResults.NoContent() : Problem(outcome.Status, outcome.Errors, "No such field restriction.");
    }

    private static bool TryFieldId(string raw, out int fieldId) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out fieldId) && fieldId > 0;

    // ----- Break-glass -----

    internal static async Task<IResult> ListBreakGlassAsync(string workspaceId, HttpContext context, DocumentSecurityService service, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var activations = await service.ListBreakGlassAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return TypedResults.Ok(new BreakGlassActivationList([.. activations.Select(a => ToResource(a, now))]));
    }

    internal static async Task<IResult> ActivateBreakGlassAsync(
        string workspaceId, BreakGlassActivationRequest request, HttpContext context, DocumentSecurityService service, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.ActivateBreakGlassAsync(access.Principal, access.WorkspaceId, request ?? new(null), cancellationToken).ConfigureAwait(false);
        if (outcome.Value is not { } saved)
        {
            return Problem(outcome.Status, outcome.Errors, "No such activation.", "You already have an open break-glass activation in this workspace.");
        }

        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{BreakGlassPath}/{saved.ActivationId}", ToResource(saved, time.GetUtcNow()));
    }

    internal static async Task<IResult> EndBreakGlassAsync(
        string workspaceId, string activationId, HttpContext context, DocumentSecurityService service, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(activationId, out var id))
        {
            return Problems.NotFound("No such activation.");
        }

        var outcome = await service.EndBreakGlassAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return outcome.Value is { } saved
            ? TypedResults.Ok(ToResource(saved, time.GetUtcNow()))
            : Problem(outcome.Status, outcome.Errors, "No such activation.", "The activation has already ended.");
    }

    // ----- Mapping -----

    internal static RestrictionClassResource ToResource(RestrictionClassState c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new RestrictionClassResource(c.ClassKey, c.Definition.DisplayName, c.IsBuiltIn, [.. c.Definition.Roles.Select(r => r.Key())],
            c.Definition.Rules, c.UpdatedAt, c.Version);
    }

    internal static EthicalWallResource ToResource(EthicalWallState w)
    {
        ArgumentNullException.ThrowIfNull(w);
        var d = w.Definition;
        return new EthicalWallResource(w.WallId, d.Name, d.Description, new EthicalWallMembers(d.UserIds, d.Groups),
            new EthicalWallScope(d.DocumentIds, d.Custodians, d.Choices), w.UpdatedAt, w.Version);
    }

    internal static FieldRestrictionResource ToResource(FieldRestrictionState r, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new FieldRestrictionResource(r.FieldId, fieldName, [.. r.VisibleTo.Select(x => x.Key())], [.. r.EditableBy.Select(x => x.Key())],
            r.UpdatedAt, r.Version);
    }

    internal static BreakGlassActivationResource ToResource(BreakGlassActivationState a, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new BreakGlassActivationResource(a.ActivationId, a.UserId, a.UserDisplayName, a.Reason, a.ActivatedAt, a.ExpiresAt, a.EndedAt,
            a.EndedReason, a.EndedAt is null && a.ExpiresAt > now);
    }

    private static IResult Problem(
        SecurityOutcomeStatus status, IReadOnlyDictionary<string, string[]> errors, string notFound, string? conflict = null) => status switch
        {
            SecurityOutcomeStatus.Invalid => Problems.Validation(errors.ToDictionary()),
            SecurityOutcomeStatus.VersionConflict => Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
                "The resource was modified since it was read."),
            SecurityOutcomeStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, conflict ?? "Conflict."),
            SecurityOutcomeStatus.SelfProtection => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.SelfProtection,
                "This change applies to you; another administrator must make it."),
            _ => Problems.NotFound(notFound),
        };
}

public static class DocumentSecurityEndpointRegistration
{
    /// <summary>The document-security administration endpoints and use cases (the store comes with AddPostgresSecurityState).</summary>
    public static IServiceCollection AddDocumentSecurityEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<DocumentSecurityService>();
        services.AddSingleton<IApiEndpointModule, DocumentSecurityEndpoints>();
        return services;
    }
}
