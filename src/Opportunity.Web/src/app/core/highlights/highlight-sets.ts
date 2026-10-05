import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  HighlightSetList,
  HighlightSetRequest,
  HighlightSetResource,
  HighlightSetSelection,
} from '../api/generated/models';
import { WorkspaceContext } from '../workspace/workspace-context';

/**
 * Highlight Sets (E16-T12, familiarity guide §3.2 persistent highlighting): workspace term lists with a colour that
 * reviewers switch on and off in the viewer. Colours are palette names; each maps to the `--opp-color-hl-<name>-*`
 * design tokens (fill, text, underline), which meet 4.5:1 for text and 3:1 for the underline in every theme.
 */
export type HighlightColor = 'search' | 'amber' | 'green' | 'blue' | 'violet' | 'rose' | 'teal';

/** Display names of the palette, in palette order ("search" is the built-in Search hits colour). */
export const HIGHLIGHT_COLOR_LABELS: Readonly<Record<HighlightColor, string>> = {
  search: 'Cyan',
  amber: 'Amber',
  green: 'Green',
  blue: 'Blue',
  violet: 'Violet',
  rose: 'Rose',
  teal: 'Teal',
};

export function highlightColor(name: string | null | undefined): HighlightColor {
  return name && name in HIGHLIGHT_COLOR_LABELS ? (name as HighlightColor) : 'search';
}

export interface HighlightTerm {
  readonly termId: string | null;
  readonly expression: string;
  readonly color: string | null;
}

export interface HighlightSet {
  readonly highlightSetId: string;
  readonly name: string;
  readonly description: string | null;
  readonly color: string;
  readonly terms: readonly HighlightTerm[];
  readonly modifiedBy: string;
  readonly modifiedAt: string;
  readonly version: number;
}

export interface HighlightSetDraft {
  readonly name: string;
  readonly description: string | null;
  readonly color: string;
  readonly terms: readonly HighlightTerm[];
}

export interface HighlightToggles {
  readonly disabledSetIds: readonly string[];
  readonly searchHits: boolean;
}

/** The Highlight Set routes under `/api/v1/workspaces/{id}` (ADR-019; If-Match on replace and delete). */
@Injectable()
export abstract class HighlightSetsApi {
  /** `GET …/highlight-sets`: every set by name, and the palette the API accepts. */
  abstract list(): Promise<{ sets: readonly HighlightSet[]; colors: readonly string[] }>;
  /** `POST …/highlight-sets` (HighlightSet.Manage). */
  abstract create(draft: HighlightSetDraft): Promise<HighlightSet>;
  /** `PUT …/highlight-sets/{id}` with If-Match. */
  abstract update(set: HighlightSet, draft: HighlightSetDraft): Promise<HighlightSet>;
  /** `DELETE …/highlight-sets/{id}` with If-Match. */
  abstract delete(set: HighlightSet): Promise<void>;
  /** `GET …/highlight-set-selection`: the caller's toggles. */
  abstract toggles(): Promise<HighlightToggles>;
  /** `PUT …/highlight-set-selection`. */
  abstract setToggles(toggles: HighlightToggles): Promise<HighlightToggles>;
}

const SETS = 'highlight-sets';
const SELECTION = 'highlight-set-selection';

@Injectable()
export class HttpHighlightSetsApi extends HighlightSetsApi {
  private readonly http = inject(HttpClient);
  private readonly context = inject(WorkspaceContext);

  async list(): Promise<{ sets: readonly HighlightSet[]; colors: readonly string[] }> {
    const body = await firstValueFrom(this.http.get<HighlightSetList>(this.context.apiUrl(SETS)));
    return { sets: body.items.map(toSet), colors: body.colors };
  }

  async create(draft: HighlightSetDraft): Promise<HighlightSet> {
    return toSet(
      await firstValueFrom(
        this.http.post<HighlightSetResource>(this.context.apiUrl(SETS), toRequest(draft)),
      ),
    );
  }

  async update(set: HighlightSet, draft: HighlightSetDraft): Promise<HighlightSet> {
    return toSet(
      await firstValueFrom(
        this.http.put<HighlightSetResource>(
          this.context.apiUrl(SETS, set.highlightSetId),
          toRequest(draft),
          { headers: { 'If-Match': `"${set.version}"` } },
        ),
      ),
    );
  }

  async delete(set: HighlightSet): Promise<void> {
    await firstValueFrom(
      this.http.delete(this.context.apiUrl(SETS, set.highlightSetId), {
        headers: { 'If-Match': `"${set.version}"` },
      }),
    );
  }

  async toggles(): Promise<HighlightToggles> {
    return firstValueFrom(this.http.get<HighlightSetSelection>(this.context.apiUrl(SELECTION)));
  }

  async setToggles(toggles: HighlightToggles): Promise<HighlightToggles> {
    const body: HighlightSetSelection = {
      disabledSetIds: [...toggles.disabledSetIds],
      searchHits: toggles.searchHits,
    };
    return firstValueFrom(
      this.http.put<HighlightSetSelection>(this.context.apiUrl(SELECTION), body),
    );
  }
}

function toSet(r: HighlightSetResource): HighlightSet {
  return {
    highlightSetId: r.highlightSetId,
    name: r.name,
    description: r.description ?? null,
    color: r.color,
    terms: r.terms.map((t) => ({
      termId: t.termId,
      expression: t.expression,
      color: t.color ?? null,
    })),
    modifiedBy: r.modifiedBy.displayName,
    modifiedAt: r.modifiedAt,
    version: Number(r.version),
  };
}

function toRequest(draft: HighlightSetDraft): HighlightSetRequest {
  return {
    name: draft.name,
    description: draft.description,
    color: draft.color,
    terms: draft.terms.map((t) => ({
      expression: t.expression,
      color: t.color,
      termId: t.termId,
    })),
  };
}

/**
 * The reviewer's highlighting in Review mode: the workspace's sets and which of them are on (persisted per user and
 * workspace through `…/highlight-set-selection`). Loaded once per Documents page; a failure leaves only the search
 * hits, never blocks the viewer.
 */
@Injectable()
export class HighlightState {
  private readonly api = inject(HighlightSetsApi);
  private readonly _sets = signal<readonly HighlightSet[]>([]);
  private readonly _toggles = signal<HighlightToggles>({ disabledSetIds: [], searchHits: true });
  private loading: Promise<void> | null = null;

  readonly sets = this._sets.asReadonly();
  readonly searchHits = computed(() => this._toggles().searchHits);
  /** The sets that are on, in name order. */
  readonly enabledSets = computed(() => {
    const off = new Set(this._toggles().disabledSetIds);
    return this._sets().filter((s) => !off.has(s.highlightSetId));
  });

  load(): Promise<void> {
    this.loading ??= Promise.all([this.api.list(), this.api.toggles()]).then(
      ([list, toggles]) => {
        this._sets.set(list.sets);
        this._toggles.set(toggles);
      },
      () => undefined,
    );
    return this.loading;
  }

  isOn(highlightSetId: string): boolean {
    return !this._toggles().disabledSetIds.includes(highlightSetId);
  }

  setSearchHits(on: boolean): void {
    this.save({ ...this._toggles(), searchHits: on });
  }

  setSet(highlightSetId: string, on: boolean): void {
    const off = this._toggles().disabledSetIds.filter((id) => id !== highlightSetId);
    this.save({ ...this._toggles(), disabledSetIds: on ? off : [...off, highlightSetId] });
  }

  private save(toggles: HighlightToggles): void {
    // Optimistic: the viewer follows at once; the preference is stored in the background (last write wins).
    this._toggles.set(toggles);
    this.api.setToggles(toggles).catch(() => undefined);
  }
}
