import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
} from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { distinctUntilChanged, filter, map, skip } from 'rxjs';
import { CommandRegistry, cycleRegion } from '../core/commands';
import { PreferenceStorage } from '../core/preferences/preference-storage';
import { SessionService } from '../core/session/session';
import { WorkspaceDirectory } from '../core/workspace/workspace-api';
import { ActiveWorkspace } from '../core/workspace/workspace-context';
import {
  ADMIN_AREAS,
  SEARCH_AREAS,
  WORKSPACE_SECTIONS,
  allowedSections,
  type WorkspaceSection,
} from '../core/workspace/sections';
import { Badge, Button, DialogService, Icon, type IconName } from '../ui';
import { Brand } from './brand';
import { SHELL_PATHS } from './navigation';
import { JobTray } from './job-tray';
import { SessionEndedDialog } from './session-ended-dialog';
import { ShortcutDialogs } from './shortcuts/shortcut-dialogs';
import { SkipLink } from './skip-link';
import { UserMenu } from './user-menu';
import { WorkspaceSwitcher } from './workspace-switcher';

interface Subnav {
  readonly label: string;
  readonly base: string;
  readonly areas: readonly WorkspaceSection[];
}

/** Sidebar icon per workspace section (original icons, `ui/icon`). */
const SECTION_ICONS: Readonly<Record<string, IconName>> = {
  documents: 'documents',
  searches: 'search',
  productions: 'production',
  imports: 'import',
  exports: 'export',
  jobs: 'jobs',
};

/**
 * Authenticated layout (familiarity guide §2.2, Q-64): skip link, banner with the product mark, workspace
 * switcher, the job tray and the user menu; a collapsible left sidebar with the RBAC-filtered workspace sections; the main
 * region, which shows a section's own tabs (e.g. Admin's areas) at its top. Focus moves to the new page's
 * heading after each navigation (ADR-018 §3.4). When the session ends, the routed content (and with it every
 * workspace-scoped store) is destroyed and the user is asked to sign in again.
 */
@Component({
  selector: 'opp-app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    Badge,
    Brand,
    Button,
    Icon,
    JobTray,
    SkipLink,
    UserMenu,
    WorkspaceSwitcher,
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
  /**
   * The workspace whose sections the sidebar and job tray show: none while the member still has to accept the
   * workspace's acknowledgment (E20-T03), when only the acknowledgment page is reachable.
   */
  protected readonly navigable = computed(() => {
    const ws = this.workspace();
    return ws && !ws.acknowledgmentPending ? ws : null;
  });
  protected readonly sections = computed(() =>
    allowedSections(WORKSPACE_SECTIONS, this.workspace()?.permissions ?? []),
  );
  protected readonly adminAreas = computed(() =>
    allowedSections(ADMIN_AREAS, this.workspace()?.permissions ?? []),
  );
  protected readonly sessionEnded = computed(() => this.session.status() === 'expired');
  /** The workspace is under a legal hold (E20-T01): shown in the header to every member. */
  protected readonly onHold = computed(
    () => Number(this.workspace()?.activePreservationLocks ?? 0) > 0,
  );

  private readonly preferences = inject(PreferenceStorage);
  /** The user's choice: collapsed to icons; follows the user profile like the other UI preferences. */
  private readonly collapsedPreference = linkedSignal(
    () => this.preferences.read<{ sidebarCollapsed?: boolean }>('shell')?.sidebarCollapsed === true,
  );
  /** Narrow viewports always show the icons-only sidebar so the content keeps its width. */
  private readonly narrow = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 48rem)')
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly collapsed = computed(() => this.collapsedPreference() || this.narrow());

  protected iconFor(path: string): IconName {
    return SECTION_ICONS[path] ?? 'documents';
  }

  protected toggleCollapsed(): void {
    const next = !this.collapsedPreference();
    this.collapsedPreference.set(next);
    this.preferences.write('shell', { sidebarCollapsed: next });
  }

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.router.url),
    ),
    { initialValue: this.router.url },
  );
  protected readonly inAdmin = computed(() => /^\/w\/[^/]+\/admin(\/|$)/.test(this.url()));
  private readonly searchAreas = computed(() =>
    allowedSections(SEARCH_AREAS, this.workspace()?.permissions ?? []),
  );
  /** Tabs across the top of the main region for a section with sub-pages (Q-64). */
  protected readonly subnav = computed((): Subnav | null => {
    if (this.inAdmin()) return { label: 'Admin', base: 'admin', areas: this.adminAreas() };
    if (/^\/w\/[^/]+\/searches(\/|$)/.test(this.url()))
      return { label: 'Searches', base: 'searches', areas: this.searchAreas() };
    return null;
  });

  constructor() {
    this.registerShellCommands();
    effect(() => {
      if (this.sessionEnded()) untracked(() => this.onSessionEnded());
    });
    // A new page takes focus to its heading. A change of the query string only (a page's own state, such as
    // Review mode on Documents) leaves focus to that page.
    this.router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd),
        map((e) => e.urlAfterRedirects.split(/[?#]/)[0]),
        distinctUntilChanged(),
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
