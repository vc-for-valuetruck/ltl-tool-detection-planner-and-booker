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
  { path: '**', redirectTo: '' },
];
