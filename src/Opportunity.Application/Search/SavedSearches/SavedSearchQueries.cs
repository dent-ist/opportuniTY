using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;
using Opportunity.Core.Security;

namespace Opportunity.Application.Search.SavedSearches;

/// <summary>
/// <see cref="ISavedSearchQueries"/> over <see cref="ISavedSearchStore"/>. Nested criteria are loaded one batch per
/// saved search that has references (never per document), re-parsed with the current limits and inlined with the span of
/// the reference the caller wrote; a reference back to a search already on the path is a cycle, and more than
/// <see cref="SavedSearchQuerySyntax.MaxDepth"/> levels is rejected.
/// </summary>
public sealed class SavedSearchQueries(ISavedSearchStore store, IAuthorizationService authorization, QueryLimits limits) : ISavedSearchQueries
{
    public async Task<SavedSearchExpansion> ExpandAsync(SavedSearchExpansionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var direct = SavedSearchReferences.Collect(request.Ast);
        if (direct.Count == 0)
        {
            return SavedSearchExpansion.Unchanged(request.Ast);
        }

        var errors = new List<QueryDiagnostic>(direct.Where(r => r.Error is not null).Select(r => r.Error!));
        var ids = direct.Where(r => r.Id is not null).Select(r => r.Id!.Value).Distinct().ToList();
        var viewer = request.Caller is { } caller
            ? await ViewerAsync(authorization, caller, request.WorkspaceId, cancellationToken).ConfigureAwait(false)
            : null;
        var visible = ids.Count == 0
            ? new Dictionary<Guid, SavedSearchCriteria>()
            : await store.GetCriteriaAsync(request.WorkspaceId, ids, viewer, cancellationToken).ConfigureAwait(false);

        var expansion = new Expansion(store, limits, request.WorkspaceId, request.Self, errors);
        foreach (var (id, criteria) in visible)
        {
            expansion.Cache[id] = criteria;
        }

        var replacements = new Dictionary<QueryNode, QueryNode>(ReferenceEqualityComparer.Instance);
        var names = new Dictionary<SourceSpan, string>();
        foreach (var reference in direct.Where(r => r.Id is not null))
        {
            var id = reference.Id!.Value;
            var span = reference.Node.Span;
            if (!visible.TryGetValue(id, out var criteria))
            {
                errors.Add(new QueryDiagnostic(SavedSearchErrorCodes.NotFound,
                    $"No saved search with ID {id:D} is available to you. It may have been deleted or not shared with you.", span, []));
                continue;
            }

            if (id == request.Self)
            {
                errors.Add(new QueryDiagnostic(SavedSearchErrorCodes.Cycle, "A saved search cannot use itself as a criterion.", span, []));
                continue;
            }

            names[span] = criteria.Name;
            if (await expansion.InlineAsync(id, span, [], cancellationToken).ConfigureAwait(false) is { } inlined)
            {
                replacements[reference.Node] = inlined;
            }
        }

        if (errors.Count > 0)
        {
            return new SavedSearchExpansion(request.Ast, [.. errors.OrderBy(e => e.Span.Start).ThenBy(e => e.Span.End)], ids);
        }

        var expanded = SavedSearchReferences.Map(request.Ast, n => replacements.GetValueOrDefault(n));
        return new SavedSearchExpansion(expanded, [], ids) { ReferenceNames = names };
    }

    public async Task<SavedSearchRunSource?> FindForRunAsync(
        SecurityPrincipal caller, Guid workspaceId, Guid savedSearchId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var viewer = await ViewerAsync(authorization, caller, workspaceId, cancellationToken).ConfigureAwait(false);
        var search = await store.GetAsync(workspaceId, savedSearchId, viewer, cancellationToken).ConfigureAwait(false);
        return search is null
            ? null
            : new SavedSearchRunSource(search.SavedSearchId, search.Name, search.QueryText, search.Sort) { Expansion = search.Expansion };
    }

    public Task RecordRunAsync(Guid workspaceId, Guid savedSearchId, SavedSearchRunResult run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return store.RecordRunAsync(workspaceId, savedSearchId,
            new SavedSearchLastRun(run.At, run.HitCount, run.Exact, run.Current, run.ServedGeneration), cancellationToken);
    }

    /// <summary>
    /// The caller's view of saved searches from the PDP's current effective permissions: Workspace Admins
    /// (<c>Workspace.ManageUsers</c>) see every search of the workspace.
    /// </summary>
    public static async Task<SavedSearchViewer> ViewerAsync(
        IAuthorizationService authorization, SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(principal);
        var effective = await authorization.GetEffectivePermissionsAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return new SavedSearchViewer(principal.UserId, principal.Groups, IsAdministrator(effective));
    }

    /// <summary>Workspace Admins manage every saved search; the built-in role is the only one holding <c>Workspace.ManageUsers</c>.</summary>
    public static bool IsAdministrator(EffectivePermissions effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.Decision.IsAllowed && effective.Permissions.Contains(Permission.WorkspaceManageUsers);
    }

    private sealed class Expansion(ISavedSearchStore store, QueryLimits limits, Guid workspaceId, Guid? self, List<QueryDiagnostic> errors)
    {
        public Dictionary<Guid, SavedSearchCriteria?> Cache { get; } = [];

        /// <summary>The criteria of <paramref name="id"/> with its own references inlined, every span set to <paramref name="span"/>.</summary>
        public async Task<QueryNode?> InlineAsync(Guid id, SourceSpan span, List<Guid> path, CancellationToken cancellationToken)
        {
            var criteria = Cache[id]!;
            if (path.Count + 1 > SavedSearchQuerySyntax.MaxDepth)
            {
                errors.Add(new QueryDiagnostic(SavedSearchErrorCodes.TooDeep,
                    $"Saved searches are nested more than {SavedSearchQuerySyntax.MaxDepth} levels deep; flatten the criteria.", span, []));
                return null;
            }

            var parsed = QueryParser.Parse(criteria.QueryText, limits);
            if (parsed.Ast is not { } ast)
            {
                errors.Add(new QueryDiagnostic(SavedSearchErrorCodes.Invalid,
                    $"Saved search '{criteria.Name}' can no longer run: {(parsed.Errors.Count > 0 ? parsed.Errors[0].Message : "its query does not parse.")}", span, []));
                return null;
            }

            var nested = SavedSearchReferences.Collect(ast);
            if (nested.Count == 0)
            {
                return SavedSearchReferences.Map(ast, _ => null, span);
            }

            if (nested.Any(r => r.Error is not null))
            {
                errors.Add(new QueryDiagnostic(SavedSearchErrorCodes.InvalidReference,
                    $"Saved search '{criteria.Name}' has an invalid saved-search reference.", span, []));
                return null;
            }

            var missing = nested.Select(r => r.Id!.Value).Where(n => !Cache.ContainsKey(n)).Distinct().ToList();
            if (missing.Count > 0)
            {
                var loaded = await store.GetCriteriaAsync(workspaceId, missing, null, cancellationToken).ConfigureAwait(false);
                foreach (var m in missing)
                {
                    Cache[m] = loaded.GetValueOrDefault(m);
                }
            }

            var chain = path.Append(id).ToList();
            var replacements = new Dictionary<QueryNode, QueryNode>(ReferenceEqualityComparer.Instance);
            foreach (var reference in nested)
            {
                var target = reference.Id!.Value;
                if (target == self || chain.Contains(target))
                {
                    var name = Cache.GetValueOrDefault(target)?.Name ?? criteria.Name;
                    errors.Add(new QueryDiagnostic(SavedSearchErrorCodes.Cycle,
                        $"Saved search '{criteria.Name}' refers back to '{name}': nested saved searches cannot form a loop.", span, []));
                    return null;
                }

                if (Cache.GetValueOrDefault(target) is null)
                {
                    errors.Add(new QueryDiagnostic(SavedSearchErrorCodes.NotFound,
                        $"Saved search '{criteria.Name}' uses a saved search that no longer exists.", span, []));
                    return null;
                }

                if (await InlineAsync(target, span, chain, cancellationToken).ConfigureAwait(false) is not { } inlined)
                {
                    return null;
                }

                replacements[reference.Node] = inlined;
            }

            return SavedSearchReferences.Map(ast, n => replacements.GetValueOrDefault(n), span);
        }
    }
}

/// <summary>Finding and rewriting <c>savedsearch:&lt;id&gt;</c> references in an AST (pure).</summary>
public static class SavedSearchReferences
{
    public sealed record Reference(FieldNode Node, Guid? Id, QueryDiagnostic? Error);

    public static bool IsReference(FieldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.Equals(node.Name, SavedSearchQuerySyntax.FieldName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every reference of the AST (not those inside referenced searches), with an error for a misplaced or malformed one.</summary>
    public static IReadOnlyList<Reference> Collect(QueryNode ast)
    {
        ArgumentNullException.ThrowIfNull(ast);
        var found = new List<Reference>();
        Walk(ast, false, found);
        return found;
    }

    /// <summary>
    /// Rebuilds the tree, replacing every node for which <paramref name="replace"/> returns a node; with
    /// <paramref name="span"/>, every rebuilt node gets that span.
    /// </summary>
    public static QueryNode Map(QueryNode node, Func<QueryNode, QueryNode?> replace, SourceSpan? span = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(replace);
        if (replace(node) is { } replaced)
        {
            return replaced;
        }

        QueryNode mapped = node switch
        {
            AndNode and => and with { Children = [.. and.Children.Select(c => Map(c, replace, span))] },
            OrNode or => or with { Children = [.. or.Children.Select(c => Map(c, replace, span))] },
            NotNode not => not with { Child = Map(not.Child, replace, span) },
            FieldNode field => field with { Child = Map(field.Child, replace, span) },
            ProximityNode p => p with { Left = Map(p.Left, replace, span), Right = Map(p.Right, replace, span) },
            _ => node,
        };
        return span is { } s ? mapped with { Span = s } : mapped;
    }

    private static void Walk(QueryNode node, bool restricted, List<Reference> found)
    {
        switch (node)
        {
            case FieldNode field when IsReference(field):
                found.Add(restricted
                    ? new Reference(field, null, Invalid(field, "A saved-search reference cannot be used inside W/n or inside another field."))
                    : field.Child switch
                    {
                        TermNode t when Guid.TryParse(t.Text, out var id) => new Reference(field, id, null),
                        PhraseNode p when Guid.TryParse(p.Text, out var id) => new Reference(field, id, null),
                        _ => new Reference(field, null, Invalid(field, "Write savedsearch:<saved search ID>, for example savedsearch:0199a8a0-0000-7000-8000-000000000001.")),
                    });
                break;
            case FieldNode field:
                Walk(field.Child, true, found);
                break;
            case ProximityNode p:
                Walk(p.Left, true, found);
                Walk(p.Right, true, found);
                break;
            case AndNode and:
                foreach (var child in and.Children)
                {
                    Walk(child, restricted, found);
                }

                break;
            case OrNode or:
                foreach (var child in or.Children)
                {
                    Walk(child, restricted, found);
                }

                break;
            case NotNode not:
                Walk(not.Child, restricted, found);
                break;
        }
    }

    private static QueryDiagnostic Invalid(FieldNode field, string message) =>
        new(SavedSearchErrorCodes.InvalidReference, message, field.Span, []);
}
