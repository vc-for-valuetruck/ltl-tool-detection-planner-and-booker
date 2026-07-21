# ltl-tool.md — Alvys Production Writeback Decision Record

This file tracks the decision to extend Alvys writeback from **sandbox** to a **live
production tenant**. It exists separately from `CLAUDE.md` so the production-writeback
approval can be recorded, reviewed, and updated on its own without editing the
project's core instruction set. `CLAUDE.md` remains the source of truth for the safety
principle this record implements:

> Keep Alvys writeback gated and explicit. Writeback defaults to Disabled and is
> config-gated... Flipping the mode alone can never reach a live/production tenant.
> Do not claim live booking/writeback to Alvys unless the supported write API contract
> is confirmed and implemented safely. Do not create a fake booking path.

## Current state (as built)

Sandbox writeback is fully implemented, not a stub:

- The operations in `AlvysWriteOperationRegistry`
  (`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/AlvysWriteOperations.cs`) are
  `AlvysLiveSupport.Supported` — `create-load-note`, `tender-accept`,
  `trip-stop-arrival`, `trip-stop-departure`, `load-update` (only `OrderNumber`
  writable), `trip-assign`, `trip-dispatch`, `carrier-status-update`, and the
  2026-07-21 Public-API document/invoice writes `upload-load-document`,
  `upload-trip-document`, `create-carrier-invoice` (the last additionally flag-gated by
  `Alvys:Writeback:EnableCarrierInvoice`). The document/invoice uploads send
  multipart/form-data via the Public-API client-credentials transport and never place
  file bytes in the outbox, logs, or idempotency hash.
- `AlvysHttpWriteClient`
  (`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/AlvysWriteClient.cs`) sends
  real HTTP requests (POST/PUT/PATCH per operation) with bearer auth, `If-Match` for
  ETag-gated operations, idempotency-safe body construction, and status-only logging
  (never bodies, which can echo secrets).
- Execution requires `AlvysWriteOptions.Mode = Sandbox`, which is refused unless
  `Environment` is one of `sandbox`/`uat`/`staging`/`test` **and** `SandboxBaseUrl` is
  set **and** does not point at `integrations.alvys.com`
  (`AlvysWriteOptions.HasSandboxBaseUrl`,
  `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/AlvysWriteOptions.cs`).
- Every write is idempotency-keyed and recorded to an outbox
  (`AlvysOperationOutbox`/`AlvysOperationRecorder`) before/after execution, storing no
  secrets.

**Today, production execution is not just disabled by a flag — it is architecturally
unreachable.** `AlvysHttpWriteClient.CreateSandboxClient()` only redirects the HTTP
client to `SandboxBaseUrl`, and `HasSandboxBaseUrl` explicitly rejects the production
host string. There is no code path that sends a write to `integrations.alvys.com`.

## What has to be true before production writeback is enabled

All of the following must be satisfied and recorded below — not assumed — before any
change is made to let a write reach a production Alvys tenant:

1. **Confirmed write contract.** For each operation intended for production, a link or
   copy of the official Alvys documentation (endpoint, verb, request/response schema,
   auth scope) confirming it is supported for production use, not just sandbox.
2. **Explicit business sign-off.** Named approver(s), date, and which specific
   operations are approved — approval must be per-operation, not blanket ("all
   writeback") since each operation has a different blast radius (e.g. `tender-accept`
   commits Value Truck to a load; `create-load-note` does not).
3. **Production gating mechanism.** A second, independent gate beyond the existing
   `Mode`/`Environment`/`BaseUrl` check — e.g. a distinct `AllowProduction` flag that
   itself requires the base URL to equal the real production host (inverting today's
   rejection), so enabling production is a deliberate, reviewable config change and not
   a side effect of relaxing sandbox checks.
4. **Reconciliation job in place** (see below) so every production write is verified
   against what Alvys actually recorded, not just "the HTTP call returned 200."
5. **Rollback/incident plan.** What happens operationally if a production write is
   wrong (e.g. wrong carrier tendered, wrong trip dispatched) — who is notified, how it
   is corrected in Alvys.

## Sign-off log

| Operation | Contract confirmed (link/doc) | Approved by | Date | Production gate implemented |
|---|---|---|---|---|
| `upload-load-document` | Public API `POST /loads/{loadNumber}/document` (docs.alvys.com, verified 2026-07-21; see [ALVYS_API_DECISIONS.md](./ALVYS_API_DECISIONS.md)) | _(pending)_ | | No |
| `upload-trip-document` | Public API `POST /trips/{tripId}/document` (docs.alvys.com, verified 2026-07-21) | _(pending)_ | | No |
| `create-carrier-invoice` | Public API `POST /invoices/carrier-invoice` (docs.alvys.com, verified 2026-07-21) | _(pending)_ | | No |

No filled-in row above means no operation is approved for production. The rows for the
2026-07-21 document/invoice operations record that Alvys **contracted** the endpoints, but
the contract confirmation is not the same as business sign-off: production execution stays
off (the operations run sandbox/audit-only today) until the Approved-by/Date columns are
filled AND the independent production gate (item 3) exists in code. Do not implement
production execution for an operation until its row is complete.

## Post-write reconciliation (sandbox now, reusable for production)

Buildable today against sandbox, independent of the production decision above:

- After an operation executes (success or failure), re-fetch the affected resource
  from Alvys (load, trip, or invoice depending on operation) and compare it against
  the outbox record's expected state.
- Surface mismatches (e.g. sandbox returned 200 but the load's status didn't change) as
  a reconciliation exception, not a silent pass — same "never coerce missing/wrong data
  to good" principle as the rest of the LTL tool.
- Track reconciliation state on the outbox record (`AlvysOperationOutbox`) so dispatch
  can see "pushed to Alvys" vs. "pushed but unconfirmed" vs. "confirmed" in the UI.

## Related files

- `CLAUDE.md` — safety principles this record implements.
- `docs/ALVYS_INTEGRATION.md` — full read/write API surface, including the
  `freight-dna` auth pattern this project's client is based on.
- `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/*` — implementation.
