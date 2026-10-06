import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList } from '@angular/cdk/drag-drop';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { toApiError } from '../../core/api/problem-details';
import { AdminChoice, AdminField, FieldAdminApi } from '../../core/fields/field-admin-api';
import { Announcer, Badge, Button, DialogService, Icon, IconButton, TextField } from '../../ui';
import { moved } from './field-admin-model';

/**
 * The choices of a Single or Multiple Choice field (E04-T06, ADR-003 R8): add, rename, reorder (drag by the handle,
 * or Move up / Move down, or Alt+Arrow keys), deactivate and reactivate. A choice that was ever used can only be
 * deactivated: documents keep it and it stays readable and searchable, but reviewers can no longer pick it. Each
 * change is saved at once with the field's version and returns the updated field.
 */
@Component({
  selector: 'opp-choice-editor',
  imports: [Badge, Button, CdkDrag, CdkDragHandle, CdkDropList, Icon, IconButton, TextField],
  templateUrl: './choice-editor.html',
  styleUrl: './field-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChoiceEditor {
  readonly field = input.required<AdminField>();
  /** The field after a change (new version and choices). */
  readonly changed = output<AdminField>();

  private readonly api = inject(FieldAdminApi);
  private readonly announcer = inject(Announcer);
  private readonly dialogs = inject(DialogService);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  protected readonly newName = signal('');
  protected readonly renaming = signal<number | null>(null);
  protected readonly renameValue = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected choices(): readonly AdminChoice[] {
    return this.field().choices ?? [];
  }

  protected async add(event: Event): Promise<void> {
    event.preventDefault();
    const name = this.newName().trim();
    if (!name) {
      this.error.set('Enter a name for the new choice.');
      return;
    }
    if (await this.run(() => this.api.addChoice(this.field(), name), `Choice ${name} added`)) {
      this.newName.set('');
    }
  }

  protected startRename(choice: AdminChoice): void {
    this.renaming.set(choice.choiceId);
    this.renameValue.set(choice.name);
    this.error.set(null);
    afterNextRender(
      () =>
        this.host
          .querySelector<HTMLInputElement>(`[data-rename="${choice.choiceId}"] input`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  protected cancelRename(): void {
    const id = this.renaming();
    this.renaming.set(null);
    if (id !== null) this.focusRow(id, 'Rename');
  }

  protected async rename(event: Event, choice: AdminChoice): Promise<void> {
    event.preventDefault();
    const name = this.renameValue().trim();
    if (!name) {
      this.error.set('A choice needs a name.');
      return;
    }
    if (name === choice.name) {
      this.cancelRename();
      return;
    }
    const ok = await this.run(
      () => this.api.updateChoice(this.field(), choice.choiceId, { name }),
      `Choice renamed to ${name}`,
    );
    if (ok) {
      this.renaming.set(null);
      this.focusRow(choice.choiceId, 'Rename');
    }
  }

  protected async setActive(choice: AdminChoice, isActive: boolean): Promise<void> {
    await this.run(
      () => this.api.updateChoice(this.field(), choice.choiceId, { isActive }),
      `${choice.name} ${isActive ? 'reactivated' : 'deactivated'}`,
    );
    this.focusRow(choice.choiceId, isActive ? 'Deactivate' : 'Reactivate');
  }

  protected async remove(choice: AdminChoice): Promise<void> {
    const confirmed = await this.dialogs.confirm({
      title: `Delete ${choice.name}?`,
      message: 'This choice has never been used, so no document loses a value.',
      confirmLabel: 'Delete choice',
      tone: 'danger',
    });
    if (!confirmed) return;
    await this.run(
      () => this.api.deleteChoice(this.field(), choice.choiceId),
      `Choice ${choice.name} deleted`,
    );
  }

  protected async move(index: number, delta: number): Promise<void> {
    const list = this.choices();
    const to = index + delta;
    if (to < 0 || to >= list.length) return;
    await this.reorder(index, to, delta < 0 ? 'up' : 'down');
  }

  protected drop(event: CdkDragDrop<unknown>): void {
    if (event.previousIndex !== event.currentIndex) {
      void this.reorder(event.previousIndex, event.currentIndex, null);
    }
  }

  /** Alt+Arrow Up/Down anywhere in a choice's row moves it. */
  protected onRowKeydown(event: KeyboardEvent, index: number): void {
    if (!event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
    event.preventDefault();
    void this.move(index, event.key === 'ArrowUp' ? -1 : 1);
  }

  private async reorder(from: number, to: number, direction: 'up' | 'down' | null): Promise<void> {
    const list = this.choices();
    const order = moved(list, from, to);
    const choice = list[from];
    const ok = await this.run(
      () =>
        this.api.reorderChoices(
          this.field(),
          order.map((c) => c.choiceId),
        ),
      `${choice.name} moved to position ${to + 1} of ${list.length}`,
    );
    if (ok && direction)
      this.focusRow(choice.choiceId, direction === 'up' ? 'Move up' : 'Move down');
  }

  private async run(change: () => Promise<AdminField>, announcement: string): Promise<boolean> {
    if (this.busy()) return false;
    this.busy.set(true);
    this.error.set(null);
    try {
      const field = await change();
      this.changed.emit(field);
      this.announcer.announce(announcement);
      return true;
    } catch (e) {
      const error = toApiError(e);
      const fields = (error.problem as { errors?: Record<string, string[]> }).errors;
      this.error.set(
        error.status === 412
          ? 'Someone else changed this field. Reopen it to see the latest choices.'
          : fields
            ? Object.values(fields).flat().join(' ')
            : (error.problem.detail ?? 'The change was not saved.'),
      );
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  /** Puts focus back on a choice's button after the list re-renders. */
  private focusRow(choiceId: number, action: string): void {
    afterNextRender(
      () => {
        const row = this.host.querySelector<HTMLElement>(`[data-choice-id="${choiceId}"]`);
        const target =
          row?.querySelector<HTMLButtonElement>(
            `button[data-action="${action}"]:not([disabled])`,
          ) ?? row?.querySelector<HTMLButtonElement>('button:not([disabled])');
        target?.focus();
      },
      { injector: this.injector },
    );
  }
}
