import { _IdGenerator } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  ElementRef,
  InjectionToken,
  computed,
  effect,
  inject,
  input,
  output,
  resource,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, catchError, debounceTime, firstValueFrom, map, of, switchMap } from 'rxjs';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { UiPreferences } from '../../../core/preferences/ui-preferences';
import { QueryHistory } from '../../../core/search/query-history';
import { SearchFieldSource } from '../../../core/search/search-fields';
import { QueryCheck, QueryDiagnostic, QueryValidator } from '../../../core/search/query-validation';
import { WorkspaceContext } from '../../../core/workspace/workspace-context';
import { Announcer, Button, Icon, IconButton } from '../../../ui';
import { Suggestion, completionContext, suggest } from './query-completion';
import { HighlightSegment, highlight, lineAndColumn } from './query-highlight';
import { tokenize } from './query-lexer';
import { QuerySyntaxHelp } from './query-syntax-help';

export interface QueryBarTiming {
  /** Pause after typing before the server checks the query. */
  readonly validateMs: number;
  /** Pause after typing or moving the caret before suggestions update (E16-T01: ≤ 150 ms). */
  readonly suggestMs: number;
}

export const QUERY_BAR_TIMING = new InjectionToken<QueryBarTiming>('QUERY_BAR_TIMING', {
  factory: () => ({ validateMs: 250, suggestMs: 100 }),
});

/** A query the server accepted; the document list runs it. */
export interface QuerySubmission {
  readonly query: string;
  readonly normalized: string | null;
}

interface PopupOption {
  readonly label: string;
  readonly detail: string;
  readonly apply: () => void;
}

type Popup = { mode: 'suggest'; items: readonly Suggestion[] } | { mode: 'history' };

/**
 * Keyword search box of the Documents search panel (E16-T01, familiarity guide §3.1, §5.2). A native
 * textarea over an `aria-hidden` highlight layer: operators, fields, phrases and ranges are coloured from
 * the client lexer, and errors and warnings are marked at the exact spans returned by the validate
 * endpoint. Invalid queries never reach `search`.
 *
 * Keyboard: Enter searches (or inserts the chosen suggestion); Shift+Enter adds a line; Down Arrow or
 * Ctrl+Space shows suggestions, Up/Down move through them, Escape closes them; Alt+Down Arrow shows
 * recent searches. The textbox keeps focus while a list is open (`aria-activedescendant`).
 */
@Component({
  selector: 'opp-query-bar',
  imports: [Button, IconButton, Icon, QuerySyntaxHelp],
  templateUrl: './query-bar.html',
  styleUrl: './query-bar.scss',
  providers: [QueryValidator],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QueryBar {
  readonly label = input('Keyword');
  readonly search = output<QuerySubmission>();

  private readonly validator = inject(QueryValidator);
  private readonly history = inject(QueryHistory);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);
  private readonly timing = inject(QUERY_BAR_TIMING);
  private readonly workspaceId = inject(WorkspaceContext).workspaceId;
  private readonly fieldSource = inject(SearchFieldSource);
  private readonly document = inject(DOCUMENT);
  private readonly input = viewChild.required<ElementRef<HTMLTextAreaElement>>('input');
  private readonly mirror = viewChild.required<ElementRef<HTMLElement>>('mirror');

  protected readonly id = inject(_IdGenerator).getId('opp-query-');
  protected readonly ids = {
    input: `${this.id}-input`,
    hint: `${this.id}-hint`,
    list: `${this.id}-list`,
    errors: `${this.id}-errors`,
    warnings: `${this.id}-warnings`,
    help: `${this.id}-help`,
  };

  readonly text = signal('');
  protected readonly expanded = signal(false);
  protected readonly helpOpen = signal(false);
  protected readonly checking = signal(false);
  private readonly caret = signal(0);
  private readonly check = signal<QueryCheck | null>(null);
  protected readonly checkFailed = signal<ApiError | null>(null);
  protected readonly popup = signal<Popup | null>(null);
  protected readonly active = signal(-1);

  protected readonly fields = resource({
    params: () => this.workspaceId,
    loader: ({ params }) => this.fieldSource.fields(params),
  });

  private readonly tokens = computed(() => tokenize(this.text()));
  /** Diagnostics of the latest check. While a newer check is pending the previous marks stay visible. */
  protected readonly errors = computed(() => this.check()?.errors ?? []);
  protected readonly warnings = computed(() => this.check()?.warnings ?? []);
  protected readonly segments = computed<HighlightSegment[]>(() =>
    highlight(this.text(), this.tokens(), this.errors(), this.warnings()),
  );
  protected readonly interpretation = computed(() => {
    const check = this.check();
    const text = this.text();
    return check?.valid &&
      check.query === text &&
      check.normalized &&
      check.normalized !== text.trim()
      ? check.normalized
      : null;
  });
  protected readonly describedBy = computed(() =>
    [
      this.ids.hint,
      ...this.errors().map((_, i) => `${this.ids.errors}-${i}`),
      ...this.warnings().map((_, i) => `${this.ids.warnings}-${i}`),
    ].join(' '),
  );

  protected readonly options = computed<readonly PopupOption[]>(() => {
    const popup = this.popup();
    if (!popup) return [];
    if (popup.mode === 'suggest') {
      return popup.items.map((s) => ({
        label: s.label,
        detail: s.detail,
        apply: () => this.insert(s),
      }));
    }
    const format = new Intl.DateTimeFormat(this.prefs.locale(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    });
    return this.history.entries().map((e) => ({
      label: e.query,
      detail: format.format(new Date(e.ranAt)),
      apply: () => this.replaceAll(e.query),
    }));
  });
  protected readonly activeId = computed(() =>
    this.popup() && this.active() >= 0 ? `${this.ids.list}-${this.active()}` : null,
  );

  private readonly checks = new Subject<string>();
  private suggestTimer?: ReturnType<typeof setTimeout>;
  /** Ctrl+Space / Down Arrow asked for suggestions; also show fields where nothing is typed. */
  private explicit = false;

  constructor() {
    this.checks
      .pipe(
        debounceTime(this.timing.validateMs),
        switchMap((query) =>
          this.validator.check(query).pipe(
            map((check) => ({ check, error: null })),
            catchError((e: unknown) => of({ check: null, error: toApiError(e) })),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ check, error }) => {
        if (check) this.check.set(check);
        this.checkFailed.set(error);
      });
    void this.history.load();
    inject(DestroyRef).onDestroy(() => clearTimeout(this.suggestTimer));

    effect(() => {
      const id = this.activeId();
      if (!id) return;
      untracked(() => this.document.getElementById(id)?.scrollIntoView?.({ block: 'nearest' }));
    });
  }

  focus(): void {
    this.input().nativeElement.focus();
  }

  protected onInput(event: Event): void {
    const el = event.target as HTMLTextAreaElement;
    this.text.set(el.value);
    this.caret.set(el.selectionStart);
    this.checkFailed.set(null);
    this.checks.next(el.value);
    if (this.popup()?.mode === 'history') this.closePopup();
    this.scheduleSuggestions();
    this.syncScroll();
  }

  /** Caret moved without typing (click, arrows): refresh an open suggestion list. */
  protected onCaretMove(): void {
    const caret = this.input().nativeElement.selectionStart;
    if (caret === this.caret()) return;
    this.caret.set(caret);
    if (this.popup()?.mode === 'suggest') this.scheduleSuggestions();
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.isComposing) return; // IME composition owns Enter and the arrow keys
    const open = this.popup() !== null && this.options().length > 0;
    switch (event.key) {
      case 'ArrowDown':
        if (event.altKey) {
          event.preventDefault();
          this.openHistory();
        } else if (open) {
          event.preventDefault();
          this.move(1);
        } else if (!this.expanded()) {
          event.preventDefault();
          this.showSuggestions(true);
        }
        return;
      case 'ArrowUp':
        if (event.altKey) {
          if (this.popup()) event.preventDefault();
          this.closePopup();
        } else if (open) {
          event.preventDefault();
          this.move(-1);
        }
        return;
      case 'Enter':
        if (event.shiftKey) {
          this.expanded.set(true);
          return;
        }
        event.preventDefault();
        if (open && this.active() >= 0) this.options()[this.active()].apply();
        else {
          this.closePopup();
          void this.submit();
        }
        return;
      case 'Escape':
        if (this.popup()) {
          event.preventDefault();
          event.stopPropagation();
          this.closePopup();
        }
        return;
      case 'Tab':
        this.closePopup();
        return;
      case ' ':
        if (event.ctrlKey) {
          event.preventDefault();
          this.showSuggestions(true);
        }
        return;
    }
  }

  protected onBlur(): void {
    this.closePopup();
  }

  protected toggleHistory(): void {
    if (this.popup()?.mode === 'history') this.closePopup();
    else this.openHistory();
    this.focus();
  }

  protected toggleExpanded(): void {
    this.expanded.update((v) => !v);
    this.focus();
  }

  protected choose(index: number): void {
    this.options()[index]?.apply();
  }

  /** Validates (if the latest check is stale) and emits `search` only for a valid query. */
  async submit(): Promise<void> {
    const query = this.text();
    let check = this.check();
    if (check?.query !== query) {
      this.checking.set(true);
      try {
        check = await firstValueFrom(this.validator.check(query));
        this.check.set(check);
        this.checkFailed.set(null);
      } catch (e) {
        const error = toApiError(e);
        this.checkFailed.set(error);
        this.announcer.announce(`Search not run. ${this.failureText(error)}`, {
          politeness: 'assertive',
        });
        return;
      } finally {
        this.checking.set(false);
      }
    }
    if (!check.valid) {
      const first = check.errors[0];
      this.goTo(first);
      this.announcer.announce(
        `Search not run. ${first ? this.describe(first) : 'The query is not valid.'}`,
        { politeness: 'assertive' },
      );
      return;
    }
    this.history.record(query);
    this.search.emit({ query, normalized: check.normalized });
  }

  clear(): void {
    this.replaceAll('');
    this.check.set(null);
  }

  /** Selects the span of a diagnostic in the textbox (empty spans place the caret). */
  protected goTo(diagnostic: QueryDiagnostic | undefined): void {
    const el = this.input().nativeElement;
    el.focus();
    if (!diagnostic) return;
    const max = el.value.length;
    el.setSelectionRange(Math.min(diagnostic.start, max), Math.min(diagnostic.end, max));
    this.caret.set(el.selectionStart);
    this.syncScroll();
  }

  /** "Message (at character n). Expected: a, b." for the message list and announcements. */
  protected describe(d: QueryDiagnostic): string {
    const message = d.message.trim().replace(/\.$/, '');
    const expected = d.expected.length ? ` Expected: ${d.expected.join(', ')}.` : '';
    return `${message} (${this.where(d)}).${expected}`;
  }

  protected where(d: QueryDiagnostic): string {
    const text = this.text();
    if (d.start >= text.length) return 'at the end of the query';
    const { line, column } = lineAndColumn(text, d.start);
    return text.includes('\n') ? `at line ${line}, character ${column}` : `at character ${column}`;
  }

  protected failureText(error: ApiError): string {
    return error.status === 0
      ? 'The query could not be checked because the server is unreachable.'
      : 'The query could not be checked. Try again.';
  }

  protected syncScroll(): void {
    const el = this.input().nativeElement;
    const mirror = this.mirror().nativeElement;
    mirror.scrollTop = el.scrollTop;
    mirror.scrollLeft = el.scrollLeft;
  }

  protected segmentClass(segment: HighlightSegment): string {
    let cls = segment.kind ? `qb-t qb-t--${segment.kind}` : 'qb-t';
    if (segment.error) cls += segment.text ? ' qb-t--error' : ' qb-t--point';
    if (segment.warning) cls += ' qb-t--warning';
    return cls;
  }

  private scheduleSuggestions(): void {
    clearTimeout(this.suggestTimer);
    this.suggestTimer = setTimeout(
      () => this.showSuggestions(this.explicit),
      this.timing.suggestMs,
    );
  }

  private showSuggestions(explicit: boolean): void {
    clearTimeout(this.suggestTimer);
    const el = this.input().nativeElement;
    if (el.selectionStart !== el.selectionEnd) return;
    const context = completionContext(this.text(), el.selectionStart, explicit, this.tokens());
    const items = context ? suggest(context, this.fields.value() ?? []) : [];
    if (!items.length) {
      this.explicit = false;
      if (this.popup()?.mode === 'suggest') this.closePopup();
      return;
    }
    this.explicit = explicit;
    const previous = this.popup();
    this.popup.set({ mode: 'suggest', items });
    if (explicit && previous?.mode !== 'suggest') this.active.set(0);
    else if (this.active() >= items.length) this.active.set(-1);
    // Announce when the list opens or its size changes, not on every keystroke that keeps it the same.
    if (previous?.mode !== 'suggest' || previous.items.length !== items.length) {
      this.announcer.announce(
        `${items.length} ${items.length === 1 ? 'suggestion' : 'suggestions'}. Down Arrow to choose.`,
      );
    }
  }

  private openHistory(): void {
    this.popup.set({ mode: 'history' });
    const count = this.history.entries().length;
    this.active.set(count ? 0 : -1);
    this.announcer.announce(
      count
        ? `${count} recent ${count === 1 ? 'search' : 'searches'}.`
        : 'No recent searches in this workspace yet.',
    );
  }

  protected closePopup(): void {
    clearTimeout(this.suggestTimer);
    this.explicit = false;
    this.popup.set(null);
    this.active.set(-1);
  }

  private move(delta: number): void {
    const count = this.options().length;
    this.active.update((i) => (i < 0 && delta < 0 ? count - 1 : (i + delta + count) % count));
  }

  private insert(s: Suggestion): void {
    const el = this.input().nativeElement;
    const text = this.text();
    const after = text.slice(s.to);
    const spacer = s.thenValuesOf || /^\s/.test(after) ? '' : ' ';
    const value = text.slice(0, s.from) + s.insert + spacer + after;
    const caret = s.from + s.insert.length + spacer.length;
    this.setText(value, caret);
    this.closePopup();
    if (s.thenValuesOf) this.showSuggestions(true);
    el.focus();
  }

  private replaceAll(query: string): void {
    this.setText(query, query.length);
    this.closePopup();
    if (query.includes('\n')) this.expanded.set(true);
    this.focus();
  }

  private setText(value: string, caret: number): void {
    const el = this.input().nativeElement;
    el.value = value;
    el.setSelectionRange(caret, caret);
    this.text.set(value);
    this.caret.set(caret);
    this.checkFailed.set(null);
    this.checks.next(value);
  }
}
