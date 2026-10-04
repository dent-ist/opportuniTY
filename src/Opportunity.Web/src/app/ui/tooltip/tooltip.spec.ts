import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Tooltip } from './tooltip';

@Component({
  imports: [Tooltip],
  template: `<button type="button" aria-disabled="true" [oppTooltip]="text()">Image</button>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly text = signal<string | null>('Image rendering in progress');
}

describe('Tooltip', () => {
  it('describes its host, shows on focus and hover, and closes with Escape without passing it on', async () => {
    const fixture = TestBed.createComponent(Host);
    document.body.appendChild(fixture.nativeElement);
    await fixture.whenStable();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    const tip = () => document.getElementById(button.getAttribute('aria-describedby') ?? '');

    expect(tip()?.getAttribute('role')).toBe('tooltip');
    expect(tip()?.textContent).toBe('Image rendering in progress');
    expect(tip()?.hidden).toBe(true);

    button.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    expect(tip()?.hidden).toBe(false);
    const outer = vi.fn();
    document.addEventListener('keydown', outer);
    button.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    document.removeEventListener('keydown', outer);
    expect(tip()?.hidden).toBe(true);
    expect(outer).not.toHaveBeenCalled();

    button.dispatchEvent(new MouseEvent('mouseenter'));
    expect(tip()?.hidden).toBe(false);
    button.dispatchEvent(new FocusEvent('focusout', { bubbles: true }));
    expect(tip()?.hidden).toBe(true);

    // No text: no tooltip and no description.
    const id = button.getAttribute('aria-describedby')!;
    fixture.componentInstance.text.set(null);
    await fixture.whenStable();
    expect(button.hasAttribute('aria-describedby')).toBe(false);
    expect(document.getElementById(id)).toBeNull();
    fixture.destroy();
    fixture.nativeElement.remove();
  });
});
