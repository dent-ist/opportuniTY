import { _IdGenerator } from '@angular/cdk/a11y';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PreferenceStorage } from '../../../../core/preferences/preference-storage';
import { UiPreferences } from '../../../../core/preferences/ui-preferences';
import { SessionService } from '../../../../core/session/session';
import { WorkspaceContext } from '../../../../core/workspace/workspace-context';
import {
  Announcer,
  Badge,
  Button,
  DialogService,
  Icon,
  LoadingState,
  Select,
  SelectOption,
  TextField,
  ToastService,
} from '../../../../ui';
import type { PropagationScope } from '../../relationships/relationships-api';
import type {
  ApplyField,
  ApplyToRelatedData,
  ApplyToRelatedResult,
} from '../related/apply-to-related-dialog';
import { DocumentAccess, DocumentUnavailableError } from '../document-access';
import type { CodingEditor } from '../review-regions';
import {
  CodingAccessLossError,
  CodingApi,
  CodingConflictError,
  CodingLayout,
  CodingLayoutField,
  CodingRejectedError,
  CodingValue,
  DocumentCoding,
} from '../review-ports';
import {
  ACCESS_DIGITS,
  EditorKind,
  apiValue,
  displayValue,
  editorKind,
  fromDateTimeInput,
  isEmpty,
  isVisible,
  layoutFields,
  linesOf,
  sameValue,
  toDateTimeInput,
  toggleChoice,
  validate,
} from './coding-form';
import { AccessLossDialog, AccessLossData } from './access-loss-dialog';
import { CodingConflict, CodingDifference } from './coding-conflict';
import { PendingCoding } from './pending-coding';

/** A choice as a radio button or checkbox. */
interface ChoiceOption {
  readonly label: string;
  readonly value: string | boolean;
  readonly checked: boolean;
  readonly disabled: boolean;
  /** Access digit 1–9, or null past the ninth choice. */
  readonly digit: number | null;
}

/** A field on screen. */
interface FieldRow {
  readonly field: CodingLayoutField;
  readonly kind: EditorKind;
  readonly id: string;
  readonly editable: boolean;
  readonly value: CodingValue | undefined;
  readonly display: string;
  readonly error: string | null;
  readonly options: readonly ChoiceOption[];
  /** Why a field the reviewer could otherwise code is read-only. */
  readonly lockedReason: string | null;
}

interface SectionView {
  readonly title: string;
  readonly id: string;
  readonly rows: readonly FieldRow[];
}

const WRITE_PRIVILEGE = 'Coding.WritePrivilege';

/**
 * The coding pane of Review mode (E16-T05, familiarity guide §3.3), rendered from the coding layout the reviewer
 * chooses (remembered per user and workspace): its sections and fields in order, required (`*`) and conditional
 * fields, an editor per field type, and security-affecting fields marked "Affects access".
 *
 * - Choice fields with ≤ 15 choices are radio buttons (single) or checkboxes (multiple) with access digits (1)…(9):
 *   a digit toggles that choice while the field has focus. Longer lists get a filter box.
 * - Explicit save only: Save, Save & Next, Save & Previous (from the workspace) and Cancel; no autosave. A save
 *   sends the changed fields with `If-Match` and an Idempotency-Key per attempt. Required fields that are empty, or
 *   values that are not valid, block the save and the first one is focused; messages are linked to their fields.
 * - A stale version (someone else saved first) is never overwritten silently: the pane shows who changed what and
 *   when, and offers to reload their coding or to apply the reviewer's changes on top of it.
 * - After a save: "Saved · indexing" until search has the new version, then "Saved · searchable".
 * - Read-only without Coding.Write (values without inputs), per field when the layout marks it read-only, for
 *   security-affecting fields without Coding.WritePrivilege, and for fields read-only for the reviewer's role (the
 *   API's `editable`); a field hidden from the role is not in the API's answer and is not shown at all.
 * - Security-affecting changes (E16-T08): unsaved changes to them say they affect who may see the document. A save
 *   that would hide the document from the reviewer is refused by the API until they confirm it ("Save and lose
 *   access?"); after it, `accessLost` tells Review mode to move on. A document that stops being available (404)
 *   shows no coding and requests nothing more.
 */
@Component({
  selector: 'opp-review-coding',
  imports: [Badge, Button, CodingConflict, Icon, LoadingState, NgTemplateOutlet, Select, TextField],
  templateUrl: './coding-pane.html',
  styleUrl: './coding-pane.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pane__content coding' },
})
export class ReviewCoding implements CodingEditor {
  readonly documentId = input.required<string>();
  /** Coding.Write: without it the pane shows values without inputs (familiarity guide §3.3). */
  readonly canCode = input(false);
  /** Save & Next / Save & Previous: the workspace saves through `save()`, then moves. */
  readonly move = output<'next' | 'previous'>();
  /** Which relations the document has: "Apply to Family…" / "Apply to Duplicates…" show only then (E16-T10). */
  readonly relations = input<{ readonly family: boolean; readonly duplicates: boolean }>({
    family: false,
    duplicates: false,
  });
  /** The document's Control Number, as the Apply dialog names it. */
  readonly controlNumber = input<string | null>(null);
  /** Coding was applied to the family or duplicates (or a job was started for it). */
  readonly propagated = output<ApplyToRelatedResult>();
  /** A save the reviewer confirmed hid this document from them (its id): Review mode moves on (E16-T08). */
  readonly accessLost = output<string>();
  /**
   * Admin › Coding Layouts preview (E04-T06): render this layout instead of the user's layouts, exactly as reviewers
   * see it, without the save, status and Apply to Family controls. Values can be tried out but are never saved.
   */
  readonly previewLayout = input<CodingLayout | null>(null);
  protected readonly preview = computed(() => this.previewLayout() !== null);

  private readonly api = inject(CodingApi);
  private readonly pending = inject(PendingCoding, { optional: true });
  private readonly storage = inject(PreferenceStorage);
  private readonly context = inject(WorkspaceContext);
  private readonly session = inject(SessionService);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);
  private readonly injector = inject(Injector);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly access = inject(DocumentAccess, { optional: true });
  protected readonly uid = inject(_IdGenerator).getId('opp-coding-');
  protected readonly accessHint =
    'Affects access: changing it can change who may see this document.';

  private readonly layoutKey = `coding.layout.${this.context.workspaceId}`;
  protected readonly layouts = signal<readonly CodingLayout[] | null>(null);
  protected readonly layout = computed(() => {
    const all = this.layouts();
    if (!all?.length) return null;
    const chosen = this.storage.read<{ layoutId?: string }>(this.layoutKey)?.layoutId;
    return all.find((l) => l.id === chosen) ?? all[0];
  });
  protected readonly layoutOptions = computed<SelectOption[]>(() =>
    (this.layouts() ?? []).map((l) => ({ value: l.id, label: l.name })),
  );

  /** `unavailable`: it could not be loaded; `noAccess`: the document is no longer available to the reviewer. */
  protected readonly coding = signal<DocumentCoding | 'loading' | 'unavailable' | 'noAccess'>(
    'loading',
  );
  /** The reviewer's own confirmed save made the document unavailable to them (in this visit). */
  protected readonly lostHere = signal(false);
  /** The reviewer's unsaved values by query name (only fields they touched). */
  private readonly edits = signal<Readonly<Record<string, CodingValue>>>({});
  private readonly errors = signal<ReadonlyMap<string, string>>(new Map());
  private readonly filters = signal<Readonly<Record<string, string>>>({});
  /** The Tab stop of each long checkbox list (roving focus). */
  private readonly roving = signal<Readonly<Record<string, number>>>({});
  protected readonly saving = signal(false);
  /** A message about the last save that is not about one field. */
  protected readonly message = signal<string | null>(null);
  /** The document's current coding after a 412: someone else saved first. */
  protected readonly conflict = signal<DocumentCoding | null>(null);
  /** The reviewer saved the displayed document in this visit (the indexing badge shows). */
  private readonly savedHere = signal(false);
  /** Fields saved in this visit: Apply to Family… offers them ticked. */
  private readonly savedFields = signal<ReadonlySet<string>>(new Set());
  /** "Apply to Family…" / "Apply to Duplicates…" are offered. */
  protected readonly canApply = computed(
    () =>
      !this.preview() &&
      this.canCode() &&
      this.stateKind() === 'ready' &&
      (this.relations().family || this.relations().duplicates) &&
      this.rows().some((r) => r.editable),
  );
  private seq = 0;
  private inFlight: Promise<boolean> | null = null;

  private readonly base = computed(() => {
    const c = this.coding();
    return typeof c === 'string' ? {} : c.values;
  });

  readonly dirty = computed(() => {
    const base = this.base();
    return Object.entries(this.edits()).some(([q, v]) => !sameValue(v, base[q]));
  });

  protected readonly stateKind = computed(() => {
    const c = this.coding();
    return typeof c === 'string' ? c : 'ready';
  });

  private readonly canWritePrivilege = this.context.can(WRITE_PRIVILEGE);

  protected readonly sections = computed<SectionView[]>(() => {
    const layout = this.layout();
    const coding = this.coding();
    if (!layout || typeof coding === 'string') return [];
    const value = (q: string) => this.value(q);
    const errors = this.errors();
    let n = 0;
    return layout.sections
      .map((section, s) => ({
        title: section.title,
        id: `${this.uid}-s${s}`,
        rows: section.fields
          .filter((field) => isVisible(field, value) && this.listed(field, coding))
          .map((field) => this.row(field, coding, errors.get(field.queryName) ?? null, n++)),
      }))
      .filter((s) => s.rows.length > 0);
  });

  /** Fields on screen in layout order (Alt+Shift+C, then n jumps to the n-th). */
  private readonly rows = computed(() => this.sections().flatMap((s) => s.rows));
  /** Labels of the security-affecting fields with unsaved changes ("Affects access", E16-T08). */
  protected readonly accessChanges = computed(() => {
    const base = this.base();
    const edits = this.edits();
    return this.rows()
      .filter(
        (r) =>
          r.field.securityAffecting &&
          r.field.queryName in edits &&
          !sameValue(edits[r.field.queryName], base[r.field.queryName]),
      )
      .map((r) => r.field.label);
  });
  /** Save actions: kept while the next document loads, so focus on them survives Save & Next. */
  protected readonly showActions = computed(
    () =>
      !this.preview() &&
      this.canCode() &&
      (this.stateKind() !== 'ready' || this.rows().some((r) => r.editable)),
  );

  /** The indexing badge: after the reviewer's save, until search has it, and once it has. */
  protected readonly indexBadge = computed(() => {
    if (this.saving()) return 'saving';
    const c = this.coding();
    if (typeof c === 'string') return null;
    // The reviewer's own save of this document in this list, also when they come back to it.
    return this.pending?.state(c.documentId) ?? (this.savedHere() ? c.indexState : null);
  });

  protected readonly differences = computed<CodingDifference[]>(() => {
    const current = this.conflict();
    if (!current) return [];
    return layoutFields(this.layout())
      .filter((f) => !sameValue(current.values[f.queryName], this.value(f.queryName)))
      .map((f) => ({
        label: f.label,
        theirs: displayValue(f, current.values[f.queryName]),
        yours: displayValue(f, this.value(f.queryName)),
      }));
  });

  protected readonly conflictTitle = computed(() => {
    const editor = this.conflict()?.lastEditor;
    if (!editor) return 'Changed by someone else';
    const who = editor.jobId ? 'a Mass Edit job' : (editor.displayName ?? 'another reviewer');
    return `Changed by ${who} at ${this.formatTime(editor.changedAt)}`;
  });

  constructor() {
    // The document stops being available while open (another user's change, a new wall, a deletion): its coding
    // goes away with it, unsaved edits included, and nothing more is requested.
    effect(() => {
      const id = this.documentId();
      if (!this.access?.isUnavailable(id)) return;
      untracked(() => {
        if (this.coding() === 'noAccess') return;
        this.seq++;
        this.showNoAccess();
      });
    });
    this.api.layouts().then(
      (layouts) => this.previewLayout() || this.layouts.set(layouts),
      () => this.previewLayout() || this.layouts.set([]),
    );
    effect(() => {
      const preview = this.previewLayout();
      if (preview) this.layouts.set([preview]);
    });
    effect(() => {
      const id = this.documentId();
      untracked(() => this.load(id));
    });
  }

  // ── CodingEditor (Save & Next, Save & Previous, the unsaved-changes prompt) ───────────────────────────────

  /** Saves the edits; false when they could not be saved and a move must not happen. */
  save(): Promise<boolean> {
    this.inFlight ??= this.trySave().finally(() => (this.inFlight = null));
    return this.inFlight;
  }

  /** Cancel: reverts every unsaved edit. */
  discard(): void {
    const had = this.dirty();
    this.edits.set({});
    this.errors.set(new Map());
    this.message.set(null);
    this.conflict.set(null);
    if (had) this.announcer.announce('Unsaved coding changes cancelled.');
  }

  /** Focuses field `n` (1-based, layout order, fields on screen). */
  focusField(n: number): void {
    const row = this.rows()[n - 1];
    if (!row) return;
    // At once when the field is on screen, so a digit typed right after lands in it; else after rendering.
    if (!this.focusRow(row.field.queryName)) {
      afterNextRender(() => this.focusRow(row.field.queryName), { injector: this.injector });
    }
  }

  /**
   * Apply to Family… / Apply to Duplicates… (E16-T10, Q-14): unsaved edits are saved first, then the dialog previews and
   * applies this document's saved values of the chosen fields.
   */
  async applyTo(scope: PropagationScope): Promise<void> {
    const relations = this.relations();
    if (!this.canApply()) {
      this.announcer.announce(
        !this.canCode()
          ? 'You cannot change coding in this workspace.'
          : 'This document has no family or duplicates to apply coding to.',
      );
      return;
    }
    if (scope === 'family' && !relations.family) scope = 'duplicates';
    if (scope === 'duplicates' && !relations.duplicates) scope = 'family';
    if (this.dirty() && !(await this.save())) return;
    const coding = this.coding();
    if (typeof coding === 'string') return;
    const saved = this.savedFields();
    const fields: ApplyField[] = this.rows()
      .filter((r) => r.editable)
      .map((r) => ({
        queryName: r.field.queryName,
        label: r.field.label,
        display: r.display,
        empty: isEmpty(r.value),
        securityAffecting: r.field.securityAffecting,
        // Q-48: the layout's "apply to family by default" fields, and those saved in this visit, are offered ticked;
        // security-affecting fields never are.
        preselected:
          (r.field.applyToFamilyByDefault || saved.has(r.field.queryName)) &&
          !r.field.securityAffecting,
      }));
    const { ApplyToRelatedDialog } = await import('../related/apply-to-related-dialog');
    const ref = this.dialogs.open<ApplyToRelatedResult, ApplyToRelatedData>(ApplyToRelatedDialog, {
      data: {
        sourceDocumentId: coding.documentId,
        controlNumber: this.controlNumber() ?? coding.documentId,
        scope,
        fields,
        hasFamily: relations.family,
        hasDuplicates: relations.duplicates,
      },
      width: '44rem',
      injector: this.injector,
    });
    const result = await firstValueFrom(ref.closed);
    if (!result) return;
    this.propagated.emit(result);
    if (result.mode === 'interactive') {
      const n = new Intl.NumberFormat(this.prefs.locale());
      const docs = result.applied === 1 ? 'document' : 'documents';
      const skipped = result.skipped > 0 ? ` ${n.format(result.skipped)} skipped.` : '';
      this.toasts.show(`Coding applied to ${n.format(result.applied)} ${docs}.${skipped}`, {
        tone: result.skipped > 0 ? 'warning' : 'success',
      });
    }
  }

  // ── Editing ──────────────────────────────────────────────────────────────────────────────────────────────

  protected chooseLayout(id: string): void {
    const layout = this.layouts()?.find((l) => l.id === id);
    if (!layout) return;
    this.storage.write(this.layoutKey, { layoutId: id });
    this.errors.set(new Map());
    this.announcer.announce(`Layout ${layout.name}.`);
  }

  protected set(field: CodingLayoutField, value: CodingValue): void {
    this.edits.update((e) => ({ ...e, [field.queryName]: value }));
    if (this.errors().has(field.queryName)) {
      const next = new Map(this.errors());
      next.delete(field.queryName);
      this.errors.set(next);
    }
  }

  protected setText(field: CodingLayoutField, text: string): void {
    this.set(field, text === '' ? null : text);
  }

  protected setLines(field: CodingLayoutField, event: Event): void {
    const lines = linesOf((event.target as HTMLTextAreaElement).value);
    this.set(field, lines.length ? lines : null);
  }

  protected setDateTime(field: CodingLayoutField, text: string): void {
    this.set(field, fromDateTimeInput(text));
  }

  protected setLongText(field: CodingLayoutField, event: Event): void {
    this.setText(field, (event.target as HTMLTextAreaElement).value);
  }

  protected choose(row: FieldRow, option: ChoiceOption): void {
    if (option.disabled) return;
    if (row.kind === 'yesNo') {
      this.set(row.field, row.value === option.value ? null : (option.value as boolean));
    } else {
      this.set(row.field, toggleChoice(row.field, row.value, option.value as string));
    }
  }

  /** Radio buttons report only selection; a single choice is cleared with its Clear button or its digit. */
  protected pick(row: FieldRow, option: ChoiceOption): void {
    if (row.value !== option.value) this.choose(row, option);
  }

  /** Digits 1–9 toggle the field's n-th choice while focus is in the field (not in its filter box). */
  protected onChoiceKey(event: KeyboardEvent, row: FieldRow): void {
    if (row.kind === 'filterMulti' && this.moveInList(event)) return;
    const digit = /^(Digit|Numpad)([1-9])$/.exec(event.code);
    if (!digit || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if ((event.target as HTMLElement).matches('input[type="search"]')) return;
    const option = row.options.find((o) => o.digit === Number(digit[2]));
    event.preventDefault();
    if (!option || option.disabled) return;
    this.choose(row, option);
    const inputs = (event.currentTarget as HTMLElement).querySelectorAll<HTMLInputElement>(
      'input:not([type="search"])',
    );
    inputs[row.options.indexOf(option)]?.focus();
  }

  protected rovingIndex(row: FieldRow): number {
    return Math.min(this.roving()[row.field.queryName] ?? 0, Math.max(0, row.options.length - 1));
  }

  protected setRoving(row: FieldRow, index: number): void {
    this.roving.update((r) => ({ ...r, [row.field.queryName]: index }));
  }

  /** Up / Down / Home / End between the checkboxes of a long list; true when the key was handled. */
  private moveInList(event: KeyboardEvent): boolean {
    const target = event.target as HTMLElement;
    if (!target.matches('input[type="checkbox"]')) return false;
    const inputs = [
      ...(event.currentTarget as HTMLElement).querySelectorAll<HTMLInputElement>(
        'input[type="checkbox"]',
      ),
    ];
    const at = inputs.indexOf(target as HTMLInputElement);
    const next = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: inputs.length - 1 }[
      event.key as 'ArrowDown'
    ];
    if (next === undefined) return false;
    event.preventDefault();
    inputs[Math.max(0, Math.min(inputs.length - 1, next))]?.focus();
    return true;
  }

  protected setFilter(field: CodingLayoutField, event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.filters.update((f) => ({ ...f, [field.queryName]: text }));
  }

  protected filterOf(field: CodingLayoutField): string {
    return this.filters()[field.queryName] ?? '';
  }

  protected chosenSummary(row: FieldRow): string {
    return Array.isArray(row.value) ? row.value.join(', ') : String(row.value ?? '');
  }

  protected userOptions(row: FieldRow): SelectOption[] {
    const me = this.session.principal();
    const options: SelectOption[] = [];
    if (me) options.push({ value: me.userId, label: `Me (${me.displayName ?? 'you'})` });
    const current = typeof row.value === 'string' ? row.value : null;
    if (current && current !== me?.userId) {
      options.push({ value: current, label: `Another user (${current.slice(0, 8)}…)` });
    }
    return options;
  }

  /** Spoken with a choice field: which digits pick its choices. */
  protected keysHint(row: FieldRow, single: boolean): string {
    const last = Math.max(...row.options.map((o) => o.digit ?? 0));
    if (last < 1) return '';
    const keys = last === 1 ? 'Key 1' : `Keys 1 to ${last}`;
    if (single) return `${keys} choose; the same key again clears.`;
    const arrows = row.kind === 'filterMulti' ? ' Up and Down arrows move between choices.' : '';
    return `${keys} check or clear a choice.${arrows}`;
  }

  protected areaDescribedBy(row: FieldRow, multi: boolean): string | null {
    const ids = [
      multi || row.field.securityAffecting ? `${row.id}-hint` : null,
      row.error ? `${row.id}-error` : null,
    ];
    return ids.filter(Boolean).join(' ') || null;
  }

  protected lines(value: CodingValue | undefined): string {
    return Array.isArray(value) ? value.join('\n') : typeof value === 'string' ? value : '';
  }

  protected text(value: CodingValue | undefined): string {
    return value === null || value === undefined ? '' : String(value);
  }

  protected dateTime(value: CodingValue | undefined): string {
    return toDateTimeInput(value);
  }

  // ── Conflict (412) ───────────────────────────────────────────────────────────────────────────────────────

  /** Their coding replaces the reviewer's edits. */
  protected reloadTheirs(): void {
    const current = this.conflict();
    if (!current) return;
    this.coding.set(current);
    this.edits.set({});
    this.errors.set(new Map());
    this.conflict.set(null);
    this.message.set(null);
    this.announcer.announce('The current coding is shown. Your changes were discarded.');
    this.focusField(1);
  }

  /** The reviewer's changed fields are saved on top of the current version (their other fields stay). */
  protected async overwrite(): Promise<void> {
    const current = this.conflict();
    if (!current) return;
    this.coding.set(current);
    this.conflict.set(null);
    await this.save();
  }

  // ── Internals ────────────────────────────────────────────────────────────────────────────────────────────

  private value(queryName: string): CodingValue | undefined {
    const edits = this.edits();
    return queryName in edits ? edits[queryName] : this.base()[queryName];
  }

  /**
   * A layout field the API's answer for this document does not list is hidden from the reviewer's role (a field
   * restriction): it is not shown. An answer listing no field at all says nothing either way.
   */
  private listed(field: CodingLayoutField, coding: DocumentCoding): boolean {
    const listed = Object.keys(coding.fields);
    return listed.length === 0 || listed.includes(field.queryName);
  }

  private showNoAccess(): void {
    // A field or button with focus goes away with the form: focus stays in the pane's region.
    const region = this.host.closest<HTMLElement>('[data-command-region]');
    if (this.host.contains(this.host.ownerDocument.activeElement)) region?.focus();
    this.coding.set('noAccess');
    this.edits.set({});
    this.errors.set(new Map());
    this.conflict.set(null);
    this.saving.set(false);
  }

  private load(documentId: string): void {
    const seq = ++this.seq;
    // A field with focus goes away while the next document loads: focus waits on the pane, then returns to the
    // same field, so code → Save & Next → code continues without the mouse.
    const active = this.host.ownerDocument.activeElement;
    const field = active?.closest('.coding__form [data-coding-field]');
    const refocus = field?.getAttribute('data-coding-field') ?? null;
    if (field) this.host.closest<HTMLElement>('[data-command-region]')?.focus();
    this.coding.set('loading');
    this.edits.set({});
    this.errors.set(new Map());
    this.filters.set({});
    this.roving.set({});
    this.message.set(null);
    this.conflict.set(null);
    this.savedHere.set(false);
    this.savedFields.set(new Set());
    this.lostHere.set(false);
    if (this.access?.isUnavailable(documentId)) {
      this.coding.set('noAccess');
      return;
    }
    this.api.get(documentId).then(
      (coding) => {
        if (seq !== this.seq) return;
        this.coding.set(coding);
        if (refocus) {
          afterNextRender(
            () => {
              const region = this.host.closest('[data-command-region]');
              const waiting = this.host.ownerDocument.activeElement === region;
              if (waiting && !this.focusRow(refocus)) this.focusField(1);
            },
            { injector: this.injector },
          );
        }
      },
      (e: unknown) => {
        if (seq !== this.seq) return;
        if (!(e instanceof DocumentUnavailableError)) return this.coding.set('unavailable');
        this.access?.markUnavailable(documentId);
        this.showNoAccess();
      },
    );
  }

  private async trySave(): Promise<boolean> {
    const coding = this.coding();
    const layout = this.layout();
    if (typeof coding === 'string' || !layout) return !this.dirty();
    if (!this.dirty()) {
      this.announcer.announce('No changes to save.');
      return true;
    }
    const fields = layoutFields(layout);
    const editable = this.rows()
      .filter((r) => r.editable)
      .map((r) => r.field);
    const errors = validate(editable, (q) => this.value(q));
    this.errors.set(errors);
    if (errors.size > 0) {
      this.message.set(null);
      this.focusFirstError(errors);
      this.announcer.announce(
        errors.size === 1
          ? 'The coding was not saved: one field needs attention.'
          : `The coding was not saved: ${errors.size} fields need attention.`,
      );
      return false;
    }
    const base = this.base();
    const changes: Record<string, CodingValue> = {};
    for (const field of fields) {
      const edits = this.edits();
      if (field.queryName in edits && !sameValue(edits[field.queryName], base[field.queryName])) {
        changes[field.queryName] = apiValue(field, edits[field.queryName]);
      }
    }
    if (Object.keys(changes).length === 0) {
      this.edits.set({});
      return true;
    }

    const seq = this.seq;
    this.saving.set(true);
    this.message.set(null);
    const send = (confirmAccessLoss: boolean) =>
      this.api.save(coding.documentId, coding.version, changes, {
        layoutId: layout.serverId,
        idempotencyKey: newIdempotencyKey(),
        ...(confirmAccessLoss ? { confirmAccessLoss } : {}),
      });
    try {
      let saved: DocumentCoding;
      try {
        saved = await send(false);
      } catch (e) {
        if (!(e instanceof CodingAccessLossError) || seq !== this.seq) throw e;
        // Nothing was saved: the reviewer decides whether to give up their own access (E16-T08).
        this.saving.set(false);
        const confirmed = await this.confirmAccessLoss(changes);
        if (seq !== this.seq) return false;
        if (!confirmed) {
          this.message.set(
            'Not saved: this change would end your access to the document. Change your edits, or cancel them.',
          );
          this.announcer.announce('Not saved. Your changes are kept.');
          return false;
        }
        this.saving.set(true);
        saved = await send(true);
      }
      if (saved.accessLost) {
        if (seq === this.seq) this.afterAccessLost(saved.documentId);
        return true;
      }
      this.pending?.track(saved);
      if (seq === this.seq) {
        this.coding.set(saved);
        this.edits.set({});
        this.errors.set(new Map());
        this.savedHere.set(true);
        this.savedFields.update((s) => new Set([...s, ...Object.keys(changes)]));
      }
      return true;
    } catch (e) {
      if (seq !== this.seq) return false;
      if (e instanceof CodingConflictError) {
        this.conflict.set(e.current);
        this.announcer.announce(`${this.conflictTitle()}. Your changes were not saved.`, {
          politeness: 'assertive',
        });
        afterNextRender(() => this.host.querySelector<HTMLElement>('.coding__conflict')?.focus(), {
          injector: this.injector,
        });
      } else if (e instanceof CodingRejectedError) {
        const fieldErrors = new Map(Object.entries(e.fieldErrors));
        this.errors.set(fieldErrors);
        // A message about a field that is not on screen (not in this layout, or hidden by its condition, such as
        // the Privilege Basis a Withhold needs) is shown with the save's message instead of under the field.
        const shown = new Set(this.rows().map((r) => r.field.queryName));
        const elsewhere = [...fieldErrors].filter(([q]) => !shown.has(q)).map(([, m]) => m);
        this.message.set([e.message, ...elsewhere].join(' '));
        if (fieldErrors.size > elsewhere.length) this.focusFirstError(fieldErrors);
      } else if (e instanceof DocumentUnavailableError) {
        this.access?.markUnavailable(coding.documentId);
        this.showNoAccess();
        this.announcer.announce(
          'This document is no longer available. Your changes could not be saved.',
          { politeness: 'assertive' },
        );
      } else {
        this.message.set('The coding could not be saved. Check your connection and try again.');
      }
      return false;
    } finally {
      this.saving.set(false);
    }
  }

  /** Asks the reviewer to confirm a save that ends their own access; true when they confirm. */
  private async confirmAccessLoss(
    changes: Readonly<Record<string, CodingValue>>,
  ): Promise<boolean> {
    const fields = this.rows()
      .filter((r) => r.field.securityAffecting && r.field.queryName in changes)
      .map((r) => r.field.label);
    const ref = this.dialogs.open<boolean, AccessLossData>(AccessLossDialog, {
      role: 'alertdialog',
      autoFocus: '[data-autofocus]',
      data: { controlNumber: this.controlNumber(), fields },
    });
    return (await firstValueFrom(ref.closed)) === true;
  }

  /** The confirmed save stands and the document is gone for the reviewer: nothing of it stays on screen. */
  private afterAccessLost(documentId: string): void {
    this.access?.markUnavailable(documentId);
    this.showNoAccess();
    this.lostHere.set(true);
    const name = this.controlNumber() ?? 'this document';
    this.announcer.announce(`Saved. You no longer have access to ${name}.`, {
      politeness: 'assertive',
    });
    this.accessLost.emit(documentId);
  }

  private row(
    field: CodingLayoutField,
    coding: DocumentCoding,
    error: string | null,
    index: number,
  ): FieldRow {
    const value = this.value(field.queryName);
    const state = coding.fields[field.queryName];
    const permitted = state ? state.editable : !field.securityAffecting || this.canWritePrivilege;
    const editable = this.canCode() && !field.readOnly && permitted;
    const kind = editorKind(field);
    return {
      field,
      kind,
      id: `${this.uid}-f${index}`,
      editable,
      value,
      display: displayValue(field, value),
      error,
      options: this.options(field, kind, value),
      lockedReason:
        !this.canCode() || field.readOnly || permitted
          ? null
          : field.securityAffecting
            ? 'Changing it needs the privilege coding permission.'
            : 'Read-only for your role.',
    };
  }

  private options(
    field: CodingLayoutField,
    kind: EditorKind,
    value: CodingValue | undefined,
  ): ChoiceOption[] {
    if (kind === 'yesNo') {
      return [true, false].map((v, i) => ({
        label: v ? 'Yes' : 'No',
        value: v,
        checked: value === v,
        disabled: false,
        digit: i + 1,
      }));
    }
    if (field.type !== 'singleChoice' && field.type !== 'multiChoice') return [];
    const chosen = Array.isArray(value) ? value : isEmpty(value) ? [] : [String(value)];
    const filter =
      kind === 'filterSingle' || kind === 'filterMulti'
        ? this.filterOf(field).trim().toLowerCase()
        : '';
    return field.choices
      .filter((c) => c.active || chosen.includes(c.name))
      .filter((c) => !filter || c.name.toLowerCase().includes(filter))
      .map((c, i) => ({
        label: c.active ? c.name : `${c.name} (inactive)`,
        value: c.name,
        checked: chosen.includes(c.name),
        disabled: !c.active && !chosen.includes(c.name),
        digit: i < ACCESS_DIGITS ? i + 1 : null,
      }));
  }

  private focusFirstError(errors: ReadonlyMap<string, string>): void {
    const first = this.rows().find((r) => errors.has(r.field.queryName));
    if (first)
      afterNextRender(() => this.focusRow(first.field.queryName), { injector: this.injector });
  }

  private focusRow(queryName: string): boolean {
    const el = this.host.querySelector<HTMLElement>(
      `[data-coding-field="${CSS.escape(queryName)}"]`,
    );
    const target =
      el?.querySelector<HTMLElement>('input:checked:not(:disabled)') ??
      el?.querySelector<HTMLElement>(
        'input:not(:disabled):not([type="search"]), textarea:not(:disabled), select:not(:disabled)',
      ) ??
      el;
    target?.focus();
    return !!target;
  }

  private formatTime(iso: string): string {
    const at = new Date(iso);
    if (Number.isNaN(at.getTime())) return iso;
    const sameDay = at.toDateString() === new Date().toDateString();
    return new Intl.DateTimeFormat(
      this.prefs.locale(),
      sameDay
        ? { hour: 'numeric', minute: '2-digit' }
        : { dateStyle: 'medium', timeStyle: 'short' },
    ).format(at);
  }
}

/** A fresh key per save attempt (the adapter reuses it for its one automatic retry). */
function newIdempotencyKey(): string {
  return (
    globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
  );
}
