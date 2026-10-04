import { _IdGenerator } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { Badge, Checkbox, Icon, Select, TextField } from '../../../ui';
import { ChoiceAction, EditMode, EditableField, FieldEdit } from './field-edits';

/**
 * One field of the Mass Edit dialog: a "Change" checkbox and, when ticked, what to do. Single-value fields are set
 * or cleared; Multiple Choice fields add / remove / leave each choice, replace all values, or clear them.
 * Security-affecting fields are marked "Affects access" and locked without the privilege coding permission.
 */
@Component({
  selector: 'opp-mass-edit-field',
  imports: [Badge, Checkbox, Icon, Select, TextField],
  templateUrl: './mass-edit-field.html',
  styleUrl: './mass-edit-field.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.is-on]': 'edit().change', '[attr.data-field]': 'field().fieldId' },
})
export class MassEditField {
  readonly field = input.required<EditableField>();
  readonly edit = input.required<FieldEdit>();
  readonly problem = input<string | undefined>();
  readonly patch = output<Partial<FieldEdit>>();

  protected readonly name = inject(_IdGenerator).getId('opp-mass-edit-mode-');

  protected readonly modes = computed<readonly { mode: EditMode; label: string }[]>(() =>
    this.field().field.type === 'multiChoice'
      ? [
          { mode: 'addRemove', label: 'Add or remove choices' },
          { mode: 'replace', label: 'Replace all values with' },
          { mode: 'clear', label: 'Clear all values' },
        ]
      : [
          { mode: 'set', label: 'Set to' },
          { mode: 'clear', label: 'Clear the value' },
        ],
  );

  protected readonly choiceOptions = computed(() =>
    this.field().choices.map((c) => ({ value: c.id, label: c.name })),
  );

  protected readonly booleanOptions = [
    { value: 'true', label: 'Yes' },
    { value: 'false', label: 'No' },
  ];

  protected readonly inputType = computed(() =>
    this.field().field.type === 'date' ? ('date' as const) : ('text' as const),
  );

  protected setChoice(choiceId: string, action: string): void {
    // Only this choice: the dialog merges it into the field's choices.
    this.patch.emit({ choices: { [choiceId]: action as ChoiceAction } });
  }

  protected toggleReplace(choiceId: string, on: boolean): void {
    const current = new Set(this.edit().replace);
    if (on) current.add(choiceId);
    else current.delete(choiceId);
    this.patch.emit({
      replace: this.field()
        .choices.map((c) => c.id)
        .filter((id) => current.has(id)),
    });
  }
}
