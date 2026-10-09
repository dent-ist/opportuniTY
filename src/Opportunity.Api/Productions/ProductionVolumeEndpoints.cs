using System.Globalization;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Exports;
using Opportunity.Api.Jobs;
using Opportunity.Application.Exports;
using Opportunity.Application.Jobs;
using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Exports;
using Opportunity.Production.Exports;
using Opportunity.Production.Volumes;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Productions;

/// <summary>
/// Production volumes (E12-T05): <c>POST …/productions/{id}/volumes</c> starts a run that writes a finalized production's
/// volume (page images with burned redactions and endorsements, natives with slip sheets, placeholders, text, DAT and
/// OPT, manifest) in the rendering worker; 202 with the run and its job. Every run of the same production writes the
/// same bytes (the manifest SHA-256 shows it). Runs are listed to every <c>Production.Create</c> holder; their files
/// are listed and downloaded (through the protected-content gateway) by the run's initiator only, whose access to every
/// member was re-checked when the run was written (Q-15).
/// </summary>
public sealed class ProductionVolumeEndpoints : IApiEndpointModule
{
    public const string VolumesPath = "/{productionId}/volumes";

    private const string Tag = "Productions";
    private const string NotFoundDetail = "No such production volume.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.Workspace.MapGroup(ProductionEndpoints.ProductionsPath + VolumesPath);

        group.MapPost(string.Empty, StartAsync)
            .RequirePermission(Permission.ProductionCreate)
            .WithName("StartProductionVolume")
            .WithTags(Tag)
            .WithSummary("Write a finalized production's volume (202 with the run and its job): images, natives, text, DAT/OPT and manifest.")
            .WithDescription(
                "Pages are imaged in the render sandbox from the page sets frozen at finalization, with the frozen redactions burned in and the " +
                "endorsements stamped (TIFF G4, or JPEG for the colour file types). Native members get a slip sheet, placeholder members a " +
                "'Withheld' page and pages that cannot be imaged a 'Technical Issue' page, each taking its Bates number. A redacted document's " +
                "text is never the original. 409 unless the production is finalized.")
            .RequireIdempotencyKey()
            .Produces<ProductionVolumeResource>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet(string.Empty, ListAsync)
            .RequirePermission(Permission.ProductionCreate)
            .WithName("ListProductionVolumes")
            .WithTags(Tag)
            .WithSummary("The production's volume runs, newest first, with their status, counts and manifest SHA-256.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{volumeId}", GetAsync)
            .RequirePermission(Permission.ProductionCreate)
            .WithName("GetProductionVolume")
            .WithTags(Tag)
            .WithSummary("One volume run with its status, report (counts, manifest SHA-256) and job progress.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{volumeId}/files", ListFilesAsync)
            .RequirePermission(Permission.ProductionCreate)
            .WithName("ListProductionVolumeFiles")
            .WithTags(Tag)
            .WithSummary("The delivered files of a completed volume run, in path order, with size and SHA-256 (the run's initiator only).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> StartAsync(
        string workspaceId, string productionId, HttpContext context, ProductionVolumeService volumes, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(productionId, out var id))
        {
            return Problems.NotFound("No such production.");
        }

        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await volumes.StartAsync(access.Principal, access.WorkspaceId, id, key.Length > 0 ? key : null, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            ProductionVolumeStartStatus.Accepted => ApiResults.JobAccepted(access.WorkspaceId.ToString(), outcome.Creation!.Job.JobId.ToString(),
                ToResource(outcome.Creation.Export, outcome.Creation.Job)),
            ProductionVolumeStartStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, outcome.Reason!),
            _ => Problems.NotFound("No such production."),
        };
    }

    internal static async Task<IResult> ListAsync(
        string workspaceId, string productionId, [AsParameters] PageQuery page, HttpContext context, IProductionStore productions, IExportStore exports,
        IJobRepository jobs, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(productionId, out var id)
            || await productions.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is null)
        {
            return Problems.NotFound("No such production.");
        }

        ExportListCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is not [var production, var ticks, var volume]
                || production != id.ToString("N")
                || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || !Guid.TryParse(volume, out var volumeId))
            {
                return PageCursor.Invalid();
            }

            after = new ExportListCursor(new DateTimeOffset(t, TimeSpan.Zero), volumeId);
        }

        var limit = page.EffectiveLimit;
        var records = await exports.ListVolumesAsync(access.WorkspaceId, id, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = new List<ProductionVolumeResource>();
        foreach (var record in records.Take(limit))
        {
            if (await jobs.GetAsync(access.WorkspaceId, record.JobId, cancellationToken).ConfigureAwait(false) is { } job)
            {
                items.Add(ToResource(record, job));
            }
        }

        var next = records.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, id.ToString("N"),
                records[limit - 1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), records[limit - 1].ExportId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<ProductionVolumeResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string productionId, string volumeId, HttpContext context, IExportStore exports, IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await VolumeAsync(context, productionId, volumeId, exports, cancellationToken).ConfigureAwait(false) is not { } volume
            || await jobs.GetAsync(volume.WorkspaceId, volume.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        return TypedResults.Ok(ToResource(volume, job));
    }

    internal static async Task<IResult> ListFilesAsync(
        string workspaceId, string productionId, string volumeId, [AsParameters] PageQuery page, HttpContext context, IExportStore exports,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (await DownloadableAsync(context, productionId, volumeId, exports, cancellationToken).ConfigureAwait(false) is not { } volume)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (volume.Status != ExportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The volume run has not completed.");
        }

        var access = context.GetWorkspaceAccess()!;
        string? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var id, var path] || id != volume.ExportId.ToString("N"))
            {
                return PageCursor.Invalid();
            }

            after = path;
        }

        var limit = page.EffectiveLimit;
        var files = await exports.GetFilesAsync(volume.WorkspaceId, volume.ExportId, ExportEndpoints.DeliveredKinds, after, limit + 1, cancellationToken)
            .ConfigureAwait(false);
        var items = files.Take(limit).Select(ExportEndpoints.ToResource).ToList();
        var next = files.Count > limit ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, volume.ExportId.ToString("N"), items[^1].Path) : null;
        return TypedResults.Ok(new CursorPage<ExportFileResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    /// <summary>The volume run of the production named in the route (PEP-1 has required <c>Production.Create</c>).</summary>
    internal static async Task<ExportRecord?> VolumeAsync(
        HttpContext context, string productionId, string volumeId, IExportStore exports, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(productionId, out var production) || !Guid.TryParse(volumeId, out var id)
            || await exports.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } volume
            || volume.ProductionId != production)
        {
            return null;
        }

        return volume;
    }

    /// <summary>
    /// The run when the caller may receive its files: its initiator only. The run re-authorized every member for them,
    /// so nobody else receives the documents through it (as for exports).
    /// </summary>
    internal static async Task<ExportRecord?> DownloadableAsync(
        HttpContext context, string productionId, string volumeId, IExportStore exports, CancellationToken cancellationToken) =>
        await VolumeAsync(context, productionId, volumeId, exports, cancellationToken).ConfigureAwait(false) is { } volume
            && volume.CreatedBy == context.GetWorkspaceAccess()!.Principal.UserId
            ? volume
            : null;

    internal static ProductionVolumeResource ToResource(ExportRecord volume, JobInfo job) => new(
        volume.ExportId,
        volume.ProductionId!.Value,
        volume.WorkspaceId,
        ExportSettings.Deserialize(volume.SettingsJson).VolumeName,
        Enum.Parse<ProductionVolumeStatusResource>(volume.Status.ToString()),
        volume.StatusReason,
        volume.Report is { } r
            ? new ProductionVolumeReportResource(r.DocumentsExported, r.Images, r.Natives, r.Texts, r.Files, r.TotalBytes, Convert.ToHexStringLower(r.ManifestSha256))
            : null,
        JobEndpoints.ToResource(job),
        volume.CreatedBy,
        volume.CreatedAt,
        volume.CompletedAt);
}

public static class ProductionVolumeEndpointRegistration
{
    /// <summary>The production volume API (E12-T05); the files are served by the protected-content gateway.</summary>
    public static IServiceCollection AddProductionVolumeEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresExportStore();
        services.AddProductionVolumeService();
        services.AddSingleton<IApiEndpointModule, ProductionVolumeEndpoints>();
        return services;
    }
}
