import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { listFields } from '../../../core/api/generated/fn/fields/list-fields';
import { getSearchPage } from '../../../core/api/generated/fn/search/get-search-page';
import { runSearch } from '../../../core/api/generated/fn/search/run-search';
import type {
  FieldResource,
  SearchRequest,
  SearchResultPage,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';

/**
 * `POST …/searches`: a query, a saved search by id (it runs the stored query, filtered for the caller), or one term of a
 * Search Terms Report (its hits within the report's frozen set, filtered for the caller; wave-11 contract, #72).
 */
export type GridSearchRequest = Omit<SearchRequest, 'query'> &
  (
    | { query: string | null }
    | { savedSearchId: string }
    | { searchTermReportId: string; termId: string }
  );

/** Which page of a running search to fetch (exactly one; `GET …/searches/{id}/pages`, Q-49). */
export type PageRequest = { cursor: string } | { page: number } | { last: true };

/**
 * The search endpoints behind the document list (E07-T05) and the field catalogue (E07-T02) over the generated
 * client. Workspace-scoped: provided by the grid.
 */
@Injectable()
export class ReviewSearchApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly workspaceId = inject(WorkspaceContext).workspaceId;

  /** Runs a search (a query, or a saved search by id) and returns its first page. */
  run(request: GridSearchRequest): Promise<SearchResultPage> {
    // `savedSearchId` (wave 9, #71) or `searchTermReportId` + `termId` (wave 11, #72) instead of `query`; the generated
    // type follows them.
    const body = request as unknown as SearchRequest;
    return firstValueFrom(
      runSearch(this.http, this.rootUrl, { workspaceId: this.workspaceId, body }).pipe(
        map((r) => r.body),
      ),
    );
  }

  /** Another page of a search; 404 when the search expired or is not the caller's. */
  page(searchId: string, request: PageRequest): Promise<SearchResultPage> {
    return firstValueFrom(
      getSearchPage(this.http, this.rootUrl, {
        workspaceId: this.workspaceId,
        searchId,
        ...request,
      }).pipe(map((r) => r.body)),
    );
  }

  /** The workspace's fields with their capabilities (the first page holds every field of a workspace). */
  fields(): Promise<FieldResource[]> {
    return firstValueFrom(
      listFields(this.http, this.rootUrl, { workspaceId: this.workspaceId }).pipe(
        map((r) => r.body.items),
      ),
    );
  }
}
