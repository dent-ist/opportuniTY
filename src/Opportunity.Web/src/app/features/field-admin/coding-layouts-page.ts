import {
  CdkDrag,
  CdkDragDrop,
  CdkDragHandle,
  CdkDropList,
  CdkDropListGroup,
} from '@angular/cdk/drag-drop';
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
import type { CodingLayoutResource, FieldResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { AdminLayout, FieldAdminApi, HttpFieldAdminApi } from '../../core/fields/field-admin-api';
import {
  Announcer,
  Badge,
  Button,
  Checkbox,
  DialogService,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
  Select,
  SelectOption,
  TextField,
} from '../../ui';
import {
  DraftCondition,
  DraftField,
  DraftSection,
  LAYOUT_ROLES,
  LayoutDraft,
  applyToFamilyBlocked,
  draftOf,
  isChoiceType,
  moved,
  newField,
  newSection,
  previewOf,
  requestOf,
  storageLabel,
  typeLabel,
  validateDraft,
} from './field-admin-model';
import { LayoutPreview } from './layout-preview';

/**
 * Admin › Coding Layouts (E04-T06, familiarity guide §3.3): which fields the coding pane shows, in which sections and
 * order, which are required, read-only or shown only while another field has a value, which are pre-selected for
 * Apply to Family (Q-48) and which roles may use the layout. Fields and sections are reordered by dragging their
 * handle or, equally, with Move up / Move down, Alt+Arrow keys and "Move to section" (WCAG 2.5.7). The preview beside
 * the editor is the coding pane itself, so it shows exactly what reviewers will see. Saving sends If-Match and is
 * audited; the default layout is always available and cannot be deleted.
 */
@Component({
  selector: 'opp-coding-layouts-page',
  imports: [
    Badge,
    Button,
    CdkDrag,
    CdkDragHandle,
    CdkDropList,
    CdkDropListGroup,
    Checkbox,
    ErrorState,
    Icon,
    IconButton,
    LayoutPreview,
    LoadingState,
    Select,
    TextField,
  ],
  templateUrl: './coding-layouts-page.html',
  styleUrl: './field-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: FieldAdminApi, useClass: HttpFieldAdminApi }],
  host: { class: 'fa-page' },
})
export class CodingLayoutsPage {
  private readonly api = inject(FieldAdminApi);
  private readonly dialogs = inject(DialogService);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly nameField = viewChild<TextField>('nameField');

  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly layouts = signal<readonly CodingLayoutResource[]>([]);
  protected readonly fields = signal<readonly FieldResource[]>([]);
  protected readonly fieldMap = computed(
    () => new Map(this.fields().map((f) => [Number(f.fieldId), f] as const)),
  );

  /** The layout being edited (null for a new one); undefined when none is open. */
  protected readonly selected = signal<AdminLayout | null | undefined>(undefined);
  protected readonly draft = signal<LayoutDraft>(draftOf(null));
  private readonly original = signal('');
  protected readonly saving = signal(false);
  protected readonly opening = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saveError = signal<string | null>(null);

  protected readonly roles = LAYOUT_ROLES;
  protected readonly typeLabel = typeLabel;
  protected readonly storageLabel = storageLabel;

  protected readonly dirty = computed(
    () =>
      this.selected() !== undefined && JSON.stringify(requestOf(this.draft())) !== this.original(),
  );
  protected readonly preview = computed(() => previewOf(this.draft(), this.fields()));
  protected readonly placed = computed(
    () => new Set(this.draft().sections.flatMap((s) => s.fields.map((f) => f.fieldId))),
  );
  /** Fields not yet in the layout, by name, for the "Add field" pickers. */
  protected readonly available = computed<SelectOption[]>(() =>
    this.fields()
      .filter((f) => !this.placed().has(Number(f.fieldId)))
      .sort((a, b) => a.displayName.localeCompare(b.displayName))
      .map((f) => ({
        value: String(f.fieldId),
        label: `${f.displayName} (${typeLabel(f.type, f.datePrecision)}, ${f.isSystem ? 'System' : storageLabel(f.storage)})`,
      })),
  );
  protected readonly sectionOptions = computed<SelectOption[]>(() =>
    this.draft().sections.map((s, i) => ({
      value: s.key,
      label: s.title.trim() || `Section ${i + 1}`,
    })),
  );
  protected readonly dropListIds = computed(() => this.draft().sections.map((s) => s.key));

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const [layouts, fields] = await Promise.all([this.api.layouts(), this.api.fields()]);
      this.layouts.set(layouts);
      this.fields.set(fields);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  // ── Choosing a layout ────────────────────────────────────────────────────────────────────────────────────

  protected isCurrent(layout: CodingLayoutResource): boolean {
    return this.selected()?.layoutId === layout.layoutId;
  }

  protected async open(layout: CodingLayoutResource): Promise<void> {
    if (this.isCurrent(layout) || !(await this.confirmDiscard())) return;
    this.opening.set(true);
    try {
      this.start(await this.api.layout(layout.layoutId));
    } catch (e) {
      this.saveError.set(toApiError(e).problem.detail ?? 'The layout could not be opened.');
    } finally {
      this.opening.set(false);
    }
  }

  protected async startNew(): Promise<void> {
    if (!(await this.confirmDiscard())) return;
    this.start(null);
  }

  private start(layout: AdminLayout | null): void {
    const draft = draftOf(layout);
    this.selected.set(layout);
    this.draft.set(draft);
    this.original.set(JSON.stringify(requestOf(draft)));
    this.errors.set({});
    this.saveError.set(null);
    afterNextRender(() => this.nameField()?.focus(), { injector: this.injector });
  }

  private async confirmDiscard(): Promise<boolean> {
    if (!this.dirty()) return true;
    return this.dialogs.confirm({
      title: 'Discard unsaved changes?',
      message: 'The changes to this layout have not been saved.',
      confirmLabel: 'Discard changes',
      tone: 'danger',
    });
  }

  protected async close(): Promise<void> {
    if (!(await this.confirmDiscard())) return;
    this.selected.set(undefined);
  }

  // ── Layout attributes ────────────────────────────────────────────────────────────────────────────────────

  protected update(patch: Partial<LayoutDraft>): void {
    this.draft.update((d) => ({ ...d, ...patch }));
  }

  protected toggleRole(key: string, on: boolean): void {
    this.draft.update((d) => ({
      ...d,
      roles: on ? [...d.roles.filter((r) => r !== key), key] : d.roles.filter((r) => r !== key),
    }));
  }

  // ── Sections ─────────────────────────────────────────────────────────────────────────────────────────────

  protected addSection(): void {
    const section = newSection('');
    this.draft.update((d) => ({ ...d, sections: [...d.sections, section] }));
    this.announcer.announce('Section added at the end.');
    this.focusIn(`[data-section="${section.key}"] input`);
  }

  protected renameSection(key: string, title: string): void {
    this.setSections((sections) => sections.map((s) => (s.key === key ? { ...s, title } : s)));
  }

  protected moveSection(index: number, delta: number): void {
    const sections = this.draft().sections;
    const to = index + delta;
    if (to < 0 || to >= sections.length) return;
    const section = sections[index];
    this.setSections((list) => moved(list, index, to));
    this.announcer.announce(
      `Section ${section.title || index + 1} moved to position ${to + 1} of ${sections.length}.`,
    );
    this.focusIn(
      `[data-section="${section.key}"] button[data-action="section-${delta < 0 ? 'up' : 'down'}"]:not([disabled])`,
      `[data-section="${section.key}"] input`,
    );
  }

  protected async removeSection(index: number): Promise<void> {
    const section = this.draft().sections[index];
    if (section.fields.length) {
      const confirmed = await this.dialogs.confirm({
        title: `Remove ${section.title || 'this section'}?`,
        message: `Its ${section.fields.length} field(s) leave the layout too. Nothing changes until you save.`,
        confirmLabel: 'Remove section',
        tone: 'danger',
      });
      if (!confirmed) return;
    }
    this.setSections((list) => list.filter((_, i) => i !== index));
    this.announcer.announce(`Section ${section.title || index + 1} removed.`);
    this.focusIn('button[data-action="add-section"]');
  }

  // ── Fields ───────────────────────────────────────────────────────────────────────────────────────────────

  protected fieldOf(id: number): FieldResource | undefined {
    return this.fieldMap().get(id);
  }

  protected labelOf(id: number): string {
    return this.fieldOf(id)?.displayName ?? `Field ${id}`;
  }

  protected isCoding(id: number): boolean {
    return this.fieldOf(id)?.storage === 'coding';
  }

  /** The field picked in each section's "Add field" list, by section key. */
  protected readonly picks = signal<Readonly<Record<string, string>>>({});

  protected pick(sectionKey: string, value: string): void {
    this.picks.update((p) => ({ ...p, [sectionKey]: value }));
  }

  protected addField(sectionKey: string): void {
    const id = Number(this.picks()[sectionKey] ?? '');
    if (!id) return;
    this.picks.update((p) => ({ ...p, [sectionKey]: '' }));
    const field = newField(id, !this.isCoding(id));
    this.setSections((list) =>
      list.map((s) => (s.key === sectionKey ? { ...s, fields: [...s.fields, field] } : s)),
    );
    this.announcer.announce(`${this.labelOf(id)} added.`);
  }

  protected setField(sectionKey: string, fieldId: number, patch: Partial<DraftField>): void {
    this.setSections((list) =>
      list.map((s) =>
        s.key === sectionKey
          ? { ...s, fields: s.fields.map((f) => (f.fieldId === fieldId ? { ...f, ...patch } : f)) }
          : s,
      ),
    );
  }

  protected setReadOnly(sectionKey: string, field: DraftField, readOnly: boolean): void {
    this.setField(sectionKey, field.fieldId, {
      readOnly,
      required: readOnly ? false : field.required,
      applyToFamily: readOnly ? false : field.applyToFamily,
    });
  }

  protected familyBlocked(field: DraftField): string | null {
    return applyToFamilyBlocked(this.fieldOf(field.fieldId), field.readOnly);
  }

  protected moveField(sectionIndex: number, index: number, delta: number): void {
    const section = this.draft().sections[sectionIndex];
    const to = index + delta;
    if (to < 0 || to >= section.fields.length) return;
    const field = section.fields[index];
    this.setSections((list) =>
      list.map((s, i) => (i === sectionIndex ? { ...s, fields: moved(s.fields, index, to) } : s)),
    );
    this.announceField(field.fieldId, to, section.fields.length, section.title);
    this.focusIn(
      `[data-layout-field="${field.fieldId}"] button[data-action="${delta < 0 ? 'up' : 'down'}"]:not([disabled])`,
      `[data-layout-field="${field.fieldId}"] button:not([disabled])`,
    );
  }

  protected moveToSection(fromKey: string, fieldId: number, toKey: string): void {
    if (fromKey === toKey) return;
    const sections = this.draft().sections;
    const field = sections
      .find((s) => s.key === fromKey)
      ?.fields.find((f) => f.fieldId === fieldId);
    const target = sections.find((s) => s.key === toKey);
    if (!field || !target) return;
    this.setSections((list) =>
      list.map((s) =>
        s.key === fromKey
          ? { ...s, fields: s.fields.filter((f) => f.fieldId !== fieldId) }
          : s.key === toKey
            ? { ...s, fields: [...s.fields, field] }
            : s,
      ),
    );
    this.announcer.announce(
      `${this.labelOf(fieldId)} moved to ${target.title || 'the section'}, position ${target.fields.length + 1}.`,
    );
    this.focusIn(`[data-layout-field="${fieldId}"] select`);
  }

  protected removeField(sectionKey: string, fieldId: number): void {
    // Conditions that depended on the removed field go with it.
    this.setSections((list) =>
      list.map((s) => ({
        ...s,
        fields: s.fields
          .filter((f) => !(s.key === sectionKey && f.fieldId === fieldId))
          .map((f) => (f.condition?.fieldId === fieldId ? { ...f, condition: null } : f)),
      })),
    );
    this.announcer.announce(`${this.labelOf(fieldId)} removed from the layout.`);
    this.focusIn(`[data-section="${sectionKey}"] [data-action="add-field"] select`);
  }

  protected onFieldKeydown(event: KeyboardEvent, sectionIndex: number, index: number): void {
    if (!event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    event.preventDefault();
    this.moveField(sectionIndex, index, event.key === 'ArrowUp' ? -1 : 1);
  }

  protected drop(event: CdkDragDrop<string>): void {
    const fromKey = event.previousContainer.data;
    const toKey = event.container.data;
    const sections = this.draft().sections;
    const from = sections.find((s) => s.key === fromKey);
    const to = sections.find((s) => s.key === toKey);
    if (!from || !to) return;
    const field = from.fields[event.previousIndex];
    if (!field) return;
    if (fromKey === toKey) {
      if (event.previousIndex === event.currentIndex) return;
      this.setSections((list) =>
        list.map((s) =>
          s.key === fromKey
            ? { ...s, fields: moved(s.fields, event.previousIndex, event.currentIndex) }
            : s,
        ),
      );
    } else {
      this.setSections((list) =>
        list.map((s) => {
          if (s.key === fromKey)
            return { ...s, fields: s.fields.filter((_, i) => i !== event.previousIndex) };
          if (s.key === toKey) {
            const fields = [...s.fields];
            fields.splice(event.currentIndex, 0, field);
            return { ...s, fields };
          }
          return s;
        }),
      );
    }
    this.announceField(
      field.fieldId,
      event.currentIndex,
      to.fields.length + (fromKey === toKey ? 0 : 1),
      to.title,
    );
  }

  // ── Conditions ───────────────────────────────────────────────────────────────────────────────────────────

  /** Fields of the layout a condition can depend on (choice and Yes/No fields other than this one). */
  protected conditionOptions(fieldId: number): SelectOption[] {
    return [
      { value: '', label: 'Always shown' },
      ...this.draft()
        .sections.flatMap((s) => s.fields)
        .filter((f) => f.fieldId !== fieldId)
        .map((f) => this.fieldOf(f.fieldId))
        .filter((f): f is FieldResource => !!f && (f.type === 'boolean' || isChoiceType(f.type)))
        .map((f) => ({ value: String(f.fieldId), label: `When ${f.displayName} is…` })),
    ];
  }

  protected setConditionField(sectionKey: string, field: DraftField, value: string): void {
    const controlling = value ? this.fieldOf(Number(value)) : undefined;
    const condition: DraftCondition | null = controlling
      ? {
          fieldId: Number(controlling.fieldId),
          choiceIds: controlling.type === 'boolean' ? null : [],
          booleanValue: controlling.type === 'boolean' ? true : null,
        }
      : null;
    this.setField(sectionKey, field.fieldId, { condition });
  }

  protected conditionChoices(field: DraftField): { id: number; name: string; checked: boolean }[] {
    const condition = field.condition;
    const controlling = condition ? this.fieldOf(condition.fieldId) : undefined;
    return (controlling?.choices ?? []).map((c) => ({
      id: Number(c.choiceId),
      name: c.isActive ? c.name : `${c.name} (inactive)`,
      checked: condition?.choiceIds?.includes(Number(c.choiceId)) ?? false,
    }));
  }

  protected toggleConditionChoice(
    sectionKey: string,
    field: DraftField,
    choiceId: number,
    on: boolean,
  ): void {
    const condition = field.condition;
    if (!condition) return;
    const ids = (condition.choiceIds ?? []).filter((id) => id !== choiceId);
    this.setField(sectionKey, field.fieldId, {
      condition: { ...condition, choiceIds: on ? [...ids, choiceId] : ids },
    });
  }

  protected setConditionBoolean(sectionKey: string, field: DraftField, value: string): void {
    if (!field.condition) return;
    this.setField(sectionKey, field.fieldId, {
      condition: { ...field.condition, booleanValue: value === 'true' },
    });
  }

  protected isBooleanCondition(field: DraftField): boolean {
    return !!field.condition && this.fieldOf(field.condition.fieldId)?.type === 'boolean';
  }

  // ── Save and delete ──────────────────────────────────────────────────────────────────────────────────────

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const selected = this.selected();
    if (selected === undefined || this.saving()) return;
    const draft = this.draft();
    const local = validateDraft(draft, this.fieldMap());
    this.errors.set(local);
    if (Object.keys(local).length) {
      this.saveError.set('Correct the problems shown in the layout.');
      return;
    }
    this.saving.set(true);
    this.saveError.set(null);
    try {
      const request = requestOf(draft);
      const saved = selected
        ? await this.api.updateLayout(selected, request)
        : await this.api.createLayout(request);
      this.announcer.announce(`Layout ${saved.name} saved`);
      this.layouts.set(await this.api.layouts());
      this.start(saved);
    } catch (e) {
      const error = toApiError(e);
      const fields = (error.problem as { errors?: Record<string, string[]> }).errors;
      if (error.status === 400 && fields) {
        this.errors.set(
          Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, v.join(' ')])),
        );
        this.saveError.set('Correct the problems shown in the layout.');
      } else if (error.status === 412) {
        this.saveError.set(
          'Someone else changed this layout. Open it again to see their version; your changes are kept on screen until then.',
        );
      } else {
        this.saveError.set(error.problem.detail ?? error.message);
      }
    } finally {
      this.saving.set(false);
    }
  }

  protected async remove(): Promise<void> {
    const layout = this.selected();
    if (!layout || layout.isDefault) return;
    const confirmed = await this.dialogs.confirm({
      title: `Delete ${layout.name}?`,
      message:
        'Reviewers who use it switch to the default layout. Coding already saved is not affected.',
      confirmLabel: 'Delete layout',
      tone: 'danger',
    });
    if (!confirmed) return;
    try {
      await this.api.deleteLayout(layout);
      this.announcer.announce(`Layout ${layout.name} deleted`);
      this.selected.set(undefined);
      this.layouts.set(await this.api.layouts());
      this.focusIn('button[data-action="new-layout"]');
    } catch (e) {
      const error = toApiError(e);
      this.saveError.set(
        error.status === 412
          ? 'Someone else changed this layout. Open it again before deleting it.'
          : (error.problem.detail ?? 'The layout was not deleted.'),
      );
    }
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────

  private setSections(
    change: (sections: readonly DraftSection[]) => readonly DraftSection[],
  ): void {
    this.draft.update((d) => ({ ...d, sections: change(d.sections) }));
  }

  private announceField(fieldId: number, to: number, count: number, section: string): void {
    this.announcer.announce(
      `${this.labelOf(fieldId)} moved to position ${to + 1} of ${count}${section ? ' in ' + section : ''}.`,
    );
  }

  /** Focuses the first match of the selectors after the next render (focus follows the moved item). */
  private focusIn(...selectors: string[]): void {
    afterNextRender(
      () => {
        for (const selector of selectors) {
          const target = this.host.querySelector<HTMLElement>(selector);
          if (target) {
            target.focus();
            return;
          }
        }
      },
      { injector: this.injector },
    );
  }
}
