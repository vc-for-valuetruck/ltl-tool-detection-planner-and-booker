import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { RUNTIME_CONFIG, isAuthConfigured } from './runtime-config';
import { DemoDirectorOverlay } from './features/ltl/demo/demo-director-overlay';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, DemoDirectorOverlay],
  templateUrl: './app.html',
  styleUrls: ['./app.css'],
})
export class App {
  private readonly config = inject(RUNTIME_CONFIG);
  private readonly router = inject(Router);

  protected readonly authConfigured = isAuthConfigured(this.config);
  /** Demo-mode identity shown top-right when Entra auth isn't configured for this environment. */
  protected readonly demoEmail = 'demo@valuetruck.com';

  /**
   * Tracks the active URL so the app shell can suppress its own header on the branded
   * `/login` screen — matching FreightDNA, where the sign-in card is rendered
   * full-bleed on a dark background with no top nav.
   */
  private readonly currentUrl = signal(this.router.url);

  protected readonly showShell = computed(() => !this.currentUrl().startsWith('/login'));

  constructor() {
    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe((e) => this.currentUrl.set(e.urlAfterRedirects));
  }
}
