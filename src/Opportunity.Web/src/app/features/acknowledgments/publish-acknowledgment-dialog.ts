import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { UserFacingError, describeError, toApiError } from '../../core/api/problem-details';
import { Button, DialogLayout, Icon, TextField } from '../../ui';
import { AcknowledgmentApi, AcknowledgmentVersion } from './acknowledgment-api';

export const MAX_TITLE = 200;
export const MAX_TEXT = 65536;

export interface PublishAcknowledgmentData {
  readonly workspaceId: string;
  /** The version the new text replaces (0 when none was published). */
  readonly currentVersion: number;
  /** The current text, offered as the starting point. */
  readonly current: AcknowledgmentVersion | null;
}

/**
 * Publish a new version of the workspace's acknowledgment text (E20-T03). The title and text are required; the dialog
 * starts from the current version. Publishing sends the version it replaces as `If-Match`, so two administrators cannot
 * overwrite each other. Closes with the published version.
 *
 * Keyboard: Tab cycles inside; Enter in the title submits; Escape cancels.
 */
@Component({
  selector: 'opp-publish-acknowledgment-dialog',
  imports: [Button, DialogLayout, Icon, TextField],
  templateUrl: './publish-acknowledgment-dialog.html',
  styleUrl: './publish-acknowledgment-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PublishAcknowledgmentDialog {
  protected readonly data = inject<PublishAcknowledgmentData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<AcknowledgmentVersion>>(DialogRef);
  private readonly api = inject(AcknowledgmentApi);

  protected readonly next = this.data.currentVersion + 1;
  protected readonly maxText = MAX_TEXT;
  protected readonly title = signal(this.data.current?.title ?? '');
  protected readonly text = signal(this.data.current?.text ?? '');
  private readonly submitted = signal(false);
  private readonly serverErrors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  protected readonly failure = signal<UserFacingError | null>(null);

  protected readonly titleError = computed(() => {
    const server = this.serverErrors()['title'];
    if (server) return server;
    if (!this.submitted()) return '';
    const value = this.title().trim();
    if (!value) return 'Enter a title.';
    return value.length > MAX_TITLE ? `Use at most ${MAX_TITLE} characters.` : '';
  });
  protected readonly textError = computed(() => {
    const server = this.serverErrors()['text'];
    if (server) return server;
    if (!this.submitted()) return '';
    const value = this.text().trim();
    if (!value) return 'Enter the text members must accept.';
    return value.length > MAX_TEXT ? `Use at most ${MAX_TEXT} characters.` : '';
  });

  protected onText(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
    this.serverErrors.update(({ text: _, ...rest }) => rest);
  }

  protected onTitle(value: string): void {
    this.title.set(value);
    this.serverErrors.update(({ title: _, ...rest }) => rest);
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.saving()) return;
    this.submitted.set(true);
    this.failure.set(null);
    if (this.titleError() || this.textError()) return;
    this.saving.set(true);
    try {
      const published = await this.api.publish(this.data.workspaceId, this.data.currentVersion, {
        title: this.title().trim(),
        text: this.text(),
      });
      this.ref.close(published);
    } catch (e) {
      const error = toApiError(e);
      const errors = error.problem.errors;
      if (error.status === 400 && errors) {
        this.serverErrors.set(
          Object.fromEntries(Object.entries(errors).map(([k, v]) => [k, v.join(' ')])),
        );
      } else if (error.status === 412) {
        this.failure.set({
          title: 'Someone published a version meanwhile',
          detail: 'Close this dialog to see the current version, then publish again if needed.',
          retryable: false,
        });
      } else {
        this.failure.set(describeError(error));
      }
    } finally {
      this.saving.set(false);
    }
  }
}
