import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toApiError } from '../../../core/api/problem-details';
import { Announcer, Button, DialogLayout, Icon, TextField } from '../../../ui';
import { GridView, GridViewApi, GridViewDraft } from './grid-view-api';
import type { ColumnSpec, SortSpec } from './grid-columns';

export interface SaveViewDialogData {
  /** Create a new view from the list's columns and sort, or rename / change who sees an existing one. */
  readonly mode: 'create' | 'edit';
  readonly view?: GridView;
  readonly columns: readonly ColumnSpec[];
  readonly sort: readonly SortSpec[];
  /** The caller holds `View.ManageShared`. */
  readonly canShare: boolean;
}

const MAX_NAME = 200;

/** Name a View and choose who sees it: only you, or everyone in the workspace (needs "manage views"). */
@Component({
  selector: 'opp-save-view-dialog',
  imports: [Button, DialogLayout, Icon, TextField],
  template: `<form (submit)="save($event)" novalidate>
    <opp-dialog-layout [title]="data.mode === 'create' ? 'Save view' : 'Edit view'">
      <div class="svd">
        <opp-text-field
          #nameField
          label="View name"
          [required]="true"
          [(value)]="name"
          [error]="nameError()"
        />
        <fieldset class="svd__visibility">
          <legend>Who sees it</legend>
          <label>
            <input
              type="radio"
              name="visibility"
              value="personal"
              [checked]="visibility() === 'personal'"
              (change)="visibility.set('personal')"
            />
            Only me (personal view)
          </label>
          <label [class.is-disabled]="!data.canShare">
            <input
              type="radio"
              name="visibility"
              value="shared"
              aria-describedby="svd-shared-note"
              [disabled]="!data.canShare"
              [checked]="visibility() === 'shared'"
              (change)="visibility.set('shared')"
            />
            Everyone in this workspace (shared view)
          </label>
          <p id="svd-shared-note" class="svd__note">
            @if (data.canShare) {
              Shared views appear in everyone's View list. Changes to them are recorded in the audit
              trail.
            } @else {
              Sharing a view needs the "Manage shared views" permission.
            }
          </p>
        </fieldset>
        @if (error()) {
          <p class="svd__error" role="alert"><opp-icon name="error" /> {{ error() }}</p>
        }
      </div>
      <ng-container dialogActions>
        <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
        <button type="submit" oppButton="primary" [busy]="saving()">Save view</button>
      </ng-container>
    </opp-dialog-layout>
  </form>`,
  styleUrl: './save-view-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SaveViewDialog {
  protected readonly data = inject<SaveViewDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<GridView>>(DialogRef);
  private readonly api = inject(GridViewApi);
  private readonly announcer = inject(Announcer);
  private readonly nameField = viewChild<TextField>('nameField');

  protected readonly name = signal(this.data.view?.name ?? '');
  protected readonly visibility = signal<'personal' | 'shared'>(
    this.data.view?.visibility ?? 'personal',
  );
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly submitted = signal(false);
  protected readonly nameError = computed(() => {
    if (!this.submitted()) return '';
    const name = this.name().trim();
    if (!name) return 'Enter a name.';
    if (name.length > MAX_NAME) return `Use at most ${MAX_NAME} characters.`;
    return '';
  });

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    this.submitted.set(true);
    if (this.nameError()) {
      this.nameField()?.focus();
      this.announcer.announce(this.nameError(), { politeness: 'assertive' });
      return;
    }
    if (this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    const draft: GridViewDraft = {
      name: this.name().trim(),
      visibility: this.visibility(),
      columns: this.data.columns,
      sort: this.data.sort,
    };
    try {
      const view = this.data.view;
      this.ref.close(
        this.data.mode === 'edit' && view
          ? await this.api.update(view, draft)
          : await this.api.create(draft),
      );
    } catch (e) {
      const error = toApiError(e);
      const message =
        error.status === 409
          ? 'A view with this name already exists. Choose another name.'
          : error.status === 403
            ? 'You cannot change this view: shared views need the "Manage shared views" permission.'
            : error.status === 412
              ? 'Someone changed this view since you opened it. Close and try again.'
              : 'The view could not be saved. Try again.';
      this.error.set(message);
      this.announcer.announce(message, { politeness: 'assertive' });
    } finally {
      this.saving.set(false);
    }
  }
}
