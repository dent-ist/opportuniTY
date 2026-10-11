import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import type {
  ProductionQcCheckResource,
  ProductionQcCheckResultResource,
  ProductionQcExceptionPageResource,
  ProductionQcReportResource,
} from '../../../core/api/generated/models';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import {
  Announcer,
  Badge,
  Button,
  Checkbox,
  DialogService,
  ErrorState,
  Icon,
  Progress,
  TextField,
  ToastService,
} from '../../../ui';
import { newIdempotencyKey } from '../../exports/frozen-set/frozen-set-api';
import { queryDocumentsParams } from '../../documents/relationships/relationship-link';
import { ProductionApi } from '../production-api';
import {
  QC_STATUS,
  batesPreview,
  controlNumberQuery,
  formFromSpecification,
  hardFailures,
  overridable,
  warnings,
} from '../production-model';
import { ProductionStore } from '../production-store';
import { DisplayFormat } from '../../../core/i18n';

interface ExceptionView {
  readonly loading: boolean;
  readonly page: ProductionQcExceptionPageResource | null;
  readonly error: ApiError | null;
}

/**
 * QC tab (E12-T03, E12-T07): assign Bates numbers (with the range preview before the job runs), run the QC gate as the
 * validation summary, see each check with its exceptions ("Open in Documents") and download the QC report, then
 * finalize — failed checks block; the overridable ones (render failures, Redact without redactions, privilege
 * conflicts) need a reason, and warnings need acknowledging. Overrides are printed in the QC report and audited.
 */
@Component({
  selector: 'opp-production-qc-tab',
  imports: [Badge, Button, Checkbox, ErrorState, Icon, Progress, RouterLink, TextField],
  templateUrl: './qc-tab.html',
  styleUrl: '../../exports/delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QcTab {
  protected readonly store = inject(ProductionStore);
  private readonly api = inject(ProductionApi);
  private readonly fmt = inject(DisplayFormat);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  protected readonly canFinalize = inject(WorkspaceContext).can(PERMISSIONS.productionFinalize);

  protected readonly qcStatus = QC_STATUS;
  protected readonly allocating = signal(false);
  protected readonly running = signal(false);
  protected readonly finalizing = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  /** A finalization refused by the gate: its message (the report replaces the latest run). */
  protected readonly refused = signal<string | null>(null);
  protected readonly exceptions = signal<Partial<Record<ProductionQcCheckResource, ExceptionView>>>(
    {},
  );
  protected readonly overrides = signal<Partial<Record<ProductionQcCheckResource, string>>>({});
  protected readonly overriding = signal<Partial<Record<ProductionQcCheckResource, boolean>>>({});
  protected readonly acknowledge = signal(false);

  protected readonly preview = computed(() => {
    const p = this.store.production();
    if (!p) return null;
    const form = formFromSpecification(p.name, p.specification);
    const documents = this.store.frozen()?.documentCount ?? p.bates.documents;
    return batesPreview(
      form,
      documents === null || documents === undefined ? null : Number(documents),
    );
  });
  protected readonly batesProgress = computed(() => {
    const c = this.store.production()?.batesJob?.committed;
    const total = c ? Number(c.chunksTotal) : 0;
    return c && total > 0 ? { value: Number(c.chunksCommitted), max: total } : null;
  });
  protected readonly checks = computed(() => this.store.qc()?.checks ?? []);
  protected readonly toOverride = computed(() => overridable(this.checks()));
  protected readonly blocking = computed(() => hardFailures(this.checks()));
  protected readonly toAcknowledge = computed(() => warnings(this.checks()));
  protected readonly canRunQc = computed(() => this.store.isDraft() && this.store.allocated());

  protected async allocate(): Promise<void> {
    const p = this.store.production();
    if (!p || this.allocating()) return;
    this.allocating.set(true);
    this.error.set(null);
    try {
      await this.api.allocate(p.productionId, newIdempotencyKey());
      this.announcer.announce('Assigning Bates numbers.');
      await this.store.refresh();
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.allocating.set(false);
    }
  }

  protected async runQc(): Promise<void> {
    const p = this.store.production();
    if (!p || this.running()) return;
    this.running.set(true);
    this.error.set(null);
    this.refused.set(null);
    try {
      const report = await this.api.runQc(p.productionId);
      this.store.qc.set(report);
      this.exceptions.set({});
      this.announcer.announce(
        report.outcome === 'passed'
          ? 'QC checks passed.'
          : `QC checks found ${this.failedCount(report)} failed checks.`,
      );
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.running.set(false);
    }
  }

  protected async toggleExceptions(check: ProductionQcCheckResultResource): Promise<void> {
    const p = this.store.production();
    if (!p) return;
    const current = this.exceptions()[check.check];
    if (current && !current.loading) {
      this.exceptions.update((m) => {
        const copy = { ...m };
        delete copy[check.check];
        return copy;
      });
      return;
    }
    this.setExceptions(check.check, { loading: true, page: null, error: null });
    try {
      const page = await this.api.qcExceptions(p.productionId, check.check);
      this.setExceptions(check.check, { loading: false, page, error: null });
    } catch (e) {
      this.setExceptions(check.check, { loading: false, page: null, error: toApiError(e) });
    }
  }

  private setExceptions(check: ProductionQcCheckResource, view: ExceptionView): void {
    this.exceptions.update((m) => ({ ...m, [check]: view }));
  }

  protected openParams(check: ProductionQcCheckResultResource) {
    const page = this.exceptions()[check.check]?.page;
    const query = page ? controlNumberQuery(page.items.map((i) => i.controlNumber)) : null;
    return query ? queryDocumentsParams(query, `QC exceptions: ${check.title}`) : null;
  }

  protected setOverride(check: ProductionQcCheckResource, on: boolean): void {
    this.overriding.update((m) => ({ ...m, [check]: on }));
  }

  protected setReason(check: ProductionQcCheckResource, reason: string): void {
    this.overrides.update((m) => ({ ...m, [check]: reason }));
  }

  protected async finalize(): Promise<void> {
    const current = this.store.loaded();
    if (!current || this.finalizing()) return;
    const qcOverrides = this.toOverride()
      .filter((c) => this.overriding()[c.check])
      .map((c) => ({ check: c.check, reason: (this.overrides()[c.check] ?? '').trim() }));
    if (qcOverrides.some((o) => !o.reason)) {
      this.refused.set('Give a reason for every check you override.');
      return;
    }
    const confirmed = await this.dialogs.confirm({
      title: 'Finalize production',
      message:
        `Finalizing freezes the specification, the ${this.num(current.production.bates.documents)} documents and ` +
        `the Bates numbers ${current.production.bates.first} – ${current.production.bates.last}. The QC checks run ` +
        'again first. A finalized production never changes; a change is a new version.',
      confirmLabel: 'Finalize',
    });
    if (!confirmed) return;
    this.finalizing.set(true);
    this.refused.set(null);
    this.error.set(null);
    try {
      const finalized = await this.api.finalize(current, {
        acknowledgeWarnings: this.acknowledge(),
        qcOverrides,
      });
      this.store.set(finalized);
      await this.store.loadQc();
      this.announcer.announce('Production finalized.');
      this.toasts.show(`${finalized.production.name} is finalized.`, { tone: 'success' });
    } catch (e) {
      const error = toApiError(e);
      const qc = error.problem['qc'] as ProductionQcReportResource | undefined;
      if (error.status === 409 && qc) {
        this.store.qc.set(qc);
        this.exceptions.set({});
        this.refused.set(
          [error.problem.title, error.problem.detail].filter(Boolean).join(': ') ||
            'The QC checks refused finalization.',
        );
      } else if (error.status === 412) {
        this.refused.set('Someone changed this production since you opened it. It was reloaded.');
        await this.store.refresh();
      } else {
        this.error.set(error);
      }
    } finally {
      this.finalizing.set(false);
    }
  }

  protected reportUrl(format: 'csv' | 'pdf'): string {
    return this.api.qcReportUrl(this.store.production()?.productionId ?? '', format);
  }

  private failedCount(report: ProductionQcReportResource): number {
    return report.checks.filter((c) => c.status === 'failed').length;
  }

  protected n(value: number | string | null | undefined): number {
    return Number(value ?? 0);
  }

  protected num(n: number | string | null | undefined): string {
    return n === null || n === undefined ? '—' : this.fmt.number(Number(n));
  }

  protected date(value: unknown): string {
    return value ? this.fmt.dateTime(String(value)) : '';
  }
}
