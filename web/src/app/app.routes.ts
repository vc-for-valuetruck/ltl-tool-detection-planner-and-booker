import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  // Branded sign-in screen (no shell chrome; app.html hides its header on /login).
  // Matches the FreightDNA login pattern — full-screen card + Microsoft redirect.
  {
    path: 'login',
    loadComponent: () => import('./pages/login/login-page').then((m) => m.LoginPage),
  },
  // Landing "welcome" surface. Kept for the API-health probe; guarded so unauthenticated
  // users can't reach it — they land on /login instead.
  {
    path: '',
    pathMatch: 'full',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  // Print-only artifact — standalone, no LTL shell chrome. Declared BEFORE the shell parent so the
  // full path wins the match and the click card renders on a clean page.
  {
    path: 'ltl/consolidate/plan/:planId/click-card',
    canActivate: [authGuard],
    loadComponent: () => import('./features/ltl/click-card').then((m) => m.ClickCard),
  },
  // Read-only Yard webhook admin panel — standalone, outside the LTL shell (mirrors the Alvys ops
  // webhook admin). Shows recent inbound Yard deliveries and receiver configuration; writes nothing.
  {
    path: 'admin/yard/webhooks',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/ltl/yard-webhooks-admin').then((m) => m.YardWebhooksAdmin),
  },
  {
    path: 'ltl',
    canActivate: [authGuard],
    loadComponent: () => import('./features/ltl/ltl-shell').then((m) => m.LtlShell),
    children: [
      {
        path: '',
        loadComponent: () => import('./features/ltl/ltl-search').then((m) => m.LtlSearch),
      },
      {
        path: 'billing',
        loadComponent: () => import('./features/ltl/ltl-billing').then((m) => m.LtlBilling),
        data: { crumb: 'Billing' },
      },
      {
        path: 'exceptions',
        loadComponent: () => import('./features/ltl/ltl-exceptions').then((m) => m.LtlExceptions),
        data: { crumb: 'Exceptions' },
      },
      {
        path: 'tenders',
        loadComponent: () => import('./features/ltl/ltl-tenders').then((m) => m.LtlTenders),
        data: { crumb: 'Tenders' },
      },
      {
        path: 'loads',
        loadComponent: () => import('./features/ltl/ltl-console').then((m) => m.LtlConsole),
        data: { crumb: 'Loads' },
      },
      {
        path: 'loads/:loadNumber',
        loadComponent: () => import('./features/ltl/ltl-load-detail').then((m) => m.LtlLoadDetail),
        data: { crumb: 'Load detail' },
      },
      {
        path: 'notifications',
        loadComponent: () =>
          import('./features/ltl/ltl-notifications').then((m) => m.LtlNotifications),
        data: { crumb: 'Notifications' },
      },
      {
        path: 'signals',
        loadComponent: () => import('./features/ltl/ltl-signals').then((m) => m.LtlSignals),
        data: { crumb: 'Signals' },
      },
      {
        path: 'reporting',
        loadComponent: () => import('./features/ltl/ltl-reporting').then((m) => m.LtlReporting),
        data: { crumb: 'Reporting' },
      },
      {
        path: 'assignments',
        loadComponent: () =>
          import('./features/ltl/ltl-assignments').then((m) => m.LtlAssignments),
        data: { crumb: 'Assignments' },
      },
      {
        path: 'consolidate',
        loadComponent: () => import('./features/ltl/consolidate').then((m) => m.Consolidate),
        data: { crumb: 'Consolidate' },
      },
      {
        path: 'dock',
        loadComponent: () => import('./features/ltl/dock').then((m) => m.Dock),
        data: { crumb: 'Dock' },
      },
      {
        path: 'dispatch',
        loadComponent: () =>
          import('./features/ltl/dispatch-assist').then((m) => m.DispatchAssist),
        data: { crumb: 'Dispatch Assist' },
      },
      {
        path: 'consolidate/plan/:planId',
        loadComponent: () => import('./features/ltl/plan-detail').then((m) => m.PlanDetail),
        data: { crumb: 'Plan detail' },
      },
    ],
  },
  // Any unknown path is bounced through the root — authGuard then decides whether the
  // user lands on /login or /ltl-friendly surfaces.
  { path: '**', redirectTo: '' },
];
