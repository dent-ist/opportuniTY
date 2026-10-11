import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
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
import { PrivilegeLogChanges } from './privilege-log-changes';
import { PrivilegeLogEntries } from './privilege-log-entries';
import {
  FinalizedProduction,
  HttpPrivilegeLogApi,
  LogSource,
  LogTemplateChoice,
  LogVersion,
  PrivilegeLogApi,
  SavedSearchChoice,
} from './privilege-logs-api';

type SourceKind = 'production' | 'savedSearch';

/**
 * Searches › Privilege Logs (E13-T03, #182): generate a privilege log from a finalized production or a saved search
 * with a template or preset (document-by-document or metadata only, Q-20); see each version with its SHA-256, the
 * recorded exclusion rules and their counts, its entries (metadata columns and privilege basis) and what changed
 * since the previous version; edit an entry's description (the Privilege Description field, through coding: audited,
 * with history) and generate the next version; download CSV or XLSX through the protected-content gateway.
 * Generating again over unchanged inputs returns the same version. Versions that list a document or read a field the
 * user may not see are not shown (Q-52), and the page says so. Template editing stays in the API for now.
 */
@Component({
  selector: 'opp-privilege-logs-page',
  imports: [
    Badge,
    Button,
    EmptyState,
    ErrorState,
    Icon,
    LoadingState,
    PrivilegeLogChanges,
    PrivilegeLogEntries,
    Select,
  ],
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
  /** `?production=<id>` from a production's Privilege Log tab preselects it (E12-T08). */
  private readonly requested = inject(ActivatedRoute).snapshot.queryParamMap.get('production');

  protected readonly canProduce = this.context.can(PERMISSIONS.productionCreate);
  protected readonly canSearch = this.context.can(PERMISSIONS.searchExecute);
  protected readonly canCode = this.context.can(PERMISSIONS.codingWrite);
  protected readonly productions = signal<readonly FinalizedProduction[]>([]);
  protected readonly savedSearches = signal<readonly SavedSearchChoice[]>([]);
  protected readonly templates = signal<readonly LogTemplateChoice[]>([]);
  protected readonly versions = signal<readonly LogVersion[] | null>(null);
  protected readonly sourceKind = signal<string>(this.canProduce ? 'production' : 'savedSearch');
  protected readonly production = signal('');
  protected readonly savedSearch = signal('');
  protected readonly template = signal('preset:documentByDocument');
  protected readonly selectedLog = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly generating = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly sourceOptions: SelectOption[] = [
    ...(this.canProduce ? [{ value: 'production', label: 'Finalized production' }] : []),
    ...(this.canSearch ? [{ value: 'savedSearch', label: 'Saved search' }] : []),
  ];
  protected readonly savedSearchOptions = computed<SelectOption[]>(() =>
    this.savedSearches().map((s) => ({ value: s.savedSearchId, label: s.name })),
  );
  /** The source chosen in the Generate panel, when one can be generated from. */
  protected readonly source = computed<LogSource | null>(() => {
    const kind = this.sourceKind() as SourceKind;
    if (kind === 'production') {
      return this.canProduce && this.production()
        ? { kind: 'production', id: this.production() }
        : null;
    }
    return this.canSearch && this.savedSearch()
      ? { kind: 'savedSearch', id: this.savedSearch() }
      : null;
  });
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
    const [productions, templates, savedSearches] = await Promise.allSettled([
      this.canProduce ? this.api.productions() : Promise.resolve([]),
      this.api.templates(),
      this.canSearch ? this.api.savedSearches() : Promise.resolve([]),
    ]);
    if (productions.status === 'fulfilled') {
      this.productions.set(productions.value);
      const requested = productions.value.find((p) => p.productionId === this.requested);
      if (productions.value.length > 0) {
        this.production.set((requested ?? productions.value[0]).productionId);
      }
    }
    if (templates.status === 'fulfilled') this.templates.set(templates.value);
    if (savedSearches.status === 'fulfilled') {
      this.savedSearches.set(savedSearches.value);
      if (savedSearches.value.length > 0)
        this.savedSearch.set(savedSearches.value[0].savedSearchId);
    }
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
    const source = this.source();
    if (source) await this.run(source, this.template());
  }

  /** Generates the next version of a version's log: the same source and template, the coding as it is now. */
  protected async regenerate(version: LogVersion): Promise<void> {
    await this.run(this.sourceOf(version), this.templateKeyOf(version));
  }

  protected canRegenerate(version: LogVersion): boolean {
    return version.source === 'production' ? this.canProduce : true;
  }

  private sourceOf(version: LogVersion): LogSource {
    if (version.source === 'production') {
      return {
        kind: 'production',
        id: version.productionId!,
        reviewSetId: version.reviewSetSnapshotId,
      };
    }
    return version.source === 'savedSearch'
      ? { kind: 'savedSearch', id: version.savedSearchId! }
      : { kind: 'snapshot', id: version.snapshotId };
  }

  private templateKeyOf(version: LogVersion): string {
    if (version.templateId) return `template:${version.templateId}`;
    const preset = this.templates().find(
      (t) => t.key.startsWith('preset:') && t.name === version.templateName,
    );
    return preset?.key ?? 'preset:documentByDocument';
  }

  private async run(source: LogSource, templateKey: string): Promise<void> {
    if (this.generating()) return;
    this.generating.set(true);
    try {
      const result = await this.api.generate(source, templateKey);
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

  protected sourceName(version: LogVersion): string {
    return version.productionName ?? version.savedSearchName ?? 'Frozen set';
  }

  protected sourceKindText(version: LogVersion): string {
    return version.source === 'production'
      ? 'Production'
      : version.source === 'savedSearch'
        ? 'Saved search'
        : 'Frozen set';
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
