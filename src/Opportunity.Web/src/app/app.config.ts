import { APP_BASE_HREF } from '@angular/common';
import {
  ApplicationConfig,
  EnvironmentProviders,
  Provider,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import {
  RouteReuseStrategy,
  Routes,
  TitleStrategy,
  provideRouter,
  withComponentInputBinding,
} from '@angular/router';
import { routes } from './app.routes';
import { provideOpportunityHttp } from './core/api/http';
import { ShellTitleStrategy, WorkspaceRouteReuseStrategy } from './shell/navigation';

/** Router with the shell's title and workspace-scope rules (shared with tests). */
export function provideAppRouting(appRoutes: Routes = routes): (Provider | EnvironmentProviders)[] {
  return [
    provideRouter(appRoutes, withComponentInputBinding()),
    { provide: RouteReuseStrategy, useClass: WorkspaceRouteReuseStrategy },
    { provide: TitleStrategy, useExisting: ShellTitleStrategy },
  ];
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // No <base href>: the CSP has `base-uri 'none'` (ADR-015 D4.6). Assets use absolute URLs (deployUrl "/"),
    // so deep links such as /w/{id}/documents load the same bundles.
    { provide: APP_BASE_HREF, useValue: '/' },
    ...provideAppRouting(),
    ...provideOpportunityHttp(),
  ],
};
