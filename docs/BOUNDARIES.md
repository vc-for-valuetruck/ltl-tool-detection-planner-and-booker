# App Boundaries — LTL Tool ↔ Yard App ↔ Alvys

**Anchor date:** 2026-07-22
**Peer docs:** [`value-truck-yard/docs/BOUNDARIES.md`](https://github.com/vc-for-valuetruck/value-truck-yard/blob/main/docs/BOUNDARIES.md)
**Related:** ROADMAP.md §Phase 3.75 (line 486), PR [#145](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/pull/145), `docs/UAT_PARITY_STATUS.md`.

This document is the single source of truth for **who owns what data** and **how the two apps talk
to each other**. Any PR that violates the rules below must be rejected. Any new consumer of
Yard-owned data must add a row to the contract table below.

## The three products

| Product | User | Auth role | Repo | Owns |
|---|---|---|---|---|
| **LTL Tool** | Dispatcher / broker / dock-worker consolidation | LTL-* | `ltl-tool-detection-planner-and-booker` | Load consolidation decisions, match scoring, billing readiness, dispatch paperwork (BOL packet, Alvys click card). |
| **Yard App** | Security guard / gate ops | VT-Yard-Supervisor | `value-truck-yard` | Physical yard workflow: check-in/out, tractor/trailer/seal photo gates, CTPAT inspection, release review, gate audit, visitor scheduling. |
| **Alvys** | System of record for freight | Alvys tenant | (external) | Loads, drivers, trucks, trailers, tenders, trips, invoices. |

**Different users. Different auth. Different Azure resource groups. Different retention rules.**
That is why they are separate products, and why they must not share a database.

## The five hard rules

1. **No shared database.** Neither product may read or write the other's tables. All cross-app
   traffic goes through documented HTTP contracts.
2. **Alvys is the only source of truth for operational data.** Neither product may ingest load,
   driver, truck, trailer, tender, invoice, dispatch, visibility, or accessorial context from any
   non-Alvys source. Yard is the only source of truth for *presence, security release, and yard
   artifacts* — those flow to LTL through the contract, and to Alvys as document uploads via the
   PR [#141](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/pull/141)
   writeback slice.
3. **Reads only, until sign-off.** Neither product writes to Alvys in production. LTL's Alvys write
   boundary stays sandbox-gated and per-op sign-off in `docs/ltl-tool.md` remains empty. Yard's
   artifacts reach Alvys only through the gated LTL writeback slice or Jordan's manual intake.
4. **Honest missing state.** When the other product's API is unavailable, render "—" / "Unavailable"
   and, for validation blockers, degrade to a warning that the dispatcher can override with a stated
   reason. Never fabricate a value. Same rule as `MissingDataFlag` inside LTL.
5. **Boundaries fail closed.** A missing Yard signal is not a green light. Assignment validation
   treats "presence unknown" as a *warning*, not a pass; "security hold on release" is a *blocker*.

## Cross-app data contracts

These are the only sanctioned channels. Every new consumer of Yard data must extend this table.

### Yard → LTL

| Contract | Direction | Owner | Purpose | Failure mode |
|---|---|---|---|---|
| `GET /api/yard/presence?tractor=…&trailer=…&driverId=…` | Yard exposes, LTL consumes | Yard App | Returns `{ atYard, releasedAt, photoGates: {tractor, trailer, seal}, driverPresent, lastEventAt }` for the equipment/driver named. LTL uses it in `AssignmentValidationService` and `/ltl/dock` combine flow. | 5xx / timeout → LTL renders `Presence: Unavailable` and downgrades any blocker to a warning. `404` → `Presence: NotOnRecord` (render "—", warning). |
| Webhook `TruckArrived` / `LoadReleased` | Yard emits, LTL receives | Yard App | Real-time push so the LTL dock screen and Consolidation queue update without polling. HMAC-signed like the existing Alvys webhook receiver (PR [#141](https://github.com/vc-for-valuetruck/ltl-tool-detection-planner-and-booker/pull/141)). | Verification failure → 4xx, fail closed. Missing secret → 503 (existing pattern). |

### Yard → Alvys

| Contract | Direction | Owner | Purpose | Failure mode |
|---|---|---|---|---|
| Alvys document upload (via LTL PR #141 gated writeback OR Jordan's manual intake) | Yard produces, Alvys stores | Yard captures; LTL/PR#141 or manual intake writes | Yard photos + CTPAT inspection PDFs attach to the Alvys load as evidence. LTL reads them back through normal Alvys reads. | Sandbox-gated in LTL writeback; empty sign-off row ⇒ production unreachable. |

### LTL → Yard

**Currently: none.** LTL does not write to Yard, does not query Yard for anything except presence.
If a future need arises (e.g., "notify yard that a load was combined at the dock"), it will be
added here as a new row with an HMAC-signed webhook contract, never a direct DB read.

## Naming clarifications (this trips people up)

| Term | In LTL | In Yard |
|---|---|---|
| **Dock** | `/ltl/dock` — the load-combine screen (dispatch decision). | Dock Inspection — CTPAT checklist + photo evidence station (compliance capture). |
| **Yard** | Static config: consolidation corridor / warehouse code. | The whole app. |
| **Release** | Not used. | Security release for a truck to leave with the load. |

If you find yourself building something whose name collides, prefix it with the product
(`LtlDockCombine`, `YardDockInspection`) rather than making the shared word mean two things.

## Why not a shared DB?

Considered and rejected because:

- **Schema drift risk.** `SchemaReconciliation.cs` in each app is the guardrail against silent
  drift. It cannot enforce anything across a database owned by another app.
- **Business-rule bypass.** A shared DB tempts each app to write to the other's tables directly,
  bypassing the other's validation.
- **Retention / compliance mismatch.** Yard photo retention (6mo cache) ≠ LTL BOL retention (7y).
  Mixing them in one DB muddles the compliance surface.
- **Read-only-Alvys guardrail becomes harder to enforce** — Yard writes could quietly bleed into
  LTL's load model without going through Alvys.
- **Refactor coupling.** Yard cannot refactor its schema without breaking LTL, and vice versa.

Same latency and safety guarantees are achievable through an API + webhooks; the coupling costs
are not.

## How to update this doc

1. New cross-app contract → add a row to the tables above **and** to the peer doc in the Yard repo.
2. New rule discovered by a real bug → add it as a numbered rule and link the incident.
3. Row of the workbook that references a boundary → cite this file under `Source`.
