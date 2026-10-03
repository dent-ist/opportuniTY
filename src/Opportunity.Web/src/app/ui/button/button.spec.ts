import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Icon } from '../icon/icon';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { Button, IconButton } from './button';

@Component({
  imports: [Button, IconButton, Icon],
  template: `
    <button type="button" oppButton>Default</button>
    <button type="button" oppButton="primary" busy>Saving</button>
    <button type="button" oppButton="danger" disabled>Delete</button>
    <a oppButton="ghost" href="/x">Link</a>
    <button type="button" oppIconButton label="Close panel"><opp-icon name="close" /></button>
  `,
})
class Host {}

describe('Button and IconButton', () => {
  it('style native elements and keep their semantics', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const [plain, busy, danger] = Array.from<HTMLButtonElement>(
      fixture.nativeElement.querySelectorAll('button[oppButton]'),
    );
    expect(plain.classList).toContain('opp-button');
    expect(plain.classList).toContain('opp-button--secondary');
    expect(busy.classList).toContain('opp-button--primary');
    expect(busy.getAttribute('aria-busy')).toBe('true');
    expect(danger.disabled).toBe(true);
    expect(fixture.nativeElement.querySelector('a').classList).toContain('opp-button--ghost');
  });

  it('gives icon buttons an accessible name and tooltip', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const icon: HTMLButtonElement = fixture.nativeElement.querySelector('button[oppIconButton]');
    expect(icon.getAttribute('aria-label')).toBe('Close panel');
    expect(icon.title).toBe('Close panel');
    expect(icon.querySelector('opp-icon')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('passes axe', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});
