import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiError } from '../../core/api/problem-details';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { EmptyState, ErrorState, LoadingState, Progress } from './state';

@Component({
  imports: [LoadingState, EmptyState, ErrorState, Progress],
  template: `
    <opp-loading-state label="Loading documents…" />
    <opp-empty-state title="No documents match" message="Change the search." />
    <opp-error-state [error]="error" (retry)="retries.set(retries() + 1)" />
    <opp-progress label="Searchable" [value]="63" />
    <opp-progress label="Rendering" />
  `,
})
class Host {
  readonly error = new ApiError(503, { title: 'Unavailable', traceId: 'trace-42' });
  readonly retries = signal(0);
}

describe('loading, empty and error states', () => {
  it('announce loading and errors, show the support reference and offer retry', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('opp-loading-state')?.getAttribute('role')).toBe('status');
    const error = el.querySelector('opp-error-state') as HTMLElement;
    expect(error.getAttribute('role')).toBe('alert');
    expect(error.textContent).toContain('Something went wrong');
    expect(error.textContent).toContain('trace-42');
    error.querySelector('button')!.click();
    expect(fixture.componentInstance.retries()).toBe(1);
  });

  it('exposes determinate and indeterminate progress', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const [determinate, indeterminate] = Array.from<HTMLElement>(
      fixture.nativeElement.querySelectorAll('[role=progressbar]'),
    );
    expect(determinate.getAttribute('aria-valuenow')).toBe('63');
    expect(determinate.getAttribute('aria-valuetext')).toBe('63%');
    expect(indeterminate.hasAttribute('aria-valuenow')).toBe(false);
  });

  it('passes axe', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});
