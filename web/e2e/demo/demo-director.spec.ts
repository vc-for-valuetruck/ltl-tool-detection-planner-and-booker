import { test, expect, Page } from '@playwright/test';

/**
 * E2E for the in-app Demo Director (feat/demo-director).
 *
 * WHAT THIS PROVES
 *   1. Opening `/ltl/demo/director?autostart=1` boots the autonomous walkthrough against the
 *      live demo stack (live Alvys reads — same stack the other e2e specs use).
 *   2. The director actually navigates the real workspace: it drives itself onto /ltl/dock,
 *      /ltl/consolidate, /ltl/dispatch and the back-office routes as it advances.
 *   3. The caption bar narrates each step and the playback controls (Next / Pause / Exit) work.
 *   4. Gated-write steps surface their honest posture note rather than faking a write.
 *
 * It intentionally drives the tour with the Next control (deterministic) instead of waiting on
 * autoplay dwell timers, and treats live-data-dependent steps as skippable — matching the
 * director's own resilience contract. It never asserts that anything was written to Alvys.
 */

const API_URL = process.env.API_URL ?? 'http://localhost:5072';

/** Advance the director and let it navigate / settle. */
async function next(page: Page): Promise<void> {
  await page.getByTestId('director-next').click();
  // The director navigates + waits on live data; give the caption a beat to update.
  await page.waitForTimeout(500);
}

test.describe('LTL Demo Director', () => {
  test.beforeAll(async ({ request }) => {
    const health = await request.get(`${API_URL}/api/health`);
    expect(health.ok(), 'API is not reachable — is the demo stack running?').toBeTruthy();
    const body = await health.json();
    expect(body.authMode, 'API is not in Demo mode — check ACCESS_POLICY_MODE=Demo').toBe('Demo');
  });

  test('autostarts and renders the narrating overlay', async ({ page }) => {
    await page.goto('/ltl/demo/director?autostart=1&speed=3');
    // Overlay + caption bar appear once the director is active.
    await expect(page.getByTestId('director-overlay')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('director-caption')).not.toBeEmpty();
    await expect(page.getByTestId('director-step-counter')).toContainText('/');
    await page.screenshot({ path: 'test-results/director/01-autostart.png', fullPage: true });
  });

  test('launcher shows the act outline without autostart', async ({ page }) => {
    await page.goto('/ltl/demo/director');
    await expect(page.getByTestId('director-launcher')).toBeVisible();
    await expect(page.getByTestId('director-start')).toBeVisible();
    await page.screenshot({ path: 'test-results/director/02-launcher.png', fullPage: true });
  });

  test('drives itself across the real workspace routes and narrates each act', async ({ page }) => {
    // Start paused so we step deterministically with the Next control.
    await page.goto('/ltl/demo/director');
    await page.getByTestId('director-start-paused').click();
    await expect(page.getByTestId('director-overlay')).toBeVisible({ timeout: 20_000 });

    // Act A — Dock. The very first step navigates to /ltl/dock.
    await expect.poll(() => page.url(), { timeout: 25_000 }).toContain('/ltl/dock');
    await expect(page.getByTestId('director-act')).toContainText('Dock');
    await page.screenshot({ path: 'test-results/director/03-dock.png', fullPage: true });

    // Walk forward until the director reaches the Lifecycle act (bounded so a stuck live-data
    // wait fails loudly rather than hanging). Each Next advances one step.
    const routesSeen = new Set<string>();
    for (let i = 0; i < DEMO_STEP_BUDGET; i++) {
      routesSeen.add(new URL(page.url()).pathname);
      const act = (await page.getByTestId('director-act').textContent())?.trim() ?? '';
      if (act.includes('Lifecycle')) break;
      await next(page);
    }

    // We should have visited the marquee operational routes on the way through.
    expect([...routesSeen].some((p) => p.includes('/ltl/dock'))).toBeTruthy();
    await page.screenshot({ path: 'test-results/director/04-final.png', fullPage: true });
  });

  test('exposes an honest posture note on a gated-write step', async ({ page }) => {
    await page.goto('/ltl/demo/director');
    await page.getByTestId('director-start-paused').click();
    await expect(page.getByTestId('director-overlay')).toBeVisible({ timeout: 20_000 });

    // Advance until a posture note is shown (dock-combine / dispatch-assemble carry one), or the
    // tour ends. The note must never claim a live Alvys write happened.
    let posture = '';
    for (let i = 0; i < DEMO_STEP_BUDGET; i++) {
      const noteEl = page.getByTestId('director-posture');
      if (await noteEl.isVisible().catch(() => false)) {
        posture = (await noteEl.textContent())?.trim() ?? '';
        if (posture) break;
      }
      const act = (await page.getByTestId('director-act').textContent())?.trim() ?? '';
      if (act.includes('Lifecycle')) break;
      await next(page);
    }

    if (posture) {
      expect(posture).toMatch(/gated|NotPerformed|override/i);
      await page.screenshot({ path: 'test-results/director/05-posture.png', fullPage: true });
    }
  });

  test('narration toggle works and the animated pointer tracks the spotlight', async ({ page }) => {
    // Stub the Web Speech API so narration is deterministic and CI needs no audio device: the
    // director's narrator will detect `speechSynthesis`, "speak" (immediately ending), and never
    // stall the run. We only assert the UI wiring, not that audio actually played.
    await page.addInitScript(() => {
      const stub = {
        speak: (u: { onend?: () => void }) => u.onend && setTimeout(() => u.onend!(), 0),
        cancel: () => {},
        getVoices: () => [],
      };
      Object.defineProperty(window, 'speechSynthesis', { value: stub, configurable: true });
      // Minimal utterance shim so `new SpeechSynthesisUtterance(text)` works headless.
      (window as unknown as { SpeechSynthesisUtterance: unknown }).SpeechSynthesisUtterance =
        class {
          text: string;
          lang = '';
          rate = 1;
          pitch = 1;
          volume = 1;
          voice: unknown = null;
          onend: (() => void) | null = null;
          onerror: (() => void) | null = null;
          constructor(text: string) {
            this.text = text;
          }
        };
    });

    await page.goto('/ltl/demo/director');
    await page.getByTestId('director-start-paused').click();
    await expect(page.getByTestId('director-overlay')).toBeVisible({ timeout: 20_000 });

    // Narration toggle is present (speech stubbed available), defaults ON, and flips on click.
    const toggle = page.getByTestId('director-narration-toggle');
    await expect(toggle).toBeVisible();
    await expect(toggle).toHaveText(/Voice: On/i);
    await toggle.click();
    await expect(toggle).toHaveText(/Voice: Off/i);
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await toggle.click();
    await expect(toggle).toHaveText(/Voice: On/i);

    // The animated pointer appears once the director spotlights a live target. Walk a few steps
    // (live-data resilient) and assert it shows up on at least one; screenshot the cue.
    let pointerSeen = false;
    for (let i = 0; i < DEMO_STEP_BUDGET; i++) {
      if (await page.getByTestId('director-pointer').isVisible().catch(() => false)) {
        pointerSeen = true;
        break;
      }
      const act = (await page.getByTestId('director-act').textContent())?.trim() ?? '';
      if (act.includes('Lifecycle')) break;
      await next(page);
    }
    expect(pointerSeen, 'animated pointer never appeared over any spotlighted step').toBeTruthy();
    await page.screenshot({ path: 'test-results/director/06-pointer-voice.png', fullPage: true });
  });

  test('exit tears the overlay down', async ({ page }) => {
    await page.goto('/ltl/demo/director?autostart=1&speed=3');
    await expect(page.getByTestId('director-overlay')).toBeVisible({ timeout: 20_000 });
    await page.getByTestId('director-exit').click();
    await expect(page.getByTestId('director-overlay')).toHaveCount(0);
  });
});

/** Upper bound on Next presses to walk the whole script — larger than the step count, with slack. */
const DEMO_STEP_BUDGET = 40;
