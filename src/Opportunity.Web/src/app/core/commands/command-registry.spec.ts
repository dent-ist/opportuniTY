import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PLATFORM } from './chord';
import {
  CommandRegionDirective,
  CommandRegistry,
  CommandScopeDirective,
  cycleRegion,
} from './command-registry';
import { Keymap } from './keymap';

const DOCS = ['ACM0000101', 'ACM0000102', 'ACM0000103'];
const CHOICES = ['Responsive', 'Not Responsive', 'Needs Further Review'];

/**
 * A stand-in for the review screen (E16): viewer and coding regions inside the review scope, a choice field
 * that takes digits like the real coding pane, and the review-flow commands registered the way features will.
 */
@Component({
  imports: [CommandScopeDirective, CommandRegionDirective],
  template: `<div oppCommandScope="review">
    <section oppCommandRegion oppCommandScope="viewer" aria-label="Viewer">
      <p class="doc">{{ docs[index()] }}</p>
    </section>
    <section oppCommandRegion oppCommandScope="coding" aria-label="Coding">
      <fieldset class="field" (keydown)="onChoiceKey($event)">
        <legend>Responsiveness</legend>
        @for (choice of choices; track choice; let i = $index) {
          <label
            ><input
              type="radio"
              name="resp"
              [checked]="value() === choice"
              (change)="value.set(choice)"
            />
            ({{ i + 1 }}) {{ choice }}</label
          >
        }
      </fieldset>
      <label class="field">Comments <input class="comments" type="text" /></label>
    </section>
  </div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class ReviewHost {
  protected readonly docs = DOCS;
  protected readonly choices = CHOICES;
  readonly index = signal(0);
  readonly value = signal<string | null>(null);
  readonly saved: { doc: string; value: string | null }[] = [];
  readonly searchFocused = vi.fn();
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  constructor() {
    const commands = inject(CommandRegistry);
    commands.handle('review.saveAndNext', () => {
      this.saved.push({ doc: DOCS[this.index()], value: this.value() });
      this.next();
    });
    commands.handle('document.next', () => this.next());
    commands.handle('document.previous', () => this.index.update((i) => Math.max(0, i - 1)));
    commands.handle('coding.focus', ({ digit }) => {
      const fields = this.host.querySelectorAll<HTMLElement>('.field');
      const field = fields[(digit ?? 1) - 1];
      field?.querySelector<HTMLElement>('input')?.focus();
    });
    commands.handle('search.focus', this.searchFocused);
  }

  protected onChoiceKey(event: KeyboardEvent): void {
    const n = /^Digit([1-9])$/.exec(event.code);
    if (!n || event.altKey || event.ctrlKey || event.metaKey) return;
    const choice = CHOICES[Number(n[1]) - 1];
    if (choice) this.value.set(choice);
    event.preventDefault();
  }

  private next(): void {
    this.index.update((i) => Math.min(DOCS.length - 1, i + 1));
    this.value.set(null);
  }
}

describe('Command registry', () => {
  let registry: CommandRegistry;

  beforeEach(async () => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [{ provide: PLATFORM, useValue: 'pc' }] });
    registry = TestBed.inject(CommandRegistry);
    registry.start();
    await registry.ready();
  });

  function press(
    code: string,
    init: KeyboardEventInit = {},
    target?: Element | null,
  ): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { code, bubbles: true, cancelable: true, ...init });
    (target ?? document.activeElement ?? document.body).dispatchEvent(event);
    return event;
  }

  function mount() {
    const fixture = TestBed.createComponent(ReviewHost);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host: fixture.componentInstance,
      el,
      doc: () => {
        fixture.detectChanges();
        return el.querySelector('.doc')!.textContent;
      },
      region: (label: string) => el.querySelector<HTMLElement>(`[aria-label="${label}"]`)!,
    };
  }

  it('runs the code → save → next loop with the keyboard only', () => {
    const { host, el, doc, region } = mount();
    region('Viewer').focus();
    expect(doc()).toBe('ACM0000101');

    // Alt+Shift+C focuses the coding pane; 1 jumps to field 1; 2 then picks choice 2 in that field.
    press('KeyC', { altKey: true, shiftKey: true });
    press('Digit1');
    expect(document.activeElement?.closest('fieldset')).not.toBeNull();
    press('Digit2');
    expect(host.value()).toBe('Not Responsive');
    // Ctrl+Enter saves and moves on, from inside the field.
    expect(press('Enter', { ctrlKey: true }).defaultPrevented).toBe(true);
    expect(doc()).toBe('ACM0000102');

    // Next document: code it, then type a comment; Save & Next works from the text box.
    press('KeyC', { altKey: true, shiftKey: true });
    press('Digit1');
    press('Digit3');
    press('KeyC', { altKey: true, shiftKey: true });
    press('Digit2');
    const comments = el.querySelector<HTMLInputElement>('.comments')!;
    expect(document.activeElement).toBe(comments);
    // Single keys type in text fields instead of running commands.
    expect(press('Slash', { key: '/' }).defaultPrevented).toBe(false);
    expect(press('BracketRight', { key: ']' }).defaultPrevented).toBe(false);
    expect(host.searchFocused).not.toHaveBeenCalled();
    press('Enter', { ctrlKey: true });

    expect(host.saved).toEqual([
      { doc: 'ACM0000101', value: 'Not Responsive' },
      { doc: 'ACM0000102', value: 'Needs Further Review' },
    ]);
    expect(doc()).toBe('ACM0000103');
  });

  it('cycles focus through the regions with Alt+Shift+G and Alt+Shift+B', () => {
    const { region } = mount();
    // The app shell registers the region commands like this.
    const off = [
      registry.register('region.next', () => cycleRegion(document, 1)),
      registry.register('region.previous', () => cycleRegion(document, -1)),
    ];
    region('Viewer').focus();
    press('KeyG', { altKey: true, shiftKey: true });
    expect(document.activeElement).toBe(region('Coding'));
    press('KeyG', { altKey: true, shiftKey: true });
    expect(document.activeElement).toBe(region('Viewer'));
    press('KeyB', { altKey: true, shiftKey: true });
    expect(document.activeElement).toBe(region('Coding'));
    off.forEach((f) => f());
  });

  it('runs single keys outside text fields, and none at all once they are turned off', () => {
    const { region, doc, host } = mount();
    region('Viewer').focus();
    press('BracketRight', { key: ']' });
    expect(doc()).toBe('ACM0000102');
    press('Slash', { key: '/' });
    expect(host.searchFocused).toHaveBeenCalledTimes(1);

    TestBed.inject(Keymap).singleKeyEnabled.set(false);
    expect(press('BracketRight', { key: ']' }).defaultPrevented).toBe(false);
    press('Slash', { key: '/' });
    expect(doc()).toBe('ACM0000102');
    expect(host.searchFocused).toHaveBeenCalledTimes(1);
    // The modified chords still work.
    press('Period', { altKey: true, shiftKey: true });
    expect(doc()).toBe('ACM0000103');
  });

  it('only runs a command in its scope and leaves keys in dialogs and menus alone', () => {
    const { region, doc } = mount();
    // Outside the review scope, review commands do not run.
    document.body.focus();
    expect(press('Period', { altKey: true, shiftKey: true }, document.body).defaultPrevented).toBe(
      false,
    );
    expect(doc()).toBe('ACM0000101');

    const dialog = document.createElement('div');
    dialog.setAttribute('role', 'dialog');
    const button = document.createElement('button');
    dialog.appendChild(button);
    region('Viewer').appendChild(dialog);
    button.focus();
    press('Period', { altKey: true, shiftKey: true });
    expect(doc()).toBe('ACM0000101');
    dialog.remove();
  });

  it('follows a rebinding at once', () => {
    const { region, doc } = mount();
    TestBed.inject(Keymap).addKey('document.next', 'Alt+Shift+KeyN');
    region('Viewer').focus();
    press('KeyN', { altKey: true, shiftKey: true });
    expect(doc()).toBe('ACM0000102');
  });

  it('ignores auto-repeat for commands that must not repeat', () => {
    const { region, doc } = mount();
    region('Viewer').focus();
    const event = press('Period', { altKey: true, shiftKey: true, repeat: true });
    expect(event.defaultPrevented).toBe(true);
    expect(doc()).toBe('ACM0000101');
  });

  it('lists the commands available in the focused region', () => {
    const { region } = mount();
    region('Coding').focus();
    expect(registry.activeScopes()).toEqual(['coding', 'review', 'global']);
    expect(registry.registered().has('review.saveAndNext')).toBe(true);
  });
});
