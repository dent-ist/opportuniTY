import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  model,
  signal,
} from '@angular/core';
import type { SnapshotResourcePurpose } from '../../../core/api/generated/models';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import {
  Announcer,
  Badge,
  Button,
  Checkbox,
  ErrorState,
  Icon,
  Select,
  SelectOption,
  TextField,
} from '../../../ui';
import { FrozenSet, FrozenSetApi, SavedSearchChoice, newIdempotencyKey } from './frozen-set-api';
import { DisplayFormat } from '../../../core/i18n';

export type SourceKind = 'savedSearch' | 'frozenSet';

/** How long the source step waits for a large set to materialize before it asks the user to check again. */
const POLL_MS = 1000;
const MAX_POLLS = 120;

/**
 * Source of an export or a production (familiarity guide §5.5 step 1, §5.6 Source): a saved search, frozen now with
 * Include: Family / Duplicates, or an existing frozen set by ID. The result is always a frozen set whose ID and
 * document count are shown, so the user sees exactly which documents will go out before anything runs. A live saved
 * search is never called frozen.
 *
 * Keyboard: native radios, select, checkboxes and buttons; the result is announced.
 */
@Component({
  selector: 'opp-frozen-set-source',
  imports: [Badge, Button, Checkbox, ErrorState, Icon, Select, TextField],
  templateUrl: './frozen-set-source.html',
  styleUrl: '../delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FrozenSetSource {
  private readonly api = inject(FrozenSetApi);
  private readonly fmt = inject(DisplayFormat);
  private readonly announcer = inject(Announcer);
  private destroyed = false;

  /** Snapshot purpose the set is frozen for (exports and productions accept only their own). */
  readonly purpose = input.required<SnapshotResourcePurpose>();
  /** The legend of the source choice (review batches take documents to review, not to send). */
  readonly legend = input('Documents to send');
  /** A saved search to preselect (`?savedSearch=` from Searches). */
  readonly initialSavedSearch = input<string | null>(null);
  /** The frozen set chosen; null until one is Ready. */
  readonly frozen = model<FrozenSet | null>(null);

  protected readonly kind = signal<SourceKind>('savedSearch');
  protected readonly searches = signal<readonly SavedSearchChoice[] | null>(null);
  protected readonly savedSearch = signal('');
  protected readonly family = signal(true);
  protected readonly duplicates = signal(false);
  protected readonly snapshotId = signal('');
  protected readonly busy = signal(false);
  protected readonly waiting = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly searchesError = signal<ApiError | null>(null);
  protected readonly mismatch = signal<string | null>(null);
  private key = newIdempotencyKey();

  protected readonly searchOptions = computed<SelectOption[]>(() =>
    (this.searches() ?? []).map((s) => ({
      value: s.savedSearchId,
      label:
        s.lastHitCount === null
          ? s.name
          : `${s.name} (last run: ${this.num(s.lastHitCount)} documents)`,
    })),
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    void this.loadSearches();
  }

  private async loadSearches(): Promise<void> {
    try {
      const searches = await this.api.savedSearches();
      this.searches.set(searches);
      const initial = this.initialSavedSearch();
      const chosen = searches.find((s) => s.savedSearchId === initial) ?? searches[0];
      if (chosen) this.savedSearch.set(chosen.savedSearchId);
    } catch (e) {
      this.searches.set([]);
      this.searchesError.set(toApiError(e));
    }
  }

  protected setKind(kind: SourceKind): void {
    this.kind.set(kind);
    this.changed();
  }

  /** Any change of the source drops the frozen set: it no longer describes what will go out. */
  protected changed(): void {
    this.frozen.set(null);
    this.error.set(null);
    this.mismatch.set(null);
    this.key = newIdempotencyKey();
  }

  protected async freeze(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.mismatch.set(null);
    try {
      let set: FrozenSet;
      if (this.kind() === 'savedSearch') {
        if (!this.savedSearch()) return;
        set = await this.api.freeze(
          {
            savedSearchId: this.savedSearch(),
            family: this.family(),
            duplicates: this.duplicates(),
          },
          this.purpose(),
          this.key,
        );
      } else {
        const id = this.snapshotId().trim();
        if (!id) return;
        set = await this.api.frozenSet(id);
        if (set.purpose !== this.purpose()) {
          this.mismatch.set(
            `This frozen set was made for ${PURPOSE_LABEL[set.purpose]}; freeze the set again for ${PURPOSE_LABEL[this.purpose()]}.`,
          );
          return;
        }
      }
      set = await this.untilReady(set);
      if (set.status === 'ready') {
        this.frozen.set(set);
        this.announcer.announce(
          `Frozen set ${set.snapshotId}: ${this.num(set.documentCount ?? 0)} documents.`,
        );
      } else if (set.status === 'failed' || set.status === 'expired') {
        this.mismatch.set(set.statusReason ?? `The frozen set is ${set.status}.`);
      }
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.busy.set(false);
    }
  }

  private async untilReady(set: FrozenSet): Promise<FrozenSet> {
    let polls = 0;
    this.waiting.set(set.status === 'materializing');
    while (set.status === 'materializing' && polls++ < MAX_POLLS && !this.destroyed) {
      await new Promise((resolve) => setTimeout(resolve, POLL_MS));
      set = await this.api.frozenSet(set.snapshotId);
    }
    this.waiting.set(false);
    return set;
  }

  protected num(n: number): string {
    return this.fmt.number(n);
  }
}

const PURPOSE_LABEL: Record<SnapshotResourcePurpose, string> = {
  bulkCoding: 'a mass edit',
  export: 'an export',
  production: 'a production',
  report: 'a report',
  reviewBatch: 'a review batch',
  listExport: 'a list export',
};
