import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { _IdGenerator } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import type { FieldResource } from '../../../core/api/generated/models';
import {
  ApiError,
  UserFacingError,
  describeError,
  toApiError,
} from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { Announcer, Button, DialogLayout, Icon, LoadingState, TextField } from '../../../ui';
import { formatTime } from '../grid/grid-format';
import { SelectionTarget } from '../grid/selection';
import { BulkCodingApi, BulkFieldChange, FrozenSet, isFinished } from './bulk-coding-api';
import { MassEditField } from './mass-edit-field';
import { MassEditProgress } from './mass-edit-progress';
import {
  EditableField,
  FieldEdit,
  NO_EDIT,
  TYPED_CONFIRMATION_THRESHOLD,
  confirmationText,
  describeEdit,
  editProblem,
  editableFields,
  matchesConfirmation,
  toChanges,
} from './field-edits';
import { MASS_EDIT_POLL_MS, MassEditJobs, TrackedJob, outcomeText } from './mass-edit-jobs';

export interface MassEditData {
  readonly target: SelectionTarget;
  /** The list's count label, to explain how the frozen count differs. */
  readonly listCount: string;
  readonly fields: readonly FieldResource[];
  readonly canWritePrivilege: boolean;
  /** Raw generation numbers are for admin and support roles only (Q-10). */
  readonly showGeneration: boolean;
  readonly timeZone: string;
}

type Step = 'edit' | 'confirm' | 'progress';

/** The Q-07 rule, word for word in every Mass Edit confirmation (familiarity guide §3.6). */
export const SKIP_RULE =
  'Documents whose changed fields are edited by someone else after this job starts will be skipped and listed.';

const STEPS: readonly { step: Step; label: string }[] = [
  { step: 'edit', label: 'Fields' },
  { step: 'confirm', label: 'Confirm' },
  { step: 'progress', label: 'Progress' },
];

/**
 * Mass Edit (E16-T06, familiarity guide §3.6): code many documents at once.
 *
 * 1. **Fields**: each coding field has a "Change" checkbox; unchecked fields stay untouched. Single-value fields are
 *    set or cleared; each choice of a Multiple Choice field is added, removed or left unchanged, or all values are
 *    replaced.
 * 2. **Confirm**: the selection is frozen first (a snapshot, ADR-002), then the frozen count, when it was frozen
 *    (and the generation, for admin and support roles, Q-10), why it may differ from the list count, a warning when
 *    documents were selected while still indexing, the Q-07 skip rule and the change summary. Above 10,000 documents
 *    or for a security-affecting field the count must be typed (Q-34).
 * 3. **Progress**: submitting queues the job and returns at once; progress is Saved n/N, then Searchable, and the
 *    outcome is Updated / Skipped / Failed. Closing the dialog leaves the job running; a toast reports the end.
 *
 * Keyboard: Tab through the controls, Space toggles a Change checkbox, arrows pick a radio, Escape closes.
 */
@Component({
  selector: 'opp-mass-edit-dialog',
  imports: [Button, DialogLayout, Icon, LoadingState, MassEditField, MassEditProgress, TextField],
  templateUrl: './mass-edit-dialog.html',
  styleUrl: './mass-edit-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MassEditDialog {
  protected readonly data = inject<MassEditData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
  private readonly api = inject(BulkCodingApi);
  private readonly jobs = inject(MassEditJobs);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);
  private readonly pollMs = inject(MASS_EDIT_POLL_MS);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  protected readonly ids = inject(_IdGenerator).getId('opp-mass-edit-');

  protected readonly steps = STEPS;
  protected readonly skipRule = SKIP_RULE;
  protected readonly step = signal<Step>('edit');
  protected readonly fields = editableFields(this.data.fields, this.data.canWritePrivilege);
  protected readonly edits = signal<Readonly<Record<string, FieldEdit>>>({});
  private readonly submitted = signal(false);

  // Freezing and submitting
  protected readonly busy = signal<'freezing' | 'submitting' | null>(null);
  protected readonly error = signal<UserFacingError | null>(null);
  protected readonly frozen = signal<FrozenSet | null>(null);
  protected readonly typed = signal('');
  private submissionKey = '';
  private destroyed = false;
  protected readonly tracked = signal<TrackedJob | null>(null);

  protected readonly n = computed(() => new Intl.NumberFormat(this.prefs.locale()));

  protected readonly changed = computed(() =>
    this.fields.filter((f) => this.edit(f.fieldId).change),
  );
  protected readonly problems = computed(() => {
    const edits = this.edits();
    return new Map(
      this.fields.flatMap((f) => {
        const p = editProblem(f, edits[f.fieldId] ?? NO_EDIT);
        return p ? [[f.fieldId, p] as const] : [];
      }),
    );
  });
  protected readonly shownProblems = computed(() =>
    this.submitted() ? this.problems() : new Map<string, string>(),
  );
  protected readonly summary = computed(() =>
    this.changed().map((f) => describeEdit(f, this.edit(f.fieldId))),
  );
  private readonly changes = computed<BulkFieldChange[]>(() =>
    this.changed().flatMap((f) => toChanges(f, this.edit(f.fieldId)) ?? []),
  );
  protected readonly securityFields = computed(() =>
    this.changed().filter((f) => f.field.isSecurityAffecting),
  );

  // Confirmation
  protected readonly count = computed(() => this.frozen()?.documentCount ?? 0);
  protected readonly needsTyping = computed(
    () => this.count() > TYPED_CONFIRMATION_THRESHOLD || this.securityFields().length > 0,
  );
  protected readonly typingReason = computed(() => {
    const security = this.securityFields();
    if (security.length) {
      const names = security.map((f) => f.field.displayName).join(', ');
      return `Required because ${names} affects who may see documents. Mass edits cannot be undone.`;
    }
    return `Required for more than ${this.n().format(TYPED_CONFIRMATION_THRESHOLD)} documents. Mass edits cannot be undone.`;
  });
  protected readonly confirmLabel = computed(
    () => `Type ${confirmationText(this.count())} to confirm`,
  );
  protected readonly canApply = computed(
    () =>
      this.count() > 0 &&
      this.busy() === null &&
      (!this.needsTyping() || matchesConfirmation(this.typed(), this.count())),
  );
  protected readonly frozenAt = computed(() => {
    const at = this.frozen()?.frozenAt;
    return at ? formatTime(at, this.prefs.locale(), this.data.timeZone) : '—';
  });
  protected readonly frozenLine = computed(() => {
    const f = this.frozen();
    const generation =
      this.data.showGeneration && f?.searchGeneration
        ? ` · generation ${this.n().format(Number(f.searchGeneration))}`
        : '';
    return `Frozen at ${this.frozenAt()}${generation}`;
  });
  /** How the frozen count relates to what the list showed (UI finding 11: the frozen count is the one acted on). */
  protected readonly difference = computed(() => {
    const target = this.data.target;
    const count = this.count();
    if (target.kind === 'documents') {
      const checked = target.documentIds.length;
      return count === checked
        ? `All ${this.n().format(checked)} checked documents are in the frozen set.`
        : `You checked ${this.n().format(checked)} documents; ${this.n().format(count)} of them are in the frozen set. The others are no longer available to you or cannot be coded by you.`;
    }
    const related = this.frozen()?.related ?? 0;
    const included =
      related > 0
        ? ` It includes ${this.n().format(related)} related ${related === 1 ? 'document' : 'documents'} (family, duplicates or email thread) added before freezing.`
        : '';
    return `The list showed ${this.data.listCount} documents. The frozen set holds the documents that matched the search when it was frozen and that you may code, so imports, coding changes and access changes since the list loaded can make the numbers differ.${included} Only the frozen documents change.`;
  });

  // Progress
  private readonly job = computed(() => this.tracked()?.job() ?? null);
  protected readonly outcome = computed(() => {
    const job = this.job();
    return job && isFinished(job) ? outcomeText(job, this.prefs.locale()) : '';
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => {
      this.destroyed = true;
      const tracked = this.tracked();
      // Still running: the Documents page keeps following it and reports the end in a toast.
      if (tracked) tracked.attached = false;
    });
    effect(() => {
      const outcome = this.outcome();
      if (outcome) untracked(() => this.announcer.announce(`Mass Edit finished. ${outcome}`));
    });
  }

  protected edit(fieldId: string): FieldEdit {
    return this.edits()[fieldId] ?? NO_EDIT;
  }

  /** Applies a field's change; `choices` holds only the choices that changed and is merged. */
  protected update(f: EditableField, patch: Partial<FieldEdit>): void {
    const current = this.edit(f.fieldId);
    let next: FieldEdit = {
      ...current,
      ...patch,
      choices: { ...current.choices, ...patch.choices },
    };
    if (
      patch.change &&
      !current.change &&
      f.field.type === 'multiChoice' &&
      current.mode === 'set'
    ) {
      next = { ...next, mode: 'addRemove' };
    }
    this.edits.update((all) => ({ ...all, [f.fieldId]: next }));
  }

  // ── Steps ─────────────────────────────────────────────────────────────────────────────────────────────────

  /** Fields → Confirm: checks the edits, then freezes the selection (once; Back and Continue reuse it). */
  protected async continue(): Promise<void> {
    if (this.busy()) return;
    this.submitted.set(true);
    this.error.set(null);
    if (this.changed().length === 0) {
      this.error.set({
        title: 'Nothing to change',
        detail: 'Tick Change for at least one field.',
        retryable: false,
      });
      this.announcer.announce('Tick Change for at least one field.', { politeness: 'assertive' });
      return;
    }
    const problems = this.problems();
    if (problems.size > 0) {
      const first = this.fields.find((f) => problems.has(f.fieldId))!;
      this.announcer.announce(`${f(first)}: ${problems.get(first.fieldId)}`, {
        politeness: 'assertive',
      });
      this.focus(`[data-field="${first.fieldId}"] input, [data-field="${first.fieldId}"] select`);
      return;
    }
    this.submissionKey = newKey();
    this.typed.set('');
    if (!this.frozen()) {
      const frozen = await this.freeze();
      if (!frozen) return;
      this.frozen.set(frozen);
    }
    this.go('confirm');
    const count = this.count();
    this.announcer.announce(
      count === 0
        ? 'Frozen: none of the selected documents can be mass edited by you.'
        : `Frozen: ${this.n().format(count)} documents. Review and confirm.`,
    );
  }

  private async freeze(): Promise<FrozenSet | null> {
    this.busy.set('freezing');
    this.ref.disableClose = true;
    try {
      let set = await this.api.freeze(this.data.target, newKey());
      while (set.status === 'materializing' && !this.destroyed) {
        await new Promise((r) => setTimeout(r, this.pollMs));
        set = await this.api.frozenSet(set.snapshotId);
      }
      if (set.status !== 'ready') {
        this.fail({
          title: 'The selection could not be frozen',
          detail: set.statusReason ?? 'Try again. If it keeps failing, narrow the selection.',
          retryable: true,
        });
        return null;
      }
      return set;
    } catch (e) {
      this.fail(describeError(toApiError(e)));
      return null;
    } finally {
      this.busy.set(null);
      this.ref.disableClose = false;
    }
  }

  protected back(): void {
    this.error.set(null);
    this.go('edit');
  }

  /** Confirm → Progress: queues the job (the frozen set and the changes) and follows it. */
  protected async apply(event?: Event): Promise<void> {
    event?.preventDefault();
    const frozen = this.frozen();
    if (!frozen || !this.canApply()) return;
    this.busy.set('submitting');
    this.error.set(null);
    this.ref.disableClose = true;
    let jobId: string;
    try {
      jobId = await this.api.submit(
        { snapshotId: frozen.snapshotId, changes: this.changes() },
        this.submissionKey,
      );
    } catch (e) {
      this.fail(describeError(toApiError(e) as ApiError));
      return;
    } finally {
      this.busy.set(null);
      this.ref.disableClose = false;
    }
    this.tracked.set(this.jobs.track(jobId, frozen.documentCount ?? 0));
    this.go('progress');
    this.announcer.announce(
      'Mass Edit submitted. You can close this dialog; the job keeps running and you are told when it finishes.',
    );
  }

  private fail(error: UserFacingError): void {
    this.error.set(error);
    this.announcer.announce(`${error.title}. ${error.detail}`, { politeness: 'assertive' });
  }

  private go(step: Step): void {
    this.step.set(step);
    // Focus moves to the new step: its typed confirmation when one is needed, else its heading.
    const target =
      step === 'edit'
        ? '[data-step-heading]'
        : step === 'confirm' && this.needsTyping()
          ? '[data-typed] input'
          : '[data-step-heading]';
    this.focus(target);
  }

  private focus(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), {
      injector: this.injector,
    });
  }

  protected stepIndex(step: Step): number {
    return STEPS.findIndex((s) => s.step === step);
  }
}

function f(field: EditableField): string {
  return field.field.displayName;
}

function newKey(): string {
  return (
    globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
  );
}
