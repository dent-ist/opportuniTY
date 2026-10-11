import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import type { ListExportResource } from '../../../core/api/generated/models';
import { describeError, toApiError } from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { Announcer, Button, DialogLayout, Icon, ToastService } from '../../../ui';
import { AllResultsSelection, SelectionTarget } from '../grid/selection';
import { ListExportApi } from './list-export-api';
import {
  ExportColumn,
  LIST_EXPORT_POLL_MS,
  RowScope,
  isActive,
  progressText,
  rowsFor,
  rowsProblem,
  sizeText,
  toRequest,
} from './list-export-model';

export interface ListExportData {
  /** The checked rows (or "all results"), null when nothing is selected. */
  readonly selection: SelectionTarget | null;
  /** Every result of the list as its query; null when the list cannot be named by a query (a report term's hits). */
  readonly list: AllResultsSelection | null;
  /** The list's count label ("≈ 58,330"). */
  readonly listCount: string;
  /** The current view's columns after Control Number, in order. */
  readonly columns: readonly ExportColumn[];
}

/**
 * Export List (CSV) (#181, familiarity guide "Export List (CSV)" under Mass Actions): writes Control Number and the
 * current view's columns for the selected documents (or the whole list) to a CSV file that spreadsheet programs open
 * directly (UTF-8 with a byte-order mark). The list is frozen when the export starts and the file is written by a
 * background job; documents the reviewer can no longer access when it runs are left out and counted (Q-15). The
 * dialog follows the job and offers the download (a gateway link) when it is ready; closing it leaves the job running
 * and a toast says when the file is ready.
 *
 * Keyboard: Tab through the controls, arrows pick the rows radio, Enter on Export starts it, Escape closes.
 */
@Component({
  selector: 'opp-list-export-dialog',
  imports: [Button, DialogLayout, Icon],
  template: `<form (submit)="start($event)" novalidate>
    <opp-dialog-layout title="Export List (CSV)">
      <div class="lx">
        @if (!export()) {
          <fieldset class="lx__group">
            <legend>Rows</legend>
            @if (data.selection) {
              <label>
                <input
                  type="radio"
                  name="lx-rows"
                  value="selection"
                  [checked]="scope() === 'selection'"
                  (change)="scope.set('selection')"
                />
                Selected documents ({{ selectionLabel() }})
              </label>
            }
            <label [class.is-disabled]="!data.list">
              <input
                type="radio"
                name="lx-rows"
                value="list"
                [disabled]="!data.list"
                [checked]="scope() === 'list'"
                (change)="scope.set('list')"
              />
              All results of the list ({{ data.listCount || '0' }})
            </label>
          </fieldset>
          <section class="lx__group" aria-labelledby="lx-columns-title">
            <h3 id="lx-columns-title" class="lx__title">Columns (the current view)</h3>
            <ol class="lx__columns">
              <li>Control Number</li>
              @for (c of data.columns; track c.fieldId) {
                <li>{{ c.label }}</li>
              }
            </ol>
          </section>
          <p class="lx__note">
            <opp-icon name="info" />
            <span
              >CSV in UTF-8, ready for spreadsheet programs. The list is frozen when you export;
              documents you can no longer access when the file is written are left out. Only you can
              download the file, and the export is recorded in the audit trail.</span
            >
          </p>
          @if (problem(); as p) {
            <p class="lx__error" role="alert"><opp-icon name="error" />{{ p }}</p>
          }
        } @else {
          @let e = export()!;
          <div class="lx__progress" role="status" aria-live="polite">
            <opp-icon
              [name]="
                e.status === 'completed' ? 'success' : e.status === 'failed' ? 'error' : 'clock'
              "
            />
            <p class="lx__progress-text" tabindex="-1" data-progress>{{ statusLine() }}</p>
          </div>
          @if (e.status === 'completed') {
            <p>
              <a oppButton="primary" [href]="downloadUrl()" download (click)="downloaded()">
                <opp-icon name="download" />Download CSV ({{ size() }})
              </a>
            </p>
          }
        }
      </div>
      <ng-container dialogActions>
        @if (!export()) {
          <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
          <button type="submit" oppButton="primary" [busy]="starting()">
            <opp-icon name="export" />Export
          </button>
        } @else {
          <button type="button" oppButton="ghost" (click)="ref.close()">Close</button>
        }
      </ng-container>
    </opp-dialog-layout>
  </form>`,
  styleUrl: './list-export-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ListExportDialog {
  protected readonly data = inject<ListExportData>(DIALOG_DATA);
  protected readonly ref = inject(DialogRef);
  private readonly api = inject(ListExportApi);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly pollMs = inject(LIST_EXPORT_POLL_MS);

  /** The checked rows when there are some, else the whole list. */
  protected readonly scope = signal<RowScope>(this.data.selection ? 'selection' : 'list');
  protected readonly starting = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly export = signal<ListExportResource | null>(null);
  private readonly key =
    globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  private timer?: ReturnType<typeof setTimeout>;
  private open = true;

  protected readonly selectionLabel = computed(() => {
    const s = this.data.selection;
    if (!s) return '0';
    return s.kind === 'all'
      ? `all ${s.countText}`
      : new Intl.NumberFormat(this.prefs.locale()).format(s.documentIds.length);
  });
  protected readonly problem = computed(
    () => this.error() ?? rowsProblem(rowsFor(this.scope(), this.data.selection, this.data.list)),
  );
  protected readonly statusLine = computed(() => {
    const e = this.export();
    return e ? progressText(e, this.prefs.locale()) : '';
  });
  protected readonly size = computed(() => sizeText(this.export()?.byteCount, this.prefs.locale()));
  protected readonly downloadUrl = computed(() => {
    const e = this.export();
    return e ? this.api.downloadUrl(e.exportId) : '';
  });

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.open = false;
      // Closing leaves the job running: the reviewer hears when the file is ready.
      const e = this.export();
      if (e && isActive(e)) void this.followInBackground(e.exportId);
      clearTimeout(this.timer);
    });
  }

  protected async start(event: Event): Promise<void> {
    event.preventDefault();
    if (this.starting() || this.export()) return;
    const rows = rowsFor(this.scope(), this.data.selection, this.data.list);
    if (rowsProblem(rows) || !rows) return;
    this.starting.set(true);
    this.error.set(null);
    try {
      const created = await this.api.create(toRequest(rows, this.data.columns), this.key);
      this.show(created);
      afterNextRender(
        () => this.host.nativeElement.querySelector<HTMLElement>('[data-progress]')?.focus(),
        { injector: this.injector },
      );
      this.schedule(created);
    } catch (e) {
      const error = toApiError(e);
      this.error.set(
        error.status === 422 || error.status === 400
          ? (error.problem.detail ?? describeError(error).detail)
          : describeError(error).detail,
      );
    } finally {
      this.starting.set(false);
    }
  }

  protected downloaded(): void {
    this.announcer.announce('Downloading the CSV file.');
  }

  private show(e: ListExportResource): void {
    this.export.set(e);
    this.announcer.announce(progressText(e, this.prefs.locale()));
  }

  private schedule(e: ListExportResource): void {
    if (!isActive(e) || !this.open) return;
    this.timer = setTimeout(() => void this.poll(e.exportId), this.pollMs);
  }

  private async poll(exportId: string): Promise<void> {
    if (!this.open) return;
    try {
      const e = await this.api.get(exportId);
      if (!this.open) return;
      const before = this.export()?.status;
      this.export.set(e);
      if (e.status !== before && !isActive(e))
        this.announcer.announce(progressText(e, this.prefs.locale()));
      this.schedule(e);
    } catch {
      this.schedule(this.export()!);
    }
  }

  /** After the dialog closed: one toast when the file is ready (or failed). */
  private async followInBackground(exportId: string): Promise<void> {
    for (let i = 0; i < 600; i++) {
      await new Promise((r) => setTimeout(r, this.pollMs * 2));
      let e: ListExportResource;
      try {
        e = await this.api.get(exportId);
      } catch {
        continue;
      }
      if (isActive(e)) continue;
      const url = this.api.downloadUrl(e.exportId);
      this.toasts.show(
        `List export: ${progressText(e, this.prefs.locale())}`,
        e.status === 'completed'
          ? { tone: 'success', action: { label: 'Download CSV', run: () => download(url) } }
          : { tone: 'error' },
      );
      return;
    }
  }
}

/** Follows the gateway link (an attachment: the page stays where it is). */
function download(url: string): void {
  const link = document.createElement('a');
  link.href = url;
  link.download = '';
  document.body.append(link);
  link.click();
  link.remove();
}
