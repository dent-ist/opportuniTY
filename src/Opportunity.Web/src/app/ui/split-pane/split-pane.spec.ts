import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PreferenceStorage } from '../../core/preferences/preference-storage';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { SplitPane } from './split-pane';

@Component({
  imports: [SplitPane],
  template: `
    <opp-split-pane
      sizedPane="end"
      label="Resize coding pane"
      [size]="30"
      [min]="20"
      [max]="50"
      storageKey="test.coding"
      collapsible
    >
      <div oppSplitStart>Document</div>
      <aside oppSplitEnd aria-label="Coding pane"><button type="button">Save</button></aside>
    </opp-split-pane>
  `,
})
class Host {}

function key(el: HTMLElement, key: string, shiftKey = false): void {
  el.dispatchEvent(
    new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true }),
  );
}

describe('SplitPane', () => {
  beforeEach(() => localStorage.clear());

  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const separator: HTMLElement = fixture.nativeElement.querySelector('[role=separator]');
    const host: HTMLElement = fixture.nativeElement.querySelector('opp-split-pane');
    return { fixture, separator, host };
  }

  it('is a focusable window splitter that controls the sized pane', async () => {
    const { fixture, separator, host } = await render();
    expect(separator.tabIndex).toBe(0);
    expect(separator.getAttribute('aria-label')).toBe('Resize coding pane');
    expect(separator.getAttribute('aria-orientation')).toBe('vertical');
    expect(separator.getAttribute('aria-valuenow')).toBe('30');
    expect(separator.getAttribute('aria-valuemax')).toBe('50');
    const controlled = fixture.nativeElement.querySelector(
      `#${separator.getAttribute('aria-controls')}`,
    );
    expect(controlled?.textContent).toContain('Save');
    expect(host.style.getPropertyValue('--opp-split-size')).toBe('30%');
  });

  it('resizes with the keyboard within min/max and persists', async () => {
    const { fixture, separator } = await render();
    key(separator, 'ArrowLeft'); // splitter toward start → end pane grows
    await fixture.whenStable();
    expect(separator.getAttribute('aria-valuenow')).toBe('32');
    key(separator, 'ArrowRight', true);
    await fixture.whenStable();
    expect(separator.getAttribute('aria-valuenow')).toBe('22');
    key(separator, 'Home');
    await fixture.whenStable();
    expect(separator.getAttribute('aria-valuenow')).toBe('20');
    key(separator, 'End');
    await fixture.whenStable();
    expect(separator.getAttribute('aria-valuenow')).toBe('50');
    expect(TestBed.inject(PreferenceStorage).read('pane.test.coding')).toEqual({
      size: 50,
      collapsed: false,
    });
  });

  it('collapses with Enter, making the pane inert, and restores the persisted layout', async () => {
    const { fixture, separator } = await render();
    key(separator, 'Enter');
    await fixture.whenStable();
    expect(separator.getAttribute('aria-valuenow')).toBe('0');
    expect(fixture.nativeElement.querySelector('.split__pane--end').hasAttribute('inert')).toBe(
      true,
    );

    const again = await render();
    expect(again.separator.getAttribute('aria-valuenow')).toBe('0');
    key(again.separator, 'Enter');
    await again.fixture.whenStable();
    expect(again.separator.getAttribute('aria-valuenow')).toBe('30');
  });

  it('passes axe', async () => {
    const { fixture } = await render();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});
