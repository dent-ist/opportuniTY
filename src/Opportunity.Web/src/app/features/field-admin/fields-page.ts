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
import type { FieldResource, UpdateFieldRequest } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import {
  AdminField,
  CapacityEntry,
  FieldAdminApi,
  HttpFieldAdminApi,
} from '../../core/fields/field-admin-api';
import {
  Announcer,
  Badge,
  Button,
  Checkbox,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
  Select,
  SelectOption,
  TextField,
} from '../../ui';
import { ChoiceEditor } from './choice-editor';
import {
  FIELD_TYPES,
  FieldType,
  SECURITY_CLASSES,
  capabilityWords,
  isChoiceType,
  storageLabel,
  typeLabel,
} from './field-admin-model';

interface FieldForm {
  readonly name: string;
  readonly description: string;
  readonly type: FieldType;
  readonly storage: 'coding' | 'metadata';
  readonly multiValue: boolean;
  readonly datePrecision: 'date' | 'dateTime';
  readonly decimalPrecision: string;
  readonly decimalScale: string;
  readonly textAnalysis: 'prose' | 'identifier';
  readonly searchable: boolean;
  readonly securityClass: string;
  readonly hidden: boolean;
}

const NEW_FIELD: FieldForm = {
  name: '',
  description: '',
  type: 'singleChoice',
  storage: 'coding',
  multiValue: false,
  datePrecision: 'date',
  decimalPrecision: '18',
  decimalScale: '2',
  textAnalysis: 'prose',
  searchable: true,
  securityClass: '',
  hidden: false,
};

const SOURCE_FILTERS: SelectOption[] = [
  { value: '', label: 'All fields' },
  { value: 'coding', label: 'Coding fields' },
  { value: 'metadata', label: 'Imported fields' },
  { value: 'system', label: 'System fields' },
];

/**
 * Admin › Fields (E04-T06, familiarity guide §1.1 and §3.4): every field of the workspace with its type, source and
 * what search can do with it, and an editor to create coding or imported fields of every type, rename, describe or
 * hide them, change the type while no document holds a value, manage choices and retire custom fields. Mapping
 * limitations are shown before saving ("This field will not be sortable."). System fields can be renamed and hidden
 * but never retyped or retired. Changes need `Workspace.ManageFields`, are sent with If-Match and are audited.
 */
@Component({
  selector: 'opp-fields-page',
  imports: [
    Badge,
    Button,
    Checkbox,
    ChoiceEditor,
    EmptyState,
    ErrorState,
    Icon,
    IconButton,
    LoadingState,
    Select,
    TextField,
  ],
  templateUrl: './fields-page.html',
  styleUrl: './field-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: FieldAdminApi, useClass: HttpFieldAdminApi }],
  host: { class: 'fa-page' },
})
export class FieldsPage {
  private readonly api = inject(FieldAdminApi);
  private readonly dialogs = inject(DialogService);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');
  private readonly nameField = viewChild<TextField>('nameField');

  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly fields = signal<readonly FieldResource[]>([]);
  protected readonly capacity = signal<readonly CapacityEntry[]>([]);
  protected readonly filterText = signal('');
  protected readonly sourceFilter = signal('');

  /** The field being edited, `'new'` for a new one, null when the editor is closed. */
  protected readonly editing = signal<AdminField | 'new' | null>(null);
  protected readonly opening = signal(false);
  protected readonly form = signal<FieldForm>(NEW_FIELD);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saveError = signal<string | null>(null);

  protected readonly typeOptions: SelectOption[] = FIELD_TYPES.map((t) => ({ ...t }));
  protected readonly sourceFilters = SOURCE_FILTERS;
  protected readonly storageOptions: SelectOption[] = [
    { value: 'coding', label: 'Coding: set by reviewers' },
    { value: 'metadata', label: 'Imported: filled by load files' },
  ];
  protected readonly securityOptions: SelectOption[] = SECURITY_CLASSES.map((c) => ({ ...c }));
  protected readonly typeLabel = typeLabel;
  protected readonly storageLabel = storageLabel;
  protected readonly isChoiceType = isChoiceType;

  protected readonly visibleFields = computed(() => {
    const text = this.filterText().trim().toLowerCase();
    const source = this.sourceFilter();
    return this.fields()
      .filter((f) =>
        source === 'system' ? f.isSystem : source ? f.storage === source && !f.isSystem : true,
      )
      .filter((f) => !text || f.displayName.toLowerCase().includes(text))
      .sort((a, b) => a.displayName.localeCompare(b.displayName));
  });

  protected readonly current = computed(() => {
    const e = this.editing();
    return e && e !== 'new' ? e : null;
  });

  /** Why the type cannot change, or null. */
  protected readonly typeLock = computed(() => {
    const field = this.current();
    if (!field) return null;
    if (field.isSystem) return 'System fields keep their type.';
    if (field.hasValues)
      return 'Documents already hold values for this field, so its type cannot change. Create a new field and copy the values instead.';
    return null;
  });

  /** Mapping limitations of what is on screen: the saved field, or what a new type would get. */
  protected readonly limitations = computed<readonly string[]>(() => {
    const form = this.form();
    const field = this.current();
    if (field && field.type === form.type) return field.limitations;
    if (!form.searchable && !field) {
      return [
        'This field will not be searchable: it cannot be used in searches, conditions, filters or sorting.',
      ];
    }
    const storage = field ? field.storage : form.storage;
    const analysis = form.type === 'text' ? (field?.textAnalysis ?? form.textAnalysis) : null;
    const entry = this.capacity().find(
      (c) => c.storage === storage && c.type === form.type && (c.textAnalysis ?? null) === analysis,
    );
    return entry?.limitations ?? [];
  });

  protected readonly capabilities = computed<readonly string[]>(() => {
    const form = this.form();
    const field = this.current();
    if (field && field.type === form.type) return capabilityWords(field.capabilities);
    if (!form.searchable && !field) return [];
    const storage = field ? field.storage : form.storage;
    const analysis = form.type === 'text' ? (field?.textAnalysis ?? form.textAnalysis) : null;
    const entry = this.capacity().find(
      (c) => c.storage === storage && c.type === form.type && (c.textAnalysis ?? null) === analysis,
    );
    return entry ? capabilityWords(entry.capabilities) : [];
  });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [fields, capacity] = await Promise.all([this.api.fields(), this.api.capacity()]);
      this.fields.set(fields);
      this.capacity.set(capacity);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected capabilitySummary(field: FieldResource): string {
    if (field.reducedCapabilities) return 'Reduced search';
    const words = capabilityWords(field.capabilities);
    return words.length ? words.join(' · ') : 'Not searchable';
  }

  protected startNew(): void {
    this.editing.set('new');
    this.form.set(NEW_FIELD);
    this.errors.set({});
    this.saveError.set(null);
    afterNextRender(() => this.nameField()?.focus(), { injector: this.injector });
  }

  protected async edit(field: FieldResource): Promise<void> {
    if (this.opening()) return;
    this.opening.set(true);
    try {
      this.open(await this.api.field(Number(field.fieldId)));
      afterNextRender(() => this.nameField()?.focus(), { injector: this.injector });
    } catch (e) {
      this.saveError.set(toApiError(e).problem.detail ?? 'The field could not be opened.');
    } finally {
      this.opening.set(false);
    }
  }

  private open(field: AdminField): void {
    this.editing.set(field);
    this.form.set({
      ...NEW_FIELD,
      name: field.displayName,
      description: field.description ?? '',
      type: field.type,
      storage: field.storage === 'metadata' ? 'metadata' : 'coding',
      multiValue: field.multiValue,
      datePrecision: field.datePrecision ?? 'dateTime',
      decimalPrecision: String(field.decimalPrecision ?? 18),
      decimalScale: String(field.decimalScale ?? 2),
      textAnalysis: field.textAnalysis ?? 'prose',
      searchable: field.isSearchable,
      securityClass: field.securityClass ?? '',
      hidden: field.isHidden,
    });
    this.errors.set({});
    this.saveError.set(null);
  }

  protected cancel(): void {
    this.editing.set(null);
    afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
  }

  protected update(patch: Partial<FieldForm>): void {
    this.form.update((f) => ({ ...f, ...patch }));
  }

  protected onDescription(event: Event): void {
    this.update({ description: (event.target as HTMLTextAreaElement).value });
  }

  protected setType(value: string): void {
    this.update({ type: value as FieldType });
  }

  protected setStorage(value: string): void {
    this.update({ storage: value === 'metadata' ? 'metadata' : 'coding' });
  }

  protected setDatePrecision(value: string): void {
    this.update({ datePrecision: value === 'dateTime' ? 'dateTime' : 'date' });
  }

  protected setAnalysis(value: string): void {
    this.update({ textAnalysis: value === 'identifier' ? 'identifier' : 'prose' });
  }

  /** The choice editor saved a change: keep the editor and the list in step. */
  protected choicesChanged(field: AdminField): void {
    // Choice changes do not re-check whether documents hold values: keep what the editor knew.
    const before = this.current();
    this.editing.set({ ...field, hasValues: field.hasValues ?? before?.hasValues ?? null });
    void this.refreshList();
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const editing = this.editing();
    if (!editing || this.saving()) return;
    const form = this.form();
    const local: Record<string, string> = {};
    if (!form.name.trim()) local['name'] = 'Enter a field name.';
    if (form.type === 'decimal') {
      const p = Number(form.decimalPrecision);
      const s = Number(form.decimalScale);
      if (!Number.isInteger(p) || p < 1 || p > 18)
        local['decimalPrecision'] = 'Use 1 to 18 digits.';
      if (!Number.isInteger(s) || s < 0 || s > 6 || s > p)
        local['decimalScale'] = 'Use 0 to 6 digits, at most the total digits.';
    }
    this.errors.set(local);
    if (Object.keys(local).length) return;

    this.saving.set(true);
    this.saveError.set(null);
    const decimal = form.type === 'decimal';
    const multi = (form.type === 'text' || form.type === 'keyword') && form.multiValue;
    try {
      let saved: AdminField;
      if (editing === 'new') {
        saved = await this.api.createField({
          displayName: form.name.trim(),
          description: form.description.trim() || null,
          type: form.type,
          storage: form.storage,
          multiValue: multi,
          datePrecision: form.type === 'date' ? form.datePrecision : null,
          decimalPrecision: decimal ? Number(form.decimalPrecision) : null,
          decimalScale: decimal ? Number(form.decimalScale) : null,
          textAnalysis: form.type === 'text' ? form.textAnalysis : null,
          isSearchable: form.searchable,
          securityClass: form.securityClass
            ? (form.securityClass as (typeof SECURITY_CLASSES)[number]['value'])
            : null,
        });
        this.announcer.announce(`Field ${saved.displayName} created`);
      } else {
        const request: UpdateFieldRequest = {
          displayName: form.name.trim(),
          description: form.description.trim(),
          isHidden: form.hidden,
          type: form.type,
          multiValue: multi,
          datePrecision: form.type === 'date' ? form.datePrecision : null,
          decimalPrecision: decimal ? Number(form.decimalPrecision) : null,
          decimalScale: decimal ? Number(form.decimalScale) : null,
        };
        saved = await this.api.updateField(editing, request);
        this.announcer.announce(`Field ${saved.displayName} saved`);
      }
      await this.refreshList();
      if (isChoiceType(saved.type) && editing === 'new') {
        // A new choice field is useless without choices: keep the editor open on its choices.
        this.open(saved);
        afterNextRender(
          () =>
            document.querySelector<HTMLInputElement>('.fa__add-choice input')?.focus({
              preventScroll: false,
            }),
          { injector: this.injector },
        );
      } else {
        this.cancel();
      }
    } catch (e) {
      this.showSaveError(toApiError(e));
    } finally {
      this.saving.set(false);
    }
  }

  protected async retire(): Promise<void> {
    const field = this.current();
    if (!field || field.isSystem) return;
    const confirmed = await this.dialogs.confirm({
      title: `Retire ${field.displayName}?`,
      message:
        'The field leaves every coding layout, and its values are hidden from review, search and exports. ' +
        'Documents are not changed now; the values are removed later by a background job. This cannot be undone.',
      confirmLabel: 'Retire field',
      tone: 'danger',
      typedConfirmation: field.displayName,
    });
    if (!confirmed) return;
    try {
      await this.api.retireField(field);
      this.announcer.announce(`Field ${field.displayName} retired`);
      this.editing.set(null);
      await this.refreshList();
      afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
    } catch (e) {
      this.showSaveError(toApiError(e));
    }
  }

  private showSaveError(error: ApiError): void {
    const fields = (error.problem as { errors?: Record<string, string[]> }).errors;
    if (error.status === 400 && fields) {
      const mapped: Record<string, string> = {};
      for (const [key, messages] of Object.entries(fields)) {
        mapped[key === 'displayName' ? 'name' : key] = messages.join(' ');
      }
      this.errors.set(mapped);
      this.saveError.set('Correct the highlighted values.');
    } else if (error.status === 412) {
      this.saveError.set(
        'Someone else changed this field. Cancel and open it again to see the latest version.',
      );
    } else {
      this.saveError.set(error.problem.detail ?? error.message);
    }
  }

  private async refreshList(): Promise<void> {
    try {
      const [fields, capacity] = await Promise.all([this.api.fields(), this.api.capacity()]);
      this.fields.set(fields);
      this.capacity.set(capacity);
    } catch {
      // The list stays as it was; the next visit reloads it.
    }
  }
}
