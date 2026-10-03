import { Routes } from '@angular/router';

// Development-only routes. The production build swaps this file for dev-routes.prod.ts (angular.json
// fileReplacements), so none of this code ships.
export const devRoutes: Routes = [
  {
    path: 'dev/components',
    title: 'Component showcase · opportuniTY',
    loadComponent: () => import('./showcase/showcase').then((m) => m.Showcase),
  },
];
