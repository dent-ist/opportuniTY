# Logical search service (E07-T05)

`ISearchService` (Application) is the only search path. `SearchService` (internal) runs every page as:

1. **Plan** the query text: `QueryParser` (ADR-008) → `ISearchQueryTranslator` (`SearchQueryPlanner`) → user clause.
2. **Visibility** from the PDP (`GetVisibilityAsync`, needs `Search.Execute`).
3. **Filter** (`SearchDsl.Query`): `bool.filter[0]` = `term workspaceId` (from the authorized route), then
   `must_not terms securityTags` for denied classes/walls; the user clause goes only into `bool.must`.
4. **Search** a point-in-time reader of the placement's read alias (`IIndexManager`; routing on shared indexes) with
   `search_after` and the `documentId` tie-breaker, `track_total_hits` capped at 10,000 (Q-32), bounded snippets.
   Without an explicit sort, a query with a keyword (an unfielded term, phrase, wildcard or proximity outside a NOT)
   sorts by relevance and any other query (empty, filters only) by Control Number ascending (`SortKey.DefaultFor`); an
   explicit sort always wins. Sort keys are the fixed `SearchSortFields` or the query name of any workspace field with
   the `sortable` capability (`SearchColumns.SortKeyFor`: text on its `.kw` companion, keyword on its natural-sort
   key; choice and user fields never), stored with their resolved path in `search_session.sort_keys`. Every sort ends
   with `controlNumberSort` ascending (unless already sorted by it) and then `documentId`, so ties page deterministically
   (E16-T09). `SearchRequest.Fields` names the fields whose values each hit carries in `SearchHit.Fields` (strings,
   bounded); they are resolved once and kept in `search_session.result_fields` for later pages.
5. **Post-filter** the page: `AuthorizeManyAsync(Document.View, DenialAudit.Summary)` against PostgreSQL (Q-12, Q-59).
   Then one size-0 aggregation on the same reader and outer filter marks family parents (`SearchHit.IsFamilyParent`:
   top-level documents of the page whose family has members with `familySequence >= 1`).
6. **Persist** the reader and positions in `search_session` / `search_cursor` (V0013, RLS); clients get opaque IDs
   bound to (user, session, workspace). Mismatches answer 404 and are audited (`AuthZ.Denied`, `SearchHandleMismatch`).
7. **Audit** `Search.Executed` with the full text in restricted details (Q-16); later pages `Search.ResultsPageServed`.

## Interactive reader lifecycle (E10-T03, ADR-002 §8, Q-33)

Interactive grid and review cursors always page a live point-in-time reader (`SnapshotStrategyRules.Decide` with
`SetOperationKind.InteractiveCursor`; never materialized in the MVP). Settings under `OpenSearch:Search`:

- `PointInTimeKeepAlive` (5 min): renewed by every page and by the family-parent aggregation.
- `PointInTimeMaxAge` (30 min): a page asked for after that closes the reader and opens a new one.
- `MaxOpenPointInTimesPerUser` (3, per user and workspace): a new search detaches and closes the user's oldest readers
  beyond the cap (`ISearchSessionStore.DetachReadersAsync`); the detached search keeps its handle and cursors.

Whenever a page cannot use its reader — expired or lost (OpenSearch `search_context_missing_exception`), aged out, or
detached — the service opens a new reader with the same query and sort, resumes `search_after` from the cursor's
stored sort values (or the page offset), stores the new reader with its open time and the watermark read before it
(`search_session.pit_opened_at` / `served_generation`, V0030), and answers with `resultsRefreshed: true` and the new
`freshness`. `Search.ResultsPageServed` records `readerReestablished` (`expired`, `maxAge` or `detached`). The grid
and Review mode show "Results refreshed"; a document that left the set gets "no longer in the results — continue from
next".

## Snapshot selection (E10-T02)

`SelectAsync` enumerates every matching document ID for snapshot materialization (ADR-002 §5.1): optional index
refresh, one point-in-time reader (`SelectionKeepAlive`), sort by `documentId` only, `_source: false`, the same outer
filter, an exact count first (`TooManyHits` above the caller's bound), and the Q-12 PostgreSQL re-check of every page
(one summary `AuthZ.Denied` per selection). A lost reader returns `ReaderLost`; partial or timed-out pages abort the
selection. No search handle is stored; `DocumentSetSnapshotService` audits the selection with what it froze.

## Plug-in points for parallel tickets

- **Planner (E07-T07, #69)** – `SearchQueryPlanner` is the registered `ISearchQueryTranslator` (replace it by registering
  another before `AddOpenSearchSearchService`, which uses `TryAdd`). It binds query names (`FieldQueryNames`, ADR-008 R11)
  to projection paths (`ProjectionFieldPaths`) through `SearchFieldResolver`, using the catalogue's capability flags;
  the catalogue comes from `IFieldCatalogRepository` (cached `OpenSearch:Search:FieldCatalogCacheTtl`, default 5 s).
  Terms use `match`/`match_phrase`, keywords `term` (case-insensitive), choices are resolved by name to ChoiceIds, dates
  are whole units in the context zone (R8, UTC until the user zone is wired), `W/n` is `span_near` over tokens from
  OpenSearch's own analyzers (`OpenSearchTextAnalyzer`, derived from the mapping), wildcards follow R7 (leading only with
  `LeadingWildcard`, matched on `fileName.wc`; inside `W/n` via `span_multi` + `constant_score_boolean`). Limits:
  `QueryLimits.MaxClauses` (after custodian expansion / choice resolution) and `MaxProximityWildcards`; OpenSearch's
  clause-limit failure becomes `WILDCARD_TOO_BROAD` at the bounded wildcard spans and a timed-out search
  `QUERY_TIMEOUT` (both 400). Every problem is a positioned `QueryDiagnostic`, never a silent match-none, and the
  validate endpoint reports the same errors through `IQueryBinder`. `QueryClass` follows the §29 gate classifier
  (coding filters, sort and facets make a query complex). Golden DSL lives next to the AST in
  `tests/Opportunity.UnitTests/QueryLanguage/Golden` (fields: `PlannerFixture`).
- **Projection (E07-T02, #64)** – documents must carry the fields in `ProjectionFields` (`workspaceId` and
  `documentId` as `Guid "D"` strings, `controlNumber.sort`, `fileName.kw`, stored `text`) and encode security
  attributes in `securityTags` as `SecurityTags.Class(classKey)` / `SecurityTags.Wall(wallId)`. Grid columns come from
  `ProjectionFields.GridSource`; extend it (and `SearchHit`) when field capabilities arrive.
- **Watermark (E07-T08)** – interim: `ISearchWatermarkReader` reads the applied watermark (ADR-001 §7.2) before the
  reader opens; it is stored as `search_session.served_generation` and served as `SearchFreshness.ServedGeneration`,
  with `Current` true while the workspace's generation counter still equals it. E07-T08 makes it refresh-aware and
  fills `AuditEvent.SearchGeneration`.
- **Pending-set exclusion (ADR-015 D8.3, E05-T06)** – add the unindexed security-change IDs as a second server-side
  `must_not` in `SearchDsl.Query`.
- **Saved searches (E07-T09, #71)** – `SearchService` takes an optional `ISavedSearchQueries`: `PlanAsync` expands
  `savedsearch:<id>` references (ADR-008 §9) before translation, `SearchRequest.SavedSearchId` runs a stored query
  (re-parsed now, its stored sort unless one is given) and records the last run, and the `Search.Executed` audit event
  carries `savedSearchId`. Snapshots freeze a saved search as the query `savedsearch:<id>`.
