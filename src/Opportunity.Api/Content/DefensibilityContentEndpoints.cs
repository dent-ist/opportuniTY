using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Conventions;
using Opportunity.Api.Defensibility;
using Opportunity.Application.Authorization;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Production.Defensibility;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// The defensibility downloads and the chain of custody through the protected-content gateway (E14-T05): the reports as
/// CSV, a document's chain of custody (JSON and CSV; it re-reads the document's stored bytes and load files to compare
/// them with the hashes recorded at import) and the report pack (ZIP). <c>Compliance.Report</c>; the chain of custody also
/// needs the document to be visible to the caller (else 404 as for a missing document). Each writes its durable audit
/// event before the first byte.
/// </summary>
public sealed class DefensibilityContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        Csv(ws.MapGet(DefensibilityEndpoints.ReviewSummaryPath + "/export", ReviewSummaryCsvAsync), "ExportReviewSummary",
            "The review population and coding summary as CSV. Audited.", DefensibilityEndpoints.AsOfDescription + " snapshotId as for the JSON form.");
        Csv(ws.MapGet(DefensibilityEndpoints.ProductionHistoryPath + "/export", ProductionHistoryCsvAsync), "ExportProductionHistory",
            "The production history as CSV: one row per production, then one per volume. Audited.", DefensibilityEndpoints.AsOfDescription);
        Csv(ws.MapGet(DefensibilityEndpoints.SearchTermHitsPath + "/export", SearchTermHitsCsvAsync), "ExportSearchTermHits",
            "The search term hit reports as CSV: one row per term and a totals row per report. Audited.", DefensibilityEndpoints.AsOfDescription);
        Csv(ws.MapGet(DefensibilityEndpoints.ChainOfCustodyPath + "/export", ChainOfCustodyCsvAsync), "ExportChainOfCustody",
            "A document's chain of custody as CSV. Re-reads the stored bytes. Audited.", DefensibilityEndpoints.AsOfDescription);

        ws.MapGet(DefensibilityEndpoints.ChainOfCustodyPath, ChainOfCustodyAsync)
            .WithName("GetChainOfCustody")
            .WithTags(DefensibilityEndpoints.Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ComplianceReport)
            .WithSummary("A document's chain of custody as of a point: import source and load file, hashes recorded at import next to the "
                + "hashes of the bytes re-read from storage now, derived artifacts and productions. Audited.")
            .WithDescription(DefensibilityEndpoints.AsOfDescription + " The native and text are re-read first, then load files and other objects, at "
                + "most 100 per report; a mismatch raises Integrity.HashMismatch. A document you may not view answers 404.")
            .Produces<ChainOfCustodyResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet(DefensibilityEndpoints.PackPath, PackAsync)
            .WithName("ExportDefensibilityPack")
            .WithTags(DefensibilityEndpoints.Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ComplianceReport)
            .WithSummary("The defensibility report pack (ZIP) as of a point: the reports, production manifests and QC reports, compliance "
                + "reports, the verifiable audit trail and manifest.json with every file's SHA-256. Audited.")
            .WithDescription(DefensibilityEndpoints.AsOfDescription + " from (optional, before asOf; default 30 days before it): start of the period "
                + "of the ethical-wall proof and the audit trail. snapshotId scopes the review summary. documentId (repeatable, at most 25): "
                + "documents whose chain of custody to include. The audit trail is included only for callers with Audit.Export (search text "
                + "only with Audit.ReadSearchText).")
            .Produces<Stream>(StatusCodes.Status200OK, DefensibilityPackService.ContentType)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void Csv(RouteHandlerBuilder builder, string name, string summary, string description) =>
        builder.WithName(name)
            .WithTags(DefensibilityEndpoints.Tag)
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ComplianceReport)
            .WithSummary(summary)
            .WithDescription(description)
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

    internal static async Task<IResult> ReviewSummaryCsvAsync(
        string workspaceId,
        [FromQuery(Name = "asOf")] DateTimeOffset? asOf,
        [FromQuery(Name = "snapshotId")] Guid? snapshotId,
        HttpContext context,
        DefensibilityReportService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (service.Point(asOf) is not { } point)
        {
            return DefensibilityEndpoints.InvalidAsOf();
        }

        var outcome = await service.ReviewSummaryAsync(access.Principal, access.WorkspaceId, point, snapshotId, DefensibilityFormat.Csv, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Report is not { } report)
        {
            return DefensibilityEndpoints.SnapshotNotFound();
        }

        return File(context, report.Statement, DefensibilityCsv.ReviewSummary(report));
    }

    internal static async Task<IResult> ProductionHistoryCsvAsync(
        string workspaceId,
        [FromQuery(Name = "asOf")] DateTimeOffset? asOf,
        HttpContext context,
        DefensibilityReportService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (service.Point(asOf) is not { } point)
        {
            return DefensibilityEndpoints.InvalidAsOf();
        }

        var report = await service.ProductionHistoryAsync(access.Principal, access.WorkspaceId, point, DefensibilityFormat.Csv, cancellationToken)
            .ConfigureAwait(false);
        return File(context, report.Statement, DefensibilityCsv.ProductionHistory(report));
    }

    internal static async Task<IResult> SearchTermHitsCsvAsync(
        string workspaceId,
        [FromQuery(Name = "asOf")] DateTimeOffset? asOf,
        HttpContext context,
        DefensibilityReportService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (service.Point(asOf) is not { } point)
        {
            return DefensibilityEndpoints.InvalidAsOf();
        }

        var report = await service.SearchTermHitsAsync(access.Principal, access.WorkspaceId, point, DefensibilityFormat.Csv, cancellationToken)
            .ConfigureAwait(false);
        return File(context, report.Statement, DefensibilityCsv.SearchTermHits(report));
    }

    internal static async Task<IResult> ChainOfCustodyAsync(
        string workspaceId,
        string documentId,
        [FromQuery(Name = "asOf")] DateTimeOffset? asOf,
        HttpContext context,
        DefensibilityReportService service,
        CancellationToken cancellationToken)
    {
        var (result, report) = await CustodyAsync(workspaceId, documentId, asOf, context, service, DefensibilityFormat.Json, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return result!;
        }

        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(DefensibilityMapping.ToResource(report));
    }

    internal static async Task<IResult> ChainOfCustodyCsvAsync(
        string workspaceId,
        string documentId,
        [FromQuery(Name = "asOf")] DateTimeOffset? asOf,
        HttpContext context,
        DefensibilityReportService service,
        CancellationToken cancellationToken)
    {
        var (result, report) = await CustodyAsync(workspaceId, documentId, asOf, context, service, DefensibilityFormat.Csv, cancellationToken)
            .ConfigureAwait(false);
        return report is null ? result! : File(context, report.Statement, DefensibilityCsv.ChainOfCustody(report), report.Data.DocumentId.ToString("N")[..8]);
    }

    internal static async Task<IResult> PackAsync(
        string workspaceId,
        [FromQuery(Name = "asOf")] DateTimeOffset? asOf,
        [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "snapshotId")] Guid? snapshotId,
        [FromQuery(Name = "documentId")] string[]? documentIds,
        HttpContext context,
        DefensibilityPackService packs,
        DefensibilityReportService service,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (service.Point(asOf) is not { } point)
        {
            return DefensibilityEndpoints.InvalidAsOf();
        }

        var documents = new List<Guid>();
        foreach (var text in documentIds ?? [])
        {
            if (!Guid.TryParse(text, out var id) || id == Guid.Empty)
            {
                return Problems.Validation(new Dictionary<string, string[]> { ["documentId"] = ["Each documentId must be a document ID."] });
            }

            documents.Add(id);
        }

        if (documents.Distinct().Count() > DefensibilityPackService.MaxDocuments)
        {
            return Problems.Validation(new Dictionary<string, string[]>
            {
                ["documentId"] = [$"At most {DefensibilityPackService.MaxDocuments} documents per pack."],
            });
        }

        // The audit trail only for callers who may export it, its search text only for those who may read it (Q-16);
        // checked without an audit event (it changes no decision).
        var permissions = await authorization.GetEffectivePermissionsAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var request = new DefensibilityPackRequest(point, from, snapshotId, documents, permissions.Permissions.Contains(Permission.AuditExport),
            permissions.Permissions.Contains(Permission.AuditReadSearchText));
        var outcome = await packs.PrepareAsync(access.Principal, access.WorkspaceId, request, context.RequestServices.GetService<IObjectStore>(), cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case DefensibilityPackStatus.InvalidPeriod:
                return Problems.Validation(new Dictionary<string, string[]> { ["from"] = ["from must be before asOf."] });
            case DefensibilityPackStatus.SnapshotNotFound:
                return DefensibilityEndpoints.SnapshotNotFound();
            case DefensibilityPackStatus.DocumentNotFound:
                return ProtectedContentGateway.DocumentNotFound();
        }

        var pack = outcome.Pack!;
        Headers(context, DefensibilityPackService.FileName(pack.Statement));
        context.Response.Headers[ProtectedContentGateway.RetrievalIdHeader] = pack.Statement.AuditEventId.ToString();
        return TypedResults.Stream(async body =>
        {
            // The ZIP writer makes some synchronous writes, which the response body refuses: spool to a temporary file
            // (deleted on close), then copy.
            var spool = new FileStream(Path.Combine(Path.GetTempPath(), "opp-pack-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await using (spool.ConfigureAwait(false))
            {
                await packs.WriteAsync(access.WorkspaceId, pack, spool, context.RequestAborted).ConfigureAwait(false);
                spool.Position = 0;
                await spool.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
            }
        }, DefensibilityPackService.ContentType);
    }

    private static async Task<(IResult? Result, ChainOfCustodyReport? Report)> CustodyAsync(
        string workspaceId, string documentId, DateTimeOffset? asOf, HttpContext context, DefensibilityReportService service, DefensibilityFormat format,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(documentId, out var id) || id == Guid.Empty)
        {
            return (ProtectedContentGateway.DocumentNotFound(), null);
        }

        if (service.Point(asOf) is not { } point)
        {
            return (DefensibilityEndpoints.InvalidAsOf(), null);
        }

        var outcome = await service.ChainOfCustodyAsync(access.Principal, access.WorkspaceId, id, point, context.RequestServices.GetService<IObjectStore>(),
            format, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            DefensibilityOutcomeStatus.Ok => (null, outcome.Report),
            DefensibilityOutcomeStatus.Forbidden => (Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
                "You do not have permission for this operation."), null),
            _ => (ProtectedContentGateway.DocumentNotFound(), null),
        };
    }

    private static FileContentHttpResult File(HttpContext context, DefensibilityStatement statement, byte[] content, string? suffix = null)
    {
        Headers(context, DefensibilityCsv.FileName(statement, suffix));
        context.Response.Headers[ProtectedContentGateway.RetrievalIdHeader] = statement.AuditEventId.ToString();
        return TypedResults.Bytes(content, DefensibilityCsv.ContentType);
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

public static class DefensibilityContentRegistration
{
    public static IServiceCollection AddDefensibilityContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, DefensibilityContentEndpoints>();
        return services;
    }
}
