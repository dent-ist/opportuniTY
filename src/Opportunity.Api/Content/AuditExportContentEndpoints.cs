using Opportunity.Api.Audit;
using Opportunity.Api.Conventions;
using Opportunity.Application.Audit;
using Opportunity.Application.Storage;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// An audit export's CSV through the protected-content gateway (E14-T04): <c>GET …/audit-exports/{exportId}/content</c>.
/// PEP-1 requires <c>Audit.Read</c>; the export must be the caller's own and completed, the caller must still hold
/// <c>Audit.ReadSearchText</c> when it includes search text (access to documents was re-checked when the export ran, as
/// for document exports, Q-15). Every download writes a durable <c>Audit.ExportDownloaded</c> before the first byte; the
/// file is streamed part by part, never held whole in memory.
/// </summary>
public sealed class AuditExportContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(AuditLogEndpoints.ExportsPath + "/{exportId}/content", DownloadAsync)
            .WithName("DownloadAuditExport")
            .WithTags("Audit")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.AuditRead)
            .WithSummary("The completed audit export as CSV (UTF-8 with BOM), oldest event first. Audited (Audit.ExportDownloaded).")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> DownloadAsync(
        string workspaceId, string exportId, HttpContext context, AuditLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(exportId, out var id))
        {
            return Problems.NotFound("No such audit export.");
        }

        var outcome = await service.OpenDownloadAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != AuditLogStatus.Ok)
        {
            return AuditLogEndpoints.Problem(outcome.Status, null, outcome.Detail);
        }

        var export = outcome.Export!;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = outcome.AuditEventId!.Value.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(AuditExportCsv.FileName(export.ExportId, export.CreatedAt));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        if (export.ByteCount is { } length)
        {
            response.ContentLength = length;
        }

        return TypedResults.Stream(output => service.WriteFileAsync(export, output, context.RequestAborted), AuditExportCsv.ContentType);
    }
}

public static class AuditExportContentRegistration
{
    public static IServiceCollection AddAuditExportContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, AuditExportContentEndpoints>();
        return services;
    }
}
