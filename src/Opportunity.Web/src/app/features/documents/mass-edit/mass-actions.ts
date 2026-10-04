import { ChangeDetectionStrategy, Component, Injector, inject, input } from '@angular/core';
import type { FieldResource } from '../../../core/api/generated/models';
import { CommandRegistry } from '../../../core/commands';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Button, DialogService, Icon, MENU, ToastService } from '../../../ui';
import { SelectionTarget, selectionProblem } from '../grid/selection';
import type { MassEditData } from './mass-edit-dialog';

const CODING_BULK = 'Coding.Bulk';
const CODING_WRITE_PRIVILEGE = 'Coding.WritePrivilege';
const JOB_VIEW_ALL = 'Job.ViewAll';

/**
 * The Mass Actions menu over the document list's selection (familiarity guide §3.6). Mass Edit… (Coding.Bulk) opens
 * the bulk coding dialog; Export to Load File…, Export List (CSV)… and Apply to Family… join it when their backends
 * exist (E12-T01, E09-T05). Alt+Shift+E opens Mass Edit from the list.
 */
@Component({
  selector: 'opp-mass-actions',
  imports: [Button, Icon, ...MENU],
  // Hidden for roles without any mass action (an empty menu would be a dead end).
  template: `@if (canBulkCode) {
      <button type="button" oppButton="secondary" [cdkMenuTriggerFor]="menu">
        Mass Actions<opp-icon name="chevron-down" />
      </button>
    }
    <ng-template #menu>
      <div cdkMenu class="opp-menu" aria-label="Mass Actions">
        <button
          type="button"
          cdkMenuItem
          class="opp-menu__item"
          (cdkMenuItemTriggered)="massEdit()"
        >
          Mass Edit…
        </button>
      </div>
    </ng-template>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MassActions {
  /** The list's selection (null when nothing is selected). */
  readonly target = input.required<SelectionTarget | null>();
  /** The list's count label ("≈ 58,330"). */
  readonly listCount = input.required<string>();
  readonly fields = input.required<readonly FieldResource[] | null>();
  /** Where focus goes when the dialog closes: the document list. */
  readonly returnFocus = input<() => HTMLElement | undefined>(() => undefined);

  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly context = inject(WorkspaceContext);
  private readonly injector = inject(Injector);
  protected readonly canBulkCode = this.context.can(CODING_BULK);

  constructor() {
    inject(CommandRegistry).handle('actions.massEdit', () => void this.massEdit(), {
      enabled: () => this.canBulkCode,
    });
  }

  /** Opens Mass Edit for the selection; the dialog's code is loaded on first use (kept out of the list's chunk). */
  async massEdit(): Promise<void> {
    const target = this.target();
    const problem = selectionProblem(target);
    if (problem || !target) {
      this.toasts.show(problem ?? '', { tone: 'warning' });
      return;
    }
    const returnTo = this.returnFocus()();
    const { MassEditDialog } = await import('./mass-edit-dialog');
    this.dialogs.open<boolean, MassEditData>(MassEditDialog, {
      data: {
        target,
        listCount: this.listCount(),
        fields: this.fields() ?? [],
        canWritePrivilege: this.context.can(CODING_WRITE_PRIVILEGE),
        showGeneration: this.context.can(JOB_VIEW_ALL),
        timeZone: this.context.workspace.displayTimeZone || 'UTC',
      },
      width: '40rem',
      injector: this.injector,
      restoreFocus: returnTo ?? true,
    });
  }
}
