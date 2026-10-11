using Opportunity.Api.Conventions;
using Opportunity.Api.Workspaces;
using Opportunity.Application.Audit;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// The acknowledgment roster as CSV through the protected-content gateway (E20-T03 "exportable roster"): every
/// acceptance with its version, text hash and time, and the direct members who accepted none. Writes a durable
/// <c>Security.AcknowledgmentRosterExported</c> before the first byte.
/// </summary>
public sealed class AcknowledgmentRosterContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(AcknowledgmentEndpoints.RosterPath + "/export", ExportAsync)
            .WithName("ExportAcknowledgmentRoster")
            .WithTags("Acknowledgments")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.WorkspaceManageAcknowledgments)
            .WithSummary("The acknowledgment roster as CSV (UTF-8 with BOM): one row per acceptance, plus pending direct members. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> ExportAsync(
        string workspaceId, HttpContext context, AcknowledgmentService service, IAuditEventWriter audit, TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var versions = await service.ListVersionsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var roster = await service.ExportRosterAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var (content, rows) = AcknowledgmentRosterCsv.Build(roster, versions.Count == 0 ? 0 : versions[0].Version);
        var auditEvent = service.RosterExportedEvent(access.Principal, access.WorkspaceId, rows, roster.Count);
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEvent.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(AcknowledgmentRosterCsv.FileName(time.GetUtcNow()));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Bytes(content, AcknowledgmentRosterCsv.ContentType);
    }
}

public static class AcknowledgmentRosterContentRegistration
{
    public static IServiceCollection AddAcknowledgmentRosterContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, AcknowledgmentRosterContentEndpoints>();
        return services;
    }
}
