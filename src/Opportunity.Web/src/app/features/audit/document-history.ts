import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import type { CodingHistoryEntryResource, FieldResource } from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  Button,
  EmptyState,
  ErrorState,
  Icon,
  LoadingState,
  Select,
  type SelectOption,
} from '../../ui';
import { DocumentHistoryApi, HttpDocumentHistoryApi, valueText } from './document-history-api';
import { DisplayFormat } from '../../core/i18n';

interface HistoryRow {
  readonly id: string;
  readonly when: string;
  readonly field: string;
  readonly before: string | null;
  readonly after: string | null;
  readonly skipped: boolean;
  readonly by: string;
  readonly source: 'interactive' | 'job' | 'propagated';
  readonly sourceLabel: string;
  readonly jobId: string | null;
  readonly automated: string | null;
}

/**
 * Document History, a viewer tab (E14-T04, familiarity guide §3.3 "Document History"): who changed which coding field
 * of the document, from what to what and when, and how: interactively, by a Mass Edit or other job (linked to the
 * job), or propagated from a family or duplicate (Apply to Family / Duplicates). A bulk change that was skipped because
 * someone else edited the field first (Q-07) says so. Newest first, one page at a time; a field filter narrows the
 * list. Built from the coding history API, which never lists fields the reviewer may not see. Reading it is not
 * audited (it holds coding, not content); auditors get a link to the document's full audit trail.
 */
@Component({
  selector: 'opp-document-history',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState, RouterLink, Select],
  template: `<div class="viewer-tools">
      <opp-select
        class="history__field"
        label="Field"
        placeholder="All fields"
        [options]="fieldOptions()"
        [value]="fieldId() ?? ''"
        (valueChange)="chooseField($event)"
      />
      <span class="viewer-tools__count" role="status">{{ countText() }}</span>
      @if (canReadAudit && controlNumber()) {
        <a
          class="history__audit"
          [routerLink]="['/w', workspaceId, 'admin', 'audit']"
          [queryParams]="{ document: controlNumber() }"
          ><opp-icon name="report" />Audit trail of this document</a
        >
      }
    </div>
    @if (loading() && rows().length === 0) {
      <opp-loading-state label="Loading coding history…" />
    } @else if (error() && rows().length === 0) {
      <opp-error-state [error]="error()!" (retry)="load()" />
    } @else if (rows().length === 0) {
      <opp-empty-state
        icon="clock"
        [title]="fieldId() ? 'No changes to this field' : 'No coding changes yet'"
        message="Coding saved for this document, by reviewers or by jobs, is listed here."
      />
    } @else {
      <div class="history__scroll" tabindex="0" role="group" [attr.aria-label]="caption()">
        <table class="history" [attr.data-history-document]="documentId()">
          <caption class="opp-visually-hidden">
            {{
              caption()
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">When</th>
              <th scope="col">Field</th>
              <th scope="col">Change</th>
              <th scope="col">By</th>
              <th scope="col">How</th>
            </tr>
          </thead>
          <tbody>
            @for (row of rows(); track row.id) {
              <tr>
                <td class="history__nowrap">{{ row.when }}</td>
                <th scope="row">{{ row.field }}</th>
                <td>
                  @if (row.skipped) {
                    <opp-badge tone="warning" icon="warning">Not changed</opp-badge>
                    <span class="history__note">
                      Kept {{ row.before ?? 'empty' }}: someone else had changed it after the job
                      started (it would have set {{ row.after ?? 'empty' }}).</span
                    >
                  } @else {
                    <span class="history__change">
                      <span [class.history__empty]="row.before === null">{{
                        row.before ?? 'Empty'
                      }}</span>
                      <span aria-hidden="true">→</span
                      ><span class="opp-visually-hidden">changed to</span>
                      <strong [class.history__empty]="row.after === null">{{
                        row.after ?? 'Empty'
                      }}</strong>
                    </span>
                  }
                </td>
                <td class="history__nowrap">
                  {{ row.by }}
                  @if (row.automated) {
                    <opp-badge tone="info">{{ row.automated }}</opp-badge>
                  }
                </td>
                <td class="history__nowrap">
                  @if (row.jobId) {
                    <a [routerLink]="['/w', workspaceId, 'jobs', row.jobId]">{{
                      row.sourceLabel
                    }}</a>
                  } @else {
                    {{ row.sourceLabel }}
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      @if (error()) {
        <opp-error-state [error]="error()!" (retry)="loadMore()" />
      }
      @if (nextCursor()) {
        <div>
          <button type="button" oppButton="secondary" [busy]="loadingMore()" (click)="loadMore()">
            Load more changes
          </button>
        </div>
      }
    }`,
  styleUrls: ['../documents/review/viewer/viewer-shared.scss', './document-history.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: DocumentHistoryApi, useClass: HttpDocumentHistoryApi }],
  host: { class: 'viewer-mode' },
})
export class DocumentHistory {
  readonly documentId = input.required<string>();
  readonly controlNumber = input<string | null>(null);

  private readonly api = inject(DocumentHistoryApi);
  private readonly fmt = inject(DisplayFormat);
  private readonly context = inject(WorkspaceContext, { optional: true });
  protected readonly workspaceId = this.context?.workspaceId ?? '';
  protected readonly canReadAudit = this.context?.can(PERMISSIONS.auditRead) ?? false;

  protected readonly fieldId = signal<string | null>(null);
  private readonly fields = signal<readonly FieldResource[]>([]);
  private readonly entries = signal<readonly CodingHistoryEntryResource[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  protected readonly fieldOptions = computed<SelectOption[]>(() =>
    this.fields()
      .filter((f) => f.storage === 'coding' && !f.isHidden)
      .map((f) => ({ value: String(f.fieldId), label: f.displayName }))
      .sort((a, b) => a.label.localeCompare(b.label)),
  );

  protected readonly rows = computed<HistoryRow[]>(() => {
    const byId = new Map(this.fields().map((f) => [String(f.fieldId), f]));
    return this.entries().map((e) => {
      const field = byId.get(String(e.fieldId));
      const propagated = !!e.originEventId;
      const source = propagated ? 'propagated' : e.source === 'job' ? 'job' : 'interactive';
      return {
        id: e.eventId,
        when: this.fmt.dateTime(String(e.occurredAt)),
        field: e.fieldName,
        before: valueText(e.oldValue, field),
        after: valueText(e.newValue, field),
        skipped: e.kind === 'skippedConcurrentEdit',
        by: e.actor.displayName ?? 'Unknown user',
        source,
        sourceLabel:
          source === 'propagated'
            ? 'Propagated from a related document'
            : source === 'job'
              ? 'Job (Mass Edit or import)'
              : 'Interactive',
        jobId: e.jobId,
        automated:
          e.actorType === 'systemRule' ? 'System rule' : e.actorType === 'model' ? 'Model' : null,
      };
    });
  });

  protected readonly caption = computed(() =>
    this.controlNumber()
      ? `Coding history of ${this.controlNumber()}, newest first`
      : 'Coding history, newest first',
  );
  protected readonly countText = computed(() => {
    const n = this.entries().length;
    if (this.loading() && n === 0) return '';
    const shown = `${n} ${n === 1 ? 'change' : 'changes'}`;
    return this.nextCursor() ? `${shown} shown; more available` : shown;
  });

  constructor() {
    void this.api.fields().then((f) => this.fields.set(f));
    effect(() => {
      const documentId = this.documentId();
      const fieldId = this.fieldId();
      untracked(() => void this.load(documentId, fieldId));
    });
  }

  protected chooseField(value: string): void {
    this.fieldId.set(value || null);
  }

  protected async load(documentId = this.documentId(), fieldId = this.fieldId()): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.history(documentId, fieldId);
      if (documentId !== this.documentId() || fieldId !== this.fieldId()) return;
      this.entries.set(page.items);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.entries.set([]);
      this.nextCursor.set(null);
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.loadingMore()) return;
    this.loadingMore.set(true);
    this.error.set(null);
    try {
      const page = await this.api.history(this.documentId(), this.fieldId(), cursor);
      const seen = new Set(this.entries().map((e) => e.eventId));
      this.entries.update((list) => [...list, ...page.items.filter((e) => !seen.has(e.eventId))]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loadingMore.set(false);
    }
  }
}
