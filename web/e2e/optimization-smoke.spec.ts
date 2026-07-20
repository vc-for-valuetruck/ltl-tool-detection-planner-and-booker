import { test, expect } from '@playwright/test';
import { isVisible } from './helpers/home-flavor';

/**
 * Phase 2 optimization flags-on smoke.
 *
 * The CI e2e-demo job boots the compose stack with all three optimization flags ON
 * (LTL_TRAILER_FIT_ENABLED / LTL_SOLVER_ENABLED / LTL_AGENT_COMMANDS_ENABLED) plus the
 * trailer-fit sidecar container. This spec proves the environment is actually running with
 * optimization enabled — not just deployed — and that the consolidation UI renders the
 * solver-backed surfaces (or an honest fallback when live Alvys has no candidates today).
 *
 * WHAT IT PROVES
 *   1. /api/health/optimization reports all flags on, the trailer-fit sidecar reachable,
 *      and the in-memory solver self-test passing (deterministic — no live data needed).
 *   2. The consolidation queue renders either solver-ranked opportunities or the honest
 *      "pipeline is clean" empty state (never a crash / error state).
 *   3. When a live opportunity exists, its plan detail shows a trailer-fit verdict badge
 *      (a real verdict or the honest "dimensions unknown" panel) and a click-card the
 *      dispatcher pastes into Alvys.
 *
 * Data-dependent steps (2's row-click, 3) tolerate live Alvys variance the same way
 * demo-workflow.spec.ts does: skip cleanly rather than flake against real load-state.
 */

const API_URL = process.env.API_URL ?? 'http://localhost:5072';

test.describe('Phase 2 optimization — flags on', () => {
  test('optimization health reports flags on, sidecar reachable, solver passing', async ({
    request,
  }) => {
    const resp = await request.get(`${API_URL}/api/health/optimization`);
    expect(resp.ok(), 'optimization health endpoint is not reachable').toBeTruthy();
    const body = await resp.json();

    // Flags must be ON — the whole point of the UAT/E2E posture is optimization enabled.
    expect(body.flags.trailerFit, 'trailer-fit flag should be on').toBe(true);
    expect(body.flags.solver, 'solver flag should be on').toBe(true);
    expect(body.flags.agentCommands, 'agent-commands flag should be on').toBe(true);

    // The sidecar container is part of the compose stack; with the flag on the API must
    // reach it. The in-memory solver self-test is deterministic (synthetic 2-load solve).
    expect(body.trailerFit.reachable, 'trailer-fit sidecar should be reachable').toBe(true);
    expect(body.solver.passed, 'solver self-test should pass').toBe(true);

    // Aggregate status must be healthy when every enabled component passes.
    expect(body.status).toBe('ok');
  });

  test('consolidation queue renders solver-ranked opportunities or an honest fallback', async ({
    page,
  }) => {
    await page.goto('/ltl');

    // The queue resolves to exactly one of: ranked opportunities, the "pipeline is clean"
    // empty state, or (on Alvys trouble) a retryable error. The first two are legitimate;
    // an error state is a real failure worth surfacing.
    const opportunityList = page.locator('.opportunity-list');
    const emptyState = page.locator('.state-empty');
    const errorState = page.locator('.state-error');

    await expect(opportunityList.or(emptyState).or(errorState)).toBeVisible({ timeout: 30_000 });
    expect(await isVisible(errorState), 'consolidation queue is in an error state').toBe(false);

    if (!(await isVisible(opportunityList))) {
      console.log('  ⚠  No live consolidations right now — honest empty state rendered.');
      return;
    }

    // A ranked opportunity is present — open its plan detail and assert the trailer-fit
    // verdict surface + click-card. Either a real verdict panel or the honest
    // "dimensions unknown" panel is acceptable; both prove the fit surface rendered.
    await page.getByRole('button', { name: /Review plan/ }).first().click();

    const fitPanel = page.getByTestId('fit-panel');
    const fitPanelUnknown = page.getByTestId('fit-panel-unknown');
    await expect(fitPanel.or(fitPanelUnknown)).toBeVisible({ timeout: 20_000 });

    if (await isVisible(fitPanel)) {
      await expect(page.getByTestId('fit-verdict')).toBeVisible();
    }

    await expect(page.getByTestId('generate-click-card')).toBeVisible();
  });
});
