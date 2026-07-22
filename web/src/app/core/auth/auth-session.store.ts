import { Injectable, signal } from '@angular/core';

/**
 * Cross-cutting session-expired signal set by the {@link SessionExpiredInterceptor}
 * whenever any HTTP call returns 401. Components subscribe to it to render a re-auth
 * prompt instead of an indefinite spinner. Contract-preserving: nothing in the store
 * suppresses component-level error handling — interceptor still re-throws.
 */
@Injectable({ providedIn: 'root' })
export class AuthSessionStore {
  /** True once any authenticated HTTP call has returned 401 for the current SPA session. */
  private readonly _sessionExpired = signal(false);

  /** Read-only signal for template binding. */
  readonly sessionExpired = this._sessionExpired.asReadonly();

  /** Called by the interceptor on any 401. Idempotent. */
  markExpired(): void {
    if (!this._sessionExpired()) {
      this._sessionExpired.set(true);
    }
  }

  /**
   * Cleared explicitly by the sign-in flow once a fresh MSAL redirect has been requested,
   * so the app can render its normal loading states again after re-auth.
   */
  clear(): void {
    if (this._sessionExpired()) {
      this._sessionExpired.set(false);
    }
  }
}
