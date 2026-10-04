import { DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  Injector,
  signal,
} from '@angular/core';
import {
  ApiError,
  UserFacingError,
  describeError,
  toApiError,
} from '../../core/api/problem-details';
import { SessionService } from '../../core/session/session';
import { Workspace, WorkspaceDirectory } from '../../core/workspace/workspace-api';
import { Button, DialogLayout, Icon } from '../../ui';
import { WorkspaceFields } from './workspace-fields';
import {
  type DraftErrors,
  type WorkspaceDraft,
  browserTimeZone,
  hasErrors,
  serverErrors,
  toWrite,
  validateDraft,
} from './workspace-form';

/** Problem code of an MFA requirement the session does not meet (ADR-015 D3.6). */
export const STEP_UP_REQUIRED = 'step-up-required';

/** Where the app returns after an MFA sign-in started here: the Workspaces list, which reopens this dialog. */
export const NEW_WORKSPACE_RETURN_URL = '/workspaces?new=true';

/**
 * New workspace (E04-T07): name, matter number and display time zone. Creating needs `Installation.ManageWorkspaces`
 * and a session signed in with MFA; without MFA the dialog explains it and offers the MFA sign-in instead of a form
 * whose entries would be lost on the way. Closes with the created workspace.
 */
@Component({
  selector: 'opp-new-workspace-dialog',
  imports: [Button, DialogLayout, Icon, WorkspaceFields],
  templateUrl: './new-workspace-dialog.html',
  styleUrl: './workspace-admin.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NewWorkspaceDialog {
  protected readonly ref = inject<DialogRef<Workspace>>(DialogRef);
  private readonly directory = inject(WorkspaceDirectory);
  private readonly session = inject(SessionService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly draft = signal<WorkspaceDraft>({
    name: '',
    matterNumber: '',
    displayTimeZone: browserTimeZone(),
  });
  private readonly submitted = signal(false);
  private readonly server = signal<DraftErrors>({});
  protected readonly saving = signal(false);
  protected readonly failure = signal<UserFacingError | null>(null);
  /** The session has no MFA (from `/me`, or a `step-up-required` answer); `stepUpUrl` from that answer. */
  protected readonly needsMfa = signal(this.session.principal()?.mfa === false);
  private stepUpUrl: string | null = null;

  /** Client checks after the first submit; a server message shows until that field is edited. */
  protected readonly errors = computed<DraftErrors>(() => {
    const local = this.submitted() ? validateDraft(this.draft()) : {};
    return { ...this.server(), ...local };
  });

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

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.saving() || this.needsMfa()) return;
    this.submitted.set(true);
    this.failure.set(null);
    if (hasErrors(validateDraft(this.draft()))) {
      this.focusFirstInvalid();
      return;
    }
    this.saving.set(true);
    try {
      const workspace = await this.directory.create(toWrite(this.draft()));
      this.ref.close(workspace);
    } catch (e) {
      this.onFailure(toApiError(e));
    } finally {
      this.saving.set(false);
    }
  }

  protected signInWithMfa(): void {
    this.session.stepUp(NEW_WORKSPACE_RETURN_URL, this.stepUpUrl);
  }

  private onFailure(error: ApiError): void {
    if (error.code === STEP_UP_REQUIRED) {
      const url = error.problem['stepUpUrl'];
      this.stepUpUrl = typeof url === 'string' ? url : null;
      this.needsMfa.set(true);
      return;
    }
    if (error.code === 'validation') {
      this.server.set(serverErrors(error));
      if (hasErrors(this.server())) {
        this.focusFirstInvalid();
        return;
      }
    }
    this.failure.set(
      error.status === 403
        ? {
            title: 'You cannot create workspaces',
            detail:
              'Creating workspaces needs the Installation Admin role. Ask your installation administrator.',
            reference: error.traceId,
            retryable: false,
          }
        : describeError(error),
    );
  }

  private focusFirstInvalid(): void {
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus(),
      { injector: this.injector },
    );
  }
}
