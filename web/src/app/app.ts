import { NgComponentOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { filter } from 'rxjs';
import { RUNTIME_CONFIG, isAuthConfigured } from './runtime-config';
import { OverlayOutletService } from './core/overlay/overlay-outlet.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, NgComponentOutlet],
  templateUrl: './app.html',
  styleUrls: ['./app.css'],
})
export class App {
  private readonly config = inject(RUNTIME_CONFIG);
  private readonly router = inject(Router);
  private readonly msal = inject(MsalService);

  /**
   * App-owned outlet for a persistent, route-spanning overlay. Isolated features (the lazy-loaded
   * Demo Director) mount their overlay here; the app root never imports the feature itself. Rendered
   * via `NgComponentOutlet` in the template — null until a feature mounts something.
   */
  protected readonly overlayOutlet = inject(OverlayOutletService);

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

  /**
   * Signed-in user label shown in the shell header when Entra auth is configured. Refreshes on
   * every navigation so a returning-from-Entra redirect picks the account up as soon as MSAL's
   * `handleRedirectPromise` (see `provideAppInitializer` in app.config.ts) sets it active. Falls
   * back to the demo email only when auth is not configured — the "Demo mode" pill in the
   * template makes that state legible so nobody mistakes it for a real signed-in identity.
   */
  private readonly accountLabel = signal<string>(this.resolveAccountLabel());

  protected readonly displayEmail = computed(() =>
    this.authConfigured ? this.accountLabel() : this.demoEmail,
  );

  constructor() {
    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe((e) => {
        this.currentUrl.set(e.urlAfterRedirects);
        // MSAL's active account is populated by `provideAppInitializer` on cold load, and again
        // by `handleRedirectPromise` after a sign-in redirect completes — both of which run
        // before the first NavigationEnd, so re-reading here catches the post-sign-in identity
        // without a page reload.
        this.accountLabel.set(this.resolveAccountLabel());
      });
  }

  private resolveAccountLabel(): string {
    if (!this.authConfigured) {
      return this.demoEmail;
    }
    const account = this.msal.instance.getActiveAccount() ?? this.msal.instance.getAllAccounts()[0];
    if (!account) {
      return 'Signing in…';
    }
    // Prefer the username (usually preferred_username / UPN); fall back to display name, then id.
    return account.username || account.name || account.localAccountId;
  }
}
