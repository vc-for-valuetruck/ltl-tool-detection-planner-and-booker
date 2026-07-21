import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  {
    path: 'ltl',
    loadComponent: () => import('./features/ltl/ltl-search').then((m) => m.LtlSearch),
  },
  {
    path: 'ltl/billing',
    loadComponent: () => import('./features/ltl/ltl-billing').then((m) => m.LtlBilling),
  },
  {
    path: 'ltl/exceptions',
    loadComponent: () => import('./features/ltl/ltl-exceptions').then((m) => m.LtlExceptions),
  },
  {
    path: 'ltl/tenders',
    loadComponent: () => import('./features/ltl/ltl-tenders').then((m) => m.LtlTenders),
  },
  {
    path: 'ltl/loads',
    loadComponent: () => import('./features/ltl/ltl-console').then((m) => m.LtlConsole),
  },
  {
    path: 'ltl/loads/:loadNumber',
    loadComponent: () => import('./features/ltl/ltl-load-detail').then((m) => m.LtlLoadDetail),
  },
  {
    path: 'ltl/notifications',
    loadComponent: () =>
      import('./features/ltl/ltl-notifications').then((m) => m.LtlNotifications),
  },
  {
    path: 'ltl/signals',
    loadComponent: () => import('./features/ltl/ltl-signals').then((m) => m.LtlSignals),
  },
  {
    path: 'ltl/reporting',
    loadComponent: () => import('./features/ltl/ltl-reporting').then((m) => m.LtlReporting),
  },
  {
    path: 'ltl/consolidate',
    loadComponent: () => import('./features/ltl/consolidate').then((m) => m.Consolidate),
  },
  {
    path: 'ltl/consolidate/plan/:planId',
    loadComponent: () => import('./features/ltl/plan-detail').then((m) => m.PlanDetail),
  },
  {
    path: 'ltl/consolidate/plan/:planId/click-card',
    loadComponent: () => import('./features/ltl/click-card').then((m) => m.ClickCard),
  },
  { path: '**', redirectTo: '' },
];
