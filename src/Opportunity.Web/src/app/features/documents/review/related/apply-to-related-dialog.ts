import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toApiError } from '../../../../core/api/problem-details';
import { JobFeed } from '../../../../core/jobs/job-feed';
import { UiPreferences } from '../../../../core/preferences/ui-preferences';
import { WorkspaceContext } from '../../../../core/workspace/workspace-context';
import { Announcer, Badge, Button, DialogLayout, Icon, LoadingState } from '../../../../ui';
import {
  PropagationPreview,
  PropagationScope,
  PropagationStaleError,
  RelationshipsApi,
} from '../../relationships/relationships-api';

/** A coding field the dialog offers, with the source document's (saved) value. */
export interface ApplyField {
  readonly queryName: string;
  readonly label: string;
  /** The value as shown ("Not set" when empty: applying it clears the field on the targets). */
  readonly display: string;
  readonly empty: boolean;
  readonly securityAffecting: boolean;
  /** Ticked when the dialog opens (fields just saved; never security-affecting ones, Q-48). */
  readonly preselected: boolean;
}

export interface ApplyToRelatedData {
  readonly sourceDocumentId: string;
  readonly controlNumber: string;
  readonly scope: PropagationScope;
  readonly fields: readonly ApplyField[];
  readonly hasFamily: boolean;
  readonly hasDuplicates: boolean;
}

/** Applied at once, or started as a job (the dialog then stays open with the link to it until closed). */
export type ApplyToRelatedResult =
  | { readonly mode: 'interactive'; readonly applied: number; readonly skipped: number }
  | { readonly mode: 'job'; readonly jobId: string };

type Step = 'choose' | 'preview' | 'job';

const SCOPES: readonly { value: PropagationScope; label: string }[] = [
  { value: 'family', label: 'Family' },
  { value: 'duplicates', label: 'Duplicates' },
  { value: 'familyAndDuplicates', label: 'Family and duplicates' },
];

/**
 * Apply to Family… / Apply to Duplicates… (E16-T10 with E09-T05, Q-14, Q-48): an explicit action, never automatic on
 * save. The reviewer picks the fields whose saved values of this document are applied, previews how many documents
 * change and which are already coded differently (current → new value), and applies. Documents the reviewer may not
 * see are never listed. Above the threshold the change runs as a Mass Edit job (Q-07: documents someone else edits
 * after it starts are skipped and listed), followed in the job tray and on the Jobs page.
 */
@Component({
  selector: 'opp-apply-to-related-dialog',
  imports: [Badge, Button, DialogLayout, Icon, LoadingState, RouterLink],
  templateUrl: './apply-to-related-dialog.html',
  styleUrl: './apply-to-related-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApplyToRelatedDialog {
  protected readonly data = inject<ApplyToRelatedData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ApplyToRelatedResult>>(DialogRef);
  private readonly api = inject(RelationshipsApi);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);
  private readonly feed = inject(JobFeed, { optional: true });
  protected readonly workspaceId = inject(WorkspaceContext).workspaceId;

  protected readonly scopes = SCOPES.filter(
    (s) =>
      (s.value !== 'duplicates' || this.data.hasDuplicates) &&
      (s.value !== 'family' || this.data.hasFamily) &&
      (s.value !== 'familyAndDuplicates' || (this.data.hasFamily && this.data.hasDuplicates)),
  );
  protected readonly scope = signal<PropagationScope>(this.data.scope);
  protected readonly selected = signal<ReadonlySet<string>>(
    new Set(this.data.fields.filter((f) => f.preselected).map((f) => f.queryName)),
  );
  protected readonly step = signal<Step>('choose');
  protected readonly busy = signal(false);
  protected readonly preview = signal<PropagationPreview | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly stale = signal(false);
  protected readonly jobId = signal<string | null>(null);
  /** Security-affecting fields need an explicit acknowledgement (they can change who may see the targets). */
  protected readonly acknowledged = signal(false);
  private idempotencyKey = newKey();

  protected readonly title = computed(() => {
    switch (this.scope()) {
      case 'duplicates':
        return 'Apply to Duplicates';
      case 'familyAndDuplicates':
        return 'Apply to Family and Duplicates';
      default:
        return 'Apply to Family';
    }
  });
  protected readonly chosenFields = computed(() =>
    this.data.fields.filter((f) => this.selected().has(f.queryName)),
  );
  protected readonly affectsAccess = computed(() =>
    this.chosenFields().some((f) => f.securityAffecting),
  );
  protected readonly labels = computed(
    () => new Map(this.data.fields.map((f) => [f.queryName.toLowerCase(), f.label])),
  );
  protected readonly applyLabel = computed(() => {
    const p = this.preview();
    if (!p) return 'Apply';
    const n = this.count(p.targetCount);
    const docs = p.targetCount === 1 ? 'document' : 'documents';
    return p.mode === 'job' ? `Start job for ${n} ${docs}` : `Apply to ${n} ${docs}`;
  });
  protected readonly canApply = computed(() => {
    const p = this.preview();
    return (
      !!p && p.targetCount > 0 && !this.busy() && (!this.affectsAccess() || this.acknowledged())
    );
  });

  protected count(n: number): string {
    return new Intl.NumberFormat(this.prefs.locale()).format(n);
  }

  protected toggle(queryName: string, checked: boolean): void {
    const next = new Set(this.selected());
    if (checked) next.add(queryName);
    else next.delete(queryName);
    this.selected.set(next);
  }

  protected fieldLabel(queryName: string): string {
    return this.labels().get(queryName.toLowerCase()) ?? queryName;
  }

  protected values(values: readonly string[]): string {
    return values.length ? values.join('; ') : 'Not set';
  }

  protected restrictedText(n: number): string {
    return n === 1
      ? '1 restricted item is not changed.'
      : `${this.count(n)} restricted items are not changed.`;
  }

  /** Counts and conflicts of the chosen fields and scope (nothing changes yet). */
  protected async runPreview(): Promise<void> {
    if (this.chosenFields().length === 0) {
      this.error.set('Choose at least one field to apply.');
      this.announcer.announce('Choose at least one field to apply.', { politeness: 'assertive' });
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.stale.set(false);
    this.preview.set(null);
    this.step.set('preview');
    try {
      const preview = await this.api.preview({
        sourceDocumentId: this.data.sourceDocumentId,
        scope: this.scope(),
        fields: this.chosenFields().map((f) => f.queryName),
      });
      this.preview.set(preview);
      this.idempotencyKey = newKey();
      this.announcer.announce(this.summary(preview));
    } catch (e) {
      const error = toApiError(e);
      this.error.set(
        error.status === 403
          ? 'You do not have permission to apply these fields.'
          : 'The preview could not be made. Try again.',
      );
      this.announcer.announce(this.error()!, { politeness: 'assertive' });
    } finally {
      this.busy.set(false);
    }
  }

  protected back(): void {
    this.step.set('choose');
    this.preview.set(null);
    this.error.set(null);
    this.stale.set(false);
    this.acknowledged.set(false);
  }

  protected async apply(): Promise<void> {
    const preview = this.preview();
    if (!preview || !this.canApply()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await this.api.apply(preview.previewId, this.idempotencyKey);
      if (result.mode === 'interactive') {
        this.ref.close({ mode: 'interactive', applied: result.applied, skipped: result.skipped });
        return;
      }
      this.feed?.observe(result.job);
      this.jobId.set(result.job.jobId);
      this.step.set('job');
      this.announcer.announce(
        'The change runs as a Mass Edit job. Follow it in the job tray or on the Jobs page.',
      );
    } catch (e) {
      if (e instanceof PropagationStaleError) {
        this.stale.set(true);
        this.error.set(
          `The coding of ${this.data.controlNumber} changed, or this preview is more than 10 minutes old. Preview again before applying.`,
        );
      } else {
        const error = toApiError(e);
        this.error.set(
          error.status === 403
            ? 'You do not have permission to apply these fields.'
            : 'The change could not be applied. Try again.',
        );
      }
      this.announcer.announce(this.error()!, { politeness: 'assertive' });
    } finally {
      this.busy.set(false);
    }
  }

  protected close(): void {
    const id = this.jobId();
    this.ref.close(id ? { mode: 'job', jobId: id } : undefined);
  }

  private summary(p: PropagationPreview): string {
    if (p.targetCount === 0) return 'No other documents to apply to.';
    const parts = [`Applies to ${this.count(p.targetCount)} documents.`];
    if (p.conflictCount > 0)
      parts.push(`${this.count(p.conflictCount)} already coded differently.`);
    if (p.mode === 'job') parts.push('Runs as a Mass Edit job.');
    return parts.join(' ');
  }
}

function newKey(): string {
  return (
    globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
  );
}
