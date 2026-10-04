import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { Button, DialogLayout, Icon, TextField } from '../../ui';
import type { DeletionAvailability } from './workspace-deletion';

export interface DeleteWorkspaceData {
  readonly workspaceName: string;
  readonly availability: Exclude<DeletionAvailability, { kind: 'unavailable' }>;
}

/**
 * Delete workspace (E04-T07, baseline §15). Under a preservation lock it only explains why deletion is blocked.
 * Otherwise it lists what will be removed and what is kept, and the request is enabled only once the user has typed
 * the workspace name exactly. Closes with `true` when the user requests the deletion; the caller submits it.
 *
 * Keyboard: Tab cycles inside; Enter submits once the name matches; Escape cancels.
 */
@Component({
  selector: 'opp-delete-workspace-dialog',
  imports: [Button, DialogLayout, Icon, TextField],
  templateUrl: './delete-workspace-dialog.html',
  styleUrl: './workspace-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeleteWorkspaceDialog {
  protected readonly data = inject<DeleteWorkspaceData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly prefs = inject(UiPreferences);

  protected readonly typed = signal('');
  protected readonly matches = computed(() => this.typed() === this.data.workspaceName);
  protected readonly lock =
    this.data.availability.kind === 'locked' ? this.data.availability.lock : null;
  protected readonly summary =
    this.data.availability.kind === 'allowed' ? this.data.availability.summary : null;
  protected readonly title = this.lock ? 'Deletion blocked' : `Delete ${this.data.workspaceName}`;

  protected date(value: string): string {
    const d = new Date(value);
    return Number.isNaN(d.getTime())
      ? value
      : new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium' }).format(d);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    if (this.summary && this.matches()) this.ref.close(true);
  }
}
