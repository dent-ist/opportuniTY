import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ApiError, toApiError } from '../../core/api/problem-details';
import type {
  CustodyVerificationResource,
  DefensibilityStatementResource,
  SearchTermHitReportResource,
} from '../../core/api/generated/models';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../core/workspace/sections';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  BadgeTone,
  Button,
  EmptyState,
  ErrorState,
  Icon,
  IconName,
  LoadingState,
  TextField,
} from '../../ui';
import {
  ChainOfCustody,
  DefensibilityApi,
  DefensibilityQuery,
  HttpDefensibilityApi,
  ProductionHistory,
  ReviewSummary,
  SearchTermHits,
  localDateTime,
  parseDocumentIds,
} from './defensibility-api';

/** Rows of the per-day table shown on the page; the CSV holds them all. */
const SHOWN_DAYS = 200;

/** Documents the pack can trace (the API's limit). */
const MAX_PACK_DOCUMENTS = 25;

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const VERIFICATION: Record<string, { label: string; tone: BadgeTone; icon: IconName }> = {
  Match: { label: 'Matches', tone: 'success', icon: 'check' },
  Mismatch: { label: 'Does not match', tone: 'danger', icon: 'error' },
  Missing: { label: 'Missing from storage', tone: 'danger', icon: 'error' },
  NotVerified: { label: 'Not re-read', tone: 'neutral', icon: 'info' },
};

/** A report generated on request: its result, whether it is loading and its error. */
class Generated<T> {
  readonly value = signal<T | null>(null);
  readonly loading = signal(false);
  readonly error = signal<ApiError | null>(null);

  async run(load: () => Promise<T>): Promise<void> {
    if (this.loading()) return;
    this.loading.set(true);
    this.error.set(null);
    try {
      this.value.set(await load());
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }
}

/**
 * Admin › Defensibility Reports (E14-T05): reports counsel can stand behind. Each is generated on request (generation is
 * audited) as of a point in time, states that point and a fingerprint of its content, and gives the same fingerprint
 * when generated again for the same point: the review population and coding by reviewer and date, the production
 * history with Bates ranges, hashes and QC results, the search term hit reports with the index lag at execution, and a
 * document's chain of custody with the hashes recorded at import next to the hashes re-read from storage. The report
 * pack downloads them together with the production manifests, QC reports, compliance reports and the audit trail.
 * Needs `Compliance.Report`.
 */
@Component({
  selector: 'opp-defensibility-page',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState, NgTemplateOutlet, TextField],
  templateUrl: './defensibility-page.html',
  styleUrl: './defensibility-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: DefensibilityApi, useClass: HttpDefensibilityApi }],
  host: { class: 'dfn-page' },
})
export class DefensibilityPage {
  private readonly api = inject(DefensibilityApi);
  private readonly context = inject(WorkspaceContext);
  private readonly prefs = inject(UiPreferences);

  protected readonly canExportAudit = this.context.can(PERMISSIONS.auditExport);
  protected readonly shownDays = SHOWN_DAYS;
  protected readonly maxPackDocuments = MAX_PACK_DOCUMENTS;

  // The point every report is generated for (empty: now) and the review summary's optional snapshot scope.
  protected readonly asOfInput = signal('');
  protected readonly snapshotInput = signal('');
  protected readonly asOf = computed(() => localDateTime(this.asOfInput()));
  protected readonly asOfError = computed(() => {
    if (!this.asOfInput()) return null;
    const at = this.asOf();
    if (!at) return 'Enter a valid date and time.';
    return new Date(at).getTime() > Date.now() ? 'The point cannot be in the future.' : null;
  });
  protected readonly snapshotError = computed(() => {
    const value = this.snapshotInput().trim();
    return value && !GUID.test(value) ? 'Enter a snapshot ID.' : null;
  });
  private readonly query = computed<DefensibilityQuery>(() => ({ asOf: this.asOf() }));
  protected readonly blocked = computed(() => !!this.asOfError());

  protected readonly review = new Generated<ReviewSummary>();
  protected readonly productions = new Generated<ProductionHistory>();
  protected readonly hits = new Generated<SearchTermHits>();
  protected readonly custody = new Generated<ChainOfCustody>();

  protected readonly reviewCsvUrl = computed(() =>
    this.api.exportUrl(this.context.workspaceId, 'review-summary', {
      ...this.query(),
      snapshotId: this.snapshotInput().trim() || null,
    }),
  );
  protected readonly productionsCsvUrl = computed(() =>
    this.api.exportUrl(this.context.workspaceId, 'production-history', this.query()),
  );
  protected readonly hitsCsvUrl = computed(() =>
    this.api.exportUrl(this.context.workspaceId, 'search-term-hits', this.query()),
  );
  protected readonly shownDayRows = computed(
    () => this.review.value()?.days.slice(0, SHOWN_DAYS) ?? [],
  );

  // Chain of custody of one document.
  protected readonly documentInput = signal('');
  protected readonly documentError = computed(() => {
    const value = this.documentInput().trim();
    return value && !GUID.test(value) ? 'Enter a document ID.' : null;
  });
  protected readonly custodyCsvUrl = computed(() => {
    const id = this.documentInput().trim();
    return GUID.test(id)
      ? this.api.chainOfCustodyExportUrl(this.context.workspaceId, id, this.query())
      : null;
  });

  // The pack.
  protected readonly packFromInput = signal('');
  protected readonly packDocumentsInput = signal('');
  protected readonly packFrom = computed(() => localDateTime(this.packFromInput()));
  protected readonly packDocuments = computed(() => parseDocumentIds(this.packDocumentsInput()));
  protected readonly packError = computed(() => {
    if (this.packFromInput() && !this.packFrom()) return 'Enter a valid start date and time.';
    const end = this.asOf() ? new Date(this.asOf()!).getTime() : Date.now();
    if (this.packFrom() && new Date(this.packFrom()!).getTime() >= end)
      return 'The period must start before the report point.';
    const ids = this.packDocuments();
    if (!ids) return 'Enter document IDs separated by commas or spaces.';
    if (ids.length > MAX_PACK_DOCUMENTS) return `At most ${MAX_PACK_DOCUMENTS} documents per pack.`;
    return null;
  });
  protected readonly packUrl = computed(() =>
    this.api.packUrl(this.context.workspaceId, {
      ...this.query(),
      snapshotId: this.snapshotInput().trim() || null,
      from: this.packFrom(),
      documentIds: this.packDocuments() ?? [],
    }),
  );

  generateReview(): Promise<void> {
    if (this.blocked() || this.snapshotError()) return Promise.resolve();
    return this.review.run(() =>
      this.api.reviewSummary(this.context.workspaceId, {
        ...this.query(),
        snapshotId: this.snapshotInput().trim() || null,
      }),
    );
  }

  generateProductions(): Promise<void> {
    if (this.blocked()) return Promise.resolve();
    return this.productions.run(() =>
      this.api.productionHistory(this.context.workspaceId, this.query()),
    );
  }

  generateHits(): Promise<void> {
    if (this.blocked()) return Promise.resolve();
    return this.hits.run(() => this.api.searchTermHits(this.context.workspaceId, this.query()));
  }

  generateCustody(): Promise<void> {
    const id = this.documentInput().trim();
    if (this.blocked() || !GUID.test(id)) return Promise.resolve();
    return this.custody.run(() =>
      this.api.chainOfCustody(this.context.workspaceId, id, this.query()),
    );
  }

  protected verification(v: CustodyVerificationResource): {
    label: string;
    tone: BadgeTone;
    icon: IconName;
  } {
    return VERIFICATION[v.status] ?? { label: v.status, tone: 'neutral', icon: 'info' };
  }

  protected lag(r: SearchTermHitReportResource): string {
    if (r.indexPendingChanges === null || r.indexPendingChanges === undefined)
      return 'Not recorded';
    const pending = Number(r.indexPendingChanges);
    if (pending === 0) return 'None';
    const changes = pending === 1 ? '1 change' : `${pending} changes`;
    return `${changes} not yet searchable, oldest ${Number(r.indexLagSeconds ?? 0)} s`;
  }

  protected statementTime(s: DefensibilityStatementResource): string {
    return this.date(s.asOf, true);
  }

  protected date(value: unknown, seconds = false): string {
    if (!value) return '';
    const d = new Date(String(value));
    return Number.isNaN(d.getTime())
      ? String(value)
      : new Intl.DateTimeFormat(this.prefs.locale(), {
          dateStyle: 'medium',
          timeStyle: seconds ? 'medium' : 'short',
        }).format(d);
  }

  protected n(value: unknown): string {
    const v = Number(value);
    return Number.isFinite(v) ? v.toLocaleString(this.prefs.locale()) : '';
  }

  protected short(value: string | null | undefined, length = 12): string {
    return value ? value.slice(0, length) : '';
  }
}
