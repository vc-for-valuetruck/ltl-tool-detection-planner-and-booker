import { clearStuckInteractionLock } from './app.config';

/**
 * Guards the "constant auth reload / signed out" fix: a redirect interrupted mid-flight can
 * leave MSAL's session-storage interaction-status lock stuck as "in progress", which makes
 * every future loginRedirect() throw BrowserAuthError('interaction_in_progress') until the
 * lock is cleared. clearStuckInteractionLock() is called defensively on every app bootstrap
 * (see app.config.ts) to guarantee a stuck lock never permanently blocks sign-in.
 */
describe('clearStuckInteractionLock', () => {
  afterEach(() => sessionStorage.clear());

  it('removes any session-storage key carrying the interaction-status lock', () => {
    sessionStorage.setItem('msal.interaction.status', 'in_progress');
    sessionStorage.setItem('msal.some-client-id.interaction.status', 'in_progress');
    sessionStorage.setItem('msal.request.state', 'keep-me');

    clearStuckInteractionLock();

    expect(sessionStorage.getItem('msal.interaction.status')).toBeNull();
    expect(sessionStorage.getItem('msal.some-client-id.interaction.status')).toBeNull();
    expect(sessionStorage.getItem('msal.request.state')).toBe('keep-me');
  });

  it('is a no-op when nothing is stuck', () => {
    sessionStorage.setItem('msal.request.state', 'keep-me');

    expect(() => clearStuckInteractionLock()).not.toThrow();

    expect(sessionStorage.getItem('msal.request.state')).toBe('keep-me');
  });
});
