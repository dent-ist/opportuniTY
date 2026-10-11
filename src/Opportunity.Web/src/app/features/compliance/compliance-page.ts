import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  BadgeTone,
  Button,
  EmptyState,
  ErrorState,
  Icon,
  IconName,
  LoadingState,
  TextField,
} from '../../ui';
import {
  AccessReview,
  AccessReviewEntry,
  ComplianceApi,
  CompliancePeriod,
  HttpComplianceApi,
  WallProof,
  dayBoundary,
} from './compliance-api';

const RESULTS: Record<string, { label: string; tone: BadgeTone; icon: IconName }> = {
  Denied: { label: 'Denied', tone: 'success', icon: 'lock' },
  BreakGlass: { label: 'Break-glass', tone: 'warning', icon: 'warning' },
  Allowed: { label: 'Allowed', tone: 'danger', icon: 'error' },
  Failed: { label: 'Failed', tone: 'neutral', icon: 'info' },
};

const DAY_MS = 86_400_000;

function localDay(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/**
 * Admin › Compliance (E14-T06): evidence for counsel and auditors. The ethical-wall proof lists every audited attempt by
 * a walled user on walled content in a period and says whether the walls held; the access review lists who can use the
 * workspace with which roles and walls, and when they last did; the audit export downloads a copy of the audit trail
 * that verifies offline. Reports are generated on request because each generation is audited. Needs
 * `Compliance.Report`; the audit export also needs `Audit.Export`.
 */
@Component({
  selector: 'opp-compliance-page',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState, TextField],
  templateUrl: './compliance-page.html',
  styleUrl: './compliance-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: ComplianceApi, useClass: HttpComplianceApi }],
  host: { class: 'cmp-page' },
})
export class CompliancePage {
  private readonly api = inject(ComplianceApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);

  protected readonly canExportAudit = this.context.can(PERMISSIONS.auditExport);

  // Ethical-wall proof: the last 30 days by default.
  protected readonly proofFrom = signal(localDay(new Date(Date.now() - 30 * DAY_MS)));
  protected readonly proofTo = signal(localDay(new Date()));
  protected readonly proofPeriod = computed(() => this.period(this.proofFrom(), this.proofTo()));
  protected readonly proofPeriodError = computed(() =>
    this.periodError(this.proofFrom(), this.proofTo(), true),
  );
  protected readonly proof = signal<WallProof | null>(null);
  protected readonly proofLoading = signal(false);
  protected readonly proofError = signal<ApiError | null>(null);
  protected readonly proofCsvUrl = computed(() =>
    this.api.wallProofExportUrl(this.context.workspaceId, this.proofPeriod()),
  );
  protected readonly wallNames = computed(
    () => new Map((this.proof()?.walls ?? []).map((w) => [w.wallId, w.name])),
  );

  // Access review.
  protected readonly review = signal<AccessReview | null>(null);
  protected readonly reviewLoading = signal(false);
  protected readonly reviewError = signal<ApiError | null>(null);
  protected readonly reviewCsvUrl = this.api.accessReviewExportUrl(this.context.workspaceId);

  // Audit export: the whole trail unless a period is given.
  protected readonly exportFrom = signal('');
  protected readonly exportTo = signal('');
  protected readonly exportPeriodError = computed(() =>
    this.periodError(this.exportFrom(), this.exportTo(), false),
  );
  protected readonly auditExportUrl = computed(() =>
    this.api.auditExportUrl(
      this.context.workspaceId,
      this.period(this.exportFrom(), this.exportTo()),
    ),
  );

  async generateProof(): Promise<void> {
    if (this.proofPeriodError() || this.proofLoading()) return;
    this.proofLoading.set(true);
    this.proofError.set(null);
    try {
      this.proof.set(await this.api.wallProof(this.context.workspaceId, this.proofPeriod()));
    } catch (e) {
      this.proofError.set(toApiError(e));
    } finally {
      this.proofLoading.set(false);
    }
  }

  async generateReview(): Promise<void> {
    if (this.reviewLoading()) return;
    this.reviewLoading.set(true);
    this.reviewError.set(null);
    try {
      this.review.set(await this.api.accessReview(this.context.workspaceId));
    } catch (e) {
      this.reviewError.set(toApiError(e));
    } finally {
      this.reviewLoading.set(false);
    }
  }

  protected result(value: string): { label: string; tone: BadgeTone; icon: IconName } {
    return RESULTS[value] ?? { label: value, tone: 'neutral', icon: 'info' };
  }

  protected wallList(ids: readonly string[]): string {
    const names = this.wallNames();
    return ids.map((id) => names.get(id) ?? id).join(', ');
  }

  protected principal(entry: AccessReviewEntry): string {
    return entry.principal === 'Group'
      ? (entry.group ?? '')
      : entry.displayName || entry.email || entry.userId || '';
  }

  protected roles(entry: AccessReviewEntry): string {
    return entry.roles
      .map((r) => r.displayName + (r.viaGroup ? ` (via ${r.viaGroup})` : ''))
      .join(', ');
  }

  protected walls(entry: AccessReviewEntry): string {
    return entry.walls.map((w) => w.name + (w.viaGroup ? ` (via ${w.viaGroup})` : '')).join(', ');
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

  protected short(id: string): string {
    return id.slice(0, 8);
  }

  private period(from: string, to: string): CompliancePeriod {
    return { from: dayBoundary(from), to: dayBoundary(to, true) };
  }

  private periodError(from: string, to: string, required: boolean): string | null {
    if (required && (!from || !to)) return 'Choose both dates.';
    if (from && to && from > to) return 'The start date must be on or before the end date.';
    return null;
  }
}
