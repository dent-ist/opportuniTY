import { Route, Routes } from '@angular/router';
import { WORKSPACE_DATA } from './core/workspace/workspace-context';
import {
  ADMIN_AREAS,
  PERMISSIONS,
  SEARCH_AREAS,
  WORKSPACE_SECTIONS,
} from './core/workspace/sections';
import { devRoutes } from './dev/dev-routes';
import { AppShell } from './shell/app-shell';
import { acknowledgmentGuard } from './core/workspace/acknowledgment';
import {
  WORKSPACE_SCOPE,
  authGuard,
  currentWorkspaceResolver,
  requirePermission,
  workspaceGuard,
  workspaceResolver,
} from './shell/navigation';
import { ErrorPage } from './shell/pages/error-page';
import { NotAvailablePage } from './shell/pages/not-available';
import { PublicLayout } from './shell/public-layout';
import { WorkspaceShell } from './shell/workspace-shell';

const sectionPage = () => import('./shell/pages/section-page').then((m) => m.SectionPage);

/** Admin areas that have shipped; the others show the section placeholder (E04-T07). */
const ADMIN_PAGES: Readonly<Record<string, Route['loadComponent']>> = {
  settings: () =>
    import('./features/workspace-admin/workspace-settings-page').then(
      (m) => m.WorkspaceSettingsPage,
    ),
  setup: () =>
    import('./features/workspace-admin/workspace-setup-page').then((m) => m.WorkspaceSetupPage),
  'highlight-sets': () =>
    import('./features/highlight-sets/highlight-sets-page').then((m) => m.HighlightSetsPage),
  // Field and coding layout administration (E04-T06).
  fields: () => import('./features/field-admin/fields-page').then((m) => m.FieldsPage),
  choices: () => import('./features/field-admin/choices-page').then((m) => m.ChoicesPage),
  'coding-layouts': () =>
    import('./features/field-admin/coding-layouts-page').then((m) => m.CodingLayoutsPage),
  // Roles, permissions and user/group assignment (E05-T08).
  'users-groups': () =>
    import('./features/role-admin/users-groups-page').then((m) => m.UsersGroupsPage),
  'roles-security': () =>
    import('./features/role-admin/roles-security-page').then((m) => m.RolesSecurityPage),
  // Reviewer attestation and protective-order acknowledgment (E20-T03).
  acknowledgments: () =>
    import('./features/acknowledgments/acknowledgments-admin-page').then(
      (m) => m.AcknowledgmentsAdminPage,
    ),
};

/** Children of `/w/:workspaceId`, each guarded by the permission that shows it in the navigation. */
export const workspaceChildren: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'documents' },
  {
    path: 'documents',
    title: 'Documents',
    canActivate: [requirePermission(PERMISSIONS.documentView)],
    loadComponent: () => import('./features/documents/documents-page').then((m) => m.DocumentsPage),
  },
  {
    path: 'searches',
    children: [
      { path: '', pathMatch: 'full', redirectTo: SEARCH_AREAS[0].path },
      ...SEARCH_AREAS.map((a): Route => {
        const base = {
          path: a.path,
          canActivate: [requirePermission(a.permission)],
          data: { section: a.label },
        };
        if (a.path === 'saved')
          return {
            ...base,
            title: a.label,
            // Saved Searches (E16-T11).
            loadComponent: () =>
              import('./features/searches/saved-searches-page').then((m) => m.SavedSearchesPage),
          };
        if (a.path === 'terms-reports')
          return {
            ...base,
            // Search Terms Reports (#180): the list, New report, and one report's progress and results.
            children: [
              {
                path: '',
                pathMatch: 'full',
                title: a.label,
                loadComponent: () =>
                  import('./features/searches/terms-reports/terms-reports-page').then(
                    (m) => m.TermsReportsPage,
                  ),
              },
              {
                path: 'new',
                title: 'New Search Terms Report',
                loadComponent: () =>
                  import('./features/searches/terms-reports/new-terms-report-page').then(
                    (m) => m.NewTermsReportPage,
                  ),
              },
              {
                path: ':reportId',
                title: 'Search Terms Report',
                loadComponent: () =>
                  import('./features/searches/terms-reports/terms-report-page').then(
                    (m) => m.TermsReportPage,
                  ),
              },
            ],
          };
        if (a.path === 'privilege-conflicts')
          return {
            ...base,
            title: a.label,
            // Privilege Conflicts (E13-T02): the on-demand report, CSV and "propagate to duplicates".
            loadComponent: () =>
              import('./features/searches/privilege-conflicts/privilege-conflicts-page').then(
                (m) => m.PrivilegeConflictsPage,
              ),
          };
        return { ...base, title: a.label, loadComponent: sectionPage };
      }),
    ],
  },
  {
    // Import history, New Import wizard and one import's progress and report (E08-T08).
    path: 'imports',
    canActivate: [requirePermission(PERMISSIONS.importRun)],
    children: [
      {
        path: '',
        pathMatch: 'full',
        title: 'Imports',
        loadComponent: () => import('./features/imports/imports-page').then((m) => m.ImportsPage),
      },
      {
        path: 'new',
        title: 'New Import',
        loadComponent: () => import('./features/imports/import-wizard').then((m) => m.ImportWizard),
      },
      {
        path: ':importId',
        title: 'Import',
        loadComponent: () => import('./features/imports/import-detail').then((m) => m.ImportDetail),
      },
    ],
  },
  {
    // The workspace's jobs and one job's progress, failures, retry and cancel (E06-T07).
    path: 'jobs',
    canActivate: [requirePermission(PERMISSIONS.documentView)],
    children: [
      {
        path: '',
        pathMatch: 'full',
        title: 'Jobs',
        loadComponent: () => import('./features/jobs/jobs-page').then((m) => m.JobsPage),
      },
      {
        path: ':jobId',
        title: 'Job',
        loadComponent: () => import('./features/jobs/job-detail').then((m) => m.JobDetail),
      },
    ],
  },
  ...WORKSPACE_SECTIONS.filter(
    (s) => !['documents', 'searches', 'imports', 'jobs'].includes(s.path),
  ).map((s): Route => ({
    path: s.path,
    title: s.label,
    canActivate: [requirePermission(s.permission)],
    data: { section: s.label },
    loadComponent: sectionPage,
  })),
  {
    path: 'admin',
    children: [
      { path: '', pathMatch: 'full', redirectTo: ADMIN_AREAS[0].path },
      ...ADMIN_AREAS.map((a): Route => ({
        path: a.path,
        title: a.label,
        canActivate: [requirePermission(a.permission)],
        data: { section: a.label },
        loadComponent: ADMIN_PAGES[a.path] ?? sectionPage,
      })),
    ],
  },
  { path: '**', title: 'Not available', component: NotAvailablePage },
];

/** The workspace scope route; exported so tests can mount feature children under the real scope. */
export function workspaceRoute(children: Routes = workspaceChildren): Route {
  return {
    path: 'w/:workspaceId',
    component: WorkspaceShell,
    canActivate: [workspaceGuard, acknowledgmentGuard],
    resolve: { [WORKSPACE_DATA]: workspaceResolver },
    data: { [WORKSPACE_SCOPE]: true },
    children,
  };
}

export function appRoutes(workspace: Route = workspaceRoute()): Routes {
  return [
    {
      path: 'sign-in',
      component: PublicLayout,
      children: [
        {
          path: '',
          title: 'Sign in',
          loadComponent: () => import('./shell/pages/sign-in').then((m) => m.SignInPage),
        },
      ],
    },
    {
      path: 'session-error',
      component: PublicLayout,
      children: [{ path: '', title: 'Something went wrong', component: ErrorPage }],
    },
    ...devRoutes,
    {
      path: '',
      component: AppShell,
      canActivate: [authGuard],
      children: [
        { path: '', pathMatch: 'full', redirectTo: 'workspaces' },
        {
          path: 'workspaces',
          title: 'Workspaces',
          loadComponent: () => import('./shell/pages/workspaces').then((m) => m.WorkspacesPage),
        },
        {
          // Workspace deletions (E20-T02): installation level, so they outlive the deleted workspace.
          path: 'workspace-deletions',
          title: 'Workspace deletions',
          loadComponent: () =>
            import('./features/workspace-deletions/workspace-deletions-page').then(
              (m) => m.WorkspaceDeletionsPage,
            ),
        },
        {
          path: 'workspace-deletions/:deletionId',
          title: 'Workspace deletion',
          loadComponent: () =>
            import('./features/workspace-deletions/workspace-deletion-page').then(
              (m) => m.WorkspaceDeletionPage,
            ),
        },
        {
          // The acknowledgment a member accepts before using the workspace (E20-T03). Outside the workspace scope:
          // the workspace's sections stay closed until it is accepted.
          path: 'w/:workspaceId/acknowledgment',
          title: 'Acknowledgment',
          canActivate: [workspaceGuard],
          resolve: { [WORKSPACE_DATA]: currentWorkspaceResolver },
          loadComponent: () =>
            import('./features/acknowledgments/acknowledgment-page').then(
              (m) => m.AcknowledgmentPage,
            ),
        },
        {
          path: 'about',
          title: 'About',
          loadComponent: () => import('./shell/pages/about').then((m) => m.AboutPage),
        },
        workspace,
        { path: 'error', title: 'Something went wrong', component: ErrorPage },
        { path: 'not-available', title: 'Not available', component: NotAvailablePage },
        { path: '**', title: 'Not available', component: NotAvailablePage },
      ],
    },
  ];
}

export const routes: Routes = appRoutes();
