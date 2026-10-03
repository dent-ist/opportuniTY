import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { expectNoAxeViolations } from '../testing/axe.testing';
import { Announcer } from './announcer';
import { ToastRegion, ToastService } from './toast';

describe('ToastService and ToastRegion', () => {
  let announce: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
    announce = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [{ provide: LiveAnnouncer, useValue: { announce } }],
    });
  });

  afterEach(() => vi.useRealTimers());

  it('announces every toast, errors assertively', () => {
    const toasts = TestBed.inject(ToastService);
    toasts.show('Coding saved.');
    toasts.show('Export failed.', { tone: 'error' });
    expect(announce).toHaveBeenCalledWith('Coding saved.', 'polite');
    expect(announce).toHaveBeenCalledWith('Export failed.', 'assertive');
  });

  it('auto-dismisses info toasts, pauses while hovered or focused, keeps errors', () => {
    const toasts = TestBed.inject(ToastService);
    const info = toasts.show('Job queued.');
    toasts.show('Export failed.', { tone: 'error' });
    toasts.pause(info);
    vi.advanceTimersByTime(10_000);
    expect(toasts.toasts().length).toBe(2);
    toasts.resume(info);
    vi.advanceTimersByTime(6_000);
    expect(toasts.toasts().map((t) => t.message)).toEqual(['Export failed.']);
  });

  it('renders a labelled region with dismiss buttons and passes axe', async () => {
    vi.useRealTimers();
    const fixture = TestBed.createComponent(ToastRegion);
    TestBed.inject(ToastService).show('Export failed.', {
      tone: 'error',
      action: { label: 'View job', run: () => undefined },
    });
    await fixture.whenStable();
    const region: HTMLElement = fixture.nativeElement.querySelector('section');
    expect(region.getAttribute('aria-label')).toBe('Notifications');
    expect(region.textContent).toContain('error:');
    region.querySelector<HTMLButtonElement>('button[aria-label="Dismiss notification"]')!.click();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('section')).toBeNull();
  });

  it('passes axe with toasts shown', async () => {
    vi.useRealTimers();
    const fixture = TestBed.createComponent(ToastRegion);
    TestBed.inject(ToastService).show('Bulk coding job queued.', {
      action: { label: 'View job', run: () => undefined },
    });
    await fixture.whenStable();
    await expectNoAxeViolations(fixture.nativeElement);
  });
});

describe('Announcer', () => {
  it('throttles announcements that share a key and delivers the latest', () => {
    vi.useFakeTimers();
    const announce = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [{ provide: LiveAnnouncer, useValue: { announce } }],
    });
    const announcer = TestBed.inject(Announcer);
    announcer.announce('Search index updating', { throttleKey: 'freshness' });
    announcer.announce('Search index 50 % current', { throttleKey: 'freshness' });
    announcer.announce('Search index current', { throttleKey: 'freshness' });
    expect(announce).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(30_000);
    expect(announce).toHaveBeenCalledTimes(2);
    expect(announce).toHaveBeenLastCalledWith('Search index current', 'polite');
    vi.useRealTimers();
  });
});
