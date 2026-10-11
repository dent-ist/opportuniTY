import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { UiPreferences } from '../preferences/ui-preferences';
import type { Workspace } from '../workspace/workspace-api';
import { ActiveWorkspace } from '../workspace/workspace-context';
import { DateTimePipe, DisplayFormat, NumberPipe } from './display-format';
import { formatterFor } from './formatter';

const AT = '2026-03-14T15:04:05Z';

describe('Formatter (E15-T05, Q-28)', () => {
  it('renders instants in the display time zone with a zone indicator, per locale', () => {
    expect(formatterFor('en-US', 'America/New_York').dateTime(AT)).toBe('03/14/2026, 11:04 AM EDT');
    expect(formatterFor('en-GB', 'Europe/London').dateTime(AT)).toBe('14/03/2026, 15:04 GMT');
    expect(formatterFor('en-GB', 'UTC').dateTime(AT, { seconds: true })).toBe(
      '14/03/2026, 15:04:05 UTC',
    );
    expect(formatterFor('en-US', 'UTC').time(AT)).toBe('03:04 PM UTC');
  });

  it('offers ISO 8601 order with a 24-hour clock', () => {
    const iso = formatterFor('en-US', 'America/New_York', 'iso');
    expect(iso.dateTime(AT)).toBe('2026-03-14 11:04 EDT');
    expect(iso.dateTime(AT, { seconds: true })).toBe('2026-03-14 11:04:05 EDT');
    expect(iso.date(AT)).toBe('2026-03-14');
    expect(iso.time('2026-03-14T23:30:00Z')).toBe('19:30 EDT');
  });

  it('takes the calendar date of an instant in the display zone, but never shifts a date-only value', () => {
    const tokyo = formatterFor('en-US', 'Asia/Tokyo');
    expect(tokyo.date('2026-03-14T20:00:00Z')).toBe('03/15/2026');
    const la = formatterFor('en-GB', 'America/Los_Angeles');
    expect(la.calendarDate('2026-03-14')).toBe('14/03/2026');
    expect(la.calendarDate('2026-03-14T00:00:00Z')).toBe('14/03/2026');
  });

  it('falls back to UTC for an unknown zone and keeps missing or unparseable values visible', () => {
    const f = formatterFor('en-US', 'Mars/Olympus_Mons');
    expect(f.timeZone).toBe('UTC');
    expect(f.dateTime(AT)).toBe('03/14/2026, 03:04 PM UTC');
    expect(f.dateTime(null)).toBe('—');
    expect(f.dateTime('not a date')).toBe('not a date');
    expect(f.number(undefined)).toBe('—');
  });

  it('formats numbers, percentages and sizes for the locale', () => {
    const f = formatterFor('en-GB');
    expect(f.number(1234567.5)).toBe('1,234,567.5');
    expect(f.percent(0.425)).toBe('43%');
    expect(formatterFor('en-US').compact(1_250_000)).toBe('1.3M');
    expect(f.bytes(1023)).toBe('1,023 B');
    expect(f.bytes(1536)).toBe('1.5 KB');
  });
});

@Component({
  selector: 'opp-format-probe',
  imports: [DateTimePipe, NumberPipe],
  template: `<span id="at">{{ at | oppDateTime }}</span
    ><span id="n">{{ 1234.5 | oppNumber }}</span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class FormatProbe {
  protected readonly at = AT;
  readonly format = inject(DisplayFormat);
}

describe('DisplayFormat (E15-T05)', () => {
  const workspace = { workspaceId: 'ws-1', displayTimeZone: 'America/New_York' } as Workspace;

  beforeEach(() => localStorage.clear());

  it('uses the workspace display time zone unless the user overrides it', () => {
    const format = TestBed.inject(DisplayFormat);
    const prefs = TestBed.inject(UiPreferences);
    expect(format.timeZone()).toBe('UTC');
    TestBed.inject(ActiveWorkspace).enter(workspace);
    expect(format.timeZone()).toBe('America/New_York');
    expect(format.dateTime(AT)).toBe('03/14/2026, 11:04 AM EDT');
    prefs.timeZone.set('utc');
    expect(format.dateTime(AT)).toBe('03/14/2026, 03:04 PM UTC');
    prefs.timeZone.set('local');
    expect(format.timeZone()).toBe(format.browserTimeZone);
  });

  it('re-renders formatted values when the locale or date style changes, without a reload', () => {
    TestBed.inject(ActiveWorkspace).enter({ ...workspace, displayTimeZone: 'UTC' });
    const fixture = TestBed.createComponent(FormatProbe);
    fixture.detectChanges();
    const at = () => fixture.nativeElement.querySelector('#at').textContent;
    expect(at()).toBe('03/14/2026, 03:04 PM UTC');
    expect(fixture.nativeElement.querySelector('#n').textContent).toBe('1,234.5');

    const prefs = TestBed.inject(UiPreferences);
    prefs.locale.set('en-GB');
    fixture.detectChanges();
    expect(at()).toBe('14/03/2026, 15:04 UTC');

    prefs.dateStyle.set('iso');
    fixture.detectChanges();
    expect(at()).toBe('2026-03-14 15:04 UTC');
  });
});
