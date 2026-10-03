using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Fields;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Data.Fields;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Fields;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/fields</c> (E07-T02, ADR-007 R8): the field catalogue with query names and
/// search capabilities, so the grid enables only the sorts, filters and facets the projection can serve.
/// </summary>
public sealed class FieldEndpoints : IApiEndpointModule
{
    public const string Path = "/fields";

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
    }

    internal static async Task<Results<Ok<CursorPage<FieldResource>>, ProblemHttpResult>> ListFieldsAsync(
        string workspaceId, IFieldCatalogRepository fields, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws))
        {
            return Problems.NotFound("No such workspace.");
        }

        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = ToResources(catalog.Fields);
        return TypedResults.Ok(new CursorPage<FieldResource>(items, null, new TotalCount(items.Count, TotalRelation.Eq)));
    }

    internal static IReadOnlyList<FieldResource> ToResources(IEnumerable<FieldDefinition> definitions)
    {
        var ordered = definitions.Where(f => !f.IsDeleted).OrderBy(f => f.FieldId).ToList();
        var queryNames = FieldQueryNames.Assign(ordered);
        return [.. ordered.Select(f => new FieldResource(
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
            f.SearchSlot == FieldRules.OverflowSlot))];
    }

    private static FieldCapabilitiesResource ToResource(FieldCapabilities c) => new(
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
        services.AddSingleton<IApiEndpointModule, FieldEndpoints>();
        return services;
    }
}
