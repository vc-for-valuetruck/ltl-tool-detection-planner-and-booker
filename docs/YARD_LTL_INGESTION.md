# Yard → LTL Scheduler Ingestion (v1)

**Anchor date:** 2026-07-22
**Owner:** LTL Tool (owns the HTTP contract, the SQL store, and the derived projection)
**Boundary rules:** [`docs/BOUNDARIES.md`](BOUNDARIES.md) (Yard → LTL contract table)

This is the durable pipeline that turns Yard's physical freight events into scheduler input for the
LTL tool. Yard stays a **peer system**: it POSTs events over the HTTP contract below and the LTL tool
persists them to **its own** SQL store. There is **no shared database, no cross-database read of
Yard's tables, and no Alvys relay** — this endpoint neither reads nor writes Alvys.

## Endpoint surface

| Verb + route | Purpose | Auth |
|---|---|---|
| `POST /api/v1/yard-events` | Ingest one Yard event (idempotent). | `YardEventIngest` policy |
| `GET /api/v1/yard-events/schedule-input` | Scheduler worklist: projections, newest-updated first. Filters: `readiness`, `holdState`, `sourceRecordType`, `yardLocationId`, `schedulableOnly`, `max` (≤1000, default 200). | `YardEventIngest` policy |
| `GET /api/v1/yard-events/schedule-input/{sourceSystem}/{sourceRecordType}/{sourceRecordId}` | One projection, or 404. | `YardEventIngest` policy |
| `GET /api/v1/yard-events/events` | Recent inbox events, newest first (`max` ≤1000, default 100). | `YardEventIngest` policy |
| `GET /api/v1/yard-events/events/{sourceSystem}/{sourceRecordType}/{sourceRecordId}` | All inbox events for one record, oldest occurrence first. | `YardEventIngest` policy |
| `POST /api/v1/yard-events/schedule-input/{sourceSystem}/{sourceRecordType}/{sourceRecordId}/replay` | Rebuild one projection from its stored events without ingesting a new event (admin/repair). 200 or 404. | `YardEventIngest` policy |

## Request envelope

```jsonc
{
  "eventId": "3f2b…",             // UUID, required, non-empty
  "schemaVersion": 1,              // integer, must equal the supported version (1)
  "eventType": "truck.arrived",    // string, classified to a freight category (see vocabulary)
  "occurredAt": "2026-07-01T12:00:00Z", // UTC timestamp the event occurred in the yard
  "sourceSystem": "yard-control",  // must match the configured expected source
  "sourceRecordType": "appointment", // e.g. appointment / trailer
  "sourceRecordId": "R1",          // Yard's stable id for the freight record
  "yardLocationId": "YARD-A",      // required
  "correlationId": null,           // optional cross-system trace id
  "payload": { "truckId": "T-1" }  // JSON object (must be an object, not a scalar/array)
}
```

### Responses

| Status | Meaning |
|---|---|
| **202 Accepted** | First delivery — persisted and (if freight-affecting) projected. Body: `{ status: "accepted", category, affectsSchedulerInput, projection }`. |
| **200 OK** | Duplicate delivery of the same `eventId` + source record identity — idempotent no-op, no second projection. Body shaped as above with `status: "duplicate"`. |
| **400 Bad Request** | Contract violation — missing required field, empty `eventId`, unsupported `schemaVersion`, unexpected `sourceSystem`, or a non-object `payload`. Body: `{ errors: [ … ] }`. Nothing is persisted. |
| **401 Unauthorized** | Missing/invalid service-to-service token. |

## Idempotency

The inbox primary key is the dedupe key
`{eventId}:{sourceSystem}:{sourceRecordType}:{sourceRecordId}`. At-least-once delivery is safe: a
repeat collides on that key and is acked (200) without re-projecting. A concurrent race that loses
the unique-key insert is also treated as a duplicate.

## Projection semantics

- **Eligible immediately.** The moment a freight-affecting event is persisted, `SchedulerEligible`
  is true — the scheduler may consider the record right away.
- **Provisional → Ready.** `Readiness` starts `Provisional` and advances to `Ready` only when dock
  completion **and** security clearance are observed and no active hold/cancellation is in effect.
  `Completeness` (0..1) is the fraction of those two milestones observed.
- **Order-independent.** The projection is deterministically rebuilt from the full event log on every
  append, sorted by `(OccurredAt, Sequence)`, so out-of-order delivery and replay converge to the
  same result.
- **Honest missing state.** Freight/equipment/timing fields stay `null` until a Yard event actually
  carries them — never coerced to 0/false. A `null` means "unknown", not "empty".
- **Last-writer-wins overlay.** A later event never clears a value an earlier event already set.
- **Administrative / Unknown events** are persisted to the immutable inbox for audit/replay but never
  create or advance a projection.

### Payload fields consumed

Optional keys read from `payload` (all others are retained verbatim in the inbox but not projected):
`truckId`, `trailerId`, `dockId`, `weightLbs`, `lengthInches`, `widthInches`, `heightInches`,
`pieceCount`, `originLocationId`, `destinationLocationId`, `appointmentAt`, `parentSourceRecordId`,
`relatedRecordIds` (array).

## Canonical `eventType` vocabulary

Classification normalizes the wire string first (lower-case; `_`, `-`, space, `/`, `:` collapse to
`.`), so `Truck_Arrived`, `truck-arrived`, and `truck.arrived` all match. Unrecognized types
**fail closed** to `Unknown` (audited, never projected).

| Category | Canonical + tolerated wire types | Scheduler input |
|---|---|---|
| Arrival | `arrival`, `truck.arrived`, `truck.arrival`, `gate.arrival` | yes |
| Departure | `departure`, `truck.departed`, `gate.departure` | yes |
| CheckIn | `check.in`, `checkin`, `driver.check.in` | yes |
| LoadStart | `load.start`, `loading.started` | yes |
| LoadComplete | `load.complete`, `loading.completed`, `dock.complete` | yes (dock milestone) |
| UnloadStart | `unload.start`, `unloading.started` | yes |
| UnloadComplete | `unload.complete`, `unloading.completed` | yes |
| TrailerAssignment | `trailer.assignment`, `trailer.assigned` | yes |
| DockAssignment | `dock.assignment`, `dock.assigned` | yes |
| FreightDimensions | `freight.dimensions`, `freight.dimensions.captured` | yes |
| FreightWeight | `freight.weight`, `freight.weight.captured` | yes |
| Appointment | `appointment`, `appointment.scheduled`, `appointment.updated` | yes |
| Exception | `exception`, `exception.raised` | yes (flags open exception) |
| Hold | `hold`, `hold.placed`, `security.hold` | yes (hold state) |
| Release | `release`, `load.released`, `security.release` | yes (security clearance) |
| Cancellation | `cancellation`, `cancelled`, `canceled` | yes (terminal) |
| Split | `split`, `load.split` | yes (relationship) |
| Consolidation | `consolidation`, `load.consolidated` | yes (relationship) |
| Administrative | `gate.log`, `note.added`, `visitor.scheduled`, `report.generated`, `user.login` | no (audited only) |
| Unknown | anything unrecognized | no (audited only) |

## Configuration

Bound from the `YardIngestion` section (all defaults match the v1 contract, so a fresh deployment
accepts `yard-control` events without extra config):

| Key | Default | Meaning |
|---|---|---|
| `YardIngestion:SupportedSchemaVersion` | `1` | The only schema version accepted; others → 400. |
| `YardIngestion:ExpectedSourceSystem` | `yard-control` | Accepted `sourceSystem`; mismatch → 400. Empty disables the check. |
| `YardIngestion:RequiredAppRole` | `YardEvents.Ingest` | Entra app role a caller must present (`roles` claim). Empty disables the app-role check. |
| `YardIngestion:RequiredScope` | *(unset)* | Alternative delegated scope (`scp` claim) that also satisfies the policy. |

## Auth (service-to-service)

The `YardEventIngest` authorization policy requires an authenticated caller and then, in EntraId
mode, either the configured **app role** (`roles`) **or** the configured **scope** (`scp`). Yard's
managed identity is granted the `YardEvents.Ingest` app role on the LTL API app registration; its
client-credentials token then carries that role — **no shared secret is introduced**. In Demo access
mode (local/UAT) the policy passes so the pipeline is usable without an Entra tenant; if neither role
nor scope is configured the role/scope check is disabled (authenticated caller is enough).

## Deployment / config requirements

1. Run EF migrations (`AddYardScheduleIngestion`) — creates `YardEvents` + `YardScheduleInputs`.
2. Register Yard's managed identity with the `YardEvents.Ingest` app role on the LTL API app
   registration (or configure `YardIngestion:RequiredScope` for a delegated caller).
3. No other config needed; defaults accept `yard-control` v1 events.
