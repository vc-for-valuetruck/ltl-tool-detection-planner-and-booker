import { CommonModule } from '@angular/common';
import {
  Component,
  DestroyRef,
  NgZone,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { DemoDirectorService } from './demo-director.service';

interface SpotRect {
  readonly top: number;
  readonly left: number;
  readonly width: number;
  readonly height: number;
}

/**
 * The Demo Director's visual layer: a spotlight overlay + narrating caption bar with playback
 * controls. Mounted onto the app-owned overlay outlet by the launcher (via `OverlayOutletService`),
 * so it renders once at the app root and persists across route changes; only rendered while the
 * director is {@link DemoDirectorService.active}. It owns zero business logic — it renders the
 * service's signals and forwards control clicks back to the service.
 *
 * The spotlight is a transparent window over the currently-targeted element (found by the
 * service's {@link DemoDirectorService.spotlight} selector) using a giant box-shadow to dim the
 * rest of the page. It re-measures on an rAF tick (outside Angular) so it tracks scroll, layout
 * shifts and live-data reflows without a change-detection storm.
 */
@Component({
  selector: 'app-demo-director-overlay',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './demo-director-overlay.html',
  styleUrls: ['./demo-director-overlay.css'],
})
export class DemoDirectorOverlay implements OnInit {
  protected readonly director = inject(DemoDirectorService);
  private readonly zone = inject(NgZone);
  private readonly destroyRef = inject(DestroyRef);

  /** Measured spotlight rectangle in viewport coordinates; null → dim-only (no hole). */
  protected readonly rect = signal<SpotRect | null>(null);
  private rafId: number | null = null;

  protected readonly showCaption = computed(() => this.director.active());
  protected readonly actLabel = computed(() => this.director.current()?.act ?? '');

  /**
   * Animated pointer target: the centre of the currently spotlighted rect, or null when there's
   * no target (pointer hides). Driven by the same measured {@link rect} the spotlight uses, so it
   * tracks scroll/layout reflows for free.
   */
  protected readonly pointer = computed(() => {
    const r = this.rect();
    if (!r) return null;
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  });

  /** Pulse the pointer while the current step is a click interaction (visual "tap" cue). */
  protected readonly pointerPulse = computed(
    () => this.director.current()?.action === 'click' && this.director.status() === 'running',
  );

  constructor() {
    // Re-measure immediately whenever the target selector changes (don't wait for the next tick).
    effect(() => {
      this.director.spotlight();
      this.measure();
    });
  }

  ngOnInit(): void {
    if (typeof requestAnimationFrame !== 'function') return;
    // Measure loop runs outside Angular so it doesn't trip change detection every frame; we only
    // write the `rect` signal (which schedules CD) when the measured box actually moves.
    this.zone.runOutsideAngular(() => {
      const tick = () => {
        this.measure();
        this.rafId = requestAnimationFrame(tick);
      };
      this.rafId = requestAnimationFrame(tick);
    });
    this.destroyRef.onDestroy(() => {
      if (this.rafId !== null && typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(this.rafId);
      }
    });
  }

  private measure(): void {
    if (!this.director.active()) {
      if (this.rect() !== null) this.zone.run(() => this.rect.set(null));
      return;
    }
    const selector = this.director.spotlight();
    const next = selector ? this.rectOf(selector) : null;
    if (!this.sameRect(this.rect(), next)) {
      this.zone.run(() => this.rect.set(next));
    }
  }

  private rectOf(selector: string): SpotRect | null {
    if (typeof document === 'undefined') return null;
    const el = document.querySelector(selector) as HTMLElement | null;
    if (!el) return null;
    const r = el.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) return null;
    const pad = 8;
    return {
      top: Math.max(0, r.top - pad),
      left: Math.max(0, r.left - pad),
      width: r.width + pad * 2,
      height: r.height + pad * 2,
    };
  }

  private sameRect(a: SpotRect | null, b: SpotRect | null): boolean {
    if (a === null || b === null) return a === b;
    return (
      Math.abs(a.top - b.top) < 1 &&
      Math.abs(a.left - b.left) < 1 &&
      Math.abs(a.width - b.width) < 1 &&
      Math.abs(a.height - b.height) < 1
    );
  }

  protected onSpeedChange(value: string): void {
    const n = Number(value);
    if (!Number.isNaN(n)) this.director.setSpeed(n);
  }

  protected onVoiceChange(value: string): void {
    if (value === 'narrator' || value === 'auFemale' || value === 'system') {
      this.director.setVoicePreset(value);
    }
  }
}
