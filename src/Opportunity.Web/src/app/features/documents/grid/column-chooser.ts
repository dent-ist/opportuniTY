import {
  CdkDrag,
  CdkDragDrop,
  CdkDragHandle,
  CdkDropList,
  moveItemInArray,
} from '@angular/cdk/drag-drop';
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import type { FieldResource } from '../../../core/api/generated/models';
import {
  Announcer,
  Badge,
  Button,
  DialogLayout,
  Icon,
  IconButton,
  Select,
  SelectOption,
  Tooltip,
} from '../../../ui';
import {
  AvailableColumn,
  ColumnGroup,
  ColumnSpec,
  MAX_COLUMN_WIDTH,
  MAX_SORT_LEVELS,
  MIN_COLUMN_WIDTH,
  SortSpec,
  availableColumns,
  buildColumns,
} from './grid-columns';

export interface ColumnChooserData {
  readonly fields: readonly FieldResource[] | null;
  readonly columns: readonly ColumnSpec[];
  readonly sort: readonly SortSpec[];
}

export interface ColumnChooserResult {
  readonly columns: ColumnSpec[];
  readonly sort: SortSpec[];
}

const GROUP_LABELS: Record<ColumnGroup, string> = {
  structural: 'Document fields',
  metadata: 'Metadata',
  coding: 'Coding',
};

interface SortLevel {
  readonly field: string;
  readonly direction: 'asc' | 'desc';
}

/**
 * Columns and sort of the document list (E16-T09): the shown columns in order (drag by the handle, or Move up / Move
 * down, or Alt+Arrow Up/Down on a row), each with Pin and a width; the catalogue's document, metadata and coding fields
 * to add (fields that cannot be shown say why); and up to three sort levels, where fields that cannot be sorted are
 * listed but disabled with the reason. Control Number is always the first column and breaks sort ties.
 */
@Component({
  selector: 'opp-column-chooser',
  imports: [
    Badge,
    Button,
    CdkDrag,
    CdkDragHandle,
    CdkDropList,
    DialogLayout,
    Icon,
    IconButton,
    Select,
    Tooltip,
  ],
  template: `<form (submit)="apply($event)" novalidate>
    <opp-dialog-layout title="Columns and sort">
      <div class="cc">
        <section class="cc__section" aria-labelledby="cc-shown">
          <h3 id="cc-shown" class="cc__heading">Shown columns</h3>
          <p class="cc__hint" id="cc-shown-hint">
            Control Number is always first. Drag a column by its handle, or use Move up and Move
            down (Alt+Arrow Up and Alt+Arrow Down on a column).
          </p>
          @if (shown().length === 0) {
            <p class="cc__empty">Only Control Number is shown. Add columns below.</p>
          }
          <ol
            class="cc__list"
            cdkDropList
            aria-describedby="cc-shown-hint"
            (cdkDropListDropped)="drop($event)"
          >
            @for (col of shown(); track col.field; let i = $index, last = $last) {
              <li
                class="cc__item"
                cdkDrag
                [attr.data-field]="col.field"
                (keydown)="onRowKeydown($event, i)"
              >
                <span class="cc__handle" cdkDragHandle aria-hidden="true">
                  <opp-icon name="grip-vertical" />
                </span>
                <span class="cc__label" [id]="'cc-label-' + i">{{ labelOf(col.field) }}</span>
                @if (sortReasonOf(col.field); as reason) {
                  <opp-badge tone="neutral" [oppTooltip]="reason">Not sortable</opp-badge>
                }
                <label class="cc__pin">
                  <input
                    type="checkbox"
                    [checked]="!!col.pinned"
                    (change)="setPinned(i, $any($event.target).checked)"
                  />
                  Pin
                </label>
                <label class="cc__width">
                  Width
                  <input
                    type="number"
                    inputmode="numeric"
                    [min]="minWidth"
                    [max]="maxWidth"
                    step="10"
                    placeholder="Auto"
                    [value]="col.width ?? ''"
                    (change)="setWidth(i, $any($event.target).value)"
                  />
                  <span class="cc__unit">px</span>
                </label>
                <button
                  type="button"
                  oppIconButton
                  [label]="'Move ' + labelOf(col.field) + ' up'"
                  [disabled]="i === 0"
                  (click)="move(i, -1)"
                >
                  <opp-icon name="chevron-up" />
                </button>
                <button
                  type="button"
                  oppIconButton
                  [label]="'Move ' + labelOf(col.field) + ' down'"
                  [disabled]="last"
                  (click)="move(i, 1)"
                >
                  <opp-icon name="chevron-down" />
                </button>
                <button
                  type="button"
                  oppIconButton
                  [label]="'Remove ' + labelOf(col.field)"
                  (click)="remove(i)"
                >
                  <opp-icon name="close" />
                </button>
              </li>
            }
          </ol>
        </section>

        <section class="cc__section" aria-labelledby="cc-add">
          <h3 id="cc-add" class="cc__heading">Add columns</h3>
          <label class="cc__find">
            Find a field
            <input
              type="search"
              [value]="filter()"
              (input)="filter.set($any($event.target).value)"
            />
          </label>
          <div class="cc__groups">
            @for (group of groups(); track group.group) {
              <div class="cc__group" role="group" [attr.aria-label]="group.label">
                <h4 class="cc__subheading">{{ group.label }}</h4>
                <ul class="cc__fields">
                  @for (f of group.fields; track f.queryName) {
                    <li>
                      <button
                        type="button"
                        oppButton="ghost"
                        class="cc__add"
                        [attr.aria-disabled]="f.unavailableReason ? 'true' : null"
                        [oppTooltip]="f.unavailableReason"
                        (click)="add(f)"
                      >
                        <opp-icon name="plus" />{{ f.label }}
                      </button>
                    </li>
                  } @empty {
                    <li class="cc__empty">No fields to add.</li>
                  }
                </ul>
              </div>
            }
          </div>
        </section>

        <fieldset class="cc__sort">
          <legend>Sort</legend>
          <p class="cc__hint">
            Up to {{ maxLevels }} levels. Documents with equal values are always ordered by Control
            Number. Fields that cannot be sorted are listed but unavailable.
          </p>
          @for (level of levels(); track $index; let i = $index) {
            <div class="cc__level">
              <opp-select
                [label]="'Sort level ' + (i + 1)"
                placeholder="None"
                [options]="sortOptions()"
                [value]="level.field"
                (valueChange)="setLevelField(i, $event)"
              />
              <opp-select
                [label]="'Direction, level ' + (i + 1)"
                [options]="directions"
                [value]="level.direction"
                [disabled]="!level.field"
                (valueChange)="setLevelDirection(i, $event)"
              />
            </div>
          }
        </fieldset>
      </div>
      <ng-container dialogActions>
        <button type="button" oppButton="ghost" (click)="ref.close()">Cancel</button>
        <button type="submit" oppButton="primary">Apply</button>
      </ng-container>
    </opp-dialog-layout>
  </form>`,
  styleUrl: './column-chooser.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ColumnChooser {
  protected readonly data = inject<ColumnChooserData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<ColumnChooserResult>>(DialogRef);
  private readonly announcer = inject(Announcer);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly minWidth = MIN_COLUMN_WIDTH;
  protected readonly maxWidth = MAX_COLUMN_WIDTH;
  protected readonly maxLevels = MAX_SORT_LEVELS;
  protected readonly directions: SelectOption[] = [
    { value: 'asc', label: 'Ascending' },
    { value: 'desc', label: 'Descending' },
  ];

  private readonly catalogue = availableColumns(this.data.fields);
  private readonly byName = new Map(this.catalogue.map((c) => [c.queryName, c]));
  /** Column labels and sort fields as the grid builds them (fixed sort keys such as `documentDate`). */
  private readonly built = (() => {
    const all = buildColumns(
      this.data.fields,
      this.catalogue.map((c) => ({ field: c.queryName })),
    );
    return [all.controlNumber, ...all.view];
  })();

  protected readonly shown = signal<ColumnSpec[]>(
    this.data.columns
      .filter((c) => c.field.toLowerCase() !== 'controlnumber')
      .map((c) => ({ ...c, field: c.field.toLowerCase() })),
  );
  protected readonly filter = signal('');
  protected readonly levels = signal<SortLevel[]>(
    Array.from({ length: MAX_SORT_LEVELS }, (_, i) => {
      const s = this.data.sort[i];
      return s ? { field: s.field, direction: s.direction } : { field: '', direction: 'asc' };
    }),
  );

  protected readonly groups = computed(() => {
    const shown = new Set(this.shown().map((c) => c.field));
    const text = this.filter().trim().toLowerCase();
    return (['structural', 'metadata', 'coding'] as const).map((group) => ({
      group,
      label: GROUP_LABELS[group],
      fields: this.catalogue.filter(
        (c) =>
          c.group === group &&
          !shown.has(c.queryName) &&
          (!text || c.label.toLowerCase().includes(text) || c.queryName.includes(text)),
      ),
    }));
  });

  /** Every column of the catalogue as a sort option; those that cannot be sorted are disabled and say so. */
  protected readonly sortOptions = computed<SelectOption[]>(() =>
    this.built.map((c) => ({
      value: c.sortField ?? `unsortable:${c.queryName}`,
      label: c.sortField ? c.label : `${c.label} (cannot be sorted)`,
      disabled: !c.sortField,
    })),
  );

  protected labelOf(field: string): string {
    return this.byName.get(field)?.label ?? field;
  }

  protected sortReasonOf(field: string): string | null {
    return this.byName.get(field)?.sortReason ?? null;
  }

  protected drop(event: CdkDragDrop<unknown>): void {
    if (event.previousIndex === event.currentIndex) return;
    const next = [...this.shown()];
    moveItemInArray(next, event.previousIndex, event.currentIndex);
    this.shown.set(next);
    this.announceMove(next, event.currentIndex);
  }

  protected move(index: number, delta: number, focus: 'button' | 'row' = 'button'): void {
    const to = index + delta;
    const next = [...this.shown()];
    if (to < 0 || to >= next.length) return;
    moveItemInArray(next, index, to);
    this.shown.set(next);
    this.announceMove(next, to);
    // Keep focus on the moved column (its button may now be disabled at the ends).
    afterNextRender(
      () => {
        const row = this.host.nativeElement.querySelectorAll<HTMLElement>('.cc__item')[to];
        if (!row) return;
        const label = this.labelOf(next[to].field);
        const target =
          focus === 'row'
            ? row
            : (row.querySelector<HTMLButtonElement>(
                `button[aria-label="Move ${label} ${delta < 0 ? 'up' : 'down'}"]:not([disabled])`,
              ) ?? row.querySelector<HTMLButtonElement>('button:not([disabled])'));
        if (focus === 'row') row.tabIndex = -1;
        target?.focus();
      },
      { injector: this.injector },
    );
  }

  /** Alt+Arrow Up/Down anywhere in a column's row moves it. */
  protected onRowKeydown(event: KeyboardEvent, index: number): void {
    if (!event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    event.preventDefault();
    this.move(index, event.key === 'ArrowUp' ? -1 : 1);
  }

  private announceMove(columns: readonly ColumnSpec[], to: number): void {
    this.announcer.announce(
      `${this.labelOf(columns[to].field)} moved to position ${to + 1} of ${columns.length}.`,
    );
  }

  protected setPinned(index: number, pinned: boolean): void {
    this.shown.update((cols) => cols.map((c, i) => (i === index ? { ...c, pinned } : c)));
  }

  protected setWidth(index: number, raw: string): void {
    const n = Number(raw);
    const width =
      raw === '' || !Number.isFinite(n)
        ? null
        : Math.round(Math.min(MAX_COLUMN_WIDTH, Math.max(MIN_COLUMN_WIDTH, n)));
    this.shown.update((cols) => cols.map((c, i) => (i === index ? { ...c, width } : c)));
  }

  protected remove(index: number): void {
    const removed = this.shown()[index];
    this.shown.update((cols) => cols.filter((_, i) => i !== index));
    this.announcer.announce(`${this.labelOf(removed.field)} removed.`);
    afterNextRender(
      () => {
        const rows = this.host.nativeElement.querySelectorAll<HTMLElement>('.cc__item');
        const row = rows[Math.min(index, rows.length - 1)];
        (row?.querySelector<HTMLElement>('button[aria-label^="Remove"]') ??
          this.host.nativeElement.querySelector<HTMLElement>('input[type="search"]'))!.focus();
      },
      { injector: this.injector },
    );
  }

  protected add(field: AvailableColumn): void {
    if (field.unavailableReason) {
      this.announcer.announce(`${field.label} cannot be added. ${field.unavailableReason}`);
      return;
    }
    this.shown.update((cols) => [...cols, { field: field.queryName }]);
    this.announcer.announce(`${field.label} added as the last column.`);
  }

  protected setLevelField(index: number, field: string): void {
    this.levels.update((levels) => levels.map((l, i) => (i === index ? { ...l, field } : l)));
  }

  protected setLevelDirection(index: number, direction: string): void {
    this.levels.update((levels) =>
      levels.map((l, i) =>
        i === index ? { ...l, direction: direction === 'desc' ? 'desc' : 'asc' } : l,
      ),
    );
  }

  protected apply(event: Event): void {
    event.preventDefault();
    const seen = new Set<string>();
    const sort: SortSpec[] = [];
    for (const level of this.levels()) {
      if (!level.field || level.field.startsWith('unsortable:') || seen.has(level.field)) continue;
      seen.add(level.field);
      sort.push({ field: level.field, direction: level.direction });
    }
    this.ref.close({ columns: this.shown(), sort });
  }
}
