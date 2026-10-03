using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Opportunity.Api.Conventions;
using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;
using Opportunity.Hosting.Options;

namespace Opportunity.Api.Search;

/// <summary>Query-language limits (ADR-008 §5), section <c>Search</c>. Defaults are the ADR values.</summary>
public sealed class QueryLanguageOptions
{
    public const string SectionName = "Search";

    [Range(1, 100_000)]
    public int MaxQueryLength { get; set; } = QueryLimits.Default.MaxLength;

    [Range(1, 100_000)]
    public int MaxQueryNodes { get; set; } = QueryLimits.Default.MaxNodes;

    [Range(1, 256)]
    public int MaxQueryDepth { get; set; } = QueryLimits.Default.MaxDepth;

    [Range(1, 100_000)]
    public int MaxQueryTerms { get; set; } = QueryLimits.Default.MaxTerms;

    [Range(0, 10_000)]
    public int MaxWildcardTerms { get; set; } = QueryLimits.Default.MaxWildcardTerms;

    [Range(1, 100_000)]
    public int MaxProximityDistance { get; set; } = QueryLimits.Default.MaxProximityDistance;

    [Range(1, 1_000_000)]
    public int MaxWildcardExpansion { get; set; } = QueryLimits.Default.MaxWildcardExpansion;

    [Range(0, 100)]
    public int MinWildcardPrefix { get; set; } = QueryLimits.Default.MinWildcardPrefix;

    public QueryLimits ToLimits() => new()
    {
        MaxLength = MaxQueryLength,
        MaxNodes = MaxQueryNodes,
        MaxDepth = MaxQueryDepth,
        MaxTerms = MaxQueryTerms,
        MaxWildcardTerms = MaxWildcardTerms,
        MaxProximityDistance = MaxProximityDistance,
        MaxWildcardExpansion = MaxWildcardExpansion,
        MinWildcardPrefix = MinWildcardPrefix,
    };
}

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceId}/query-validations</c>: parses query text and returns the AST or positioned
/// errors for the query bar (E07-T06, UI E16). Workspace-scoped so that binding (fields, choices, zone) can join later
/// without a new route.
/// </summary>
public sealed class QueryValidationEndpoints : IApiEndpointModule
{
    public const string Path = "/query-validations";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapPost(Path, (string workspaceId, QueryValidationRequest request, QueryValidator validator) =>
            {
                // Parsing is workspace-independent (ADR-008 R15); the route still carries the workspace authorization.
                _ = workspaceId;
                if (request.Query is null)
                {
                    return (IResult)Problems.Validation(
                        new Dictionary<string, string[]> { ["query"] = ["The query text is required (it may be empty)."] });
                }

                return TypedResults.Ok(validator.Validate(request.Query));
            })
            .WithName("ValidateQuery")
            .WithSummary("Parse query-language text and return its AST, normalized form and positioned errors.")
            .Produces<QueryValidationResult>()
            .ProducesValidationProblem();
    }
}

public static class QueryValidationRegistration
{
    public static IServiceCollection AddQueryValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddValidatedOptions<QueryLanguageOptions>(QueryLanguageOptions.SectionName);
        services.AddSingleton(sp => new QueryValidator(sp.GetRequiredService<IOptions<QueryLanguageOptions>>().Value.ToLimits()));
        services.AddSingleton<IApiEndpointModule, QueryValidationEndpoints>();
        return services;
    }
}
