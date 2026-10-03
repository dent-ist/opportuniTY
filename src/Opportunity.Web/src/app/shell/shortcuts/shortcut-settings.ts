import { DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  Injector,
  signal,
  viewChild,
} from '@angular/core';
import { Chord, chordFromEvent, isCharacterKey } from '../../core/commands';
import {
  COMMAND_GROUPS,
  COMMANDS,
  CommandDefinition,
  SCOPE_LABELS,
} from '../../core/commands/command-catalog';
import { Keymap } from '../../core/commands/keymap';
import { formatChord, speakChord } from '../../core/commands/chord-format';
import { bindingProblem } from '../../core/commands/reserved-keys';
import { Announcer, Button, Checkbox, DialogLayout, Icon, IconButton } from '../../ui';
import { KeyChord } from './key-chord';

interface Capture {
  command: CommandDefinition;
  /** A chord that is taken by other commands, waiting for "Replace" or "Cancel". */
  conflict?: { chord: Chord; message: string };
  error?: string;
}

/**
 * Rebinding (E15-T03): every command with its keys, "Add key" captures the next chord, with conflict detection
 * against commands that can be active at the same time and against browser, OS and screen-reader keys. The
 * single-key switch turns all character-key shortcuts off (WCAG 2.1.4). Changes save to the user profile.
 */
@Component({
  selector: 'opp-shortcut-settings',
  imports: [Button, Checkbox, DialogLayout, Icon, IconButton, KeyChord],
  template: `<opp-dialog-layout title="Customise keyboard shortcuts">
    <opp-checkbox
      [checked]="keymap.singleKeyEnabled()"
      (checkedChange)="keymap.singleKeyEnabled.set($event)"
      >Single-key shortcuts</opp-checkbox
    >
    <p class="settings__hint">
      Letters, digits and punctuation pressed without {{ modifiers }}, such as <kbd>/</kbd> or
      <kbd>]</kbd>. They never act while you type in a text field. Turn them off if you use speech
      input or a screen reader's single-key navigation.
    </p>

    @for (group of groups; track group.name) {
      <section class="settings__group" [attr.aria-labelledby]="'settings-group-' + $index">
        <h3 class="help__group-title" [id]="'settings-group-' + $index">{{ group.name }}</h3>
        <ul class="settings__list">
          @for (command of group.commands; track command.id) {
            <li class="settings__row">
              <div class="settings__command">
                <span class="settings__label">{{ command.label }}</span>
                <span class="help__where">{{ where(command) }}</span>
              </div>
              <div class="settings__keys">
                @for (chord of keymap.keysFor(command.id); track chord) {
                  <span class="settings__chip" [class.settings__chip--off]="isOff(chord)">
                    <opp-key-chord [chord]="chord" />
                    @if (isOff(chord)) {
                      <span class="help__where">(off)</span>
                    }
                    <button
                      type="button"
                      oppIconButton
                      [label]="'Remove ' + spoken(chord) + ' from ' + command.label"
                      (click)="remove(command, chord)"
                    >
                      <opp-icon name="close" />
                    </button>
                  </span>
                } @empty {
                  <span class="help__none">No key</span>
                }
              </div>
              <div class="settings__actions">
                <button
                  type="button"
                  oppButton="ghost"
                  [attr.data-add-key]="command.id"
                  [attr.aria-label]="'Add key for ' + command.label"
                  (click)="startCapture(command)"
                >
                  Add key
                </button>
                @if (keymap.isCustomised(command.id)) {
                  <button
                    type="button"
                    oppButton="ghost"
                    [attr.aria-label]="'Reset ' + command.label"
                    (click)="reset(command)"
                  >
                    Reset
                  </button>
                }
              </div>
              @if (capture()?.command === command) {
                <div class="settings__capture">
                  @if (capture()!.conflict; as conflict) {
                    <p role="alert">{{ conflict.message }}</p>
                    <div class="settings__capture-actions">
                      <button type="button" oppButton="primary" (click)="replace()" #replaceButton>
                        Use for {{ command.label }}
                      </button>
                      <button type="button" oppButton="ghost" (click)="cancelCapture()">
                        Cancel
                      </button>
                    </div>
                  } @else {
                    <div
                      #captureBox
                      class="settings__capture-box"
                      tabindex="0"
                      role="textbox"
                      aria-readonly="true"
                      [attr.aria-label]="'New key for ' + command.label"
                      [attr.aria-describedby]="'capture-help-' + command.id"
                      (keydown)="onCaptureKey($event)"
                      (blur)="onCaptureBlur()"
                    >
                      Press the new keys…
                    </div>
                    <p class="settings__hint" [id]="'capture-help-' + command.id">
                      Press the key combination for {{ command.label }}. Esc cancels.
                    </p>
                    @if (capture()!.error; as error) {
                      <p class="settings__error" role="alert">{{ error }}</p>
                    }
                  }
                </div>
              }
            </li>
          }
        </ul>
      </section>
    }
    <ng-container dialogActions>
      <button type="button" oppButton="ghost" (click)="resetAll()">Reset all to defaults</button>
      <button type="button" oppButton="primary" (click)="ref.close()">Done</button>
    </ng-container>
  </opp-dialog-layout>`,
  styleUrl: './shortcuts.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShortcutSettings {
  protected readonly ref = inject(DialogRef);
  protected readonly keymap = inject(Keymap);
  private readonly announcer = inject(Announcer);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly captureBox = viewChild<ElementRef<HTMLElement>>('captureBox');
  private readonly replaceButton = viewChild('replaceButton', { read: ElementRef<HTMLElement> });

  protected readonly modifiers =
    this.keymap.platform === 'mac' ? '⌘, Control or Option' : 'Ctrl or Alt';
  protected readonly capture = signal<Capture | null>(null);
  protected readonly groups = COMMAND_GROUPS.map((name) => ({
    name,
    commands: COMMANDS.filter((c) => c.group === name),
  })).filter((g) => g.commands.length > 0);
  private readonly singleKeyOn = computed(() => this.keymap.singleKeyEnabled());

  protected where(command: CommandDefinition): string {
    const scope = SCOPE_LABELS[command.scope];
    return command.takesDigit ? `${scope}; ${command.takesDigit}` : scope;
  }

  protected isOff(chord: Chord): boolean {
    return !this.singleKeyOn() && isCharacterKey(chord);
  }

  protected spoken(chord: Chord): string {
    return speakChord(chord, this.keymap.platform);
  }

  protected startCapture(command: CommandDefinition): void {
    this.capture.set({ command });
    afterNextRender(() => this.captureBox()?.nativeElement.focus(), { injector: this.injector });
  }

  protected onCaptureKey(event: KeyboardEvent): void {
    const capture = this.capture();
    if (!capture) return;
    if (event.key === 'Tab') return; // Leaving the box cancels (blur).
    event.preventDefault();
    event.stopPropagation();
    if (event.key === 'Escape') {
      this.cancelCapture();
      return;
    }
    const chord = chordFromEvent(event, this.keymap.platform);
    if (!chord) return; // A modifier on its own: wait for the key.
    const problem = bindingProblem(this.keymap, capture.command.id, chord);
    if (!problem) {
      this.keymap.addKey(capture.command.id, chord);
      this.finish(capture.command, `${this.spoken(chord)} added to ${capture.command.label}.`);
    } else if (problem.conflicts.length > 0) {
      this.capture.set({
        command: capture.command,
        conflict: { chord, message: `${this.shown(chord)}: ${problem.reason}` },
      });
      afterNextRender(() => this.replaceButton()?.nativeElement.focus(), {
        injector: this.injector,
      });
    } else {
      this.capture.set({
        ...capture,
        error: `${this.shown(chord)} cannot be used. ${problem.reason}`,
      });
    }
  }

  protected onCaptureBlur(): void {
    // Tabbing away from the capture box gives up, unless a conflict question replaced it.
    queueMicrotask(() => {
      if (this.capture() && !this.capture()!.conflict) this.capture.set(null);
    });
  }

  protected replace(): void {
    const capture = this.capture();
    if (!capture?.conflict) return;
    const { chord } = capture.conflict;
    this.keymap.addKey(capture.command.id, chord, { replace: true });
    this.finish(capture.command, `${this.spoken(chord)} now runs ${capture.command.label}.`);
  }

  protected cancelCapture(): void {
    const command = this.capture()?.command;
    this.capture.set(null);
    if (command) this.focusAddButton(command);
  }

  protected remove(command: CommandDefinition, chord: Chord): void {
    this.keymap.removeKey(command.id, chord);
    this.announcer.announce(`${this.spoken(chord)} removed from ${command.label}.`);
    this.focusAddButton(command);
  }

  protected reset(command: CommandDefinition): void {
    this.keymap.reset(command.id);
    this.announcer.announce(`${command.label} reset to its default keys.`);
    this.focusAddButton(command);
  }

  protected resetAll(): void {
    this.capture.set(null);
    this.keymap.resetAll();
    this.announcer.announce('All shortcuts reset to the defaults.');
  }

  private finish(command: CommandDefinition, message: string): void {
    this.capture.set(null);
    this.announcer.announce(message);
    this.focusAddButton(command);
  }

  private focusAddButton(command: CommandDefinition): void {
    afterNextRender(
      () =>
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-add-key="${command.id}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  private shown(chord: Chord): string {
    return formatChord(chord, this.keymap.platform);
  }
}
