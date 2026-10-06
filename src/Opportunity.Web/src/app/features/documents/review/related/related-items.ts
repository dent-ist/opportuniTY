import { _IdGenerator } from '@angular/cdk/a11y';
import { CdkMenuItemCheckbox } from '@angular/cdk/menu';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import type { FieldResource, SearchHit } from '../../../../core/api/generated/models';
import { PreferenceStorage } from '../../../../core/preferences/preference-storage';
import { UiPreferences } from '../../../../core/preferences/ui-preferences';
import { WorkspaceContext } from '../../../../core/workspace/workspace-context';
import { Button, Icon, LoadingState, MENU } from '../../../../ui';
import type { RelationshipPivot } from '../../grid/review-grid';
import { CellFormatter } from '../../grid/grid-format';
import {
  DocumentRelationships,
  MAX_RELATIONSHIP_FIELDS,
  RelatedDocument,
  RelationshipsApi,
} from '../../relationships/relationships-api';

export type RelatedTab = 'family' | 'duplicates' | 'thread';

/** A coding column of the Related Items tables. */
interface CodingColumn {
  readonly queryName: string;
  readonly label: string;
  readonly type: FieldResource['type'];
}

/** A member row as shown. */
interface MemberRow {
  readonly member: RelatedDocument;
  readonly relation: string;
  readonly date: string;
  readonly coding: readonly string[];
}

interface TabView {
  readonly tab: RelatedTab;
  readonly label: string;
  readonly count: number;
  readonly rows: readonly MemberRow[];
  readonly restricted: number;
  /** Id for "Show … in the list"; null when the document has none of this relation. */
  readonly pivotId: string | null;
  readonly pivotLabel: string;
  readonly empty: string;
  /** "Showing the first 200 of 1,234." for a long email thread. */
  readonly truncated: string | null;
}

/** Coding columns shown by default: the first few choice and Yes/No coding fields. */
const DEFAULT_COLUMNS = 3;
/** At most this many coding columns fit the pane. */
export const MAX_RELATED_COLUMNS = 6;

/**
 * Related Items of Review mode (E16-T10, familiarity guide §3.2, ticket review E16-T10): the Family, Duplicates and
 * Email Thread members of the displayed document in tabs, each with relation, Control Number, name, date and the
 * coding columns the reviewer picks (their current values: who is coded how). Opening a member shows it in the viewer
 * without moving the review cursor. Members the reviewer may not see are never listed (Q-52); the API only counts
 * them, shown as "N restricted items" without any of their metadata. "Show … in the list" runs the relation as a new
 * search in Documents.
 */
@Component({
  selector: 'opp-related-items',
  imports: [Button, CdkMenuItemCheckbox, Icon, LoadingState, ...MENU],
  templateUrl: './related-items.html',
  styleUrl: './related-items.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pane__content related' },
})
export class RelatedItems {
  /** The displayed document (the cursor document, or a related item opened from here). */
  readonly hit = input.required<SearchHit>();
  /** The workspace's field catalogue: the coding fields offered as columns. */
  readonly fields = input<readonly FieldResource[] | null>(null);
  /** Show a member in the viewer (the review cursor stays). */
  readonly openItem = output<SearchHit>();
  /** Run a relation as a new search in Documents. */
  readonly pivot = output<RelationshipPivot>();

  private readonly api = inject(RelationshipsApi);
  private readonly storage = inject(PreferenceStorage);
  private readonly prefs = inject(UiPreferences);
  private readonly context = inject(WorkspaceContext);
  private readonly columnsKey = `related.columns.${this.context.workspaceId}`;
  protected readonly uid = inject(_IdGenerator).getId('opp-related-');

  protected readonly tab = signal<RelatedTab>('family');
  protected readonly state = signal<DocumentRelationships | 'loading' | 'unavailable'>('loading');
  private readonly reloads = signal(0);
  private seq = 0;
  private loadedId: string | null = null;

  /** Coding fields that can be columns (coding storage, not hidden). */
  protected readonly codingFields = computed<CodingColumn[]>(() =>
    (this.fields() ?? [])
      .filter((f) => String(f.storage).toLowerCase() === 'coding' && !f.isHidden)
      .map((f) => ({ queryName: f.queryName.toLowerCase(), label: f.displayName, type: f.type })),
  );

  /** The chosen coding columns (remembered per workspace), else the first choice and Yes/No fields. */
  protected readonly columns = computed<CodingColumn[]>(() => {
    const all = this.codingFields();
    const chosen = this.storage.read<{ fields?: string[] }>(this.columnsKey)?.fields;
    if (chosen) {
      const byName = new Map(all.map((c) => [c.queryName, c]));
      return chosen.flatMap((q) => byName.get(q) ?? []).slice(0, MAX_RELATED_COLUMNS);
    }
    return all
      .filter((c) => c.type === 'singleChoice' || c.type === 'multiChoice' || c.type === 'boolean')
      .slice(0, DEFAULT_COLUMNS);
  });

  private readonly format = computed(
    () => new CellFormatter(this.prefs.locale(), this.context.workspace.displayTimeZone || 'UTC'),
  );

  protected readonly tabs = computed<TabView[]>(() => {
    const state = this.state();
    if (typeof state === 'string') return [];
    const n = new Intl.NumberFormat(this.prefs.locale());
    const { family, duplicates, thread } = state;
    const truncated =
      thread.total > thread.members.length
        ? `Showing the first ${n.format(thread.members.length)} of ${n.format(thread.total)} email thread members.`
        : null;
    return [
      {
        tab: 'family',
        label: 'Family',
        count: family.members.length,
        rows: family.members.map((m) => this.row(m, familyRelation(m))),
        restricted: family.restrictedCount,
        pivotId: family.members.length > 1 ? family.familyId : null,
        pivotLabel: 'Show family in the list',
        empty: 'This document is not part of a family.',
        truncated: null,
      },
      {
        tab: 'duplicates',
        label: 'Duplicates',
        count: duplicates.members.length,
        rows: duplicates.members.map((m) => this.row(m, m.isPrimary ? 'Primary' : 'Duplicate')),
        restricted: duplicates.restrictedCount,
        pivotId: duplicates.duplicateGroupId,
        pivotLabel: 'Show duplicates in the list',
        empty: 'This document has no duplicates.',
        truncated: null,
      },
      {
        tab: 'thread',
        label: 'Email Thread',
        count: thread.total,
        rows: thread.members.map((m) => this.row(m, m.isSelf ? 'This document' : 'Thread member')),
        restricted: thread.restrictedCount,
        pivotId: thread.emailThreadId,
        pivotLabel: 'Show email thread in the list',
        empty: 'This document is not part of an email thread.',
        truncated,
      },
    ];
  });

  /** Whether the document has other family members or duplicates the reviewer may see (null while loading). */
  readonly relations = computed(() => {
    const state = this.state();
    if (typeof state === 'string') return null;
    return {
      family: state.family.members.length > 1,
      duplicates: state.duplicates.members.length > 1,
    };
  });

  protected readonly current = computed(
    () => this.tabs().find((t) => t.tab === this.tab()) ?? null,
  );

  constructor() {
    effect(() => {
      const id = this.hit().documentId;
      const fields = this.columns().map((c) => c.queryName);
      this.reloads();
      untracked(() => this.load(id, fields));
    });
  }

  /** Reads the relationships again (after coding was applied to the family or duplicates). */
  reload(): void {
    this.reloads.update((n) => n + 1);
  }

  protected choose(tab: string | undefined): void {
    if (tab === 'family' || tab === 'duplicates' || tab === 'thread') this.tab.set(tab);
  }

  /** Left/Right, Home/End between the tabs; Enter or Space selects (APG tabs, manual activation). */
  protected onTabKey(event: KeyboardEvent): void {
    const tabs = [
      ...(event.currentTarget as HTMLElement).querySelectorAll<HTMLElement>('[role="tab"]'),
    ];
    const index = tabs.indexOf(event.target as HTMLElement);
    if (index < 0) return;
    const last = tabs.length - 1;
    const target: Record<string, number> = {
      ArrowRight: index === last ? 0 : index + 1,
      ArrowLeft: index === 0 ? last : index - 1,
      Home: 0,
      End: last,
    };
    if (event.key in target) {
      event.preventDefault();
      tabs[target[event.key]].focus();
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.choose(tabs[index].dataset['tab']);
    }
  }

  protected isShown(column: CodingColumn): boolean {
    return this.columns().some((c) => c.queryName === column.queryName);
  }

  protected toggleColumn(column: CodingColumn): void {
    const current = this.columns().map((c) => c.queryName);
    const next = current.includes(column.queryName)
      ? current.filter((q) => q !== column.queryName)
      : [...current, column.queryName].slice(-MAX_RELATED_COLUMNS);
    this.storage.write(this.columnsKey, { fields: next });
  }

  protected open(row: MemberRow): void {
    const state = this.state();
    const m = row.member;
    const family = typeof state === 'string' ? null : state.family;
    const inFamily = family?.members.some((f) => f.documentId === m.documentId) ?? false;
    this.openItem.emit({
      documentId: m.documentId,
      controlNumber: m.controlNumber,
      documentDate: m.documentDate,
      familyId: inFamily ? (family?.familyId ?? null) : null,
      familySequence: m.familySequence,
      parentDocumentId:
        inFamily && !m.isParent && family?.parent ? family.parent.documentId : null,
      isFamilyParent: inFamily && m.isParent,
      fileName: m.fileName,
      fileExtension: null,
      fileSize: null,
      fileType: null,
      mimeType: null,
      pageCount: null,
      snippets: [],
    });
  }

  protected showInList(view: TabView): void {
    if (!view.pivotId) return;
    this.pivot.emit({ kind: view.tab, id: view.pivotId, controlNumber: this.hit().controlNumber });
  }

  protected restrictedText(count: number): string {
    const n = new Intl.NumberFormat(this.prefs.locale()).format(count);
    return count === 1 ? '1 restricted item' : `${n} restricted items`;
  }

  private load(documentId: string, fields: readonly string[]): void {
    const seq = ++this.seq;
    // Another document starts from the loading state; a reload of the same one keeps its rows meanwhile.
    if (documentId !== this.loadedId) this.state.set('loading');
    this.loadedId = documentId;
    this.api.relationships(documentId, fields.slice(0, MAX_RELATIONSHIP_FIELDS)).then(
      (relationships) => {
        if (seq === this.seq) this.state.set(relationships);
      },
      () => {
        if (seq === this.seq) this.state.set('unavailable');
      },
    );
  }

  private row(member: RelatedDocument, relation: string): MemberRow {
    const f = this.format();
    return {
      member,
      relation: member.isSelf ? `${relation} (this document)` : relation,
      date: member.documentDate ? f.formatDate(member.documentDate) : '',
      coding: this.columns().map((c) => codingText(c, member.coding[c.queryName] ?? [])),
    };
  }
}

function familyRelation(m: RelatedDocument): string {
  if (m.isParent) return 'Parent';
  return m.familySequence ? `Attachment ${m.familySequence}` : 'Attachment';
}

function codingText(column: CodingColumn, values: readonly string[]): string {
  if (values.length === 0) return '';
  if (column.type === 'boolean') {
    return values.map((v) => (v === 'true' ? 'Yes' : v === 'false' ? 'No' : v)).join('; ');
  }
  return values.join('; ');
}
