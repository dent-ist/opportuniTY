using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Fields;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Fields;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/fields</c> (E07-T02, ADR-007 R8): the field catalogue with query names and
/// search capabilities, so the grid enables only the sorts, filters and facets the projection can serve, and the choices
/// of choice fields for query-bar suggestions (#186). Fields the caller may not see (<see cref="IFieldAccessFilter"/>,
/// E05-T06) are omitted with their choices.
/// <c>GET …/coding-layouts</c> (E04-T03, E16-T05): the coding layouts the caller's roles may use, for the coding pane.
/// </summary>
public sealed class FieldEndpoints : IApiEndpointModule
{
    public const string Path = "/fields";
    public const string LayoutsPath = "/coding-layouts";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(Path, ListFieldsAsync)
            .WithName("ListFields")
            .WithTags("Fields")
            .WithSummary("The workspace's fields with query names and search capabilities (sortable, filterable, aggregatable, …).")
            .WithDescription("Every member can read the catalogue: reviewers need it for the grid, the query bar and coding.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        routes.Workspace.MapGet(LayoutsPath, ListCodingLayoutsAsync)
            .WithName("ListCodingLayouts")
            .WithTags("Fields")
            .WithSummary("The coding layouts you may use: sections, field order, required, read-only and conditional fields.")
            .WithDescription(
                "The default layout first, then the layouts assigned to one of your roles (Workspace Admins see every layout), " +
                "by name. Fields you may not see are omitted. Field names, types and choices come from GET …/fields.")
            .RequirePermission(Permission.DocumentView)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Ok<CursorPage<CodingLayoutResource>>, ProblemHttpResult>> ListCodingLayoutsAsync(
        string workspaceId,
        HttpContext context,
        IFieldCatalogRepository fields,
        IFieldAccessFilter access,
        ISecurityStateReader security,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } caller)
        {
            return Problems.NotFound("No such workspace.");
        }

        var state = await security.ReadAsync(caller.WorkspaceId, caller.Principal, includePrincipal: true, documentIds: null, cancellationToken)
            .ConfigureAwait(false);
        var roles = state.Principal?.Roles.Where(r => r != WorkspaceRole.BreakGlass).ToHashSet() ?? [];
        var layouts = await fields.GetLayoutsAsync(caller.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var catalog = await fields.GetCatalogAsync(caller.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await access.RestrictedFieldIdsAsync(caller.WorkspaceId, caller.Principal, catalog, cancellationToken).ConfigureAwait(false);
        var items = UsableLayouts(layouts, roles).Select(l => ToResource(l, restricted)).ToList();
        return TypedResults.Ok(new CursorPage<CodingLayoutResource>(items, null, new TotalCount(items.Count, TotalRelation.Eq)));
    }

    /// <summary>The default layout, and those assigned to one of <paramref name="roles"/>; Workspace Admins use every layout.</summary>
    internal static IEnumerable<CodingLayout> UsableLayouts(IEnumerable<CodingLayout> layouts, IReadOnlySet<WorkspaceRole> roles)
    {
        var keys = roles.Select(r => r.Key()).ToHashSet(StringComparer.Ordinal);
        return layouts
            .Where(l => l.IsDefault || roles.Contains(WorkspaceRole.WorkspaceAdmin) || l.Roles.Any(keys.Contains))
            .OrderByDescending(l => l.IsDefault)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase);
    }

    internal static CodingLayoutResource ToResource(CodingLayout layout, IReadOnlySet<int> restricted) => new(
        layout.LayoutId,
        layout.Name,
        layout.IsDefault,
        [.. layout.Sections.Select(s => new CodingLayoutSectionResource(
            s.SectionId,
            s.Title,
            [.. s.Fields.Where(f => !restricted.Contains(f.FieldId)).Select(f => new CodingLayoutFieldResource(
                f.FieldId,
                f.IsRequired,
                f.IsReadOnly,
                f.VisibleWhen is { } c ? new CodingLayoutConditionResource(c.FieldId, c.ChoiceIds, c.BooleanValue) : null,
                f.ApplyToFamilyByDefault))]))]);

    internal static async Task<Results<Ok<CursorPage<FieldResource>>, ProblemHttpResult>> ListFieldsAsync(
        string workspaceId, HttpContext context, IFieldCatalogRepository fields, IFieldAccessFilter access, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } caller)
        {
            return Problems.NotFound("No such workspace.");
        }

        var catalog = await fields.GetCatalogAsync(caller.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await access.RestrictedFieldIdsAsync(caller.WorkspaceId, caller.Principal, catalog, cancellationToken).ConfigureAwait(false);
        var items = ToResources(catalog, restricted);
        return TypedResults.Ok(new CursorPage<FieldResource>(items, null, new TotalCount(items.Count, TotalRelation.Eq)));
    }

    internal static IReadOnlyList<FieldResource> ToResources(FieldCatalog catalog, IReadOnlySet<int> restricted)
    {
        // Query names are assigned over every live field, so omitting a restricted one never renames another.
        var ordered = catalog.Fields.Where(f => !f.IsDeleted).OrderBy(f => f.FieldId).ToList();
        var queryNames = FieldQueryNames.Assign(ordered);
        return [.. ordered.Where(f => !restricted.Contains(f.FieldId)).Select(f => new FieldResource(
            f.FieldId,
            f.Name,
            queryNames[f.FieldId],
            Enum.Parse<FieldResourceType>(f.Type.ToString()),
            Enum.Parse<FieldResourceStorage>(f.Storage.ToString()),
            f.IsMultiValue,
            f.IsSystem,
            f.IsHidden,
            f.IsSecurityAffecting,
            f.DatePrecision is { } p ? Enum.Parse<FieldResourceDatePrecision>(p.ToString()) : null,
            ToResource(f.Capabilities),
            f.SearchSlot == FieldRules.OverflowSlot,
            f.Type is FieldType.SingleChoice or FieldType.MultiChoice
                ? [.. catalog.ChoicesOf(f.FieldId).Select(c => new FieldChoiceResource(c.ChoiceId, c.Name, c.IsActive, c.SystemKey))]
                : null))];
    }

    internal static FieldCapabilitiesResource ToResource(FieldCapabilities c) => new(
        c.HasFlag(FieldCapabilities.Sortable),
        c.HasFlag(FieldCapabilities.Filterable),
        c.HasFlag(FieldCapabilities.Rangeable),
        c.HasFlag(FieldCapabilities.Aggregatable),
        c.HasFlag(FieldCapabilities.FullText),
        c.HasFlag(FieldCapabilities.Wildcard),
        c.HasFlag(FieldCapabilities.LeadingWildcard),
        c.HasFlag(FieldCapabilities.Highlightable),
        c.HasFlag(FieldCapabilities.Exists));
}

public static class FieldEndpointRegistration
{
    public static IServiceCollection AddFieldEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IApiEndpointModule, FieldEndpoints>();
        services.AddSingleton<IApiEndpointModule, FieldAdminEndpoints>();
        return services;
    }
}
