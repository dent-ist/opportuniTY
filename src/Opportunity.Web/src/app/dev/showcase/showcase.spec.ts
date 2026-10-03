import { TestBed } from '@angular/core/testing';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { KEYBOARD_DOCS, Showcase } from './showcase';

describe('Component showcase', () => {
  beforeEach(() => localStorage.clear());

  it('renders every core component and passes axe as a whole page', async () => {
    const fixture = TestBed.createComponent(Showcase);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    for (const selector of [
      'button[oppButton]',
      'button[oppIconButton]',
      'opp-text-field',
      'opp-select',
      'opp-checkbox',
      '[role=tablist]',
      'opp-split-pane',
      'opp-protection-badge',
      'opp-freshness',
      'opp-count',
      'opp-status-pill',
      'opp-progress',
      'opp-loading-state',
      'opp-empty-state',
      'opp-error-state',
    ]) {
      expect(el.querySelector(selector), selector).not.toBeNull();
    }
    await expectNoAxeViolations(el);
  }, 60_000); // axe over the whole page is slow in jsdom, especially on loaded CI runners

  it('shows the review layout with a compact list of at least 30 rows', async () => {
    const fixture = TestBed.createComponent(Showcase);
    await fixture.whenStable();
    const list = fixture.nativeElement.querySelector('[data-density=compact] table');
    expect(list.querySelectorAll('tbody tr').length).toBeGreaterThanOrEqual(30);
    expect(fixture.nativeElement.querySelectorAll('[role=separator]').length).toBe(3);
  });

  it('documents keyboard interaction for every interactive component', () => {
    const documented = KEYBOARD_DOCS.map((d) => d.component).join(' ');
    for (const name of [
      'Button',
      'Text field',
      'Select',
      'Checkbox',
      'Dialog',
      'Tabs',
      'Split pane',
      'Toast',
    ]) {
      expect(documented).toContain(name);
    }
  });
});
