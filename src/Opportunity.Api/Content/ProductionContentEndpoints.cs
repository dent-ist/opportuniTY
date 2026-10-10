using Opportunity.Api.Conventions;
using Opportunity.Api.Productions;
using Opportunity.Application.Audit;
using Opportunity.Application.Productions;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Production.Productions;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// The QC report of a production's latest QC run (E12-T07, <c>GET …/productions/{productionId}/qc/report?format=csv|pdf</c>;
/// a finalized production's is the run it was finalized with) and the re-designation overlay load file of a finalized
/// production through the protected-content gateway (E12-T04):
/// <c>GET …/productions/{productionId}/redesignation-overlay</c>. PEP-1 requires <c>Production.Create</c>. A DAT in
/// the production's delimiters and encoding with ProdBegBates, ProdEndBates and the designation each produced document
/// should now carry; documents the caller may not view are left out (Q-52; the JSON report counts them). Every download
/// writes a durable <c>Production.RedesignationExported</c> before the first byte.
/// </summary>
public sealed class ProductionContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.Workspace.MapGroup(ProductionEndpoints.ProductionsPath)
            .WithTags("Productions")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ProductionCreate);
        group.MapGet("/{productionId}/qc/report", DownloadQcReportAsync)
            .WithName("DownloadProductionQcReport")
            .WithSummary("The QC report of the production's latest QC run as CSV (default) or PDF (?format=pdf): checks, overrides with their reasons, and exceptions. Audited.")
            .WithDescription("Documents you may not view are counted, not listed. A finalized production's report is the run it was finalized with.")
            .Produces<Stream>(StatusCodes.Status200OK, ContentDispositionHeader.OctetStream)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{productionId}/redesignation-overlay", DownloadOverlayAsync)
            .WithName("DownloadProductionRedesignationOverlay")
            .WithSummary("The re-designation overlay load file (DAT): ProdBegBates, ProdEndBates and the designation each re-designated document now carries. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, ContentDispositionHeader.OctetStream)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> DownloadQcReportAsync(
        string workspaceId, string productionId, string? format, HttpContext context, IProductionStore productions, ProductionService service,
        IAuditEventWriter audit, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        var kind = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        if (kind is not ("csv" or "pdf"))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["format"] = ["Use csv or pdf."] });
        }

        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(productionId, out var id)
            || await productions.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } production
            || await service.GetQcAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } qc)
        {
            return Problems.NotFound("No such production, or its QC checks have not run.");
        }

        var auditEvent = service.QcReportExportAudit(access.Principal, production, qc, kind);
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);

        // Built into a temporary file first, then streamed; deleted on close.
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "opp-qc-report-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            if (kind == "pdf")
            {
                await service.WriteQcReportPdfAsync(access.Principal, production, qc, temp, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await service.WriteQcReportCsvAsync(access.Principal, production, qc, temp, cancellationToken).ConfigureAwait(false);
            }

            temp.Position = 0;
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEvent.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(ProductionService.QcReportFileName(production, kind));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(temp, ContentDispositionHeader.OctetStream);
    }

    internal static async Task<IResult> DownloadOverlayAsync(
        string workspaceId, string productionId, HttpContext context, IProductionStore productions, ProductionService service, IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(productionId, out var id)
            || await productions.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } production)
        {
            return Problems.NotFound("No such production.");
        }

        if (await service.RedesignationRefusalAsync(production, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, refusal.Reason ?? "The production has no re-designation report.");
        }

        var auditEvent = service.RedesignationExportAudit(access.Principal, production);
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);

        // Built into a temporary file first, then streamed; deleted on close.
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "opp-redesignation-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await service.WriteRedesignationOverlayAsync(access.Principal, production, temp, cancellationToken).ConfigureAwait(false);
            temp.Position = 0;
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEvent.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(ProductionService.OverlayFileName(production));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(temp, ContentDispositionHeader.OctetStream);
    }
}

public static class ProductionContentRegistration
{
    public static IServiceCollection AddProductionContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, ProductionContentEndpoints>();
        services.AddProductionVolumeContentEndpoints();
        return services;
    }
}
