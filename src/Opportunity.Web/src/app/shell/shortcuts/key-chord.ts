import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Chord } from '../../core/commands';
import { Keymap } from '../../core/commands/keymap';
import { formatChord, speakChord } from '../../core/commands/chord-format';

/** A key chord as `<kbd>`: shown the platform's way (`Alt+Shift+K`, `⌥⇧K`), read out in words. */
@Component({
  selector: 'opp-key-chord',
  template: `<kbd
    ><span aria-hidden="true">{{ shown() }}</span
    ><span class="opp-visually-hidden">{{ spoken() }}</span></kbd
  >`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class KeyChord {
  readonly chord = input.required<Chord>();
  private readonly platform = inject(Keymap).platform;
  protected readonly shown = computed(() => formatChord(this.chord(), this.platform));
  protected readonly spoken = computed(() => speakChord(this.chord(), this.platform));
}
