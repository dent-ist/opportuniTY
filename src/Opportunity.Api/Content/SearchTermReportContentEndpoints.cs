using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Conventions;
using Opportunity.Api.Search;
using Opportunity.Application.Audit;
using Opportunity.Application.Search.TermReports;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.SearchTermReports;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// Search term report exports through the protected-content gateway (E07-T10, Q-30): <c>GET
/// …/search-term-reports/{reportId}/export?format=csv|xlsx</c>. PEP-1 requires <c>SearchTermReport.Run</c>; the report
/// must be visible to the caller (its creator or <c>Job.ViewAll</c>) and completed. Every download writes a durable
/// <c>Search.TermReportExported</c> before the first byte. The file states the snapshot, search generation and whether
/// the index was current at execution.
/// </summary>
public sealed class SearchTermReportContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGroup(SearchTermReportEndpoints.Path)
            .WithTags("Search Term Reports")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.SearchTermReportRun)
            .MapGet("/{reportId}/export", ExportAsync)
            .WithName("ExportSearchTermReport")
            .WithSummary("The completed report as CSV (UTF-8 with BOM) or XLSX: provenance, one row per term, totals. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv", SearchTermReportExport.XlsxContentType)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> ExportAsync(
        string workspaceId, string reportId, [FromQuery] string? format, HttpContext context, SearchTermReportService reports,
        ISearchTermReportStore store, IAuditEventWriter audit, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        var kind = (format ?? "csv").ToUpperInvariant();
        if (kind is not ("CSV" or "XLSX"))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["format"] = ["The format is csv or xlsx."] });
        }

        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(reportId, out var id)
            || await reports.GetVisibleAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } report)
        {
            return Problems.NotFound("No such search term report.");
        }

        if (report.Status != SearchTermReportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The report has not completed.");
        }

        var terms = await store.GetTermsAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        var auditEvent = reports.ExportAudit(access.Principal, report, kind.ToLowerInvariant());
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);

        var csv = kind == "CSV";
        var bytes = csv ? SearchTermReportExport.Csv(report, terms) : SearchTermReportExport.Xlsx(report, terms);
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEvent.EventId.ToString();
        response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(SearchTermReportExport.FileName(report, csv ? "csv" : "xlsx"));
        response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Bytes(bytes, csv ? SearchTermReportExport.CsvContentType : SearchTermReportExport.XlsxContentType);
    }
}

public static class SearchTermReportContentRegistration
{
    public static IServiceCollection AddSearchTermReportContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, SearchTermReportContentEndpoints>();
        return services;
    }
}
