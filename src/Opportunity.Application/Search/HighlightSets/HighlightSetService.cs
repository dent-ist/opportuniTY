using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.Highlighting;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Application.Search.HighlightSets;

public enum HighlightSetOutcomeStatus
{
    Ok,
    Created,
    NotFound,

    /// <summary>The <c>If-Match</c> version is stale: 412.</summary>
    VersionConflict,

    /// <summary>Another set has the name: 409.</summary>
    Conflict,

    InvalidRequest,
}

public sealed record HighlightSetOutcome(HighlightSetOutcomeStatus Status, HighlightSetRecord? Set = null)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>
/// Highlight Sets (E16-T12, familiarity guide §3.2 "persistent highlighting"): workspace term lists with a colour,
/// read by every member and changed with <c>HighlightSet.Manage</c> (declared by the endpoints). Each term must give at
/// least one highlight unit (<see cref="HitUnits.FromHighlightTerm"/>); problems are reported per term. Create, change
/// and delete are audited in the transaction of the change (<c>Search</c> / <c>HighlightSet.*</c>, the expressions in
/// the restricted details, Q-16). Reviewers' toggles are per user and workspace and not audited.
/// </summary>
public sealed class HighlightSetService(IHighlightSetStore store, TimeProvider time)
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2_000;
    public const int MaxTerms = 500;
    public const int MaxExpressionLength = 1_000;
    public const int MaxSelectionEntries = 1_000;

    /// <summary>
    /// The colours a set or term may use, in display order. The web app maps each name to design tokens (fill,
    /// underline and outline with at least 3:1 contrast in every theme); the API never stores raw colour values.
    /// </summary>
    public static IReadOnlyList<string> Colors { get; } = ["amber", "green", "blue", "violet", "rose", "teal"];

    private const string CorrelationTag = "opportunity.correlation_id";

    public Task<IReadOnlyList<HighlightSetRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListAsync(workspaceId, cancellationToken);

    public async Task<HighlightSetRecord?> GetAsync(Guid workspaceId, Guid highlightSetId, CancellationToken cancellationToken = default)
    {
        var found = await store.GetManyAsync(workspaceId, [highlightSetId], cancellationToken).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    public async Task<HighlightSetOutcome> CreateAsync(
        SecurityPrincipal actor, Guid workspaceId, HighlightSetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Validate(request, null, out var errors) is not { } definition)
        {
            return new HighlightSetOutcome(HighlightSetOutcomeStatus.InvalidRequest) { Errors = errors };
        }

        var id = Guid.CreateVersion7();
        var result = await store.CreateAsync(workspaceId, id, actor.UserId, definition,
            Event(actor, workspaceId, id, AuditTaxonomy.HighlightSet.Created, definition), cancellationToken).ConfigureAwait(false);
        return Map(result, HighlightSetOutcomeStatus.Created);
    }

    public async Task<HighlightSetOutcome> UpdateAsync(
        SecurityPrincipal actor, Guid workspaceId, HighlightSetRecord current, long expectedVersion, HighlightSetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(current);
        if (Validate(request, current, out var errors) is not { } definition)
        {
            return new HighlightSetOutcome(HighlightSetOutcomeStatus.InvalidRequest) { Errors = errors };
        }

        var result = await store.UpdateAsync(workspaceId, current.HighlightSetId, expectedVersion, actor.UserId, definition,
            Event(actor, workspaceId, current.HighlightSetId, AuditTaxonomy.HighlightSet.Modified, definition), cancellationToken).ConfigureAwait(false);
        return Map(result, HighlightSetOutcomeStatus.Ok);
    }

    public async Task<HighlightSetOutcome> DeleteAsync(
        SecurityPrincipal actor, Guid workspaceId, HighlightSetRecord current, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(current);
        var definition = new HighlightSetDefinition(current.Name, current.Description, current.Color, current.Terms);
        var result = await store.DeleteAsync(workspaceId, current.HighlightSetId, expectedVersion,
            Event(actor, workspaceId, current.HighlightSetId, AuditTaxonomy.HighlightSet.Deleted, definition), cancellationToken).ConfigureAwait(false);
        return Map(result, HighlightSetOutcomeStatus.Ok);
    }

    public Task<HighlightSetSelection> GetSelectionAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
        store.GetSelectionAsync(workspaceId, userId, cancellationToken);

    /// <summary>Stores the toggles; IDs that name no set of the workspace are dropped.</summary>
    public async Task<HighlightSetOutcome> SetSelectionAsync(
        Guid workspaceId, Guid userId, HighlightSetSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var disabled = (selection.DisabledSetIds ?? []).Distinct().ToList();
        if (disabled.Count > MaxSelectionEntries)
        {
            return new HighlightSetOutcome(HighlightSetOutcomeStatus.InvalidRequest)
            {
                Errors = new Dictionary<string, string[]> { ["disabledSetIds"] = [$"At most {MaxSelectionEntries} sets can be switched off."] },
            };
        }

        if (disabled.Count > 0)
        {
            var existing = (await store.GetManyAsync(workspaceId, disabled, cancellationToken).ConfigureAwait(false))
                .Select(s => s.HighlightSetId).ToHashSet();
            disabled = [.. disabled.Where(existing.Contains)];
        }

        await store.SetSelectionAsync(workspaceId, userId, new HighlightSetSelection(disabled, selection.SearchHits), cancellationToken)
            .ConfigureAwait(false);
        return new HighlightSetOutcome(HighlightSetOutcomeStatus.Ok);
    }

    /// <summary>The definition, or null with the problems keyed like the request (<c>terms[2].expression</c>).</summary>
    public static HighlightSetDefinition? Validate(HighlightSetRequest? request, HighlightSetRecord? current, out Dictionary<string, string[]> errors)
    {
        errors = [];
        if (request is null)
        {
            errors["body"] = ["Send a highlight set."];
            return null;
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name has 1 to {MaxNameLength} characters."];
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > MaxDescriptionLength })
        {
            errors["description"] = [$"A description has at most {MaxDescriptionLength} characters."];
        }

        var color = request.Color?.Trim().ToLowerInvariant();
        if (color is null || !Colors.Contains(color))
        {
            errors["color"] = [$"Choose one of: {string.Join(", ", Colors)}."];
        }

        var terms = request.Terms ?? [];
        if (terms.Count is 0 or > MaxTerms)
        {
            errors["terms"] = [$"A highlight set has 1 to {MaxTerms} terms."];
        }

        var known = current?.Terms.Select(t => t.TermId).ToHashSet() ?? [];
        var seenIds = new HashSet<Guid>();
        var seenExpressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HighlightTerm>();
        for (var i = 0; i < Math.Min(terms.Count, MaxTerms); i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"terms[{i}]");
            var term = terms[i];
            var expression = term?.Expression?.Trim() ?? string.Empty;
            if (expression.Length is 0 or > MaxExpressionLength)
            {
                errors[key + ".expression"] = [$"A term has 1 to {MaxExpressionLength} characters."];
                continue;
            }

            if (!seenExpressions.Add(expression))
            {
                errors[key + ".expression"] = ["This term is already in the set."];
                continue;
            }

            var (_, problems) = HitUnits.FromHighlightTerm(expression, QueryLimits.Default);
            if (problems.Count > 0)
            {
                errors[key + ".expression"] = [.. problems.Select(p => $"{p.Code}: {p.Message}")];
                continue;
            }

            var termColor = string.IsNullOrWhiteSpace(term!.Color) ? null : term.Color.Trim().ToLowerInvariant();
            if (termColor is not null && !Colors.Contains(termColor))
            {
                errors[key + ".color"] = [$"Choose one of: {string.Join(", ", Colors)}."];
                continue;
            }

            // A term keeps its ID across edits when the client sends it back; anything else gets a new one.
            var id = term.TermId is { } given && known.Contains(given) && seenIds.Add(given) ? given : Guid.CreateVersion7();
            seenIds.Add(id);
            result.Add(new HighlightTerm(id, expression, termColor));
        }

        return errors.Count == 0 ? new HighlightSetDefinition(name, description, color!, result) : null;
    }

    private static HighlightSetOutcome Map(HighlightSetWriteResult result, HighlightSetOutcomeStatus success) => result.Status switch
    {
        HighlightSetWriteStatus.Ok => new HighlightSetOutcome(success, result.Set),
        HighlightSetWriteStatus.VersionConflict => new HighlightSetOutcome(HighlightSetOutcomeStatus.VersionConflict),
        HighlightSetWriteStatus.NameTaken => new HighlightSetOutcome(HighlightSetOutcomeStatus.Conflict),
        _ => new HighlightSetOutcome(HighlightSetOutcomeStatus.NotFound),
    };

    private AuditEvent Event(SecurityPrincipal actor, Guid workspaceId, Guid highlightSetId, string action, HighlightSetDefinition definition)
    {
        var expressions = string.Join('\n', definition.Terms.Select(t => t.Expression));
        return new AuditEvent
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.HighlightSet.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : actor.DisplayName,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = AuditTaxonomy.HighlightSet.ResourceType,
            ResourceId = highlightSetId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = new Dictionary<string, string?>
            {
                ["name"] = definition.Name,
                ["color"] = definition.Color,
                ["termCount"] = definition.Terms.Count.ToString(CultureInfo.InvariantCulture),
            },
            RestrictedDetails = expressions.Length <= AuditEventRules.MaxRestrictedDetailsBytes / 2
                ? new Dictionary<string, string?> { ["terms"] = expressions }
                : null,
        };
    }
}
