import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { RUNTIME_CONFIG, isAuthConfigured } from '../../runtime-config';

/**
 * Route guard that mirrors FreightDNA's auth pattern: if Entra is configured and
 * no MSAL account exists, redirect to the branded `/login` screen. If Entra is
 * NOT configured (empty runtime-config, or the placeholder GUIDs the E2E demo
 * stack injects), the guard lets traffic through — the app shell already
 * surfaces a "Demo mode" indicator, and there is no real Entra tenant behind a
 * sign-in redirect in that environment. Only real, configured Entra deployments
 * enforce the /login gate.
 *
 * The login click itself calls `MsalService.loginRedirect(...)`, which is the
 * only place interactive sign-in is triggered — guards do not start login on
 * their own, matching FreightDNA's `AuthFacadeService.login()` contract.
 */
export const authGuard: CanActivateFn = (): true | UrlTree => {
  const router = inject(Router);
  const msal = inject(MsalService);
  const config = inject(RUNTIME_CONFIG);

  // Demo / unconfigured environments (local ng serve without RUNTIME_*, the
  // docker-compose demo stack, Playwright E2E) render normally without a login
  // gate — there's nothing to authenticate against.
  if (!isAuthConfigured(config)) {
    return true;
  }

  const hasAccount = msal.instance.getAllAccounts().length > 0;
  return hasAccount ? true : router.parseUrl('/login');
};
