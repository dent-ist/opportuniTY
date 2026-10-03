import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map, skip } from 'rxjs';
import { CommandRegistry, cycleRegion } from '../core/commands';
import { SessionService } from '../core/session/session';
import { WorkspaceDirectory } from '../core/workspace/workspace-api';
import { ActiveWorkspace } from '../core/workspace/workspace-context';
import { ADMIN_AREAS, WORKSPACE_SECTIONS, allowedSections } from '../core/workspace/sections';
import { Button, DialogService, Icon, MENU } from '../ui';
import { Brand } from './brand';
import { SHELL_PATHS } from './navigation';
import { SessionEndedDialog } from './session-ended-dialog';
import { ShortcutDialogs } from './shortcuts/shortcut-dialogs';
import { SkipLink } from './skip-link';
import { UserMenu } from './user-menu';
import { WorkspaceSwitcher } from './workspace-switcher';

/**
 * Authenticated layout (familiarity guide §2.2): skip link, banner with the product mark, workspace
 * switcher, RBAC-filtered section tabs and the user menu; then the main region. Focus moves to the new
 * page's heading after each navigation (ADR-018 §3.4). When the session ends, the routed content (and
 * with it every workspace-scoped store) is destroyed and the user is asked to sign in again.
 */
@Component({
  selector: 'opp-app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    Brand,
    Button,
    Icon,
    SkipLink,
    UserMenu,
    WorkspaceSwitcher,
    ...MENU,
  ],
  templateUrl: './app-shell.html',
  styleUrl: './app-shell.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'shell' },
})
export class AppShell {
  private readonly session = inject(SessionService);
  private readonly directory = inject(WorkspaceDirectory);
  private readonly dialogs = inject(DialogService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);

  protected readonly paths = SHELL_PATHS;
  protected readonly workspace = inject(ActiveWorkspace).current;
  protected readonly sections = computed(() =>
    allowedSections(WORKSPACE_SECTIONS, this.workspace()?.permissions ?? []),
  );
  protected readonly adminAreas = computed(() =>
    allowedSections(ADMIN_AREAS, this.workspace()?.permissions ?? []),
  );
  protected readonly sessionEnded = computed(() => this.session.status() === 'expired');

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.router.url),
    ),
    { initialValue: this.router.url },
  );
  protected readonly inAdmin = computed(() => /^\/w\/[^/]+\/admin(\/|$)/.test(this.url()));

  constructor() {
    this.registerShellCommands();
    effect(() => {
      if (this.sessionEnded()) untracked(() => this.onSessionEnded());
    });
    this.router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd),
        skip(1), // Initial load: leave focus at the top of the document.
        takeUntilDestroyed(),
      )
      .subscribe(() => afterNextRender(() => this.focusPageHeading(), { injector: this.injector }));
  }

  /** Keyboard commands that work on every page (familiarity guide §4); features register their own. */
  private registerShellCommands(): void {
    const commands = inject(CommandRegistry);
    const shortcuts = inject(ShortcutDialogs);
    commands.start();
    commands.handle('help.shortcuts', () => void shortcuts.help());
    const hasRegions = () => this.document.querySelector('[data-command-region]') !== null;
    commands.handle('region.next', () => cycleRegion(this.document, 1), { enabled: hasRegions });
    commands.handle('region.previous', () => cycleRegion(this.document, -1), {
      enabled: hasRegions,
    });
  }

  protected signIn(): void {
    this.session.login();
  }

  private onSessionEnded(): void {
    this.directory.clear();
    this.dialogs.open(SessionEndedDialog, { role: 'alertdialog', autoFocus: '[data-autofocus]' });
  }

  private focusPageHeading(): void {
    const main = this.document.getElementById('main');
    const heading = main?.querySelector<HTMLElement>('h1');
    if (heading) {
      heading.tabIndex = -1;
      heading.setAttribute('data-route-focus', '');
      heading.focus();
    } else {
      main?.focus();
    }
  }
}
