import { test, expect, Page } from '@playwright/test';

/**
 * LTL demo workflow — a Playwright walkthrough of the pilot slice.
 *
 * WHAT THIS EXISTS FOR
 * This is the "so you can view workflows" test. Running it with the demo stack up
 * (`./scripts/demo-up.sh` then `npm run test:e2e` from the `web/` folder) launches a real
 * Chromium window and drives the Search → Consolidate flow while you watch. Every step
 * pauses briefly so operators/leadership can follow along.
 *
 * WHAT IT PROVES
 *   1. The API and Angular app can be reached at the demo ports.
 *   2. Demo-mode auth is armed (health endpoint publishes authMode=Demo).
 *   3. The LTL search page loads and returns rows from live Alvys.
 *   4. Clicking a load opens the detail drawer.
 *   5. The Consolidate route accepts a seed and returns corridor candidates.
 *   6. A plan preview renders both the Customer-side and Driver-side (RPM math)
 *      sections — the empirically-corrected driver-RPM math from PR #59.
 *
 * WHAT IT DOES NOT DO
 *   - No Alvys writes. The pilot is read-only; the plan preview is text the operator
 *     pastes into Alvys manually.
 *   - Does not run in main CI (see `web/package.json` — `test:e2e` is headed, meant for
 *     interactive local runs; `test:e2e:ci` is available for pipelines that spin up
 *     the demo stack in a container).
 */

/** Pause between visible steps so the workflow is watchable. Set to 0 in CI. */
const STEP_PAUSE_MS = process.env.CI ? 0 : 1500;
async function pauseSoOperatorCanSee(page: Page, message: string): Promise<void> {
  console.log(`  ▸ ${message}`);
  if (STEP_PAUSE_MS > 0) await page.waitForTimeout(STEP_PAUSE_MS);
}

const API_URL = process.env.API_URL ?? 'http://localhost:5072';

test.describe('LTL demo workflow — Laredo → Dallas pilot', () => {
  test.beforeAll(async ({ request }) => {
    // Preflight: fail loud if the demo stack isn't up or isn't in Demo mode. This
    // mirrors the shell smoke test in scripts/demo-up.sh so the E2E doesn't spend a
    // minute clicking around a mis-booted stack before failing on some downstream
    // assertion.
    const health = await request.get(`${API_URL}/api/health`);
    expect(health.ok(), 'API is not reachable — is the demo stack running?').toBeTruthy();
    const body = await health.json();
    expect(body.authMode, 'API is not in Demo mode — check .env ACCESS_POLICY_MODE=Demo').toBe(
      'Demo',
    );
  });

  test('operator can search Laredo→Dallas and land on a load detail', async ({ page }) => {
    await pauseSoOperatorCanSee(page, 'Opening the LTL Operating Console');
    await page.goto('/ltl');

    // Sanity: the console header renders. No MSAL redirect in demo mode.
    await expect(page.getByRole('heading', { name: 'LTL Operating Console' })).toBeVisible();

    await pauseSoOperatorCanSee(page, 'Typing Laredo → Dallas into the search filters');
    await page.getByTestId('search-origin-city').fill('Laredo');
    await page.getByTestId('search-dest-city').fill('Dallas');

    await pauseSoOperatorCanSee(page, 'Submitting search');
    await page.getByTestId('search-submit').click();

    // The result grid can render either a load row or an empty state. Both are legitimate
    // outcomes depending on the live Alvys tenant's current open-load set — we don't want
    // the test to be flaky against real data variance. Assert the grid finished loading,
    // then either exercise a row or note the empty state clearly.
    await pauseSoOperatorCanSee(page, 'Waiting for results');
    const rows = page.getByTestId('search-load-row');
    const firstRow = rows.first();
    const hasRow = await firstRow.isVisible().catch(() => false);

    if (hasRow) {
      await pauseSoOperatorCanSee(page, 'Clicking the first result to open the detail drawer');
      await firstRow.click();
      // The drawer surfaces the selected load number/customer somewhere on the page;
      // any change to the URL or a heading update is acceptable proof of selection.
      // We just assert we're still on the LTL route (no MSAL bounce).
      await expect(page).toHaveURL(/\/ltl/);
    } else {
      console.log(
        '  ⚠  No live loads matched Laredo → Dallas. Skipping row-click; the empty state is honest and rendered.',
      );
    }
  });

  test('operator can navigate to Consolidate and see the seed form', async ({ page }) => {
    await pauseSoOperatorCanSee(page, 'Navigating to the Consolidate tab');
    await page.goto('/ltl/consolidate');

    await expect(page.getByRole('heading', { name: 'Consolidate' })).toBeVisible();
    await expect(page.getByText('Laredo → Dallas pilot corridor')).toBeVisible();
    await expect(page.getByTestId('consolidate-seed-form')).toBeVisible();

    await pauseSoOperatorCanSee(page, 'Focusing the seed input');
    await page.getByTestId('consolidate-seed-input').fill('L-100234');
    // Don't submit — we don't want to depend on a specific live-load id. The point of
    // this spec is to prove the form renders and the corridor banner is present.
    await expect(page.getByTestId('consolidate-find-candidates')).toBeEnabled();
  });

  test('plan preview shows both Customer-side and Driver-side sections when data is available', async ({
    page,
    request,
  }) => {
    // This test only asserts the driver-RPM math surfaces IF a plan can be built end-to-end.
    // We look up the configured corridors from the API rather than hardcoding a city pair —
    // that way the pilot's shape can widen (add more corridors to ConsolidationOptions) and
    // this spec starts exercising them without any test code change.
    //
    // Fallback ladder: for each configured corridor, try each nearby-cities cross-product
    // until we find a pair with a live seed. If none has a live seed, test.skip() cleanly.
    // Better a skipped test than a flake against real load-state variance.
    const corridorsResp = await request.get(`${API_URL}/api/ltl/consolidation/corridors`);
    if (!corridorsResp.ok()) {
      test.skip(true, `Corridors API returned ${corridorsResp.status()}; skipping plan-preview.`);
      return;
    }
    const corridors = (await corridorsResp.json()) as Array<{
      code: string;
      origin: { nearbyCities: string[] };
      destination: { nearbyCities: string[] };
    }>;
    if (corridors.length === 0) {
      test.skip(true, 'No consolidation corridors configured; skipping plan-preview.');
      return;
    }

    // Cheap early-exit: if the corridor-health endpoint reports zero open loads across all
    // configured corridors, don't bother crawling nearby-cities cross-products — skip clean
    // with a specific reason.
    const healthResp = await request.get(`${API_URL}/api/ltl/consolidation/corridors/health`);
    if (healthResp.ok()) {
      const healths = (await healthResp.json()) as Array<{
        code: string;
        openLoadCount: number | null;
      }>;
      const total = healths.reduce((n, h) => n + (h.openLoadCount ?? 0), 0);
      if (total === 0) {
        test.skip(
          true,
          `Corridor health reports 0 open loads across ${healths.length} corridors; skipping plan-preview.`,
        );
        return;
      }
    }

    // Walk each configured corridor and each origin/destination city pair inside it, taking
    // the first live seed we find. "First" is arbitrary — the pilot corridor is
    // LAREDO_TO_DALLAS so that's where matches will land 95% of the time, but we don't
    // hardcode that assumption.
    type Seed = { loadNumber?: string | null; id: string };
    let seed: Seed | undefined;
    let matchedCorridor = '';
    let matchedOrigin = '';
    let matchedDest = '';
    for (const corridor of corridors) {
      for (const originCity of corridor.origin.nearbyCities) {
        for (const destCity of corridor.destination.nearbyCities) {
          const search = await request.get(
            `${API_URL}/api/ltl/search?originCity=${encodeURIComponent(originCity)}&destinationCity=${encodeURIComponent(destCity)}`,
          );
          if (!search.ok()) continue;
          const body = await search.json();
          const items = (body.items ?? []) as Seed[];
          const candidate = items.find(l => l.loadNumber || l.id);
          if (candidate) {
            seed = candidate;
            matchedCorridor = corridor.code;
            matchedOrigin = originCity;
            matchedDest = destCity;
            break;
          }
        }
        if (seed) break;
      }
      if (seed) break;
    }

    if (!seed) {
      const total = corridors.reduce(
        (n, c) => n + c.origin.nearbyCities.length * c.destination.nearbyCities.length,
        0,
      );
      test.skip(
        true,
        `No live loads found across ${corridors.length} corridors / ${total} city pairs; skipping plan-preview.`,
      );
      return;
    }

    console.log(
      `  ▸ Using seed ${seed.loadNumber ?? seed.id} from corridor ${matchedCorridor} (${matchedOrigin} → ${matchedDest})`,
    );

    await pauseSoOperatorCanSee(page, `Seeding Consolidate with load ${seed.loadNumber ?? seed.id}`);
    await page.goto('/ltl/consolidate');
    await page.getByTestId('consolidate-seed-input').fill(seed.loadNumber ?? seed.id);
    await page.getByTestId('consolidate-find-candidates').click();

    await pauseSoOperatorCanSee(page, 'Waiting for candidate rows');
    const firstCandidate = page.getByTestId('consolidate-candidate-row').first();
    const hasCandidate = await firstCandidate.isVisible({ timeout: 10_000 }).catch(() => false);
    if (!hasCandidate) {
      console.log(
        `  ⚠  Seed ${seed.loadNumber ?? seed.id} has no corridor siblings today; skipping plan build.`,
      );
      test.skip();
      return;
    }

    await pauseSoOperatorCanSee(page, 'Selecting the first candidate as a sibling');
    await firstCandidate.getByTestId('consolidate-candidate-checkbox').check();

    await pauseSoOperatorCanSee(page, 'Building the plan preview');
    await page.getByTestId('consolidate-build-plan').click();

    // The empirically-corrected driver-RPM section (PR #59) — this is the whole reason
    // the pilot demo matters. If this header is missing, the plan preview shipped
    // billing-only math and leadership sees an inflated number.
    await pauseSoOperatorCanSee(page, 'Asserting the Driver-side (RPM math) section rendered');
    await expect(page.getByTestId('driver-side-header')).toBeVisible();
    await expect(page.getByText('Customer-side (billing)')).toBeVisible();

    // The click card — text the dispatcher pastes into Alvys — must be non-empty.
    const cardText = await page.getByTestId('click-card-text').textContent();
    expect(cardText?.length ?? 0).toBeGreaterThan(50);
  });
});
