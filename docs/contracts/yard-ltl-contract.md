# Yard → LTL Cross-App Contract

**Anchor date:** 2026-07-23
**Owner:** LTL Tool (owns every HTTP contract, SQL store, and projection on the LTL side)
**Authoritative boundary rules:** [`docs/BOUNDARIES.md`](../BOUNDARIES.md)
**Ingestion pipeline detail:** [`docs/YARD_LTL_INGESTION.md`](../YARD_LTL_INGESTION.md)

This is the single place that enumerates **how the LTL Tool learns about the Yard application** —
every data shape, every channel, and the typed files that define them. It is a contract *index*: it
does not restate the envelope/rule detail that lives in the two docs linked above, it points at it.

## Principle

The Yard app is a **peer system**, not a data source the LTL Tool reaches into. The LTL Tool knows
about Yard **only** through the explicit HTTP contracts listed below. Hard rules (from
[`docs/BOUNDARIES.md`](../BOUNDARIES.md)):

- **No shared database.** Neither app reads or writes the other's tables.
- **No Alvys relay.** None of these channels read or write Alvys — Alvys stays the sole source of
  truth for operational data.
- **Honest missing state.** When a Yard channel is unavailable, the LTL UI renders `—` /
  "Unavailable" and degrades validation to an overridable warning. Yard presence/signals never
  fabricate a pass; a security hold on release is a blocker, unknown presence is a warning
  (boundaries fail closed).
- **New consumers must be registered.** Any new consumer of Yard-owned data must add a row to the
  contract table in [`docs/BOUNDARIES.md`](../BOUNDARIES.md) **and** appear in the table below.

## Channels

| # | Direction | Channel | Purpose | Contract detail |
|---|-----------|---------|---------|-----------------|
| 1 | Yard → LTL | `POST /api/v1/yard-events` (+ `GET` projection/inbox reads) | Durable scheduler ingestion: Yard freight events → LTL's own SQL store → scheduler projection. Versioned, idempotent, service-to-service auth (`YardEventIngest`). | [`docs/YARD_LTL_INGESTION.md`](../YARD_LTL_INGESTION.md) |
| 2 | Yard → LTL | `POST /api/yard/webhooks/receiver` (+ `GET /api/yard/webhooks/events` admin read) | Real-time freight webhooks: `TruckArrived` / `LoadReleased` / `LtlDraftCreated`. HMAC-signed (`X-Yard-Signature: t=…,v1=…`), fail-closed without a secret, gated behind `Yard:Webhooks:Enabled`. `LtlDraftCreated` seeds yard-originated LTL consolidation opportunities. | Backend: `src/LtlTool.Api/Features/Integrations/Yard/Webhooks/` |
| 3 | Yard → LTL | Yard-artifact intake (Phase 8.2) | Dock-verified pallet/dimension + photo/PDF artifacts and CTPAT inspection outcome, surfaced against equipment/load. Yard is the source of truth for presence, security release, and yard artifacts. | Backend: `src/LtlTool.Api/Features/Ltl/YardArtifacts/` |

The LTL Tool acts on channel 2's `LtlDraftCreated` **only inside its own Alvys-backed combine
flow** — the yard opportunity is an inbound suggestion, never a write path back into Yard or Alvys.

## Typed contract files (shape source of truth)

The wire shapes are defined once on the backend (C#) and mirrored on the frontend (TypeScript). These
files **are** the typed contract — treat them as the boundary, and change them together.

| Channel | Backend (authoritative) | Frontend mirror |
|---------|-------------------------|-----------------|
| 1 — scheduler ingestion | `src/LtlTool.Api/Features/Ltl/YardIngestion/*` (event envelope, classifier + projection) | scheduler reads are server-side; no SPA mirror |
| 2 — webhooks | `src/LtlTool.Api/Features/Integrations/Yard/Webhooks/YardWebhookApiModels.cs` | [`web/src/app/features/ltl/yard-webhooks.models.ts`](../../web/src/app/features/ltl/yard-webhooks.models.ts) |
| 3 — yard artifacts | `src/LtlTool.Api/Features/Ltl/YardArtifacts/YardArtifactModels.cs` | [`web/src/app/features/ltl/yard-artifacts.models.ts`](../../web/src/app/features/ltl/yard-artifacts.models.ts) |

Rules for these files:

- The frontend mirrors carry a header comment pointing at the backend model they mirror. Keep that
  pointer accurate.
- Nullable fields render `—`, never a fabricated `0` / `false` / "good".
- No signing secret or auth material ever appears in a payload shape — the webhook admin projection
  exposes only *whether* a secret is configured, never its value.

## What is explicitly NOT a channel

- No direct SQL/database access to Yard tables.
- No open-web scraping or third-party TMS ingestion of Yard-owned data.
- No implicit, stringly-typed Yard field access outside the typed files above. If a new Yard field
  is needed, add it to the backend model, mirror it, and register the consumer in
  [`docs/BOUNDARIES.md`](../BOUNDARIES.md).

## Relationship to the Demo Director

The isolated Demo Director feature (`web/src/app/features/ltl/demo`, see
[`ltl_separation_summary.md`](../../ltl_separation_summary.md) / this repo's demo-isolation PR) does
**not** consume any Yard channel directly. Its Dock act drives the LTL Tool's own Dock UI (yard
*cards* rendered by the real pages), which in turn read the channels above through the normal LTL
services. The walkthrough therefore inherits this contract; it never widens it.
