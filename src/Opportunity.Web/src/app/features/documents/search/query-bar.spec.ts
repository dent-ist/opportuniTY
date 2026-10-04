import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { CommandRegistry } from '../../../core/commands';
import { FakeApi, provideFakeApi } from '../../../core/api/fake-api.testing';
import type {
  QueryHistoryEntryResource,
  QueryHistoryRequest,
  QueryValidationRequest,
  SearchRequest,
} from '../../../core/api/generated/models';
import { provideOpportunityHttp } from '../../../core/api/http';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { fakePage } from '../grid/grid-fixtures.testing';
import { goldenCases, goldenResult } from './golden.testing';
import { QUERY_BAR_TIMING, QueryBarTiming } from './query-bar';
import { TEST_FIELDS, TEST_FIELD_RESOURCES } from './search-fixtures.testing';

const VALIDATE = '/api/v1/workspaces/ws-1/query-validations';
const FIELDS = '/api/v1/workspaces/ws-1/fields';
const HISTORY = '/api/v1/workspaces/ws-1/query-history';
const SEARCHES = '/api/v1/workspaces/ws-1/searches';
const golden = new Map(goldenCases().map((c) => [c.query, goldenResult(c)]));
/** An invalid result as the server builds it (rules of the golden error cases, at other offsets). */
function invalid(
  code: string,
  message: string,
  start: number,
  end: number,
  expected: string[] = [],
) {
  return {
    valid: false,
    astVersion: 1,
    errors: [{ code, message, span: { start, end }, expected }],
    warnings: [],
  };
}
golden.set(
  'contract AND "trade secret',
  invalid('UNTERMINATED_PHRASE', 'The phrase has no closing quote.', 13, 26),
);
golden.set(
  'contract\nAND',
  invalid('SYNTAX_ERROR', 'The query ends after AND; a term is missing.', 12, 12),
);
golden.set(
  'contract\n"open',
  invalid('UNTERMINATED_PHRASE', 'The phrase has no closing quote.', 9, 14),
);

describe('Query bar (Documents search panel)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let announce: ReturnType<typeof vi.spyOn>;
  /** The server's copy of the user's history in ws-1: outlives the page, like PostgreSQL. */
  let stored: QueryHistoryEntryResource[];

  beforeEach(() => {
    stored = [];
  });

  async function setup(timing?: QueryBarTiming): Promise<void> {
    api = new FakeApi()
      .on('GET', FIELDS, {
        body: {
          items: TEST_FIELD_RESOURCES,
          nextCursor: null,
          total: { value: TEST_FIELD_RESOURCES.length, relation: 'eq' },
        },
      })
      .on('GET', HISTORY, () => ({ body: { items: stored } }))
      .on('POST', HISTORY, (req: HttpRequest<unknown>) => {
        const query = (req.body as QueryHistoryRequest).query!.trim();
        stored = [
          { query, ranAt: new Date().toISOString() },
          ...stored.filter((e) => e.query !== query),
        ].slice(0, 50);
        return { status: 204 };
      })
      .on('GET', '/api/v1/me', {
        body: {
          userId: 'u-1',
          displayName: 'Alex Reviewer',
          email: 'alex@example.test',
          groups: [],
        },
      })
      .on('GET', '/api/v1/workspaces/ws-1', {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          permissions: [PERMISSIONS.documentView, PERMISSIONS.searchExecute],
        },
      })
      .on('POST', SEARCHES, { body: fakePage({ total: 3, pageSize: 100 }, 1) })
      .on('POST', VALIDATE, (req: HttpRequest<unknown>) => {
        const query = (req.body as QueryValidationRequest).query ?? '';
        return {
          body: golden.get(query) ?? {
            valid: true,
            astVersion: 1,
            normalized: query.trim(),
            errors: [],
            warnings: [],
          },
        };
      });
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        ...(timing ? [{ provide: QUERY_BAR_TIMING, useValue: timing }] : []),
      ],
    });
    announce = vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/documents');
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, ms));
    await harness.fixture.whenStable();
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const textbox = () => root().querySelector('textarea') as HTMLTextAreaElement;
  const query = (selector: string) => root().querySelector<HTMLElement>(selector);
  const texts = (selector: string) =>
    [...root().querySelectorAll(selector)].map((e) => e.textContent?.trim());

  function type(value: string, caret = value.length): void {
    const el = textbox();
    el.focus();
    el.value = value;
    el.setSelectionRange(caret, caret);
    el.dispatchEvent(new Event('input'));
  }

  function key(
    key: string,
    init: KeyboardEventInit = {},
    target: Element = textbox(),
  ): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
    target.dispatchEvent(event);
    return event;
  }

  async function search(text: string): Promise<void> {
    type(text);
    key('Enter');
    await settle(5);
  }

  /** Queries the document list ran: the page opens on every document (''), then one per search. */
  const searched = () =>
    api.requests
      .filter((r) => r.method === 'POST' && r.url === SEARCHES)
      .map((r) => (r.body as SearchRequest).query);

  it('renders an accessible keyword box in the Documents search panel', async () => {
    await setup();
    const el = textbox();
    expect(query('label[for]')?.textContent?.trim()).toBe('Keyword');
    expect(el.getAttribute('aria-autocomplete')).toBe('list');
    expect(el.getAttribute('aria-describedby')).toContain('-hint');
    expect(query('[aria-hidden="true"].qb__mirror')).not.toBeNull();
    await expectNoAxeViolations(root());
  });

  it('shows an invalid query inline at the offending token, announces it and does not search', async () => {
    await setup({ validateMs: 0, suggestMs: 0 });
    type('contract AND "trade secret');
    await settle(5);

    expect(textbox().getAttribute('aria-invalid')).toBe('true');
    const errorsId = textbox().getAttribute('aria-describedby')!.split(' ')[1];
    const message = root().querySelector(`#${errorsId}`)?.textContent?.trim();
    expect(message).toBe('Error: The phrase has no closing quote (at character 14).');
    // The highlight layer marks exactly the span the server returned.
    expect(texts('.qb-t--error')).toEqual(['"trade secret']);

    key('Enter');
    await settle(5);
    expect(searched()).toEqual(['']);
    expect(announce).toHaveBeenCalledWith(
      'Search not run. The phrase has no closing quote (at character 14).',
      'assertive',
    );
    expect([textbox().selectionStart, textbox().selectionEnd]).toEqual([13, 26]);
    await expectNoAxeViolations(root());
  });

  it('checks a query typed faster than the debounce before running it', async () => {
    await setup({ validateMs: 10_000, suggestMs: 0 });
    type('contract AND');
    key('Enter');
    await settle(5);
    expect(api.requests.filter((r) => r.url === VALIDATE)).toHaveLength(1);
    expect(searched()).toEqual(['']);
    expect(query('.qb-t--point')).not.toBeNull(); // empty span at the end: missing operand
    expect(root().textContent).toContain(
      'a term is missing (at the end of the query). Expected: term, phrase, field:, (, NOT.',
    );
  });

  it('marks a missing ")" and lets the reviewer jump to it', async () => {
    await setup({ validateMs: 0, suggestMs: 0 });
    await search('(a OR b');
    const go = [...root().querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Go to error'),
    )!;
    textbox().setSelectionRange(0, 0);
    go.click();
    const error = golden.get('(a OR b')!.errors[0];
    expect(textbox().selectionStart).toBe(Number(error.span.start));
    expect(document.activeElement).toBe(textbox());
  });

  const examples: [string, [string, string][]][] = [
    [
      'contract AND termination',
      [
        ['operator', 'AND'],
        ['term', 'contract'],
      ],
    ],
    ['"trade secret"', [['phrase', '"trade secret"']]],
    [
      'apple W/10 iphone',
      [
        ['proximity', 'W/10'],
        ['term', 'iphone'],
      ],
    ],
    [
      'custodian:"John Smith"',
      [
        ['field', 'custodian:'],
        ['phrase', '"John Smith"'],
      ],
    ],
    [
      'date:[2025-01-01 TO 2025-12-31]',
      [
        ['field', 'date:'],
        ['range-bracket', '['],
        ['range-to', 'TO'],
        ['range-bound', '2025-12-31'],
      ],
    ],
    [
      'filename:*.xlsx',
      [
        ['field', 'filename:'],
        ['wildcard', '*.xlsx'],
      ],
    ],
  ];

  for (const [example, expected] of examples) {
    it(`runs and highlights the §9 example ${example}`, async () => {
      await setup({ validateMs: 0, suggestMs: 0 });
      await search(example);
      expect(textbox().getAttribute('aria-invalid')).toBeNull();
      expect(query('.qb-t--error')).toBeNull();
      expect(searched()).toEqual(['', example]);
      for (const [kind, text] of expected) expect(texts(`.qb-t--${kind}`)).toContain(text);
      expect(query('.qb__mirror')?.textContent).toBe(`${example} `);
    });
  }

  it('shows lower-case operator warnings and the server interpretation', async () => {
    await setup({ validateMs: 0, suggestMs: 0 });
    type('contract and termination');
    await settle(5);
    expect(texts('.qb-t--warning')).toEqual(['and']);
    expect(root().textContent).toContain(
      "'and' is searched as a word. Did you mean AND? Operators must be upper case (at character 10).",
    );
    expect(query('.qb__code')?.textContent).toBe('contract AND and AND termination');
    expect(textbox().getAttribute('aria-invalid')).toBeNull();
  });

  it('suggests fields within 150 ms and choice values after field:, from the keyboard', async () => {
    await setup(); // default timing
    type('contract AND resp');
    await settle(150);
    const list = () => query('[role="listbox"]');
    expect(texts('[role="option"] .qb__option-label')).toEqual(['responsiveness']);
    expect(textbox().getAttribute('aria-controls')).toBe(list()?.id);
    expect(announce).toHaveBeenCalledWith('1 suggestion. Down Arrow to choose.', 'polite');

    key('ArrowDown');
    await settle();
    const active = textbox().getAttribute('aria-activedescendant')!;
    expect(root().querySelector(`#${active}`)?.getAttribute('aria-selected')).toBe('true');
    key('Enter');
    await settle();
    expect(textbox().value).toBe('contract AND responsiveness:');
    expect(texts('[role="option"] .qb__option-label')).toEqual([
      'Responsive',
      'Not Responsive',
      'Needs Further Review',
      '*',
    ]);

    key('ArrowDown');
    key('Enter');
    await settle();
    expect(textbox().value).toBe('contract AND responsiveness:"Not Responsive" ');
    expect(list()).toBeNull();
    expect(searched()).toEqual(['']); // inserting is not searching
    await expectNoAxeViolations(root());
  });

  it('opens field suggestions on demand and closes them with Escape', async () => {
    await setup({ validateMs: 0, suggestMs: 0 });
    type('');
    key(' ', { ctrlKey: true });
    await settle();
    expect(texts('[role="option"]').length).toBe(TEST_FIELDS.length);
    const escape = key('Escape');
    await settle();
    expect(escape.defaultPrevented).toBe(true);
    expect(query('[role="listbox"]')).toBeNull();
  });

  it('keeps recent searches per workspace, newest first, and recalls them with Alt+Down', async () => {
    await setup({ validateMs: 0, suggestMs: 0 });
    await search('contract AND termination');
    await search('"trade secret"');
    await search('contract AND'); // invalid: not recorded
    await search('contract AND termination');

    key('ArrowDown', { altKey: true });
    await settle();
    expect(texts('[role="option"] .qb__option-label')).toEqual([
      'contract AND termination',
      '"trade secret"',
    ]);
    key('ArrowDown');
    key('Enter');
    await settle();
    expect(textbox().value).toBe('"trade secret"');
    expect(query('[role="listbox"]')).toBeNull();
    expect(
      api.requests.filter((r) => r.method === 'POST' && r.url === HISTORY).map((r) => r.body),
    ).toEqual([
      { query: 'contract AND termination' },
      { query: '"trade secret"' },
      { query: 'contract AND termination' },
    ]);
  });

  it('shows the history the server kept from earlier sessions and never stores it in the browser', async () => {
    stored = [
      { query: 'custodian:"Smith"', ranAt: '2026-10-02T09:00:00.000Z' },
      { query: 'pricing', ranAt: '2026-10-01T09:00:00.000Z' },
    ];
    localStorage.clear();
    sessionStorage.clear();
    await setup({ validateMs: 0, suggestMs: 0 });
    await search('termination');

    key('ArrowDown', { altKey: true });
    await settle();
    expect(texts('[role="option"] .qb__option-label')).toEqual([
      'termination',
      'custodian:"Smith"',
      'pricing',
    ]);
    expect(stored.map((e) => e.query)).toEqual(['termination', 'custodian:"Smith"', 'pricing']);
    const browserStorage = JSON.stringify({ ...localStorage, ...sessionStorage });
    expect(browserStorage).not.toContain('termination');
    expect(browserStorage).not.toContain('Smith');
  });

  it('offers the custom fields and choices of the workspace catalogue, without unsearchable fields', async () => {
    await setup({ validateMs: 0, suggestMs: 0 });
    type('');
    key(' ', { ctrlKey: true });
    await settle();
    const labels = texts('[role="option"] .qb__option-label');
    expect(labels).toContain('privilege_status');
    expect(labels).not.toContain('internal_note');
    expect(api.urls()).toContain(FIELDS);
  });

  it('focuses the keyword box with Alt+Shift+K, or / while single-key shortcuts are on', async () => {
    await setup();
    const keymap = await TestBed.inject(CommandRegistry).ready();
    (document.activeElement as HTMLElement | null)?.blur();
    key('K', { altKey: true, shiftKey: true, code: 'KeyK' }, document.body);
    expect(document.activeElement).toBe(textbox());

    // In the keyword box, / is typed, not a shortcut.
    expect(key('/', { code: 'Slash' }).defaultPrevented).toBe(false);
    textbox().blur();
    expect(key('/', { code: 'Slash' }, document.body).defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(textbox());

    // WCAG 2.1.4: with single-key shortcuts off, / does nothing; Alt+Shift+K still works.
    keymap.singleKeyEnabled.set(false);
    textbox().blur();
    expect(key('/', { code: 'Slash' }, document.body).defaultPrevented).toBe(false);
    expect(document.activeElement).not.toBe(textbox());
    key('K', { altKey: true, shiftKey: true, code: 'KeyK' }, document.body);
    expect(document.activeElement).toBe(textbox());
  });

  it('expands to a multi-line editor and reports line and character', async () => {
    await setup({ validateMs: 0, suggestMs: 0 });
    const toggle = query('button[aria-label="Multi-line editor"]')!;
    toggle.click();
    await settle();
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    expect(textbox().rows).toBe(5);
    type('contract\nAND');
    await settle(5);
    expect(root().textContent).toContain('at the end of the query');
    type('contract\n"open');
    await settle(5);
    expect(texts('.qb__msgs--error li span')[0]).toContain('at line 2, character 1');
  });

  it('opens the syntax help without naming any other product', async () => {
    await setup();
    const help = query('button[aria-label="Search syntax help"]')!;
    help.click();
    await settle();
    expect(help.getAttribute('aria-expanded')).toBe('true');
    const card = query('opp-query-syntax-help')!;
    expect(card.id).toBe(help.getAttribute('aria-controls'));
    expect(card.textContent).toContain('W/10');
    expect(card.textContent).not.toMatch(/dtsearch|relativity|lucene|opensearch/i);
    await expectNoAxeViolations(root());
  });
});
