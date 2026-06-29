import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { MsalService } from '@azure/msal-angular';
import { RUNTIME_CONFIG, isAuthConfigured } from '../../runtime-config';

@Component({
  selector: 'app-home',
  standalone: true,
  template: `
    <section>
      <p>This is the <strong>MyApp</strong> starter SPA. Replace this page with your application.</p>

      @if (authConfigured) {
        <button type="button" (click)="login()">Sign in</button>
      } @else {
        <p class="hint">
          Set <code>RUNTIME_TENANT_ID</code> and <code>RUNTIME_WEB_CLIENT_ID</code> to enable sign-in.
        </p>
      }

      <button type="button" (click)="checkHealth()">Check API health</button>
      @if (health(); as h) {
        <pre class="health">{{ h }}</pre>
      }
    </section>
  `,
  styles: [
    `
      section { display: flex; flex-direction: column; gap: 0.75rem; align-items: flex-start; }
      .hint { color: #555; }
      .health { background: #f4f4f4; padding: 0.5rem 0.75rem; border-radius: 4px; }
    `,
  ],
})
export class Home {
  private readonly http = inject(HttpClient);
  private readonly msal = inject(MsalService);
  private readonly config = inject(RUNTIME_CONFIG);

  protected readonly authConfigured = isAuthConfigured(this.config);
  protected readonly health = signal<string | null>(null);

  protected login(): void {
    const scopes = this.config.apiScope ? [this.config.apiScope] : [];
    this.msal.loginRedirect({ scopes });
  }

  protected checkHealth(): void {
    this.http.get(`${this.config.apiBaseUrl}/health`, { responseType: 'text' }).subscribe({
      next: (res) => this.health.set(res),
      error: (err) => this.health.set(`Error: ${err.status} ${err.statusText}`),
    });
  }
}
