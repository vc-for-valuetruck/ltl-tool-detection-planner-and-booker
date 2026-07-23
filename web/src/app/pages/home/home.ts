import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { RUNTIME_CONFIG } from '../../runtime-config';

/**
 * Post-login landing surface for the LTL Tool. Unauthenticated users never reach
 * this page — the branded /login screen (see pages/login) sits in front via
 * authGuard. Kept intentionally small: welcome copy + an API health probe +
 * deep-link into the LTL console.
 */
@Component({
  selector: 'app-home',
  standalone: true,
  imports: [RouterLink],
  template: `
    <section>
      <p>Welcome to the <strong>LTL Tool Detection, Planner, and Booker</strong>.</p>
      <p>
        <a routerLink="/ltl">Open the LTL console →</a>
      </p>

      <button type="button" (click)="checkHealth()">Check API health</button>
      @if (health(); as h) {
        <pre class="health">{{ h }}</pre>
      }
    </section>
  `,
  styles: [
    `
      section { display: flex; flex-direction: column; gap: 0.75rem; align-items: flex-start; padding: 1.5rem; }
      .health { background: #f4f4f4; padding: 0.5rem 0.75rem; border-radius: 4px; }
      a { color: var(--link, #2563eb); font-weight: 600; }
    `,
  ],
})
export class Home {
  private readonly http = inject(HttpClient);
  private readonly config = inject(RUNTIME_CONFIG);

  protected readonly health = signal<string | null>(null);

  protected checkHealth(): void {
    this.http.get(`${this.config.apiBaseUrl}/health`, { responseType: 'text' }).subscribe({
      next: (res) => this.health.set(res),
      error: (err) => this.health.set(`Error: ${err.status} ${err.statusText}`),
    });
  }
}
