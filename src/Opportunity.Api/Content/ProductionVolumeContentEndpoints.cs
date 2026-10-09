using System.Globalization;

using Opportunity.Api.Conventions;
using Opportunity.Api.Exports;
using Opportunity.Api.Productions;
using Opportunity.Application.Audit;
using Opportunity.Application.Content;
using Opportunity.Application.Exports;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>
/// Production volume downloads through the protected-content gateway (E12-T05, ADR-015 D12): one file of a completed
/// volume run with single-range support, or the whole run as a streamed ZIP (manifest and the volume in path order).
/// PEP-1 requires <c>Production.Create</c>; only the run's initiator receives its files (every member was
/// re-authorized for them when the run was written). Every download writes a durable <c>Production.Downloaded</c>
/// event before the first byte, and every file is re-verified against its registered SHA-256 while it streams.
/// </summary>
public sealed class ProductionVolumeContentEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var volumes = routes.Workspace.MapGroup(ProductionEndpoints.ProductionsPath + ProductionVolumeEndpoints.VolumesPath + "/{volumeId}")
            .WithTags("Productions")
            .WithMetadata(ProtectedContentEndpointMetadata.Instance);

        volumes.MapGet("/files/{fileId}/content", DownloadFileAsync)
            .RequirePermission(Permission.ProductionCreate)
            .WithName("DownloadProductionVolumeFile")
            .WithSummary("Download one file of a completed production volume run (the run's initiator only).")
            .WithDescription("Always an attachment of type application/octet-stream; a single byte range is supported. Audited.")
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces<Stream>(StatusCodes.Status206PartialContent, "application/octet-stream")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status416RangeNotSatisfiable);

        volumes.MapGet("/package", DownloadPackageAsync)
            .RequirePermission(Permission.ProductionCreate)
            .WithName("DownloadProductionVolumePackage")
            .WithSummary("Download a completed production volume run as one ZIP: manifest and the volume (the run's initiator only). Audited.")
            .WithDescription("Streamed; entries are stored uncompressed under their volume paths (ZIP64). Use the file downloads to resume.")
            .Produces<Stream>(StatusCodes.Status200OK, ExportContentEndpoints.PackageContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> DownloadFileAsync(
        string workspaceId, string productionId, string volumeId, string fileId, HttpContext context, IExportStore exports, IAuditEventWriter audit,
        IServiceProvider services, ILogger<ProductionVolumeContentEndpoints> logger, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (services.GetService<IObjectStore>() is not { } store)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Object storage is not configured.");
        }

        if (await ProductionVolumeEndpoints.DownloadableAsync(context, productionId, volumeId, exports, cancellationToken).ConfigureAwait(false) is not { } volume
            || !Guid.TryParse(fileId, out var id)
            || await exports.GetFileAsync(volume.WorkspaceId, volume.ExportId, id, cancellationToken).ConfigureAwait(false) is not { } file
            || !ExportEndpoints.DeliveredKinds.Contains(file.Kind))
        {
            return Problems.NotFound("No such production volume file.");
        }

        if (volume.Status != ExportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The volume run has not completed.");
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

        var eventId = await AuditAsync(audit, context.GetWorkspaceAccess()!, volume, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = volume.ProductionId!.Value.ToString(),
            ["VolumeId"] = volume.ExportId.ToString(),
            ["FileId"] = file.FileId.ToString(),
            ["Kind"] = file.Kind.ToString(),
            ["Sha256"] = Convert.ToHexStringLower(file.Sha256),
            ["Range"] = range is { } r ? string.Create(CultureInfo.InvariantCulture, $"{r.Offset}-{r.LastByte}") : null,
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
        string workspaceId, string productionId, string volumeId, HttpContext context, IExportStore exports, IAuditEventWriter audit,
        IServiceProvider services, ILogger<ProductionVolumeContentEndpoints> logger, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (services.GetService<IObjectStore>() is not { } store)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Object storage is not configured.");
        }

        if (await ProductionVolumeEndpoints.DownloadableAsync(context, productionId, volumeId, exports, cancellationToken).ConfigureAwait(false) is not { } volume)
        {
            return Problems.NotFound("No such production volume.");
        }

        if (volume.Status != ExportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The volume run has not completed.");
        }

        var eventId = await AuditAsync(audit, context.GetWorkspaceAccess()!, volume, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = volume.ProductionId!.Value.ToString(),
            ["VolumeId"] = volume.ExportId.ToString(),
            ["Package"] = "zip",
            ["ManifestSha256"] = volume.Report is { } report ? Convert.ToHexStringLower(report.ManifestSha256) : null,
        }, cancellationToken).ConfigureAwait(false);
        return new ExportContentEndpoints.PackageResult(store, exports, volume, eventId, logger);
    }

    private static async Task<Guid> AuditAsync(
        IAuditEventWriter audit, WorkspaceAccess access, ExportRecord volume, Dictionary<string, string?> details, CancellationToken cancellationToken)
    {
        var principal = access.Principal;
        var auditEvent = new AuditEvent
        {
            WorkspaceId = access.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Production.Category,
            Action = AuditTaxonomy.Production.Downloaded,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            CorrelationId = principal.CorrelationId,
            ResourceType = AuditTaxonomy.Production.ResourceType,
            ResourceId = volume.ProductionId!.Value.ToString(),
            JobId = volume.JobId,
            SnapshotId = volume.SnapshotId,
            Outcome = AuditOutcome.Success,
            Details = details,
        };
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return auditEvent.EventId;
    }
}

public static class ProductionVolumeContentRegistration
{
    /// <summary>The gateway's production volume download endpoints (object storage comes from <c>AddProtectedContentGateway</c>).</summary>
    public static IServiceCollection AddProductionVolumeContentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IApiEndpointModule, ProductionVolumeContentEndpoints>();
        return services;
    }
}
