using System.Globalization;

using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Core.Documents;
using Opportunity.Core.Security;

namespace Opportunity.Application.Audit;

public enum AuditLogStatus
{
    Ok,

    /// <summary>The caller lacks a permission the request needs (search text, or an export under break-glass).</summary>
    Forbidden,

    /// <summary>A filter is invalid (e.g. an action without its category, or no visible document with that Control Number).</summary>
    Invalid,

    NotFound,

    /// <summary>The export has not completed (or failed).</summary>
    NotReady,

}

public sealed record AuditLogResult(AuditLogStatus Status, AuditEventPage? Page = null, IReadOnlyDictionary<string, string[]>? Errors = null, string? Detail = null);

public sealed record AuditExportOutcome(
    AuditLogStatus Status, AuditExportCreation? Creation = null, IReadOnlyDictionary<string, string[]>? Errors = null, string? Detail = null);

public sealed record AuditDownloadOutcome(AuditLogStatus Status, AuditExportRecord? Export = null, string? Detail = null, Guid? AuditEventId = null);

/// <summary>The viewer's request: the filters, a document by Control Number (resolved for the reader) and whether to include search text.</summary>
public sealed record AuditLogRequest(AuditLogFilters Filters, string? ControlNumber = null, bool IncludeSearchText = false);

/// <summary>
/// The audit log viewer and audit exports (E14-T04, ADR-013 §8). PEP-1 has required <c>Audit.Read</c> in the workspace
/// (Workspace Admin, Auditor; break-glass reads it too, Q-45). This service adds the rest of §8:
/// <list type="bullet">
/// <item>search text (<c>RestrictedDetails</c>) only with <c>Audit.ReadSearchText</c> (Q-16);</item>
/// <item>walls and restriction classes apply to the reader: events whose resource is a document the reader may not see
/// are left out entirely, and a document filter only finds documents the reader may see (Q-52);</item>
/// <item>every page read is audited as <c>Audit.Queried</c> in the read's transaction (the reader port), an export
/// request as <c>Audit.Exported</c> with its job, and each download of the file as <c>Audit.ExportDownloaded</c>.</item>
/// </list>
/// An export runs as an <c>AuditExport</c> job (<see cref="AuditExportRunner"/>) with the initiator's visibility re-checked
/// for every chunk (Q-15); its file is readable by its initiator only, who must still hold the permissions it needed.
/// Exports are refused while the caller's audit access relies on break-glass, which is read-only (Q-45).
/// </summary>
public sealed class AuditLogService(
    IAuditEventReader reader,
    IAuditLogStore store,
    IAuditEventWriter audit,
    IAuthorizationService authorization,
    IJobRepository jobs,
    TimeProvider time)
{
    public const int MaxPageSize = 200;

    public async Task<AuditLogResult> QueryAsync(
        SecurityPrincipal principal, Guid workspaceId, AuditLogRequest request, AuditCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request.Filters) is { } errors)
        {
            return new AuditLogResult(AuditLogStatus.Invalid, Errors: errors);
        }

        var read = await authorization.AuthorizeAsync(principal, workspaceId, Permission.AuditRead, cancellationToken).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            return new AuditLogResult(read.Outcome == AuthorizationOutcome.NotFound ? AuditLogStatus.NotFound : AuditLogStatus.Forbidden);
        }

        if (request.IncludeSearchText
            && !(await authorization.AuthorizeAsync(principal, workspaceId, Permission.AuditReadSearchText, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return new AuditLogResult(AuditLogStatus.Forbidden, Detail: "Reading search text in the audit trail needs Audit.ReadSearchText.");
        }

        var visibility = await VisibilityAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var (filters, invalid) = await ResolveAsync(workspaceId, request, visibility, cancellationToken).ConfigureAwait(false);
        if (invalid is not null)
        {
            return new AuditLogResult(AuditLogStatus.Invalid, Errors: invalid);
        }

        var query = filters.ToQuery(workspaceId) with
        {
            NewestFirst = true,
            DocumentVisibility = visibility,
            IncludeRestrictedDetails = request.IncludeSearchText,
            After = after,
            Limit = Math.Clamp(limit, 1, MaxPageSize),
        };
        var page = await reader.QueryAsync(query, Actor(principal, workspaceId, read.BreakGlass), cancellationToken).ConfigureAwait(false);
        return new AuditLogResult(AuditLogStatus.Ok, page);
    }

    public async Task<AuditExportOutcome> CreateExportAsync(
        SecurityPrincipal principal, Guid workspaceId, AuditLogRequest request, string? clientIdempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request.Filters) is { } errors)
        {
            return new AuditExportOutcome(AuditLogStatus.Invalid, Errors: errors);
        }

        var read = await authorization.AuthorizeAsync(principal, workspaceId, Permission.AuditRead, cancellationToken).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            return new AuditExportOutcome(read.Outcome == AuthorizationOutcome.NotFound ? AuditLogStatus.NotFound : AuditLogStatus.Forbidden);
        }

        if (read.BreakGlass)
        {
            return new AuditExportOutcome(AuditLogStatus.Forbidden, Detail: "Break-glass access is read-only: it cannot export the audit trail.");
        }

        if (request.IncludeSearchText
            && !(await authorization.AuthorizeAsync(principal, workspaceId, Permission.AuditReadSearchText, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return new AuditExportOutcome(AuditLogStatus.Forbidden, Detail: "Exporting search text from the audit trail needs Audit.ReadSearchText.");
        }

        var visibility = await VisibilityAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var (filters, invalid) = await ResolveAsync(workspaceId, request, visibility, cancellationToken).ConfigureAwait(false);
        if (invalid is not null)
        {
            return new AuditExportOutcome(AuditLogStatus.Invalid, Errors: invalid);
        }

        var now = time.GetUtcNow();
        var exportId = Guid.CreateVersion7();
        var details = filters.ToDetails();
        details["ExportId"] = exportId.ToString();
        details["IncludeSearchText"] = request.IncludeSearchText ? "true" : "false";
        var requested = Actor(principal, workspaceId, breakGlass: false) with
        {
            Category = AuditTaxonomy.Audit.Category,
            Action = AuditTaxonomy.Audit.Exported,
            ResourceType = AuditTaxonomy.Audit.ExportResourceType,
            ResourceId = exportId.ToString(),
            Details = details,
        };
        var creation = await store.CreateAsync(new NewAuditExport
        {
            WorkspaceId = workspaceId,
            ExportId = exportId,
            Filters = filters,
            IncludeSearchText = request.IncludeSearchText,
            OccurredBefore = filters.To is { } to && to < now ? to : now,
            CreatedBy = principal.UserId,
            CreatedByDisplay = Display(principal),
            CreatedByGroups = principal.Groups,
            ClientIdempotencyKey = clientIdempotencyKey,
            CorrelationId = principal.CorrelationId,
            Requested = requested,
        }, cancellationToken).ConfigureAwait(false);
        return new AuditExportOutcome(AuditLogStatus.Ok, creation);
    }

    /// <summary>The caller's own export, or null (another user's export does not exist for the caller).</summary>
    public async Task<AuditExportRecord?> GetExportAsync(SecurityPrincipal principal, Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var export = await store.GetAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false);
        return export?.CreatedBy == principal.UserId ? export : null;
    }

    public Task<IReadOnlyList<AuditExportRecord>> ListExportsAsync(
        SecurityPrincipal principal, Guid workspaceId, (DateTimeOffset CreatedAt, Guid ExportId)? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return store.ListAsync(workspaceId, principal.UserId, after, Math.Clamp(limit, 1, MaxPageSize), cancellationToken);
    }

    /// <summary>
    /// Checks that the caller may read the export's file now and writes <c>Audit.ExportDownloaded</c> (durable before the
    /// first byte); the caller then streams it with <see cref="WriteFileAsync"/>.
    /// </summary>
    public async Task<AuditDownloadOutcome> OpenDownloadAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        if (await GetExportAsync(principal, workspaceId, exportId, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return new AuditDownloadOutcome(AuditLogStatus.NotFound);
        }

        if (export.Status != AuditExportStatus.Completed)
        {
            return new AuditDownloadOutcome(AuditLogStatus.NotReady, export,
                export.Status == AuditExportStatus.Failed ? "The export failed; run it again." : "The export has not finished yet.");
        }

        var read = await authorization.AuthorizeAsync(principal, workspaceId, Permission.AuditRead, cancellationToken).ConfigureAwait(false);
        if (!read.IsAllowed || read.BreakGlass
            || (export.IncludeSearchText
                && !(await authorization.AuthorizeAsync(principal, workspaceId, Permission.AuditReadSearchText, cancellationToken).ConfigureAwait(false)).IsAllowed))
        {
            return new AuditDownloadOutcome(AuditLogStatus.Forbidden, export, "You may no longer read this export.");
        }

        var downloaded = Actor(principal, workspaceId, breakGlass: false) with
        {
            Category = AuditTaxonomy.Audit.Category,
            Action = AuditTaxonomy.Audit.ExportDownloaded,
            ResourceType = AuditTaxonomy.Audit.ExportResourceType,
            ResourceId = export.ExportId.ToString(),
            JobId = export.JobId,
            Details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ExportId"] = export.ExportId.ToString(),
                ["Rows"] = export.RowCount?.ToString(CultureInfo.InvariantCulture),
                ["Bytes"] = export.ByteCount?.ToString(CultureInfo.InvariantCulture),
                ["Sha256"] = export.ContentSha256,
            },
        };
        await audit.WriteAsync(downloaded, cancellationToken).ConfigureAwait(false);
        return new AuditDownloadOutcome(AuditLogStatus.Ok, export, AuditEventId: downloaded.EventId);
    }

    /// <summary>The export's file: the header and the parts in order.</summary>
    public async Task WriteFileAsync(AuditExportRecord export, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(AuditExportCsv.Header(export.IncludeSearchText), cancellationToken).ConfigureAwait(false);
        await store.ReadPartsAsync(export.WorkspaceId, export.ExportId,
            part => output.WriteAsync(part, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The reader's document visibility (ADR-013 §8.3): the search visibility filter, or every document event hidden when
    /// the reader has none (no <c>Search.Execute</c>). An active break-glass activation sees every document (Q-45).
    /// </summary>
    internal static async Task<AuditDocumentVisibility> VisibilityAsync(
        IAuthorizationService authorization, SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken)
    {
        var visibility = await authorization.GetVisibilityAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return visibility.Filter is { } filter
            ? new AuditDocumentVisibility(filter.DeniedClasses, filter.WallIds)
            : new AuditDocumentVisibility([], [], HideAllDocuments: true);
    }

    /// <summary>Category and action must name the closed taxonomy (an action only with its category); the range must be ordered.</summary>
    public static IReadOnlyDictionary<string, string[]>? Validate(AuditLogFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (filters.Action is not null && filters.Category is null)
        {
            errors["action"] = ["Choose the category of the action."];
        }
        else if (filters.Category is { } category
            && (filters.Action is { } action ? !AuditTaxonomy.IsDefined(category, action) : AuditTaxonomy.All.All(a => a.Category != category)))
        {
            errors[filters.Action is null ? "category" : "action"] = ["Not an audit category or action."];
        }

        if (filters is { From: { } from, To: { } to } && to <= from)
        {
            errors["to"] = ["The end of the date range must be after its start."];
        }

        if (filters.ActorId is { Length: > AuditEventRules.MaxActorIdLength })
        {
            errors["actorId"] = ["Not a user."];
        }

        return errors.Count == 0 ? null : errors;
    }

    private Task<AuditDocumentVisibility> VisibilityAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken) =>
        VisibilityAsync(authorization, principal, workspaceId, cancellationToken);

    /// <summary>
    /// The document filter (by Control Number or ID) must name a document the reader may see, and the job filter a job of
    /// the workspace; otherwise the filter is invalid, the same whether the document is hidden, in another workspace or
    /// nowhere (Q-52).
    /// </summary>
    private async Task<(AuditLogFilters Filters, IReadOnlyDictionary<string, string[]>? Errors)> ResolveAsync(
        Guid workspaceId, AuditLogRequest request, AuditDocumentVisibility visibility, CancellationToken cancellationToken)
    {
        var filters = request.Filters;
        if (request.ControlNumber is not null || filters.DocumentId is not null)
        {
            if (await store.FindDocumentAsync(workspaceId, request.ControlNumber, filters.DocumentId, visibility, cancellationToken).ConfigureAwait(false)
                is not { } documentId)
            {
                return (filters, new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [request.ControlNumber is null ? "documentId" : "controlNumber"] = ["No such document."],
                });
            }

            filters = filters with { DocumentId = documentId };
        }

        if (filters.JobId is { } jobId && await jobs.GetAsync(workspaceId, jobId, cancellationToken).ConfigureAwait(false) is null)
        {
            return (filters, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["jobId"] = ["No such job."] });
        }

        return (filters, null);
    }

    private AuditEvent Actor(SecurityPrincipal principal, Guid workspaceId, bool breakGlass) => new()
    {
        WorkspaceId = workspaceId,
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Audit.Category,
        Action = AuditTaxonomy.Audit.Queried,
        ActorType = AuditActorType.User,
        ActorId = principal.UserId.ToString(),
        ActorDisplay = Display(principal),
        AccessPath = breakGlass ? AuditAccessPath.BreakGlass : AuditAccessPath.Normal,
        ClientIp = principal.ClientIp,
        UserAgent = principal.UserAgent,
        CorrelationId = principal.CorrelationId,
        Outcome = AuditOutcome.Success,
    };

    private static string Display(SecurityPrincipal principal)
    {
        var display = string.IsNullOrWhiteSpace(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName;
        return display.Length > AuditEventRules.MaxActorDisplayLength ? display[..AuditEventRules.MaxActorDisplayLength] : display;
    }
}

/// <summary>Control Number normalization for a lookup: the workspace's rule decides which of the two forms applies.</summary>
public static class AuditControlNumber
{
    /// <summary>The case-insensitive and case-sensitive normal forms, or null when the input is not a Control Number.</summary>
    public static (string Insensitive, string Sensitive)? Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return ControlNumber.TryNormalize(raw, caseSensitive: false, null, out var insensitive, out _)
               && ControlNumber.TryNormalize(raw, caseSensitive: true, null, out var sensitive, out _)
            ? (insensitive, sensitive)
            : null;
    }
}
