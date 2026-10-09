import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiError, describeError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import { Badge, Button, DialogService, ErrorState, LoadingState, ToastService } from '../../ui';
import { type LegalHold, LegalHoldApi } from './legal-hold-api';
import { LegalHoldDialog, type LegalHoldDialogData } from './legal-hold-dialog';

/** What a hold freezes, in the words the settings page and its dialogs use (ADR-014 §2.3). */
export const HOLD_EFFECT =
  'While a legal hold applies, nothing in this workspace can be deleted or purged: documents and their files, coding and redaction history, frozen sets, productions, exports, reports and the audit trail. Review, coding and imports continue as normal.';

/**
 * The Legal hold card of Admin › Workspace Settings (E20-T01). Every member sees whether the workspace is held
 * (`activePreservationLocks` of the workspace). People with `Workspace.ManageHolds` also see each hold with its reason
 * and can place one, release one (with a reason), and approve or cancel a release someone else requested. Emits
 * `changed` after every change so the page can refresh the workspace and the deletion entry point.
 */
@Component({
  selector: 'opp-legal-hold-card',
  imports: [Badge, Button, ErrorState, LoadingState],
  templateUrl: './legal-hold-card.html',
  styleUrl: './workspace-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalHoldCard {
  /** Active holds as the workspace resource reports them (shown to every member). */
  readonly activeCount = input(0);
  /** The caller holds `Workspace.ManageHolds`. */
  readonly canManage = input(false);
  readonly changed = output<void>();

  private readonly api = inject(LegalHoldApi);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly session = inject(SessionService);
  private readonly prefs = inject(UiPreferences);
  private readonly injector = inject(Injector);

  protected readonly effect = HOLD_EFFECT;
  protected readonly holds = signal<readonly LegalHold[] | null>(null);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly busy = signal<string | null>(null);

  protected readonly active = computed(() => {
    const holds = this.holds();
    return holds ? holds.filter((h) => h.status !== 'released') : [];
  });
  protected readonly released = computed(() =>
    (this.holds() ?? []).filter((h) => h.status === 'released'),
  );
  /** The list when it is loaded, otherwise the workspace's count. */
  protected readonly activeTotal = computed(() =>
    this.holds() ? this.active().length : this.activeCount(),
  );

  constructor() {
    effect(() => {
      if (this.canManage()) untracked(() => void this.load());
    });
  }

  protected async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.holds.set((await this.api.list()).items);
    } catch (e) {
      this.loadError.set(toApiError(e));
    }
  }

  protected name(actor: { readonly userId: string; readonly displayName?: string | null }): string {
    return actor.displayName || actor.userId;
  }

  protected isMine(actor: { readonly userId: string } | null | undefined): boolean {
    return !!actor && actor.userId === this.session.principal()?.userId;
  }

  protected date(value: unknown): string {
    if (!value) return '';
    const d = new Date(String(value));
    return Number.isNaN(d.getTime())
      ? String(value)
      : new Intl.DateTimeFormat(this.prefs.locale(), {
          dateStyle: 'medium',
          timeStyle: 'short',
        }).format(d);
  }

  protected async place(): Promise<void> {
    const saved = await this.openDialog({ mode: 'place' });
    if (!saved) return;
    this.toasts.show('Legal hold placed. Nothing in this workspace can be deleted now.', {
      tone: 'success',
    });
    await this.afterChange();
  }

  protected async release(hold: LegalHold): Promise<void> {
    const saved = await this.openDialog({ mode: 'release', hold });
    if (!saved) return;
    this.toasts.show(
      saved.status === 'released'
        ? 'Legal hold released.'
        : 'Release requested. Another person who manages legal holds must approve it.',
      { tone: 'success' },
    );
    await this.afterChange();
  }

  protected async approve(hold: LegalHold): Promise<void> {
    const confirmed = await this.dialogs.confirm({
      title: 'Approve release',
      message: `The hold placed by ${this.name(hold.placedBy)} ends now. If no other hold applies, data in this workspace can be deleted again.`,
      confirmLabel: 'Approve release',
    });
    if (!confirmed) return;
    await this.run(hold, () => this.api.approve(hold), 'Legal hold released.');
  }

  protected async cancel(hold: LegalHold): Promise<void> {
    await this.run(
      hold,
      () => this.api.cancel(hold),
      'Release request cancelled. The hold stays in place.',
    );
  }

  private async run(hold: LegalHold, step: () => Promise<LegalHold>, done: string): Promise<void> {
    if (this.busy()) return;
    this.busy.set(hold.lockId);
    try {
      await step();
      this.toasts.show(done, { tone: 'success' });
      await this.afterChange();
    } catch (e) {
      const error = describeError(toApiError(e));
      this.toasts.show(`${error.title}. ${error.detail}`, { tone: 'error' });
      await this.load();
    } finally {
      this.busy.set(null);
    }
  }

  private async openDialog(data: LegalHoldDialogData): Promise<LegalHold | undefined> {
    const ref = this.dialogs.open<LegalHold, LegalHoldDialogData>(LegalHoldDialog, {
      data,
      width: '36rem',
      autoFocus: 'textarea',
      injector: this.injector,
    });
    return firstValueFrom(ref.closed);
  }

  private async afterChange(): Promise<void> {
    await this.load();
    this.changed.emit();
  }
}
