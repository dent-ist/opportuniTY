import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { UserFacingError, describeError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { Button, DialogLayout, Icon, TextField } from '../../ui';
import type {
  DeletionView,
  RetentionProfile,
} from '../workspace-deletions/workspace-deletions-api';
import {
  DELETION_SUMMARIES,
  type DeletionAvailability,
  WorkspaceDeletion,
} from './workspace-deletion';

export const MAX_DELETION_REASON = 2000;
export const MAX_DELETION_REFERENCE = 200;

export interface DeleteWorkspaceData {
  readonly workspaceName: string;
  readonly availability: Extract<DeletionAvailability, { kind: 'locked' | 'allowed' }>;
}

const PROFILES: readonly { value: RetentionProfile; label: string; hint: string }[] = [
  {
    value: 'retainRecords',
    label: 'Keep productions',
    hint: 'Recommended. Productions, their Bates numbers and produced files stay available for later requests.',
  },
  {
    value: 'purgeAll',
    label: 'Remove everything',
    hint: 'Also removes productions and destroys the encryption keys. Only the audit trail and the certificate remain.',
  },
];

/**
 * Delete workspace (E20-T02, baseline §15). Under a legal hold it only explains why deletion is blocked. Otherwise the
 * requester picks what to keep (retention profile, Q-23 default: keep productions), gives a reason and an optional
 * order reference, and types the workspace name; the request is sent from here and nothing is removed until a second
 * person approves it and the waiting period has passed. Closes with the recorded request.
 *
 * Keyboard: Tab cycles inside; arrow keys move between the two choices; Enter submits; Escape cancels.
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
  protected readonly ref = inject<DialogRef<DeletionView>>(DialogRef);
  private readonly deletion = inject(WorkspaceDeletion, { optional: true });
  private readonly prefs = inject(UiPreferences);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly profiles = PROFILES;
  protected readonly maxReason = MAX_DELETION_REASON;
  protected readonly lock =
    this.data.availability.kind === 'locked' ? this.data.availability.lock : null;
  protected readonly title = this.lock ? 'Deletion blocked' : `Delete ${this.data.workspaceName}`;

  protected readonly profile = signal<RetentionProfile>('retainRecords');
  protected readonly summary = computed(() => DELETION_SUMMARIES[this.profile()]);
  protected readonly reason = signal('');
  protected readonly reference = signal('');
  protected readonly typed = signal('');
  protected readonly matches = computed(() => this.typed() === this.data.workspaceName);
  private readonly submitted = signal(false);
  private readonly serverErrors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  protected readonly failure = signal<UserFacingError | null>(null);

  protected readonly reasonError = computed(() => {
    const server = this.serverErrors()['reason'];
    if (server) return server;
    if (!this.submitted()) return '';
    const text = this.reason().trim();
    if (!text) return 'Enter a reason.';
    return text.length > MAX_DELETION_REASON
      ? `Use at most ${MAX_DELETION_REASON} characters.`
      : '';
  });
  protected readonly referenceError = computed(() => {
    const server = this.serverErrors()['externalReference'];
    if (server) return server;
    return this.submitted() && this.reference().trim().length > MAX_DELETION_REFERENCE
      ? `Use at most ${MAX_DELETION_REFERENCE} characters.`
      : '';
  });
  protected readonly nameError = computed(
    () =>
      this.serverErrors()['confirmName'] ??
      (this.submitted() && !this.matches() ? 'Type the workspace name exactly.' : ''),
  );

  protected date(value: string): string {
    const d = new Date(value);
    return Number.isNaN(d.getTime())
      ? value
      : new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium' }).format(d);
  }

  protected onReason(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
    this.serverErrors.update(({ reason: _, ...rest }) => rest);
  }

  protected setProfile(value: RetentionProfile): void {
    this.profile.set(value);
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.lock || this.saving() || !this.deletion) return;
    this.submitted.set(true);
    this.failure.set(null);
    if (this.reasonError() || this.referenceError() || this.nameError()) {
      this.focusFirstInvalid();
      return;
    }
    this.saving.set(true);
    try {
      const saved = await this.deletion.request({
        retentionProfile: this.profile(),
        reason: this.reason().trim(),
        externalReference: this.reference().trim() || null,
        confirmName: this.typed(),
      });
      this.ref.close(saved);
    } catch (e) {
      const error = toApiError(e);
      if (error.code === 'validation') {
        const errors = (error.problem['errors'] ?? {}) as Record<string, string[]>;
        const mapped: Record<string, string> = {};
        for (const [key, messages] of Object.entries(errors))
          mapped[key] = messages[0] ?? 'Check this value.';
        if (Object.keys(mapped).length > 0) {
          this.serverErrors.set(mapped);
          this.focusFirstInvalid();
          return;
        }
      }
      this.failure.set(
        error.status === 409
          ? {
              title: 'Already requested',
              detail:
                'This workspace already has a deletion request or run in progress. Close this dialog to see it.',
              retryable: false,
              reference: error.traceId,
            }
          : describeError(error),
      );
    } finally {
      this.saving.set(false);
    }
  }

  private focusFirstInvalid(): void {
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus(),
      { injector: this.injector },
    );
  }
}
