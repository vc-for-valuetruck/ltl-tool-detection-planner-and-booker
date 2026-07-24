import {
  ApplicationConfig,
  EnvironmentProviders,
  Provider,
  inject,
  provideAppInitializer,
  provideZoneChangeDetection,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors, withInterceptorsFromDi, HTTP_INTERCEPTORS } from '@angular/common/http';
import { sessionExpiredInterceptor } from './core/auth/session-expired.interceptor';
import {
  IPublicClientApplication,
  PublicClientApplication,
  InteractionType,
  BrowserCacheLocation,
} from '@azure/msal-browser';
import {
  MSAL_INSTANCE,
  MSAL_GUARD_CONFIG,
  MSAL_INTERCEPTOR_CONFIG,
  MsalService,
  MsalGuard,
  MsalBroadcastService,
  MsalInterceptor,
  MsalGuardConfiguration,
  MsalInterceptorConfiguration,
} from '@azure/msal-angular';

import { routes } from './app.routes';
import { RUNTIME_CONFIG, RuntimeConfig, isAuthConfigured } from './runtime-config';

// A syntactically valid placeholder so PublicClientApplication can be constructed
// before real Entra values are supplied. No interactive flow runs until configured.
const PLACEHOLDER_CLIENT_ID = '00000000-0000-0000-0000-000000000000';
const PLACEHOLDER_TENANT_ID = 'common';

function msalInstanceFactory(config: RuntimeConfig): IPublicClientApplication {
  const clientId = config.clientId || PLACEHOLDER_CLIENT_ID;
  const tenantId = config.tenantId || PLACEHOLDER_TENANT_ID;
  return new PublicClientApplication({
    auth: {
      clientId,
      authority: `https://login.microsoftonline.com/${tenantId}`,
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
    },
    cache: {
      cacheLocation: BrowserCacheLocation.LocalStorage,
    },
  });
}

/**
 * Removes MSAL's session-storage interaction-status lock. MSAL sets this while a
 * loginRedirect()/acquireTokenRedirect() is in flight and clears it once that flow
 * completes; if a redirect is interrupted (tab closed mid-flow, a second tab racing the
 * same sign-in, `handleRedirectPromise()` itself throwing before it finishes) the lock can
 * be left stuck as "in progress" — which makes every future sign-in attempt fail
 * immediately with `BrowserAuthError('interaction_in_progress')` until a human clears
 * browser storage by hand. Matched by key substring (rather than one exact literal)
 * deliberately, since msal-browser has varied the precise key name across versions.
 * Exported for unit testing; safe to call even when nothing is stuck.
 */
export function clearStuckInteractionLock(): void {
  try {
    for (let i = window.sessionStorage.length - 1; i >= 0; i--) {
      const key = window.sessionStorage.key(i);
      if (key && key.includes('interaction.status')) {
        window.sessionStorage.removeItem(key);
      }
    }
  } catch {
    // sessionStorage can throw in locked-down/private-browsing contexts; this cleanup is
    // best-effort and must never break app bootstrap.
  }
}

function guardConfigFactory(config: RuntimeConfig): MsalGuardConfiguration {
  const scopes = config.apiScope ? [config.apiScope] : [];
  return {
    interactionType: InteractionType.Redirect,
    authRequest: { scopes },
  };
}

function interceptorConfigFactory(config: RuntimeConfig): MsalInterceptorConfiguration {
  const protectedResourceMap = new Map<string, string[] | null>();
  if (config.apiScope) {
    protectedResourceMap.set(config.apiBaseUrl, [config.apiScope]);
  }
  return {
    interactionType: InteractionType.Redirect,
    protectedResourceMap,
  };
}

export function buildAppConfig(rawConfig: RuntimeConfig): ApplicationConfig {
  const providers: Array<Provider | EnvironmentProviders> = [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    // Global session-expired interceptor runs before the DI-based MSAL interceptor and
    // marks {@link AuthSessionStore} on any 401 without swallowing the error.
    provideHttpClient(withInterceptors([sessionExpiredInterceptor]), withInterceptorsFromDi()),
    { provide: RUNTIME_CONFIG, useValue: rawConfig },
    { provide: MSAL_INSTANCE, useFactory: msalInstanceFactory, deps: [RUNTIME_CONFIG] },
    { provide: MSAL_GUARD_CONFIG, useFactory: guardConfigFactory, deps: [RUNTIME_CONFIG] },
    { provide: MSAL_INTERCEPTOR_CONFIG, useFactory: interceptorConfigFactory, deps: [RUNTIME_CONFIG] },
    MsalService,
    MsalGuard,
    MsalBroadcastService,
    // MSAL v4 requires `initialize()` be awaited before any other API call — otherwise
    // `loginRedirect()` from the branded /login screen throws
    // `BrowserAuthError: uninitialized_public_client_application`. `provideAppInitializer`
    // (Angular 20+) blocks bootstrap on the returned promise, so by the time the app
    // renders and a user clicks Sign in, MSAL is ready. `handleRedirectPromise()` here
    // also clears any pending redirect state (returning tokens from a completed sign-in)
    // before route guards run — matches the FreightDNA `AuthFacadeService.initializeAsync`
    // pattern.
    //
    // A redirect interrupted mid-flight (tab closed while returning from Microsoft, a
    // second tab racing the same sign-in, `handleRedirectPromise()` itself throwing before
    // it finishes consuming the response) can leave MSAL's session-storage interaction
    // lock stuck as "in progress". Once stuck, every future loginRedirect() throws
    // BrowserAuthError('interaction_in_progress') immediately.
    //
    // IMPORTANT: the lock must NOT be cleared before `handleRedirectPromise()` runs.
    // `handleRedirectPromise()` uses that same flag to recognise "this page load is the
    // return trip from a redirect I sent the user on" — clearing it first makes every
    // legitimate return trip look like a cold load with nothing to process, so the account
    // never gets extracted from the response and the user is bounced straight back to
    // /login on every single attempt (a strictly worse, 100%-reproducible regression this
    // app shipped and then fixed — see PR history). Only clear it in the catch branch,
    // i.e. after `initialize()`/`handleRedirectPromise()` has already failed on its own —
    // that recovers a stale lock left by an earlier interrupted attempt without touching
    // the normal, successful path.
    provideAppInitializer(async () => {
      const msal = inject(MsalService);
      try {
        await msal.instance.initialize();
        await msal.instance.handleRedirectPromise();
        const account = msal.instance.getAllAccounts()[0];
        if (account) {
          msal.instance.setActiveAccount(account);
        }
      } catch (err) {
        console.error('[Auth] MSAL initialize failed', err);
        clearStuckInteractionLock();
      }
    }),
  ];

  // Only attach bearer tokens to API calls when auth is actually configured.
  if (isAuthConfigured(rawConfig)) {
    providers.push({ provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true });
  }

  return { providers };
}
