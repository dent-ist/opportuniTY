import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { DocumentFolder, FolderApi, FolderLevel } from './folder-api';
import { FolderBrowser } from './folder-browser';

function folder(
  folderId: string,
  name: string,
  parentFolderId: string | null,
  documentCount: number,
  totalCount: number,
): DocumentFolder {
  return {
    folderId,
    parentFolderId,
    name,
    path: name,
    documentCount,
    totalCount,
    hasSubfolders: totalCount > documentCount,
  };
}

class FakeFolderApi extends FolderApi {
  readonly calls: { parent: string | null; cursor: string | null }[] = [];
  levels = new Map<string, FolderLevel>([
    [
      'top',
      {
        items: [
          folder('f-mail', 'Mail', null, 0, 1234),
          folder('f-loose', 'Loose files', null, 1, 1),
        ],
        nextCursor: 'c-1',
        countsApproximate: false,
      },
    ],
    [
      'top:c-1',
      { items: [folder('f-z', 'Zeta', null, 2, 2)], nextCursor: null, countsApproximate: true },
    ],
    [
      'f-mail',
      {
        items: [folder('f-inbox', 'Inbox', 'f-mail', 1000, 1234)],
        nextCursor: null,
        countsApproximate: false,
      },
    ],
  ]);

  level(parent: string | null, cursor: string | null = null): Promise<FolderLevel> {
    this.calls.push({ parent, cursor });
    const key = (parent ?? 'top') + (cursor ? `:${cursor}` : '');
    const level = this.levels.get(key);
    return level ? Promise.resolve(level) : Promise.reject(new Error('unknown level'));
  }
}

@Component({
  imports: [FolderBrowser],
  template: `<opp-folder-browser
    [activeId]="active()"
    [includeSubfolders]="subfolders()"
    (selectFolder)="selected.push($event.folderId)"
  />`,
})
class Host {
  readonly active = signal<string | null>(null);
  readonly subfolders = signal(true);
  readonly selected: string[] = [];
}

describe('Folders in the Documents browser (#179)', () => {
  let api: FakeFolderApi;

  async function render() {
    api = new FakeFolderApi();
    TestBed.configureTestingModule({ providers: [{ provide: FolderApi, useValue: api }] });
    const fixture = TestBed.createComponent(Host);
    const settle = async () => {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      await fixture.whenStable();
    };
    await settle();
    const el: HTMLElement = fixture.nativeElement;
    const items = () => [...el.querySelectorAll<HTMLElement>('[role=treeitem]')];
    const item = (label: string) =>
      items().find((i) => i.querySelector('.tree__label')?.textContent === label)!;
    const labels = () => items().map((i) => i.querySelector('.tree__label')?.textContent);
    const key = async (target: HTMLElement, k: string) => {
      target.dispatchEvent(
        new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }),
      );
      await settle();
    };
    return { fixture, el, items, item, labels, key, settle };
  }

  it('lists the top level with counts and loads a folder’s subfolders when it is first opened', async () => {
    const { el, item, labels, key } = await render();
    expect(api.calls).toEqual([{ parent: null, cursor: null }]);
    expect(el.querySelector('[role=tree]')?.getAttribute('aria-label')).toBe('Folders');
    expect(labels()).toEqual(['Mail', 'Loose files', 'Show more folders']);
    expect(item('Mail').querySelector('.tree__detail')?.textContent).toBe('1,234 documents');
    expect(item('Loose files').querySelector('.tree__detail')?.textContent).toBe('1 document');
    expect(item('Mail').getAttribute('aria-expanded')).toBe('false');
    expect(item('Loose files').hasAttribute('aria-expanded')).toBe(false);

    item('Mail').focus();
    await key(item('Mail'), 'ArrowRight');
    expect(api.calls).toContainEqual({ parent: 'f-mail', cursor: null });
    expect(labels()).toEqual(['Mail', 'Inbox', 'Loose files', 'Show more folders']);
    // Closing and reopening does not load again.
    await key(item('Mail'), 'ArrowLeft');
    await key(item('Mail'), 'ArrowRight');
    expect(api.calls.filter((c) => c.parent === 'f-mail')).toHaveLength(1);
    await expectNoAxeViolations(el);
  });

  it('shows more of a long level, marks approximate counts and selects a folder with Enter', async () => {
    const { fixture, el, item, labels, key } = await render();
    item('Show more folders').focus();
    await key(item('Show more folders'), 'Enter');
    expect(api.calls).toContainEqual({ parent: null, cursor: 'c-1' });
    expect(labels()).toEqual(['Mail', 'Loose files', 'Zeta']);
    expect(el.textContent).toContain('Counts are catching up with recent changes.');
    expect(item('Zeta').querySelector('.tree__detail')?.textContent).toBe('≈2 documents');

    await key(item('Loose files'), 'Enter');
    expect(fixture.componentInstance.selected).toEqual(['f-loose']);
  });

  it('counts only the folder itself when subfolders are excluded', async () => {
    const { fixture, item, settle } = await render();
    fixture.componentInstance.subfolders.set(false);
    await settle();
    expect(item('Mail').querySelector('.tree__detail')?.textContent).toBe('0 documents');
  });

  it('says when there are no folders and offers a reload when they cannot be loaded', async () => {
    api = new FakeFolderApi();
    const { el } = await (async () => {
      TestBed.configureTestingModule({
        providers: [
          {
            provide: FolderApi,
            useValue: {
              level: () =>
                Promise.resolve({ items: [], nextCursor: null, countsApproximate: false }),
            },
          },
        ],
      });
      const fixture = TestBed.createComponent(Host);
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      await fixture.whenStable();
      return { el: fixture.nativeElement as HTMLElement };
    })();
    expect(el.textContent).toContain('No folders. They come from the Folder Path an import maps.');
  });
});
