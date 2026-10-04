using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.Http.HttpResults;

using Opportunity.Api.Conventions;
using Opportunity.Api.Import;
using Opportunity.Application.Audit;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Import;
using Opportunity.Core.Security;
using Opportunity.Import.Jobs;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// Import report downloads through the protected-content gateway (E08-T06, ADR-015 D12): the summary report as JSON and
/// CSV, the re-loadable error file (failed DAT rows in the source's delimiter profile and encoding, built from the
/// retained source and the row errors) and the full issue list of a pre-flight (its creator only, while it is kept).
/// PEP-1 requires <c>Import.Run</c>; every download writes a durable <c>Import.ReportDownloaded</c> event before the
/// first byte.
/// </summary>
public sealed class ImportContentEndpoints : IApiEndpointModule
{
    public const string CsvContentType = "text/csv; charset=utf-8";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var imports = routes.Workspace.MapGroup(ImportEndpoints.ImportsPath)
            .WithTags("Import")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance)
            .RequirePermission(Permission.ImportRun);

        imports.MapGet("/{importId}/report", GetReportAsync)
            .WithName("GetImportReport")
            .WithSummary("The import summary report: rows, natives/text, images/pages, families, elapsed and issue counts (frozen when the import finishes).")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        imports.MapGet("/{importId}/report.csv", DownloadReportCsvAsync)
            .WithName("DownloadImportReportCsv")
            .WithSummary("The import summary report as CSV (UTF-8 with BOM): one Section,Metric,Value line per figure.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        imports.MapGet("/{importId}/error-file", DownloadErrorFileAsync)
            .WithName("DownloadImportErrorFile")
            .WithSummary("The re-loadable error file: the DAT header plus ImportError, then every row not loaded, byte for byte in the source's delimiters and encoding.")
            .WithDescription("Fix the rows and load the file again unchanged: the ImportError column is never loaded. 409 for an OPT-only load (no DAT).")
            .Produces<Stream>(StatusCodes.Status200OK, ContentDispositionHeader.OctetStream)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        imports.MapGet("/preflight/{preflightId}/issues", DownloadPreflightIssuesAsync)
            .WithName("DownloadImportPreflightIssues")
            .WithSummary("Every issue of a pre-flight as CSV (UTF-8 with BOM), in row order; kept 48 hours, for the user who ran it only.")
            .Produces<Stream>(StatusCodes.Status200OK, "text/csv")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Ok<ImportReportResource>, ProblemHttpResult>> GetReportAsync(
        string workspaceId, string importId, HttpContext context, IImportBatchStore batches, IJobRepository jobs, IImportReportStore reports,
        IAuditEventWriter audit, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await LoadAsync(context, importId, batches, jobs, reports, cancellationToken).ConfigureAwait(false) is not { } loaded)
        {
            return Problems.NotFound("No such import.");
        }

        var eventId = await AuditAsync(audit, context, loaded.Batch, "report", cancellationToken).ConfigureAwait(false);
        SetHeaders(context.Response, eventId);
        return TypedResults.Ok(ImportEndpoints.ToReportResource(loaded.Batch, loaded.Job, loaded.Report));
    }

    internal static async Task<IResult> DownloadReportCsvAsync(
        string workspaceId, string importId, HttpContext context, IImportBatchStore batches, IJobRepository jobs, IImportReportStore reports,
        IAuditEventWriter audit, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await LoadAsync(context, importId, batches, jobs, reports, cancellationToken).ConfigureAwait(false) is not { } loaded)
        {
            return Problems.NotFound("No such import.");
        }

        var eventId = await AuditAsync(audit, context, loaded.Batch, "report.csv", cancellationToken).ConfigureAwait(false);
        var report = ImportEndpoints.ToReportResource(loaded.Batch, loaded.Job, loaded.Report);
        var csv = new CsvBuilder();
        csv.Line("Section", "Metric", "Value");
        csv.Line("Import", "Name", report.Name);
        csv.Line("Import", "Source file", report.SourceFileName);
        csv.Line("Import", "Mode", report.Mode.ToString());
        csv.Line("Import", "Status", report.Status.ToString());
        csv.Line("Import", "Final", report.Final ? "Yes" : "No");
        csv.Line("Import", "Started", report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        csv.Line("Import", "Completed", report.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "");
        csv.Line("Import", "Elapsed seconds", report.ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        csv.Line("Rows", "Read", report.Rows.Read?.ToString(CultureInfo.InvariantCulture) ?? "");
        csv.Line("Rows", "Imported", N(report.Rows.Imported));
        csv.Line("Rows", "Overlaid", N(report.Rows.Overlaid));
        csv.Line("Rows", "Skipped", N(report.Rows.Skipped));
        csv.Line("Rows", "Errored", N(report.Rows.Errored));
        csv.Line("Rows", "With warnings", N(report.Rows.WithWarnings));
        csv.Line("Natives", "Linked", N(report.Natives.Linked));
        csv.Line("Natives", "Missing", N(report.Natives.Missing));
        csv.Line("Text", "Linked", N(report.Text.Linked));
        csv.Line("Text", "Missing", N(report.Text.Missing));
        csv.Line("Text", "Truncated", N(report.Text.Truncated));
        csv.Line("Images", "Documents linked", N(report.Images.DocumentsLinked));
        csv.Line("Images", "Documents without images", N(report.Images.DocumentsWithoutImages));
        csv.Line("Images", "Pages linked", N(report.Images.PagesLinked));
        csv.Line("Images", "Pages missing", N(report.Images.PagesMissing));
        csv.Line("Families", "Built", N(report.Families.Built));
        csv.Line("Families", "Orphans", N(report.Families.Orphans));
        csv.Line("Fields", "Fields created", report.FieldsCreated.ToString(CultureInfo.InvariantCulture));
        csv.Line("Fields", "Choices created", report.ChoicesCreated.ToString(CultureInfo.InvariantCulture));
        csv.Line("Error file", "Rows", N(report.ErrorFileRows));
        foreach (var count in report.IssueCounts)
        {
            csv.Line(count.Severity == ImportRowIssueSeverity.Error ? "Errors" : "Warnings", count.Code, N(count.Count));
        }

        return Csv(context, csv.ToBytes(), Path.GetFileNameWithoutExtension(loaded.Batch.SourceFileName) + "_report.csv", eventId);
    }

    internal static async Task<IResult> DownloadErrorFileAsync(
        string workspaceId, string importId, HttpContext context, IImportBatchStore batches, IJobRepository jobs, IImportReportStore reports,
        IAuditEventWriter audit, IServiceProvider services, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (services.GetService<IObjectStore>() is not { } store)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Object storage is not configured.");
        }

        if (await LoadAsync(context, importId, batches, jobs, reports, cancellationToken).ConfigureAwait(false) is not { } loaded)
        {
            return Problems.NotFound("No such import.");
        }

        var batch = loaded.Batch;
        if (batch.ImagesOnly || batch.Preparation is null)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
                batch.ImagesOnly ? "An OPT-only load has no DAT error file; see the row errors." : "The import has not read its load file yet.");
        }

        var eventId = await AuditAsync(audit, context, batch, "error-file", cancellationToken).ConfigureAwait(false);

        // Built into a temporary file first (the writer is synchronous), then streamed; deleted on close.
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "opp-errfile-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var source = await store.OpenReadAsync(ObjectKey.Parse(batch.SourceObjectKey), cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                await ImportErrorFile.WriteAsync(source, batch,
                    (after, limit, ct) => reports.GetErroredRowsAsync(batch.WorkspaceId, batch.ImportBatchId, after, limit, ct), temp, cancellationToken)
                    .ConfigureAwait(false);
            }

            temp.Position = 0;
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        SetHeaders(context.Response, eventId);
        context.Response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(ImportErrorFile.FileName(batch.SourceFileName));
        context.Response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(temp, ContentDispositionHeader.OctetStream);
    }

    internal static async Task<IResult> DownloadPreflightIssuesAsync(
        string workspaceId, string preflightId, HttpContext context, IImportPreflightStore preflights, IAuditEventWriter audit,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(preflightId, out var id)
            || await preflights.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } preflight
            || preflight.CreatedBy != access.Principal.UserId)
        {
            // Another user's pre-flight is indistinguishable from an unknown or expired one.
            return Problems.NotFound("No such pre-flight.");
        }

        var auditEvent = Event(access, "ImportPreflight", preflight.PreflightId.ToString(), new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PreflightId"] = preflight.PreflightId.ToString(),
            ["Kind"] = "preflight-issues",
        });
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);

        var csv = new CsvBuilder();
        csv.Line("Row", "ControlNumber", "Column", "Severity", "Code", "Message");
        var after = 0;
        while (true)
        {
            var page = await preflights.GetIssuesAsync(access.WorkspaceId, id, after, 5_000, cancellationToken).ConfigureAwait(false);
            foreach (var issue in page)
            {
                csv.Line(N(issue.Row), issue.ControlNumber ?? "", issue.Column ?? "", issue.Severity == ImportRowIssueSeverity.Error ? "error" : "warning",
                    issue.Code, issue.Message);
            }

            after += page.Count;
            if (page.Count < 5_000)
            {
                break;
            }
        }

        if (preflight.IssuesDropped > 0)
        {
            csv.Line("", "", "", "warning", "ISSUES_TRUNCATED",
                $"{N(preflight.IssuesDropped)} further issues were counted but not kept; fix these and run the pre-flight again.");
        }

        return Csv(context, csv.ToBytes(), Path.GetFileNameWithoutExtension(preflight.SourceFileName) + "_preflight.csv", auditEvent.EventId);
    }

    private sealed record Loaded(ImportBatchRecord Batch, JobInfo Job, ImportReportData Report);

    private static async Task<Loaded?> LoadAsync(
        HttpContext context, string importId, IImportBatchStore batches, IJobRepository jobs, IImportReportStore reports, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(importId, out var id)
            || await batches.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } batch
            || await jobs.GetAsync(access.WorkspaceId, batch.JobId, cancellationToken).ConfigureAwait(false) is not { } job
            || await reports.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } report)
        {
            return null;
        }

        return new Loaded(batch, job, report);
    }

    private static async Task<Guid> AuditAsync(IAuditEventWriter audit, HttpContext context, ImportBatchRecord batch, string kind, CancellationToken cancellationToken)
    {
        var auditEvent = Event(context.GetWorkspaceAccess()!, AuditTaxonomy.Import.ResourceType, batch.ImportBatchId.ToString(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ImportBatchId"] = batch.ImportBatchId.ToString(),
                ["Kind"] = kind,
            }, batch.JobId);
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return auditEvent.EventId;
    }

    private static AuditEvent Event(
        WorkspaceAccess access, string resourceType, string resourceId, Dictionary<string, string?> details, Guid? jobId = null)
    {
        var principal = access.Principal;
        return new AuditEvent
        {
            WorkspaceId = access.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Import.Category,
            Action = AuditTaxonomy.Import.ReportDownloaded,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            CorrelationId = principal.CorrelationId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = AuditOutcome.Success,
            JobId = jobId,
            Details = details,
        };
    }

    private static FileContentHttpResult Csv(HttpContext context, byte[] bytes, string fileName, Guid eventId)
    {
        SetHeaders(context.Response, eventId);
        context.Response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(fileName);
        context.Response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Bytes(bytes, CsvContentType);
    }

    private static void SetHeaders(HttpResponse response, Guid eventId)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers[ProtectedContentGateway.RetrievalIdHeader] = eventId.ToString();
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 CSV in UTF-8 with BOM; values that a spreadsheet would run as a formula are prefixed with an apostrophe.</summary>
    private sealed class CsvBuilder
    {
        private readonly StringBuilder _text = new();

        public void Line(params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    _text.Append(',');
                }

                var v = values[i];
                if (v.Length > 0 && v[0] is '=' or '+' or '@' or '\t' or '\r' || (v.Length > 1 && v[0] == '-' && !char.IsDigit(v[1])))
                {
                    v = "'" + v;
                }

                if (v.AsSpan().IndexOfAny(",\"\r\n") >= 0)
                {
                    _text.Append('"').Append(v.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                }
                else
                {
                    _text.Append(v);
                }
            }

            _text.Append("\r\n");
        }

        public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_text.ToString())];
    }
}

public static class ImportContentRegistration
{
    /// <summary>The gateway's import report downloads (object storage comes from <c>AddProtectedContentGateway</c>).</summary>
    public static IServiceCollection AddImportContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, ImportContentEndpoints>();
        return services;
    }
}
