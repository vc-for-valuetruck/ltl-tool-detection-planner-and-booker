import { test, expect, Page } from '@playwright/test';
import { detectHomeFlavor } from './helpers/home-flavor';

/**
 * Screenshot tour of the LTL demo stack.
 *
 * The demo-workflow spec asserts behavior; this spec produces artifacts. Its whole job is
 * to walk every key screen and save a full-page screenshot with a stable name so the CI
 * artifact bundle lets operators / leadership review the pilot's shape without booting
 * the docker stack themselves.
 *
 * Design:
 *  - One spec per screen, so failures on later screens don't hide earlier screenshots.
 *  - Every screenshot is taken with `fullPage: true` and saved into
 *    `test-results/tour/<screen>.png`. The CI job uploads the whole `test-results` tree.
 *  - Preflight uses the same authMode=Demo check as demo-workflow.spec so a mis-booted
 *    stack fails fast, not mid-tour.
 */

const API_URL = process.env.API_URL ?? 'http://localhost:5072';

/** Directory under `test-results/` where tour PNGs land. */
const TOUR_DIR = 'tour';

async function shot(page: Page, name: string): Promise<void> {
  await page.waitForLoadState('networkidle', { timeout: 15_000 }).catch(() => {
    // Some Alvys-backed lists never fully settle to networkidle if a background refresh is
    // running. Fall through to the screenshot anyway; better a mid-fetch snapshot than none.
  });
  await page.screenshot({
    path: `test-results/${TOUR_DIR}/${name}.png`,
    fullPage: true,
  });
}

test.describe('LTL demo — screenshot tour', () => {
  test.beforeAll(async ({ request }) => {
    const health = await request.get(`${API_URL}/api/health`);
    expect(health.ok(), 'API is not reachable \u2014 is the demo stack running?').toBeTruthy();
    const body = await health.json();
    expect(body.authMode, 'API is not in Demo mode \u2014 check ACCESS_POLICY_MODE=Demo').toBe(
      'Demo',
    );
  });

  test('01 - LTL Operating Console home', async ({ page }) => {
    await page.goto('/ltl');
    await detectHomeFlavor(page);
    await shot(page, '01-ltl-home');
  });

  test('02 - Search filters + saved views', async ({ page }) => {
    await page.goto('/ltl');
    const flavor = await detectHomeFlavor(page);
    if (flavor === 'legacy') {
      await expect(page.getByTestId('search-filters')).toBeVisible();
      await page.getByTestId('search-origin-city').fill('Laredo');
      await page.getByTestId('search-dest-city').fill('Dallas');
    } else {
      await expect(page.getByText('Live consolidation queue')).toBeVisible();
    }
    await shot(page, '02-search-filters-filled');
    if (flavor === 'legacy') {
      await page.getByTestId('search-submit').click();
      // Give the grid ~2s to paint whichever state Alvys returns (rows, or the empty banner).
      await page.waitForTimeout(2000);
    }
    await shot(page, '03-search-results');
  });

  test('03 - Consolidate tab: corridor picker + seed form', async ({ page }) => {
    await page.goto('/ltl/consolidate');
    await expect(page.getByText(/Laredo.*Dallas pilot corridor/)).toBeVisible();
    await expect(page.getByTestId('corridor-picker')).toBeVisible();
    // Give the corridors + corridors/health calls a moment to resolve so the chip shows
    // the live count instead of the "\u2026" placeholder.
    await page.waitForTimeout(2500);
    await shot(page, '04-consolidate-corridor-picker');
    await page.getByTestId('consolidate-seed-input').fill('L-100234');
    await shot(page, '05-consolidate-seed-entered');
  });

  test('04 - Billing worklist (via sidebar)', async ({ page }) => {
    // Billing is now a wired screen reached from the Back Office group of the Alvys-style
    // sidebar (LtlShell). Navigate the way an operator would — click the sidebar link — and
    // capture whatever live state Alvys returns (worklist rows, or an honest empty/loading state).
    await page.goto('/ltl');
    await page.getByRole('link', { name: 'Billing' }).click();
    await expect(page).toHaveURL(/\/ltl\/billing$/);
    await page.waitForTimeout(1500);
    await shot(page, '06-billing-worklist');
  });

  test('05 - Exceptions (via sidebar)', async ({ page }) => {
    // Exceptions is likewise a wired Back Office screen in the sidebar (LtlShell).
    await page.goto('/ltl');
    await page.getByRole('link', { name: 'Exceptions' }).click();
    await expect(page).toHaveURL(/\/ltl\/exceptions$/);
    await page.waitForTimeout(1500);
    await shot(page, '07-exceptions');
  });

  test('06 - Swagger UI (API surface)', async ({ page }) => {
    // The Swagger UI is served by the API on the same origin as /api. In CI docker-compose
    // exposes it on http://localhost:5072/swagger. Use direct navigation via API_URL rather
    // than the SPA proxy so the screenshot captures the real surface.
    await page.goto(`${API_URL}/swagger`);
    await page.waitForTimeout(2000);
    await shot(page, '08-swagger-ui');
  });
});
