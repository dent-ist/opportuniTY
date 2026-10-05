using System.Diagnostics;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Contracts.Api;
using Opportunity.Core.Highlighting;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Application.Search.HighlightSets;

/// <summary>A unit to highlight and where it comes from.</summary>
/// <param name="Color">The palette colour of a Highlight Set unit (term colour, else set colour); null for search hits.</param>
public sealed record TermHitUnitSource(HitUnit Unit, TermHitSource Source, Guid? HighlightSetId, Guid? TermId, string? Color);

public enum TermHitUnitStatus
{
    Ok,

    /// <summary>The search handle is unknown, expired (30 idle minutes) or not the caller's: 404, run the search again.</summary>
    SearchNotFound,

    /// <summary>A requested Highlight Set does not exist in the workspace: 404.</summary>
    HighlightSetNotFound,
}

public sealed record TermHitUnitSet(TermHitUnitStatus Status, IReadOnlyList<TermHitUnitSource> Units);

/// <summary>
/// Collects the units the viewer highlights (E16-T12): the hit units of the caller's current search (ADR-008 R17,
/// from the search handle: its query text is re-parsed, like every page) and the terms of the requested Highlight Sets,
/// in a stable order (search first, then sets in the order asked, terms in set order). An unknown or expired search
/// handle or Highlight Set is not found; a search handle of another user or session is answered exactly like an
/// unknown one and audited (<c>AuthZ.Denied</c>, <c>SearchHandleMismatch</c>).
/// </summary>
public sealed class TermHitUnitResolver(ISearchSessionStore searches, IHighlightSetStore sets, IAuditEventWriter audit, TimeProvider time)
{
    /// <summary>Highlight Sets per request.</summary>
    public const int MaxSets = 20;

    private const string HandleMismatchReason = "SearchHandleMismatch";
    private const string CorrelationTag = "opportunity.correlation_id";

    public async Task<TermHitUnitSet> ResolveAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        Guid? sessionId,
        Guid? searchId,
        IReadOnlyList<Guid> highlightSetIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(highlightSetIds);
        var units = new List<TermHitUnitSource>();
        if (searchId is { } id)
        {
            var search = await searches.GetAsync(workspaceId, id, cancellationToken).ConfigureAwait(false);
            if (search is null)
            {
                return new TermHitUnitSet(TermHitUnitStatus.SearchNotFound, []);
            }

            if (search.UserId != principal.UserId || search.SessionId != sessionId)
            {
                await AuditMismatchAsync(principal, workspaceId, id, cancellationToken).ConfigureAwait(false);
                return new TermHitUnitSet(TermHitUnitStatus.SearchNotFound, []);
            }

            if (QueryParser.Parse(search.QueryText).Ast is { } ast)
            {
                units.AddRange(HitUnits.FromQuery(ast).Select(u => new TermHitUnitSource(u, TermHitSource.Search, null, null, null)));
            }
        }

        var wanted = highlightSetIds.Distinct().ToList();
        if (wanted.Count > 0)
        {
            var found = (await sets.GetManyAsync(workspaceId, wanted, cancellationToken).ConfigureAwait(false))
                .ToDictionary(s => s.HighlightSetId);
            foreach (var setId in wanted)
            {
                if (!found.TryGetValue(setId, out var set))
                {
                    return new TermHitUnitSet(TermHitUnitStatus.HighlightSetNotFound, []);
                }

                foreach (var term in set.Terms)
                {
                    var (termUnits, _) = HitUnits.FromHighlightTerm(term.Expression);
                    units.AddRange(termUnits.Select(u =>
                        new TermHitUnitSource(u, TermHitSource.HighlightSet, set.HighlightSetId, term.TermId, term.Color ?? set.Color)));
                }
            }
        }

        return new TermHitUnitSet(TermHitUnitStatus.Ok, units);
    }

    private async Task AuditMismatchAsync(SecurityPrincipal principal, Guid workspaceId, Guid searchId, CancellationToken cancellationToken) =>
        await audit.WriteAsync(new AuditEvent
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.AuthZ.Category,
            Action = AuditTaxonomy.AuthZ.Denied,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = principal.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? principal.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            ResourceType = "Search",
            ResourceId = searchId.ToString("N"),
            Outcome = AuditOutcome.Denied,
            ReasonCode = HandleMismatchReason,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = new Dictionary<string, string?> { ["permission"] = "Search.Execute", ["use"] = "termHits" },
        }, cancellationToken).ConfigureAwait(false);
}
