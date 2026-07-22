import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthSessionStore } from './auth-session.store';
import { sessionExpiredInterceptor } from './session-expired.interceptor';

/**
 * Guards the contract in docs/BOUNDARIES.md → rule 4 (honest missing state) as it applies inside
 * the SPA: a 401 must set a shared signal *and* re-throw so component-level error handlers still
 * fire. See issue #164 for the source gap and dock.ts for the consumer.
 */
describe('sessionExpiredInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let store: AuthSessionStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionExpiredInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AuthSessionStore);
  });

  afterEach(() => httpMock.verify());

  it('marks the session expired on a 401 and re-throws the error', () => {
    let observedError: HttpErrorResponse | null = null;
    http.get('/api/anything').subscribe({
      next: () => fail('expected error branch to fire'),
      error: (err: HttpErrorResponse) => {
        observedError = err;
      },
    });

    const req = httpMock.expectOne('/api/anything');
    req.flush({ error: 'nope' }, { status: 401, statusText: 'Unauthorized' });

    expect(store.sessionExpired()).toBeTrue();
    expect(observedError).not.toBeNull();
    expect(observedError!.status).toBe(401);
  });

  it('leaves the session-expired signal false for non-401 errors', () => {
    http.get('/api/anything').subscribe({ next: () => {}, error: () => {} });
    const req = httpMock.expectOne('/api/anything');
    req.flush({ error: 'server bad' }, { status: 500, statusText: 'Server Error' });

    expect(store.sessionExpired()).toBeFalse();
  });

  it('clear() resets the signal so the app can render normal states after re-auth', () => {
    store.markExpired();
    expect(store.sessionExpired()).toBeTrue();
    store.clear();
    expect(store.sessionExpired()).toBeFalse();
  });
});
