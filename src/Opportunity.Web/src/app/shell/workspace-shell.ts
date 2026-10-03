import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { WorkspaceContext } from '../core/workspace/workspace-context';

/**
 * Root of `/w/:workspaceId`. Owns the workspace scope: `WorkspaceContext` and any cross-feature
 * workspace store are provided here, and the router destroys this component (and with it every child
 * route and store) whenever the workspace id changes. Feature stores are provided on feature components,
 * never in root or route `providers` (route injectors outlive a workspace switch).
 */
@Component({
  selector: 'opp-workspace-shell',
  imports: [RouterOutlet],
  template: `<router-outlet />`,
  providers: [WorkspaceContext],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'workspace-shell' },
})
export class WorkspaceShell {
  // Created eagerly so the active workspace is set before any child issues an API call.
  protected readonly context = inject(WorkspaceContext);
}
