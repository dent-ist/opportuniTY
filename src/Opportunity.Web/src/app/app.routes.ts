import { Route, Routes } from '@angular/router';
import { WORKSPACE_DATA } from './core/workspace/workspace-context';
import { ADMIN_AREAS, PERMISSIONS, WORKSPACE_SECTIONS } from './core/workspace/sections';
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

/** Children of `/w/:workspaceId`, each guarded by the permission that shows it in the navigation. */
export const workspaceChildren: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'documents' },
  {
    path: 'documents',
    title: 'Documents',
    canActivate: [requirePermission(PERMISSIONS.documentView)],
    loadComponent: () => import('./features/documents/documents-page').then((m) => m.DocumentsPage),
  },
  ...WORKSPACE_SECTIONS.filter((s) => s.path !== 'documents').map((s): Route => ({
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
        loadComponent: sectionPage,
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
