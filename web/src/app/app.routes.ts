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
