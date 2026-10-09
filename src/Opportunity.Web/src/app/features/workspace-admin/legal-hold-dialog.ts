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
import { Button, Checkbox, DialogLayout, Icon, TextField } from '../../ui';
import { type LegalHold, LegalHoldApi } from './legal-hold-api';

export const MAX_REASON = 2000;
export const MAX_REFERENCE = 200;

export type LegalHoldDialogData =
  { readonly mode: 'place' } | { readonly mode: 'release'; readonly hold: LegalHold };

/**
 * Place or release a legal hold (E20-T01). Both need a reason. Placing takes effect at once; it can ask that releasing
 * needs a second person (on by default, Q-23). Releasing a hold that needs a second person only requests the release.
 * Closes with the saved hold.
 *
 * Keyboard: Tab cycles inside; Enter in a single-line field submits; Escape cancels.
 */
@Component({
  selector: 'opp-legal-hold-dialog',
  imports: [Button, Checkbox, DialogLayout, Icon, TextField],
  templateUrl: './legal-hold-dialog.html',
  styleUrl: './workspace-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalHoldDialog {
  protected readonly data = inject<LegalHoldDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<LegalHold>>(DialogRef);
  private readonly api = inject(LegalHoldApi);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly hold = this.data.mode === 'release' ? this.data.hold : null;
  protected readonly title = this.hold ? 'Release legal hold' : 'Place legal hold';
  protected readonly maxReason = MAX_REASON;

  protected readonly reason = signal('');
  protected readonly reference = signal('');
  protected readonly secondPerson = signal(true);
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
    return text.length > MAX_REASON ? `Use at most ${MAX_REASON} characters.` : '';
  });
  protected readonly referenceError = computed(() => {
    const server = this.serverErrors()['matterReference'];
    if (server) return server;
    return this.submitted() && this.reference().trim().length > MAX_REFERENCE
      ? `Use at most ${MAX_REFERENCE} characters.`
      : '';
  });

  protected onReason(event: Event): void {
    this.reason.set((event.target as HTMLTextAreaElement).value);
    this.serverErrors.update(({ reason: _, ...rest }) => rest);
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.saving()) return;
    this.submitted.set(true);
    this.failure.set(null);
    if (this.reasonError() || this.referenceError()) {
      this.focusFirstInvalid();
      return;
    }
    this.saving.set(true);
    try {
      const reason = this.reason().trim();
      const saved = this.hold
        ? await this.api.release(this.hold, reason)
        : await this.api.place({
            reason,
            matterReference: this.reference().trim() || null,
            releaseRequiresApproval: this.secondPerson(),
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
      this.failure.set(describeError(error));
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
