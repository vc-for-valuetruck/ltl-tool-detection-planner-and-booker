import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DemoDirectorService } from './demo-director.service';

/**
 * Landing surface for `/ltl/demo/director`. Joshua opens one URL on UAT; `?autostart=1` starts the
 * walkthrough immediately, otherwise this shows a one-click "Start" card with the act outline.
 *
 * The launcher only kicks off the {@link DemoDirectorService}; the service then navigates the real
 * workspace and the {@link DemoDirectorOverlay} (mounted at the app root) takes over the screen, so
 * this component unmounting on the first navigation is expected and harmless.
 */
@Component({
  selector: 'app-demo-director-launcher',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './demo-director-launcher.html',
  styleUrls: ['./demo-director-launcher.css'],
})
export class DemoDirectorLauncher implements OnInit {
  protected readonly director = inject(DemoDirectorService);
  private readonly route = inject(ActivatedRoute);

  /** Distinct acts in script order, for the outline on the launch card. */
  protected readonly acts = computed(() => {
    const seen = new Set<string>();
    const out: string[] = [];
    for (const step of this.director.steps) {
      if (!seen.has(step.act)) {
        seen.add(step.act);
        out.push(step.act);
      }
    }
    return out;
  });

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    const speed = Number(params.get('speed'));
    if (!Number.isNaN(speed) && speed > 0) this.director.setSpeed(speed);
    if (params.get('autostart') === '1' || params.get('autostart') === 'true') {
      this.start();
    }
  }

  protected start(): void {
    this.director.start(true);
  }

  protected startPaused(): void {
    this.director.start(false);
  }
}
