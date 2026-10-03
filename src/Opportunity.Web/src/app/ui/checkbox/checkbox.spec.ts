import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { Checkbox } from './checkbox';

@Component({
  imports: [Checkbox],
  template: `
    <opp-checkbox [(checked)]="privileged">Privileged</opp-checkbox>
    <opp-checkbox ariaLabel="Select all documents" [indeterminate]="true" />
  `,
})
class Host {
  readonly privileged = signal(false);
}

describe('Checkbox', () => {
  it('toggles through the native input', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const input: HTMLInputElement = fixture.nativeElement.querySelector('input');
    input.click();
    await fixture.whenStable();
    expect(fixture.componentInstance.privileged()).toBe(true);
    expect(input.checked).toBe(true);
  });

  it('supports the mixed state with an aria label', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const mixed: HTMLInputElement = fixture.nativeElement.querySelectorAll('input')[1];
    expect(mixed.indeterminate).toBe(true);
    expect(mixed.getAttribute('aria-label')).toBe('Select all documents');
  });

  it('passes axe', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});
