import { Routes } from '@angular/router';

/**
 * Demo Director feature routes. Lazy-loaded as a single unit from the app router
 * (`app.routes.ts` → `loadChildren`), which is the ONLY seam through which application code
 * reaches the demo feature. Everything the walkthrough needs (launcher, overlay, service, script,
 * speech) is reachable from this chunk and nothing in it is statically imported by app code — see
 * `web/scripts/check-demo-boundaries.mjs`.
 */
export const DEMO_DIRECTOR_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./demo-director-launcher').then((m) => m.DemoDirectorLauncher),
    data: { crumb: 'Demo Director' },
  },
];
