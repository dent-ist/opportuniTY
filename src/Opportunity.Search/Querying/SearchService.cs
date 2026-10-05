using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>
/// The logical search service (E07-T05). One path for every page:
/// <list type="number">
/// <item>parse and translate the query text (the user clause can only ever land in <c>bool.must</c>);</item>
/// <item>read the caller's visibility from the PDP (requires <c>Search.Execute</c>; restriction classes and walls go
/// into the outer filter as defence in depth, ADR-006 R10);</item>
/// <item>resolve the placement through <see cref="IIndexManager"/> and search a point-in-time reader of the read alias
/// (routing on shared indexes), with the workspace term first in the outer filter;</item>
/// <item>post-filter the page against PostgreSQL with <c>AuthorizeMany(Document.View)</c> in summary-audit mode
/// (Q-12, Q-59): denied hits vanish with their snippets and grid fields;</item>
/// <item>keep the reader and positions server-side behind opaque IDs bound to (user, session, workspace).</item>
/// </list>
/// </summary>
internal sealed partial class SearchService(
    IIndexManager indexes,
    OpenSearchConnection connection,
    IAuthorizationService authorization,
    IAuditEventWriter audit,
    ISearchSessionStore sessions,
    ISearchFreshnessReader freshnessReader,
    ISearchQueryTranslator translator,
    QueryLimits limits,
    OpenSearchOptions options,
    TimeProvider time,
    ILogger<SearchService> logger,
    OpportunityMetrics? metrics = null,
    ISavedSearchQueries? savedSearches = null) : ISearchService
{
    private const string ResourceType = "Search";
    private const string CorrelationTag = "opportunity.correlation_id";
    private const string SearchClassAttribute = "opportunity.search.class";
    private const string DropReasonAttribute = "opportunity.search.drop_reason";

    /// <summary>ReasonCode of a search handle replayed by another user or session (audited, answered 404).</summary>
    internal const string HandleMismatchReason = "SearchHandleMismatch";

    /// <summary>
    /// ReasonCode of a cursor issued for another search (another user's, session's or search's) replayed on the caller's
    /// own handle (audited, answered 404). Expired and unknown cursors get the same 404 without an audit event (Q-71).
    /// </summary>
    internal const string CursorMismatchReason = "SearchCursorMismatch";

    /// <summary>Audit detail values of why a page ran on a re-established reader.</summary>
    internal const string ReaderExpired = "expired";
    internal const string ReaderAgedOut = "maxAge";
    internal const string ReaderDetached = "detached";

    /// <summary>
    /// The §22 rule for interactive cursors (ADR-002 §8, Q-33): a live point-in-time reader, never a materialized set;
    /// expiry is handled by re-establishing the reader and saying "results refreshed".
    /// </summary>
    private static readonly SelectionStrategy InteractiveStrategy =
        SnapshotStrategyRules.Decide(SetOperationKind.InteractiveCursor, null, new PitPolicy()).Strategy;

    private SearchServiceOptions Settings => options.Search;

    public async Task<SearchOutcome> SearchAsync(SearchCaller caller, SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();

        var pageSize = request.PageSize ?? Settings.DefaultPageSize;
        if (pageSize < 1 || pageSize > Settings.MaxPageSize)
        {
            return SearchOutcome.InvalidRequest("pageSize", $"pageSize must be between 1 and {Settings.MaxPageSize}.");
        }

        SavedSearchRunSource? saved = null;
        if (request.SavedSearchId is { } savedSearchId)
        {
            if (request.Query is not null)
            {
                return SearchOutcome.InvalidRequest("query", "Give either query or savedSearchId, not both.");
            }

            // A saved search the caller cannot see is indistinguishable from one that does not exist (Q-65).
            if (savedSearches is null
                || await savedSearches.FindForRunAsync(caller.Principal, caller.WorkspaceId, savedSearchId, cancellationToken).ConfigureAwait(false)
                    is not { } source)
            {
                return SearchOutcome.NotFound;
            }

            saved = source;
        }
        else if (request.Query is null)
        {
            return SearchOutcome.InvalidRequest("query", "The query text is required (it may be empty).");
        }

        var queryText = saved?.QueryText ?? request.Query!;
        var sort = new List<SortKey>();
        foreach (var key in request.Sort ?? saved?.Sort ?? [])
        {
            if (key is null || SortKey.Resolve(key.Field, key.Direction) is not { } resolved)
            {
                return SearchOutcome.InvalidRequest("sort", $"Sortable fields: {string.Join(", ", SearchSortFields.All)}.");
            }

            sort.Add(resolved);
        }

        if (sort.Count > 5 || sort.Select(s => s.Field).Distinct(StringComparer.Ordinal).Count() != sort.Count)
        {
            return SearchOutcome.InvalidRequest("sort", "At most 5 distinct sort fields.");
        }

        var facets = new List<string>();
        foreach (var facet in request.Facets ?? [])
        {
            if (SearchFacetFields.All.FirstOrDefault(f => string.Equals(f, facet, StringComparison.OrdinalIgnoreCase)) is not { } name)
            {
                return SearchOutcome.InvalidRequest("facets", $"Facet fields: {string.Join(", ", SearchFacetFields.All)}.");
            }

            facets.Add(name);
        }

        var visibility = await VisibilityAsync(caller, cancellationToken).ConfigureAwait(false);
        if (visibility.Outcome is not null)
        {
            return visibility.Outcome;
        }

        var placement = await PlacementAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        // Text the caller typed may only reference saved searches they can see; a saved search's own references are its criteria.
        var plan = await PlanAsync(caller, queryText, placement?.Generation, saved is null, cancellationToken).ConfigureAwait(false);
        if (plan.Errors is { } errors)
        {
            return SearchOutcome.InvalidQuery(errors);
        }

        if (sort.Count == 0)
        {
            sort.Add(SortKey.DefaultFor(plan.Ast));
        }

        plan = plan with { QueryClass = GateClass(plan.QueryClass, sort, facets.Count) };

        // Q-10: the refresh-aware watermark read before the reader opens is a lower bound of what the reader reflects.
        var watermark = await freshnessReader.ReadAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var search = new SearchSessionRecord(
            caller.WorkspaceId,
            Guid.NewGuid(),
            caller.Principal.UserId,
            caller.SessionId,
            queryText,
            SortKey.ToJson(sort),
            pageSize,
            request.CountExact == true,
            request.Highlight ?? true,
            PointInTimeId: null,
            TotalValue: 0,
            TotalExact: true,
            now,
            now + Settings.SearchIdleTimeout)
        {
            ServedGeneration = watermark.IndexedThroughGeneration,
        };
        var freshness = SearchFreshnessMapping.ForPage(watermark.IndexedThroughGeneration, watermark, now);

        if (placement is null)
        {
            // Nothing was ever indexed for this workspace: an empty first page, no handle to page through.
            await AuditExecutedAsync(caller, visibility.Filter!, search, plan, null, new TotalCount(0, TotalRelation.Eq), 0, 0, cancellationToken,
                saved?.SavedSearchId).ConfigureAwait(false);
            await RecordSavedRunAsync(caller, saved, new TotalCount(0, TotalRelation.Eq), freshness, cancellationToken).ConfigureAwait(false);
            RecordDuration(started, plan.QueryClass, "ok");
            return SearchOutcome.Ok(EmptyPage(plan.Normalized, pageSize, freshness) with { SavedSearchId = saved?.SavedSearchId });
        }

        if (InteractiveStrategy != SelectionStrategy.PointInTime)
        {
            throw new InvalidOperationException("Interactive searches page under a point-in-time reader (ADR-002 §8).");
        }

        var pit = await OpenPointInTimeAsync(placement, cancellationToken).ConfigureAwait(false);
        search = search with { PointInTimeOpenedAt = time.GetUtcNow() };
        var query = SearchDsl.Query(placement.WorkspaceFilterValue, visibility.Filter!, plan.Query!);
        SearchResult result;
        try
        {
            result = await ExecuteAsync(placement, search with { PointInTimeId = pit }, query, sort, Navigation.First(), facets, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (QueryRejectedException rejected)
        {
            RecordDuration(started, plan.QueryClass, "rejected");
            return SearchOutcome.InvalidQuery(plan.Rejection(rejected.Code));
        }

        var served = await ServeAsync(
            caller, search with { PointInTimeId = result.PointInTimeId }, placement, visibility.Filter!, result, Navigation.First(), plan.Normalized,
            freshness, cancellationToken).ConfigureAwait(false);
        await sessions.CreateAsync(
            search with { PointInTimeId = result.PointInTimeId, TotalValue = result.Total.Value, TotalExact = result.Total.Relation == TotalRelation.Eq },
            served.Cursors,
            cancellationToken).ConfigureAwait(false);
        await CapReadersAsync(caller, cancellationToken).ConfigureAwait(false);
        await AuditExecutedAsync(caller, visibility.Filter!, search, plan, search.SearchId, result.Total, served.Page.Items.Count,
            served.Dropped, cancellationToken, saved?.SavedSearchId).ConfigureAwait(false);
        await RecordSavedRunAsync(caller, saved, served.Page.Total, freshness, cancellationToken).ConfigureAwait(false);

        RecordDuration(started, plan.QueryClass, "ok");
        return SearchOutcome.Ok(served.Page with
        {
            Facets = Facets(result.Aggregations, facets),
            SavedSearchId = saved?.SavedSearchId,
        });
    }

    public async Task<SearchOutcome> GetPageAsync(
        SearchCaller caller, string searchId, SearchPageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(page);
        var started = Stopwatch.GetTimestamp();
        var selectors = (page.Cursor is not null ? 1 : 0) + (page.Number is not null ? 1 : 0) + (page.Last ? 1 : 0);
        if (selectors != 1)
        {
            return SearchOutcome.InvalidRequest("page", "Give exactly one of cursor, page number or last.");
        }

        if (!Guid.TryParseExact(searchId, "N", out var id)
            || await sessions.GetAsync(caller.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } search)
        {
            return SearchOutcome.NotFound;
        }

        if (search.UserId != caller.Principal.UserId || search.SessionId != caller.SessionId)
        {
            // Another user's or another session's handle: indistinguishable from a missing one, but audited.
            await AuditHandleMismatchAsync(caller, id, cancellationToken).ConfigureAwait(false);
            return SearchOutcome.NotFound;
        }

        Navigation navigation;
        if (page.Cursor is { } raw)
        {
            if (!Guid.TryParseExact(raw, "N", out var cursorId)
                || await sessions.GetCursorAsync(caller.WorkspaceId, id, cursorId, cancellationToken).ConfigureAwait(false) is not { } cursor)
            {
                // Not one of this handle's live cursors. Always the same 404 (no existence oracle); audited only when the
                // cursor was issued for another search (another user's, another session's or another of the caller's own),
                // not when it is expired, malformed or unknown (Q-71).
                if (cursorId != Guid.Empty
                    && await sessions.GetCursorBindingAsync(caller.WorkspaceId, cursorId, cancellationToken).ConfigureAwait(false) is { } binding
                    && binding.SearchId != id)
                {
                    await AuditCursorMismatchAsync(caller, id, binding, cancellationToken).ConfigureAwait(false);
                }

                return SearchOutcome.NotFound;
            }

            navigation = cursor.Direction == SearchCursorDirection.After
                ? Navigation.After(JsonNode.Parse(cursor.SortValuesJson)!.AsArray(), cursor.PageNumber)
                : Navigation.Before(JsonNode.Parse(cursor.SortValuesJson)!.AsArray(), cursor.PageNumber);
        }
        else if (page.Number is { } number)
        {
            var from = ((long)number - 1) * search.PageSize;
            if (number < 1 || from + search.PageSize + 1 > options.MaxResultWindow)
            {
                return SearchOutcome.InvalidRequest("page",
                    $"Page numbers reach the first {options.MaxResultWindow:N0} results; use Next, Last, a filter or Go to control number (Q-49).");
            }

            navigation = Navigation.Jump(number, (int)from);
        }
        else
        {
            navigation = Navigation.LastPage();
        }

        var visibility = await VisibilityAsync(caller, cancellationToken).ConfigureAwait(false);
        if (visibility.Outcome is not null)
        {
            return visibility.Outcome;
        }

        var placement = await PlacementAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(caller, search.QueryText, placement?.Generation, false, cancellationToken).ConfigureAwait(false);
        if (placement is null || plan.Errors is not null)
        {
            // The workspace lost its placement, or the stored query no longer binds (e.g. a field was deleted).
            return SearchOutcome.NotFound;
        }

        var sort = SortKey.FromJson(search.SortJson);
        plan = plan with { QueryClass = GateClass(plan.QueryClass, sort, 0) };
        var agedOut = search.PointInTimeId is not null && (search.PointInTimeOpenedAt ?? search.CreatedAt) + Settings.PointInTimeMaxAge <= time.GetUtcNow();
        if (agedOut)
        {
            // ADR-002 §8: the reader reached its maximum age; close it and re-establish one from the cursor position.
            LogPointInTimeAgedOut(logger, search.WorkspaceId, search.SearchId);
            await ClosePointInTimeAsync(search.PointInTimeId!).ConfigureAwait(false);
            search = search with { PointInTimeId = null, PointInTimeOpenedAt = null };
        }

        var watermark = await freshnessReader.ReadAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var query = SearchDsl.Query(placement.WorkspaceFilterValue, visibility.Filter!, plan.Query!);
        SearchResult result;
        try
        {
            result = await ExecuteAsync(placement, search, query, sort, navigation, [], cancellationToken).ConfigureAwait(false);
        }
        catch (QueryRejectedException rejected)
        {
            RecordDuration(started, plan.QueryClass, "rejected");
            return SearchOutcome.InvalidQuery(plan.Rejection(rejected.Code));
        }

        if (agedOut && result.RefreshReason == ReaderDetached)
        {
            result = result with { RefreshReason = ReaderAgedOut };
        }

        var current = search with { PointInTimeId = result.PointInTimeId };

        // The reader is as current as when it opened: unchanged while nothing was committed since its watermark. A
        // reader reopened just now (Q-33) is as current as the watermark read before it.
        var freshness = SearchFreshnessMapping.ForPage(
            result.Refreshed ? watermark.IndexedThroughGeneration : search.ServedGeneration, watermark, time.GetUtcNow());
        var served = await ServeAsync(caller, current, placement, visibility.Filter!, result, navigation, plan.Normalized, freshness, cancellationToken)
            .ConfigureAwait(false);

        await sessions.TouchAsync(
            caller.WorkspaceId,
            id,
            result.Refreshed ? new SearchReaderUpdate(result.PointInTimeId, result.OpenedAt ?? time.GetUtcNow(), watermark.IndexedThroughGeneration)
                : result.PointInTimeId == search.PointInTimeId ? null
                : new SearchReaderUpdate(result.PointInTimeId, search.PointInTimeOpenedAt ?? search.CreatedAt, search.ServedGeneration),
            time.GetUtcNow() + Settings.SearchIdleTimeout,
            served.Cursors,
            cancellationToken).ConfigureAwait(false);
        await AuditPageServedAsync(caller, visibility.Filter!, id, navigation, served, result, cancellationToken).ConfigureAwait(false);

        RecordDuration(started, plan.QueryClass, "ok");
        return SearchOutcome.Ok(served.Page);
    }

    /// <summary>
    /// ADR-002 §8: at most <see cref="SearchServiceOptions.MaxOpenPointInTimesPerUser"/> open readers per user and
    /// workspace; the oldest beyond that are detached from their searches and closed.
    /// </summary>
    private async Task CapReadersAsync(SearchCaller caller, CancellationToken cancellationToken)
    {
        var detached = await sessions.DetachReadersAsync(caller.WorkspaceId, caller.Principal.UserId, Settings.MaxOpenPointInTimesPerUser, cancellationToken)
            .ConfigureAwait(false);
        if (detached.Count == 0)
        {
            return;
        }

        LogReadersCapped(logger, detached.Count, caller.Principal.UserId, caller.WorkspaceId);
        foreach (var pit in detached)
        {
            await ClosePointInTimeAsync(pit).ConfigureAwait(false);
        }
    }

    private async Task<(SearchOutcome? Outcome, VisibilityFilter? Filter)> VisibilityAsync(SearchCaller caller, CancellationToken cancellationToken)
    {
        var visibility = await authorization.GetVisibilityAsync(caller.Principal, caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (visibility.Decision.IsAllowed && visibility.Filter is not null)
        {
            return (null, visibility.Filter);
        }

        return (visibility.Decision.Outcome == AuthorizationOutcome.NotFound ? SearchOutcome.NotFound : SearchOutcome.Forbidden, null);
    }

    private async Task<Placement?> PlacementAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            return await indexes.ResolveAsync(workspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceNotPlacedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses <paramref name="text"/>, expands saved-search references (E07-T09; direct ones must be visible to the caller
    /// when <paramref name="checkReferences"/>) and translates the result. The plan keeps the AST as written.
    /// </summary>
    private async Task<QueryPlan> PlanAsync(SearchCaller caller, string text, int? generation, bool checkReferences, CancellationToken cancellationToken)
    {
        var workspaceId = caller.WorkspaceId;
        var parsed = QueryParser.Parse(text, limits);
        if (parsed.Ast is not { } ast)
        {
            return QueryPlan.Failed(parsed.Errors);
        }

        var expansion = SavedSearchExpansion.Unchanged(ast);
        if (savedSearches is not null)
        {
            expansion = await savedSearches.ExpandAsync(
                new SavedSearchExpansionRequest(workspaceId, ast, checkReferences ? caller.Principal : null), cancellationToken).ConfigureAwait(false);
            if (!expansion.Success)
            {
                return QueryPlan.Failed(expansion.Errors);
            }
        }

        var translation = await translator.TranslateAsync(
            expansion.Ast, new SearchTranslationContext(workspaceId, generation ?? ProjectionMappings.Embedded.CurrentGeneration, limits), cancellationToken)
            .ConfigureAwait(false);
        if (!translation.Success)
        {
            return QueryPlan.Failed(translation.Errors.Count > 0
                ? expansion.Annotate(translation.Errors)
                : [new QueryDiagnostic(SearchQueryErrorCodes.UnsupportedForField, "The query cannot be planned.", ast.Span)]);
        }

        return new QueryPlan(ast, translation.Query, QueryPrinter.Print(ast), translation.QueryClass, null)
        {
            BoundedExpansions = translation.BoundedExpansions,
        };
    }

    /// <summary>
    /// The §29 gate class (docs/benchmarks/query-taxonomy.md): a grid sort other than relevance or facets make any
    /// query complex, as the benchmark's classifier counts them.
    /// </summary>
    private static string GateClass(string queryClass, IReadOnlyList<SortKey> sort, int facets) =>
        facets > 0 || sort.Any(k => !k.IsScore) ? SearchTranslation.Complex : queryClass;

    private async Task<string> OpenPointInTimeAsync(Placement placement, CancellationToken cancellationToken)
    {
        var routing = placement.Read.Routing is { } r ? "&routing=" + Escape(r) : string.Empty;
        var response = await connection.SendAsync(
            HttpMethod.Post,
            $"{Escape(placement.Read.Index)}/_search/point_in_time?keep_alive={SearchDsl.Seconds(Settings.PointInTimeKeepAlive)}{routing}",
            null,
            cancellationToken).ConfigureAwait(false);
        return response.Body?["pit_id"]?.GetValue<string>()
            ?? throw new OpenSearchRequestException("OpenSearch did not return a point-in-time id.");
    }

    private async Task<SearchResult> ExecuteAsync(
        Placement placement,
        SearchSessionRecord search,
        JsonObject query,
        IReadOnlyList<SortKey> sort,
        Navigation navigation,
        IReadOnlyList<string> facets,
        CancellationToken cancellationToken)
    {
        // A page of a search whose reader aged out or was detached (per-user cap) re-establishes one (ADR-002 §8).
        var refreshed = search.PointInTimeId is null;
        var reason = refreshed ? ReaderDetached : null;
        DateTimeOffset? openedAt = refreshed ? time.GetUtcNow() : null;
        var pit = search.PointInTimeId ?? await OpenPointInTimeAsync(placement, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var body = SearchDsl.Body(new SearchBodySpec
            {
                Query = query,
                PointInTimeId = pit,
                KeepAlive = Settings.PointInTimeKeepAlive,
                Sort = sort,
                Reverse = navigation.Reverse,
                SearchAfter = navigation.SearchAfter,
                From = navigation.From,
                Size = search.PageSize + 1,
                TrackTotalHitsUpTo = search.CountExact ? null : Settings.TrackTotalHitsUpTo,
                Highlight = search.Highlight,
                SnippetFragmentSize = Settings.SnippetFragmentSize,
                SnippetsPerHit = Settings.SnippetsPerHit,
                Facets = facets,
                FacetBuckets = Settings.FacetBuckets,
                Timeout = Settings.QueryTimeout,
            });

            var response = await connection.SendAsync(
                HttpMethod.Post, "_search", body, cancellationToken, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError)
                .ConfigureAwait(false);
            if (IsClauseLimit(response))
            {
                // ADR-008 R7: a wildcard expansion inside W/n exceeded the clause limit; never truncated, reported.
                throw new QueryRejectedException(SearchQueryErrorCodes.WildcardTooBroad);
            }
            if (IsPointInTimeGone(response) && !refreshed)
            {
                // Q-33: the live reader expired; reopen it and say so ("results refreshed").
                LogPointInTimeReopened(logger, search.WorkspaceId, search.SearchId);
                openedAt = time.GetUtcNow();
                pit = await OpenPointInTimeAsync(placement, cancellationToken).ConfigureAwait(false);
                refreshed = true;
                reason = ReaderExpired;
                continue;
            }

            if (response.Status != HttpStatusCode.OK || response.Body is not JsonObject json)
            {
                throw new OpenSearchRequestException(
                    $"OpenSearch search failed with {(int)response.Status} {OpenSearchConnection.ErrorType(response.Body)}.");
            }

            if (json["timed_out"]?.GetValue<bool>() == true)
            {
                // ADR-008 §5: partial results are never presented as complete.
                throw new QueryRejectedException(SearchQueryErrorCodes.QueryTimeout);
            }

            var hits = json["hits"]?["hits"]?.AsArray() ?? [];
            var total = json["hits"]?["total"];
            var totalValue = total?["value"]?.GetValue<long>() ?? 0;
            var relation = total?["relation"]?.GetValue<string>() == "eq" && json["timed_out"]?.GetValue<bool>() != true
                ? TotalRelation.Eq
                : TotalRelation.Gte;
            return new SearchResult(
                [.. hits.OfType<JsonObject>()],
                new TotalCount(totalValue, relation),
                json["pit_id"]?.GetValue<string>() ?? pit,
                refreshed,
                json["aggregations"] as JsonObject)
            {
                OpenedAt = openedAt,
                RefreshReason = refreshed ? reason : null,
            };
        }
    }

    /// <summary>OpenSearch refused to expand a multi-term query past <c>indices.query.bool.max_clause_count</c>.</summary>
    private static bool IsClauseLimit(OpenSearchResponse response)
    {
        if (response.Status == HttpStatusCode.OK)
        {
            return false;
        }

        var text = response.Body?.ToJsonString() ?? string.Empty;
        return text.Contains("maxClauseCount", StringComparison.Ordinal)
            || text.Contains("too_many_clauses", StringComparison.Ordinal)
            || text.Contains("too_many_nested_clauses", StringComparison.Ordinal);
    }

    private static bool IsPointInTimeGone(OpenSearchResponse response)
    {
        if (response.Status == HttpStatusCode.OK)
        {
            return false;
        }

        var text = response.Body?.ToJsonString() ?? string.Empty;
        return text.Contains("search_context_missing_exception", StringComparison.Ordinal)
            || text.Contains("No search context found", StringComparison.Ordinal)
            || (response.Status == HttpStatusCode.NotFound && text.Contains("point_in_time", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Post-filters the page (Q-12) and derives page info and server-side cursors from the unfiltered hits.</summary>
    private async Task<ServedPage> ServeAsync(
        SearchCaller caller,
        SearchSessionRecord search,
        Placement placement,
        VisibilityFilter visibility,
        SearchResult result,
        Navigation navigation,
        string normalized,
        SearchFreshness freshness,
        CancellationToken cancellationToken)
    {
        var size = search.PageSize;
        long? pageCount = result.Total.Relation == TotalRelation.Eq ? Math.Max(1, (result.Total.Value + size - 1) / size) : null;

        // Q-49: with an exact total the last page keeps the page boundaries (e.g. 3 of 23 at size 5); otherwise it is
        // simply the final page-size slice.
        var take = navigation.IsLast && pageCount is { } pages ? (int)(result.Total.Value - ((pages - 1) * size)) : size;
        take = Math.Clamp(take, 1, size);
        var more = result.Hits.Count > take;
        var window = result.Hits.Take(take).ToList();
        if (navigation.Reverse)
        {
            window.Reverse();
        }

        // Without a lookahead hit the walk direction is exhausted; the other direction follows from how we got here.
        bool hasNext, hasPrevious;
        if (navigation.Reverse)
        {
            hasPrevious = more;
            hasNext = !navigation.IsLast;
        }
        else
        {
            hasNext = more;
            hasPrevious = navigation.SearchAfter is not null || navigation.From > 0;
        }

        var number = navigation.IsLast ? (int?)pageCount : navigation.PageNumber;

        var expires = time.GetUtcNow() + Settings.SearchIdleTimeout;
        var cursors = new List<SearchCursorRecord>();
        string? next = null, previous = null;
        if (window.Count > 0 && hasNext)
        {
            var cursor = new SearchCursorRecord(search.WorkspaceId, search.SearchId, Guid.NewGuid(), SearchCursorDirection.After,
                (window[^1]["sort"] ?? new JsonArray()).ToJsonString(), number + 1, expires);
            cursors.Add(cursor);
            next = cursor.CursorId.ToString("N");
        }

        if (window.Count > 0 && hasPrevious)
        {
            var cursor = new SearchCursorRecord(search.WorkspaceId, search.SearchId, Guid.NewGuid(), SearchCursorDirection.Before,
                (window[0]["sort"] ?? new JsonArray()).ToJsonString(), number is > 1 ? number - 1 : null, expires);
            cursors.Add(cursor);
            previous = cursor.CursorId.ToString("N");
        }

        var (items, dropped) = await PostFilterAsync(caller, placement, window, cancellationToken).ConfigureAwait(false);
        items = await MarkFamilyParentsAsync(placement, visibility, result.PointInTimeId, items, cancellationToken).ConfigureAwait(false);
        var page = new SearchResultPage
        {
            SearchId = search.SearchId.ToString("N"),
            Normalized = normalized,
            Items = items,
            Page = new SearchPageInfo(number, size, pageCount, IsFirst: !hasPrevious, IsLast: !hasNext),
            Total = result.Total,
            Freshness = freshness,
            NextCursor = next,
            PreviousCursor = previous,
            ResultsRefreshed = result.Refreshed,
        };
        return new ServedPage(page, cursors, dropped);
    }

    /// <summary>
    /// Q-12: re-checks every hit of the page against PostgreSQL. A hit is returned only if the PDP allows
    /// <c>Document.View</c> on it now; anything else (restricted, walled, deleted, or a projection row that does not
    /// belong to this workspace) is dropped with its snippets and grid fields. One summary audit per call (Q-59).
    /// </summary>
    private async Task<(IReadOnlyList<SearchHit> Items, int Dropped)> PostFilterAsync(
        SearchCaller caller, Placement placement, List<JsonObject> hits, CancellationToken cancellationToken)
    {
        var candidates = new List<(JsonObject Hit, Guid DocumentId)>(hits.Count);
        var integrity = 0;
        foreach (var hit in hits)
        {
            var source = hit["_source"] as JsonObject;
            if (source?[ProjectionFields.WorkspaceId]?.GetValue<string>() != placement.WorkspaceFilterValue
                || !Guid.TryParse(source[ProjectionFields.DocumentId]?.GetValue<string>(), out var documentId))
            {
                integrity++;
                continue;
            }

            candidates.Add((hit, documentId));
        }

        if (integrity > 0)
        {
            LogIntegrityDrop(logger, caller.WorkspaceId, integrity);
            Dropped(integrity, "integrity");
        }

        if (candidates.Count == 0)
        {
            return ([], integrity);
        }

        var decisions = await authorization.AuthorizeManyAsync(
            caller.Principal, caller.WorkspaceId, Permission.DocumentView, [.. candidates.Select(c => c.DocumentId)], DenialAudit.Summary,
            cancellationToken).ConfigureAwait(false);
        var items = new List<SearchHit>(candidates.Count);
        foreach (var (hit, documentId) in candidates)
        {
            if (decisions.TryGetValue(documentId, out var decision) && decision.IsAllowed)
            {
                items.Add(ToHit(hit, documentId));
            }
            else
            {
                Dropped(1, decisions.TryGetValue(documentId, out var denied) ? denied.Reason : AuthorizationReasons.DocumentNotFound);
            }
        }

        return (items, integrity + candidates.Count - items.Count);
    }

    /// <summary>
    /// Marks the page's family parents: top-level documents (no parent, sequence 0) of a family that has other members
    /// in the index the caller's visibility filter lets through. One aggregation on the page's reader, so the flag is
    /// as of the same point in time as the page. A standalone document is not a parent.
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> MarkFamilyParentsAsync(
        Placement placement, VisibilityFilter visibility, string pointInTimeId, IReadOnlyList<SearchHit> items, CancellationToken cancellationToken)
    {
        var tops = items
            .Where(h => h.ParentDocumentId is null && (h.FamilySequence ?? 0) == 0 && h.FamilyId is not null)
            .Select(h => h.FamilyId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tops.Count == 0)
        {
            return items;
        }

        var familyTerms = Obj(("terms", Obj((ProjectionFields.FamilyId, new JsonArray([.. tops.Select(t => (JsonNode)t)])))));
        var nonRoot = Obj(("range", Obj((ProjectionFields.FamilySequence, Obj(("gte", 1))))));
        var members = Obj(("bool", Obj(("filter", new JsonArray(familyTerms, nonRoot)))));
        var body = Obj(
            ("size", 0),
            ("track_total_hits", false),
            ("query", SearchDsl.Query(placement.WorkspaceFilterValue, visibility, members)),
            ("aggs", Obj((FamilyParentsAggregation, Obj(("terms", Obj(("field", ProjectionFields.FamilyId), ("size", tops.Count))))))),
            ("timeout", string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (long)Settings.QueryTimeout.TotalMilliseconds)}ms")),
            ("pit", Obj(("id", pointInTimeId), ("keep_alive", SearchDsl.Seconds(Settings.PointInTimeKeepAlive)))));
        var response = await connection.SendAsync(HttpMethod.Post, "_search", body, cancellationToken).ConfigureAwait(false);
        var parents = (response.Body?["aggregations"]?[FamilyParentsAggregation]?["buckets"] as JsonArray ?? [])
            .Select(b => b?["key"]?.GetValue<string>())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return parents.Count == 0
            ? items
            : [.. items.Select(h => h.FamilyId is { } f && parents.Contains(f) && h.ParentDocumentId is null && (h.FamilySequence ?? 0) == 0
                ? h with { IsFamilyParent = true }
                : h)];
    }

    private const string FamilyParentsAggregation = "family_parents";

    private static SearchHit ToHit(JsonObject hit, Guid documentId)
    {
        var s = (JsonObject)hit["_source"]!;
        var snippets = (hit["highlight"]?[ProjectionFields.Text] as JsonArray ?? [])
            .Select(f => f?.GetValue<string>())
            .OfType<string>()
            .Select(Snippet)
            .ToList();
        return new SearchHit(
            documentId,
            String(s, ProjectionFields.ControlNumber) ?? string.Empty,
            String(s, ProjectionFields.FileName),
            String(s, ProjectionFields.FileType),
            String(s, ProjectionFields.FileExtension),
            String(s, ProjectionFields.MimeType),
            DateTimeOffset.TryParse(String(s, ProjectionFields.DocumentDate), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date
                : null,
            String(s, ProjectionFields.FamilyId),
            String(s, ProjectionFields.ParentDocumentId),
            (int?)Number(s, ProjectionFields.FamilySequence),
            Number(s, ProjectionFields.FileSize),
            (int?)Number(s, ProjectionFields.PageCount),
            snippets);
    }

    /// <summary>Strips the private-use highlight delimiters and returns the highlighted ranges as offsets.</summary>
    internal static SearchSnippet Snippet(string fragment)
    {
        var text = new System.Text.StringBuilder(fragment.Length);
        var spans = new List<TextSpan>();
        int? start = null;
        foreach (var c in fragment)
        {
            if (c == SearchDsl.HighlightStart)
            {
                start ??= text.Length;
            }
            else if (c == SearchDsl.HighlightEnd)
            {
                if (start is { } s && text.Length > s)
                {
                    spans.Add(new TextSpan(s, text.Length));
                }

                start = null;
            }
            else
            {
                text.Append(c);
            }
        }

        return new SearchSnippet(text.ToString(), spans);
    }

    private static string? String(JsonObject source, string field) => source[field] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    private static long? Number(JsonObject source, string field) =>
        source[field] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    private static IReadOnlyList<SearchFacet> Facets(JsonObject? aggregations, IReadOnlyList<string> facets) =>
        [.. facets.Select(f => new SearchFacet(f, [.. (aggregations?[SearchDsl.FacetPrefix + f]?["buckets"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(b => new SearchFacetBucket(b["key"]?.ToString() ?? string.Empty, b["doc_count"]?.GetValue<long>() ?? 0))]))];

    private static SearchResultPage EmptyPage(string normalized, int pageSize, SearchFreshness freshness) => new()
    {
        SearchId = null,
        Normalized = normalized,
        Items = [],
        Page = new SearchPageInfo(1, pageSize, 1, IsFirst: true, IsLast: true),
        Total = new TotalCount(0, TotalRelation.Eq),
        Freshness = freshness,
    };

    /// <summary>Search.Executed with the full query text in the restricted details (Q-16), before results are returned.</summary>
    private async Task AuditExecutedAsync(
        SearchCaller caller,
        VisibilityFilter visibility,
        SearchSessionRecord search,
        QueryPlan plan,
        Guid? searchId,
        TotalCount total,
        int returned,
        int dropped,
        CancellationToken cancellationToken,
        Guid? savedSearchId = null)
    {
        var details = new Dictionary<string, string?>
        {
            ["pageSize"] = Invariant(search.PageSize),
            ["sort"] = search.SortJson,
            ["countExact"] = search.CountExact ? "true" : "false",
            ["total"] = Invariant(total.Value),
            ["totalRelation"] = total.Relation == TotalRelation.Eq ? "eq" : "gte",
            ["returned"] = Invariant(returned),
            ["postFilterDropped"] = Invariant(dropped),
            ["queryClass"] = plan.QueryClass,
        };
        if (savedSearchId is { } saved)
        {
            // E07-T09: the run of a saved search (its criteria are the query below, as stored and re-parsed now).
            details["savedSearchId"] = saved.ToString();
        }

        var restricted = new Dictionary<string, string?>
        {
            ["query"] = search.QueryText,
            ["normalized"] = plan.Normalized,
        };
        var ast = QueryAstJson.Serialize(plan.Ast!);
        if (ast.Length <= AuditEventRules.MaxRestrictedDetailsBytes / 2)
        {
            restricted["ast"] = ast;
        }

        await WriteAuditAsync(caller, visibility.BreakGlass, AuditTaxonomy.SearchCategory, "Executed", searchId, details, restricted, cancellationToken)
            .ConfigureAwait(false);
        if (search.CountExact)
        {
            await WriteAuditAsync(caller, visibility.BreakGlass, AuditTaxonomy.SearchCategory, "CountExact", searchId,
                new Dictionary<string, string?> { ["total"] = Invariant(total.Value) }, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records a saved search's run (time, the runner's hit count, freshness) as its last run.</summary>
    private Task RecordSavedRunAsync(
        SearchCaller caller, SavedSearchRunSource? saved, TotalCount total, SearchFreshness freshness, CancellationToken cancellationToken) =>
        saved is null || savedSearches is null
            ? Task.CompletedTask
            : savedSearches.RecordRunAsync(caller.WorkspaceId, saved.SavedSearchId,
                new SavedSearchRunResult(freshness.AsOf, total.Value, total.Relation == TotalRelation.Eq, freshness.Current, freshness.ServedGeneration),
                cancellationToken);

    private Task AuditPageServedAsync(
        SearchCaller caller, VisibilityFilter visibility, Guid searchId, Navigation navigation, ServedPage served, SearchResult result,
        CancellationToken cancellationToken) =>
        WriteAuditAsync(caller, visibility.BreakGlass, AuditTaxonomy.SearchCategory, "ResultsPageServed", searchId,
            new Dictionary<string, string?>
            {
                ["navigation"] = navigation.Kind,
                ["page"] = served.Page.Page.Number is { } n ? Invariant(n) : null,
                ["returned"] = Invariant(served.Page.Items.Count),
                ["postFilterDropped"] = Invariant(served.Dropped),
                ["resultsRefreshed"] = result.Refreshed ? "true" : "false",
                ["readerReestablished"] = result.RefreshReason,
            },
            null,
            cancellationToken);

    private Task AuditHandleMismatchAsync(SearchCaller caller, Guid searchId, CancellationToken cancellationToken, string reason = HandleMismatchReason)
    {
        LogHandleMismatch(logger, caller.WorkspaceId, caller.Principal.UserId);
        return WriteAuditAsync(caller, false, AuditTaxonomy.AuthZ.Category, AuditTaxonomy.AuthZ.Denied, searchId,
            new Dictionary<string, string?> { ["permission"] = "Search.Execute" }, null, cancellationToken, AuditOutcome.Denied, reason);
    }

    private Task AuditCursorMismatchAsync(SearchCaller caller, Guid searchId, SearchCursorBinding binding, CancellationToken cancellationToken)
    {
        var boundTo = binding.UserId != caller.Principal.UserId ? "anotherUser"
            : binding.SessionId != caller.SessionId ? "anotherSession"
            : "anotherSearch";
        LogCursorMismatch(logger, caller.WorkspaceId, caller.Principal.UserId, boundTo);
        return WriteAuditAsync(caller, false, AuditTaxonomy.AuthZ.Category, AuditTaxonomy.AuthZ.Denied, searchId,
            new Dictionary<string, string?> { ["permission"] = "Search.Execute", ["cursorBoundTo"] = boundTo }, null, cancellationToken,
            AuditOutcome.Denied, CursorMismatchReason);
    }

    private async Task WriteAuditAsync(
        SearchCaller caller,
        bool breakGlass,
        string category,
        string action,
        Guid? searchId,
        IReadOnlyDictionary<string, string?> details,
        IReadOnlyDictionary<string, string?>? restricted,
        CancellationToken cancellationToken,
        AuditOutcome outcome = AuditOutcome.Success,
        string? reason = null)
    {
        var principal = caller.Principal;
        await audit.WriteAsync(new AuditEvent
        {
            WorkspaceId = caller.WorkspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = principal.DisplayName,
            AccessPath = breakGlass ? AuditAccessPath.BreakGlass : AuditAccessPath.Normal,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            ResourceType = ResourceType,
            ResourceId = searchId?.ToString("N") ?? "none",
            Outcome = outcome,
            ReasonCode = reason,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            SearchGeneration = null,
            Details = details,
            RestrictedDetails = restricted,
        }, cancellationToken).ConfigureAwait(false);
    }

    private void RecordDuration(long started, string queryClass, string outcome) =>
        metrics?.Histogram(OpportunityMetricCatalog.SearchRequestDuration).Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new KeyValuePair<string, object?>(SearchClassAttribute, queryClass),
            new KeyValuePair<string, object?>(TelemetryAttributes.Outcome, outcome));

    private void Dropped(int count, string reason) =>
        metrics?.Counter(OpportunityMetricCatalog.SearchPostFilterDropped).Add(count, new KeyValuePair<string, object?>(DropReasonAttribute, reason));

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Search {SearchId} in workspace {WorkspaceId}: point-in-time reader expired, reopened")]
    private static partial void LogPointInTimeReopened(ILogger logger, Guid workspaceId, Guid searchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Search {SearchId} in workspace {WorkspaceId}: point-in-time reader reached its maximum age, re-established")]
    private static partial void LogPointInTimeAgedOut(ILogger logger, Guid workspaceId, Guid searchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed {Count} point-in-time reader(s) of user {UserId} in workspace {WorkspaceId} beyond the per-user cap")]
    private static partial void LogReadersCapped(ILogger logger, int count, Guid userId, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search handle of workspace {WorkspaceId} replayed by user {UserId} or another session; answered 404")]
    private static partial void LogHandleMismatch(ILogger logger, Guid workspaceId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search cursor of workspace {WorkspaceId} replayed by user {UserId} on another search ({BoundTo}); answered 404")]
    private static partial void LogCursorMismatch(ILogger logger, Guid workspaceId, Guid userId, string boundTo);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropped {Count} hit(s) in workspace {WorkspaceId} whose projection row names another workspace or no document")]
    private static partial void LogIntegrityDrop(ILogger logger, Guid workspaceId, int count);

    private sealed record QueryPlan(QueryNode? Ast, JsonObject? Query, string Normalized, string QueryClass, IReadOnlyList<QueryValidationDiagnostic>? Errors)
    {
        public IReadOnlyList<SourceSpan> BoundedExpansions { get; init; } = [];

        /// <summary>The positioned errors of a search OpenSearch refused: at the bounded wildcards, else the whole query.</summary>
        public IReadOnlyList<QueryValidationDiagnostic> Rejection(string code)
        {
            var message = code == SearchQueryErrorCodes.WildcardTooBroad
                ? "A wildcard inside W/n matches too many different words to search exactly; add more literal characters."
                : "The search took too long and was stopped; narrow the query (fewer wildcards, smaller W/n) and try again.";
            IReadOnlyList<SourceSpan> spans = code == SearchQueryErrorCodes.WildcardTooBroad && BoundedExpansions.Count > 0
                ? BoundedExpansions
                : [Ast!.Span];
            return [.. spans.Select(s => new QueryValidationDiagnostic(code, message, new TextSpan(s.Start, s.End), []))];
        }

        public static QueryPlan Failed(IReadOnlyList<QueryDiagnostic> errors) => new(null, null, string.Empty, SearchTranslation.Simple,
            [.. errors.Select(d => new QueryValidationDiagnostic(d.Code, d.Message, new TextSpan(d.Span.Start, d.Span.End), d.Expected))]);
    }

    private sealed record SearchResult(
        IReadOnlyList<JsonObject> Hits, TotalCount Total, string PointInTimeId, bool Refreshed, JsonObject? Aggregations)
    {
        /// <summary>When a reader was (re-)established for this request; null when the page ran on the existing one.</summary>
        public DateTimeOffset? OpenedAt { get; init; }

        /// <summary><see cref="ReaderExpired"/>, <see cref="ReaderAgedOut"/> or <see cref="ReaderDetached"/> when refreshed.</summary>
        public string? RefreshReason { get; init; }
    }

    private sealed record ServedPage(SearchResultPage Page, IReadOnlyList<SearchCursorRecord> Cursors, int Dropped);

    /// <summary>How a page is reached: from the top, after/before a cursor position, a page jump, or the last page.</summary>
    private sealed record Navigation(string Kind, JsonArray? SearchAfter, bool Reverse, int From, int? PageNumber, bool IsLast)
    {
        public static Navigation First() => new("first", null, false, 0, 1, false);

        public static Navigation After(JsonArray position, int? page) => new("next", position, false, 0, page, false);

        public static Navigation Before(JsonArray position, int? page) => new("previous", position, true, 0, page, false);

        public static Navigation Jump(int page, int from) => new("page", null, false, from, page, false);

        public static Navigation LastPage() => new("last", null, true, 0, null, true);
    }
}

/// <summary>OpenSearch refused or did not finish a planned query; answered as a positioned query error (400).</summary>
internal sealed class QueryRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
