using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Coding;
using Opportunity.Api.Conventions;
using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// The privilege conflict report as CSV through the protected-content gateway (E13-T02): <c>GET
/// …/privilege-conflicts/export</c> takes the report's parameters, holds exactly what the JSON report holds for the
/// caller (Q-52) and writes a durable <c>Privilege.ConflictReportExported</c> before the first byte.
/// </summary>
public sealed class PrivilegeConflictContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(PrivilegeConflictEndpoints.Path + "/export", ExportAsync)
            .WithName("ExportPrivilegeConflicts")
            .WithTags("Privilege")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .WithSummary("The privilege conflict report as CSV (UTF-8 with BOM), one row per listed document. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> ExportAsync(
        string workspaceId,
        [FromQuery(Name = "responsivenessField")] int? responsivenessField,
        [FromQuery(Name = "productionId")] Guid? productionId,
        HttpContext context,
        PrivilegeConflictService service,
        IAuditEventWriter audit,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.ReportAsync(access.Principal, access.WorkspaceId, responsivenessField, productionId, cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case PrivilegeConflictStatus.Ok:
                break;
            case PrivilegeConflictStatus.Invalid:
                return Problems.Validation(outcome.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()));
            case PrivilegeConflictStatus.Forbidden:
                return Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");
            default:
                return Problems.NotFound("No such production.");
        }

        var report = outcome.Report!;
        var principal = access.Principal;
        var auditEvent = new AuditEvent
        {
            OccurredAt = time.GetUtcNow(),
            WorkspaceId = access.WorkspaceId,
            Category = AuditTaxonomy.Privilege.Category,
            Action = AuditTaxonomy.Privilege.ConflictReportExported,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            Outcome = AuditOutcome.Success,
            CorrelationId = principal.CorrelationId,
            Details = new Dictionary<string, string?>
            {
                ["Groups"] = report.Groups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ResponsivenessFieldId"] = report.ResponsivenessFieldId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ProductionId"] = report.ProductionId?.ToString(),
            },
        };
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEvent.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(PrivilegeConflictReportCsv.FileName(report));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Bytes(PrivilegeConflictReportCsv.Build(report), PrivilegeConflictReportCsv.ContentType);
    }
}

public static class PrivilegeConflictContentRegistration
{
    public static IServiceCollection AddPrivilegeConflictContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, PrivilegeConflictContentEndpoints>();
        return services;
    }
}
