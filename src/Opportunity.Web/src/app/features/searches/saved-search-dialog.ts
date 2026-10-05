import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toApiError } from '../../core/api/problem-details';
import { Announcer, Button, DialogLayout, Icon, Select, TextField, Tooltip } from '../../ui';
import { QueryBar, QuerySubmission } from '../documents/search/query-bar';
import {
  SavedSearch,
  SavedSearchApi,
  SavedSearchDraft,
  SavedSearchFolder,
  SavedSearchSortKey,
} from './saved-search-api';
import {
  LIVE_EXPLANATION,
  copyName,
  folderOptions,
  savedSearchErrorText,
} from './saved-search-model';

export type SavedSearchDialogMode = 'create' | 'edit' | 'rename' | 'move' | 'copy';

export interface SavedSearchDialogData {
  readonly mode: SavedSearchDialogMode;
  readonly folders: readonly SavedSearchFolder[];
  /** The saved search being edited, renamed, moved or copied. */
  readonly saved?: SavedSearch;
  /** Create: the query to start from ("Save current search" in Documents) and its view. */
  readonly query?: string;
  /** Create: the list's columns (field query names) and sort, saved with the search (E16-T09). */
  readonly columns?: readonly string[];
  readonly sort?: readonly SavedSearchSortKey[];
  readonly folderId?: string | null;
  /** Create: the list's "Include: Family / Duplicates / Email thread" choice, stored with the search (E09-T03). */
  readonly include?: {
    readonly family: boolean;
    readonly duplicates: boolean;
    readonly thread: boolean;
  };
}

const TITLES: Record<SavedSearchDialogMode, string> = {
  create: 'New saved search',
  edit: 'Edit saved search',
  rename: 'Rename saved search',
  move: 'Move saved search',
  copy: 'Copy saved search',
};

const SUBMIT: Record<SavedSearchDialogMode, string> = {
  create: 'Save search',
  edit: 'Save changes',
  rename: 'Rename',
  move: 'Move',
  copy: 'Copy',
};

const MAX_NAME = 200;

/**
 * Create, edit, rename, move or copy a saved search (E16-T11). The editor has the name, folder and keyword query;
 * the Conditions builder (field · operator · value rows) is a separate ticket and has a marked place here. The
 * dialog saves through `SavedSearchApi` and closes with the saved resource; failures stay in the dialog.
 */
@Component({
  selector: 'opp-saved-search-dialog',
  imports: [Button, DialogLayout, Icon, QueryBar, Select, TextField, Tooltip],
  template: `<form (submit)="onSubmit($event)" novalidate>
    <opp-dialog-layout [title]="title">
      <div class="ssd">
        @if (mode === 'move') {
          <p class="ssd__lead">
            Move <strong>{{ data.saved?.name }}</strong> to another folder.
          </p>
        }
        @if (showName) {
          <opp-text-field
            #nameField
            label="Name"
            [required]="true"
            [(value)]="name"
            [error]="nameError()"
            [hint]="mode === 'copy' ? 'The copy is yours and private until you share it.' : ''"
          />
        }
        @if (showFolder) {
          <opp-select label="Folder" [options]="folderChoices" [(value)]="folderId" />
        }
        @if (showQuery) {
          <div class="ssd__query">
            <opp-query-bar label="Keyword search" (search)="save($event)" />
          </div>
          <!-- Conditions builder (#187) goes here: field · operator · value rows compiled to the query. -->
          <fieldset class="ssd__conditions">
            <legend>Conditions</legend>
            <p class="ssd__note">
              <opp-icon name="info" />
              Field conditions are not available in this version. Search fields in the keyword box
              instead, for example <code>custodian:smith</code> or <code>responsiveness:*</code>.
            </p>
            <button
              type="button"
              oppButton="secondary"
              aria-disabled="true"
              oppTooltip="Not available in this version"
              (click)="$event.preventDefault()"
            >
              <opp-icon name="plus" /> Add condition
            </button>
          </fieldset>
          <p class="ssd__note">{{ liveExplanation }}</p>
        }
        @if (error()) {
          <p class="ssd__error" role="alert">
            <opp-icon name="error" />
            {{ error() }}
          </p>
        }
      </div>
      <ng-container dialogActions>
        <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
        <button type="submit" oppButton="primary" [busy]="saving()">{{ submitLabel }}</button>
      </ng-container>
    </opp-dialog-layout>
  </form>`,
  styleUrl: './saved-search-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SavedSearchDialog {
  protected readonly data = inject<SavedSearchDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<SavedSearch>>(DialogRef);
  private readonly api = inject(SavedSearchApi);
  private readonly announcer = inject(Announcer);
  private readonly queryBar = viewChild(QueryBar);
  private readonly nameField = viewChild<TextField>('nameField');

  protected readonly mode = this.data.mode;
  protected readonly title = TITLES[this.mode];
  protected readonly submitLabel = SUBMIT[this.mode];
  protected readonly showName = this.mode !== 'move';
  protected readonly showFolder = this.mode !== 'rename';
  protected readonly showQuery = this.mode === 'create' || this.mode === 'edit';
  protected readonly liveExplanation = `Saved searches are live. ${LIVE_EXPLANATION}`;
  protected readonly folderChoices = folderOptions(this.data.folders);

  protected readonly name = signal(
    this.mode === 'copy' ? copyName(this.data.saved?.name ?? '') : (this.data.saved?.name ?? ''),
  );
  protected readonly folderId = signal(
    (this.mode === 'create' ? this.data.folderId : this.data.saved?.folderId) ?? '',
  );
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly submitted = signal(false);
  protected readonly nameError = computed(() => {
    if (!this.submitted() || !this.showName) return '';
    const name = this.name().trim();
    if (!name) return 'Enter a name.';
    if (name.length > MAX_NAME) return `Use at most ${MAX_NAME} characters.`;
    return '';
  });

  constructor() {
    afterNextRender(() => {
      const query = this.mode === 'edit' ? this.data.saved?.query : this.data.query;
      if (query) this.queryBar()?.load(query);
    });
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    const bar = this.queryBar();
    // The query bar checks the query first and emits only a valid one (then `save` runs).
    if (bar) void bar.submit();
    else void this.save(null);
  }

  protected async save(submission: QuerySubmission | null): Promise<void> {
    this.submitted.set(true);
    if (this.nameError()) {
      this.nameField()?.focus();
      this.announcer.announce(this.nameError(), { politeness: 'assertive' });
      return;
    }
    if (this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    try {
      this.ref.close(await this.write(submission?.query ?? null));
    } catch (e) {
      const message = savedSearchErrorText(toApiError(e));
      this.error.set(message);
      this.announcer.announce(message, { politeness: 'assertive' });
    } finally {
      this.saving.set(false);
    }
  }

  private write(query: string | null): Promise<SavedSearch> {
    const name = this.name().trim();
    const folderId = this.folderId() || null;
    const saved = this.data.saved;
    switch (this.mode) {
      case 'create':
        return this.api.create({
          name,
          folderId,
          query: query ?? '',
          ...(this.data.columns?.length ? { columns: this.data.columns } : {}),
          ...(this.data.sort?.length ? { sort: this.data.sort } : {}),
          ...(this.data.include
            ? {
                includeFamily: this.data.include.family,
                includeDuplicates: this.data.include.duplicates,
                includeThread: this.data.include.thread,
              }
            : {}),
        });
      case 'copy':
        return this.api.clone(saved!.savedSearchId, name, folderId);
      default: {
        const draft: SavedSearchDraft = {
          name: this.mode === 'move' ? saved!.name : name,
          folderId: this.mode === 'rename' ? saved!.folderId : folderId,
          query: query ?? saved!.query,
          columns: saved!.columns,
          sort: saved!.sort,
          includeFamily: saved!.includeFamily,
          includeDuplicates: saved!.includeDuplicates,
          includeThread: saved!.includeThread,
        };
        return this.api.update(saved!.savedSearchId, saved!.version, draft);
      }
    }
  }
}
