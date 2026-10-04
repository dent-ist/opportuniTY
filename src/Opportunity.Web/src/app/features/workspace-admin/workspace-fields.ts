import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { Select, TextField } from '../../ui';
import {
  MATTER_NUMBER_MAX,
  NAME_MAX,
  type DraftErrors,
  type WorkspaceDraft,
  timeZoneOptions,
} from './workspace-form';

/**
 * Name, matter number and display time zone of a workspace: shared by New workspace and Workspace Settings so both
 * ask the same way.
 */
@Component({
  selector: 'opp-workspace-fields',
  imports: [Select, TextField],
  template: `
    <opp-text-field
      label="Workspace name"
      [hint]="'The name everyone sees in the workspace list. Up to ' + nameMax + ' characters.'"
      [required]="true"
      [disabled]="disabled()"
      [error]="errors().name"
      [value]="draft().name"
      (valueChange)="set('name', $event)"
    />
    <opp-text-field
      label="Matter number"
      [hint]="
        'Optional. Your matter or case reference, shown beside the name. Up to ' +
        matterMax +
        ' characters.'
      "
      [disabled]="disabled()"
      [error]="errors().matterNumber"
      [value]="draft().matterNumber"
      (valueChange)="set('matterNumber', $event)"
    />
    <opp-select
      label="Display time zone"
      hint="Dates and times in this workspace are shown in this time zone."
      [required]="true"
      [disabled]="disabled()"
      [options]="zones()"
      [error]="errors().displayTimeZone"
      [value]="draft().displayTimeZone"
      (valueChange)="set('displayTimeZone', $event)"
    />
  `,
  styleUrl: './workspace-fields.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceFields {
  readonly draft = model.required<WorkspaceDraft>();
  readonly errors = input<DraftErrors>({});
  readonly disabled = input(false);

  protected readonly nameMax = NAME_MAX;
  protected readonly matterMax = MATTER_NUMBER_MAX;
  protected readonly zones = computed(() => timeZoneOptions(this.draft().displayTimeZone));

  protected set(key: keyof WorkspaceDraft, value: string): void {
    this.draft.update((d) => ({ ...d, [key]: value }));
  }
}
