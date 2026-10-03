import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { Count, FreshnessIndicator, ProtectionBadge, StatusPill } from './badge';

@Component({
  imports: [ProtectionBadge, FreshnessIndicator, Count, StatusPill],
  template: `
    <p id="privileged"><opp-protection-badge kind="privileged" /></p>
    <p id="aeo"><opp-protection-badge kind="aeo" /></p>
    <p id="restricted"><opp-protection-badge kind="restricted" /></p>
    <p id="updating"><opp-freshness state="updating" [pending]="1240" /></p>
    <p id="current"><opp-freshness state="current" asOf="2026-10-03T22:05:00Z" /></p>
    <p id="eq"><opp-count [value]="12400" /></p>
    <p id="gte"><opp-count [value]="10000" relation="gte" /></p>
    <p id="approx"><opp-count [value]="12400" relation="approx" /></p>
    <p id="job"><opp-status-pill status="partial" /></p>
  `,
})
class Host {}

describe('data-state indicators', () => {
  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    return (id: string) => el.querySelector(`#${id}`) as HTMLElement;
  }

  it('state protection in text, with the AEO abbreviation expanded for screen readers', async () => {
    const get = await render();
    expect(get('privileged').textContent?.trim()).toBe('Privileged');
    expect(get('aeo').querySelector('abbr')?.title).toBe("Attorneys' Eyes Only");
    expect(get('aeo').querySelector('.opp-visually-hidden')?.textContent).toBe(
      "Attorneys' Eyes Only",
    );
    expect(get('restricted').textContent?.trim()).toBe('Access restricted');
  });

  it('describes freshness in plain language with ≈ for pending counts (Q-10)', async () => {
    const get = await render();
    expect(get('updating').textContent?.trim()).toBe('Updating · ≈ 1,240 changes pending');
    expect(get('current').textContent?.trim()).toMatch(/^Current as of \d{1,2}:\d{2}/);
  });

  it('marks count precision visually and in speech (Q-32)', async () => {
    const get = await render();
    const visual = (id: string) => get(id).querySelector('[aria-hidden=true]')?.textContent;
    const spoken = (id: string) => get(id).querySelector('.opp-visually-hidden')?.textContent;
    expect([visual('eq'), spoken('eq')]).toEqual(['12,400', '12,400']);
    expect([visual('gte'), spoken('gte')]).toEqual(['≥ 10,000', 'at least 10,000']);
    expect([visual('approx'), spoken('approx')]).toEqual(['≈ 12,400', 'approximately 12,400']);
    expect(get('job').textContent?.trim()).toBe('Completed with errors');
  });

  it('formats times for the en-GB display locale (Q-28)', async () => {
    TestBed.inject(UiPreferences).locale.set('en-GB');
    const get = await render();
    expect(get('current').textContent).not.toMatch(/AM|PM/);
  });

  it('passes axe', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});
