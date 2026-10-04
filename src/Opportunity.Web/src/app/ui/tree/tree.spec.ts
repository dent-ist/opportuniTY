import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { Tree, TreeNode } from './tree';

const NODES: TreeNode[] = [
  {
    id: 'f-1',
    label: 'First pass',
    icon: 'folder',
    children: [
      { id: 'f-2', label: 'Hot', icon: 'folder', children: [{ id: 's-2', label: 'Deep' }] },
      { id: 's-1', label: 'Responsive', detail: 'Shared' },
    ],
  },
  { id: 's-3', label: 'Unfiled' },
];

@Component({
  imports: [Tree],
  template: `<opp-tree
    label="Saved searches"
    [nodes]="nodes"
    [selected]="selected()"
    [initiallyExpanded]="['f-1']"
    (activate)="activated.push($event.id)"
  />`,
})
class Host {
  readonly nodes = NODES;
  readonly selected = signal<string | null>(null);
  readonly activated: string[] = [];
}

describe('Tree', () => {
  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    const items = () => [...el.querySelectorAll<HTMLElement>('[role=treeitem]')];
    const item = (label: string) =>
      items().find((i) => i.querySelector('.tree__label')?.textContent === label)!;
    const key = async (target: HTMLElement, k: string) => {
      target.dispatchEvent(
        new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }),
      );
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
    };
    return { fixture, el, items, item, key };
  }

  it('has tree semantics, one Tab stop and names from the label and detail', async () => {
    const { el, items, item } = await render();
    expect(el.querySelector('[role=tree]')?.getAttribute('aria-label')).toBe('Saved searches');
    expect(items().map((i) => i.querySelector('.tree__label')?.textContent)).toEqual([
      'First pass',
      'Hot',
      'Responsive',
      'Unfiled',
    ]);
    expect(items().filter((i) => i.tabIndex === 0)).toEqual([item('First pass')]);
    expect(item('First pass').getAttribute('aria-expanded')).toBe('true');
    expect(item('Hot').getAttribute('aria-expanded')).toBe('false');
    expect(item('Unfiled').hasAttribute('aria-expanded')).toBe(false);
    expect(item('Hot').getAttribute('aria-level')).toBe('2');
    const labelledBy = item('Responsive').getAttribute('aria-labelledby')!.split(' ');
    expect(labelledBy.map((id) => document.getElementById(id)?.textContent)).toEqual([
      'Responsive',
      'Shared',
    ]);
    await expectNoAxeViolations(el);
  });

  it('moves, expands, collapses and activates with the keyboard', async () => {
    const { fixture, item, key } = await render();
    item('First pass').focus();
    await key(item('First pass'), 'ArrowDown');
    expect(document.activeElement).toBe(item('Hot'));
    await key(item('Hot'), 'ArrowRight');
    expect(item('Hot').getAttribute('aria-expanded')).toBe('true');
    await key(item('Hot'), 'ArrowRight');
    expect(document.activeElement).toBe(item('Deep'));
    await key(item('Deep'), 'ArrowLeft');
    expect(document.activeElement).toBe(item('Hot'));
    await key(item('Hot'), 'ArrowLeft');
    expect(item('Hot').getAttribute('aria-expanded')).toBe('false');
    await key(item('Hot'), 'End');
    expect(document.activeElement).toBe(item('Unfiled'));
    expect(item('Unfiled').tabIndex).toBe(0);
    await key(item('Unfiled'), 'Enter');
    await key(item('Unfiled'), 'Home');
    expect(document.activeElement).toBe(item('First pass'));
    await key(item('First pass'), ' ');
    expect(fixture.componentInstance.activated).toEqual(['s-3', 'f-1']);
  });

  it('opens the groups above the selected item and marks it selected', async () => {
    const { fixture, item } = await render();
    fixture.componentInstance.selected.set('s-2');
    await fixture.whenStable();
    expect(item('Hot').getAttribute('aria-expanded')).toBe('true');
    expect(item('Deep').getAttribute('aria-selected')).toBe('true');
    expect(item('Deep').tabIndex).toBe(0);
  });
});
