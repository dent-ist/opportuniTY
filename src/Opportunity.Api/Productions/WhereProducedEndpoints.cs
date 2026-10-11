using Microsoft.AspNetCore.Http.HttpResults;

using Opportunity.Api.Content;
using Opportunity.Api.Conventions;
using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Productions;
using Opportunity.Core.Security;
using Opportunity.Data.Productions;
using Opportunity.Production.Productions;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Productions;

/// <summary>
/// The where-produced lookup (E13-T04): <c>GET …/documents/{documentId}/where-produced</c> lists every finalized or
/// voided production, volume and Bates range in which the document, its family members and its duplicates were produced.
/// <c>Production.Create</c>, like every production route; the document itself needs <c>Document.View</c> (else 404 as
/// for a missing document) and related documents the caller may not view are omitted entirely (Q-52). The exportable
/// cross-reference is served by the protected-content gateway (<c>…/productions/bates-cross-reference</c>).
/// </summary>
public sealed class WhereProducedEndpoints : IApiEndpointModule
{
    public const string Path = "/documents/{documentId}/where-produced";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(Path, GetAsync)
            .RequirePermission(Permission.ProductionCreate)
            .WithName("GetWhereProduced")
            .WithTags("Productions")
            .WithSummary("Where a document, its family and its duplicates were produced: every finalized or voided production, volume and Bates range.")
            .WithDescription(
                "Drafts never issued numbers and are not listed. Related documents you may not view are omitted entirely (never listed or counted). " +
                "Download the same rows as CSV with GET …/productions/bates-cross-reference?documentId=.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Ok<WhereProducedResource>, ProblemHttpResult>> GetAsync(
        string workspaceId, string documentId, HttpContext context, WhereProducedService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(documentId, out var id) || id == Guid.Empty)
        {
            return Problems.NotFound(ProtectedContentGateway.DocumentNotFoundDetail);
        }

        var outcome = await service.LookupAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            WhereProducedStatus.Ok => TypedResults.Ok(ToResource(outcome.Result!)),
            WhereProducedStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation."),
            _ => Problems.NotFound(ProtectedContentGateway.DocumentNotFoundDetail),
        };
    }

    internal static WhereProducedResource ToResource(WhereProducedResult result)
    {
        var produced = result.Rows.GroupBy(r => r.DocumentId).ToDictionary(g => g.Key, g => g.Select(r => r.ProductionId).Distinct().Count());
        return new WhereProducedResource(
            result.DocumentId,
            [.. result.Documents.Select(d => new WhereProducedDocumentResource(d.DocumentId, d.ControlNumber, Relations(d.Relation),
                produced.GetValueOrDefault(d.DocumentId)))],
            [.. result.Rows.Select(r => new WhereProducedItemResource(
                r.ProductionId,
                r.ProductionName,
                r.ProductionVersion,
                Enum.Parse<ProductionStatusResource>(r.ProductionStatus.ToString()),
                r.FinalizedAt,
                r.VoidedAt,
                result.Volumes.GetValueOrDefault(r.ProductionId) ?? [],
                r.DocumentId,
                r.ControlNumber,
                Relations(result.RelationOf(r.DocumentId)),
                Output(r.Output),
                r.ProdBegBates,
                r.ProdEndBates,
                r.ProdBegAttach,
                r.ProdEndAttach))],
            result.Truncated);
    }

    private static List<WhereProducedRelationResource> Relations(WhereProducedRelation relation) =>
        [.. WhereProducedService.RelationNames(relation).Select(Enum.Parse<WhereProducedRelationResource>)];

    private static ProductionOutputResource Output(ProductionOutputKind kind) => kind switch
    {
        ProductionOutputKind.Native => ProductionOutputResource.Native,
        ProductionOutputKind.Placeholder => ProductionOutputResource.Placeholder,
        _ => ProductionOutputResource.Image,
    };
}

public static class WhereProducedEndpointRegistration
{
    /// <summary>The where-produced lookup (E13-T04); its CSV download is a gateway endpoint.</summary>
    public static IServiceCollection AddWhereProducedEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresWhereProducedStore();
        services.AddWhereProducedService();
        services.AddSingleton<IApiEndpointModule, WhereProducedEndpoints>();
        return services;
    }
}
