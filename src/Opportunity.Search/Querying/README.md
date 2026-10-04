# Logical search service (E07-T05)

`ISearchService` (Application) is the only search path. `SearchService` (internal) runs every page as:

1. **Plan** the query text: `QueryParser` (ADR-008) → `ISearchQueryTranslator` → user clause.
2. **Visibility** from the PDP (`GetVisibilityAsync`, needs `Search.Execute`).
3. **Filter** (`SearchDsl.Query`): `bool.filter[0]` = `term workspaceId` (from the authorized route), then
   `must_not terms securityTags` for denied classes/walls; the user clause goes only into `bool.must`.
4. **Search** a point-in-time reader of the placement's read alias (`IIndexManager`; routing on shared indexes) with
   `search_after` and the `documentId` tie-breaker, `track_total_hits` capped at 10,000 (Q-32), bounded snippets.
5. **Post-filter** the page: `AuthorizeManyAsync(Document.View, DenialAudit.Summary)` against PostgreSQL (Q-12, Q-59).
6. **Persist** the reader and positions in `search_session` / `search_cursor` (V0013, RLS); clients get opaque IDs
   bound to (user, session, workspace). Mismatches answer 404 and are audited (`AuthZ.Denied`, `SearchHandleMismatch`).
7. **Audit** `Search.Executed` with the full text in restricted details (Q-16); later pages `Search.ResultsPageServed`.

## Plug-in points for parallel tickets

- **Planner (E07-T07, #69)** – replace `BasicSearchQueryTranslator` by registering another `ISearchQueryTranslator`
  before `AddOpenSearchSearchService` (it uses `TryAdd`). Return positioned `QueryDiagnostic`s (never a silent
  match-none) and `SearchTranslation.Simple`/`Complex` for the latency metric. Never address
  `ProjectionFields.NotAddressable`; whatever you return is confined to `bool.must`.
- **Projection (E07-T02, #64)** – documents must carry the fields in `ProjectionFields` (`workspaceId` and
  `documentId` as `Guid "D"` strings, `controlNumber.sort`, `fileName.kw`, stored `text`) and encode security
  attributes in `securityTags` as `SecurityTags.Class(classKey)` / `SecurityTags.Wall(wallId)`. Grid columns come from
  `ProjectionFields.GridSource`; extend it (and `SearchHit`) when field capabilities arrive.
- **Watermark (E07-T08)** – fill `SearchFreshness.ServedGeneration`/`Current` and `AuditEvent.SearchGeneration`.
- **Pending-set exclusion (ADR-015 D8.3, E05-T06)** – add the unindexed security-change IDs as a second server-side
  `must_not` in `SearchDsl.Query`.
