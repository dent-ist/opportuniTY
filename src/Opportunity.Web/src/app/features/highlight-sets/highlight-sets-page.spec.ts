import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  HighlightSet,
  HighlightSetDraft,
  HighlightSetsApi,
  HighlightToggles,
} from '../../core/highlights/highlight-sets';
import { DialogService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { HighlightSetsPage, termLines } from './highlight-sets-page';

const SET: HighlightSet = {
  highlightSetId: 'set-1',
  name: 'Key terms',
  description: null,
  color: 'amber',
  terms: [
    { termId: 't-1', expression: 'termination', color: null },
    { termId: 't-2', expression: '"price increase"', color: 'rose' },
  ],
  modifiedBy: 'Alex Admin',
  modifiedAt: '2026-10-01T09:00:00Z',
  version: 3,
};

@Injectable()
class FakeApi extends HighlightSetsApi {
  sets: HighlightSet[] = [SET];
  created: HighlightSetDraft[] = [];
  updated: { set: HighlightSet; draft: HighlightSetDraft }[] = [];
  deleted: string[] = [];
  reject: HttpErrorResponse | null = null;

  async list() {
    return { sets: this.sets, colors: ['amber', 'green', 'blue'] };
  }
  async create(draft: HighlightSetDraft): Promise<HighlightSet> {
    if (this.reject) throw this.reject;
    this.created.push(draft);
    return { ...SET, highlightSetId: 'set-2', name: draft.name, version: 1 };
  }
  async update(set: HighlightSet, draft: HighlightSetDraft): Promise<HighlightSet> {
    this.updated.push({ set, draft });
    return { ...set, ...draft, version: set.version + 1 };
  }
  async delete(set: HighlightSet): Promise<void> {
    this.deleted.push(set.highlightSetId);
    this.sets = [];
  }
  async toggles(): Promise<HighlightToggles> {
    return { disabledSetIds: [], searchHits: true };
  }
  async setToggles(t: HighlightToggles) {
    return t;
  }
}

describe('Admin › Highlight Sets (E16-T12)', () => {
  let api: FakeApi;
  let root: HTMLElement;
  let confirmed: boolean;

  async function setup(): Promise<void> {
    confirmed = true;
    TestBed.configureTestingModule({
      providers: [{ provide: DialogService, useValue: { confirm: async () => confirmed } }],
    });
    TestBed.overrideComponent(HighlightSetsPage, {
      set: { providers: [{ provide: HighlightSetsApi, useClass: FakeApi }] },
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    const fixture = TestBed.createComponent(HighlightSetsPage);
    api = fixture.debugElement.injector.get(HighlightSetsApi) as FakeApi;
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      TestBed.tick();
    }
  }

  const button = (name: string) =>
    [...root.querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name,
    )!;
  const terms = () => root.querySelector<HTMLTextAreaElement>('#hs-terms')!;
  const type = (el: HTMLInputElement | HTMLTextAreaElement, value: string) => {
    el.value = value;
    el.dispatchEvent(new Event('input'));
  };

  afterEach(() => root?.remove());

  it('lists the sets with colour, term count and who changed them', async () => {
    await setup();
    const item = root.querySelector('.hs__item')!;
    expect(item.textContent).toContain('Key terms');
    expect(item.textContent).toContain('Amber · 2 terms');
    expect(item.textContent).toContain('Alex Admin');
    expect(item.querySelector('[data-color="amber"]')).not.toBeNull();
    await expectNoAxeViolations(root);
  });

  it('creates a set from one term per line and shows the server errors by line', async () => {
    await setup();
    button('New highlight set').click();
    await settle();
    type(root.querySelector<HTMLInputElement>('opp-text-field input')!, 'Names');
    type(terms(), 'smith\n\n  te*  \njones W/3 report');
    api.reject = new HttpErrorResponse({
      status: 400,
      error: {
        status: 400,
        title: 'Validation',
        errors: {
          'terms[1].expression': ['LEADING_WILDCARD: A wildcard needs at least 3 letters.'],
        },
      },
    });
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
    expect(root.querySelector('[role="alert"]')!.textContent).toContain('Line 2');
    expect(root.querySelector('[role="alert"]')!.textContent).toContain('te*');
    expect(terms().getAttribute('aria-invalid')).toBe('true');
    await expectNoAxeViolations(root);

    api.reject = null;
    type(terms(), 'smith\njones W/3 report');
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
    expect(api.created).toEqual([
      {
        name: 'Names',
        description: null,
        color: 'amber',
        terms: [
          { expression: 'smith', termId: null, color: null },
          { expression: 'jones W/3 report', termId: null, color: null },
        ],
      },
    ]);
  });

  it('edits a set keeping the identity and colour of unchanged terms, and deletes after confirmation', async () => {
    await setup();
    button('Edit Key terms').click();
    await settle();
    expect(terms().value).toBe('termination\n"price increase"');
    type(terms(), '"price increase"\nterminat*');
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
    expect(api.updated[0].set.version).toBe(3);
    expect(api.updated[0].draft.terms).toEqual([
      { expression: '"price increase"', termId: 't-2', color: 'rose' },
      { expression: 'terminat*', termId: null, color: null },
    ]);

    confirmed = false;
    button('Delete Key terms').click();
    await settle();
    expect(api.deleted).toEqual([]);
    confirmed = true;
    button('Delete Key terms').click();
    await settle();
    expect(api.deleted).toEqual(['set-1']);
    expect(root.textContent).toContain('No highlight sets yet');
  });

  it('reads one term per non-blank line', () => {
    expect(termLines(' a \r\n\n"b c"\n')).toEqual(['a', '"b c"']);
  });
});
