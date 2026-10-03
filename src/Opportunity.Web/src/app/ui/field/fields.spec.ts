import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormField, form, required } from '@angular/forms/signals';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { Select } from './select';
import { TextField } from './text-field';

@Component({
  imports: [TextField, Select],
  template: `
    <opp-text-field label="Name" hint="Shown to you only" [(value)]="name" />
    <opp-text-field label="Control number" error="Enter a control number" [required]="true" />
    <opp-select
      label="Responsiveness"
      placeholder="Choose…"
      [options]="options"
      [(value)]="choice"
    />
  `,
})
class Host {
  readonly name = signal('Initial');
  readonly choice = signal('');
  readonly options = [
    { value: 'r', label: 'Responsive' },
    { value: 'nr', label: 'Not Responsive' },
  ];
}

@Component({
  imports: [TextField, FormField],
  template: `<opp-text-field label="Saved search name" [formField]="f.name" />`,
})
class SignalFormHost {
  readonly model = signal({ name: '' });
  readonly f = form(this.model, (p) => required(p.name));
}

describe('TextField and Select', () => {
  it('associate label, hint and error with the control', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    const [name, control] = Array.from(el.querySelectorAll('input'));
    expect(el.querySelector(`label[for="${name.id}"]`)?.textContent).toContain('Name');
    expect(el.querySelector(`#${name.getAttribute('aria-describedby')}`)?.textContent).toContain(
      'Shown to you only',
    );
    expect(control.getAttribute('aria-invalid')).toBe('true');
    expect(control.required).toBe(true);
    expect(el.querySelector(`#${control.getAttribute('aria-describedby')}`)?.textContent).toContain(
      'Enter a control number',
    );
  });

  it('two-way binds values', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const input: HTMLInputElement = fixture.nativeElement.querySelector('input');
    expect(input.value).toBe('Initial');
    input.value = 'Hot docs';
    input.dispatchEvent(new Event('input'));
    expect(fixture.componentInstance.name()).toBe('Hot docs');

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('select');
    select.value = 'nr';
    select.dispatchEvent(new Event('change'));
    expect(fixture.componentInstance.choice()).toBe('nr');
  });

  it('works as a Signal Forms control', async () => {
    const fixture = TestBed.createComponent(SignalFormHost);
    await fixture.whenStable();
    const input: HTMLInputElement = fixture.nativeElement.querySelector('input');
    expect(input.required).toBe(true);
    input.value = 'Privilege review';
    input.dispatchEvent(new Event('input'));
    expect(fixture.componentInstance.model().name).toBe('Privilege review');
    expect(fixture.componentInstance.f.name().valid()).toBe(true);
  });

  it('passes axe', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});
