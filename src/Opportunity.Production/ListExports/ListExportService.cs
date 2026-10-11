using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.ListExports;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Production.ListExports;

public enum ListExportCreateStatus
{
    /// <summary>The export and its job exist; the selection is frozen, or is being frozen in the background.</summary>
    Accepted,
    Invalid,
    InvalidQuery,
    NotFound,
    Forbidden,

    /// <summary>The selection is larger than a frozen set may be.</summary>
    TooLarge,

    /// <summary>The selection could not be frozen (it changed while being read); nothing was created.</summary>
    Failed,
}

public sealed record ListExportCreateOutcome(
    ListExportCreateStatus Status,
    ListExportCreation? Creation = null,
    SnapshotRecord? Snapshot = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    IReadOnlyList<QueryValidationDiagnostic>? QueryErrors = null,
    long? Hits = null);

/// <summary>
/// Creates document list exports (#181): the caller's view columns are checked against the field catalogue (a field
/// the caller may not see does not exist for them), the list's search or checked rows are frozen into a
/// <see cref="SnapshotPurpose.ListExport"/> snapshot as the caller (members re-authorized with
/// <c>Document.ExportList</c>, ADR-002 §5.2.2), and the export is stored with its job and <c>Export.ListRequested</c>.
/// The endpoint has already required <c>Document.ExportList</c>; <see cref="ListExportRunner"/> writes the file.
/// </summary>
public sealed class ListExportService(
    IListExportStore store,
    DocumentSetSnapshotService snapshots,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    TimeProvider time)
{
    /// <summary>The most view columns one export takes (besides Control Number).</summary>
    public const int MaxColumns = 500;

    public const int MaxNameLength = SnapshotRules.MaxNameLength;

    public async Task<ListExportCreateOutcome> CreateAsync(
        SearchCaller caller, ListExportCreateRequest request, string? clientIdempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var principal = caller.Principal;
        var ws = caller.WorkspaceId;

        var decision = await authorization.AuthorizeAsync(principal, ws, Permission.DocumentExportList, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new ListExportCreateOutcome(decision.Outcome == AuthorizationOutcome.NotFound ? ListExportCreateStatus.NotFound : ListExportCreateStatus.Forbidden);
        }

        if (decision.BreakGlass)
        {
            // Break-glass is read-only (Q-45): it never exports.
            return new ListExportCreateOutcome(ListExportCreateStatus.Forbidden);
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? SnapshotRules.DefaultName(SnapshotPurpose.ListExport, time.GetUtcNow()) : request.Name.Trim();
        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return Invalid("name", $"A name has 1 to {MaxNameLength} characters and no control characters.");
        }

        if ((request.Query is null) == (request.DocumentIds is null))
        {
            return Invalid("query", "Give exactly one source: query (the list's search) or documentIds (checked rows).");
        }

        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, principal, catalog, cancellationToken).ConfigureAwait(false);
        if (Columns(request.FieldIds, catalog, restricted, out var errors) is not { } columns)
        {
            return new ListExportCreateOutcome(ListExportCreateStatus.Invalid, Errors: errors);
        }

        var frozen = await snapshots.CreateAsync(caller, new SnapshotCreateRequest(
            SnapshotPurpose.ListExport,
            name,
            request.Query,
            request.DocumentIds,
            ClientIdempotencyKey: clientIdempotencyKey is null ? null : "list-export:" + clientIdempotencyKey,
            Expansion: request.Expansion), cancellationToken).ConfigureAwait(false);
        switch (frozen.Status)
        {
            case SnapshotCreateStatus.Ready or SnapshotCreateStatus.Accepted:
                break;
            case SnapshotCreateStatus.InvalidRequest:
                return new ListExportCreateOutcome(ListExportCreateStatus.Invalid, Errors: frozen.RequestErrors);
            case SnapshotCreateStatus.InvalidQuery:
                return new ListExportCreateOutcome(ListExportCreateStatus.InvalidQuery, QueryErrors: frozen.QueryErrors);
            case SnapshotCreateStatus.Forbidden:
                return new ListExportCreateOutcome(ListExportCreateStatus.Forbidden);
            case SnapshotCreateStatus.TooLarge:
                return new ListExportCreateOutcome(ListExportCreateStatus.TooLarge, Hits: frozen.Hits);
            case SnapshotCreateStatus.Failed:
                return new ListExportCreateOutcome(ListExportCreateStatus.Failed);
            default:
                return new ListExportCreateOutcome(ListExportCreateStatus.NotFound);
        }

        var snapshot = frozen.Snapshot!;
        var display = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName;
        var creation = await store.CreateAsync(new NewListExport
        {
            WorkspaceId = ws,
            SnapshotId = snapshot.SnapshotId,
            Name = name,
            Columns = columns,
            CreatedBy = principal.UserId,
            CreatedByDisplay = display,
            CreatedByGroups = principal.Groups,
            ClientIdempotencyKey = clientIdempotencyKey,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
            Requested = new AuditEvent
            {
                OccurredAt = time.GetUtcNow(),
                Category = AuditTaxonomy.Export.Category,
                Action = AuditTaxonomy.Export.ListRequested,
                ActorType = AuditActorType.User,
                ActorId = principal.UserId.ToString(),
                ActorDisplay = display,
                ClientIp = principal.ClientIp,
                UserAgent = principal.UserAgent,
                CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
                Outcome = AuditOutcome.Success,
                SnapshotId = snapshot.SnapshotId,
                SearchGeneration = snapshot.SearchGeneration,
                Details = new Dictionary<string, string?>
                {
                    ["SnapshotId"] = snapshot.SnapshotId.ToString(),
                    ["Source"] = request.Query is null ? "documentIds" : "query",
                    ["Columns"] = string.Join(',', columns.Select(c => c.FieldId.ToString(CultureInfo.InvariantCulture))),
                    ["Documents"] = snapshot.DocumentCount?.ToString(CultureInfo.InvariantCulture),
                }.Where(d => d.Value is not null).ToDictionary(),
            },
        }, cancellationToken).ConfigureAwait(false);
        return new ListExportCreateOutcome(ListExportCreateStatus.Accepted, creation, snapshot);
    }

    /// <summary>The export when <paramref name="principal"/> created it (exports are private to their creator).</summary>
    public async Task<ListExportRecord?> GetOwnAsync(SecurityPrincipal principal, Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var export = await store.GetAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false);
        return export?.CreatedBy == principal.UserId ? export : null;
    }

    /// <summary>
    /// Control Number, then the requested fields in order (duplicates and Control Number itself skipped), each headed by
    /// its display name; unknown, deleted and restricted fields are refused alike.
    /// </summary>
    internal static List<ListExportColumn>? Columns(
        IReadOnlyList<int>? fieldIds, FieldCatalog catalog, IReadOnlySet<int> restricted, out Dictionary<string, string[]> errors)
    {
        errors = [];
        if (fieldIds is null)
        {
            errors["fieldIds"] = ["Give the view's columns (field IDs); an empty list exports Control Number only."];
            return null;
        }

        if (fieldIds.Count > MaxColumns)
        {
            errors["fieldIds"] = [$"At most {MaxColumns} columns."];
            return null;
        }

        var control = catalog.Find(SystemFields.ControlNumber);
        var columns = new List<ListExportColumn> { new(SystemFields.ControlNumber, control?.Name ?? "Control Number") };
        var seen = new HashSet<int> { SystemFields.ControlNumber };
        for (var i = 0; i < fieldIds.Count; i++)
        {
            var id = fieldIds[i];
            if (catalog.Find(id) is not { IsDeleted: false } field || restricted.Contains(id))
            {
                errors[$"fieldIds[{i}]"] = ["No such field."];
                continue;
            }

            if (seen.Add(id))
            {
                columns.Add(new ListExportColumn(id, field.Name));
            }
        }

        return errors.Count == 0 ? columns : null;
    }

    private static ListExportCreateOutcome Invalid(string key, string message) =>
        new(ListExportCreateStatus.Invalid, Errors: new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>What to export: the view's field IDs and exactly one source (a query or checked document IDs).</summary>
public sealed record ListExportCreateRequest(
    IReadOnlyList<int>? FieldIds,
    string? Query,
    IReadOnlyList<Guid>? DocumentIds,
    RelationshipExpansion Expansion,
    string? Name);
