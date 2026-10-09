using System.Globalization;

using Opportunity.Api.Conventions;
using Opportunity.Api.Exports;
using Opportunity.Application.Audit;
using Opportunity.Application.Content;
using Opportunity.Application.Exports;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Production.Exports;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// Export package downloads through the protected-content gateway (E12-T01, ADR-015 D12): one file of a completed
/// export with single-range support, or the whole package as a streamed ZIP (manifest, reports and the volume in path
/// order). PEP-1 requires <c>Export.Download</c>; only the export's creator receives its files (the documents in it were
/// re-authorized for them). Every download writes a durable <c>Export.Downloaded</c> event before the first byte, and
/// every file is re-verified against its registered SHA-256 while it streams (ADR-011 §2.5).
/// </summary>
public sealed partial class ExportContentEndpoints : IApiEndpointModule
{
    public const string PackageContentType = "application/zip";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var exports = routes.Workspace.MapGroup(ExportEndpoints.ExportsPath + "/{exportId}")
            .WithTags("Exports")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance);

        exports.MapGet("/files/{fileId}/content", DownloadFileAsync)
            .RequirePermission(Permission.ExportDownload)
            .WithName("DownloadExportFile")
            .WithSummary("Download one file of a completed export (Export.Download, your own exports).")
            .WithDescription("Always an attachment of type application/octet-stream; a single byte range is supported.")
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "application/octet-stream")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status416RangeNotSatisfiable);

        exports.MapGet("/package", DownloadPackageAsync)
            .RequirePermission(Permission.ExportDownload)
            .WithName("DownloadExportPackage")
            .WithSummary("Download a completed export as one ZIP: manifest, exclusion report and the volume (Export.Download, your own exports).")
            .WithDescription("Streamed; entries are stored uncompressed under their package paths (ZIP64). Use the file downloads to resume.")
            .Produces<Stream>(StatusCodes.Status200OK, PackageContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> DownloadFileAsync(
        string workspaceId, string exportId, string fileId, HttpContext context, IExportStore exports, IAuditEventWriter audit,
        IServiceProvider services, ILogger<ExportContentEndpoints> logger, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (services.GetService<IObjectStore>() is not { } store)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Object storage is not configured.");
        }

        if (await ExportEndpoints.DownloadableAsync(context, exportId, exports, cancellationToken).ConfigureAwait(false) is not { } export
            || !Guid.TryParse(fileId, out var id)
            || await exports.GetFileAsync(export.WorkspaceId, export.ExportId, id, cancellationToken).ConfigureAwait(false) is not { } file
            || !ExportEndpoints.DeliveredKinds.Contains(file.Kind))
        {
            return Problems.NotFound("No such export file.");
        }

        if (export.Status != ExportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The export has not completed.");
        }

        ByteRange? range = null;
        if (ProtectedContentGateway.ParseRange(context.Request) is { } requested)
        {
            if (requested.Resolve(file.SizeBytes) is not { } resolved)
            {
                context.Response.Headers.ContentRange = $"bytes */{file.SizeBytes.ToString(CultureInfo.InvariantCulture)}";
                return Problems.Create(StatusCodes.Status416RangeNotSatisfiable, ProblemCodes.RangeNotSatisfiable, "The requested range is outside the file.");
            }

            range = resolved;
        }

        var access = context.GetWorkspaceAccess()!;
        var eventId = await AuditAsync(audit, access, export, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ExportId"] = export.ExportId.ToString(),
            ["FileId"] = file.FileId.ToString(),
            ["Kind"] = file.Kind.ToString(),
            ["Sha256"] = Convert.ToHexStringLower(file.Sha256),
            ["Range"] = range is { } r
                ? string.Create(CultureInfo.InvariantCulture, $"{r.Offset}-{r.LastByte}")
                : null,
        }, cancellationToken).ConfigureAwait(false);

        var grant = new ContentGrant(
            eventId,
            ObjectKey.Parse(file.ObjectKey),
            Sha256Digest.FromBytes(file.Sha256),
            file.SizeBytes,
            ContentDispositionHeader.OctetStream,
            ObjectDeliveryMode.Stream,
            range,
            file.Path[(file.Path.LastIndexOf('/') + 1)..],
            BreakGlass: false);
        return ProtectedContentGateway.StreamAttachment(store, grant, logger);
    }

    internal static async Task<IResult> DownloadPackageAsync(
        string workspaceId, string exportId, HttpContext context, IExportStore exports, IAuditEventWriter audit, IServiceProvider services,
        ILogger<ExportContentEndpoints> logger, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (services.GetService<IObjectStore>() is not { } store)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Object storage is not configured.");
        }

        if (await ExportEndpoints.DownloadableAsync(context, exportId, exports, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return Problems.NotFound("No such export.");
        }

        if (export.Status != ExportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The export has not completed.");
        }

        var access = context.GetWorkspaceAccess()!;
        var eventId = await AuditAsync(audit, access, export, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ExportId"] = export.ExportId.ToString(),
            ["Package"] = "zip",
            ["ManifestSha256"] = export.Report is { } report ? Convert.ToHexStringLower(report.ManifestSha256) : null,
        }, cancellationToken).ConfigureAwait(false);
        return new PackageResult(store, exports, export, eventId, logger);
    }

    private static async Task<Guid> AuditAsync(
        IAuditEventWriter audit, WorkspaceAccess access, ExportRecord export, Dictionary<string, string?> details, CancellationToken cancellationToken)
    {
        var principal = access.Principal;
        var auditEvent = new AuditEvent
        {
            WorkspaceId = access.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Export.Category,
            Action = AuditTaxonomy.Export.Downloaded,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            CorrelationId = principal.CorrelationId,
            ResourceType = AuditTaxonomy.Export.ResourceType,
            ResourceId = export.ExportId.ToString(),
            JobId = export.JobId,
            SnapshotId = export.SnapshotId,
            Outcome = AuditOutcome.Success,
            Details = details,
        };
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return auditEvent.EventId;
    }

    /// <summary>Streams every delivered file of the export into one ZIP, in path order, each verified while it streams.</summary>
    internal sealed class PackageResult(IObjectStore store, IExportStore exports, ExportRecord export, Guid auditEventId, ILogger logger) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var cancellationToken = httpContext.RequestAborted;
            var response = httpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = PackageContentType;
            response.Headers.CacheControl = "no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
            response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(export.Name + ".zip");
            response.Headers[ProtectedContentGateway.RetrievalIdHeader] = auditEventId.ToString();

            var zip = new StreamingZipWriter(response.Body);
            string? after = null;
            try
            {
                while (true)
                {
                    var page = await exports.GetFilesAsync(export.WorkspaceId, export.ExportId, ExportEndpoints.DeliveredKinds, after, 500, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var file in page)
                    {
                        var key = ObjectKey.Parse(file.ObjectKey);
                        var source = await store.OpenReadAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await using (source.ConfigureAwait(false))
                        {
                            var verified = new VerifyingReadStream(source, key, Sha256Digest.FromBytes(file.Sha256), file.SizeBytes);
                            await zip.AddEntryAsync(file.Path, verified, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (page.Count < 500)
                    {
                        break;
                    }

                    after = page[^1].Path;
                }

                await zip.FinishAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectIntegrityException)
            {
                // Chain of custody: never let a truncated or altered package look complete.
                LogIntegrityFailure(logger, auditEventId);
                httpContext.Abort();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "An export file failed integrity verification during a package download (download {DownloadId}); response aborted.")]
    private static partial void LogIntegrityFailure(ILogger logger, Guid downloadId);
}

public static class ExportContentRegistration
{
    /// <summary>The gateway's export download endpoints (object storage comes from <c>AddProtectedContentGateway</c>).</summary>
    public static IServiceCollection AddExportContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, ExportContentEndpoints>();
        return services;
    }
}
