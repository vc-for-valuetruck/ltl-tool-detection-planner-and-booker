import { ApplicationConfig, EnvironmentProviders, Provider, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptorsFromDi, HTTP_INTERCEPTORS } from '@angular/common/http';
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
    provideHttpClient(withInterceptorsFromDi()),
    { provide: RUNTIME_CONFIG, useValue: rawConfig },
    { provide: MSAL_INSTANCE, useFactory: msalInstanceFactory, deps: [RUNTIME_CONFIG] },
    { provide: MSAL_GUARD_CONFIG, useFactory: guardConfigFactory, deps: [RUNTIME_CONFIG] },
    { provide: MSAL_INTERCEPTOR_CONFIG, useFactory: interceptorConfigFactory, deps: [RUNTIME_CONFIG] },
    MsalService,
    MsalGuard,
    MsalBroadcastService,
  ];

  // Only attach bearer tokens to API calls when auth is actually configured.
  if (isAuthConfigured(rawConfig)) {
    providers.push({ provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true });
  }

  return { providers };
}
