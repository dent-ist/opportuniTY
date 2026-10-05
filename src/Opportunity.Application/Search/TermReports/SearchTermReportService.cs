using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchTermReports;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Search.TermReports;

public enum SearchTermReportOutcomeStatus
{
    Accepted,
    Invalid,
    NotFound,
    Forbidden,

    /// <summary>The report's current run has not finished (rerun), or the request conflicts with its state.</summary>
    Conflict,
}

public sealed record SearchTermReportOutcome(
    SearchTermReportOutcomeStatus Status, SearchTermReportRecord? Report = null, IReadOnlyDictionary<string, string[]>? Errors = null, string? Detail = null)
{
    public static SearchTermReportOutcome Of(SearchTermReportOutcomeStatus status, string? detail = null) => new(status, Detail: detail);

    public static SearchTermReportOutcome Invalid(string key, string message) =>
        new(SearchTermReportOutcomeStatus.Invalid, Errors: new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>
/// Search term reports (E07-T10, Q-30): create, rerun, read, list and delete. A report counts named terms over a scope
/// (the workspace, a saved search's results or a snapshot) frozen into a materialized Report snapshot
/// (<see cref="SnapshotStrategyRules"/>: the saved report is X and A, always materialized) by its background run, as the
/// executor. Terms that do not parse or bind are stored with their positioned error and count nothing; the report
/// still runs. The endpoints have already required <c>SearchTermReport.Run</c>; this service also requires
/// <c>Search.Execute</c>. A report is visible to its creator and to holders of <c>Job.ViewAll</c> (its counts reflect
/// what its executor could see); its creator or a workspace admin (<c>Workspace.ManageUsers</c>) may rerun or delete it.
/// </summary>
public sealed class SearchTermReportService(
    ISearchTermReportStore store,
    IDocumentSetSnapshotStore snapshots,
    IJobRepository jobs,
    IAuthorizationService authorization,
    QueryValidator validator,
    SearchTermReportSignal signal,
    TimeProvider time,
    IQueryBinder? binder = null,
    ISavedSearchQueries? savedSearches = null)
{
    public const string EmptyTermCode = "EMPTY_TERM";

    public async Task<SearchTermReportOutcome> CreateAsync(
        SecurityPrincipal principal, Guid workspaceId, CreateSearchTermReportRequest request, string? clientIdempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (await DeniedAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (!SearchTermReportRules.IsValidName(request.Name))
        {
            return SearchTermReportOutcome.Invalid("name", $"A name has 1 to {SearchTermReportRules.MaxNameLength} characters and no control characters.");
        }

        if (ParseTerms(request, out var termErrors) is not { } terms)
        {
            return new SearchTermReportOutcome(SearchTermReportOutcomeStatus.Invalid, Errors: termErrors);
        }

        if (request.Scope is not { } scope || !Enum.IsDefined(scope.Kind))
        {
            return SearchTermReportOutcome.Invalid("scope", "Give the scope: workspace, savedSearch (with id) or snapshot (with id).");
        }

        var kind = Enum.Parse<SearchTermReportScopeKind>(scope.Kind.ToString());
        if ((kind == SearchTermReportScopeKind.Workspace) != (scope.Id is null) || scope.Id == Guid.Empty)
        {
            return SearchTermReportOutcome.Invalid("scope.id",
                kind == SearchTermReportScopeKind.Workspace ? "The workspace scope takes no id." : "Give the id of the saved search or snapshot.");
        }

        string? scopeName = null;
        Guid? snapshotId = null;
        switch (kind)
        {
            case SearchTermReportScopeKind.SavedSearch:
                // A saved search the caller cannot see is indistinguishable from one that does not exist (Q-65).
                if (savedSearches is null
                    || await savedSearches.FindForRunAsync(principal, workspaceId, scope.Id!.Value, cancellationToken).ConfigureAwait(false) is not { } saved)
                {
                    return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.NotFound, "No such saved search.");
                }

                scopeName = saved.Name;
                break;
            case SearchTermReportScopeKind.Snapshot:
                var snapshot = await snapshots.GetAsync(workspaceId, scope.Id!.Value, cancellationToken).ConfigureAwait(false);
                if (snapshot is null || !await IsOwnerOrAsync(principal, workspaceId, snapshot.CreatedBy, Permission.JobViewAll, cancellationToken).ConfigureAwait(false))
                {
                    return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.NotFound, "No such snapshot.");
                }

                if (snapshot.Status != SnapshotStatus.Ready)
                {
                    return SearchTermReportOutcome.Invalid("scope.id", "The snapshot is not Ready.");
                }

                scopeName = snapshot.Name;
                // A snapshot frozen for reports is used as it is; any other is re-frozen as a Report snapshot by the run.
                snapshotId = SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.SavedSearchTermReport, snapshot.Purpose) ? snapshot.SnapshotId : null;
                break;
        }

        var stored = new List<NewSearchTerm>(terms.Count);
        foreach (var term in terms)
        {
            stored.Add(new NewSearchTerm(term.Name, term.Expression, await ValidateAsync(principal, workspaceId, term.Expression, cancellationToken).ConfigureAwait(false)));
        }

        var reportId = Guid.CreateVersion7();
        var creation = await store.CreateAsync(new NewSearchTermReport
        {
            WorkspaceId = workspaceId,
            ReportId = reportId,
            Name = request.Name!.Trim(),
            ScopeKind = kind,
            ScopeId = scope.Id,
            ScopeName = scopeName,
            SnapshotId = snapshotId,
            Terms = stored,
            Executor = Executor(principal),
            ClientIdempotencyKey = clientIdempotencyKey,
            JobParameters = Parameters(reportId),
        }, cancellationToken).ConfigureAwait(false);
        signal.Nudge(workspaceId, creation.Report.ReportId);
        return new SearchTermReportOutcome(SearchTermReportOutcomeStatus.Accepted, creation.Report);
    }

    /// <summary>Runs a Completed or Failed report again over the same snapshot, as <paramref name="principal"/>.</summary>
    public async Task<SearchTermReportOutcome> RerunAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (await DeniedAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (await GetVisibleAsync(principal, workspaceId, reportId, cancellationToken).ConfigureAwait(false) is not { } report)
        {
            return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.NotFound, "No such search term report.");
        }

        if (!await IsOwnerOrAsync(principal, workspaceId, report.CreatedBy, Permission.WorkspaceManageUsers, cancellationToken).ConfigureAwait(false))
        {
            return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.Forbidden, "Only the report's creator or a workspace admin may rerun it.");
        }

        if (report.Status is not (SearchTermReportStatus.Completed or SearchTermReportStatus.Failed))
        {
            return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.Conflict, "The report is still running.");
        }

        var errors = new Dictionary<int, SearchTermError>();
        foreach (var term in await store.GetTermsAsync(workspaceId, reportId, cancellationToken).ConfigureAwait(false))
        {
            if (await ValidateAsync(principal, workspaceId, term.Expression, cancellationToken).ConfigureAwait(false) is { } error)
            {
                errors[term.TermNo] = error;
            }
        }

        var rerun = await store.RerunAsync(workspaceId, reportId, Guid.CreateVersion7(), Executor(principal), errors, Parameters(reportId), cancellationToken)
            .ConfigureAwait(false);
        if (rerun is null)
        {
            return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.Conflict, "The report is still running.");
        }

        signal.Nudge(workspaceId, reportId);
        return new SearchTermReportOutcome(SearchTermReportOutcomeStatus.Accepted, rerun);
    }

    /// <summary>Deletes a report (its creator or a workspace admin); a run in progress is cancelled first.</summary>
    public async Task<SearchTermReportOutcome> DeleteAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (await DeniedAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (await GetVisibleAsync(principal, workspaceId, reportId, cancellationToken).ConfigureAwait(false) is not { } report)
        {
            return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.NotFound, "No such search term report.");
        }

        if (!await IsOwnerOrAsync(principal, workspaceId, report.CreatedBy, Permission.WorkspaceManageUsers, cancellationToken).ConfigureAwait(false))
        {
            return SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.Forbidden, "Only the report's creator or a workspace admin may delete it.");
        }

        if (report.Status is SearchTermReportStatus.Queued or SearchTermReportStatus.Running)
        {
            await jobs.CancelAsync(workspaceId, report.JobId, principal.UserId, "The search term report was deleted.", cancellationToken).ConfigureAwait(false);
        }

        var deleted = await store.DeleteAsync(workspaceId, reportId, Audit(principal, report, AuditTaxonomy.SearchTermReport.Deleted,
            new Dictionary<string, string?> { ["name"] = report.Name, ["runs"] = report.RunCount.ToString(CultureInfo.InvariantCulture) }), cancellationToken)
            .ConfigureAwait(false);
        return deleted
            ? new SearchTermReportOutcome(SearchTermReportOutcomeStatus.Accepted, report)
            : SearchTermReportOutcome.Of(SearchTermReportOutcomeStatus.NotFound, "No such search term report.");
    }

    /// <summary>The report when <paramref name="principal"/> may see it: its creator, or a holder of <c>Job.ViewAll</c>.</summary>
    public async Task<SearchTermReportRecord?> GetVisibleAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var report = await store.GetAsync(workspaceId, reportId, cancellationToken).ConfigureAwait(false);
        return report is not null && await IsOwnerOrAsync(principal, workspaceId, report.CreatedBy, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)
            ? report
            : null;
    }

    /// <summary>Whether the caller sees every report (<c>Job.ViewAll</c>) rather than only their own.</summary>
    public async Task<bool> SeesAllAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default) =>
        (await authorization.AuthorizeAsync(principal, workspaceId, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed;

    /// <summary>The audit event of an export of the report (<c>Search.TermReportExported</c>).</summary>
    public AuditEvent ExportAudit(SecurityPrincipal principal, SearchTermReportRecord report, string format) =>
        Audit(principal, report, AuditTaxonomy.SearchTermReport.Exported, new Dictionary<string, string?>
        {
            ["format"] = format,
            ["terms"] = report.TermCount.ToString(CultureInfo.InvariantCulture),
            ["indexCurrent"] = report.IndexCurrent is { } current ? (current ? "true" : "false") : null,
        });

    private AuditEvent Audit(SecurityPrincipal principal, SearchTermReportRecord report, string action, Dictionary<string, string?> details)
    {
        details["reportId"] = report.ReportId.ToString();
        return new AuditEvent
        {
            WorkspaceId = report.WorkspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.SearchTermReport.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
            ResourceType = AuditTaxonomy.SearchTermReport.ResourceType,
            ResourceId = report.ReportId.ToString(),
            Outcome = AuditOutcome.Success,
            JobId = report.JobId,
            SnapshotId = report.SnapshotId,
            SearchGeneration = report.SearchGeneration,
            Details = details.Where(d => d.Value is not null).ToDictionary(),
        };
    }

    /// <summary>The first positioned error of a term, or null when it parses and binds.</summary>
    private async Task<SearchTermError?> ValidateAsync(SecurityPrincipal principal, Guid workspaceId, string expression, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new SearchTermError(EmptyTermCode, "The term has no expression; an empty search would count every document.", null);
        }

        var result = await validator.ValidateAsync(expression, workspaceId, binder, savedSearches, principal, cancellationToken).ConfigureAwait(false);
        return result.Valid || result.Errors.Count == 0
            ? null
            : new SearchTermError(result.Errors[0].Code, result.Errors[0].Message, result.Errors[0].Span.Start);
    }

    private static List<SearchTermInput>? ParseTerms(CreateSearchTermReportRequest request, out Dictionary<string, string[]> errors)
    {
        errors = [];
        List<SearchTermInput> terms;
        if (request.TermsCsv is { } csv)
        {
            if (request.Terms is not null)
            {
                errors["terms"] = ["Give terms or termsCsv, not both."];
                return null;
            }

            if (csv.Length > SearchTermReportRules.MaxCsvLength)
            {
                errors["termsCsv"] = [$"The term list is longer than {SearchTermReportRules.MaxCsvLength:N0} characters."];
                return null;
            }

            if (SearchTermReportRules.ParseCsv(csv, out var error) is not { } parsed)
            {
                errors["termsCsv"] = [error!];
                return null;
            }

            terms = [.. parsed];
        }
        else
        {
            terms = [.. (request.Terms ?? []).Select(t => new SearchTermInput(t?.Name?.Trim() ?? string.Empty, t?.Expression?.Trim() ?? string.Empty))];
        }

        if (terms.Count is 0 or > SearchTermReportRules.MaxTerms)
        {
            errors["terms"] = [$"Give 1 to {SearchTermReportRules.MaxTerms} terms."];
            return null;
        }

        for (var i = 0; i < terms.Count; i++)
        {
            if (!SearchTermReportRules.IsValidName(terms[i].Name))
            {
                errors[$"terms[{i}].name"] = [$"A term name has 1 to {SearchTermReportRules.MaxNameLength} characters and no control characters."];
            }

            if (terms[i].Expression.Length > SearchTermReportRules.MaxExpressionLength)
            {
                errors[$"terms[{i}].expression"] = [$"An expression has at most {SearchTermReportRules.MaxExpressionLength:N0} characters."];
            }
        }

        return errors.Count == 0 ? terms : null;
    }

    private async Task<SearchTermReportOutcome?> DeniedAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(principal, workspaceId, Permission.SearchExecute, cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed
            ? null
            : SearchTermReportOutcome.Of(decision.Outcome == AuthorizationOutcome.NotFound ? SearchTermReportOutcomeStatus.NotFound : SearchTermReportOutcomeStatus.Forbidden);
    }

    private async Task<bool> IsOwnerOrAsync(SecurityPrincipal principal, Guid workspaceId, Guid owner, Permission permission, CancellationToken cancellationToken) =>
        owner == principal.UserId
        || (await authorization.AuthorizeAsync(principal, workspaceId, permission, cancellationToken).ConfigureAwait(false)).IsAllowed;

    private static SearchTermReportExecutor Executor(SecurityPrincipal principal) => new(
        principal.UserId,
        string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
        principal.Groups,
        principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString());

    private static JsonObject Parameters(Guid reportId) => new() { ["reportId"] = reportId.ToString() };
}
