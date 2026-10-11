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
import { ApiError, toApiError } from '../../core/api/problem-details';
import {
  Button,
  Checkbox,
  ErrorState,
  Icon,
  IconButton,
  Select,
  SelectOption,
  TextField,
} from '../../ui';
import { csvFileName, saveCsv, toCsv, validationRows } from './csv';
import { ExportApi, ExportableField, HttpExportApi } from './export-api';
import {
  DELIMITER_OPTIONS,
  ENCODING_OPTIONS,
  EXPORT_COLUMNS,
  EXPORT_STEPS,
  ExportForm,
  ExportStepId,
  SEPARATOR_OPTIONS,
  TEXT_ENCODING_OPTIONS,
  computedColumn,
  defaultExportForm,
  examplePath,
  fieldColumn,
  moveColumn,
  settingLabel,
  stepProblems,
  toCreateRequest,
  volumeName,
  withPathColumns,
} from './export-model';
import {
  FrozenSet,
  FrozenSetApi,
  HttpFrozenSetApi,
  newIdempotencyKey,
} from './frozen-set/frozen-set-api';
import { FrozenSetSource } from './frozen-set/frozen-set-source';
import { DisplayFormat } from '../../core/i18n';

/**
 * Exports → New Export (E12-T08, familiarity guide §5.5): Source (a saved search frozen now, or a frozen set; its ID and
 * count are shown) → Volume & folders (with a live example path) → Files → Load file (ordered DAT columns with header
 * renaming, delimiters, encoding) → Review & run. Every step can go back without losing input. Errors the API finds
 * block the run and can be downloaded as a CSV list; starting goes to the export's page, which follows the job.
 *
 * Keyboard: the rail is a list of buttons (reached steps only); after a step change focus moves to the step heading;
 * column order changes with the Move up / Move down buttons of each row.
 */
@Component({
  selector: 'opp-export-wizard',
  imports: [
    Button,
    Checkbox,
    ErrorState,
    FrozenSetSource,
    Icon,
    IconButton,
    RouterLink,
    Select,
    TextField,
  ],
  templateUrl: './export-wizard.html',
  styleUrls: ['../imports/import-wizard.scss', './delivery.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'dl-page' },
  providers: [
    { provide: ExportApi, useClass: HttpExportApi },
    { provide: FrozenSetApi, useClass: HttpFrozenSetApi },
  ],
})
export class ExportWizard {
  private readonly api = inject(ExportApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  private readonly fmt = inject(DisplayFormat);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('stepHeading');

  protected readonly steps = EXPORT_STEPS;
  protected readonly delimiterOptions = DELIMITER_OPTIONS;
  protected readonly encodingOptions = ENCODING_OPTIONS;
  protected readonly textEncodingOptions = TEXT_ENCODING_OPTIONS;
  protected readonly separatorOptions = SEPARATOR_OPTIONS;
  /** `?savedSearch=<id>` from Searches preselects the source. */
  protected readonly initialSavedSearch = this.route.snapshot.queryParamMap.get('savedSearch');

  protected readonly current = signal<ExportStepId>('source');
  private readonly reached = signal(0);
  protected readonly frozen = signal<FrozenSet | null>(null);
  protected readonly fields = signal<readonly ExportableField[]>([]);
  protected readonly fieldsError = signal<ApiError | null>(null);
  protected readonly form = signal<ExportForm>(defaultExportForm([]));
  protected readonly addChoice = signal('');
  protected readonly blocked = signal<readonly string[]>([]);
  protected readonly busy = signal(false);
  /** Errors of the last run attempt, by setting (a 400 from the API). */
  protected readonly runErrors = signal<readonly string[][]>([]);
  protected readonly runError = signal<ApiError | null>(null);
  private key = newIdempotencyKey();

  protected readonly index = computed(() => this.steps.findIndex((s) => s.id === this.current()));
  protected readonly step = computed(() => this.steps[this.index()]);
  protected readonly isLast = computed(() => this.index() === this.steps.length - 1);
  protected readonly example = computed(() => examplePath(this.form()));
  protected readonly volume = computed(() => {
    const f = this.form();
    return volumeName(f.volumePrefix, Number(f.volumeStart) || 1, Number(f.volumePadding) || 3);
  });
  protected readonly addOptions = computed<SelectOption[]>(() => {
    const used = new Set(this.form().columns.map((c) => c.key));
    const f = this.form();
    return [
      ...EXPORT_COLUMNS.filter(
        (c) =>
          !used.has(`column:${c.column}`) &&
          (c.column !== 'nativePath' || f.includeNatives) &&
          (c.column !== 'textPath' || f.includeText),
      ).map((c) => ({ value: `column:${c.column}`, label: `${c.source} (computed)` })),
      ...this.fields()
        .filter((x) => !used.has(`field:${x.fieldId}`))
        .map((x) => ({ value: `field:${x.fieldId}`, label: x.name })),
    ];
  });

  constructor() {
    void this.loadFields();
  }

  private async loadFields(): Promise<void> {
    try {
      const fields = await this.api.fields();
      this.fields.set(fields);
      this.form.set(defaultExportForm(fields));
    } catch (e) {
      this.fieldsError.set(toApiError(e));
    }
  }

  protected reachable(i: number): boolean {
    return i <= this.reached();
  }

  protected goTo(id: ExportStepId): void {
    this.blocked.set([]);
    this.current.set(id);
    this.reached.update((r) => Math.max(r, this.index()));
    afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
  }

  protected back(): void {
    const i = this.index();
    if (i > 0) this.goTo(this.steps[i - 1].id);
  }

  protected async next(): Promise<void> {
    if (this.busy()) return;
    const problems = stepProblems(this.current(), this.form(), !!this.frozen());
    this.blocked.set(problems);
    if (problems.length) return;
    if (this.isLast()) {
      await this.run();
      return;
    }
    this.goTo(this.steps[this.index() + 1].id);
  }

  protected update(patch: Partial<ExportForm>): void {
    this.form.update((f) => withPathColumns({ ...f, ...patch }));
    this.runErrors.set([]);
  }

  protected setHeader(index: number, header: string): void {
    this.update({
      columns: this.form().columns.map((c, i) => (i === index ? { ...c, header } : c)),
    });
  }

  protected move(index: number, delta: -1 | 1): void {
    this.update({ columns: moveColumn(this.form().columns, index, delta) });
  }

  protected remove(index: number): void {
    this.update({ columns: this.form().columns.filter((_, i) => i !== index) });
  }

  protected addColumn(): void {
    const key = this.addChoice();
    if (!key) return;
    const [kind, value] = [key.slice(0, key.indexOf(':')), key.slice(key.indexOf(':') + 1)];
    const field = this.fields().find((f) => String(f.fieldId) === value);
    const column =
      kind === 'field' && field
        ? fieldColumn(field)
        : computedColumn(value as (typeof EXPORT_COLUMNS)[number]['column']);
    this.update({ columns: [...this.form().columns, column] });
    this.addChoice.set('');
  }

  private async run(): Promise<void> {
    const set = this.frozen();
    if (!set) return;
    this.busy.set(true);
    this.runErrors.set([]);
    this.runError.set(null);
    try {
      const created = await this.api.create(toCreateRequest(set.snapshotId, this.form()), this.key);
      await this.router.navigate(['..', created.exportId], { relativeTo: this.route });
    } catch (e) {
      const error = toApiError(e);
      const rows = validationRows(error.problem.errors);
      if (error.status === 400 && rows.length) {
        this.runErrors.set(rows.map(([k, m]) => [settingLabel(k), m]));
      } else {
        this.runError.set(error);
      }
      // A corrected request is a new request.
      this.key = newIdempotencyKey();
    } finally {
      this.busy.set(false);
    }
  }

  protected downloadErrors(): void {
    const set = this.frozen();
    saveCsv(
      csvFileName([this.form().name || 'New-export', set?.snapshotId ?? '', 'validation-errors']),
      toCsv([['Setting', 'Problem'], ...this.runErrors()]),
    );
  }

  protected num(n: number | null): string {
    return this.fmt.number(n ?? 0);
  }

  protected fileKinds(): string {
    const f = this.form();
    const kinds = [
      f.includeNatives ? 'natives' : '',
      f.includeImages ? 'images (with OPT)' : '',
      f.includeText ? 'extracted text' : '',
    ].filter(Boolean);
    return kinds.length ? kinds.join(', ') : 'load file only';
  }

  protected optionLabel(options: readonly SelectOption[], value: string): string {
    return options.find((o) => o.value === value)?.label ?? value;
  }
}
