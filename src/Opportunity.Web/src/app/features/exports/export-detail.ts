import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type {
  ExportExclusionResource,
  ExportFileResource,
  ExportResource,
} from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Announcer,
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  LoadingState,
  Progress,
  StatusPill,
  ToastService,
} from '../../ui';
import { ExportApi, HttpExportApi } from './export-api';
import {
  DELIMITER_OPTIONS,
  ENCODING_OPTIONS,
  EXPORT_COLUMNS,
  EXPORT_STATUS,
  formatBytes,
  rerunRequest,
} from './export-model';
import {
  FrozenSet,
  FrozenSetApi,
  HttpFrozenSetApi,
  newIdempotencyKey,
} from './frozen-set/frozen-set-api';

const POLL_MS = 2000;

/**
 * One export (E12-T08): its frozen set ID and document count, the settings it ran with, progress while it runs, the
 * report with the manifest SHA-256, the documents left out by the access re-check (Q-15), and the delivered files.
 * Every file and the package download through the protected-content gateway as plain links, so each download is
 * authorized and audited again. "Re-run with same settings" exports the same frozen set again: identical membership.
 */
@Component({
  selector: 'opp-export-detail',
  imports: [Button, EmptyState, ErrorState, Icon, LoadingState, Progress, RouterLink, StatusPill],
  templateUrl: './export-detail.html',
  styleUrl: './delivery.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [
    { provide: ExportApi, useClass: HttpExportApi },
    { provide: FrozenSetApi, useClass: HttpFrozenSetApi },
  ],
})
export class ExportDetail {
  private readonly api = inject(ExportApi);
  private readonly sets = inject(FrozenSetApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly prefs = inject(UiPreferences);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly context = inject(WorkspaceContext);
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly exportId = this.route.snapshot.paramMap.get('exportId') ?? '';
  protected readonly canCreate = this.context.can(PERMISSIONS.exportCreate);
  protected readonly item = signal<ExportResource | null>(null);
  protected readonly frozen = signal<FrozenSet | null>(null);
  protected readonly frozenError = signal<ApiError | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly exclusions = signal<readonly ExportExclusionResource[]>([]);
  protected readonly exclusionsCursor = signal<string | null>(null);
  protected readonly files = signal<readonly ExportFileResource[] | null>(null);
  protected readonly filesCursor = signal<string | null>(null);
  protected readonly filesError = signal<ApiError | null>(null);
  protected readonly rerunning = signal(false);
  private readonly fieldNames = signal<ReadonlyMap<number, string>>(new Map());
  protected readonly status = EXPORT_STATUS;

  protected readonly running = computed(() => this.item()?.status === 'running');
  protected readonly manifest = computed(
    () => this.files()?.find((f) => f.kind === 'manifest') ?? null,
  );
  protected readonly exclusionReport = computed(
    () => this.files()?.find((f) => f.kind === 'report') ?? null,
  );
  protected readonly progress = computed(() => {
    const c = this.item()?.job.committed;
    if (!c) return null;
    const total = Number(c.chunksTotal);
    return total > 0 ? { value: Number(c.chunksCommitted), max: total } : null;
  });
  private readonly dates = computed(
    () => new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
    void this.load();
    this.api.fields().then(
      (fields) => this.fieldNames.set(new Map(fields.map((f) => [f.fieldId, f.name]))),
      () => undefined,
    );
  }

  protected async load(): Promise<void> {
    this.error.set(null);
    try {
      const item = await this.api.get(this.exportId);
      const wasRunning = this.item()?.status === 'running';
      this.item.set(item);
      if (!this.frozen() && !this.frozenError()) void this.loadFrozen(item.snapshotId);
      if (item.status === 'running') {
        this.schedule();
        return;
      }
      if (wasRunning) {
        this.announcer.announce(`Export ${item.name}: ${STATUS_WORDS[item.status]}.`);
      }
      if (this.files() === null) void this.loadDelivery(item);
    } catch (e) {
      this.error.set(toApiError(e));
    }
  }

  private async loadFrozen(snapshotId: string): Promise<void> {
    try {
      this.frozen.set(await this.sets.frozenSet(snapshotId));
    } catch (e) {
      this.frozenError.set(toApiError(e));
    }
  }

  private async loadDelivery(item: ExportResource): Promise<void> {
    try {
      const page = await this.api.exclusions(item.exportId);
      this.exclusions.set(page.items);
      this.exclusionsCursor.set(page.nextCursor);
    } catch {
      this.exclusions.set([]);
    }
    if (item.status !== 'completed') {
      this.files.set([]);
      return;
    }
    try {
      const page = await this.api.files(item.exportId);
      this.files.set(page.items);
      this.filesCursor.set(page.nextCursor);
    } catch (e) {
      this.files.set([]);
      this.filesError.set(toApiError(e));
    }
  }

  protected async moreExclusions(): Promise<void> {
    const page = await this.api.exclusions(this.exportId, this.exclusionsCursor());
    this.exclusions.update((rows) => [...rows, ...page.items]);
    this.exclusionsCursor.set(page.nextCursor);
  }

  protected async moreFiles(): Promise<void> {
    const page = await this.api.files(this.exportId, this.filesCursor());
    this.files.update((rows) => [...(rows ?? []), ...page.items]);
    this.filesCursor.set(page.nextCursor);
  }

  private schedule(): void {
    this.stop();
    this.timer = setTimeout(() => void this.load(), POLL_MS);
  }

  private stop(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }

  protected async rerun(): Promise<void> {
    const item = this.item();
    if (!item || this.rerunning()) return;
    const confirmed = await this.dialogs.confirm({
      title: 'Re-run with same settings',
      message:
        `Export frozen set ${item.snapshotId} again with the same settings. The new volume has the same ` +
        'documents; each one is checked again for your access when it is written.',
      confirmLabel: 'Re-run export',
    });
    if (!confirmed) return;
    this.rerunning.set(true);
    try {
      const created = await this.api.create(rerunRequest(item), newIdempotencyKey());
      this.toasts.show(`Export ${created.name} started.`, { tone: 'success' });
      await this.router.navigate(['..', created.exportId], { relativeTo: this.route });
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The export could not be started again.', {
        tone: 'error',
      });
    } finally {
      this.rerunning.set(false);
    }
  }

  protected fileUrl(file: ExportFileResource): string {
    return this.api.fileUrl(this.exportId, file.fileId);
  }

  protected packageUrl(): string {
    return this.api.packageUrl(this.exportId);
  }

  protected date(value: unknown): string {
    return value ? this.dates().format(new Date(String(value))) : '';
  }

  protected num(value: number | string | null | undefined): string {
    return value === null || value === undefined
      ? '—'
      : Number(value).toLocaleString(this.prefs.locale());
  }

  protected reportTotal(e: ExportResource): number {
    return e.report ? Number(e.report.documentsExported) + Number(e.report.documentsExcluded) : 0;
  }

  protected bytes(value: number | string): string {
    return formatBytes(Number(value), this.prefs.locale());
  }

  protected columnSource(c: { fieldId: number | string | null; column: string | null }): string {
    if (c.fieldId !== null && c.fieldId !== undefined) {
      return this.fieldNames().get(Number(c.fieldId)) ?? 'Field';
    }
    return EXPORT_COLUMNS.find((x) => x.column === c.column)?.source ?? c.column ?? '';
  }

  protected label(options: readonly { value: string; label: string }[], value: string): string {
    return options.find((o) => o.value === value)?.label ?? value;
  }

  protected readonly delimiterOptions = DELIMITER_OPTIONS;
  protected readonly encodingOptions = ENCODING_OPTIONS;
}

const STATUS_WORDS: Record<ExportResource['status'], string> = {
  running: 'running',
  completed: 'completed',
  failed: 'failed',
  cancelled: 'cancelled',
};
