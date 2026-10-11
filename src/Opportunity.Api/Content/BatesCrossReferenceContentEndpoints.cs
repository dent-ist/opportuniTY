using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Conventions;
using Opportunity.Api.Productions;
using Opportunity.Application.Audit;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Productions;
using Opportunity.Production.Productions;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// The exportable Bates cross-reference (E13-T04) through the protected-content gateway:
/// <c>GET …/productions/bates-cross-reference</c> is a CSV of every produced (finalized or voided) production's Control
/// Number ↔ ProdBegBates/ProdEndBates/ProdBegAttach/ProdEndAttach rows with the production's volumes; <c>?productionId=</c>
/// narrows it to one production, <c>?documentId=</c> to the where-produced lookup of a document (its family and duplicates,
/// with their relation). PEP-1 requires <c>Production.Create</c>; documents the caller may not view are omitted entirely
/// (Q-52). Every download writes a durable <c>Production.CrossReferenceExported</c> before the first byte.
/// </summary>
public sealed class BatesCrossReferenceContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(ProductionEndpoints.ProductionsPath + "/bates-cross-reference", DownloadAsync)
            .WithName("DownloadBatesCrossReference")
            .WithTags("Productions")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ProductionCreate)
            .WithSummary("The Bates cross-reference as CSV (UTF-8 with BOM): every produced production's documents with their Bates ranges and volumes. Audited.")
            .WithDescription(
                "Without parameters the whole matter; productionId= one finalized or voided production; documentId= the where-produced lookup " +
                "of a document, its family and its duplicates (with a Relation column). Documents you may not view are omitted entirely.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> DownloadAsync(
        string workspaceId,
        [FromQuery(Name = "productionId")] string? productionId,
        [FromQuery(Name = "documentId")] string? documentId,
        HttpContext context,
        WhereProducedService service,
        IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (productionId is not null && documentId is not null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["documentId"] = ["Give a productionId or a documentId, not both."] });
        }

        var scope = CrossReferenceScope.Matter;
        Guid? production = null;
        Guid? document = null;
        WhereProducedResult? lookup = null;
        if (productionId is not null)
        {
            if (!Guid.TryParse(productionId, out var id) || !await service.IsProducedAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false))
            {
                return Problems.NotFound("No such finalized or voided production.");
            }

            (scope, production) = (CrossReferenceScope.Production, id);
        }
        else if (documentId is not null)
        {
            if (!Guid.TryParse(documentId, out var id) || id == Guid.Empty)
            {
                return Problems.NotFound(ProtectedContentGateway.DocumentNotFoundDetail);
            }

            var outcome = await service.LookupAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case WhereProducedStatus.Ok:
                    break;
                case WhereProducedStatus.Forbidden:
                    return Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");
                default:
                    return Problems.NotFound(ProtectedContentGateway.DocumentNotFoundDetail);
            }

            (scope, document, lookup) = (CrossReferenceScope.Document, id, outcome.Result);
        }

        // Built into a temporary file first (memory stays bounded for a matter-wide file), audited, then streamed.
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "opp-bates-xref-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        AuditEvent auditEvent;
        try
        {
            var rows = await service.WriteCsvAsync(access.Principal, access.WorkspaceId, production, lookup, temp, cancellationToken).ConfigureAwait(false);
            temp.Position = 0;
            auditEvent = service.ExportAudit(access.Principal, access.WorkspaceId, scope, production, document, rows);
            await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var controlNumber = lookup?.Documents.FirstOrDefault(d => d.DocumentId == document)?.ControlNumber;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEvent.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(service.FileName(scope, controlNumber));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(temp, WhereProducedService.ContentType);
    }
}

public static class BatesCrossReferenceContentRegistration
{
    public static IServiceCollection AddBatesCrossReferenceContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresWhereProducedStore();
        services.AddWhereProducedService();
        services.AddSingleton<IApiEndpointModule, BatesCrossReferenceContentEndpoints>();
        return services;
    }
}
