import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthSessionStore } from './auth-session.store';

/**
 * Global HTTP interceptor that surfaces 401 responses as a session-expired signal on the
 * {@link AuthSessionStore}. The error is **re-thrown unchanged** so component-level
 * `catchError`/`error:` handlers keep firing — this is additive, contract-preserving.
 *
 * The dock loader (and any other long-running SPA surface) subscribes to
 * `AuthSessionStore.sessionExpired()` and renders a "Session expired — sign in again"
 * panel instead of leaving the user staring at an indefinite "Working…" spinner.
 */
export const sessionExpiredInterceptor: HttpInterceptorFn = (req, next) => {
  const store = inject(AuthSessionStore);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401) {
        store.markExpired();
      }
      return throwError(() => err);
    }),
  );
};
