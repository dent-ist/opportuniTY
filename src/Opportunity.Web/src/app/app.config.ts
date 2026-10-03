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
    ...provideAppRouting(),
    ...provideOpportunityHttp(),
  ],
};
