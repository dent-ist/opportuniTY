import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiError, describeError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { acknowledgmentPath } from '../../core/workspace/acknowledgment';
import { WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  BadgeTone,
  Button,
  DialogService,
  EmptyState,
  ErrorState,
  Icon,
  LoadingState,
  ToastService,
} from '../../ui';
import {
  AcknowledgmentApi,
  AcknowledgmentVersion,
  HttpAcknowledgmentApi,
  RosterEntry,
} from './acknowledgment-api';
import { shortHash } from './acknowledgment-page';
import {
  PublishAcknowledgmentData,
  PublishAcknowledgmentDialog,
} from './publish-acknowledgment-dialog';

const STATUS: Record<RosterEntry['status'], { label: string; tone: BadgeTone }> = {
  current: { label: 'Accepted', tone: 'success' },
  outdated: { label: 'Earlier version only', tone: 'warning' },
  pending: { label: 'Not accepted', tone: 'neutral' },
};

/**
 * Admin › Acknowledgments (E20-T03): the reviewer attestation or protective-order text members must accept before
 * using the workspace, its version history, and who accepted which version (with a CSV download of the roster through
 * the protected-content gateway). Publishing a new version asks everyone to accept again, the publisher included, so
 * the page sends the publisher to the acknowledgment page right away. Needs `Workspace.ManageAcknowledgments`.
 */
@Component({
  selector: 'opp-acknowledgments-admin-page',
  imports: [Badge, Button, EmptyState, ErrorState, Icon, LoadingState],
  templateUrl: './acknowledgments-admin-page.html',
  styleUrl: './acknowledgments-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: AcknowledgmentApi, useClass: HttpAcknowledgmentApi }],
  host: { class: 'acka-page' },
})
export class AcknowledgmentsAdminPage {
  private readonly api = inject(AcknowledgmentApi);
  private readonly context = inject(WorkspaceContext);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly router = inject(Router);
  private readonly directory = inject(WorkspaceDirectory);
  private readonly injector = inject(Injector);
  private readonly prefs = inject(UiPreferences);

  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly versions = signal<readonly AcknowledgmentVersion[]>([]);
  protected readonly currentVersion = signal(0);
  protected readonly current = signal<AcknowledgmentVersion | null>(null);
  protected readonly roster = signal<readonly RosterEntry[]>([]);
  protected readonly rosterTotal = signal(0);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly exportUrl = this.api.exportUrl(this.context.workspaceId);

  protected readonly counts = computed(() => {
    const entries = this.roster();
    return {
      current: entries.filter((e) => e.status === 'current').length,
      outdated: entries.filter((e) => e.status === 'outdated').length,
      pending: entries.filter((e) => e.status === 'pending').length,
    };
  });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    const ws = this.context.workspaceId;
    try {
      const [versions, roster] = await Promise.all([this.api.versions(ws), this.api.roster(ws)]);
      this.versions.set(versions.items);
      this.currentVersion.set(versions.currentVersion);
      this.current.set(
        versions.currentVersion ? await this.api.version(ws, versions.currentVersion) : null,
      );
      this.roster.set(roster.items);
      this.rosterTotal.set(roster.total);
      this.nextCursor.set(roster.nextCursor);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected async loadMore(): Promise<void> {
    const cursor = this.nextCursor();
    if (!cursor || this.loadingMore()) return;
    this.loadingMore.set(true);
    try {
      const page = await this.api.roster(this.context.workspaceId, cursor);
      this.roster.update((entries) => [...entries, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    } catch (e) {
      const error = describeError(toApiError(e));
      this.toasts.show(`${error.title}. ${error.detail}`, { tone: 'error' });
    } finally {
      this.loadingMore.set(false);
    }
  }

  protected async publish(): Promise<void> {
    const ref = this.dialogs.open<AcknowledgmentVersion, PublishAcknowledgmentData>(
      PublishAcknowledgmentDialog,
      {
        data: {
          workspaceId: this.context.workspaceId,
          currentVersion: this.currentVersion(),
          current: this.current(),
        },
        width: '44rem',
        autoFocus: 'input',
        injector: this.injector,
      },
    );
    const published = await firstValueFrom(ref.closed);
    if (!published) {
      await this.load();
      return;
    }
    this.toasts.show(`Version ${published.version} published. Accept it to continue.`, {
      tone: 'success',
    });
    // The publisher, like everyone else, accepts the new version before using the workspace again.
    await this.directory.refresh(this.context.workspaceId);
    await this.router.navigate([acknowledgmentPath(this.context.workspaceId)], {
      queryParams: { returnUrl: this.router.url },
    });
  }

  protected status(entry: RosterEntry): { label: string; tone: BadgeTone } {
    return STATUS[entry.status];
  }

  protected latest(entry: RosterEntry): RosterEntry['acceptances'][number] | null {
    return entry.acceptances[0] ?? null;
  }

  protected name(entry: { displayName?: string | null; userId: string }): string {
    return entry.displayName || entry.userId;
  }

  protected short(hash: string | null | undefined): string {
    return shortHash(hash);
  }

  protected date(value: unknown): string {
    if (!value) return '';
    const d = new Date(String(value));
    return Number.isNaN(d.getTime())
      ? String(value)
      : new Intl.DateTimeFormat(this.prefs.locale(), {
          dateStyle: 'medium',
          timeStyle: 'short',
        }).format(d);
  }
}
