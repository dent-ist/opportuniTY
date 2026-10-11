import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { ApiError, toApiError } from '../../../core/api/problem-details';
import { Badge, BadgeTone, Button, ErrorState, LoadingState } from '../../../ui';
import {
  EntryChange,
  LogChanges,
  LogTreatment,
  LogVersion,
  PrivilegeLogApi,
} from './privilege-logs-api';
import { DisplayFormat } from '../../../core/i18n';

const KIND: Record<EntryChange['kind'], { tone: BadgeTone; text: string }> = {
  added: { tone: 'success', text: 'Added' },
  removed: { tone: 'danger', text: 'Removed' },
  changed: { tone: 'info', text: 'Changed' },
};

const TREATMENT: Record<LogTreatment, string> = {
  withheld: 'Withheld',
  redacted: 'Redacted',
  redactedPrivacy: 'Redacted (Privacy)',
};

/**
 * What changed in a privilege log version since the previous version of the same log (#182): documents added to or
 * removed from the log and, for documents in both, each changed cell with its value before and after (for example
 * a description or privilege basis recoded after the earlier version was generated).
 */
@Component({
  selector: 'opp-privilege-log-changes',
  imports: [Badge, Button, ErrorState, LoadingState],
  templateUrl: './privilege-log-changes.html',
  styleUrl: './privilege-log-tables.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PrivilegeLogChanges {
  private readonly api = inject(PrivilegeLogApi);
  private readonly fmt = inject(DisplayFormat);

  readonly version = input.required<LogVersion>();

  protected readonly changes = signal<LogChanges | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiError | null>(null);

  constructor() {
    effect(() => {
      const logId = this.version().logId;
      untracked(() => {
        this.changes.set(null);
        void this.load(logId, null);
      });
    });
  }

  protected async more(): Promise<void> {
    await this.load(this.version().logId, this.changes()?.nextCursor ?? null);
  }

  protected retry(): void {
    void this.load(this.version().logId, this.changes()?.nextCursor ?? null);
  }

  private async load(logId: string, cursor: string | null): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await this.api.changes(logId, cursor);
      if (this.version().logId !== logId) return;
      const before = cursor ? (this.changes()?.items ?? []) : [];
      this.changes.set({ ...page, items: [...before, ...page.items] });
    } catch (e) {
      this.error.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected kind(change: EntryChange): { tone: BadgeTone; text: string } {
    return KIND[change.kind];
  }

  protected treatment(change: EntryChange): string {
    return TREATMENT[change.treatment];
  }

  protected count(n: number): string {
    return this.fmt.number(n);
  }
}
