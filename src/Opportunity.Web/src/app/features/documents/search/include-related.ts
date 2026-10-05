import { ChangeDetectionStrategy, Component, model } from '@angular/core';
import type { SearchExpand } from '../../../core/api/generated/models';
import { Checkbox } from '../../../ui';

/** "Include: Family / Duplicates / Email thread" (E09-T03, ticket review E09-T03: the same labels everywhere). */
export interface IncludeRelated {
  readonly family: boolean;
  readonly duplicates: boolean;
  readonly thread: boolean;
}

export const NO_RELATED: IncludeRelated = { family: false, duplicates: false, thread: false };

export function anyRelated(value: IncludeRelated): boolean {
  return value.family || value.duplicates || value.thread;
}

/**
 * The `expand` of a search or frozen set. `explicit`: always say what is wanted, even nothing, so a saved search's
 * stored choice does not apply when the person unticked it.
 */
export function toExpand(value: IncludeRelated, explicit = false): SearchExpand | null {
  return anyRelated(value) || explicit
    ? { family: value.family, duplicates: value.duplicates, thread: value.thread }
    : null;
}

/** A saved search's stored choice. */
export function includeOf(saved: {
  readonly includeFamily?: boolean | null;
  readonly includeDuplicates?: boolean | null;
  readonly includeThread?: boolean | null;
}): IncludeRelated {
  return {
    family: !!saved.includeFamily,
    duplicates: !!saved.includeDuplicates,
    thread: !!saved.includeThread,
  };
}

/**
 * The Include toggles of the Documents search panel: the list then also holds the families, duplicates and email
 * threads of the hits, marked as added rows, with the base hits and the expanded size both in the list header.
 * Documents the reviewer may not see are never added. Native checkboxes in a labelled group: Tab reaches each one,
 * Space toggles it.
 */
@Component({
  selector: 'opp-include-related',
  imports: [Checkbox],
  template: `<fieldset class="include">
    <legend class="include__legend">Include</legend>
    <opp-checkbox [checked]="value().family" (checkedChange)="set('family', $event)"
      >Family</opp-checkbox
    >
    <opp-checkbox [checked]="value().duplicates" (checkedChange)="set('duplicates', $event)"
      >Duplicates</opp-checkbox
    >
    <opp-checkbox [checked]="value().thread" (checkedChange)="set('thread', $event)"
      >Email thread</opp-checkbox
    >
  </fieldset>`,
  styleUrl: './include-related.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IncludeRelatedToggles {
  readonly value = model<IncludeRelated>(NO_RELATED);

  protected set(key: keyof IncludeRelated, checked: boolean): void {
    this.value.set({ ...this.value(), [key]: checked });
  }
}
