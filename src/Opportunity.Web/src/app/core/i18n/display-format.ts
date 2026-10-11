import { Injectable, Pipe, PipeTransform, computed, inject } from '@angular/core';
import { UiPreferences } from '../preferences/ui-preferences';
import { ActiveWorkspace } from '../workspace/workspace-context';
import { Formatter, Instant, browserTimeZone, formatterFor, validZone } from './formatter';

/**
 * The display settings of the signed-in user as signals (Q-28, ADR-018 §11): locale (en-US / en-GB), date style
 * (locale order or ISO) and display time zone — the workspace's matter time zone unless the user chose their
 * computer's zone or UTC. Changing a preference re-renders every formatted value without a reload.
 */
@Injectable({ providedIn: 'root' })
export class DisplayFormat {
  private readonly prefs = inject(UiPreferences);
  private readonly active = inject(ActiveWorkspace);

  readonly locale = this.prefs.locale;
  /** The workspace's display time zone (UTC outside a workspace or for a zone this browser does not know). */
  readonly workspaceTimeZone = computed(() => validZone(this.active.current()?.displayTimeZone));
  readonly browserTimeZone = browserTimeZone();
  /** The zone dates are shown in. */
  readonly timeZone = computed(() => {
    switch (this.prefs.timeZone()) {
      case 'local':
        return this.browserTimeZone;
      case 'utc':
        return 'UTC';
      default:
        return this.workspaceTimeZone();
    }
  });
  readonly formatter = computed<Formatter>(() =>
    formatterFor(this.prefs.locale(), this.timeZone(), this.prefs.dateStyle()),
  );

  dateTime(value: Instant | null | undefined, options?: { seconds?: boolean }): string {
    return this.formatter().dateTime(value, options);
  }

  date(value: Instant | null | undefined): string {
    return this.formatter().date(value);
  }

  time(value: Instant | null | undefined): string {
    return this.formatter().time(value);
  }

  calendarDate(value: string | null | undefined): string {
    return this.formatter().calendarDate(value);
  }

  number(value: number | string | null | undefined, options?: Intl.NumberFormatOptions): string {
    return this.formatter().number(value, options);
  }

  percent(fraction: number, maximumFractionDigits?: number): string {
    return this.formatter().percent(fraction, maximumFractionDigits);
  }

  bytes(value: number | string | null | undefined): string {
    return this.formatter().bytes(value);
  }
}

/** `{{ at | oppDateTime }}`: date and time in the display time zone with a zone indicator. */
@Pipe({ name: 'oppDateTime', pure: false })
export class DateTimePipe implements PipeTransform {
  private readonly format = inject(DisplayFormat);
  transform(value: Instant | null | undefined, seconds = false): string {
    return this.format.dateTime(value, { seconds });
  }
}

/** `{{ at | oppDate }}`: the calendar date of an instant in the display time zone. */
@Pipe({ name: 'oppDate', pure: false })
export class DatePipe implements PipeTransform {
  private readonly format = inject(DisplayFormat);
  transform(value: Instant | null | undefined): string {
    return this.format.date(value);
  }
}

/** `{{ n | oppNumber }}`: a number in the user's locale. */
@Pipe({ name: 'oppNumber', pure: false })
export class NumberPipe implements PipeTransform {
  private readonly format = inject(DisplayFormat);
  transform(value: number | string | null | undefined): string {
    return this.format.number(value);
  }
}
