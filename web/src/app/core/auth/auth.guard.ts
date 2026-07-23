import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { RUNTIME_CONFIG, isAuthConfigured } from '../../runtime-config';

/**
 * Route guard that mirrors FreightDNA's auth pattern: if Entra is configured and
 * no MSAL account exists, redirect to the branded `/login` screen. If Entra is
 * NOT configured, the guard also redirects to `/login` — the login page surfaces
 * the misconfiguration explicitly (Sign in disabled + status banner) instead of
 * letting protected routes load in a broken half-authed state.
 *
 * The login click itself calls `MsalService.loginRedirect(...)`, which is the
 * only place interactive sign-in is triggered — guards do not start login on
 * their own, matching FreightDNA's `AuthFacadeService.login()` contract.
 */
export const authGuard: CanActivateFn = (): true | UrlTree => {
  const router = inject(Router);
  const msal = inject(MsalService);
  const config = inject(RUNTIME_CONFIG);

  if (!isAuthConfigured(config)) {
    return router.parseUrl('/login');
  }

  const hasAccount = msal.instance.getAllAccounts().length > 0;
  return hasAccount ? true : router.parseUrl('/login');
};
