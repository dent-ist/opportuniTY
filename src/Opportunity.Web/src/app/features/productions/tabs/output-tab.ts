import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import type {
  ExportFileResource,
  ProductionVerificationResource,
  ProductionVolumeResource,
} from '../../../core/api/generated/models';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { SessionService } from '../../../core/session/session';
import {
  Announcer,
  Badge,
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  Progress,
  StatusPill,
  TextField,
  ToastService,
} from '../../../ui';
import { csvFileName } from '../../exports/csv';
import { EXPORT_STATUS, formatBytes } from '../../exports/export-model';
import { newIdempotencyKey } from '../../exports/frozen-set/frozen-set-api';
import { ProductionApi } from '../production-api';
import { ProductionStore } from '../production-store';
import { downloadCrossReference } from './cross-reference';
import { DisplayFormat } from '../../../core/i18n';

const POLL_MS = 2000;

/**
 * Output tab (familiarity guide §5.6): for a finalized production, the manifest with its SHA-256, the QC report, the
 * Bates cross-reference, and the volume runs — "Re-run with same specification" writes the volume again from the
 * frozen definition, and every completed run shows whether its manifest is byte-identical to the first one. "Verify
 * against manifest" re-checks the specification, the frozen set and a re-run of the Bates allocation. Volume files
 * download through the protected-content gateway, for the person who ran the volume. A finalized production can be
 * voided here (its numbers are never issued again).
 */
@Component({
  selector: 'opp-production-output-tab',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, Progress, StatusPill, TextField],
  templateUrl: './output-tab.html',
  styleUrl: '../../exports/delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OutputTab {
  protected readonly store = inject(ProductionStore);
  private readonly api = inject(ProductionApi);
  private readonly prefs = inject(UiPreferences);
  private readonly fmt = inject(DisplayFormat);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly context = inject(WorkspaceContext);
  private readonly me = inject(SessionService).principal;
  protected readonly canFinalize = this.context.can(PERMISSIONS.productionFinalize);
  private timer: ReturnType<typeof setTimeout> | null = null;
  private loadedFor: string | null = null;

  protected readonly status = EXPORT_STATUS;
  protected readonly volumes = signal<readonly ProductionVolumeResource[] | null>(null);
  protected readonly volumesError = signal<ApiError | null>(null);
  protected readonly starting = signal(false);
  protected readonly verifying = signal(false);
  protected readonly verification = signal<ProductionVerificationResource | null>(null);
  protected readonly verifyError = signal<ApiError | null>(null);
  protected readonly exporting = signal(false);
  protected readonly files = signal<Record<string, readonly ExportFileResource[] | 'error'>>({});
  protected readonly voidReason = signal('');
  protected readonly voiding = signal(false);

  /** The first completed run's manifest: every later run must match it byte for byte (Q-08). */
  protected readonly firstManifest = computed(() => {
    const done = (this.volumes() ?? []).filter((v) => v.status === 'completed' && v.report);
    return done.length ? done[done.length - 1].report!.manifestSha256 : null;
  });
  protected readonly completedRuns = computed(
    () => (this.volumes() ?? []).filter((v) => v.status === 'completed').length,
  );
  protected readonly anyRunning = computed(() =>
    (this.volumes() ?? []).some((v) => v.status === 'running'),
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
    effect(() => {
      const p = this.store.production();
      const key = p && p.status !== 'draft' && p.status !== 'discarded' ? p.productionId : null;
      if (key && key !== this.loadedFor) {
        this.loadedFor = key;
        void this.loadVolumes();
      }
    });
  }

  protected async loadVolumes(): Promise<void> {
    const p = this.store.production();
    if (!p) return;
    this.stop();
    try {
      const before = this.anyRunning();
      const page = await this.api.volumes(p.productionId);
      this.volumes.set(page.items);
      this.volumesError.set(null);
      if (this.anyRunning()) this.timer = setTimeout(() => void this.loadVolumes(), POLL_MS);
      else if (before) this.announcer.announce('The volume run finished.');
    } catch (e) {
      this.volumesError.set(toApiError(e));
    }
  }

  private stop(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }

  protected async startVolume(): Promise<void> {
    const p = this.store.production();
    if (!p || this.starting()) return;
    const rerun = this.completedRuns() > 0;
    if (rerun) {
      const confirmed = await this.dialogs.confirm({
        title: 'Re-run with same specification',
        message:
          'The volume is written again from the frozen specification, documents and Bates numbers. Its ' +
          'manifest is compared with the first run: the files must be byte-identical.',
        confirmLabel: 'Re-run',
      });
      if (!confirmed) return;
    }
    this.starting.set(true);
    try {
      await this.api.startVolume(p.productionId, newIdempotencyKey());
      this.toasts.show(rerun ? 'Volume re-run started.' : 'Volume started.', { tone: 'success' });
      await this.loadVolumes();
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The volume could not be started.', {
        tone: 'error',
      });
    } finally {
      this.starting.set(false);
    }
  }

  protected async verify(): Promise<void> {
    const p = this.store.production();
    if (!p || this.verifying()) return;
    this.verifying.set(true);
    this.verifyError.set(null);
    try {
      const result = await this.api.verify(p.productionId);
      this.verification.set(result);
      this.announcer.announce(
        result.consistent
          ? 'The production matches its manifest.'
          : `${result.differences.length} differences from the manifest.`,
      );
    } catch (e) {
      this.verifyError.set(toApiError(e));
    } finally {
      this.verifying.set(false);
    }
  }

  protected async toggleFiles(volume: ProductionVolumeResource): Promise<void> {
    const p = this.store.production();
    if (!p) return;
    if (this.files()[volume.volumeId]) {
      this.files.update((m) => {
        const copy = { ...m };
        delete copy[volume.volumeId];
        return copy;
      });
      return;
    }
    try {
      const page = await this.api.volumeFiles(p.productionId, volume.volumeId);
      this.files.update((m) => ({ ...m, [volume.volumeId]: page.items }));
    } catch {
      this.files.update((m) => ({ ...m, [volume.volumeId]: 'error' }));
    }
  }

  protected fileList(volumeId: string): readonly ExportFileResource[] | null {
    const v = this.files()[volumeId];
    return Array.isArray(v) ? v : null;
  }

  protected filesFailed(volumeId: string): boolean {
    return this.files()[volumeId] === 'error';
  }

  /** Only the person who ran a volume receives its files (their access was re-checked when it was written). */
  protected mine(volume: ProductionVolumeResource): boolean {
    return volume.createdBy === this.me()?.userId;
  }

  /** The manifest as recorded at finalization, read again from the API when asked for. */
  protected async downloadManifest(): Promise<void> {
    const p = this.store.production();
    if (!p) return;
    try {
      const { production } = await this.api.get(p.productionId);
      if (!production.manifest) return;
      const blob = new Blob([production.manifest], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = csvFileName([
        this.context.workspace.name,
        production.name,
        `v${production.version}`,
        'manifest',
      ]).replace(/\.csv$/, '.json');
      a.hidden = true;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 0);
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The manifest could not be read.', {
        tone: 'error',
      });
    }
  }

  protected async crossReference(): Promise<void> {
    const p = this.store.production();
    if (!p || this.exporting()) return;
    this.exporting.set(true);
    try {
      const n = await downloadCrossReference(this.api, p, this.context.workspace.name);
      this.toasts.show(`Bates cross-reference of ${this.num(n)} documents downloaded.`, {
        tone: 'success',
      });
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The cross-reference could not be built.', {
        tone: 'error',
      });
    } finally {
      this.exporting.set(false);
    }
  }

  protected async voidProduction(): Promise<void> {
    const current = this.store.loaded();
    const reason = this.voidReason().trim();
    if (!current || !reason || this.voiding()) return;
    const confirmed = await this.dialogs.confirm({
      title: 'Void production',
      message:
        `${current.production.name} stays on record as voided. Its Bates numbers ` +
        `${current.production.bates.first} – ${current.production.bates.last} are never issued again.`,
      confirmLabel: 'Void production',
      tone: 'danger',
      typedConfirmation: current.production.specification.bates.prefix,
    });
    if (!confirmed) return;
    this.voiding.set(true);
    try {
      this.store.set(await this.api.voidProduction(current, reason));
      this.toasts.show('Production voided.', { tone: 'success' });
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The production could not be voided.', {
        tone: 'error',
      });
    } finally {
      this.voiding.set(false);
    }
  }

  protected reportUrl(format: 'csv' | 'pdf'): string {
    return this.api.qcReportUrl(this.store.production()?.productionId ?? '', format);
  }

  protected packageUrl(v: ProductionVolumeResource): string {
    return this.api.volumePackageUrl(v.productionId, v.volumeId);
  }

  protected verificationUrl(v: ProductionVolumeResource): string {
    return this.api.verificationReportUrl(v.productionId, v.volumeId);
  }

  protected fileUrl(v: ProductionVolumeResource, f: ExportFileResource): string {
    return this.api.volumeFileUrl(v.productionId, v.volumeId, f.fileId);
  }

  protected progress(v: ProductionVolumeResource): { value: number; max: number } | null {
    const total = Number(v.job.committed.chunksTotal);
    return total > 0 ? { value: Number(v.job.committed.chunksCommitted), max: total } : null;
  }

  protected num(n: number | string | null | undefined): string {
    return n === null || n === undefined ? '—' : this.fmt.number(Number(n));
  }

  protected bytes(n: number | string): string {
    return formatBytes(Number(n), this.prefs.locale());
  }

  protected date(value: unknown): string {
    return value ? this.fmt.dateTime(String(value)) : '';
  }
}
