import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toApiError } from '../../core/api/problem-details';
import { Announcer, Button, DialogLayout, Icon, Select, TextField } from '../../ui';
import { SavedSearchApi, SavedSearchFolder } from './saved-search-api';
import { folderAndDescendants, folderOptions, savedSearchErrorText } from './saved-search-model';

export interface FolderDialogData {
  readonly folders: readonly SavedSearchFolder[];
  /** The folder to rename or move; absent for a new folder. */
  readonly folder?: SavedSearchFolder;
  /** New folder: the parent to suggest. */
  readonly parentFolderId?: string | null;
}

/** New folder, or rename / move a folder (a folder never moves into itself or below itself). */
@Component({
  selector: 'opp-folder-dialog',
  imports: [Button, DialogLayout, Icon, Select, TextField],
  template: `<form (submit)="save($event)" novalidate>
    <opp-dialog-layout [title]="data.folder ? 'Edit folder' : 'New folder'">
      <div class="ssd">
        <opp-text-field
          #nameField
          label="Folder name"
          [required]="true"
          [(value)]="name"
          [error]="nameError()"
        />
        <opp-select label="Inside" [options]="parents" [(value)]="parentId" />
        @if (error()) {
          <p class="ssd__error" role="alert">
            <opp-icon name="error" />
            {{ error() }}
          </p>
        }
      </div>
      <ng-container dialogActions>
        <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
        <button type="submit" oppButton="primary" [busy]="saving()">
          {{ data.folder ? 'Save folder' : 'Create folder' }}
        </button>
      </ng-container>
    </opp-dialog-layout>
  </form>`,
  styleUrl: './saved-search-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FolderDialog {
  protected readonly data = inject<FolderDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<SavedSearchFolder>>(DialogRef);
  private readonly api = inject(SavedSearchApi);
  private readonly announcer = inject(Announcer);
  private readonly nameField = viewChild.required<TextField>('nameField');

  protected readonly parents = folderOptions(
    this.data.folders,
    this.data.folder
      ? folderAndDescendants(this.data.folder.folderId, this.data.folders)
      : new Set(),
  ).map((o) => (o.value === '' ? { ...o, label: 'Top level' } : o));
  protected readonly name = signal(this.data.folder?.name ?? '');
  protected readonly parentId = signal(
    (this.data.folder ? this.data.folder.parentFolderId : this.data.parentFolderId) ?? '',
  );
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly submitted = signal(false);
  protected readonly nameError = computed(() =>
    this.submitted() && !this.name().trim() ? 'Enter a folder name.' : '',
  );

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    this.submitted.set(true);
    if (this.nameError()) {
      this.nameField().focus();
      return;
    }
    if (this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    const name = this.name().trim();
    const parent = this.parentId() || null;
    try {
      const folder = this.data.folder;
      this.ref.close(
        folder
          ? await this.api.updateFolder(folder, name, parent)
          : await this.api.createFolder(name, parent),
      );
    } catch (e) {
      const message = savedSearchErrorText(toApiError(e));
      this.error.set(message);
      this.announcer.announce(message, { politeness: 'assertive' });
    } finally {
      this.saving.set(false);
    }
  }
}
