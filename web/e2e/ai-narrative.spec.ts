import { test, expect, Page } from '@playwright/test';

/**
 * AI narrative smoke (issue #151). Drives the `<ai-narrative>` card on the plan detail page with
 * every upstream dependency mocked, so it runs against a served SPA without a live Alvys tenant.
 *
 * It proves the two behaviours the acceptance criteria call out:
 *   1. With the narrative endpoint mocked to 200, the three review labels render.
 *   2. With the endpoint mocked to 404 `disabled`, the labels are absent AND the plan detail still
 *      renders normally — the narrative never blocks the page.
 *
 * Like the rest of the E2E suite it expects the SPA to be served at WEB_URL (default :4200); it
 * does NOT need the API/demo stack up because every backend call is intercepted here.
 */

const PLAN_URL = '/ltl/consolidate/plan/live?parent=L-1&siblings=L-2';

function loadSummary(id: string, loadNumber: string) {
  return {
    id,
    loadNumber,
    customerName: 'Masonite',
    status: 'Available',
    revenue: 3200,
    mileage: 540,
    loadedMiles: 540,
    weightLbs: 18000,
    origin: { city: 'Laredo', state: 'TX', label: 'Laredo, TX' },
    destination: { city: 'Dallas', state: 'TX', label: 'Dallas, TX' },
  };
}

const PLAN_PREVIEW = {
  previewId: 'preview-123',
  corridorCode: 'LAREDO_TO_DALLAS',
  parent: loadSummary('L-1', 'L-1'),
  siblings: [],
  clickCard: {},
  blockers: [],
};

/** Intercepts every backend call the plan detail makes so the page renders without a live stack. */
async function mockPlanDependencies(page: Page): Promise<void> {
  // Force the SPA's auth config empty so MSAL stays off. The demo-stack CI job injects placeholder
  // Azure GUIDs into the web container's runtime-config, which flips isAuthConfigured() true and
  // attaches the MsalInterceptor — that gates every /api call behind a token acquisition against a
  // non-existent tenant, so the page.route mocks below never fire and the plan detail hangs on its
  // loading spinner. Pinning auth off here keeps this fully-mocked spec deterministic in both the
  // empty-auth local demo stack and the GUID-injected CI stack.
  await page.route('**/runtime-config.json', (route) =>
    route.fulfill({ json: { tenantId: '', clientId: '', apiScope: '', apiBaseUrl: '/api' } }),
  );
  await page.route('**/api/ltl/loads/L-1', (route) =>
    route.fulfill({ json: loadSummary('L-1', 'L-1') }),
  );
  await page.route('**/api/ltl/loads/L-2', (route) =>
    route.fulfill({ json: loadSummary('L-2', 'L-2') }),
  );
  await page.route('**/api/ltl/consolidation/plan', (route) =>
    route.fulfill({ json: PLAN_PREVIEW }),
  );
  await page.route('**/api/ltl/lane-rate**', (route) =>
    route.fulfill({
      json: {
        originState: 'TX',
        destinationState: 'TX',
        sampleSize: 0,
        medianRpm: null,
        minRpm: null,
        maxRpm: null,
        basis: 'Recent tenant history, not a market rate.',
        generatedAt: '2026-07-22T00:00:00Z',
      },
    }),
  );
}

test.describe('AI narrative on plan detail (#151)', () => {
  test('renders the three review labels when the endpoint returns 200', async ({ page }) => {
    await mockPlanDependencies(page);
    await page.route('**/api/ai/consolidation/narrative**', (route) =>
      route.fulfill({
        status: 200,
        headers: { 'X-Ai-Source': 'llm', 'X-Ai-Cached': 'false' },
        json: {
          whyReview: 'Two Laredo→Dallas loads share a receiver and lane.',
          whatToVerify: 'Confirm pallet counts and the 45,000 lb trailer limit at the dock.',
          nextAction: 'Generate the click card once weights are verified.',
          citations: ['Load L-1', 'Load L-2'],
        },
      }),
    );

    await page.goto(PLAN_URL);

    const narrative = page.getByTestId('ai-narrative');
    await expect(narrative).toBeVisible();
    await expect(page.getByTestId('ai-narrative-why-label')).toHaveText('Why review');
    await expect(page.getByTestId('ai-narrative-verify-label')).toHaveText('What to verify');
    await expect(page.getByTestId('ai-narrative-next-label')).toHaveText('Next action');
    await expect(
      narrative.locator('[data-testid="ai-narrative-citations"] .ai-narrative-chip'),
    ).toHaveCount(2);
  });

  test('hides the narrative on 404 disabled while the plan detail still renders', async ({
    page,
  }) => {
    await mockPlanDependencies(page);
    await page.route('**/api/ai/consolidation/narrative**', (route) =>
      route.fulfill({ status: 404, json: { reason: 'disabled' } }),
    );

    await page.goto(PLAN_URL);

    // The plan detail renders normally — the trailer-plan card is present.
    await expect(page.getByRole('heading', { name: /Trailer plan/i })).toBeVisible();

    // The narrative collapsed to nothing — no card, no labels.
    await expect(page.getByTestId('ai-narrative')).toHaveCount(0);
    await expect(page.getByTestId('ai-narrative-skeleton')).toHaveCount(0);
    await expect(page.getByText('Why review')).toHaveCount(0);
  });
});
