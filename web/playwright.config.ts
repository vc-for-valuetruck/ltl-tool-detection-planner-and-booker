import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright config for the LTL demo E2E suite.
 *
 * By default targets a running docker-compose demo stack on localhost:4200 (SPA) and
 * localhost:5072 (API). Override with WEB_URL / API_URL env vars for other bases (e.g.
 * pointing at a UAT deployment later).
 *
 * Design choices:
 *  - Headed by default in `test:e2e` so the operator can WATCH the workflow. CI runs
 *    `test:e2e:ci` (headless) via a separate npm script.
 *  - No webServer block — we don't want Playwright starting/killing docker-compose.
 *    The test suite expects the demo stack to be already up (via `scripts/demo-up.sh`).
 *    That keeps the "watch the demo" experience aligned with what leadership sees.
 *  - Traces + videos on retry only, to keep local test runs fast.
 *  - Chromium only for the primary suite; adding Firefox/WebKit later is a one-line change.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false, // single serial run — the tests share the demo stack's state
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.WEB_URL ?? 'http://localhost:4200',
    // Keep artifacts on every run — the E2E doubles as a walking-tour of the demo stack.
    // CI uploads playwright-report unconditionally, so operators can download screenshots
    // + traces from any successful run and eyeball the workflow without booting docker.
    trace: 'on',
    video: 'on',
    screenshot: 'on',
    actionTimeout: 10_000,
    navigationTimeout: 20_000,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
