using System.Text;

using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Compliance;
using Opportunity.Api.Conventions;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Authorization;
using Opportunity.Application.Compliance;
using Opportunity.Application.Storage;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// The compliance downloads through the protected-content gateway (E14-T06): the ethical-wall proof and the access
/// review as CSV, and the verifiable audit export (JSON Lines: manifest, public keys, signed checkpoints, purge records and
/// every sealed event of a checkpoint-aligned range of the workspace chain, verifiable offline with
/// <c>audit verify-export</c>). Each writes its durable audit event before the first byte.
/// </summary>
public sealed class ComplianceContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(ComplianceEndpoints.WallProofPath + "/export", WallProofAsync)
            .WithName("ExportWallProof")
            .WithTags(ComplianceEndpoints.Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ComplianceReport)
            .WithSummary("The ethical-wall proof for a period as CSV (UTF-8 with BOM): summary, walls, wall changes, then one row per attempt. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet(ComplianceEndpoints.AccessReviewPath + "/export", AccessReviewAsync)
            .WithName("ExportAccessReview")
            .WithTags(ComplianceEndpoints.Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ComplianceReport)
            .WithSummary("The workspace access review as CSV (UTF-8 with BOM): one row per user and per group. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet(ComplianceEndpoints.AuditExportPath, AuditExportAsync)
            .WithName("ExportAuditTrail")
            .WithTags(ComplianceEndpoints.Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.AuditExport)
            .WithSummary("The workspace audit trail as a verifiable export (JSON Lines) with its chain verification result. Audited as Audit.Exported.")
            .WithDescription("from (inclusive) and to (exclusive), ISO 8601, both optional (default: the whole chain). The file covers the "
                + "checkpoint-aligned chain range around the period, so it verifies on its own: `audit verify-export --file <file> "
                + "--public-key <pem>`. Search text is included only for callers with Audit.ReadSearchText; otherwise those events "
                + "are marked withheld and verified through the chain links and Merkle roots.")
            .Produces<Stream>(StatusCodes.Status200OK, AuditExportFormat.ContentType)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> WallProofAsync(
        string workspaceId,
        [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "to")] DateTimeOffset? to,
        HttpContext context,
        ComplianceReportService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (service.Period(from, to) is not { } period)
        {
            return ComplianceEndpoints.InvalidPeriod();
        }

        var report = await service.WallProofAsync(access.Principal, access.WorkspaceId, period, ComplianceReportFormat.Csv, cancellationToken)
            .ConfigureAwait(false);
        Headers(context, ComplianceCsv.WallProofFileName(report.GeneratedAt));
        return TypedResults.Bytes(ComplianceCsv.WallProof(report), ComplianceCsv.ContentType);
    }

    internal static async Task<IResult> AccessReviewAsync(
        string workspaceId, HttpContext context, ComplianceReportService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var report = await service.AccessReviewAsync(access.Principal, access.WorkspaceId, ComplianceReportFormat.Csv, cancellationToken)
            .ConfigureAwait(false);
        Headers(context, ComplianceCsv.AccessReviewFileName(report.GeneratedAt));
        return TypedResults.Bytes(ComplianceCsv.AccessReview(report), ComplianceCsv.ContentType);
    }

    internal static async Task<IResult> AuditExportAsync(
        string workspaceId,
        [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "to")] DateTimeOffset? to,
        HttpContext context,
        ComplianceReportService service,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (from is { } f && to is { } t && f >= t)
        {
            return ComplianceEndpoints.InvalidPeriod();
        }

        // Search text (Q-16) only for callers who may read it; checked without an audit event (it changes no decision).
        var permissions = await authorization.GetEffectivePermissionsAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var includeSearchText = permissions.Permissions.Contains(Permission.AuditReadSearchText);
        var export = await service.PrepareAuditExportAsync(access.Principal, access.WorkspaceId, from, to, includeSearchText, cancellationToken)
            .ConfigureAwait(false);

        Headers(context, AuditExportFormat.FileName(access.WorkspaceId, export.Manifest.GeneratedAt));
        context.Response.Headers[ProtectedContentGateway.RetrievalIdHeader] = export.Manifest.AuditEventId?.ToString();
        return TypedResults.Stream(async body =>
        {
            var writer = new StreamWriter(body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true);
            await using (writer.ConfigureAwait(false))
            {
                await service.WriteAuditExportAsync(access.WorkspaceId, export, writer, context.RequestAborted).ConfigureAwait(false);
            }
        }, AuditExportFormat.ContentType + "; charset=utf-8");
    }

    private static void Headers(HttpContext context, string fileName)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(fileName);
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
    }
}

public static class ComplianceContentRegistration
{
    public static IServiceCollection AddComplianceContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, ComplianceContentEndpoints>();
        return services;
    }
}
