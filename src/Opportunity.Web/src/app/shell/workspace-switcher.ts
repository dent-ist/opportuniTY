import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Workspace, WorkspaceDirectory } from '../core/workspace/workspace-api';
import { RecentWorkspaces } from '../core/workspace/workspace-context';
import { Icon, MENU } from '../ui';
import { SHELL_PATHS } from './navigation';

/**
 * Workspace name ▾ in the header (familiarity guide §2.2): recent workspaces plus "All workspaces".
 * Choosing one navigates to `/w/{id}`; the router then rebuilds the workspace scope.
 */
@Component({
  selector: 'opp-workspace-switcher',
  imports: [Icon, RouterLink, ...MENU],
  template: `<button
      type="button"
      class="switcher__trigger"
      [cdkMenuTriggerFor]="menu"
      (cdkMenuOpened)="loadNames()"
    >
      <span class="opp-visually-hidden">Workspace: </span>
      <span class="switcher__name">{{ workspace().name }}</span>
      <opp-icon name="chevron-down" />
    </button>
    <ng-template #menu>
      <div cdkMenu class="opp-menu" aria-label="Switch workspace">
        @if (recent().length) {
          <div cdkMenuGroup aria-label="Recent workspaces">
            <div class="opp-menu__heading" aria-hidden="true">Recent workspaces</div>
            @for (ws of recent(); track ws.workspaceId) {
              <a cdkMenuItem class="opp-menu__item" [routerLink]="['/w', ws.workspaceId]">
                {{ ws.name }}
                @if (ws.matterNumber) {
                  <span class="opp-menu__detail">{{ ws.matterNumber }}</span>
                }
              </a>
            }
          </div>
          <div class="opp-menu__separator" role="separator"></div>
        }
        <a cdkMenuItem class="opp-menu__item" [routerLink]="allWorkspaces">All workspaces</a>
      </div>
    </ng-template>`,
  styleUrl: './workspace-switcher.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceSwitcher {
  readonly workspace = input.required<Workspace>();

  private readonly directory = inject(WorkspaceDirectory);
  private readonly recentIds = inject(RecentWorkspaces).ids;
  protected readonly allWorkspaces = SHELL_PATHS.workspaces;
  private loading = false;

  /** Other recently opened workspaces whose names are known (the user is still a member). */
  protected readonly recent = computed(() => {
    const known = this.directory.known();
    return this.recentIds()
      .filter((id) => id !== this.workspace().workspaceId)
      .map((id) => known.get(id))
      .filter((ws) => ws !== undefined);
  });

  /** Names come from the workspace list, fetched once per session on first use. */
  protected loadNames(): void {
    if (this.loading || this.directory.known().size > 0) return;
    this.loading = true;
    this.directory
      .list()
      .catch(() => undefined) // The menu still offers "All workspaces", which shows the error.
      .finally(() => (this.loading = false));
  }
}
