import { test, expect, Page } from '@playwright/test';

/**
 * Dock mode stakeholder demo — an automated, watchable walkthrough of the full dock-worker
 * combine flow, recorded to video.
 *
 * WHO THIS IS FOR
 * Jason (President of Logistics) wants to see that a dock worker can change (combine) loads
 * quickly, efficiently, and with a documented paper trail. This spec drives the real dock
 * flow end-to-end against the demo stack with human-readable pacing (DOCK_DEMO_SLOWMO), so the
 * resulting `.webm` reads as a narrated walkthrough rather than a machine test. It also doubles
 * as a regression test: every step carries an assertion.
 *
 * THE HAPPY PATH IT WALKS
 *   1. Open /ltl/dock and pick the yard the truck landed at.
 *   2. Tap the BOL-controlling load (the parent).
 *   3. Add one or two auto-suggested siblings.
 *   4. One-tap Combine — records an internal audit, builds the BOL packet + click card, notifies.
 *   5. The result screen shows the parent/child badges, the one-tap Undo window, and the
 *      honest notification-status chip.
 *   6. Print views: the combined BOL packet / dock manifest and the Alvys click card.
 *
 * ALVYS POSTURE
 * Strictly read-only. Combine records an internal audit (AlvysWriteback = NotPerformed); the
 * click card is text the dispatcher applies in Alvys manually. Nothing here writes to Alvys.
 *
 * LIVE-DATA HONESTY
 * Arrivals and sibling suggestions are live Alvys reads. When the tenant has no eligible parent
 * or no sibling suggestions at a yard right now, the spec test.skip()s with a specific reason
 * rather than fabricating data to force a green demo — consistent with the no-fabrication rule.
 */

/** Pause between visible steps so the walkthrough is watchable. Set to 0 in CI. */
const STEP_PAUSE_MS = process.env.CI ? 0 : 1200;
async function beat(page: Page, message: string): Promise<void> {
  console.log(`  ▸ ${message}`);
  if (STEP_PAUSE_MS > 0) await page.waitForTimeout(STEP_PAUSE_MS);
}

const API_URL = process.env.API_URL ?? 'http://localhost:5072';

/** Directory under `test-results/` where the demo PNGs land (video is handled by the project config). */
async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: `test-results/dock-demo/${name}.png`, fullPage: true });
}

/**
 * Force the print-only host class on <app-dock> so the print artifact becomes visible on screen
 * (dock.css hides `.print-doc` until `:host(.printing-bol)` / `:host(.printing-card)`), then take
 * a screenshot. We drive the host class directly rather than clicking Print because window.print()
 * opens a native dialog Playwright can't screenshot, and the component clears the mode on a 0-timeout.
 */
async function capturePrintView(
  page: Page,
  hostClass: string,
  name: string,
  assertSelector?: string,
  assertText?: string,
): Promise<void> {
  await page.evaluate((cls) => {
    document.querySelector('app-dock')?.classList.add(cls);
  }, hostClass);
  await page.waitForTimeout(200);
  // Assert the artifact is actually revealed (and carries its marking) BEFORE we clear the class.
  if (assertSelector && assertText) {
    await expect(page.locator(assertSelector).getByText(assertText).first()).toBeVisible();
  }
  await shot(page, name);
  await page.evaluate((cls) => {
    document.querySelector('app-dock')?.classList.remove(cls);
  }, hostClass);
}

test.describe('Dock mode — stakeholder demo walkthrough', () => {
  test.beforeAll(async ({ request }) => {
    // Preflight: fail loud if the demo stack isn't up or isn't in Demo mode.
    const health = await request.get(`${API_URL}/api/health`);
    expect(health.ok(), 'API is not reachable — is the demo stack running?').toBeTruthy();
    const body = await health.json();
    expect(body.authMode, 'API is not in Demo mode — check ACCESS_POLICY_MODE=Demo').toBe('Demo');
  });

  test('dock worker combines loads quickly, with a documented paper trail', async ({ page }) => {
    // -------- Step 1: open Dock mode + pick the yard --------
    await beat(page, 'Opening Dock mode');
    await page.goto('/ltl/dock');
    await expect(page.getByRole('heading', { name: 'Dock mode' })).toBeVisible();
    await shot(page, '01-dock-home');

    const yards = page.getByTestId('dock-warehouse-card');
    const firstYard = yards.first();
    if (!(await firstYard.isVisible({ timeout: 15_000 }).catch(() => false))) {
      test.skip(true, 'No yards configured in the consolidation corridor config; nothing to demo.');
      return;
    }
    await beat(page, 'Picking the yard the truck landed at');
    await firstYard.click();

    // -------- Step 2: pick the BOL-controlling (parent) load --------
    await expect(page.getByRole('heading', { name: /Tap the BOL-controlling load/ })).toBeVisible();
    await shot(page, '02-arrivals');

    // Only cards with a load number can control a BOL — the rest render disabled. Pick the first
    // enabled one. Playwright's :not([disabled]) filter keeps us off the disabled cards.
    const eligibleParent = page.locator('[data-testid="dock-arrival-card"]:not([disabled])').first();
    if (!(await eligibleParent.isVisible({ timeout: 15_000 }).catch(() => false))) {
      test.skip(
        true,
        'No BOL-controlling arrivals at this yard right now (live Alvys read); skipping combine walkthrough.',
      );
      return;
    }
    await beat(page, 'Tapping the BOL-controlling load (the parent)');
    await eligibleParent.click();

    // -------- Step 3: add auto-suggested siblings --------
    await expect(page.getByRole('heading', { name: /Add the loads riding with/ })).toBeVisible();
    await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => {});
    await shot(page, '03-siblings-suggested');

    const siblings = page.locator('[data-testid="dock-candidate-card"]:not([disabled])');
    const siblingCount = await siblings.count();
    if (siblingCount === 0) {
      test.skip(
        true,
        'No auto-suggested siblings for this parent right now (live Alvys read); skipping combine walkthrough.',
      );
      return;
    }

    // Tap one or two siblings — a realistic dock combine is 2-3 loads total.
    const toTap = Math.min(2, siblingCount);
    for (let i = 0; i < toTap; i++) {
      await beat(page, `Adding sibling ${i + 1} of ${toTap}`);
      await siblings.nth(i).click();
    }
    // The selected-bar (with the one-tap Combine button) appears once at least one sibling is added.
    const combineBtn = page.getByTestId('dock-combine');
    await expect(combineBtn).toBeVisible();
    await shot(page, '04-siblings-selected');

    // -------- Step 4: one-tap Combine --------
    await beat(page, 'One-tap Combine — records the audit, builds the docs, notifies the yard');
    await combineBtn.click();

    // -------- Step 5: result — badge, undo window, notification chip --------
    const result = page.getByTestId('dock-result');
    await expect(result).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('heading', { name: 'Combine recorded' })).toBeVisible();

    // One-tap Undo is offered for a few minutes post-combine (countdown chip).
    await expect(page.getByTestId('dock-undo')).toBeVisible();

    // The notification status chip is honest about what happened (Delivered/Pending/NotConfigured/
    // Disabled/Failed) — any of these is a legitimate outcome; we assert the chip container renders.
    await expect(page.getByTestId('dock-notify')).toBeVisible();

    await beat(page, 'Combine recorded — audit + Undo window + notification status visible');
    await shot(page, '05-result-combined');

    // -------- Step 6: the documented paper trail (print views) --------
    // The BOL packet carries the consistent parent/child marking — proof the paper trail is documented.
    await beat(page, 'Showing the combined BOL packet / dock manifest');
    await capturePrintView(page, 'printing-bol', '06-bol-packet', '.print-bol', 'PARENT · BOL controlling');

    await beat(page, 'Showing the Alvys click card (manual steps for the dispatcher)');
    await capturePrintView(page, 'printing-card', '07-click-card', '.print-clickcard', 'Alvys Click Card');

    console.log('  ✓ Dock combine walkthrough complete — video + screenshots captured.');
  });
});
