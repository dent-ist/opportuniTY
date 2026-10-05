import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ApiError, toApiError } from '../../core/api/problem-details';
import {
  HIGHLIGHT_COLOR_LABELS,
  HighlightSet,
  HighlightSetsApi,
  HighlightTerm,
  HttpHighlightSetsApi,
  highlightColor,
} from '../../core/highlights/highlight-sets';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import {
  Announcer,
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  IconButton,
  LoadingState,
  Select,
  SelectOption,
  TextField,
} from '../../ui';

interface Draft {
  readonly name: string;
  readonly color: string;
  readonly terms: string;
}

const EMPTY: Draft = { name: '', color: 'amber', terms: '' };

/**
 * Admin › Highlight Sets (E16-T12, familiarity guide §3.2): workspace term lists with a colour that reviewers switch
 * on in the viewer's highlight bar. One term per line: a word, a "phrase", a wildcard (terminat*) or a W/n proximity;
 * several words on a line are a phrase. The server validates every line and the errors are shown per line. Changes
 * need `HighlightSet.Manage`, are sent with If-Match and are audited.
 */
@Component({
  selector: 'opp-highlight-sets-page',
  imports: [Button, EmptyState, ErrorState, Icon, IconButton, LoadingState, Select, TextField],
  templateUrl: './highlight-sets-page.html',
  styleUrl: './highlight-sets-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: HighlightSetsApi, useClass: HttpHighlightSetsApi }],
  host: { class: 'hs-page' },
})
export class HighlightSetsPage {
  private readonly api = inject(HighlightSetsApi);
  private readonly dialogs = inject(DialogService);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);
  private readonly injector = inject(Injector);
  private readonly nameField = viewChild<TextField>('nameField');
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly sets = signal<readonly HighlightSet[]>([]);
  protected readonly colors = signal<readonly string[]>([
    'amber',
    'green',
    'blue',
    'violet',
    'rose',
    'teal',
  ]);
  /** The set being edited; null with `editing` for a new one. */
  protected readonly editing = signal<HighlightSet | null | undefined>(undefined);
  protected readonly draft = signal<Draft>(EMPTY);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string[]>>({});
  protected readonly saveError = signal<string | null>(null);

  protected readonly colorOptions = computed<SelectOption[]>(() =>
    this.colors().map((c) => ({ value: c, label: HIGHLIGHT_COLOR_LABELS[highlightColor(c)] })),
  );
  protected readonly lines = computed(() => termLines(this.draft().terms));
  /** Server errors of the terms, by line (1-based), with the line's text. */
  protected readonly termErrors = computed(() => {
    const lines = this.lines();
    return Object.entries(this.errors())
      .map(([key, messages]) => {
        const match = /^terms\[(\d+)\]/.exec(key);
        if (!match) return null;
        const index = Number(match[1]);
        return { line: index + 1, text: lines[index] ?? '', message: messages.join(' ') };
      })
      .filter((e): e is { line: number; text: string; message: string } => e !== null)
      .sort((a, b) => a.line - b.line);
  });
  protected readonly swatch = (color: string) => highlightColor(color);
  protected readonly colorLabel = (color: string) => HIGHLIGHT_COLOR_LABELS[highlightColor(color)];

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const list = await this.api.list();
      this.sets.set(list.sets);
      if (list.colors.length) this.colors.set(list.colors);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected preview(set: HighlightSet): string {
    return set.terms
      .slice(0, 6)
      .map((t) => t.expression)
      .join(' · ');
  }

  protected date(value: string): string {
    return new Intl.DateTimeFormat(this.prefs.locale(), { dateStyle: 'medium' }).format(
      new Date(value),
    );
  }

  protected startNew(): void {
    this.open(null, EMPTY);
  }

  protected edit(set: HighlightSet): void {
    this.open(set, {
      name: set.name,
      color: set.color,
      terms: set.terms.map((t) => t.expression).join('\n'),
    });
  }

  private open(set: HighlightSet | null, draft: Draft): void {
    this.editing.set(set);
    this.draft.set(draft);
    this.errors.set({});
    this.saveError.set(null);
    afterNextRender(() => this.nameField()?.focus(), { injector: this.injector });
  }

  protected cancel(): void {
    this.editing.set(undefined);
    afterNextRender(() => this.heading()?.nativeElement.focus(), { injector: this.injector });
  }

  protected update(patch: Partial<Draft>): void {
    this.draft.update((d) => ({ ...d, ...patch }));
  }

  protected onTerms(event: Event): void {
    this.update({ terms: (event.target as HTMLTextAreaElement).value });
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const current = this.editing();
    if (current === undefined || this.saving()) return;
    const draft = this.draft();
    const known = new Map((current?.terms ?? []).map((t) => [t.expression, t]));
    const terms: HighlightTerm[] = this.lines().map((expression) => ({
      expression,
      termId: known.get(expression)?.termId ?? null,
      color: known.get(expression)?.color ?? null,
    }));
    const local: Record<string, string[]> = {};
    if (!draft.name.trim()) local['name'] = ['Enter a name.'];
    if (!terms.length) local['terms'] = ['Enter at least one term.'];
    this.errors.set(local);
    if (Object.keys(local).length) return;
    this.saving.set(true);
    this.saveError.set(null);
    try {
      const body = { name: draft.name.trim(), description: null, color: draft.color, terms };
      const saved = current ? await this.api.update(current, body) : await this.api.create(body);
      this.announcer.announce(`Highlight set ${saved.name} saved`);
      this.editing.set(undefined);
      await this.load();
    } catch (e) {
      const error = toApiError(e);
      const fields = (error.problem as { errors?: Record<string, string[]> }).errors;
      if (error.status === 400 && fields) {
        this.errors.set(fields);
        this.saveError.set('Some terms cannot be highlighted. Correct the lines listed below.');
      } else if (error.status === 409) {
        this.errors.set({ name: ['Another highlight set has this name.'] });
      } else if (error.status === 412) {
        this.saveError.set(
          'This set was changed by someone else. Cancel and open it again to see the latest terms.',
        );
      } else {
        this.saveError.set(error.problem.detail ?? error.message);
      }
    } finally {
      this.saving.set(false);
    }
  }

  protected async remove(set: HighlightSet): Promise<void> {
    const confirmed = await this.dialogs.confirm({
      title: `Delete ${set.name}?`,
      message: 'Reviewers will no longer see these terms highlighted. This cannot be undone.',
      confirmLabel: 'Delete highlight set',
      tone: 'danger',
    });
    if (!confirmed) return;
    try {
      await this.api.delete(set);
      this.announcer.announce(`Highlight set ${set.name} deleted`);
      if (this.editing()?.highlightSetId === set.highlightSetId) this.editing.set(undefined);
      await this.load();
    } catch (e) {
      this.saveError.set(toApiError(e).problem.detail ?? 'The highlight set was not deleted.');
    }
  }
}

/** The non-blank lines of the terms box, trimmed. */
export function termLines(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter(Boolean);
}
