using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Application.Authorization;
using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Exports;
using Opportunity.Data.Fields;
using Opportunity.Data.Jobs;
using Opportunity.Data.Snapshots;
using Opportunity.Production.Exports;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Exports;

/// <summary>
/// Load-file exports of a frozen set (E12-T01). <c>POST …/exports</c> (<c>Export.Create</c>) validates the field list,
/// delimiters, encodings and volume layout against a Ready <c>Export</c> snapshot and answers <c>202 Accepted</c> with
/// the export and its job; the export worker writes the volume in chunks, re-checking the initiator's access to every
/// document (Q-15). Exports are visible to their creator, or to holders of <c>Job.ViewAll</c>. The file listing serves
/// the creator with <c>Export.Download</c>; the files themselves come from the protected-content gateway.
/// </summary>
public sealed class ExportEndpoints : IApiEndpointModule
{
    public const string ExportsPath = "/exports";

    private const string Tag = "Exports";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.Workspace.MapGroup(ExportsPath);

        group.MapPost(string.Empty, CreateAsync)
            .RequirePermission(Permission.ExportCreate)
            .WithName("CreateExport")
            .WithTags(Tag)
            .WithSummary("Export a frozen set to a load-file volume (Export.Create); 202 with the export and its job.")
            .WithDescription(
                "The volume holds a DAT with the listed fields in order (and native/text path columns), natives, extracted text and " +
                "page images with an OPT, a manifest with the SHA-256 of every file, and an exclusion report: every document is " +
                "re-authorized for you when its chunk runs, and documents you can no longer access are left out (Q-15).")
            .RequireIdempotencyKey()
            .Produces<ExportResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet(string.Empty, ListAsync)
            .RequireWorkspaceMember()
            .WithName("ListExports")
            .WithTags(Tag)
            .WithSummary("Exports, newest first: your own, or everyone's with Job.ViewAll.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{exportId}", GetAsync)
            .RequireWorkspaceMember()
            .WithName("GetExport")
            .WithTags(Tag)
            .WithSummary("One export with its settings, report (counts, manifest SHA-256) and job progress.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{exportId}/exclusions", ListExclusionsAsync)
            .RequireWorkspaceMember()
            .WithName("ListExportExclusions")
            .WithTags(Tag)
            .WithSummary("Documents left out because you could no longer access them when their chunk ran (Q-15), in volume order.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{exportId}/files", ListFilesAsync)
            .RequirePermission(Permission.ExportDownload)
            .WithName("ListExportFiles")
            .WithTags(Tag)
            .WithSummary("The delivered files of a completed export, in path order, with size and SHA-256 (Export.Download, your own exports).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<Results<Accepted<ExportResource>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        string workspaceId, CreateExportRequest request, HttpContext context, ExportService exports, IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send the export request."] });
        }

        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await exports.CreateAsync(access.Principal, access.WorkspaceId, request, key.Length > 0 ? key : null, cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case ExportCreateStatus.Accepted:
                var creation = outcome.Creation!;
                return ApiResults.JobAccepted(access.WorkspaceId.ToString(), creation.Job.JobId.ToString(), ToResource(creation.Export, creation.Job));
            case ExportCreateStatus.Invalid:
                return Problems.Validation(outcome.Errors!.ToDictionary());
            default:
                return Problems.NotFound("No such frozen set.");
        }
    }

    internal static async Task<Results<Ok<CursorPage<ExportResource>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, IExportStore exports, IJobRepository jobs,
        IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        ExportListCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var ticks, var id]
                || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || !Guid.TryParse(id, out var exportId))
            {
                return PageCursor.Invalid();
            }

            after = new ExportListCursor(new DateTimeOffset(t, TimeSpan.Zero), exportId);
        }

        var viewAll = (await authorization.AuthorizeAsync(access.Principal, access.WorkspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false))
            .IsAllowed;
        var limit = page.EffectiveLimit;
        var records = await exports.ListAsync(access.WorkspaceId, viewAll ? null : access.Principal.UserId, after, limit + 1, cancellationToken)
            .ConfigureAwait(false);
        var items = new List<ExportResource>();
        foreach (var record in records.Take(limit))
        {
            if (await jobs.GetAsync(access.WorkspaceId, record.JobId, cancellationToken).ConfigureAwait(false) is { } job)
            {
                items.Add(ToResource(record, job));
            }
        }

        var next = records.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId,
                records[limit - 1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), records[limit - 1].ExportId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<ExportResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<Results<Ok<ExportResource>, ProblemHttpResult>> GetAsync(
        string workspaceId, string exportId, HttpContext context, IExportStore exports, IJobRepository jobs, IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await ReadableAsync(context, exportId, exports, authorization, cancellationToken).ConfigureAwait(false) is not { } export
            || await jobs.GetAsync(export.WorkspaceId, export.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return Problems.NotFound("No such export.");
        }

        return TypedResults.Ok(ToResource(export, job));
    }

    internal static async Task<Results<Ok<CursorPage<ExportExclusionResource>>, ValidationProblem, ProblemHttpResult>> ListExclusionsAsync(
        string workspaceId, string exportId, [AsParameters] PageQuery page, HttpContext context, IExportStore exports,
        IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (await ReadableAsync(context, exportId, exports, authorization, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return Problems.NotFound("No such export.");
        }

        var access = context.GetWorkspaceAccess()!;
        long after = 0;
        if (page.Cursor is { } cursor
            && (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var id, var ordinal]
                || id != export.ExportId.ToString("N") || !long.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out after)))
        {
            return PageCursor.Invalid();
        }

        var limit = page.EffectiveLimit;
        var rows = await exports.GetExclusionsAsync(export.WorkspaceId, export.ExportId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = rows.Take(limit).Select(r => new ExportExclusionResource(r.Ordinal, r.DocumentId, r.ControlNumber, r.Reason ?? ExportChunkExecutor.AccessChanged))
            .ToList();
        var next = rows.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, export.ExportId.ToString("N"), items[^1].Ordinal.ToString(CultureInfo.InvariantCulture))
            : null;
        return TypedResults.Ok(new CursorPage<ExportExclusionResource>(items, next,
            new TotalCount(items.Count, next is null && after == 0 ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<Results<Ok<CursorPage<ExportFileResource>>, ValidationProblem, ProblemHttpResult>> ListFilesAsync(
        string workspaceId, string exportId, [AsParameters] PageQuery page, HttpContext context, IExportStore exports,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (await DownloadableAsync(context, exportId, exports, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return Problems.NotFound("No such export.");
        }

        if (export.Status != ExportStatus.Completed)
        {
            return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The export has not completed.");
        }

        var access = context.GetWorkspaceAccess()!;
        string? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var id, var path] || id != export.ExportId.ToString("N"))
            {
                return PageCursor.Invalid();
            }

            after = path;
        }

        var limit = page.EffectiveLimit;
        var files = await exports.GetFilesAsync(export.WorkspaceId, export.ExportId, DeliveredKinds, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = files.Take(limit).Select(ToResource).ToList();
        var next = files.Count > limit ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, export.ExportId.ToString("N"), items[^1].Path) : null;
        return TypedResults.Ok(new CursorPage<ExportFileResource>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    /// <summary>The file kinds a package delivers (chunk parts are internal).</summary>
    public static readonly IReadOnlyList<ExportFileKind> DeliveredKinds =
    [
        ExportFileKind.Native, ExportFileKind.Text, ExportFileKind.Image, ExportFileKind.Dat, ExportFileKind.Opt, ExportFileKind.Manifest,
        ExportFileKind.Report,
    ];

    /// <summary>The export when the caller may read it: its creator, or a holder of <c>Job.ViewAll</c>.</summary>
    internal static async Task<ExportRecord?> ReadableAsync(
        HttpContext context, string exportId, IExportStore exports, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(exportId, out var id)
            || await exports.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return null;
        }

        return export.CreatedBy == access.Principal.UserId
            || (await authorization.AuthorizeAsync(access.Principal, access.WorkspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed
                ? export
                : null;
    }

    /// <summary>
    /// The export when the caller may download it: its creator only (PEP-1 has required <c>Export.Download</c>). The
    /// package holds documents re-authorized for the creator, so nobody else receives it (decision to confirm).
    /// </summary>
    internal static async Task<ExportRecord?> DownloadableAsync(HttpContext context, string exportId, IExportStore exports, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(exportId, out var id)
            || await exports.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return null;
        }

        return export.CreatedBy == access.Principal.UserId ? export : null;
    }

    internal static ExportFileResource ToResource(ExportFileRecord file) => new(
        file.FileId, file.Path, Enum.Parse<ExportFileResourceKind>(file.Kind.ToString()), file.SizeBytes, Convert.ToHexStringLower(file.Sha256));

    internal static ExportResource ToResource(ExportRecord export, JobInfo job) => new(
        export.ExportId,
        export.WorkspaceId,
        export.Name,
        export.SnapshotId,
        Enum.Parse<ExportResourceStatus>(export.Status.ToString()),
        export.StatusReason,
        ExportSettings.Deserialize(export.SettingsJson).ToResource(),
        export.Report is { } r
            ? new ExportReportResource(r.DocumentsExported, r.DocumentsExcluded, r.Natives, r.Texts, r.Images, r.Pages, r.Files, r.TotalBytes,
                Convert.ToHexStringLower(r.ManifestSha256))
            : null,
        JobEndpoints.ToResource(job),
        export.CreatedBy,
        export.CreatedAt,
        export.CompletedAt);
}

public static class ExportEndpointRegistration
{
    /// <summary>The export API with its stores; the files are served by the protected-content gateway.</summary>
    public static IServiceCollection AddExportEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresExportStore();
        services.AddPostgresSnapshotStore();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.AddExportService();
        services.AddSingleton<IApiEndpointModule, ExportEndpoints>();
        return services;
    }
}
