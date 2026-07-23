# Pre-demo checklist (LTL Tool, UAT)

**Run this ~10 minutes before the demo. Every step is copy-paste-ready. If any step is red, the demo is at risk — pick a fix or a fallback narrative below.**

## URLs (paste these in order)

| Purpose | URL |
|---|---|
| Presenter opens here | https://ltl-uat-web.azurewebsites.net/ltl |
| Health probe (anonymous, OK to open live) | https://ltl-uat-api.azurewebsites.net/api/health |
| Optimization health (backend subsystems) | https://ltl-uat-api.azurewebsites.net/api/health/optimization |

**Do not open** the trailer-fit sidecar URL on stage — it's proxied via the API and its public URL returns Azure's stark "IP Forbidden" page by design.

## 10-minute pre-flight

1. **Warm the landing pages.** In an already-authenticated browser, open in order:
   - `/ltl` — waits for the "Today's consolidations" sweep + Laredo Arrivals board.
   - `/ltl/loads` — the Search / saved-views grid (this is the runbook §A/§B target).
   - `/ltl/consolidate` — corridor picker + candidate queue.
   - `/ltl/dock` — the tablet-first dock combine flow.
   - `/ltl/billing` — billing worklist.
   - `/ltl/exceptions` — late / stuck / active-transit union sweep.
   Warming heats the Alvys token + corridor-health cache + first opportunities sweep, so the first live impression is instant.

2. **Verify health.** In a separate tab:
   ```
   curl -sS https://ltl-uat-api.azurewebsites.net/api/health
   curl -sS https://ltl-uat-api.azurewebsites.net/api/health/optimization
   ```
   Both should return `status: ok`. Optimization tile must show `trailerFit.reachable: true` and `solver.passed: true`.

3. **Confirm CI is green on `main`.** Latest `Deploy LTL Tool UAT` run should be ✅ (auto-triggered on merge).

4. **Have a Laredo seed load ready.** Per `LTL_DEMO_RUNBOOK.md:322`, keep a known-good Laredo-origin load number in your pocket for Consolidate / Dock walk-throughs.

5. **Close all incognito tabs.** MSAL cache lives per profile — the token you warmed lives in your main browser, not incognito.

## During the demo

### If a page shows "Couldn't reach Alvys" mid-demo

- Click **Retry** — the error handlers on every demo-path page clear the spinner and re-fetch.
- If that fails, narrate the honest-state posture: *"This is what the tool shows when Alvys is briefly unavailable — no fabricated numbers, just an honest error and a retry."*

### If a card or tile shows `—`

- Say: *"That field wasn't present on the Alvys record. The tool never coerces missing data to zero — it shows `—` and refuses to compute a score off missing evidence."*

### If a 500 renders

- The API now returns a small ProblemDetails body with `traceId` — read it aloud if asked, and keep going. Nothing was written to Alvys (read-only posture).

## The three moments that matter

1. **Consolidate corridor chip** — leads with real projected uplift $ (per PR pair 26-07-22).
2. **Billing worklist header** — leads with revenue-at-risk $ across today's filtered loads (per PR pair 26-07-22).
3. **Dock combine result** — click card + BOL packet + audit + "Not pushed to Alvys" label. This is the honest-state pinnacle.

## Do-not-demo list (avoid on stage)

- The two direct-fetch buttons on the `/` home page ("Sign in", "Check API health") — they work, but they're plumbing, not the pitch.
- The trailer-fit sidecar's public URL (403 by design).
- The **Search** tab of the runbook without first clicking **Loads** in the sidebar — the `/ltl` landing is a different screen (the opportunity queue), not the filter grid.
- The `AI Hub Analytics` menu (does not exist here — that was a FreightDNA-only feature; do not narrate it as an LTL feature).

## If something breaks and you need to bail out

Narrate: *"This is decision-support running against a read-only Alvys mirror in UAT. The demo posture is by design — nothing here mutates production data. Let me switch to the pre-recorded stakeholder demo video."*

That path is documented in `LTL_DEMO_RUNBOOK.md` §7 and the `dock-demo` Playwright artifact (bytes preserved in the CI `e2e-demo` job).
