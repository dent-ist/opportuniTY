import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ApiError,
  UserFacingError,
  describeError,
  toApiError,
} from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import {
  Badge,
  Button,
  DialogService,
  ErrorState,
  Icon,
  LoadingState,
  ToastService,
} from '../../ui';
import { PROFILE_LABELS, STATUS_LABELS, stepSummary } from './deletion-presentation';
import {
  DELETION_STEPS,
  type DeletionStepName,
  type DeletionView,
  OPEN_DELETION_STATUSES,
  WorkspaceDeletionsApi,
} from './workspace-deletions-api';

/** How often an open deletion is read again (approval, start and progress happen elsewhere). */
export const DELETION_REFRESH_MS = 5000;

const STEP_UP_REQUIRED = 'step-up-required';

type StepState = 'done' | 'current' | 'halted' | 'waiting';

interface StepRow {
  readonly step: DeletionStepName;
  readonly number: number;
  readonly label: string;
  readonly state: StepState;
  readonly summary: string;
}

/**
 * One workspace deletion (E20-T02): the request (what is kept, reason, order reference, who asked), its approval, its
 * progress through the nine steps of the run, and the destruction certificate. A Retention Approver who did not request
 * it approves it here (with MFA and an optional note on the certificate); the requester or an approver may cancel it
 * until the run starts. The page reads the deletion again every few seconds while it is open.
 */
@Component({
  selector: 'opp-workspace-deletion-page',
  imports: [RouterLink, Badge, Button, ErrorState, Icon, LoadingState],
  templateUrl: './workspace-deletion-page.html',
  styleUrls: ['./workspace-deletions.scss', './deletion-detail.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceDeletionPage {
  /** Route parameter `:deletionId` (component input binding). */
  readonly deletionId = input.required<string>();

  private readonly api = inject(WorkspaceDeletionsApi);
  private readonly prefs = inject(UiPreferences);
  private readonly session = inject(SessionService);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);

  protected readonly statusLabels = STATUS_LABELS;
  protected readonly profileLabels = PROFILE_LABELS;
  protected readonly maxNote = 2000;

  protected readonly deletion = signal<DeletionView | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly acting = signal(false);
  protected readonly failure = signal<UserFacingError | null>(null);
  protected readonly stepUpUrl = signal<string | null>(null);
  protected readonly note = signal('');

  protected readonly started = computed(() => !!this.deletion()?.startedAt);
  protected readonly steps = computed<readonly StepRow[]>(() => {
    const d = this.deletion();
    if (!d) return [];
    const recorded = new Map(d.steps.map((s) => [s.step, s]));
    const format = (value: number) => new Intl.NumberFormat(this.prefs.locale()).format(value);
    return DELETION_STEPS.map(({ step, label }, i) => {
      const record = recorded.get(step);
      const done = !!record?.finishedAt;
      const state: StepState = done
        ? 'done'
        : d.currentStep === step
          ? d.status === 'halted'
            ? 'halted'
            : 'current'
          : 'waiting';
      return {
        step,
        number: i + 1,
        label,
        state,
        summary: done ? stepSummary(step, record.totals, format) : '',
      };
    });
  });

  constructor() {
    const timer = setInterval(() => {
      const d = this.deletion();
      if (d && OPEN_DELETION_STATUSES.includes(d.status) && !this.acting()) void this.refresh();
    }, DELETION_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
    effect(() => {
      this.deletionId();
      untracked(() => void this.load());
    });
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      this.deletion.set(await this.api.get(this.deletionId()));
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected certificateUrl(d: DeletionView): string {
    return this.api.certificateUrl(d.deletionId);
  }

  protected onNote(event: Event): void {
    this.note.set((event.target as HTMLTextAreaElement).value);
  }

  protected async approve(d: DeletionView): Promise<void> {
    const confirmed = await this.dialogs.confirm({
      title: `Approve deleting ${d.workspaceName}?`,
      message:
        'After the waiting period, the workspace is closed to everyone and its contents are removed. This cannot be undone once it starts. You or the requester can cancel until then.',
      confirmLabel: 'Approve deletion',
      tone: 'danger',
    });
    if (!confirmed) return;
    await this.act(async () => {
      const approved = await this.api.approve(d, this.note().trim() || null);
      this.note.set('');
      this.toasts.show('Deletion approved. It starts after the waiting period.', {
        tone: 'success',
      });
      return approved;
    });
  }

  protected async cancel(d: DeletionView): Promise<void> {
    const confirmed = await this.dialogs.confirm({
      title: 'Cancel this deletion request?',
      message: `${d.workspaceName} stays as it is. Deleting it later needs a new request and a new approval.`,
      confirmLabel: 'Cancel request',
      cancelLabel: 'Keep request',
    });
    if (!confirmed) return;
    await this.act(async () => {
      const cancelled = await this.api.cancel(d);
      this.toasts.show('Deletion request cancelled.', { tone: 'success' });
      return cancelled;
    });
  }

  protected stepUp(): void {
    this.session.stepUp(undefined, this.stepUpUrl());
  }

  protected date(value: unknown, withTime = true): string {
    if (!value) return '';
    const d = new Date(String(value));
    return Number.isNaN(d.getTime())
      ? String(value)
      : new Intl.DateTimeFormat(
          this.prefs.locale(),
          withTime ? { dateStyle: 'medium', timeStyle: 'short' } : { dateStyle: 'medium' },
        ).format(d);
  }

  private async refresh(): Promise<void> {
    try {
      this.deletion.set(await this.api.get(this.deletionId()));
    } catch {
      // Keep showing the last state; the next tick tries again.
    }
  }

  private async act(action: () => Promise<DeletionView>): Promise<void> {
    if (this.acting()) return;
    this.acting.set(true);
    this.failure.set(null);
    this.stepUpUrl.set(null);
    try {
      this.deletion.set(await action());
    } catch (e) {
      const error = toApiError(e);
      if (error.code === STEP_UP_REQUIRED) {
        const url = error.problem['stepUpUrl'];
        this.stepUpUrl.set(typeof url === 'string' ? url : '');
        this.failure.set({
          title: 'Verify your identity',
          detail:
            'Approving a deletion needs a sign-in with multi-factor authentication. Verify, then approve again.',
          retryable: false,
          reference: error.traceId,
        });
      } else {
        this.failure.set(describeError(error));
        if (error.status === 409 || error.status === 412) void this.refresh();
      }
    } finally {
      this.acting.set(false);
    }
  }
}
