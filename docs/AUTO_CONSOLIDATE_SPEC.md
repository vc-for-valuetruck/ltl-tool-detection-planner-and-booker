# Auto-Executing Alvys Consolidation Clicks — Technical Spec

**Status:** DRAFT — spec + flag-off scaffolding only. No production writes. No behavior change
until every sign-off row in [`docs/ltl-tool.md`](./ltl-tool.md) is filled.
**Author:** LTL Tool engineering
**Depends on:** [`docs/PILOT_LAREDO_DALLAS.md`](./PILOT_LAREDO_DALLAS.md),
[`docs/ALVYS_API_DECISIONS.md`](./ALVYS_API_DECISIONS.md),
[`docs/transcripts/2026-07-17-reuben-sync.md`](./transcripts/2026-07-17-reuben-sync.md),
[`docs/ltl-tool.md`](./ltl-tool.md), [`docs/BOUNDARIES.md`](./BOUNDARIES.md)
**Extends (does not replace):**
`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*`
**Reads from (does not modify the read model):**
`src/LtlTool.Api/Features/Ltl/Consolidation/*`

---

## Required reading — what this spec is built on

This spec is deliberately load-bearing on five existing documents/code slices. Nothing below
invents a new writeback mechanism; it wires the existing one to the existing click card.

1. **[`docs/PILOT_LAREDO_DALLAS.md`](./PILOT_LAREDO_DALLAS.md)** — Phase 1 scope is read-only.
   §2.5 defines "Poornima's sanctioned Alvys click path" verbatim: waypoints on the parent,
   zeroed *dispatch* (loaded) miles on siblings, an `LTL` boolean trip reference, a `Main Load Id`
   reference, and a filterable Trips report (`Trip References contain "LTL = true" AND "Main Load
   Id = L-100234"`) to view combined RPM. §3 names Phase 2 (writeback) preconditions: Alvys
   permission, confirmed endpoints, sign-off in `docs/ltl-tool.md`, and the sandbox→production gate
   staying intact. This document is that Phase 2/5 execution spec.
2. **[`docs/ALVYS_API_DECISIONS.md`](./ALVYS_API_DECISIONS.md)** — decision #10 (2026-07-17) and
   the 2026-07-21 update draw the Public API vs internal API boundary: the Public API is
   read-only for consolidation writes forever; Waypoint creation, dispatch-mileage zeroing, and
   trip-reference writes are internal-API-only, observed-not-contracted. The 2026-07-21 update
   does **not** change this — it only adds unrelated Public-API document/invoice writes.
3. **[`docs/transcripts/2026-07-17-reuben-sync.md`](./transcripts/2026-07-17-reuben-sync.md)** —
   Reuben (Alvys) states verbatim at 01:11: *"you would always have to have an active session
   token from a user that signed in in order to authenticate the internal API endpoint"* — not a
   client-credentials token. At 03:07 he demonstrates the network-tab discovery method and
   confirms the token expires. At 15:55 he draws the `dispatch_miles` vs `customer_miles`
   distinction this spec depends on for §2.3.
4. **[`docs/ltl-tool.md`](./ltl-tool.md)** — the per-operation sign-off table that gates every
   Alvys write from sandbox to production. Today it holds three rows for the 2026-07-21
   document/invoice operations, all with an empty "Approved by" column. §3 of this spec adds five
   new rows — one per consolidation click — that must independently be filled before any of this
   reaches production.
5. **[`docs/BOUNDARIES.md`](./BOUNDARIES.md)** — Rule 3: *"Reads only, until sign-off... LTL's
   Alvys write boundary stays sandbox-gated and per-op sign-off in `docs/ltl-tool.md` remains
   empty."* This spec does not relax that rule; it extends the existing sandbox-gated boundary so
   that when sign-off is eventually granted, execution is a config change, not a new code path.
6. **`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*`** — the write boundary this spec
   extends. It already contains: `AlvysWriteOperationRegistry` with three
   `AlvysWriteApiSurface.Internal` operations (`add-extended-stop`, `zero-child-dispatch-miles`,
   `set-trip-references`) marked `AlvysLiveSupport.Unsupported` as scaffolding;
   `AlvysInternalApiOptions` with a master `Enabled` switch and three per-operation arm switches;
   `IAlvysInternalWriteClient` / `AlvysHttpInternalWriteClient` with the session-token auth +
   single re-auth-on-`token_expired` retry Reuben described; `AlvysOperationOutbox` /
   `AlvysOperationRecorder` for idempotency + audit; `AlvysWriteGateway.BuildInternal()` for the
   armed/enabled gating. **This spec's job is to (a) flip three registry entries from
   `Unsupported` to `Supported` once endpoints are confirmed, (b) add a fourth operation
   (`trip-assign` reuse — already `Supported` on the Public surface — chained per sibling), (c)
   add the plan-level orchestrator that walks the click card's five steps through this existing
   boundary, and (d) add the UI surface. It does not create a second writer.**
7. **`src/LtlTool.Api/Features/Ltl/Consolidation/*`** — the read-side model this spec now
   executes. `ConsolidationPlanService.BuildPlanAsync` already produces a `ConsolidationPlanResponse`
   containing `Parent`, `Siblings`, `StopSequence`, `ClickCard.{PlainText, TripReferenceValue,
   MainLoadIdReferenceValue}`, `CombinedRevenuePerMile`, and `Blockers`. `ClickCardCopiedRequest` /
   the `record-click-card-copied` endpoint already capture the manual-copy effectiveness metric.
   This spec adds an execution path that consumes the *same* `ConsolidationPlanResponse` — it does
   not re-derive parent/sibling/waypoint data independently.

---

## 1. Executive summary

This spec describes an **opt-in, sign-off-gated "Execute now" action** that takes the Alvys click
card the LTL tool already generates for a consolidation plan (Poornima's sanctioned pattern —
waypoints on the parent, zeroed dispatch miles on the children, the `LTL` boolean and `Main Load
Id` trip references) and drives those same five clicks through Alvys on the dispatcher's behalf,
using the dispatcher's own signed-in Alvys session. Nothing here ships live: every operation stays
routed through the existing `AlvysWriteOptions` / `AlvysInternalApiOptions` sandbox gate, the new
`Ltl:Writeback:AutoConsolidate:Enabled` flag defaults to `false`, and production execution remains
architecturally unreachable until all five new rows in [`docs/ltl-tool.md`](./ltl-tool.md) carry
two named reviewers and a date, exactly like the existing document/invoice rows. What dispatchers
gain, once sign-off lands and sandbox piloting is clean: the manual click-card execution
(currently ~8–12 minutes of Alvys UI work per consolidation across parent + N siblings — see §8)
collapses to a single confirm-and-wait action with a visible per-step reconciliation checklist,
and the tool—not tribal memory—becomes the source of truth for which trips actually got LTL'd. The
honest limits: because the internal API requires an active user Auth0 session token (Reuben,
2026-07-17 sync, 01:11), **there is no unattended/scheduled execution in Phase 1** — a dispatcher
must be signed into Alvys and the LTL tool at the moment "Execute now" is pressed, and if their
session lapses mid-plan the tool halts rather than guessing. Alvys writes cannot be atomically
undone, so failure handling is honest-partial-state-plus-human-review, not auto-rollback.

---

## 2. Operations to execute

Five clicks from Poornima's card (`docs/PILOT_LAREDO_DALLAS.md` §2.5), each mapped to the existing
`AlvysWriteOperationKind` registry entries. All five share:

- **Auth surface:** `AlvysWriteApiSurface.Internal` for ops 1–4; `AlvysWriteApiSurface.Public` for
  a driver/truck/trailer assignment reuse noted in step 2, which already exists as `trip-assign`
  and is out of scope for *this* spec's new writes (dispatchers already get that from the existing
  Assign workflow — see `docs/BOUNDARIES.md`'s naming table for what "Assign" already covers).
  This spec covers the four internal-surface ops the click card is unique to.
- **Auth token:** the acting dispatcher's Alvys **Auth0 session token**
  (`IAlvysInternalTokenProvider.GetSessionTokenAsync(request.ActingUserId, ct)`), never a
  client-credentials token — per Reuben, 2026-07-17 sync, 01:11 and 03:07, and per decision "2026-
  07-17 · Internal API auth requires an active user session, not client-credentials" in
  `docs/ALVYS_API_DECISIONS.md`.
- **Route status:** the internal-API routes below are **observed shapes captured via the Alvys
  UI network tab per Reuben's sanctioned discovery method** (2026-07-17 sync, 03:07: *"right-click,
  inspect, open up a network tab... add extended stop. This is the internal endpoint"*), recorded
  as placeholders in `AlvysHttpInternalWriteClient.ResolveEndpoint()` and the "Discovered internal
  endpoints" table in `docs/ALVYS_API_DECISIONS.md`. **They are not Alvys-documented and can change
  without notice.** Each is marked below as `OBSERVED, NOT CONTRACTED`.

### 2.1 Create the LTL trip (or attach existing) with the main-load reference

- **Operation code:** `set-trip-references` (`AlvysWriteOperationKind.SetTripReferences`),
  applied to the **parent** trip with `LtlReference = true`.
- **Endpoint (observed):** `PATCH /internal/trips/{tripId}/references` — `OBSERVED, NOT
  CONTRACTED`. Body carries `LTL` as the string `"true"` (Reuben, 18:18: *"Parameter ID string...
  yeah"* — both the LTL boolean and Main Load Id are string-encoded on the wire, never a native
  JSON boolean).
- **Auth:** internal-API session token, acting dispatcher.
- **Idempotency:** key = `tripId + "set-trip-references" + payloadHash` (see §5). A replay against
  the same parent trip with the same `LTL=true` / same `MainLoadId` value hashes identically and
  is a no-op per `AlvysOperationOutbox.FindExecutableByKey`; a replay that tries to change the
  `MainLoadId` on an already-executed record is a **conflict**, surfaced to the dispatcher rather
  than silently overwritten (mirrors `AlvysOperationRecorder`'s existing
  `AlvysRecordDisposition.Conflict` path).
- **Reconciliation:** re-fetch the parent trip via the existing read surface
  (`trips_get_by_id` / `AlvysClient`, per `docs/BOUNDARIES.md`'s Alvys read contract) and confirm
  `Trip.References[]` contains an entry with `Name="LTL"`, `Value="true"`. Mismatch (200 returned
  but re-fetch shows no such reference, or a different value) → `AlvysReconciliationState.Mismatch`,
  never coerced to Confirmed.
- **Failure modes:**
  - 4xx (validation/auth) → `AlvysOperationRecordStatus.InternalFailed`; surfaced to the dispatcher
    as "Alvys rejected the reference write" with the raw error *type*, never the raw body.
  - 5xx → same `InternalFailed` path; retried exactly once only if it was a `token_expired` signal
    (`AlvysInternalTokenSignals.IndicatesTokenExpired`); any other 5xx is an honest failure, no
    silent retry loop.
  - Silent no-op (200 returned, reference not actually set) → caught by reconciliation, never by
    the HTTP call alone. This is why reconciliation is mandatory, not optional, for this operation.

### 2.2 Add waypoints for the sibling loads

- **Operation code:** `add-extended-stop` (`AlvysWriteOperationKind.AddExtendedStop`), once per
  sibling, applied to the **parent** trip.
- **Endpoint (observed):** `POST /internal/trips/{tripId}/waypoints` — `OBSERVED, NOT CONTRACTED`
  (this is the literal "Add extended stop" call Reuben captured live in the 03:07 network-tab
  demo).
- **Auth:** internal-API session token, acting dispatcher.
- **Payload contract:** per decision "Waypoint stop details must be duplicated onto the parent —
  no auto-inherit" (`docs/ALVYS_API_DECISIONS.md`, Reuben 12:28): the payload must carry the
  **full sibling stop** — address, scheduled window, contact, references — not a bare load number.
  `AlvysWaypointStop { CompanyId, Sequence, ScheduledAt }` is the existing shape in
  `AlvysOperationModels.cs`; this spec requires it be populated from the same
  `ConsolidationPlanResponse.Siblings[i]` / `StopSequence` the click card already renders, so the
  waypoint the tool creates matches the address the dispatcher would have typed by hand. A
  client-side completeness check (all address fields present) runs before the call fires, per the
  "Failure mode to avoid" callout in the same decision.
- **Idempotency:** key = `tripId + "add-extended-stop" + payloadHash`, where the hash includes the
  sibling's `CompanyId` + `Sequence` so adding a *second, distinct* sibling as a waypoint is a
  different key (not collapsed into a false no-op), while re-submitting the *same* sibling
  waypoint after a crash mid-plan is a true no-op.
- **Reconciliation:** re-fetch the parent trip and confirm a stop matching the sibling's
  `CompanyId` exists at the expected `Sequence` position in `Trip.Stops[]`.
- **Failure modes:** same 4xx/5xx/token_expired handling as §2.1. Additional failure mode specific
  to this op: Alvys accepts an incomplete address (per the decision's explicit warning) — this is
  why the client-side pre-flight check exists; the tool must never rely on Alvys to reject a bad
  payload.

### 2.3 Zero the loaded miles on the child trips

- **Operation code:** `zero-child-dispatch-miles` (`AlvysWriteOperationKind.ZeroChildDispatchMiles`),
  once per sibling, applied to each **sibling/child** trip.
- **Endpoint (observed):** `PATCH /internal/trips/{tripId}/mileage` — `OBSERVED, NOT CONTRACTED`.
- **Auth:** internal-API session token, acting dispatcher.
- **Field-level contract (critical safety rule):** sets **only**
  `Trip.LoadedMileage.Distance.Value = 0`. `Load.CustomerMileage` is never touched. Per Reuben,
  2026-07-17 sync, 15:55: *"You only want to zero out the dispatch mileage because that's
  driver-facing... for the parent load, that's where you're going to keep all the miles."* This is
  the anti-failure-map 3e (payroll double-pay) guardrail — zeroing the wrong field, or zeroing it
  on the parent, is the single most expensive bug this spec can introduce. The payload builder
  must be unit-tested to assert `CustomerMileage` never appears as a mutated field in the request
  body for this operation.
- **Idempotency:** key = `tripId + "zero-child-dispatch-miles" + payloadHash`. Because the target
  value is always `0`, the hash is stable across replays — a repeat call against an already-zeroed
  trip is always a no-op, never a conflict.
- **Reconciliation:** re-fetch the child trip and confirm `Trip.LoadedMileage.Distance.Value == 0`
  **and** `Load.CustomerMileage` is unchanged from the pre-write snapshot captured at plan-build
  time. A change to `CustomerMileage` is treated as a `Mismatch` even if `LoadedMileage` zeroed
  correctly — this reconciliation check is deliberately stricter than "did the field I wanted
  change" because an accidental customer-mileage mutation is the failure mode this operation exists
  to prevent.
- **Failure modes:** same 4xx/5xx/token_expired handling as §2.1. A silent no-op here (200 but
  `LoadedMileage` unchanged) is caught only by reconciliation — this is the operation where a
  false "success" has the highest financial blast radius (driver overpay), so its reconciliation
  check is mandatory before the click card's checkmark can turn green.

### 2.4 Set the boolean LTL trip flag

Folded into §2.1 (`set-trip-references`) — the click card treats "LTL = true" and "Main Load Id =
{parent}" as one Trip References edit made together on the parent, matching Poornima's card text
verbatim (`docs/PILOT_LAREDO_DALLAS.md` §2.5, step 1: *"In Trip References, add: LTL = true, Main
Load Id = L-100234"*). Listed as its own row in the task brief; implemented as one operation
because Alvys exposes both as fields on the same reference-write call (per the observed endpoint
shape). No separate endpoint call is made for this step — see §2.1 for the full spec.

### 2.5 Set the main-load identifier on each child

- **Operation code:** `set-trip-references` again, this time applied **once per sibling/child
  trip** with `MainLoadId = {parent.LoadNumber}` (and `LtlReference = true` on the child too, per
  the click card's step 2/3 text: *"In Trip References, add: LTL = true, Main Load Id =
  L-100234"* — the same two-field write happens on **every** sibling, not just the parent).
- **Endpoint (observed):** same `PATCH /internal/trips/{tripId}/references` as §2.1, called against
  each child `tripId`.
- **Auth / idempotency / reconciliation / failure modes:** identical pattern to §2.1, scoped per
  child trip. Idempotency key = `childTripId + "set-trip-references" + payloadHash`.

### Summary table

| # | Click | Operation code | Surface | Target | Endpoint (observed) |
|---|---|---|---|---|---|
| 1 | LTL trip ref on parent | `set-trip-references` | Internal | parent trip | `PATCH /internal/trips/{tripId}/references` |
| 2 | Waypoint per sibling | `add-extended-stop` | Internal | parent trip | `POST /internal/trips/{tripId}/waypoints` |
| 3 | Zero dispatch miles per child | `zero-child-dispatch-miles` | Internal | each child trip | `PATCH /internal/trips/{tripId}/mileage` |
| 4 | LTL boolean flag | *(same call as #1)* | Internal | parent trip | *(same as #1)* |
| 5 | Main-load id per child | `set-trip-references` | Internal | each child trip | `PATCH /internal/trips/{tripId}/references` |

All five clicks route through the **same** `AlvysHttpInternalWriteClient.ExecuteAsync` and the
**same** `AlvysOperationRecorder` outbox pattern already in the repo. No new writer class.

---

## 3. Gate + posture

### 3.1 Feature flag

`Ltl:Writeback:AutoConsolidate:Enabled` (env var `Ltl__Writeback__AutoConsolidate__Enabled` /
`LTL_WRITEBACK_AUTOCONSOLIDATE_ENABLED`). **Default: `false`.** Bound onto a new
`ConsolidationAutoExecuteOptions` class (sibling to `AlvysWriteOptions` /
`AlvysInternalApiOptions`, same options-pattern conventions as the rest of the writeback slice).
This flag gates only the **orchestrator** (the thing that walks all five clicks as one plan); it
does not bypass any existing per-operation arm switch on `AlvysInternalApiOptions`. Both must be
true.

### 3.2 Environment mode

Unchanged from the existing pattern — reused, not duplicated:

- `AlvysWriteOptions.Mode` must be `Sandbox`.
- `AlvysWriteOptions.Environment` must be one of `RecognisedSandboxEnvironments`
  (`sandbox`/`uat`/`staging`/`test`).
- `AlvysWriteOptions.SandboxBaseUrl` must be set and must not contain `integrations.alvys.com`
  (`HasSandboxBaseUrl`).
- `AlvysInternalApiOptions.Enabled` must be `true` and `HasBaseUrl` must be `true`.
- Each of the three internal operations (`EnableAddExtendedStop`, `EnableZeroChildDispatchMiles`,
  `EnableSetTripReferences`) must be individually armed.

This spec adds **no new environment check** — it composes checks that already exist in
`AlvysWriteGateway.InternalBlockers()`. If any is false, the orchestrator's `Execute now` action
is not offered in the UI (§4) and the API returns the existing `AlvysOperationDisposition.AuditOnly`
outcome with the relevant blocker string.

### 3.3 Sign-off

Five new rows in [`docs/ltl-tool.md`](./ltl-tool.md)'s sign-off log — one per operation
(§2.1/§2.4 share a row since they're one call), following the exact table shape already used for
the 2026-07-21 document/invoice rows:

| Operation | Contract confirmed (link/doc) | Approved by | Date | Production gate implemented |
|---|---|---|---|---|
| `set-trip-references` (parent LTL flag) | Internal API — observed via network-tab capture, not Alvys-documented; pending re-verification against a live sandbox call | _(pending)_ | | No |
| `add-extended-stop` (sibling waypoint) | Internal API — observed via network-tab capture, not Alvys-documented; pending re-verification against a live sandbox call | _(pending)_ | | No |
| `zero-child-dispatch-miles` (child mileage) | Internal API — observed via network-tab capture, not Alvys-documented; pending re-verification against a live sandbox call | _(pending)_ | | No |
| `set-trip-references` (child main-load id) | Internal API — observed via network-tab capture, not Alvys-documented; pending re-verification against a live sandbox call | _(pending)_ | | No |
| `Ltl:Writeback:AutoConsolidate` orchestrator (plan-level) | N/A — composes the four rows above; no independent Alvys endpoint | _(pending)_ | | No |

Per `docs/ltl-tool.md`'s existing rule: *"No filled-in row above means no operation is approved
for production... Do not implement production execution for an operation until its row is
complete."* Production stays unreachable until **all five** rows carry an approver name and date
**and** the independent production gate (item 3 in that file's preconditions list) exists in code.
This spec does not implement that production gate — it is explicitly Phase 2 (§7).

### 3.4 Two-reviewer rule

Unchanged, called out explicitly: any PR touching
`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*`,
`src/LtlTool.Api/Features/Ltl/Consolidation/*`, or the new orchestrator/UI slice this spec adds
requires **two approvers**, per the existing repo rule. This is enforced at the branch-protection
level, not by this spec, but every PR description for this slice must name both reviewers.

### 3.5 Kill switch

`Ltl:Writeback:AutoConsolidate:Enabled` doubles as the kill switch — flipping it to `false` at
runtime (config reload, no deploy required) stops the orchestrator from offering `Execute now` and
fails closed on any in-flight orchestration request that hasn't yet been dispatched to Alvys (a
request already mid-flight for an individual operation completes or fails on its own; the switch
prevents starting new operations, it does not abort a live HTTP call already in transit — the
outbox provides the crash-safety net for that boundary). The flag's current state is surfaced on
the existing writeback ops posture endpoint (`AlvysReadinessService` / the readiness JSON the ops
panel reads) as a new top-level `AutoConsolidateEnabled: bool` field, following the same pattern
`WritebackEnabled` and `SandboxExecutionConfigured` already use.

---

## 4. UI surface (in-tool)

All additions live on the existing **Plan Detail** screen (`docs/PILOT_LAREDO_DALLAS.md` §9,
Screen 2), which today shows the trailer allocation, economics, click card, and audit-trail row
with nothing written to Alvys. This spec adds:

- **"Auto-execute Alvys clicks" toggle.** Visible only when both are true: (a)
  `Ltl:Writeback:AutoConsolidate:Enabled` is `true` (read from the readiness endpoint's new
  `AutoConsolidateEnabled` field, §3.5), and (b) the current user has a valid Alvys internal-API
  session token (checked via the same `IAlvysInternalTokenProvider` the backend uses — the SPA
  calls a lightweight `/api/ltl/consolidation/auto-execute/session-status` check that never
  returns the token itself, only `{ hasValidSession: bool, expiresInSeconds: int? }`). When either
  condition is false, the toggle renders disabled with the specific reason (mirrors the existing
  "Missing pallet data → visual verify" honesty pattern already used elsewhere on this screen).
- **"Execute now" primary CTA.** Enabled only when the toggle above is on and the plan has zero
  `Blockers` (reusing `ConsolidationPlanResponse.Blockers` — the same list that already suppresses
  the copy-card action per `docs/PILOT_LAREDO_DALLAS.md`'s existing rule that a blocked plan must
  not offer the click card). Pressing it starts a **countdown + Undo window** — mirroring the dock
  combine's one-tap Undo pattern already shipped in `DockController.Undo` /
  `DockService.UndoAsync` (`DockUndoRequest` / `DockUndoResponse`, `Action = Undo`). The countdown
  (proposed default: 8 seconds, configurable) gives the dispatcher a last chance to cancel before
  the first Alvys call fires; unlike the dock combine's undo (which reverses nothing in Alvys
  because dock combine never writes there), this Undo *before* execution starts is a true
  cancel — no Alvys call has happened yet, so there is nothing to reconcile. Once the countdown
  elapses and the first operation dispatches, Undo is no longer offered (§5 — Alvys writes cannot
  be atomically undone after they land).
- **Reconciliation panel.** Renders the five-row operation list from §2 as a checklist. Each row
  starts `Pending`, flips to a green check when `AlvysReconciliationState.Confirmed` is returned
  for that operation's outbox record, and flips to a red X on
  `AlvysOperationRecordStatus.InternalFailed` or `AlvysReconciliationState.Mismatch`. On red X, the
  panel surfaces the **error type only** — e.g. `token_expired`, `HTTP 422`, `reconciliation_mismatch`
  — never the raw Alvys response body (which can carry auth material or verbose internal detail
  not meant for a dispatcher-facing screen; mirrors the existing rule in
  `AlvysHttpInternalWriteClient.SendAsync` that logs status-only, never bodies).

---

## 5. Failure & rollback

- **Partial-success audit trail.** If operation 3 of 5 fails after 1 and 2 succeeded, the outbox
  (`AlvysOperationOutbox` / `AlvysOperationRecord`) has three rows: two `Status = Recorded` with
  `ReconciliationState = Confirmed`, one `Status = InternalFailed`. The remaining two operations
  are **not attempted** — the orchestrator halts on first failure rather than continuing to
  half-execute the plan (this matches the existing internal-API guardrail note in
  `docs/ALVYS_API_DECISIONS.md`: *"on any 500 or unexpected shape, halt the plan and surface a
  legible error — never partial-execute"* further than the failure point).
- **No auto-rollback.** Alvys writes cannot be atomically undone (there is no "delete waypoint" /
  "restore mileage" call in scope here, and building one would double the blast radius of this
  spec for no proven benefit). Recovery is a **human action in Alvys**, informed by the
  reconciliation panel's exact per-operation state.
- **Human review surface.** The existing cross-load **Assignments history** page
  (`GET /api/ltl/assignments?user={u}&day={yyyy-MM-dd}&reasonType={reason}`, per `CLAUDE.md`'s
  API surface list — read-only, `AlvysWriteback` stays `NotPerformed` today) is extended with a
  filter for `AutoConsolidate` plan executions, so a dispatcher or ops lead can pull "every
  auto-execute attempt today, and which operations within each succeeded/failed" without opening
  Alvys. This is a read-only extension of an existing endpoint, not a new write surface.
- **Idempotency key.** `tripId + operationType + payloadHash`, exactly matching the existing
  outbox contract (`AlvysOperationOutbox.FindExecutableByKey(ownerId, idempotencyKey)` +
  `AlvysPayloadHasher.Hash(operationCode, body)`). A replay of the same trip/operation/payload
  after a crash or a double-click is a no-op; a replay with a *different* payload under the same
  key is a `AlvysRecordDisposition.Conflict`, surfaced to the dispatcher rather than silently
  applied.

---

## 6. Testing

### 6.1 Unit tests

- **Idempotency-key dedupe.** Same `tripId + operationType + payloadHash` submitted twice →
  second call returns `DuplicateReplay`, not a second outbox row, not a second Alvys dispatch
  (mirrors existing `AlvysOperationRecorder` tests for the Public-API path — the internal-API path
  needs the equivalent case).
- **Sign-off gate enforcement.** With every `docs/ltl-tool.md` row for this slice empty, the
  orchestrator's readiness check reports zero operations `Supported`/armed regardless of flag
  state — a test asserting "flag on + sandbox configured + sign-off empty" still cannot dispatch
  live is the single most important test in this PR, because it's the test that keeps §3.3 honest
  in code, not just in a doc.
- **Sandbox-only enforcement.** `AlvysWriteOptions.HasSandboxBaseUrl` rejecting
  `integrations.alvys.com` already has coverage (PR #39 per `docs/ltl-tool.md`); this spec adds
  the equivalent assertion for `AlvysInternalApiOptions.BaseUrl` — a production-shaped internal
  host string must be rejected the same way.
- **`dispatch_miles`-only mutation.** Unit test asserting the `zero-child-dispatch-miles` payload
  builder never includes `CustomerMileage` as a field, for every code path (this is the 3e
  payroll-double-pay guardrail from §2.3, and it deserves a standalone test independent of the
  broader plan-execution tests).

### 6.2 Integration test

A fake internal-API server (in-process `HttpMessageHandler` stub, matching the existing pattern
`AlvysHttpInternalWriteClient`'s tests already use per the "fake handler only" note in
`AlvysInternalWriteClient.cs`'s remarks) that can be told to emit, per operation: `200` (success),
`401` with `token_expired` body (exercise the single re-auth retry), `422` (validation failure),
`500` (transport-level failure), and a **silent no-op 200** (returns success but the subsequent
re-fetch shows no change) to prove the reconciler — not the HTTP status alone — is what the click
card's green-check state depends on. Each of the five click-card operations gets at least one test
per response shape, run through the full orchestrator (not just the client in isolation) so the
halt-on-first-failure behavior (§5) is exercised end-to-end.

### 6.3 Contract test

Extends the existing `AlvysWriteOptions` production-host-rejection contract test
(`docs/ltl-tool.md` references PR #39's coverage for the Public-API sandbox boundary) with the
equivalent assertion for the internal-API surface and the new
`Ltl:Writeback:AutoConsolidateOptions`: **the application must refuse to start**, or must resolve
`AutoConsolidateEnabled=false` regardless of config-file input, if `Ltl:Writeback:AutoConsolidate:Enabled=true`
is combined with a production-shaped `AlvysInternalApiOptions.BaseUrl` or
`AlvysWriteOptions.SandboxBaseUrl`. This is a startup-time check, not a runtime one, matching the
"architecturally unreachable" framing `docs/ltl-tool.md` already uses for the Public-API path.

---

## 7. Rollout sequence

- **Phase 1a — ships as spec + flag-off code (this PR and its immediate follow-ups).** This
  document; the `Ltl:Writeback:AutoConsolidateOptions` class (flag default `false`); the
  orchestrator interface (`IConsolidationAutoExecuteService` or similarly named) that sequences
  the five §2 calls through the *existing* `IAlvysWriteGateway` / `IAlvysOperationRecorder` /
  `IAlvysInternalWriteClient`; the reconciliation-panel API contract; all §6 tests. **No real
  writes.** The three internal-API registry entries stay `AlvysLiveSupport.Unsupported` until
  their endpoints are independently reconfirmed (§2's "observed, not contracted" caveat) — this
  phase does not flip that switch.
- **Phase 1b — behind sign-off + non-prod sandbox.** Once (a) the internal-API endpoints in §2 are
  reconfirmed against a live Alvys sandbox call (not just the network-tab capture) and flipped to
  `AlvysLiveSupport.Supported`, and (b) at least the sandbox-scoped rows of §3.3's sign-off table
  are filled, dispatchers pilot `Execute now` against **real UAT loads in the sandbox tenant only**
  — never production data, per the existing `AlvysWriteOptions.Mode = Sandbox` +
  `RecognisedSandboxEnvironments` gate. This is the phase where the countdown/Undo UX and the
  reconciliation panel get real dispatcher feedback.
- **Phase 2 — production.** Every row in §3.3's sign-off table filled with two named reviewers and
  a date, **and** the independent production gate `docs/ltl-tool.md` §"What has to be true before
  production writeback is enabled" item 3 (a distinct `AllowProduction` flag requiring the base URL
  to equal the real production host) is implemented in code for the internal-API surface — which
  does not exist today and is explicitly out of scope for this spec. Until both conditions hold,
  production execution of any of the five clicks in §2 is **architecturally unreachable**, the same
  posture the Public-API writeback slice already has.

---

## 8. Revenue narrative

Per [`docs/PILOT_LAREDO_DALLAS.md`](./PILOT_LAREDO_DALLAS.md), the pilot corridor is bounded to
loads originating within 150 miles of the Laredo yard and destined within 250 miles of the Dallas
yard — a deliberately small, defensible box. The pilot document does not yet commit to a specific
loads/week figure for the corridor (that number depends on Jordan's upstream data-quality fix and
the review-meeting sign-off Jason requested — see `docs/PILOT_LAREDO_DALLAS.md` §6), so this
section states the honest form of the math rather than inventing a volume:

> **Time saved = (manual click-card execution time per consolidation) × (consolidations
> executed per week in the corridor).**

Poornima's click card (`docs/PILOT_LAREDO_DALLAS.md` §2.5) requires, per consolidation: opening
the parent load, assigning driver/truck/trailer, adding one waypoint per sibling, setting two trip
references on the parent, then repeating the trip-reference edit on *each* sibling, then confirming
via a manual Trips-report filter to see the combined RPM. For a typical 2-sibling consolidation
(parent + 2 children, matching the worked example in §2.5), that is roughly 8–12 minutes of Alvys
UI navigation per plan, based on the number of discrete screens/edits the card enumerates (1
waypoint-add screen + 1 parent reference edit + 2 child reference edits + 2 mileage edits + 1
report-filter check). Automating §2's five operations does not change *what* happens in Alvys — it
changes who clicks: the dispatcher confirms the plan once and watches a reconciliation checklist
instead of re-typing the same reference values three to five times per consolidation. **At N
consolidations/week and M minutes/consolidation, this saves M × N dispatcher-minutes/week** — the
tool should report this number against the pilot's own measured N once Phase 1b sandbox piloting
produces real counts, rather than this spec asserting a number the pilot doc itself has not yet
committed to.

**Second-order lever: accessorial + billing-leakage.** Once the tool — not a dispatcher's memory —
controls the `Main Load Id` and `LTL` trip references on every consolidated trip, two things become
possible that are out of scope for this spec but worth naming: (1) the Trips-report filter
Poornima's card currently asks a human to run manually (`Trip References contain "LTL = true" AND
"Main Load Id = ..."`) can be run by the tool itself, closing the loop on combined-RPM visibility
without a manual report step; (2) `docs/PILOT_LAREDO_DALLAS.md`'s own anti-failure map names 3d
(accessorials) and billing-readiness scoring as explicitly deferred, later-phase material — but
both depend on the trip references being reliably set, which is exactly what §2 makes reliable.
This spec does not claim an accessorial dollar figure; it notes that accessorial/billing-leakage
work becomes *tractable* once trip-reference integrity is automated rather than manually typed
three to five times per consolidation.

---

## 9. What this spec explicitly does NOT ship

- **No auto-execution without a signed-in dispatcher.** The internal API requires an active user
  Auth0 session token (Reuben, 2026-07-17 sync); there is no service-account or headless-login path
  in this spec. If the dispatcher's session lapses mid-plan, the orchestrator halts (§5) rather than
  substituting any other credential.
- **No writes outside the pilot corridor.** The orchestrator only ever operates on a
  `ConsolidationPlanResponse` already produced by the existing, corridor-bounded
  `ConsolidationPlanService` (Laredo↔Dallas per `docs/PILOT_LAREDO_DALLAS.md` §2.3). It does not
  independently query or write any load outside that corridor.
- **No writes to any endpoint not listed in §2.** The four internal-API operation kinds
  (`SetTripReferences`, `AddExtendedStop`, `ZeroChildDispatchMiles`, and the shared parent/child
  reference call) are the entire write surface this spec adds. Anything else a dispatcher might do
  manually in Alvys (driver/truck/trailer assignment beyond what `trip-assign` already supports,
  dispatch, invoicing, accessorial entry) stays untouched by this spec.
- **No production execution until all sign-off rows filled.** Per §3.3 and §7 Phase 2 — the
  existing `docs/ltl-tool.md` mechanism, unmodified in its rules, extended only with five new rows
  that start empty exactly like every other pending row in that file.

---

## Related files

- [`docs/ltl-tool.md`](./ltl-tool.md) — sign-off table this spec adds five rows to.
- [`docs/ALVYS_API_DECISIONS.md`](./ALVYS_API_DECISIONS.md) — internal-API decisions this spec is
  built on; decision #10 in particular.
- [`docs/BOUNDARIES.md`](./BOUNDARIES.md) — read-only-Alvys guardrail this spec's sandbox posture
  must keep satisfying.
- [`docs/PILOT_LAREDO_DALLAS.md`](./PILOT_LAREDO_DALLAS.md) — Phase 1 scope, the click card this
  spec executes, and the corridor bound the orchestrator inherits.
- `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*` — the write boundary extended, not
  replaced.
- `src/LtlTool.Api/Features/Ltl/Consolidation/*` — the read-side plan/click-card model consumed
  as-is.
