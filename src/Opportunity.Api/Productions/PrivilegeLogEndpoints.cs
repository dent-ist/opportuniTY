using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Fields;
using Opportunity.Application.PrivilegeLogs;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Data.Exports;
using Opportunity.Data.Fields;
using Opportunity.Data.PrivilegeLogs;
using Opportunity.Data.Productions;
using Opportunity.Data.Snapshots;
using Opportunity.Production.PrivilegeLogs;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Productions;

/// <summary>
/// Privilege logs (E13-T03) under <c>/api/v1/workspaces/{workspaceId}</c>, all with <c>PrivilegeLog.Generate</c>:
/// column templates (<c>…/privilege-log-templates</c>, If-Match on replace, audited), generation
/// (<c>POST …/privilege-logs</c> from a production, a frozen set or a saved search: a new version, or the latest one when
/// nothing changed; audited <c>Privilege.LogGenerated</c>), versions, their rows and what changed since the previous
/// version (<c>…/changes</c>, #182). The CSV and XLSX files are protected-content endpoints
/// (<see cref="Content.PrivilegeLogContentEndpoints"/>). A version listing a document or reading a field the caller may
/// not see answers like a missing one (Q-52).
/// </summary>
public sealed class PrivilegeLogEndpoints : IApiEndpointModule
{
    public const string TemplatesPath = "/privilege-log-templates";
    public const string LogsPath = "/privilege-logs";

    private const string Tag = "Privilege logs";
    private const string NoTemplate = "No such privilege log template.";
    private const string NoLog = "No such privilege log.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(TemplatesPath, ListTemplatesAsync)
            .WithName("ListPrivilegeLogTemplates")
            .WithTags(Tag)
            .WithSummary("The workspace's privilege log templates by name, and the built-in presets resolved in this workspace.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeLogTemplateList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost(TemplatesPath, CreateTemplateAsync)
            .WithName("CreatePrivilegeLogTemplate")
            .WithTags(Tag)
            .WithSummary("Create a privilege log template: columns, Priv ID numbering, date format and zone, privacy redactions, exclusion rules (audited).")
            .WithDescription("Absent members take their defaults; without columns the preset's columns (default document-by-document) are used.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeLogTemplateResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        ws.MapGet(TemplatesPath + "/{templateId}", GetTemplateAsync)
            .WithName("GetPrivilegeLogTemplate")
            .WithTags(Tag)
            .WithSummary("A privilege log template with its ETag.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeLogTemplateResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPut(TemplatesPath + "/{templateId}", UpdateTemplateAsync)
            .WithName("UpdatePrivilegeLogTemplate")
            .WithTags(Tag)
            .WithSummary("Replace a privilege log template (If-Match required; audited). Versions generated earlier keep the template they used.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeLogTemplateResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        ws.MapPost(LogsPath, GenerateAsync)
            .WithName("GeneratePrivilegeLog")
            .WithTags(Tag)
            .WithSummary("Generate a privilege log from a finalized production, a frozen set or a saved search: 201 with a new version, or 200 with the unchanged latest one.")
            .WithDescription(
                "Lists every document withheld or redacted for privilege exactly once: production members produced as a placeholder and coded " +
                "Withhold (their Bates is their Priv ID), members produced with redactions (privilege reasons or coded Redact; privacy-only ones " +
                "only when the template includes them) and documents coded Withhold that were not produced (members' families and the optional " +
                "review set snapshotId). A frozen-set log lists members coded Withhold or Redact; a saved-search log (savedSearchId alone) " +
                "freezes the saved search as you see it now (Search.Execute) and lists the same, and its versions form one log whatever " +
                "was frozen each time. Documents produced in full never appear; " +
                "documents you may not see are neither listed nor counted. Recorded exclusion rules are written into the metadata with their " +
                "counts. Needs Production.Create for a production. At most 50,000 documents per log.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeLogGenerationResource>(StatusCodes.Status201Created)
            .Produces<PrivilegeLogGenerationResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        ws.MapGet(LogsPath, ListAsync)
            .WithName("ListPrivilegeLogs")
            .WithTags(Tag)
            .WithSummary("Privilege log versions newest first, optionally of one production, frozen set or saved search.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<CursorPage<PrivilegeLogResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet(LogsPath + "/{logId}", GetAsync)
            .WithName("GetPrivilegeLog")
            .WithTags(Tag)
            .WithSummary("A privilege log version: source, template, metadata (exclusion rules with their counts) and the files' SHA-256.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeLogResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet(LogsPath + "/{logId}/entries", ListEntriesAsync)
            .WithName("ListPrivilegeLogEntries")
            .WithTags(Tag)
            .WithSummary("The rows of a privilege log version in log order, their cells in column order.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<CursorPage<PrivilegeLogEntryResource>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet(LogsPath + "/{logId}/changes", ChangesAsync)
            .WithName("GetPrivilegeLogChanges")
            .WithTags(Tag)
            .WithSummary("What changed in a privilege log version since the previous version (or the version `against`): added, removed and changed entries.")
            .WithDescription(
                "Entries are matched by document and their cells compared by column header; a changed entry names each changed cell with " +
                "its value before and after. Added and changed entries come in log order, then removed ones. Without an earlier version " +
                "you may read, previousLogId is null and nothing is listed. Pages with cursor and limit.")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeLogChangesResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> ListTemplatesAsync(string workspaceId, HttpContext context, PrivilegeLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.ListTemplatesAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var list = outcome.Value!;
        return TypedResults.Ok(new PrivilegeLogTemplateList(
            [.. list.Templates.Select(ToResource)],
            [.. list.Presets.Select(p => new PrivilegeLogPresetResource(p.Preset, PrivilegeLogTemplateRules.PresetName(p.Preset), p.Definition))]));
    }

    internal static async Task<IResult> CreateTemplateAsync(
        string workspaceId, PrivilegeLogTemplateRequest request, HttpContext context, PrivilegeLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.SaveTemplateAsync(access.Principal, access.WorkspaceId, null, null, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != PrivilegeLogStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Reason, NoTemplate);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{TemplatesPath}/{outcome.Value.TemplateId}", ToResource(outcome.Value));
    }

    internal static async Task<IResult> GetTemplateAsync(
        string workspaceId, string templateId, HttpContext context, PrivilegeLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(templateId, out var id)
            || await service.GetTemplateAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } template)
        {
            return Problems.NotFound(NoTemplate);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(template.Version);
        return TypedResults.Ok(ToResource(template));
    }

    internal static async Task<IResult> UpdateTemplateAsync(
        string workspaceId, string templateId, PrivilegeLogTemplateRequest request, HttpContext context, PrivilegeLogService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(templateId, out var id)
            || await service.GetTemplateAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NoTemplate);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.SaveTemplateAsync(access.Principal, access.WorkspaceId, id, current.Version, request, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status != PrivilegeLogStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Reason, NoTemplate);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.Version);
        return TypedResults.Ok(ToResource(outcome.Value));
    }

    internal static async Task<IResult> GenerateAsync(
        string workspaceId, GeneratePrivilegeLogRequest request, HttpContext context, PrivilegeLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        PrivilegeLogSavedSearch? savedSearch = null;
        if (request?.SavedSearchId is { } savedSearchId)
        {
            if (request.ProductionId is not null || request.SnapshotId is not null)
            {
                return Problems.Validation(new Dictionary<string, string[]>
                {
                    ["savedSearchId"] = ["Give a production, a frozen set or a saved search, not several."],
                });
            }

            var frozen = await FreezeSavedSearchAsync(context, access.Principal, access.WorkspaceId, savedSearchId, cancellationToken).ConfigureAwait(false);
            if (frozen.Problem is { } problem)
            {
                return problem;
            }

            savedSearch = frozen.SavedSearch;
            request = request with { SnapshotId = frozen.SnapshotId };
        }

        var outcome = await service.GenerateAsync(access.Principal, access.WorkspaceId, request!, savedSearch, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != PrivilegeLogStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Reason, "No such production, frozen set or saved search.");
        }

        var creation = outcome.Value!;
        var body = new PrivilegeLogGenerationResource(ToResource(creation.Log), creation.Unchanged);
        return creation.Unchanged
            ? TypedResults.Ok(body)
            : TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{LogsPath}/{creation.Log.LogId}", body);
    }

    internal static async Task<IResult> ListAsync(
        string workspaceId, [FromQuery(Name = "productionId")] Guid? productionId, [FromQuery(Name = "snapshotId")] Guid? snapshotId,
        [FromQuery(Name = "savedSearchId")] Guid? savedSearchId, [AsParameters] PageQuery page, HttpContext context, PrivilegeLogService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        (DateTimeOffset, Guid)? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var at, var id]
                || !long.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParse(id, out var logId))
            {
                return PageCursor.Invalid();
            }

            after = (new DateTimeOffset(ticks, TimeSpan.Zero), logId);
        }

        var (items, next) = await service.ListAsync(access.Principal, access.WorkspaceId, productionId, snapshotId, savedSearchId, after, page.EffectiveLimit,
            cancellationToken).ConfigureAwait(false);
        var nextCursor = next is { } n
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, n.At.UtcTicks.ToString(CultureInfo.InvariantCulture), n.LogId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<PrivilegeLogResource>([.. items.Select(ToResource)], nextCursor,
            new TotalCount(items.Count, nextCursor is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> GetAsync(string workspaceId, string logId, HttpContext context, PrivilegeLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        return await ReadableAsync(context, logId, service, cancellationToken).ConfigureAwait(false) is { } log
            ? TypedResults.Ok(ToResource(log))
            : Problems.NotFound(NoLog);
    }

    internal static async Task<IResult> ListEntriesAsync(
        string workspaceId, string logId, [AsParameters] PageQuery page, HttpContext context, PrivilegeLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (await ReadableAsync(context, logId, service, cancellationToken).ConfigureAwait(false) is not { } log)
        {
            return Problems.NotFound(NoLog);
        }

        var access = context.GetWorkspaceAccess()!;
        var after = 0;
        if (page.Cursor is { } cursor
            && (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var id, var ordinal]
                || id != log.LogId.ToString("N") || !int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out after)))
        {
            return PageCursor.Invalid();
        }

        var limit = page.EffectiveLimit;
        var rows = await service.ReadEntriesAsync(log, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = rows.Take(limit).Select(r => new PrivilegeLogEntryResource(r.Ordinal, r.DocumentId, Treatment(r.Treatment), r.Cells)).ToList();
        var next = rows.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, log.LogId.ToString("N"), items[^1].Ordinal.ToString(CultureInfo.InvariantCulture))
            : null;
        return TypedResults.Ok(new CursorPage<PrivilegeLogEntryResource>(items, next, new TotalCount(log.EntryCount, TotalRelation.Eq)));
    }

    internal static async Task<IResult> ChangesAsync(
        string workspaceId, string logId, [FromQuery(Name = "against")] Guid? against, [AsParameters] PageQuery page, HttpContext context,
        PrivilegeLogService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (await ReadableAsync(context, logId, service, cancellationToken).ConfigureAwait(false) is not { } log)
        {
            return Problems.NotFound(NoLog);
        }

        var access = context.GetWorkspaceAccess()!;
        var againstKey = against?.ToString("N") ?? "-";
        var offset = 0;
        if (page.Cursor is { } cursor
            && (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is not [var id, var other, var position]
                || id != log.LogId.ToString("N") || other != againstKey
                || !int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out offset)))
        {
            return PageCursor.Invalid();
        }

        var outcome = await service.CompareAsync(access.Principal, log, against, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != PrivilegeLogStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Reason, NoLog);
        }

        var (current, previous, comparison) = outcome.Value!;
        if (previous is null || comparison is null)
        {
            return TypedResults.Ok(new PrivilegeLogChangesResource(current.LogId, current.Version, null, null, 0, 0, 0, 0, [], [], [], null));
        }

        var limit = page.EffectiveLimit;
        var items = comparison.Differences.Skip(offset).Take(limit).Select(ToChange).ToList();
        var next = offset + items.Count < comparison.Differences.Count
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, log.LogId.ToString("N"), againstKey,
                (offset + items.Count).ToString(CultureInfo.InvariantCulture))
            : null;
        return TypedResults.Ok(new PrivilegeLogChangesResource(current.LogId, current.Version, previous.LogId, previous.Version, comparison.Added,
            comparison.Removed, comparison.Changed, comparison.Unchanged, comparison.ColumnsAdded, comparison.ColumnsRemoved, items, next));
    }

    private static PrivilegeLogEntryChange ToChange(PrivilegeLogEntryDifference difference)
    {
        var row = difference.Current ?? difference.Previous!;
        var kind = difference.Current is null ? PrivilegeLogChangeKind.Removed
            : difference.Previous is null ? PrivilegeLogChangeKind.Added
            : PrivilegeLogChangeKind.Changed;
        return new PrivilegeLogEntryChange(kind, row.DocumentId, difference.Current?.Ordinal, difference.Previous?.Ordinal, Treatment(row.Treatment), row.Cells,
            [.. difference.ChangedCells.Select(c => new PrivilegeLogCellChange(c.Column, c.Before, c.After))]);
    }

    private sealed record SavedSearchFreeze(Guid SnapshotId, PrivilegeLogSavedSearch? SavedSearch, IResult? Problem);

    /// <summary>
    /// Freezes a saved search the caller can see, as they see it now, into a Report frozen set (Search.Execute; members
    /// authorized with Document.View), as <c>POST …/snapshots</c> does. A selection too large to freeze in the request
    /// is refused: a log holds at most <see cref="PrivilegeLogService.MaxEntries"/> documents.
    /// </summary>
    private static async Task<SavedSearchFreeze> FreezeSavedSearchAsync(
        HttpContext context, Application.Authorization.SecurityPrincipal principal, Guid workspaceId, Guid savedSearchId, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        if (services.GetService<ISavedSearchQueries>() is not { } savedSearches
            || await savedSearches.FindForRunAsync(principal, workspaceId, savedSearchId, cancellationToken).ConfigureAwait(false) is not { } saved)
        {
            return new SavedSearchFreeze(Guid.Empty, null, Problems.NotFound("No such saved search."));
        }

        var session = Guid.TryParse(context.User.FindFirst(OpportunityClaimTypes.SessionId)?.Value, out var sid) ? sid : (Guid?)null;
        var snapshots = services.GetRequiredService<DocumentSetSnapshotService>();
        var name = "Privilege log: " + saved.Name;
        var outcome = await snapshots.CreateAsync(new SearchCaller(principal, workspaceId, session), new SnapshotCreateRequest(
            SnapshotPurpose.Report,
            name.Length > 200 ? name[..200] : name,
            $"{SavedSearchQuerySyntax.FieldName}:{savedSearchId:D}",
            Expansion: saved.Expansion), cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            SnapshotCreateStatus.Ready => new SavedSearchFreeze(outcome.Snapshot!.SnapshotId, new PrivilegeLogSavedSearch(saved.SavedSearchId, saved.Name), null),
            SnapshotCreateStatus.InvalidRequest or SnapshotCreateStatus.InvalidQuery => new SavedSearchFreeze(Guid.Empty, null,
                Problems.Validation(new Dictionary<string, string[]> { ["savedSearchId"] = ["The saved search cannot be run; open it to see why."] })),
            SnapshotCreateStatus.Forbidden => new SavedSearchFreeze(Guid.Empty, null,
                Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.")),
            SnapshotCreateStatus.Accepted or SnapshotCreateStatus.TooLarge => new SavedSearchFreeze(Guid.Empty, null,
                Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.UnprocessableContent,
                    $"The saved search selects more than {PrivilegeLogService.MaxEntries.ToString("N0", CultureInfo.InvariantCulture)} documents; " +
                    "narrow it or generate the log from a frozen set.")),
            SnapshotCreateStatus.Failed => new SavedSearchFreeze(Guid.Empty, null,
                Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "The selection changed while it was being frozen; try again.")),
            _ => new SavedSearchFreeze(Guid.Empty, null, Problems.NotFound("No such saved search.")),
        };
    }

    /// <summary>The version named by the route when the caller may read it (Q-52), else null.</summary>
    internal static async Task<PrivilegeLogRecord?> ReadableAsync(HttpContext context, string logId, PrivilegeLogService service, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(logId, out var id))
        {
            return null;
        }

        return await service.GetAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
    }

    internal static PrivilegeLogResource ToResource(PrivilegeLogRecord log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return new PrivilegeLogResource(
            log.LogId,
            log.Version,
            PrivilegeLogService.SourceKind(log.Source),
            log.ProductionId,
            log.SnapshotId,
            log.ScopeSnapshotId,
            log.TemplateId,
            log.TemplateName,
            Convert.ToHexStringLower(log.ContentSha256),
            [
                new PrivilegeLogFileResource("csv", Convert.ToHexStringLower(log.CsvSha256), log.CsvBytes),
                new PrivilegeLogFileResource("xlsx", Convert.ToHexStringLower(log.XlsxSha256), log.XlsxBytes),
            ],
            PrivilegeLogFiles.DeserializeMetadata(log.Metadata),
            new ReviewerResource(log.GeneratedBy, log.GeneratedByDisplay),
            log.GeneratedAt,
            log.SavedSearchId,
            PrivilegeLogService.DescriptionColumn(log));
    }

    internal static PrivilegeLogTemplateResource ToResource(PrivilegeLogTemplateRecord template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new PrivilegeLogTemplateResource(template.TemplateId, template.Name, PrivilegeLogTemplateRules.Deserialize(template.DefinitionJson),
            new ReviewerResource(template.ModifiedBy, template.ModifiedByDisplay), template.ModifiedAt, template.Version);
    }

    private static PrivilegeLogTreatment Treatment(PrivilegeLogEntryTreatment treatment) => treatment switch
    {
        PrivilegeLogEntryTreatment.Withheld => PrivilegeLogTreatment.Withheld,
        PrivilegeLogEntryTreatment.Redacted => PrivilegeLogTreatment.Redacted,
        _ => PrivilegeLogTreatment.RedactedPrivacy,
    };

    internal static IResult Problem(PrivilegeLogStatus status, IReadOnlyDictionary<string, string[]>? errors, string? reason, string notFound) => status switch
    {
        PrivilegeLogStatus.Invalid => Problems.Validation(errors?.ToDictionary(e => e.Key, e => e.Value) ?? new Dictionary<string, string[]>()),
        PrivilegeLogStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation."),
        PrivilegeLogStatus.InvalidState => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, reason),
        PrivilegeLogStatus.TooLarge => Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.UnprocessableContent, reason),
        PrivilegeLogStatus.NameConflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "A privilege log template with this name already exists."),
        PrivilegeLogStatus.VersionConflict => Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
            "The template was modified since it was read."),
        PrivilegeLogStatus.IntegrityFailure => Problems.Create(StatusCodes.Status500InternalServerError, ProblemCodes.Internal, reason),
        _ => Problems.NotFound(reason ?? notFound),
    };
}

public static class PrivilegeLogEndpointRegistration
{
    /// <summary>The privilege log API, its use cases and PostgreSQL store (needs the production, export and snapshot stores).</summary>
    public static IServiceCollection AddPrivilegeLogEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresPrivilegeLogStore();
        services.AddPostgresProductionStore();
        services.AddPostgresSnapshotStore();
        services.AddPostgresExportStore();
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.TryAddScoped<PrivilegeLogService>();
        services.AddSingleton<IApiEndpointModule, PrivilegeLogEndpoints>();
        return services;
    }
}
