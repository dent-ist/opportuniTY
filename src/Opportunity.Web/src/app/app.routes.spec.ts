import { Route, Routes } from '@angular/router';
import { workspaceChildren } from './app.routes';

/** Leaf routes that render a page (redirects and the "Not available" fallback excluded), with their full path. */
function pages(routes: Routes, prefix = ''): { path: string; route: Route }[] {
  return routes.flatMap((route) => {
    const path = `${prefix}/${route.path ?? ''}`;
    if (route.children) return pages(route.children, path);
    if (route.redirectTo !== undefined || route.path === '**') return [];
    return [{ path, route }];
  });
}

// E15-T04 / ADR-018 §14: feature pages (document viewer, admin areas) stay out of the initial bundle. The bundle
// gate (scripts/check-bundle-budgets.mjs) proves the same on the build output.
describe('Workspace routes', () => {
  it('lazy-load every feature and admin page', () => {
    const eager = pages(workspaceChildren)
      .filter(({ route }) => !route.loadComponent && !route.loadChildren)
      .map(({ path }) => path);
    expect(eager).toEqual([]);
    expect(pages(workspaceChildren).map((p) => p.path)).toContain('/admin/fields');
  });
});
