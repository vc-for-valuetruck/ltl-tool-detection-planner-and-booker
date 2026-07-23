import { Injectable, Type, signal } from '@angular/core';

/**
 * App-owned mount point for a single, full-screen overlay component that must persist across route
 * changes (rendered once at the app root — see {@link App}). Feature modules register their overlay
 * lazily via {@link mount}; the app root renders whatever is registered with `NgComponentOutlet`.
 *
 * This is the one-way seam that lets an isolated, lazy-loaded feature (today: the Demo Director)
 * project a persistent overlay onto the app shell WITHOUT the app root importing the feature. The
 * dependency direction is strictly feature → core (allowed). The app root never imports the
 * feature, which keeps the demo bundle out of the initial app chunk and lets the import-boundary
 * check (`web/scripts/check-demo-boundaries.mjs`) stay green.
 */
@Injectable({ providedIn: 'root' })
export class OverlayOutletService {
  private readonly _component = signal<Type<unknown> | null>(null);

  /** The currently-mounted overlay component, or null when nothing is mounted. */
  readonly component = this._component.asReadonly();

  /** Register the overlay component to render at the app root. Idempotent. */
  mount(component: Type<unknown>): void {
    this._component.set(component);
  }

  /**
   * Unmount the overlay. When a component is supplied, only clears if it is the one currently
   * mounted (so a late teardown can't stomp a freshly-mounted overlay).
   */
  clear(component?: Type<unknown>): void {
    if (!component || this._component() === component) {
      this._component.set(null);
    }
  }
}
