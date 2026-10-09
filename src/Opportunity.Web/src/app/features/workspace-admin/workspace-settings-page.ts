import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import type {
  WorkspaceSearchPlacementKind,
  WorkspaceSearchPlacementState,
} from '../../core/api/generated/models';
import { ApiError, toApiError } from '../../core/api/problem-details';
import { UiPreferences } from '../../core/preferences/ui-preferences';
import { PERMISSIONS } from '../../core/workspace/sections';
import { Workspace, WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { ActiveWorkspace, WorkspaceContext } from '../../core/workspace/workspace-context';
import {
  Badge,
  Button,
  DialogService,
  ErrorState,
  Icon,
  LoadingState,
  ToastService,
} from '../../ui';
import { DeleteWorkspaceDialog, type DeleteWorkspaceData } from './delete-workspace-dialog';
import { WorkspaceFields } from './workspace-fields';
import {
  type DraftErrors,
  type WorkspaceDraft,
  draftOf,
  hasErrors,
  sameDraft,
  serverErrors,
  toWrite,
  validateDraft,
} from './workspace-form';
import { HttpLegalHoldApi, LegalHoldApi } from './legal-hold-api';
import { LegalHoldCard } from './legal-hold-card';
import {
  type DeletionAvailability,
  HttpWorkspaceDeletion,
  WorkspaceDeletion,
} from './workspace-deletion';
import type { DeletionView } from '../workspace-deletions/workspace-deletions-api';

const PLACEMENT_KIND: Record<WorkspaceSearchPlacementKind, string> = {
  shared: 'Shared index',
  dedicated: 'Dedicated index',
};

const PLACEMENT_STATE: Record<WorkspaceSearchPlacementState, string> = {
  active: 'Active',
  building: 'Building',
  moving: 'Moving',
  deleting: 'Deleting',
};

/**
 * Admin › Workspace Settings (E04-T07): name, matter number and display time zone (everything `PUT
 * /api/v1/workspaces/{id}` changes, sent with `If-Match`), the read-only storage profile, search index placement and
 * projection generation, legal holds (E20-T01: the hold state for every member, placing and releasing for hold
 * managers) and the deletion entry point (E20-T02): blocked while a legal hold applies, a request for Workspace Admins
 * (a second person approves it on the Workspace deletions page), or the open request with a link to its progress.
 */
@Component({
  selector: 'opp-workspace-settings-page',
  imports: [
    Badge,
    Button,
    ErrorState,
    Icon,
    LegalHoldCard,
    LoadingState,
    RouterLink,
    WorkspaceFields,
  ],
  templateUrl: './workspace-settings-page.html',
  styleUrl: './workspace-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    { provide: LegalHoldApi, useClass: HttpLegalHoldApi },
    { provide: WorkspaceDeletion, useClass: HttpWorkspaceDeletion },
  ],
  host: { class: 'wsa-page' },
})
export class WorkspaceSettingsPage {
  private readonly directory = inject(WorkspaceDirectory);
  private readonly context = inject(WorkspaceContext);
  private readonly active = inject(ActiveWorkspace);
  private readonly deletion = inject(WorkspaceDeletion);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);
  private readonly prefs = inject(UiPreferences);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly workspaceId = this.context.workspaceId;
  protected readonly canSeeSetup = this.context.can(PERMISSIONS.manageSecurity);
  protected readonly canManageHolds = this.context.can(PERMISSIONS.manageHolds);
  protected readonly placementKind = PLACEMENT_KIND;
  protected readonly placementState = PLACEMENT_STATE;

  protected readonly workspace = signal<Workspace | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiError | null>(null);

  protected readonly draft = signal<WorkspaceDraft>({
    name: '',
    matterNumber: '',
    displayTimeZone: 'UTC',
  });
  private readonly submitted = signal(false);
  private readonly server = signal<DraftErrors>({});
  protected readonly saving = signal(false);
  protected readonly saveError = signal<ApiError | null>(null);
  protected readonly conflict = computed(() => this.saveError()?.status === 412);

  protected readonly dirty = computed(() => {
    const ws = this.workspace();
    return !!ws && !sameDraft(this.draft(), draftOf(ws));
  });
  protected readonly errors = computed<DraftErrors>(() => ({
    ...this.server(),
    ...(this.submitted() ? validateDraft(this.draft()) : {}),
  }));

  protected readonly deletionState = signal<DeletionAvailability | null>(null);
  protected readonly canOpenDeletion = computed(() => {
    const kind = this.deletionState()?.kind;
    return kind === 'locked' || kind === 'allowed';
  });
  protected readonly deletionText = computed(() => {
    const state = this.deletionState();
    switch (state?.kind) {
      case 'unavailable':
        return state.reason;
      case 'locked':
        return 'Deletion is blocked while a legal hold (preservation lock) applies to this workspace.';
      case 'allowed':
        return 'Removes the workspace and its contents once a second person approves the request.';
      case 'pending':
        return this.pendingText(state.deletion);
      default:
        return 'Checking whether this workspace can be deleted…';
    }
  });

  constructor() {
    void this.load();
    void this.deletion.availability().then((a) => this.deletionState.set(a));
  }

  /** The open deletion, in words: who asked when, and where it stands. */
  protected pendingText(d: DeletionView): string {
    const by = d.requestedBy.displayName || 'a Workspace Admin';
    const asked = `Deletion was requested by ${by} on ${this.date(d.requestedAt)}`;
    switch (d.status) {
      case 'requested':
        return `${asked} and is waiting for approval by a second person.`;
      case 'approved':
        return `${asked} and approved. It starts on ${this.date(d.runNotBefore)} unless it is cancelled before then.`;
      case 'halted':
        return `${asked}. It is paused by a legal hold.`;
      default:
        return `${asked} and is in progress.`;
    }
  }

  /** Active legal holds as the workspace resource reports them. */
  protected holdCount(ws: Workspace): number {
    return Number(ws.activePreservationLocks ?? 0);
  }

  /** A hold was placed or released: the workspace (header badge) and the deletion entry point follow. */
  protected async holdsChanged(): Promise<void> {
    try {
      const workspace = await this.directory.refresh(this.workspaceId);
      this.workspace.update((ws) =>
        ws ? { ...ws, activePreservationLocks: workspace.activePreservationLocks } : ws,
      );
      this.active.enter(workspace);
    } catch {
      // The card already shows the change; the badge catches up on the next navigation.
    }
    this.deletionState.set(await this.deletion.availability().catch(() => null));
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

  protected num(value: number | string): string {
    return new Intl.NumberFormat(this.prefs.locale()).format(Number(value));
  }

  /** Reads the workspace afresh (not the copy cached at navigation) so the form starts from the current version. */
  protected async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const workspace = await this.directory.refresh(this.workspaceId);
      this.apply(workspace);
    } catch (e) {
      this.loadError.set(toApiError(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected update(draft: WorkspaceDraft): void {
    const before = this.draft();
    this.draft.set(draft);
    this.server.update((errors) => {
      const next = { ...errors };
      for (const key of Object.keys(next) as (keyof WorkspaceDraft)[])
        if (draft[key] !== before[key]) delete next[key];
      return next;
    });
  }

  protected reset(): void {
    const ws = this.workspace();
    if (ws) this.apply(ws);
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const ws = this.workspace();
    if (!ws || this.saving() || !this.dirty()) return;
    this.submitted.set(true);
    this.saveError.set(null);
    if (hasErrors(validateDraft(this.draft()))) {
      this.focusFirstInvalid();
      return;
    }
    this.saving.set(true);
    try {
      const updated = await this.directory.update(ws, toWrite(this.draft(), ws.storageProfile));
      this.apply(updated);
      this.active.enter(updated);
      this.toasts.show('Workspace settings saved', { tone: 'success' });
    } catch (e) {
      const error = toApiError(e);
      if (error.code === 'validation') {
        this.server.set(serverErrors(error));
        if (hasErrors(this.server())) {
          this.focusFirstInvalid();
          return;
        }
      }
      this.saveError.set(error);
    } finally {
      this.saving.set(false);
    }
  }

  protected async openDeletion(): Promise<void> {
    const state = this.deletionState();
    const ws = this.workspace();
    if (!ws || !state || state.kind === 'unavailable') return;
    if (state.kind !== 'locked' && state.kind !== 'allowed') return;
    const ref = this.dialogs.open<DeletionView, DeleteWorkspaceData>(DeleteWorkspaceDialog, {
      data: { workspaceName: ws.name, availability: state },
      role: 'alertdialog',
      width: '42rem',
      autoFocus: state.kind === 'allowed' ? 'input[type="radio"]:checked' : '[data-autofocus]',
      injector: this.injector,
    });
    const requested = await firstValueFrom(ref.closed);
    if (requested) {
      this.deletionState.set({ kind: 'pending', deletion: requested });
      this.toasts.show('Deletion requested. It starts once a second person approves it.', {
        tone: 'success',
      });
      return;
    }
    // Closed without a request (or after a conflict): show what the server has now.
    this.deletionState.set(await this.deletion.availability().catch(() => state));
  }

  private apply(workspace: Workspace): void {
    this.workspace.set(workspace);
    this.draft.set(draftOf(workspace));
    this.submitted.set(false);
    this.server.set({});
    this.saveError.set(null);
  }

  private focusFirstInvalid(): void {
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus(),
      { injector: this.injector },
    );
  }
}
