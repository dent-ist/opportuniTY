import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FakeApi, provideFakeApi } from '../../../../core/api/fake-api.testing';
import { PreferenceStorage } from '../../../../core/preferences/preference-storage';
import { WorkspaceContext } from '../../../../core/workspace/workspace-context';
import { expectNoAxeViolations } from '../../../../ui/testing/axe.testing';
import {
  CodingApi,
  CodingConflictError,
  CodingLayout,
  CodingLayoutField,
  CodingRejectedError,
  CodingSaveOptions,
  CodingValue,
  DocumentCoding,
} from '../review-ports';
import { ReviewCoding } from './coding-pane';
import { PendingCoding } from './pending-coding';

const field = (over: Partial<CodingLayoutField>): CodingLayoutField => ({
  queryName: 'q',
  label: 'Field',
  type: 'text',
  multiValue: false,
  securityAffecting: false,
  datePrecision: null,
  choices: [],
  required: false,
  readOnly: false,
  visibleWhen: null,
  applyToFamilyByDefault: false,
  ...over,
});
const named = (...names: string[]) => names.map((name) => ({ name, active: true }));

const RESPONSIVENESS = field({
  queryName: 'responsiveness',
  label: 'Responsiveness',
  type: 'singleChoice',
  required: true,
  choices: named('Responsive', 'Not Responsive', 'Needs Further Review'),
});
const PRIVILEGE = field({
  queryName: 'privilege_status',
  label: 'Privilege Status',
  type: 'singleChoice',
  securityAffecting: true,
  choices: named('Not Privileged', 'Withhold', 'Redact'),
});
const BASIS = field({
  queryName: 'privilege_basis',
  label: 'Privilege Basis',
  type: 'multiChoice',
  multiValue: true,
  required: true,
  choices: named('Attorney-Client', 'Work Product'),
  visibleWhen: { queryName: 'privilege_status', choices: ['Withhold', 'Redact'], value: null },
});
const ISSUES = field({
  queryName: 'issues',
  label: 'Issues',
  type: 'multiChoice',
  multiValue: true,
  choices: named(...Array.from({ length: 18 }, (_, i) => `Issue ${i + 1}`)),
});
const KEY = field({ queryName: 'key_document', label: 'Key Document', type: 'boolean' });
const COMMENTS = field({ queryName: 'comments', label: 'Reviewer Comments', type: 'text' });

const FIRST_PASS: CodingLayout = {
  id: 'l-first',
  name: 'First Pass Review',
  isDefault: true,
  serverId: 'l-first',
  sections: [
    { title: 'Responsiveness', fields: [RESPONSIVENESS] },
    { title: 'Privilege', fields: [PRIVILEGE, BASIS] },
    { title: 'Issues', fields: [ISSUES, KEY, COMMENTS] },
  ],
};
const DETAILS: CodingLayout = {
  id: 'l-details',
  name: 'Review Details',
  isDefault: false,
  serverId: 'l-details',
  sections: [
    {
      title: 'Details',
      fields: [
        field({
          queryName: 'review_date',
          label: 'Review Date',
          type: 'date',
          datePrecision: 'date',
        }),
        field({
          queryName: 'follow_up',
          label: 'Follow-up At',
          type: 'date',
          datePrecision: 'dateTime',
        }),
        field({ queryName: 'pages', label: 'Pages Reviewed', type: 'integer' }),
        field({ queryName: 'hours', label: 'Hours Spent', type: 'decimal' }),
        field({ queryName: 'reviewer', label: 'Second-Level Reviewer', type: 'user' }),
        field({ queryName: 'terms', label: 'Key Terms', type: 'keyword', multiValue: true }),
        field({ queryName: 'code', label: 'Matter Code', type: 'keyword' }),
      ],
    },
  ],
};

function coding(
  documentId: string,
  values: Record<string, CodingValue> = {},
  over: Partial<DocumentCoding> = {},
): DocumentCoding {
  return {
    documentId,
    version: '3',
    values,
    indexState: 'searchable',
    fields: {},
    lastEditor: null,
    ...over,
  };
}

class FakeCodingApi extends CodingApi {
  layoutList: CodingLayout[] = [FIRST_PASS, DETAILS];
  readonly docs = new Map<string, DocumentCoding>();
  readonly saves: {
    documentId: string;
    version: string;
    values: Readonly<Record<string, CodingValue>>;
    options: CodingSaveOptions;
  }[] = [];
  onSave: (
    documentId: string,
    version: string,
    values: Readonly<Record<string, CodingValue>>,
  ) => Promise<DocumentCoding> = async (documentId, version, values) =>
    coding(
      documentId,
      { ...this.docs.get(documentId)?.values, ...values },
      { version: String(Number(version) + 1), indexState: 'pending' },
    );

  layouts(): Promise<readonly CodingLayout[]> {
    return Promise.resolve(this.layoutList);
  }

  get(documentId: string): Promise<DocumentCoding> {
    return Promise.resolve(this.docs.get(documentId) ?? coding(documentId));
  }

  save(
    documentId: string,
    version: string,
    values: Readonly<Record<string, CodingValue>>,
    options: CodingSaveOptions,
  ): Promise<DocumentCoding> {
    this.saves.push({ documentId, version, values, options });
    return this.onSave(documentId, version, values);
  }
}

@Component({
  imports: [ReviewCoding],
  template: `<opp-review-coding
    [documentId]="documentId()"
    [canCode]="canCode()"
    (move)="moves.push($event)"
  />`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly documentId = signal('doc-1');
  readonly canCode = signal(true);
  readonly moves: string[] = [];
  readonly pane = viewChild.required(ReviewCoding);
}

describe('Coding pane (E16-T05)', () => {
  let api: FakeCodingApi;
  let fixture: ComponentFixture<Host>;
  let permissions: Set<string>;

  async function setup(
    options: { permissions?: string[]; canCode?: boolean; layoutId?: string } = {},
  ): Promise<void> {
    localStorage.clear();
    api = new FakeCodingApi();
    permissions = new Set(options.permissions ?? ['Coding.Write', 'Coding.WritePrivilege']);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideFakeApi(new FakeApi()),
        { provide: CodingApi, useValue: api },
        PendingCoding,
        {
          provide: WorkspaceContext,
          useValue: {
            workspaceId: 'ws-1',
            can: (p: string) => permissions.has(p),
            apiUrl: (...s: string[]) => s.join('/'),
          },
        },
      ],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    if (options.layoutId)
      TestBed.inject(PreferenceStorage).write('coding.layout.ws-1', { layoutId: options.layoutId });
    fixture = TestBed.createComponent(Host);
    fixture.componentInstance.canCode.set(options.canCode ?? true);
    document.body.appendChild(fixture.nativeElement);
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((r) => setTimeout(r, 0));
      await fixture.whenStable();
    }
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const pane = () => fixture.componentInstance.pane();
  const fieldEl = (q: string) => el().querySelector<HTMLElement>(`[data-coding-field="${q}"]`);
  const group = (q: string) => fieldEl(q)?.querySelector<HTMLElement>('fieldset');
  const option = (q: string, label: string) =>
    [...(fieldEl(q)?.querySelectorAll<HTMLLabelElement>('.coding__option') ?? [])]
      .find((l) => l.querySelector('.coding__option-label')?.textContent?.trim() === label)!
      .querySelector('input')!;
  const button = (name: string) =>
    [...el().querySelectorAll('button')].find((b) => b.textContent?.trim() === name)!;
  const key = (target: Element, code: string) =>
    target.dispatchEvent(new KeyboardEvent('keydown', { code, bubbles: true, cancelable: true }));
  const status = () =>
    el().querySelector('[role="status"]')!.textContent!.replace(/\s+/g, ' ').trim();

  afterEach(() => fixture?.nativeElement.remove());

  it('renders the layout: sections in order, required and security markers, access digits, conditional fields', async () => {
    await setup();
    expect([...el().querySelectorAll('.coding__section-title')].map((h) => h.textContent)).toEqual([
      'Responsiveness',
      'Privilege',
      'Issues',
    ]);
    const resp = group('responsiveness')!;
    expect(resp.getAttribute('role')).toBe('radiogroup');
    expect(resp.getAttribute('aria-required')).toBe('true');
    expect(resp.querySelector('legend')!.textContent).toContain('*');
    expect([...resp.querySelectorAll('.coding__digit')].map((d) => d.textContent?.trim())).toEqual([
      '(1)',
      '(2)',
      '(3)',
    ]);
    expect(group('privilege_status')!.querySelector('legend')!.textContent).toContain(
      'Affects access',
    );
    // Privilege Basis shows only while Privilege Status is Withhold or Redact.
    expect(fieldEl('privilege_basis')).toBeNull();
    option('privilege_status', 'Withhold').click();
    await settle();
    expect(fieldEl('privilege_basis')).not.toBeNull();
    // More than 15 choices: checkboxes under a filter box.
    const filter = fieldEl('issues')!.querySelector<HTMLInputElement>('input[type="search"]')!;
    filter.value = '1';
    filter.dispatchEvent(new Event('input'));
    await settle();
    expect(fieldEl('issues')!.querySelectorAll('.coding__option')).toHaveLength(10);
    await expectNoAxeViolations(el());
  }, 30_000);

  it('toggles choices with access digits while a choice field has focus, and tracks unsaved changes', async () => {
    await setup();
    const resp = group('responsiveness')!;
    option('responsiveness', 'Responsive').focus();
    key(document.activeElement!, 'Digit2');
    await settle();
    expect(option('responsiveness', 'Not Responsive').checked).toBe(true);
    expect(document.activeElement).toBe(option('responsiveness', 'Not Responsive'));
    expect(pane().dirty()).toBe(true);
    expect(status()).toContain('Unsaved changes');
    key(resp, 'Digit2'); // the chosen one again clears it
    await settle();
    expect(resp.querySelector('input:checked')).toBeNull();
    expect(pane().dirty()).toBe(false);

    key(group('key_document')!, 'Digit1');
    await settle();
    expect(option('key_document', 'Yes').checked).toBe(true);
    pane().discard();
    await settle();
    expect(option('key_document', 'Yes').checked).toBe(false);
    expect(pane().dirty()).toBe(false);
  });

  it('blocks a save with a required field empty and focuses it, the message linked by aria-describedby', async () => {
    await setup();
    option('key_document', 'Yes').click();
    await settle();
    expect(await pane().save()).toBe(false);
    await settle();
    expect(api.saves).toEqual([]);
    const resp = group('responsiveness')!;
    const error = el().querySelector<HTMLElement>(
      `[data-coding-field="responsiveness"] .coding__error`,
    )!;
    expect(error.textContent).toContain('Responsiveness is required.');
    expect(resp.getAttribute('aria-describedby')).toContain(error.id);
    expect(resp.getAttribute('aria-invalid')).toBe('true');
    expect(
      document.activeElement?.closest('[data-coding-field]')?.getAttribute('data-coding-field'),
    ).toBe('responsiveness');
  });

  it('saves the changed fields with the version, layout and a fresh Idempotency-Key, then shows Saved · indexing until searchable', async () => {
    await setup();
    api.docs.set('doc-1', coding('doc-1', { comments: 'old' }));
    fixture.componentInstance.documentId.set('doc-2');
    await settle();
    fixture.componentInstance.documentId.set('doc-1');
    await settle();
    option('responsiveness', 'Responsive').click();
    const comments = fieldEl('comments')!.querySelector('textarea')!;
    comments.value = 'Pricing terms';
    comments.dispatchEvent(new Event('input'));
    await settle();
    // Polling finds the save searchable.
    api.docs.set('doc-1', coding('doc-1', {}, { indexState: 'searchable' }));
    expect(await pane().save()).toBe(true);
    await settle();
    expect(api.saves).toHaveLength(1);
    expect(api.saves[0]).toEqual({
      documentId: 'doc-1',
      version: '3',
      values: { responsiveness: 'Responsive', comments: 'Pricing terms' },
      options: { layoutId: 'l-first', idempotencyKey: expect.any(String) },
    });
    expect(status()).toContain('Saved · indexing');
    expect(pane().dirty()).toBe(false);

    option('responsiveness', 'Not Responsive').click();
    await settle();
    await pane().save();
    expect(api.saves[1].version).toBe('4');
    expect(api.saves[1].options.idempotencyKey).not.toBe(api.saves[0].options.idempotencyKey);

    await new Promise((r) => setTimeout(r, 600));
    await settle();
    expect(status()).toContain('Saved · searchable');
    expect(TestBed.inject(PendingCoding).isPending('doc-1')).toBe(false);
  });

  it('shows who changed the document on a version conflict and offers reload or overwrite; never saves silently', async () => {
    await setup();
    option('responsiveness', 'Responsive').click();
    await settle();
    const theirs = coding(
      'doc-1',
      { responsiveness: 'Not Responsive' },
      {
        version: '4',
        lastEditor: { displayName: 'J. Smith', changedAt: new Date().toISOString(), jobId: null },
      },
    );
    api.onSave = () => Promise.reject(new CodingConflictError(theirs));
    expect(await pane().save()).toBe(false);
    await settle();
    const alert = el().querySelector<HTMLElement>('opp-coding-conflict')!;
    expect(alert.getAttribute('role')).toBe('alert');
    expect(alert.textContent).toMatch(/Changed by J\. Smith at \d{1,2}:\d{2}/);
    const cells = [...alert.querySelectorAll('tbody tr')].map((r) =>
      [...r.children].map((c) => c.textContent?.trim()),
    );
    expect(cells).toEqual([['Responsiveness', 'Not Responsive', 'Responsive']]);
    expect(document.activeElement).toBe(alert);

    // Overwrite with mine: saved again against their version.
    api.onSave = async (id, version, values) => coding(id, values, { version: '5' });
    button('Overwrite with mine').click();
    await settle();
    expect(api.saves.map((s) => s.version)).toEqual(['3', '4']);
    expect(api.saves[1].values).toEqual({ responsiveness: 'Responsive' });
    expect(el().querySelector('opp-coding-conflict')).toBeNull();

    // Reload: their coding replaces the edits.
    option('responsiveness', 'Needs Further Review').click();
    await settle();
    api.onSave = () => Promise.reject(new CodingConflictError({ ...theirs, version: '6' }));
    await pane().save();
    await settle();
    button('Reload current coding').click();
    await settle();
    expect(option('responsiveness', 'Not Responsive').checked).toBe(true);
    expect(pane().dirty()).toBe(false);
  });

  it('shows the API’s field messages when it refuses a save', async () => {
    await setup();
    option('responsiveness', 'Responsive').click();
    await settle();
    api.onSave = () =>
      Promise.reject(
        new CodingRejectedError(
          400,
          { responsiveness: 'Not allowed here.' },
          'The coding was not saved.',
        ),
      );
    expect(await pane().save()).toBe(false);
    await settle();
    expect(el().querySelector('.coding__message')?.textContent).toContain(
      'The coding was not saved.',
    );
    expect(fieldEl('responsiveness')!.textContent).toContain('Not allowed here.');
  });

  it('is read-only without Coding.Write, and security-affecting fields without Coding.WritePrivilege', async () => {
    await setup({ canCode: false });
    expect(el().querySelector('input:not([type="search"]), textarea')).toBeNull();
    expect(el().querySelector('.coding__actions')).toBeNull();
    expect(fieldEl('responsiveness')!.textContent).toContain('Not set');
    fixture.nativeElement.remove();
    TestBed.resetTestingModule();

    await setup({ permissions: ['Coding.Write'] });
    expect(fieldEl('privilege_status')!.querySelector('input')).toBeNull();
    expect(fieldEl('privilege_status')!.textContent).toContain(
      'Changing it needs the privilege coding permission.',
    );
    expect(group('responsiveness')).not.toBeNull();
  });

  it('remembers the chosen layout per workspace and renders every field type with its validation', async () => {
    await setup({ layoutId: 'l-details' });
    const select = el().querySelector<HTMLSelectElement>('.coding__layout select')!;
    expect(select.value).toBe('l-details');
    const input = (q: string) =>
      fieldEl(q)!.querySelector<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(
        'input, select, textarea',
      )!;
    expect((input('review_date') as HTMLInputElement).type).toBe('date');
    expect((input('follow_up') as HTMLInputElement).type).toBe('datetime-local');
    expect(input('reviewer').tagName).toBe('SELECT');
    expect(input('terms').tagName).toBe('TEXTAREA');
    expect((input('code') as HTMLInputElement).type).toBe('text');

    const pages = input('pages') as HTMLInputElement;
    pages.value = '1.5';
    pages.dispatchEvent(new Event('input'));
    const terms = input('terms') as HTMLTextAreaElement;
    terms.value = 'supply\n\ntermination ';
    terms.dispatchEvent(new Event('input'));
    await settle();
    expect(await pane().save()).toBe(false);
    await settle();
    expect(pages.getAttribute('aria-invalid')).toBe('true');
    const message = el().querySelector(
      `#${pages.getAttribute('aria-describedby')!.split(' ').at(-1)}`,
    );
    expect(message?.textContent).toContain('Pages Reviewed: enter a whole number');
    expect(document.activeElement).toBe(pages);
    pages.value = '12';
    pages.dispatchEvent(new Event('input'));
    await settle();
    expect(await pane().save()).toBe(true);
    expect(api.saves[0].values).toEqual({ pages: 12, terms: ['supply', 'termination'] });
    expect(api.saves[0].options.layoutId).toBe('l-details');

    select.value = 'l-first';
    select.dispatchEvent(new Event('change'));
    await settle();
    expect(TestBed.inject(PreferenceStorage).read('coding.layout.ws-1')).toEqual({
      layoutId: 'l-first',
    });
    expect(fieldEl('responsiveness')).not.toBeNull();
    await expectNoAxeViolations(el());
  }, 30_000);

  it('makes a long checkbox list one Tab stop with Up / Down between its choices', async () => {
    await setup();
    const boxes = () => [
      ...fieldEl('issues')!.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
    ];
    expect(boxes().filter((b) => b.tabIndex === 0)).toEqual([boxes()[0]]);
    boxes()[0].focus();
    const list = group('issues')!;
    boxes()[0].dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
    );
    await settle();
    expect(document.activeElement).toBe(boxes()[1]);
    expect(boxes().filter((b) => b.tabIndex === 0)).toEqual([boxes()[1]]);
    key(document.activeElement!, 'Digit3');
    await settle();
    expect(boxes()[2].checked).toBe(true);
    expect(list.querySelector('.opp-visually-hidden[id$="-hint"]')?.textContent).toContain(
      'Up and Down arrows',
    );
  });

  it('focuses the n-th field on screen (Alt+Shift+C, then n)', async () => {
    await setup();
    pane().focusField(3);
    await settle();
    expect(
      document.activeElement?.closest('[data-coding-field]')?.getAttribute('data-coding-field'),
    ).toBe('issues');
  });
});
