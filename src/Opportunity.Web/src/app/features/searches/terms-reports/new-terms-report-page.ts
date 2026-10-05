import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { toApiError } from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Announcer, Button, Icon, Select, SelectOption, TextField } from '../../../ui';
import {
  FrozenSetSummary,
  HttpSavedSearchApi,
  SavedSearchApi,
  SavedSearchSummary,
} from '../saved-search-api';
import { formatDate } from '../saved-search-model';
import {
  HttpSearchTermReportApi,
  ReportScopeKind,
  SearchTermReportApi,
  SearchTermReportDraft,
} from './search-term-report-api';
import {
  ParsedTerms,
  SCOPE_KINDS,
  SCOPE_SAVED_SEARCH_PARAM,
  SCOPE_SNAPSHOT_PARAM,
  defaultReportName,
  parsePastedTerms,
  parseTermsCsv,
  reportErrorText,
} from './search-term-report-model';

type TermSource = 'paste' | 'csv';

const EMPTY: ParsedTerms = { terms: [], issues: [], blocking: [] };
/** Terms listed in the preview; all of them are sent. */
const PREVIEW_ROWS = 200;

/**
 * Searches › Search Terms Reports › New report (#180, familiarity guide §5 step 5): a name, the scope (a saved
 * search, a frozen set or the whole workspace; `?savedSearch=<id>` or `?frozenSet=<id>` pre-fills it) and the terms,
 * pasted one per line or uploaded as a CSV file of `Name,Expression` rows, with a preview of what will be counted.
 * Running it starts a background job and opens the report, which shows its progress.
 */
@Component({
  selector: 'opp-new-terms-report-page',
  imports: [Button, Icon, RouterLink, Select, TextField],
  templateUrl: './new-terms-report-page.html',
  styleUrls: ['./terms-reports.scss', './terms-report-form.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    { provide: SearchTermReportApi, useClass: HttpSearchTermReportApi },
    { provide: SavedSearchApi, useClass: HttpSavedSearchApi },
  ],
})
export class NewTermsReportPage {
  private readonly api = inject(SearchTermReportApi);
  private readonly savedApi = inject(SavedSearchApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly announcer = inject(Announcer);
  private readonly summary = viewChild<ElementRef<HTMLElement>>('summary');
  private readonly injector = inject(Injector);

  protected readonly scopeKinds = SCOPE_KINDS;
  protected readonly previewRows = PREVIEW_ROWS;
  protected readonly name = signal(defaultReportName(new Date(), this.prefs.locale()));
  protected readonly scopeKind = signal<ReportScopeKind>('savedSearch');
  protected readonly savedSearchId = signal('');
  protected readonly snapshotId = signal('');
  protected readonly source = signal<TermSource>('paste');
  protected readonly pasted = signal('');
  protected readonly csvFile = signal<{ name: string; parsed: ParsedTerms } | null>(null);
  protected readonly csvError = signal<string | null>(null);

  protected readonly savedSearches = signal<readonly SavedSearchSummary[] | null>(null);
  protected readonly frozenSets = signal<readonly FrozenSetSummary[] | null>(null);
  protected readonly optionsError = signal<string | null>(null);

  protected readonly submitted = signal(false);
  protected readonly busy = signal(false);
  protected readonly submitError = signal<string | null>(null);
  /** One key per draft: a retry after a lost answer starts no second job. */
  private idempotency: { key: string; body: string } | null = null;

  private readonly timeZone = this.context.workspace.displayTimeZone || 'UTC';

  protected readonly parsed = computed<ParsedTerms>(() => {
    if (this.source() === 'csv') return this.csvFile()?.parsed ?? EMPTY;
    return parsePastedTerms(this.pasted());
  });
  protected readonly termCountText = computed(() => {
    const n = this.parsed().terms.length;
    return `${new Intl.NumberFormat(this.prefs.locale()).format(n)} ${n === 1 ? 'term' : 'terms'}`;
  });

  protected readonly savedOptions = computed<SelectOption[]>(() => {
    const searches = this.savedSearches() ?? [];
    return [...searches]
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
      .map((s) => ({ value: s.savedSearchId, label: s.name }));
  });
  protected readonly frozenOptions = computed<SelectOption[]>(() => {
    const locale = this.prefs.locale();
    const n = new Intl.NumberFormat(locale);
    return (this.frozenSets() ?? []).map((f) => ({
      value: f.snapshotId,
      label: `${f.name} · ${f.documentCount === null ? 'freezing' : n.format(f.documentCount) + ' documents'}${f.frozenAt ? ' · ' + formatDate(f.frozenAt, locale, this.timeZone) : ''}`,
      disabled: f.documentCount === null,
    }));
  });
  protected readonly errors = computed(() => {
    const e: { field: string; message: string }[] = [];
    if (!this.name().trim()) e.push({ field: 'name', message: 'Enter a name for the report.' });
    if (this.scopeKind() === 'savedSearch' && !this.savedSearchId())
      e.push({ field: 'savedSearch', message: 'Choose the saved search to count in.' });
    if (this.scopeKind() === 'snapshot' && !this.snapshotId())
      e.push({ field: 'snapshot', message: 'Choose the frozen set to count in.' });
    const parsed = this.parsed();
    if (parsed.terms.length === 0)
      e.push({
        field: 'terms',
        message:
          this.source() === 'csv'
            ? 'Upload a CSV file with at least one term.'
            : 'Enter at least one term.',
      });
    for (const b of parsed.blocking) e.push({ field: 'terms', message: b });
    return e;
  });
  protected readonly shownErrors = computed(() => (this.submitted() ? this.errors() : []));
  protected errorFor(field: string): string | undefined {
    return this.shownErrors().find((e) => e.field === field)?.message;
  }

  constructor() {
    const params = this.route.snapshot.queryParamMap;
    const saved = params.get(SCOPE_SAVED_SEARCH_PARAM);
    const frozen = params.get(SCOPE_SNAPSHOT_PARAM);
    if (frozen) {
      this.scopeKind.set('snapshot');
      this.snapshotId.set(frozen);
    } else if (saved) this.savedSearchId.set(saved);
    void this.loadOptions();
  }

  protected async loadOptions(): Promise<void> {
    this.optionsError.set(null);
    const [searches, frozen] = await Promise.allSettled([
      this.savedApi.listAll(),
      this.savedApi.frozenSets(),
    ]);
    if (searches.status === 'fulfilled') this.savedSearches.set(searches.value);
    else this.savedSearches.set([]);
    if (frozen.status === 'fulfilled') this.frozenSets.set(frozen.value);
    else this.frozenSets.set([]);
    if (searches.status === 'rejected' || frozen.status === 'rejected')
      this.optionsError.set(
        'Some saved searches or frozen sets could not be loaded. Try again, or count in the whole workspace.',
      );
    // A pre-filled saved search the caller cannot see (deleted, no longer shared) is not chosen silently.
    const id = this.savedSearchId();
    if (id && !this.savedSearches()?.some((s) => s.savedSearchId === id))
      this.savedSearchId.set('');
  }

  protected setScope(kind: ReportScopeKind): void {
    this.scopeKind.set(kind);
  }

  protected setSource(source: TermSource): void {
    this.source.set(source);
  }

  protected onPaste(event: Event): void {
    this.pasted.set((event.target as HTMLTextAreaElement).value);
  }

  protected async onCsv(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.csvError.set(null);
    if (!file) {
      this.csvFile.set(null);
      return;
    }
    try {
      const parsed = parseTermsCsv(await file.text());
      this.csvFile.set({ name: file.name, parsed });
      this.announcer.announce(
        `${file.name}: ${parsed.terms.length} ${parsed.terms.length === 1 ? 'term' : 'terms'} read.`,
      );
    } catch {
      this.csvFile.set(null);
      this.csvError.set('The file could not be read. Save it as a UTF-8 CSV file and try again.');
    }
  }

  /** The error summary is rendered by the change that shows it: focus it once it is in the page. */
  private focusSummary(): void {
    afterNextRender(() => this.summary()?.nativeElement.focus(), { injector: this.injector });
  }

  protected issuesText(n: number): string {
    return `${n} ${n === 1 ? 'line was' : 'lines were'} not used:`;
  }

  protected async run(event?: Event): Promise<void> {
    event?.preventDefault();
    this.submitted.set(true);
    this.submitError.set(null);
    if (this.errors().length) {
      this.focusSummary();
      return;
    }
    const kind = this.scopeKind();
    const draft: SearchTermReportDraft = {
      name: this.name().trim(),
      terms: this.parsed().terms,
      scope:
        kind === 'workspace'
          ? { kind }
          : { kind, id: kind === 'savedSearch' ? this.savedSearchId() : this.snapshotId() },
    };
    const body = JSON.stringify(draft);
    if (this.idempotency?.body !== body)
      this.idempotency = {
        key:
          globalThis.crypto?.randomUUID?.() ??
          `${Date.now()}-${Math.random().toString(36).slice(2)}`,
        body,
      };
    this.busy.set(true);
    try {
      const report = await this.api.create(draft, this.idempotency.key);
      await this.router.navigate(['..', report.reportId], { relativeTo: this.route });
    } catch (e) {
      this.submitError.set(reportErrorText(toApiError(e)));
      this.focusSummary();
    } finally {
      this.busy.set(false);
    }
  }
}
