import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Button, Icon, MENU } from '../../../ui';
import type { GridView } from './grid-view-api';

/**
 * The View controls above the document list (E16-T09, familiarity guide §3.1): the View selector (Default, shared
 * views, my views), "Modified" while the list differs from the View, Columns, and the Views menu (save changes, save as
 * new view, rename or share, reset, delete). The grid owns the state; this only shows it and reports choices.
 */
@Component({
  selector: 'opp-grid-view-bar',
  imports: [Button, Icon, ...MENU],
  templateUrl: './grid-view-bar.html',
  styleUrl: './grid-view-bar.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid-view-bar' },
})
export class GridViewBar {
  readonly sharedViews = input.required<readonly GridView[]>();
  readonly myViews = input.required<readonly GridView[]>();
  readonly activeView = input.required<GridView | null>();
  readonly modified = input(false);

  readonly selected = output<string | null>();
  readonly columns = output<void>();
  readonly saveChanges = output<void>();
  readonly saveAs = output<'create' | 'edit'>();
  readonly reset = output<void>();
  readonly deleted = output<void>();

  protected readonly name = computed(() => this.activeView()?.name ?? 'Default');

  protected onSelect(event: Event): void {
    this.selected.emit((event.target as HTMLSelectElement).value || null);
  }
}
