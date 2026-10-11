using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Conventions;
using Opportunity.Application.Defensibility;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Defensibility;
using Opportunity.Production.Defensibility;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Defensibility;

/// <summary>
/// The defensibility reports (E14-T05) as JSON for Admin › Compliance › Defensibility: the review population and coding
/// summary, the production history and the search term hit reports, each as of a point (<c>asOf</c>, default now) with
/// its fingerprint. <c>Compliance.Report</c>; every generation is audited (<c>Audit.DefensibilityReportGenerated</c>).
/// The CSV downloads, a document's chain of custody (it re-reads stored bytes) and the report pack are gateway endpoints
/// (<c>Content/DefensibilityContentEndpoints</c>).
/// </summary>
public sealed class DefensibilityEndpoints : IApiEndpointModule
{
    public const string ReviewSummaryPath = "/defensibility/review-summary";
    public const string ProductionHistoryPath = "/defensibility/production-history";
    public const string SearchTermHitsPath = "/defensibility/search-term-hits";
    public const string ChainOfCustodyPath = "/documents/{documentId}/chain-of-custody";
    public const string PackPath = "/defensibility/pack";

    public const string Tag = "Defensibility";

    public const string AsOfDescription =
        "asOf (ISO 8601, optional, not in the future): the data-as-of point. The report reads only what was recorded at or before it, so the "
        + "same asOf gives the same report and fingerprint. Without it the report is as of the moment it is generated (statement.asOf).";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(ReviewSummaryPath, ReviewSummaryAsync)
            .WithName("GetReviewSummary")
            .WithTags(Tag)
            .Produces<ReviewSummaryResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Review population and coding summary by reviewer and date, as of a point. Audited.")
            .WithDescription(AsOfDescription + " snapshotId (optional): a Ready snapshot whose members are the population; otherwise every "
                + "document the workspace held at the point. At most 20,000 reviewer days are listed (the totals cover all).")
            .RequirePermission(Permission.ComplianceReport);

        ws.MapGet(ProductionHistoryPath, ProductionHistoryAsync)
            .WithName("GetProductionHistory")
            .WithTags(Tag)
            .Produces<ProductionHistoryResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Production history as of a point: volumes, Bates ranges, counts, hashes and QC results. Audited.")
            .WithDescription(AsOfDescription + " A production voided after the point is shown as finalized.")
            .RequirePermission(Permission.ComplianceReport);

        ws.MapGet(SearchTermHitsPath, SearchTermHitsAsync)
            .WithName("GetSearchTermHits")
            .WithTags(Tag)
            .Produces<SearchTermHitsResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Search term hit reports completed by a point, each with its snapshot, search generation and index lag at execution. Audited.")
            .WithDescription(AsOfDescription + " At most 500 reports are listed (the oldest first).")
            .RequirePermission(Permission.ComplianceReport);
    }

    internal static async Task<IResult> ReviewSummaryAsync(
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
            return InvalidAsOf();
        }

        var outcome = await service.ReviewSummaryAsync(access.Principal, access.WorkspaceId, point, snapshotId, DefensibilityFormat.Json, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Report is not { } report)
        {
            return SnapshotNotFound();
        }

        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(DefensibilityMapping.ToResource(report));
    }

    internal static async Task<IResult> ProductionHistoryAsync(
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
            return InvalidAsOf();
        }

        var report = await service.ProductionHistoryAsync(access.Principal, access.WorkspaceId, point, DefensibilityFormat.Json, cancellationToken)
            .ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(DefensibilityMapping.ToResource(report));
    }

    internal static async Task<IResult> SearchTermHitsAsync(
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
            return InvalidAsOf();
        }

        var report = await service.SearchTermHitsAsync(access.Principal, access.WorkspaceId, point, DefensibilityFormat.Json, cancellationToken)
            .ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(DefensibilityMapping.ToResource(report));
    }

    internal static IResult InvalidAsOf() =>
        Problems.Validation(new Dictionary<string, string[]> { ["asOf"] = ["asOf must not be in the future."] });

    internal static IResult SnapshotNotFound() =>
        Problems.Validation(new Dictionary<string, string[]> { ["snapshotId"] = ["The snapshot does not exist or is no longer kept."] });
}

/// <summary>Report → API resource mapping, shared with the gateway endpoints.</summary>
public static class DefensibilityMapping
{
    public static DefensibilityStatementResource ToResource(DefensibilityStatement s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Report.ToString(), s.AsOf, s.SnapshotId, s.SearchGeneration, s.GeneratedAt, s.GeneratedBy, s.Fingerprint, s.AuditEventId);
    }

    public static ReviewSummaryResource ToResource(ReviewSummaryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var p = report.Data.Population;
        return new ReviewSummaryResource(
            ToResource(report.Statement),
            report.Snapshot is { } s ? new DefensibilitySnapshotResource(s.SnapshotId, s.Name, s.Purpose, s.Documents, s.MaterializedAt) : null,
            new ReviewPopulationResource(p.Documents, p.Reviewed, p.NotReviewed, p.SystemCodedOnly, p.BulkSkips),
            [.. report.Data.Reviewers.Select(r => new ReviewerCodingResource(r.ActorId, r.DisplayName, r.Documents, r.Changes, r.InteractiveChanges,
                r.BulkChanges, r.SystemChanges, r.FirstAt, r.LastAt))],
            [.. report.Data.Days.Select(d => new ReviewerDayResource(d.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                d.ActorId, d.DisplayName, d.Documents, d.Changes, d.InteractiveChanges, d.BulkChanges, d.SystemChanges))],
            report.Data.DaysTruncated);
    }

    public static ProductionHistoryResource ToResource(ProductionHistoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ProductionHistoryResource(ToResource(report.Statement), [.. report.Productions.Select(p => new ProductionHistoryEntryResource(
            p.ProductionId,
            p.Name,
            p.Version,
            p.Voided ? "Voided" : "Finalized",
            p.SnapshotId,
            p.FinalizedAt,
            p.FinalizedByDisplay ?? p.FinalizedBy.ToString("D"),
            p.VoidedAt,
            p.VoidReason,
            p.BatesBegin,
            p.BatesEnd,
            p.Documents,
            p.Units,
            Hex(p.SpecificationSha256),
            p.AssignmentsSha256 is { } a ? Hex(a) : null,
            Hex(p.ManifestSha256),
            p.Qc is { } qc
                ? new ProductionQcHistoryResource(qc.QcRunId, qc.RunAt, qc.Outcome, qc.WarningsAcknowledged, Hex(qc.ReportSha256),
                    [.. qc.Checks.Select(c => new ProductionQcCheckHistoryResource(c.Check, c.Severity, c.Status, c.Documents))])
                : null,
            [.. p.Volumes.Select(v => new ProductionVolumeHistoryResource(v.VolumeId, DefensibilityCsv.VolumeName(v.SettingsJson), v.CompletedAt, v.Documents,
                v.Pages, v.Files, v.Bytes, Hex(v.ManifestSha256),
                v.VerificationPassed switch { true => "Passed", false => "Failed", null => "NotRecorded" }, v.VerifiedDocuments,
                v.VerificationReportSha256 is { } h ? Hex(h) : null))]))]);
    }

    public static SearchTermHitsResource ToResource(SearchTermHitsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new SearchTermHitsResource(ToResource(report.Statement), [.. report.Reports.Select(e =>
        {
            var r = e.Report;
            return new SearchTermHitReportResource(
                r.ReportId,
                r.Name,
                r.RunCount,
                DefensibilityCsv.Scope(r),
                r.SnapshotId,
                r.SearchGeneration,
                r.IndexCurrent,
                r.IndexPendingChanges,
                r.IndexLag is { } lag ? Math.Round(lag.TotalSeconds, 3) : null,
                r.ExecutedByDisplay,
                r.ExecutedAt,
                r.CompletedAt,
                new SearchTermReportTotals(r.DocumentsInScope ?? 0, r.DocumentsWithHits ?? 0, r.DocumentsWithHitsIncludingFamily ?? 0,
                    r.DocumentsWithoutHits ?? 0),
                [.. e.Terms.Select(t => new SearchTermReportTerm(t.TermId, t.Name, t.Expression,
                    t.Error is { } err ? new SearchTermReportTermError(err.Code, err.Message, err.Position) : null,
                    t.DocumentsWithHits, t.DocumentsWithHitsIncludingFamily, t.UniqueHits, t.UniqueHitsIncludingFamily))]);
        })], report.Truncated);
    }

    public static ChainOfCustodyResource ToResource(ChainOfCustodyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var d = report.Data;
        return new ChainOfCustodyResource(
            ToResource(report.Statement),
            d.DocumentId,
            d.ControlNumber,
            d.FileName,
            d.FileSize,
            d.CreatedAt,
            new CustodyHashesResource(Opt(d.Sha256), Opt(d.Sha1), Opt(d.Md5), d.UpstreamHash),
            report.Verified,
            report.Mismatches,
            [.. report.Imports.Select(i => new CustodyImportResource(i.Import.ImportId, i.Import.Name, i.Import.Mode, i.Import.LoadFileName,
                Hex(i.Import.LoadFileSha256), i.Import.LoadFileSize, Verification(i.Verification), i.Import.RowNumber, i.Import.Action,
                i.Import.StartedByDisplay ?? i.Import.StartedBy.ToString("D"), i.Import.StartedAt, i.Import.CompletedAt, i.Import.JobId))],
            [.. report.Objects.Select(o => new CustodyObjectResource(o.Stored.ObjectId, o.Stored.Area.ToString(), o.Stored.Current, o.Stored.PageSetId,
                o.Stored.PageOrdinal, o.Stored.SizeBytes, o.Stored.ContentType, o.Stored.CreatedAt, o.Stored.CreatedByJobId, Hex(o.Stored.Sha256),
                Verification(o.Verification)))],
            d.ObjectsTruncated,
            [.. d.PageSets.Select(p => new CustodyPageSetResource(p.PageSetId, p.Source, p.Renderer, p.RendererVersion, p.Pages, p.Status, p.CreatedAt, p.Active))],
            [.. d.Overlays.Select(o => new CustodyOverlayResource(o.ImportId, o.RowNumber, o.DocumentVersion, o.OccurredAt, o.Changed))],
            [.. d.Productions.Select(p => new CustodyProductionResource(p.ProductionId, p.Name, p.Version, p.FinalizedAt, p.VoidedAt, p.Output, p.BegBates,
                p.EndBates, p.Volumes))]);
    }

    private static CustodyVerificationResource Verification(ObjectVerification v) =>
        new(v.Status.ToString(), v.Sha256 is { } h ? Hex(h) : null, v.VerifiedAt);

    private static string Hex(byte[] value) => Convert.ToHexStringLower(value);

    private static string? Opt(byte[]? value) => value is null ? null : Convert.ToHexStringLower(value);
}

public static class DefensibilityEndpointRegistration
{
    /// <summary>The defensibility report endpoints, their use cases and the PostgreSQL store.</summary>
    public static IServiceCollection AddDefensibilityEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresDefensibilityReports();
        services.AddDefensibilityReportService();
        services.AddSingleton<IApiEndpointModule, DefensibilityEndpoints>();
        return services;
    }
}
