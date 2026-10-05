import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, map } from 'rxjs';
import { ApiConfiguration } from '../../../core/api/generated/api-configuration';
import { createGridView } from '../../../core/api/generated/fn/grid-views/create-grid-view';
import { getGridLayout } from '../../../core/api/generated/fn/grid-views/get-grid-layout';
import { listGridViews } from '../../../core/api/generated/fn/grid-views/list-grid-views';
import { saveGridLayout } from '../../../core/api/generated/fn/grid-views/save-grid-layout';
import type {
  GridLayoutResource,
  GridViewColumn,
  GridViewResource,
  SearchSortKey,
} from '../../../core/api/generated/models';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import type { ColumnSpec, SortSpec } from './grid-columns';

/** A saved View of the document list (E16-T09): columns and sort, personal or shared with the workspace. */
export interface GridView {
  readonly viewId: string;
  readonly name: string;
  readonly visibility: 'personal' | 'shared';
  readonly owner: { readonly userId: string; readonly displayName: string };
  readonly columns: readonly ColumnSpec[];
  readonly sort: readonly SortSpec[];
  readonly version: number;
  readonly canEdit: boolean;
}

/** Body of create and replace. */
export interface GridViewDraft {
  readonly name: string;
  readonly visibility: 'personal' | 'shared';
  readonly columns: readonly ColumnSpec[];
  readonly sort: readonly SortSpec[];
}

/** The user's list layout in this workspace: the view last used and their adjustments (null: the view's own). */
export interface GridLayout {
  readonly viewId: string | null;
  readonly columns: readonly ColumnSpec[] | null;
  readonly sort: readonly SortSpec[] | null;
}

/** `…/grid-views` as a port, so the grid and its tests do not depend on HTTP. Provided by the grid. */
@Injectable()
export abstract class GridViewApi {
  abstract list(): Promise<readonly GridView[]>;
  abstract create(draft: GridViewDraft): Promise<GridView>;
  /** `PUT …/grid-views/{id}` with If-Match. */
  abstract update(view: GridView, draft: GridViewDraft): Promise<GridView>;
  /** `DELETE …/grid-views/{id}` with If-Match. */
  abstract delete(view: GridView): Promise<void>;
  abstract layout(): Promise<GridLayout>;
  abstract saveLayout(layout: GridLayout): Promise<void>;
}

const VIEWS = 'grid-views';
const ifMatch = (version: number) => ({ 'If-Match': `"${version}"` });

@Injectable()
export class HttpGridViewApi extends GridViewApi {
  private readonly http = inject(HttpClient);
  private readonly rootUrl = inject(ApiConfiguration).rootUrl;
  private readonly context = inject(WorkspaceContext);

  list(): Promise<readonly GridView[]> {
    return firstValueFrom(
      listGridViews(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }).pipe(
        map((r) => r.body.items.map(toView)),
      ),
    );
  }

  create(draft: GridViewDraft): Promise<GridView> {
    return firstValueFrom(
      createGridView(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        body: toBody(draft),
      }).pipe(map((r) => toView(r.body))),
    );
  }

  update(view: GridView, draft: GridViewDraft): Promise<GridView> {
    return firstValueFrom(
      this.http
        .put<GridViewResource>(this.context.apiUrl(VIEWS, view.viewId), toBody(draft), {
          headers: ifMatch(view.version),
        })
        .pipe(map(toView)),
    );
  }

  async delete(view: GridView): Promise<void> {
    await firstValueFrom(
      this.http.delete(this.context.apiUrl(VIEWS, view.viewId), {
        headers: ifMatch(view.version),
      }),
    );
  }

  layout(): Promise<GridLayout> {
    return firstValueFrom(
      getGridLayout(this.http, this.rootUrl, { workspaceId: this.context.workspaceId }).pipe(
        map((r) => toLayout(r.body)),
      ),
    );
  }

  async saveLayout(layout: GridLayout): Promise<void> {
    await firstValueFrom(
      saveGridLayout(this.http, this.rootUrl, {
        workspaceId: this.context.workspaceId,
        body: {
          viewId: layout.viewId,
          columns: layout.columns ? layout.columns.map(toColumn) : null,
          sort: layout.sort ? layout.sort.map(toKey) : null,
        },
      }),
    );
  }
}

function toBody(draft: GridViewDraft) {
  return {
    name: draft.name,
    visibility: draft.visibility,
    columns: draft.columns.map(toColumn),
    sort: draft.sort.map(toKey),
  };
}

function toColumn(c: ColumnSpec): GridViewColumn {
  return { field: c.field, width: c.width ?? null, pinned: !!c.pinned };
}

function toKey(s: SortSpec): SearchSortKey {
  return { field: s.field, direction: s.direction };
}

export function fromColumns(columns: readonly GridViewColumn[] | null | undefined): ColumnSpec[] {
  return (columns ?? []).map((c) => ({
    field: c.field,
    width: c.width === null || c.width === undefined ? null : Number(c.width),
    pinned: !!c.pinned,
  }));
}

export function fromSort(sort: readonly SearchSortKey[] | null | undefined): SortSpec[] {
  return (sort ?? []).map((s) => ({
    field: s.field,
    direction: String(s.direction).toLowerCase() === 'desc' ? 'desc' : 'asc',
  }));
}

function toView(v: GridViewResource): GridView {
  return {
    viewId: v.viewId,
    name: v.name,
    visibility: String(v.visibility).toLowerCase() === 'shared' ? 'shared' : 'personal',
    owner: v.owner,
    columns: fromColumns(v.columns),
    sort: fromSort(v.sort),
    version: Number(v.version),
    canEdit: !!v.canEdit,
  };
}

function toLayout(l: GridLayoutResource | null): GridLayout {
  return {
    viewId: l?.viewId ?? null,
    columns: l?.columns ? fromColumns(l.columns) : null,
    sort: l?.sort ? fromSort(l.sort) : null,
  };
}
