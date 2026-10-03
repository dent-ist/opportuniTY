import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { TABS } from './tabs';

@Component({
  imports: [...TABS],
  template: `
    <div oppTabs>
      <ul oppTabList [(selectedTab)]="mode" aria-label="Viewer mode">
        <li oppTab value="text">Extracted Text</li>
        <li oppTab value="native">Native</li>
        <li oppTab value="image">Image</li>
      </ul>
      <div oppTabPanel value="text"><ng-template ngTabContent>Text body</ng-template></div>
      <div oppTabPanel value="native"><ng-template ngTabContent>Native body</ng-template></div>
      <div oppTabPanel value="image"><ng-template ngTabContent>Image body</ng-template></div>
    </div>
  `,
})
class Host {
  readonly mode = signal<string | undefined>('text');
}

describe('Tabs', () => {
  it('exposes tab semantics and the selected panel', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    const tabs = Array.from(el.querySelectorAll<HTMLElement>('[role=tab]'));
    expect(el.querySelector('[role=tablist]')?.classList).toContain('opp-tab-list');
    expect(tabs.map((t) => t.getAttribute('aria-selected'))).toEqual(['true', 'false', 'false']);
    const panel = el.querySelector<HTMLElement>(`#${tabs[0].getAttribute('aria-controls')}`);
    expect(panel?.getAttribute('role')).toBe('tabpanel');
    expect(panel?.textContent).toContain('Text body');
  });

  it('moves selection with the arrow keys', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const tabs = Array.from<HTMLElement>(fixture.nativeElement.querySelectorAll('[role=tab]'));
    tabs[0].focus();
    tabs[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await fixture.whenStable();
    expect(fixture.componentInstance.mode()).toBe('native');
    expect(document.activeElement).toBe(tabs[1]);
    tabs[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
    await fixture.whenStable();
    expect(fixture.componentInstance.mode()).toBe('image');
  });

  it('passes axe', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});
