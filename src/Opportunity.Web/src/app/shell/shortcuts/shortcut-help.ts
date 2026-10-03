import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommandRegistry } from '../../core/commands';
import {
  COMMAND_GROUPS,
  COMMANDS,
  CommandScope,
  FIXED_KEYS,
  SCOPE_LABELS,
} from '../../core/commands/command-catalog';
import { Keymap } from '../../core/commands/keymap';
import { Button, Checkbox, DialogLayout } from '../../ui';
import { KeyChord } from './key-chord';

export interface ShortcutHelpData {
  /** Scopes around the element that had focus when the cheat sheet was opened, innermost first. */
  scopes: readonly CommandScope[];
  /** Opens the customisation dialog (after this one closes). */
  customise: () => void;
}

interface Row {
  id: string;
  label: string;
  where: string;
  chords: readonly string[];
  fixed?: string;
  detail?: string;
}

/**
 * The `?` cheat sheet (familiarity guide §4): the shortcuts that work where focus was, grouped like the guide's
 * table, with the user's own keys. "Show all commands" lists every command with the region it works in.
 */
@Component({
  selector: 'opp-shortcut-help',
  imports: [Button, Checkbox, DialogLayout, KeyChord],
  template: `<opp-dialog-layout title="Keyboard shortcuts">
    <p class="help__context">
      @if (showAll()) {
        All commands and the region each one works in.
      } @else {
        Shortcuts available in {{ regionLabel() }}.
      }
      Single-key shortcuts are <strong>{{ keymap.singleKeyEnabled() ? 'on' : 'off' }}</strong
      >.
    </p>
    <opp-checkbox [(checked)]="showAll">Show all commands</opp-checkbox>
    @for (group of groups(); track group.name) {
      <section class="help__group" [attr.aria-labelledby]="'shortcut-group-' + $index">
        <h3 class="help__group-title" [id]="'shortcut-group-' + $index">{{ group.name }}</h3>
        <dl class="help__list">
          @for (row of group.rows; track row.id) {
            <div class="help__row">
              <dt>
                {{ row.label }}
                @if (showAll()) {
                  <span class="help__where">{{ row.where }}</span>
                }
                @if (row.detail) {
                  <span class="help__where">{{ row.detail }}</span>
                }
              </dt>
              <dd>
                @if (row.fixed) {
                  <kbd>{{ row.fixed }}</kbd>
                } @else {
                  @for (chord of row.chords; track chord) {
                    <opp-key-chord [chord]="chord" />
                  } @empty {
                    <span class="help__none">No key</span>
                  }
                }
              </dd>
            </div>
          }
        </dl>
      </section>
    } @empty {
      <p>No shortcuts here.</p>
    }
    <ng-container dialogActions>
      <button type="button" oppButton="ghost" (click)="customise()">Customise shortcuts…</button>
      <button type="button" oppButton="primary" data-autofocus (click)="ref.close()">Close</button>
    </ng-container>
  </opp-dialog-layout>`,
  styleUrl: './shortcuts.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShortcutHelp {
  protected readonly ref = inject(DialogRef);
  protected readonly keymap = inject(Keymap);
  private readonly registry = inject(CommandRegistry);
  private readonly data = inject<ShortcutHelpData>(DIALOG_DATA);
  protected readonly showAll = signal(false);

  protected readonly regionLabel = computed(() => {
    const inner = this.data.scopes[0];
    return inner && inner !== 'global' ? `the ${SCOPE_LABELS[inner].toLowerCase()}` : 'this page';
  });

  protected readonly groups = computed(() => {
    const all = this.showAll();
    this.registry.registered();
    const scopes = this.data.scopes;
    const rows = new Map<string, Row[]>();
    for (const command of COMMANDS) {
      if (!all && !this.registry.isAvailable(command, scopes)) continue;
      const list = rows.get(command.group) ?? [];
      list.push({
        id: command.id,
        label: command.label,
        where: SCOPE_LABELS[command.scope],
        chords: this.keymap.activeKeysFor(command.id),
        detail: command.takesDigit,
      });
      rows.set(command.group, list);
    }
    const fixed = FIXED_KEYS.filter((k) => all || scopes.includes(k.scope)).map((k): Row => ({
      id: k.label,
      label: k.label,
      where: SCOPE_LABELS[k.scope],
      chords: [],
      fixed: k.keys,
    }));
    if (fixed.length) rows.set('Other keys', fixed);
    return [...COMMAND_GROUPS, 'Other keys']
      .filter((name) => rows.has(name))
      .map((name) => ({ name, rows: rows.get(name)! }));
  });

  protected customise(): void {
    this.ref.close();
    this.data.customise();
  }
}
