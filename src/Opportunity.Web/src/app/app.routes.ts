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
import {
  WORKSPACE_SCOPE,
  authGuard,
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
      ...SEARCH_AREAS.map((a): Route => ({
        path: a.path,
        title: a.label,
        canActivate: [requirePermission(a.permission)],
        data: { section: a.label },
        loadComponent: sectionPage,
      })),
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
  ...WORKSPACE_SECTIONS.filter(
    (s) => s.path !== 'documents' && s.path !== 'searches' && s.path !== 'imports',
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
    canActivate: [workspaceGuard],
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
