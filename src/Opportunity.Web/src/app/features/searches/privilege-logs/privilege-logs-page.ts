import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import {
  Announcer,
  Badge,
  Button,
  EmptyState,
  ErrorState,
  Icon,
  LoadingState,
  Select,
  SelectOption,
  ToastService,
} from '../../../ui';
import { formatDate } from '../saved-search-model';
import {
  FinalizedProduction,
  HttpPrivilegeLogApi,
  LogTemplateChoice,
  LogVersion,
  PrivilegeLogApi,
} from './privilege-logs-api';

/**
 * Searches › Privilege Logs (E13-T03, as far as #111 needs of #182): generate a privilege log from a finalized
 * production with a template or preset (document-by-document or metadata only, Q-20), see each version with its
 * SHA-256, the recorded exclusion rules and their counts, and download it as CSV or XLSX through the protected-content
 * gateway. Generating again over unchanged inputs returns the same version. Versions that list a document or read a
 * field the user may not see are not shown (Q-52), and the page says so. Template editing stays in the API for now.
 */
@Component({
  selector: 'opp-privilege-logs-page',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState, Select],
  templateUrl: './privilege-logs-page.html',
  styleUrl: './privilege-logs-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: PrivilegeLogApi, useClass: HttpPrivilegeLogApi }],
})
export class PrivilegeLogsPage {
  private readonly api = inject(PrivilegeLogApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly timeZone = this.context.workspace.displayTimeZone || 'UTC';

  protected readonly canProduce = this.context.can(PERMISSIONS.productionCreate);
  protected readonly productions = signal<readonly FinalizedProduction[]>([]);
  protected readonly templates = signal<readonly LogTemplateChoice[]>([]);
  protected readonly versions = signal<readonly LogVersion[] | null>(null);
  protected readonly production = signal('');
  protected readonly template = signal('preset:documentByDocument');
  protected readonly selectedLog = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly generating = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly productionOptions = computed<SelectOption[]>(() =>
    this.productions().map((p) => ({
      value: p.productionId,
      label: `${p.name}${p.version > 1 ? ` (v${p.version})` : ''}${p.batesRange ? ` · ${p.batesRange}` : ''}`,
    })),
  );
  protected readonly templateOptions = computed<SelectOption[]>(() =>
    this.templates().map((t) => ({ value: t.key, label: t.name })),
  );
  protected readonly templateColumns = computed(
    () => this.templates().find((t) => t.key === this.template())?.columns ?? [],
  );
  protected readonly selected = computed(
    () => this.versions()?.find((v) => v.logId === this.selectedLog()) ?? null,
  );

  constructor() {
    void this.start();
  }

  private async start(): Promise<void> {
    const [productions, templates] = await Promise.allSettled([
      this.canProduce ? this.api.productions() : Promise.resolve([]),
      this.api.templates(),
    ]);
    if (productions.status === 'fulfilled') {
      this.productions.set(productions.value);
      if (productions.value.length > 0) this.production.set(productions.value[0].productionId);
    }
    if (templates.status === 'fulfilled') this.templates.set(templates.value);
    await this.refresh();
  }

  protected async refresh(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const versions = await this.api.versions();
      this.versions.set(versions);
      if (!versions.some((v) => v.logId === this.selectedLog())) {
        this.selectedLog.set(versions[0]?.logId ?? null);
      }
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async generate(): Promise<void> {
    const production = this.production();
    if (!production || this.generating()) return;
    this.generating.set(true);
    try {
      const result = await this.api.generate(production, this.template());
      await this.refresh();
      this.selectedLog.set(result.log.logId);
      const message = result.unchanged
        ? `Nothing changed since version ${result.log.version}: no new version was created.`
        : `Version ${result.log.version} generated: ${this.count(result.log.entries, 'document')} listed.`;
      this.announcer.announce(message);
      this.toasts.show(message, { tone: result.unchanged ? 'info' : 'success' });
    } catch (e) {
      this.toasts.show(
        toApiError(e).problem.detail ?? 'The privilege log could not be generated.',
        {
          tone: 'error',
        },
      );
    } finally {
      this.generating.set(false);
    }
  }

  protected select(version: LogVersion): void {
    this.selectedLog.set(version.logId);
  }

  protected downloadUrl(version: LogVersion, format: 'csv' | 'xlsx'): string {
    return this.api.downloadUrl(version.logId, format);
  }

  protected generatedAt(version: LogVersion): string {
    return formatDate(version.generatedAt, this.prefs.locale(), this.timeZone);
  }

  protected count(n: number, noun: string): string {
    return `${n.toLocaleString(this.prefs.locale())} ${noun}${n === 1 ? '' : 's'}`;
  }

  protected size(bytes: number): string {
    return bytes < 1024
      ? `${bytes.toLocaleString(this.prefs.locale())} B`
      : `${(bytes / 1024).toLocaleString(this.prefs.locale(), { maximumFractionDigits: 1 })} KB`;
  }

  protected short(sha: string): string {
    return sha.slice(0, 12);
  }
}
