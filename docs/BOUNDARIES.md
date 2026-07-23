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
| `GET /api/yard/presence?tractor=…&trailer=…&driverId=…` | Yard exposes, LTL consumes | Yard App | Returns `{ onRecord, atYard, releasedAt, driverPresent, securityHold, lastEventAt, gates: {tractor, trailer, seal} }` for the equipment/driver named. LTL consumes it in `AssignmentValidationService` (via `YardPresenceClient`) and the `/ltl/dock` combine Review step (`GET /api/ltl/dock/presence`). | 5xx / timeout / unconfigured → `YardPresenceClient` returns `null`; LTL renders `Presence: Unavailable` and downgrades any blocker to a warning (never a fabricated pass). `404` → `NotOnRecord` sentinel (`onRecord=false`, render "—", warning). |
| Webhook events `TruckArrived` / `LoadReleased` / `LtlDraftCreated` → `POST /api/yard/webhooks/receiver` | Yard emits, LTL receives | Yard App | Real-time push so the LTL dock screen and Consolidation queue update without polling. `TruckArrived` invalidates the presence cache; `LoadReleased` invalidates + fans out over SignalR; `LtlDraftCreated` persists a yard-originated opportunity, surfaces it on the `/ltl/dock` opportunities card, and fans out over SignalR. Read-only admin listing at `/admin/yard/webhooks`. | Verification failure → 401, fail closed. Missing signing secret → 503. Receiver gated behind `Yard:Webhooks:Enabled` (default off) → 404 when dormant. |
| `POST /api/v1/yard-events` (+ `GET /api/v1/yard-events/schedule-input[/…]`, `/events[/…]`, `POST …/replay`) | Yard emits, LTL ingests + exposes | LTL Tool (owns the contract + store) | Durable Yard→LTL scheduler feed. Yard POSTs every freight-affecting event (arrival, check-in, load/unload complete, trailer/dock assignment, freight weight/dimensions, appointment, exception, hold/release/cancel, split/consolidation) as a versioned envelope; LTL persists an immutable inbox row and derives a normalized scheduler projection (eligible on first freight event; readiness `Provisional`→`Ready` after dock completion + security clearance). Idempotent on `eventId` + source record identity (dupe → 200, no second projection). Administrative-only events audited but never projected. The scheduler consumer reads projections via `schedule-input`. See [`docs/YARD_LTL_INGESTION.md`](YARD_LTL_INGESTION.md). | Contract violation (missing field, wrong `schemaVersion`, wrong `sourceSystem`, non-object payload) → 400 with error list, nothing persisted. Duplicate → 200, idempotent no-op. Auth failure → 401 (Yard service-to-service token required; **no shared DB read, no Alvys relay**). |

#### Yard webhook signing + event payloads

The receiver (`POST /api/yard/webhooks/receiver`) is anonymous but signed — the Yard is a machine
caller with no email identity. Every delivery carries:

| Header | Purpose |
|---|---|
| `X-Yard-Signature: t={unix},v1={hex}` | HMAC-SHA256 over `"{t}.{rawBody}"` with the shared `Yard:Webhooks:Secret`. Constant-time compare; 5-minute timestamp tolerance (`Yard:Webhooks:ToleranceSeconds`, default 300). |
| `X-Yard-Event` | Event type (`TruckArrived` / `LoadReleased` / `LtlDraftCreated`); falls back to the body's `eventType`. |
| `X-Yard-Event-Id` | Idempotency key. Duplicate ids are acked 200 without reprocessing. |
| `X-Yard-Timestamp` | Echo of the signed `t` for auditing. |

Event bodies (all fields nullable — LTL renders "—", never fabricated):

- **`TruckArrived`** / **`LoadReleased`**: `{ eventType, yardCode?, tractorId?, trailerId?, driverId? }`.
- **`LtlDraftCreated`**: `{ eventType, yardCode?, draftId, parentLoadId?, siblingLoadIds[], freight[], createdByStation?, scannedAt? }` — a yard-originated LTL consolidation suggestion. LTL persists it (`YardLtlOpportunities`), never as an Alvys write; the dock acts on it inside its own Alvys-backed combine flow. `freight[]` lines carry `{ loadId?, pallets?, pieces?, weightLbs?, dims?, osd? }`.

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

## Alvys read contract (LTL Tool)

LTL reads from Alvys only through the endpoints below. Every read is unfiltered on raw ALVYS
statuses; server-side status-bucket mapping happens *after* the read (mirrors the FreightDNA
PR #226 discipline). Empirical field shapes captured 2026-07-22 from the live Alvys tenant
(`alvys_f4df3d…`) via the MCP connector; use these when writing new normalizers so the LTL
models match observed tenant shape, not assumed shape (PR #56 field-shape record pattern).

| Endpoint (Public API + MCP tool) | Consumer inside LTL | Key fields LTL depends on (verified live) |
|---|---|---|
| `loads_search` / `loads_get_by_id` | `LtlLoadService`, Consolidation, Billing, Dock arrivals | `Id`, `LoadNumber`, `OrderNumber`, `CustomerId`, `CustomerName`, `Status` (raw — map before filtering), `Stops[].{StopType, Status, Address, Coordinates, ScheduleType, StopWindow, AppointmentDate, ArrivedAt, DepartedAt, References[]}`, `CustomerRate.{Amount,Currency}`, `CustomerMileage.Distance.{Value,UnitOfMeasure}`, `Weight.{Value,UnitOfMeasure}`, `ScheduledPickupAt`, `ScheduledDeliveryAt`, `Fleet.{Id,Name}`, `RequiredEquipment[]`. |
| `trips_search` / `trips_get_by_id` | `MatchScoringService` (Combined-RPM), Consolidation, Assignment history | `TripNumber`, `LoadNumber`, `Status` (raw), `Stops[]` (same shape as loads), `LoadedMileage.Distance.Value` + `EmptyMileage.Distance.Value`, `TripValue.{Amount,Currency}`, `Truck.{Id,Fleet}`, `Trailer.{Id,EquipmentType,EquipmentLength}`, `Driver1.{Id,ContractorType,Fleet,RatesV2}`. |
| `visibility_inbound_history` | Exceptions tab (predicted-late, stuck-at-stop, active-transit union sweep — PRs #112/#122/#123/#125) | Array of `{Id, TripNumber, EventType, SharedAt, Destination, Address, Coordinates, Status}`. Never assume ordering — sort by `SharedAt` client-side. |
| `tenders_search` / `tenders_get_by_id` | Tenders board (PRs #29/#111/#115) | Tender status, source, linked load. Reads only — writes internal. |
| `drivers_search` / `drivers_get_by_id` | `AssignmentValidationService` driver-prediction fallback | `Status` is **ELD duty state** (`DRIVING`/`ON DUTY`/`OFF DUTY`/`SLEEPING`/`ONLINE`/`OFFLINE`) — **not** dispatch availability. Honour the CAVEAT in the tool description. Never fabricate a prediction when the endpoint returns error — render `Unavailable`. |
| `trucks_search` / `trucks_get_by_id` | Equipment context in Match + Assign | `TruckNum`, `VinNumber`, `Year`, `Make`, `Model`, `LicenseNum`, `Status` (case-sensitive: `Active`/`Inactive`/`Repair`/`Crashed`/`Planned`/`Temporary`/`Leased Out`/`In Shop`), `Fleet.{Id,Name}`. |
| `trailers_search` / `trailers_get_by_id` | Trailer-fit sidecar + equipment context | `TrailerNum`, `EquipmentType`, `EquipmentSize`, `Status` (same allowed set as trucks), `Fleet`. **Watch:** many rows carry `VinNum:"UNKNOWN"` and `LicenseExpiresAt:"1970-01-01"` — do not misread as a fresh timestamp; treat as `MissingDataFlag`. |
| `carriers_*` / `customers_*` / `invoices_*` / `deductions_*` / `fuel_transactions_*` / `drivers_events_search` / `trucks_events_search` | Billing readiness, revenue-protection signals, HOS context | Reads only. `customers_search` has **no name filter** — iterate/status-filter. |

### Two Alvys tenants

Production tenant: `alvys_f4df3d…` (verified live 2026-07-22).
Secondary tenant: `alvys_7cb72de…` — was `TEMPORARILY_UNAVAILABLE` at 2026-07-22 22:59 UTC; do
not treat as an auth issue, retry via `list_external_tools` on the source id.

## How to update this doc

1. New cross-app contract → add a row to the tables above **and** to the peer doc in the Yard repo.
2. New rule discovered by a real bug → add it as a numbered rule and link the incident.
3. Row of the workbook that references a boundary → cite this file under `Source`.
4. New Alvys field a normalizer starts to depend on → add it to the Alvys read contract table
   with the verified live shape, so future consumers don't assume a shape that doesn't exist.
