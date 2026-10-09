import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import {
  Announcer,
  Badge,
  Button,
  Checkbox,
  DialogService,
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
  CodedValue,
  ConflictGroup,
  ConflictReport,
  HttpPrivilegeConflictApi,
  PrivilegeConflictApi,
  ResponsivenessField,
} from './privilege-conflicts-api';
import {
  REASON_DESCRIPTIONS,
  REASON_LABELS,
  defaultSource,
  documentsParams,
  groupTitle,
  propagationMessage,
  summaryText,
  valueText,
} from './privilege-conflicts-model';

/** The responsiveness field preselected when the workspace has one by this name (the default template's). */
const DEFAULT_RESPONSIVENESS = 'responsiveness';

/**
 * Searches › Privilege Conflicts (E13-T02, ticket-review note): an on-demand QC report of families with a withheld
 * member next to members that are not (Q-14), optionally families whose responsiveness calls differ, and duplicates
 * with different privilege calls, each member with its values and the reviewer who set them. Every group opens in
 * Documents (where conflicts are fixed in normal review); the report downloads as CSV through the protected-content
 * gateway; with Coding.Bulk and Coding.WritePrivilege, chosen duplicate groups can take one member's privilege call
 * ("Propagate privilege call…", one bulk job with provenance). Only documents the caller may see are checked, listed
 * or counted (Q-52), and the page says so.
 */
@Component({
  selector: 'opp-privilege-conflicts-page',
  imports: [
    Badge,
    Button,
    Checkbox,
    EmptyState,
    ErrorState,
    Icon,
    LoadingState,
    NgTemplateOutlet,
    RouterLink,
    Select,
  ],
  templateUrl: './privilege-conflicts-page.html',
  styleUrl: './privilege-conflicts-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: PrivilegeConflictApi, useClass: HttpPrivilegeConflictApi }],
})
export class PrivilegeConflictsPage {
  private readonly api = inject(PrivilegeConflictApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly router = inject(Router);
  private readonly timeZone = this.context.workspace.displayTimeZone || 'UTC';
  private propagationKey: string | null = null;

  protected readonly workspaceId = this.context.workspaceId;
  protected readonly canPropagate =
    this.context.can(PERMISSIONS.codingBulk) && this.context.can(PERMISSIONS.codingWritePrivilege);
  protected readonly reasonLabels = REASON_LABELS;
  protected readonly reasonDescriptions = REASON_DESCRIPTIONS;
  protected readonly title = groupTitle;
  protected readonly documentsParams = documentsParams;
  protected readonly valueText = valueText;

  protected readonly fields = signal<readonly ResponsivenessField[]>([]);
  /** The chosen responsiveness field id as the select's value ('' = do not check). */
  protected readonly responsiveness = signal('');
  protected readonly report = signal<ConflictReport | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  /** Duplicate groups chosen for propagation, and the chosen source per group. */
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly sources = signal<ReadonlyMap<string, string>>(new Map());
  protected readonly propagating = signal(false);
  protected readonly startedJob = signal<string | null>(null);

  protected readonly fieldOptions = computed<SelectOption[]>(() =>
    this.fields().map((f) => ({ value: String(f.fieldId), label: f.name })),
  );
  protected readonly families = computed(
    () => this.report()?.groups.filter((g) => g.kind === 'family') ?? [],
  );
  protected readonly duplicates = computed(
    () => this.report()?.groups.filter((g) => g.kind === 'duplicates') ?? [],
  );
  protected readonly showResponsiveness = computed(
    () => this.report()?.responsivenessFieldId != null,
  );
  protected readonly responsivenessName = computed(() => {
    const id = this.report()?.responsivenessFieldId;
    return this.fields().find((f) => f.fieldId === id)?.name ?? 'Responsiveness';
  });
  protected readonly summary = computed(() => {
    const r = this.report();
    return r
      ? summaryText(r.familyConflictCount, r.duplicateConflictCount, this.prefs.locale())
      : '';
  });
  protected readonly checkedAt = computed(() =>
    formatDate(this.report()?.generatedAt, this.prefs.locale(), this.timeZone),
  );
  protected readonly exportUrl = computed(() => this.api.exportUrl(this.fieldId()));
  /** Selected groups that have a source to copy from. */
  protected readonly ready = computed(() =>
    this.duplicates().filter(
      (g) => this.selected().has(g.groupId) && this.sources().has(g.groupId),
    ),
  );

  constructor() {
    void this.start();
  }

  private fieldId(): number | null {
    const v = this.responsiveness();
    return v === '' ? null : Number(v);
  }

  private async start(): Promise<void> {
    try {
      const fields = await this.api.responsivenessFields();
      this.fields.set(fields);
      const preset = fields.find((f) => f.name.toLowerCase() === DEFAULT_RESPONSIVENESS);
      if (preset) this.responsiveness.set(String(preset.fieldId));
    } catch {
      // Without the field list the report still runs, without the responsiveness check.
    }
    await this.run();
  }

  /** Runs the report (on demand: it is computed on the server each time). */
  protected async run(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const report = await this.api.report(this.fieldId());
      this.report.set(report);
      this.selected.set(new Set());
      this.sources.set(
        new Map(
          report.groups
            .filter((g) => g.kind === 'duplicates')
            .map((g) => [g.groupId, defaultSource(g)] as const)
            .filter((e): e is readonly [string, string] => e[1] !== null),
        ),
      );
      this.announcer.announce(`Report updated: ${this.summary()}.`);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected reviewer(value: CodedValue | null): string {
    if (!value?.changedBy) return '';
    const at = formatDate(value.changedAt, this.prefs.locale(), this.timeZone, {
      dateStyle: 'medium',
    });
    return at ? `by ${value.changedBy.displayName}, ${at}` : `by ${value.changedBy.displayName}`;
  }

  protected isSelected(group: ConflictGroup): boolean {
    return this.selected().has(group.groupId);
  }

  protected setSelected(group: ConflictGroup, on: boolean): void {
    const next = new Set(this.selected());
    if (on) next.add(group.groupId);
    else next.delete(group.groupId);
    this.selected.set(next);
  }

  protected sourceOf(group: ConflictGroup): string | null {
    return this.sources().get(group.groupId) ?? null;
  }

  protected setSource(group: ConflictGroup, documentId: string): void {
    this.sources.set(new Map(this.sources()).set(group.groupId, documentId));
  }

  /** "Propagate privilege call…": confirm, then one bulk coding job over the chosen groups. */
  protected async propagate(): Promise<void> {
    const groups = this.ready();
    if (groups.length === 0 || this.propagating()) return;
    const others = groups.reduce((n, g) => n + g.members.length - 1, 0);
    const confirmed = await this.dialogs.confirm({
      title: 'Propagate privilege call',
      message: propagationMessage(groups.length, others),
      confirmLabel: 'Start propagation',
    });
    if (!confirmed) return;
    this.propagating.set(true);
    this.propagationKey ??=
      globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    try {
      const job = await this.api.propagate(
        groups.map((g) => ({ duplicateGroupId: g.groupId, sourceDocumentId: this.sourceOf(g)! })),
        this.propagationKey,
      );
      this.propagationKey = null;
      this.startedJob.set(job.jobId);
      this.selected.set(new Set());
      this.toasts.show('Propagation started. Run the report again when the job has finished.', {
        tone: 'success',
        action: {
          label: 'View job',
          run: () => void this.router.navigate(['/w', this.workspaceId, 'jobs', job.jobId]),
        },
      });
    } catch (e) {
      this.toasts.show(toApiError(e).problem.detail ?? 'The propagation could not be started.', {
        tone: 'error',
      });
    } finally {
      this.propagating.set(false);
    }
  }
}
