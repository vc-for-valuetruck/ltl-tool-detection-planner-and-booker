import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { RUNTIME_CONFIG, isAuthConfigured } from './runtime-config';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <header class="app-header">
      <h1>MyApp</h1>
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
