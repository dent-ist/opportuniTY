import {
  DOCUMENT,
  DestroyRef,
  Directive,
  Injectable,
  Injector,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { PLATFORM, chordFromEvent, chordParts, isEditableTarget, isUnmodified } from './chord';
import type { CommandDefinition, CommandScope } from './command-catalog';
import type { Keymap } from './keymap';

export interface CommandInvocation {
  /** The digit typed right after a `takesDigit` command (Alt+Shift+C, then 3 → field 3). */
  readonly digit?: number;
}

export interface CommandHandlerOptions {
  /** The handler is skipped (and the command not listed as available) while this returns false. */
  readonly enabled?: () => boolean;
}

interface Registration {
  readonly run: (invocation: CommandInvocation) => void;
  readonly enabled?: () => boolean;
}

/** How long after a `takesDigit` chord a digit still counts as its argument. */
export const DIGIT_WINDOW_MS = 1500;

const SCOPE_ATTR = 'data-command-scope';
/** Inside these, keys belong to the widget (dialogs and menus handle their own keys). */
const OWN_KEYS = '[role="dialog"],[role="alertdialog"],[role="menu"],[role="menubar"]';

/**
 * Central keyboard command registry (E15-T03, familiarity guide §4). Commands and their default keys are the
 * catalogue (`COMMANDS`); the user's keys come from `Keymap`; features register what a command does while
 * they are on screen (`handle`). One document listener dispatches a key press to the command bound to it in
 * the innermost active scope, where the scopes are the `[oppCommandScope]` regions around the focused element
 * plus `global`.
 *
 * Single-key (unmodified) bindings never fire while typing in a text field, and character keys can be turned
 * off altogether (WCAG 2.1.4). Keys pressed inside dialogs and menus are left to them.
 *
 * The catalogue and key map are a separate chunk that `start()` fetches at once, in parallel with the
 * session check, so they stay out of the initial bundle; keys pressed before it arrives are left alone.
 */
@Injectable({ providedIn: 'root' })
export class CommandRegistry {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly platform = inject(PLATFORM);
  private readonly destroyRef = inject(DestroyRef);
  private readonly keymap = signal<Keymap | null>(null);
  private loading?: Promise<Keymap>;
  private readonly handlers = new Map<string, Registration[]>();
  private readonly version = signal(0);
  private awaitingDigit?: { id: string; until: number };
  private started = false;

  /** Ids of commands that have a handler on screen now. */
  readonly registered = computed(() => {
    this.version();
    return new Set([...this.handlers.entries()].filter(([, r]) => r.length > 0).map(([id]) => id));
  });

  /** Starts listening for key presses (the app shell calls this once). */
  start(): void {
    if (this.started) return;
    this.started = true;
    // The digit after a `takesDigit` chord is taken in the capture phase, before the focused widget sees it
    // (Alt+Shift+C, then 2 jumps to field 2 even though digits toggle choices in a focused choice field).
    const onDigit = (event: KeyboardEvent) => this.onDigit(event);
    const onKeydown = (event: KeyboardEvent) => this.onKeydown(event);
    this.document.addEventListener('keydown', onDigit, true);
    this.document.addEventListener('keydown', onKeydown);
    this.destroyRef.onDestroy(() => {
      this.document.removeEventListener('keydown', onDigit, true);
      this.document.removeEventListener('keydown', onKeydown);
    });
    // Fails only when the app (or a test) is torn down before the chunk arrives.
    this.ready().catch(() => undefined);
  }

  /** The user's key map, once its chunk has loaded. */
  ready(): Promise<Keymap> {
    this.loading ??= import('./keymap').then(({ Keymap }) => {
      const keymap = this.injector.get(Keymap);
      this.keymap.set(keymap);
      return keymap;
    });
    return this.loading;
  }

  /** Registers `run` for `commandId` until `unregister` is called. The latest registration wins. */
  register(
    commandId: string,
    run: (invocation: CommandInvocation) => void,
    options: CommandHandlerOptions = {},
  ): () => void {
    const registration: Registration = { run, enabled: options.enabled };
    this.handlers.set(commandId, [...(this.handlers.get(commandId) ?? []), registration]);
    this.version.update((v) => v + 1);
    return () => {
      this.handlers.set(
        commandId,
        (this.handlers.get(commandId) ?? []).filter((r) => r !== registration),
      );
      this.version.update((v) => v + 1);
    };
  }

  /** `register` for the lifetime of the current injection context (a component or directive). */
  handle(
    commandId: string,
    run: (invocation: CommandInvocation) => void,
    options: CommandHandlerOptions = {},
  ): void {
    inject(DestroyRef).onDestroy(this.register(commandId, run, options));
  }

  /** Scopes active for keys typed at `element`, innermost first, always ending with `global`. */
  activeScopes(element: Element | null = this.document.activeElement): CommandScope[] {
    const scopes: CommandScope[] = [];
    for (
      let el = element?.closest(`[${SCOPE_ATTR}]`);
      el;
      el = el.parentElement?.closest(`[${SCOPE_ATTR}]`)
    ) {
      const scope = el.getAttribute(SCOPE_ATTR) as CommandScope;
      if (!scopes.includes(scope)) scopes.push(scope);
    }
    scopes.push('global');
    return scopes;
  }

  /** The command would run now from `scopes`: it has an enabled handler and its scope is active. */
  isAvailable(command: CommandDefinition, scopes: readonly CommandScope[]): boolean {
    return scopes.includes(command.scope) && this.handlerFor(command.id) !== undefined;
  }

  /** Runs a command directly (menus, buttons, tests). Returns false when nothing handles it. */
  invoke(commandId: string, invocation: CommandInvocation = {}): boolean {
    const handler = this.handlerFor(commandId);
    if (!handler) return false;
    handler.run(invocation);
    return true;
  }

  private handlerFor(commandId: string): Registration | undefined {
    const list = this.handlers.get(commandId) ?? [];
    for (let i = list.length - 1; i >= 0; i--) {
      if (list[i].enabled?.() ?? true) return list[i];
    }
    return undefined;
  }

  private onDigit(event: KeyboardEvent): void {
    const pending = this.awaitingDigit;
    if (!pending || /^(Shift|Alt|Control|Meta)/.test(event.code)) return;
    this.awaitingDigit = undefined;
    const digit = /^(Digit|Numpad)([1-9])$/.exec(event.code);
    if (!digit || event.ctrlKey || event.metaKey || event.altKey || Date.now() > pending.until)
      return;
    event.preventDefault();
    event.stopPropagation();
    this.invoke(pending.id, { digit: Number(digit[2]) });
  }

  private onKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented || event.isComposing) return;
    const target = event.target instanceof Element ? event.target : null;
    if (target?.closest(OWN_KEYS)) return;

    const keymap = this.keymap();
    const chord = keymap && chordFromEvent(event, this.platform);
    if (!chord) return;
    const candidates = keymap.active().get(chord);
    if (!candidates?.length) return;
    if (isUnmodified(chord) && !/^F\d+$/.test(chordParts(chord).code) && isEditableTarget(target)) {
      return;
    }

    const scopes = this.activeScopes(target ?? this.document.activeElement);
    const command = candidates
      .filter((c) => this.isAvailable(c, scopes))
      .sort((a, b) => scopes.indexOf(a.scope) - scopes.indexOf(b.scope))[0];
    if (!command) return;
    event.preventDefault();
    if (event.repeat && !command.repeatable) return;
    this.invoke(command.id);
    if (command.takesDigit) {
      this.awaitingDigit = { id: command.id, until: Date.now() + DIGIT_WINDOW_MS };
    }
  }
}

/**
 * Marks a region as a command scope (`grid`, `review`, `viewer`, `coding`, `related`, `redaction`): commands of
 * that scope run while focus is inside it.
 */
@Directive({
  selector: '[oppCommandScope]',
  host: { '[attr.data-command-scope]': 'oppCommandScope()' },
})
export class CommandScopeDirective {
  readonly oppCommandScope = input.required<CommandScope>();
}

/**
 * A focus region for "Next / Previous region" (Alt+Shift+G / Alt+Shift+B): list → viewer → coding → related on
 * the review screen. The region must have an accessible name (`aria-label` or `aria-labelledby`); focus lands
 * on the region itself so screen readers announce it. Regions cycle in document order unless they give an
 * order (`oppCommandRegion="2"`), for layouts whose visual order differs from the source order.
 */
@Directive({
  selector: '[oppCommandRegion]',
  host: { '[attr.data-command-region]': 'oppCommandRegion()', tabindex: '-1' },
})
export class CommandRegionDirective {
  readonly oppCommandRegion = input<string | number>('');
}

/** Moves focus to the next (`step` 1) or previous (-1) visible command region; false when there is none. */
export function cycleRegion(document: Document, step: 1 | -1): boolean {
  const order = (r: HTMLElement) => Number(r.dataset['commandRegion']) || 0;
  const regions = [...document.querySelectorAll<HTMLElement>('[data-command-region]')]
    .filter((r) => !r.closest('[hidden],[inert],[aria-hidden="true"]'))
    .sort((a, b) => order(a) - order(b));
  if (regions.length === 0) return false;
  const active = document.activeElement;
  const current = regions.findIndex((r) => r === active || r.contains(active));
  const next =
    current < 0
      ? step === 1
        ? 0
        : regions.length - 1
      : (current + step + regions.length) % regions.length;
  regions[next].focus();
  return true;
}
