# UAT UI Parity — LTL Tool status

**Anchor date:** 2026-07-22
**Workbook:** `UAT-UI-Parity-Workbook-FreightDNA-LTL-Tool-Yard.xlsx` (LTL Tool Parity + Fix Order Roadmap sheets)
**Purpose:** Point-in-time reconciliation between the UAT parity workbook and the state of `main` in
this repo. Any future audit should update this doc alongside the workbook so the two do not drift.

## Executive summary

Of the 20 rows on the **LTL Tool Parity** sheet plus the 13 LTL rows on the **Fix Order Roadmap** sheet,
**every P0 and P1 item is resolved on `main`.** One P3 polish item remains — dock-mode 401 re-auth
handling — tracked as [#164](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/issues/164).

## LTL Tool Parity — row-by-row status (2026-07-22)

| # | Page / Module | Workbook status | Real state | Evidence |
|---|---|---|---|---|
| 1 | Search tab | Planned/P1 | **OK** | LTL Operating Console shipped (PR #108); global load search (PR #144). Issue [#79](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/issues/79) closed 2026-07-20. |
| 2 | Match tab | OK | **OK** | Phase 2 depth (PR #134), Phase 3.5 accessorial (PR #135), Phase 4 billing leakage + RPM + lane templates (PRs #136, #140). |
| 3 | Assign tab | Planned/P1 | **OK** | Phase 3 assign hardening (PR #139) — typed blockers/warnings, batch validate, `/ltl/assignments` history. |
| 4 | Bill tab | Planned/P1 | **OK** | `BillingReadinessService` full badge set; revenue-protection signals (PR #126); sandbox-gated doc upload (PR #141). |
| 5 | Exceptions tab | Planned/P1 | **OK** | Predicted-late (PR #112), actual-late (PR #122), stuck-at-stop (PR #123), empty-sweep default (PR #124), active-transit union (PR #125). |
| 6 | Tenders tab | Planned/P1 | **OK** | Tenders scaffold (PR #29), EDI enrichment (PR #111), policy source badge (PR #115). Writes internal-only. |
| 7 | Dock overweight / uplift / drill-through | Missing/P1 | **OK** | Dock combine flow (PR #142), Alvys-style sidebar + planner (PR #143), red weight badge + uplift chip + drill-through (PR #144). Issues [#76](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/issues/76), [#77](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/issues/77), [#78](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/issues/78) closed 2026-07-20. |
| 8 | Dock email channel | Missing/P2 | **OK** | Real Graph `sendMail` provider for dock notifications (PR #148) — honest states, outbox semantics. |
| 9 | Dock 401 handling | Polish/P3 | **OPEN → tracked** | No global Angular `HttpInterceptor`; `dock.html` renders `Working…` indefinitely on 401. Filed as [#164](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/issues/164). |
| 10 | Laredo Arrivals Board | OK | **OK** | PRs #117–#121, #145 (yard-artifact intake boundary). |
| 11 | Saved Views | OK | **OK** | EF-backed; SQL Server 2022 migration verified in CI. |
| 12 | Corridor picker | OK | **OK** | Config-driven; live open-load counts; scheduled canary. |
| 13 | BOL reader slice | Missing/P2 | **OK** | PR #146 merged 2026-07-22 — suggest-only, fail-closed on low confidence, no auto-write. |
| 14 | Global — CI main build | Broken/P0 | **OK** | `main` CI green (multiple runs 2026-07-22 incl. scheduled canary). |
| 15 | Global — UAT vs standalone drift | Blocked-ext/P0 | **OK** | Per-merge UAT deploy flipped in PR #133; deploys green 2026-07-22. |
| 16 | Global — Writeback architecture | Broken/P1 | **OK** | PR #141 merged with existing sandbox-only posture: `Mode=Sandbox`, non-production `SandboxBaseUrl` guard, per-op sign-off empty in `docs/ltl-tool.md` ⇒ production unreachable. Webhooks HMAC-verified and fail closed. |
| 17 | Global — Cold-start | Missing/P2 | **OK** | Issue [#80](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/issues/80) closed 2026-07-20. |
| 18 | Global — Demo mode | OK | **OK** | Docker-compose demo stack + Playwright + `authMode=Demo`. |
| 19 | Global — Field-shape record | OK | **OK** | Empirical MCP shape persistence maintained. |
| 20 | Global — Driver economics | OK | **OK** | Combined RPM derivation + LTL_TIER/LTL_ALLOW customer-note fallback intact. |

## Fix Order Roadmap — LTL rows (2026-07-22)

Cross-project roadmap ranks 5, 6, 18–24, 34, 35, 36, 42 belong to LTL Tool. Twelve are now **OK**; only
**rank 42** (dock 401 polish) remains, tracked as issue #164 above.

| Rank | Row | Status |
|---|---|---|
| 5  | Global — CI main build | ✅ OK |
| 6  | Global — UAT vs standalone drift | ✅ OK |
| 18 | Search tab | ✅ OK |
| 19 | Assign tab | ✅ OK |
| 20 | Bill tab | ✅ OK |
| 21 | Exceptions tab | ✅ OK |
| 22 | Tenders tab | ✅ OK |
| 23 | Dock overweight / uplift / drill-through | ✅ OK |
| 24 | Writeback architecture (PR #141) | ✅ OK |
| 34 | Dock email channel | ✅ OK |
| 35 | BOL reader slice | ✅ OK |
| 36 | Cold-start | ✅ OK |
| 42 | Dock 401 handling | 🟡 Polish/P3 — issue #164 |

## Guardrails carried into every remaining row

- **Read-only over ALVYS.** LTL never ingests operational data from any non-Alvys source; writes go
  through the internal API (or sandbox-gated Public API for docs/webhooks), never open-web.
- **Sandbox posture until sign-off.** `docs/ltl-tool.md` sign-off rows remain empty; production
  execution is architecturally unreachable until a two-reviewer sign-off row is filled.
- **Honest states.** Missing values render as `—` / `missing`; unavailable Alvys endpoints render
  `Unavailable` rather than fabricated scores (per Match tab contract test).
- **Schema drift fails visibly.** Any new `DbSet` must land in EF migrations *and*
  `SchemaReconciliation.cs` (mirrors the FreightDNA PR #217/#260/#261/#287 pattern).

## How to update this doc

1. When a workbook row changes status on `main`, edit both the workbook and the matching table row
   here in the same PR.
2. If a new gap is found, file a tracking issue, add the row here with the issue link, and add the
   same row to the workbook.
3. If a P0/P1 regression is introduced, flip the row in both places back to Broken/Missing before
   merging — do not treat green deploy as full parity.
