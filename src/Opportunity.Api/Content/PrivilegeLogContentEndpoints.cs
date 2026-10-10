using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Conventions;
using Opportunity.Api.Productions;
using Opportunity.Application.Audit;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Production.PrivilegeLogs;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// A privilege log version as CSV or XLSX through the protected-content gateway (E13-T03): <c>GET
/// …/privilege-logs/{logId}/content?format=csv|xlsx</c> renders the file again from the version's frozen metadata and
/// rows, checks it against the SHA-256 recorded with the version (a mismatch sends nothing: 500) and writes a durable
/// <c>Privilege.LogDownloaded</c> before the first byte. A version the caller may not read answers like a missing one.
/// </summary>
public sealed class PrivilegeLogContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(PrivilegeLogEndpoints.LogsPath + "/{logId}/content", DownloadAsync)
            .WithName("DownloadPrivilegeLog")
            .WithTags("Privilege logs")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .WithSummary("A privilege log version as CSV (UTF-8 with BOM, default) or XLSX, byte-identical on every download. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, PrivilegeLogFiles.CsvContentType, PrivilegeLogFiles.XlsxContentType)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    internal static async Task<IResult> DownloadAsync(
        string workspaceId,
        string logId,
        [FromQuery(Name = "format")] string? format,
        HttpContext context,
        PrivilegeLogService service,
        IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        format ??= "csv";
        if (await PrivilegeLogEndpoints.ReadableAsync(context, logId, service, cancellationToken).ConfigureAwait(false) is not { } log)
        {
            return Problems.NotFound("No such privilege log.");
        }

        var outcome = await service.RenderAsync(log, format, cancellationToken).ConfigureAwait(false);
        if (outcome.Status == PrivilegeLogStatus.Invalid)
        {
            return PrivilegeLogEndpoints.Problem(outcome.Status, outcome.Errors, outcome.Reason, "No such privilege log.");
        }

        var ok = outcome.Status == PrivilegeLogStatus.Ok;
        var auditEvent = service.DownloadEvent(access.Principal, access.WorkspaceId, log.LogId, format.ToLowerInvariant(),
            ok ? AuditOutcome.Success : AuditOutcome.Failure, ok ? null : "sha256-mismatch");
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return PrivilegeLogEndpoints.Problem(outcome.Status, outcome.Errors, outcome.Reason, "No such privilege log.");
        }

        var file = outcome.Value!;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEvent.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(file.FileName);
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["X-Content-SHA256"] = Convert.ToHexStringLower(file.Format == "csv" ? log.CsvSha256 : log.XlsxSha256);
        return TypedResults.Bytes(file.Content, file.ContentType);
    }
}

public static class PrivilegeLogContentRegistration
{
    public static IServiceCollection AddPrivilegeLogContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, PrivilegeLogContentEndpoints>();
        return services;
    }
}
