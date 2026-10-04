import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { JobFeed } from '../core/jobs/job-feed';
import { HttpJobsApi, JobsApi } from '../core/jobs/jobs-api';
import { QueryHistory } from '../core/search/query-history';
import { WorkspaceContext } from '../core/workspace/workspace-context';

/**
 * Root of `/w/:workspaceId`. Owns the workspace scope: `WorkspaceContext` and any cross-feature
 * workspace store (query history, the job feed) are provided here, and the router destroys this component (and with it every child
 * route and store) whenever the workspace id changes. Feature stores are provided on feature components,
 * never in root or route `providers` (route injectors outlive a workspace switch).
 */
@Component({
  selector: 'opp-workspace-shell',
  imports: [RouterOutlet],
  template: `<router-outlet />`,
  providers: [WorkspaceContext, QueryHistory, { provide: JobsApi, useClass: HttpJobsApi }, JobFeed],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'workspace-shell' },
})
export class WorkspaceShell {
  // Created eagerly so the active workspace is set before any child issues an API call.
  protected readonly context = inject(WorkspaceContext);
  // Follows the workspace's jobs for the header's job tray and notifications (E06-T07) while the workspace is open.
  protected readonly jobs = inject(JobFeed);
}
