import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { RUNTIME_CONFIG, isAuthConfigured } from './runtime-config';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="app-header">
      <h1>LTL Tool Detection, Planner, and Booker</h1>
      <nav class="app-nav">
        <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Home</a>
        <a routerLink="/ltl" routerLinkActive="active">LTL Loads</a>
      </nav>
      <span class="auth-state" [class.ok]="authConfigured">
        {{ authConfigured ? 'Auth configured' : 'Auth not configured' }}
      </span>
    </header>
    <main class="app-main">
      <router-outlet />
    </main>
  `,
})
export class App {
  protected readonly authConfigured = isAuthConfigured(inject(RUNTIME_CONFIG));
}
