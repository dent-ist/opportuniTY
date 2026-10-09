import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { SessionService } from '../../core/session/session';
import { INSTALLATION_PERMISSIONS } from '../../core/workspace/sections';
import { Badge, Checkbox, EmptyState, ErrorState, LoadingState } from '../../ui';
import { PROFILE_LABELS, STATUS_LABELS, nextStep } from './deletion-presentation';
import {
  type DeletionView,
  OPEN_DELETION_STATUSES,
  WorkspaceDeletionsApi,
} from './workspace-deletions-api';

/**
 * Workspace deletions (E20-T02), installation level: every deletion for Retention Approvers, one's own requests for
 * anyone else. Each row opens the deletion's page (approve, cancel, progress, certificate). The list stays available
 * after a workspace is fenced and gone, which is where requesters follow their requests.
 */
@Component({
  selector: 'opp-workspace-deletions-page',
  imports: [RouterLink, Badge, Checkbox, EmptyState, ErrorState, LoadingState],
  templateUrl: './workspace-deletions-page.html',
  styleUrls: ['./workspace-deletions.scss', './deletions-list.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceDeletionsPage {
  private readonly api = inject(WorkspaceDeletionsApi);
  private readonly locale = inject(UiPreferences).locale;
  private readonly session = inject(SessionService);

  /** Display only: the API decides what the caller sees and may approve. */
  protected readonly approver = computed(() =>
    this.session.hasInstallationPermission(INSTALLATION_PERMISSIONS.approveDeletion),
  );
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly profileLabels = PROFILE_LABELS;

  protected readonly items = signal<readonly DeletionView[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly openOnly = signal(false);
  protected readonly shown = computed(() =>
    this.openOnly()
      ? this.items().filter((d) => OPEN_DELETION_STATUSES.includes(d.status))
      : this.items(),
  );
  protected readonly waiting = computed(
    () => this.items().filter((d) => d.status === 'requested' && d.canApprove).length,
  );

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.items.set(await this.api.list());
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected next(d: DeletionView): string {
    return nextStep(d, (value) => this.date(value));
  }

  protected date(value: unknown): string {
    if (!value) return '';
    const d = new Date(String(value));
    return Number.isNaN(d.getTime())
      ? String(value)
      : new Intl.DateTimeFormat(this.locale(), { dateStyle: 'medium' }).format(d);
  }
}
