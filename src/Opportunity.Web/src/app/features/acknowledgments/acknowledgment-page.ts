import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ApiError,
  UserFacingError,
  describeError,
  toApiError,
} from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { continueUrl } from '../../core/workspace/acknowledgment';
import { Workspace, WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { ActiveWorkspace, WORKSPACE_DATA } from '../../core/workspace/workspace-context';
import {
  Announcer,
  Badge,
  Button,
  Checkbox,
  ErrorState,
  Icon,
  LoadingState,
  ToastService,
} from '../../ui';
import {
  AcknowledgmentApi,
  AcknowledgmentState,
  HttpAcknowledgmentApi,
} from './acknowledgment-api';

/** The short form of a text fingerprint shown to people (the full SHA-256 is in the roster and audit). */
export function shortHash(hash: string | null | undefined): string {
  return hash ? hash.slice(0, 12) : '';
}

/**
 * The acknowledgment page (E20-T03): what a member reads and accepts before using a workspace that requires a reviewer
 * attestation or protective-order undertaking. Reached from the workspace guard, from any call the API refused with
 * `acknowledgment-required` (a new version was published), or directly. Accepting records the version, a fingerprint
 * of the text and the time; the member then continues where they were going. Lives outside the workspace scope (no
 * sidebar, no job feed), so it holds the active workspace itself while it is open.
 *
 * Keyboard: the text region is focusable and scrolls with the arrow keys; Space ticks the confirmation; Enter on the
 * button accepts.
 */
@Component({
  selector: 'opp-acknowledgment-page',
  imports: [Badge, Button, Checkbox, ErrorState, Icon, LoadingState, RouterLink],
  templateUrl: './acknowledgment-page.html',
  styleUrl: './acknowledgment-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: AcknowledgmentApi, useClass: HttpAcknowledgmentApi }],
  host: { class: 'ack-page' },
})
export class AcknowledgmentPage {
  private readonly api = inject(AcknowledgmentApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly directory = inject(WorkspaceDirectory);
  private readonly toasts = inject(ToastService);
  private readonly announcer = inject(Announcer);
  private readonly prefs = inject(UiPreferences);
  private readonly confirmBox = viewChild<Checkbox>('confirm');

  protected readonly workspace = this.route.snapshot.data[WORKSPACE_DATA] as Workspace;
  private readonly returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
  protected readonly next = continueUrl(this.workspace.workspaceId, this.returnUrl);

  protected readonly state = signal<AcknowledgmentState | null>(null);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly agreed = signal(false);
  protected readonly submitted = signal(false);
  protected readonly saving = signal(false);
  protected readonly failure = signal<UserFacingError | null>(null);
  /** The text changed while it was being read: the new version is shown and must be accepted. */
  protected readonly replaced = signal(false);

  protected readonly pending = computed(() => {
    const s = this.state();
    return !!s && s.required && !s.acknowledged;
  });
  protected readonly heading = computed(() => {
    const s = this.state();
    if (!s || this.loadError()) return 'Acknowledgment';
    return s.required ? (s.title ?? 'Acknowledgment') : 'No acknowledgment needed';
  });
  protected readonly confirmError = computed(() =>
    this.submitted() && !this.agreed() ? 'Tick the box to confirm you agree.' : '',
  );

  constructor() {
    // The page is outside the workspace scope, so it marks the workspace active itself (the HTTP boundary check).
    const active = inject(ActiveWorkspace);
    active.enter(this.workspace);
    inject(DestroyRef).onDestroy(() => active.leave(this.workspace.workspaceId));
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.state.set(await this.api.state(this.workspace.workspaceId));
    } catch (e) {
      this.loadError.set(toApiError(e));
    }
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

  protected short(hash: string | null | undefined): string {
    return shortHash(hash);
  }

  protected async accept(event: Event): Promise<void> {
    event.preventDefault();
    const s = this.state();
    if (!s || !s.version || !s.textSha256 || this.saving()) return;
    this.submitted.set(true);
    this.failure.set(null);
    if (!this.agreed()) {
      this.confirmBox()?.focus();
      return;
    }
    this.saving.set(true);
    try {
      await this.api.accept(this.workspace.workspaceId, s.version, s.textSha256);
      // The workspace answers acknowledgmentPending: false from now on; the guards read it again.
      await this.directory.refresh(this.workspace.workspaceId);
      this.toasts.show('Acknowledgment recorded.', { tone: 'success' });
      await this.router.navigateByUrl(this.next);
    } catch (e) {
      const error = toApiError(e);
      if (error.status === 409 && error.code === 'acknowledgment-outdated') {
        // A new version was published while this one was being read.
        this.agreed.set(false);
        this.submitted.set(false);
        this.replaced.set(true);
        await this.load();
        this.announcer.announce('The text was updated. Read the new version before accepting.');
      } else {
        this.failure.set(describeError(error));
      }
    } finally {
      this.saving.set(false);
    }
  }
}
