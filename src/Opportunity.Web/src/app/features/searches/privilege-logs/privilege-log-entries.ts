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
import { ApiError, toApiError } from '../../../core/api/problem-details';
import {
  Announcer,
  Badge,
  Button,
  ErrorState,
  Icon,
  LoadingState,
  ToastService,
} from '../../../ui';
import { formatDate } from '../saved-search-model';
import { DescriptionChange, LogEntry, LogVersion, PrivilegeLogApi } from './privilege-logs-api';
import { DisplayFormat } from '../../../core/i18n';

/** The open description editor of one entry. */
interface Editor {
  readonly documentId: string;
  readonly documentVersion: string;
  readonly draft: string;
  readonly saving: boolean;
}

/**
 * The rows of a privilege log version (#182): its metadata columns (Priv ID, dates, people, subject, privilege basis,
 * description, treatment) as frozen with the version, a page at a time. The per-document description (Q-20) is the
 * Privilege Description field: reviewers who may code edit it here inline through the coding API (If-Match, audited
 * `Coding.Changed`), see its history, and the edit appears in the log's next version — a version itself never changes.
 */
@Component({
  selector: 'opp-privilege-log-entries',
  imports: [Badge, Button, ErrorState, Icon, LoadingState],
  templateUrl: './privilege-log-entries.html',
  styleUrl: './privilege-log-tables.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PrivilegeLogEntries {
  private readonly api = inject(PrivilegeLogApi);
  private readonly fmt = inject(DisplayFormat);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);

  readonly version = input.required<LogVersion>();
  /** The caller may code documents (Coding.Write); the API still decides per document and field. */
  readonly canEdit = input(false);

  protected readonly rows = signal<readonly LogEntry[] | null>(null);
  protected readonly total = signal(0);
  protected readonly next = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  /** Descriptions saved since this version, by document. */
  protected readonly edited = signal<Readonly<Record<string, string>>>({});
  protected readonly editor = signal<Editor | null>(null);
  protected readonly opening = signal<string | null>(null);
  protected readonly history = signal<{
    documentId: string;
    items: readonly DescriptionChange[] | null;
  } | null>(null);

  protected readonly descriptionColumn = computed(() => this.version().descriptionColumn);
  protected readonly editable = computed(() => this.canEdit() && this.descriptionColumn() !== null);
  protected readonly editedCount = computed(() => Object.keys(this.edited()).length);
  protected readonly span = computed(
    () => this.version().columns.length + (this.editable() ? 1 : 0),
  );

  constructor() {
    effect(() => {
      const logId = this.version().logId;
      untracked(() => {
        this.rows.set(null);
        this.next.set(null);
        this.editor.set(null);
        this.history.set(null);
        void this.load(logId, null);
      });
    });
  }

  protected async more(): Promise<void> {
    await this.load(this.version().logId, this.next());
  }

  private async load(logId: string, cursor: string | null): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.entries(logId, cursor);
      if (this.version().logId !== logId) return;
      this.rows.set([...(cursor ? (this.rows() ?? []) : []), ...page.items]);
      this.total.set(page.total);
      this.next.set(page.nextCursor);
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected retry(): void {
    void this.load(this.version().logId, this.next());
  }

  /** The log identifier of a row (its first cell), for labels. */
  protected label(row: LogEntry): string {
    return row.cells[0] || `entry ${row.ordinal}`;
  }

  protected cell(row: LogEntry, index: number): string {
    if (index === this.descriptionColumn() && row.documentId in this.edited()) {
      return this.edited()[row.documentId];
    }
    return row.cells[index] ?? '';
  }

  protected isEdited(row: LogEntry, index: number): boolean {
    return index === this.descriptionColumn() && row.documentId in this.edited();
  }

  protected async edit(row: LogEntry): Promise<void> {
    if (this.opening()) return;
    this.opening.set(row.documentId);
    try {
      const current = await this.api.description(row.documentId);
      if (!current.editable) {
        this.toasts.show(`You cannot edit the description of ${this.label(row)}.`, {
          tone: 'error',
        });
        return;
      }
      this.editor.set({
        documentId: row.documentId,
        documentVersion: current.documentVersion,
        draft: current.value,
        saving: false,
      });
      this.focus(`pl-desc-${row.documentId}`);
    } catch (e) {
      this.toasts.show(
        toApiError(e).problem.detail ?? 'The description could not be opened for editing.',
        { tone: 'error' },
      );
    } finally {
      this.opening.set(null);
    }
  }

  protected onDraft(event: Event): void {
    const editor = this.editor();
    if (editor) this.editor.set({ ...editor, draft: (event.target as HTMLTextAreaElement).value });
  }

  protected cancel(row: LogEntry): void {
    this.editor.set(null);
    this.focus(`pl-edit-${row.documentId}`);
  }

  protected async save(row: LogEntry): Promise<void> {
    const editor = this.editor();
    if (!editor || editor.saving) return;
    this.editor.set({ ...editor, saving: true });
    try {
      const saved = await this.api.saveDescription(
        editor.documentId,
        editor.documentVersion,
        editor.draft,
      );
      this.edited.set({ ...this.edited(), [editor.documentId]: saved.value });
      this.editor.set(null);
      if (this.history()?.documentId === editor.documentId) void this.loadHistory(row);
      const message = `Description of ${this.label(row)} saved. It is listed in the next version you generate.`;
      this.announcer.announce(message);
      this.toasts.show(message, { tone: 'success' });
      this.focus(`pl-edit-${row.documentId}`);
    } catch (e) {
      const error = toApiError(e);
      this.editor.set({ ...editor, saving: false });
      this.toasts.show(
        error.status === 412
          ? 'The document was changed by someone else since you opened it. Cancel and edit again to see the current description.'
          : (error.problem.detail ?? 'The description could not be saved.'),
        { tone: 'error' },
      );
    }
  }

  protected keydown(event: KeyboardEvent, row: LogEntry): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.cancel(row);
    } else if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      void this.save(row);
    }
  }

  protected async toggleHistory(row: LogEntry): Promise<void> {
    if (this.history()?.documentId === row.documentId) {
      this.history.set(null);
      return;
    }
    await this.loadHistory(row);
  }

  private async loadHistory(row: LogEntry): Promise<void> {
    this.history.set({ documentId: row.documentId, items: null });
    try {
      const items = await this.api.descriptionHistory(row.documentId);
      if (this.history()?.documentId === row.documentId) {
        this.history.set({ documentId: row.documentId, items });
      }
    } catch (e) {
      this.history.set(null);
      this.toasts.show(
        toApiError(e).problem.detail ?? 'The description history could not be loaded.',
        { tone: 'error' },
      );
    }
  }

  protected when(at: string): string {
    return formatDate(at, this.fmt.formatter());
  }

  protected count(n: number): string {
    return this.fmt.number(n);
  }

  private focus(id: string): void {
    setTimeout(() => document.getElementById(id)?.focus());
  }
}
