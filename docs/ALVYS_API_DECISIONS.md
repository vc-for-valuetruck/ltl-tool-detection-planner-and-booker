# Alvys API — Decisions Log

Durable engineering decisions about how the LTL tool interacts with Alvys. Every entry cites the
transcript or doc it came from so a future PR can trace the reasoning back to source.

Newest at top. When a decision is superseded, mark it and link forward.

---

## 2026-07-17 (evening) · Empirical findings from live MCP calls

**Source.** Three tool calls executed via the Perplexity Alvys MCP connector against the live
va336 tenant on 2026-07-17. Real payload shapes captured from real load / trip / customer
records, not documentation.

### Finding 1 — Load reference shape (answers pending row in decision #8)

**Schema of load references:** `Load.References[]` on the loads endpoint, one entry per reference:

```jsonc
{
  "Id":  "<guid>",           // record id of this reference row
  "ReferenceId": "<guid>",   // reference-type id (optional; present for managed types)
  "Name": "Method of Payment",   // reference type name
  "Value": "Prepaid (by Seller)",
  "Type": "Text",            // seen values: "", "Text", "List"
  "Access": "Public",
  "Origin": "EDI"            // seen: "Manual", "EDI", "Unknown"
}
```

**Where each reference kind actually lives (Reuben was unsure at 18:18 in the transcript):**
- **Load-level references** — `Load.References[]` on `loads_search` / `loads_get_by_id`.
- **Stop-level references** — `Load.Stops[].References[]` (nested per stop on the load).
- **Trip-level references** — `Trip.References[]` on `trips_search` / `trips_get_by_id`, and
  `Trip.Stops[].References[]` for stop references seen from the trip side.

**Consequence for Phase 5.** The `LTL` boolean on the parent goes on `Trip.References[]`
(not `Load.References[]`). The `Main Load Id` string on each child goes on the child
trip's `Trip.References[]`. Both are written the same way once the internal endpoint that
adds a reference to a trip is discovered.

### Finding 2 — The "loaded miles" field is `Trip.LoadedMileage.Distance.Value`

Zeroing dispatch miles on a child (Reuben, 15:55 in the transcript) writes to the trip, not
the load:

```jsonc
"LoadedMileage": {
  "Distance": { "Value": 1059.0, "UnitOfMeasure": "Miles" },
  "Source": "Engine",
  "ProfileName": "PCMiler"
}
```

Also present on the trip:
- `Trip.TotalMileage.Distance.Value` (parent stays populated; this is what Reuben meant by
  "the driver gets paid based off those miles because they auto-calculate once you add the
  waypoints").
- `Trip.EmptyMileage.Distance.Value`.

**Consequence for Phase 5.** When executing a consolidation plan, the internal-API write
for "zero out child miles" sets `Trip.LoadedMileage.Distance.Value = 0` on each child trip.
`Load.CustomerMileage` is untouched — that's the billing number and stays accurate for the
customer, matching Reuben's guidance.

### Finding 3 — Driver RPM = `Trip.TripValue.Amount / Trip.LoadedMileage.Distance.Value`

Confirms decision #12 formula but names the exact fields:

```jsonc
"TripValue": { "Amount": 5632.5, "Currency": 840 },
"LoadedMileage": { "Distance": { "Value": 1059.0, "UnitOfMeasure": "Miles" }, ... }
```

So for that live load (Vertiv Mexico → Olive Branch): `5632.5 / 1059 = $5.32/mi` driver RPM.

**Consequence.** The `ConsolidationPlanService`'s combined-RPM computation:

```csharp
combinedRpm = (parentTripValue + sum(siblingTripValues))
            / parentLoadedMileage;
```

Where sibling `LoadedMileage` values are 0 after Phase 5 zeroing.

### Finding 4 — `Customer.Notes[]` is a real field on `customers_get_by_id`

Confirms decision #10 empirically:

```jsonc
{
  "Id": "...",
  "Name": "Value Logistics",
  "Status": "Active",
  ...,
  "Notes": []           // empty here, but the field is on the response schema
}
```

The schema slot is present regardless of whether the customer has notes. The
`CustomerNotesLtlPolicyReader` proposed in decision #10 will parse this array
for `LTL_ALLOW=true`, `LTL_TIER=Allowed|NotifyRequired|Never`, `LTL_NOTIFY=<email>`
lines when it's built in Phase 3.

### Finding 5 — Value Truck currently has 492 open loads in production

For reference / scale-check when reasoning about Phase 5 rate limits. `loads_search` with
`status=Open` returned `Total: 492` across paging. Corridor scoping (Laredo→Dallas) will
filter this down to a much smaller candidate set client-side; the API doesn't offer
server-side corridor filtering (decision #11).

---

## 2026-07-17 (later) · MCP proven live for Value Truck (`org_id=org_4C3HR7pSPWcXWkuo`, `org_name=va336`)

**Source.** Live `tools/list` call against `https://mcp.alvys.com/mcp` using the LTL tool's
existing `ALVYS_CLIENT_ID` + `ALVYS_CLIENT_SECRET`. HTTP 200, 40 tools returned.

**Decision.** MCP is proven working end-to-end for our tenant. Reuben's portal-activation
cleared and the Auth0 organization on the M2M app resolves cleanly. Client configs (Claude
Desktop, Cursor, Claude Code CLI) are ready to receive the bearer token.

**Key claim from the token JWT (for the record):**
- `iss=https://auth.alvys.com/`, `aud=https://api.alvys.com/public/` (with trailing `/public/`)
- `client_name=public-api-va336-freightdnaprod` (prod, not sandbox)
- `org_id=org_4C3HR7pSPWcXWkuo`, `org_name=va336`
- 69 scopes on the token, including all 14 required MCP read scopes plus every write scope
- 1-hour TTL

**Tool catalog surprise.** The live server returns **40 tools** vs. the ~30 the docs page
listed at the time this decision was written. Includes a real `trips_update_stop_appointment`
split out from the generic `trips_update_stop_status`, plus `trips_record_arrival` and
`trips_record_departure` as separate tools. Alvys is iterating on the server; treat the docs
page as approximately-current, not authoritative.

---

## 2026-07-17 (later) · `trips_assign` IS on the MCP surface — partial correction to "internal API only"

**Source.** Same live `tools/list` call above.

**Decision.** The earlier decision "Public API is read-only forever; writes go through the
internal API" was **partially wrong**. Corrected picture:

| Phase 5 write | Public API via MCP? | Internal API only? |
| --- | --- | --- |
| Waypoint creation (`add-extended-stop`) | ❌ | ✅ (per Reuben) |
| Zero `dispatch_miles` on child load | ❌ (no `load-update` on MCP) | ✅ |
| `LTL` boolean + `main_load_id` trip references | ❌ (no reference-write tool on MCP) | ✅ |
| `trips_assign` (driver / truck / trailer) | ✅ **exists** — gated by `Mcp:EnableWriteTools` (off during beta) | Reuben-sanctioned path also works |
| Stop arrival/departure (`trips_record_arrival`, `trips_record_departure`) | ✅ **exists** — same gate | — |
| Stop appointment update (`trips_update_stop_appointment`) | ✅ **exists** — same gate | — |

**Consequence.** Phase 5 has a **branching strategy**:
- Waypoint, mileage zeroing, and reference writes still route through the internal API
  (`AlvysInternalWriteClient` per the previous decision, unchanged).
- `trip-assign` and stop status recording can route through MCP once Alvys lifts
  `Mcp:EnableWriteTools`. In the meantime, the Reuben-sanctioned internal path is the fallback.
- The `IAlvysWriteClient` abstraction stays useful — it just gains a second implementation
  (`AlvysMcpWriteClient`) alongside the Reuben-internal one. Selection happens per operation.

**Follow-up.** Update the Phase 5 section of `ROADMAP.md` when we actually start Phase 5
coding, not now — the beta gate on `Mcp:EnableWriteTools` may lift before we ship, and the
selection logic depends on which is available when we're actually cutting code.

---

## 2026-07-17 · Public API is read-only forever; writes go through the internal API

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 00:00 and 01:11.

> "For the waypoint for the public API, we don't expose that to be able to create waypoints. …
> The public API endpoints only let you read things, right?"

**Decision.** Treat the Alvys **Public API** as read-only for the lifetime of the LTL tool. All
writes (Waypoint creation, mileage updates, references, trip-assign) go through the **internal
API** — the endpoints the Alvys web app itself calls.

**Consequence.** The current writeback slice under
`src/LtlTool.Api/Features/Integrations/Alvys/Writeback/` is aimed at the Public API, which
cannot ever fulfill it. The slice needs a new client class targeted at the internal API before
Phase 2 can ship any writes.

**Not affected.** `AlvysClient` (Public API reads) stays exactly as-is. Everything the LTL tool
reads today continues to work.

---

## 2026-07-17 · Internal API auth requires an active user session, not client-credentials

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 01:11 and 03:07.

> "You would always have to have an active session token from a user that signed in in order to
> authenticate the internal API endpoint."
> …
> "You can reuse this token to hit this endpoint. Technically outside of the system to trigger
> these actions."
> …
> "It does expire — that's why you need an active session basically."

**Decision.** The internal API accepts **an Alvys user's Auth0 bearer token** (obtained by
signing that user into the Alvys web UI), not the machine-to-machine client-credentials token
the Public API + MCP accept. The session token expires; the LTL tool must hold a valid one for
every write.

**Implementation direction (not final; needs its own PR + review).**

- Add a new **`AlvysInternalTokenProvider`** that holds a session token per acting user.
- Refresh path is undocumented — Reuben confirmed the token expires but not how to refresh it
  without re-signing-in. First internal-API PR must include a `token_expired` failure test that
  verifies the tool surfaces a clean error to the operator rather than silently retrying.
- Consider a **dedicated Alvys user account** ("valuetruck-ltl-tool") whose session is refreshed
  by a small headless-login helper. Requires Alvys account rep to provision.
- Under no circumstances share a real dispatcher's token — the audit trail would attribute LTL
  tool writes to that human.

**Anti-failure map coverage.** 3l (Operator hand-off). If the token expires mid-plan the tool
must not half-execute a consolidation.

---

## 2026-07-17 · Reuben-sanctioned discovery pattern for internal endpoints

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 03:07.

> "Right-click, inspect, open up a network tab. Basically in the UI I would go to add stop →
> waypoint, put any address in, date, and then click add stop. Look in our network tab. Add
> extended stop. This is the internal endpoint that triggers that."

**Decision.** The canonical way to discover an internal-API endpoint is Alvys UI → Network tab
→ perform the action → capture path + payload + response. Reuben approved this workflow
verbatim. Document every internal endpoint the tool uses in this file (below, under
"Discovered internal endpoints") so future contributors don't have to re-discover them.

**Guardrail.** Because the internal API is undocumented, treat any behavior we depend on as
subject to change. Every internal-endpoint call site needs:

1. A regression test that fails loudly (not silently) when the endpoint 4xx/5xx's differently
   than the recorded snapshot.
2. A record in this decisions log of when the endpoint was last verified to work.
3. A CLAUDE.md note next to "Alvys writeback boundary" that reads: "Internal API endpoints are
   observed, not contracted. Break-glass posture: on any 500 or unexpected shape, halt the
   plan and surface a legible error — never partial-execute."

### Discovered internal endpoints

_Add rows as we verify each._

| Action | Method | Endpoint (as observed) | Last verified | PR |
| --- | --- | --- | --- | --- |
| Add Waypoint to a load | POST | `add-extended-stop` (root path TBD in first PR) | 2026-07-17 (Reuben demo) | pending |
| _Zero `dispatch_miles` on a child load_ | _TBD_ | _TBD_ | _pending capture_ | pending |
| _Create trip reference (LTL boolean on parent)_ | _TBD_ | _TBD_ | _pending capture_ | pending |
| _Create trip reference (main load id on child)_ | _TBD_ | _TBD_ | _pending capture_ | pending |
| _Trip-assign (driver + truck + trailer)_ | _TBD_ | _TBD_ | _pending capture_ | pending |

---

## 2026-07-17 · MCP inherits the Public API read-only ceiling

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 05:09,
plus [`docs/mcp` on Alvys' side](https://docs.alvys.com/docs/mcp).

**Decision.** Alvys MCP sits in front of the Alvys Public API. Because the Public API is
read-only for the LTL tool's use cases (previous decision), **MCP cannot fulfill Phase 2
writes**. MCP is:

- A useful **read** surface for AI clients (Claude Desktop, Cursor, Perplexity Computer).
- **Not** a replacement for the internal-API writeback path.

This corrects [`docs/ALVYS_MCP_SETUP.md`](ALVYS_MCP_SETUP.md) — that document's "Should the LTL
tool itself call MCP?" section is right by conclusion but wrong by rationale (it assumed writes
would show up in prod when the beta lifts).

**Follow-up PR.** Update `docs/ALVYS_MCP_SETUP.md` to reflect that MCP is architecturally
read-only for this tool's writes, not just beta-gated.

---

## 2026-07-17 · MCP portal activation is per-tenant, not yet on for Value Truck

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 05:09.

> "For MCP we have to activate it per portal so it's not currently activated in your portal."

**Decision.** Do not test MCP connectivity until Reuben activates the Value Truck portal.
Every tool call before activation returns 401. This is the same "beta enrollment" gate the
public MCP docs describe.

**Action.** Josh to confirm activation with Reuben; then the two prerequisite steps in
`docs/ALVYS_MCP_SETUP.md` (beta enrollment, Auth0 org assignment) can be verified together.

---

## 2026-07-17 · Waypoint stop details must be duplicated onto the parent — no auto-inherit

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 12:28.

> "You're basically going to be duplicating the stops that are not on the main load into the
> main load as waypoints."

**Decision.** When the tool executes Poornima's step 3 (Add stop → Waypoint), the payload the
internal endpoint receives must contain the full stop details from the sibling load — address,
scheduled window, contact, references. The Waypoint does not auto-populate from a sibling load
number.

**Consequence.** The `ConsolidationPlanService` already resolves sibling stops from Alvys as
part of the plan preview. Phase 2 needs to serialize those stop details into the internal-API
Waypoint payload rather than send a reference.

**Failure mode to avoid.** The Waypoint gets created with an incomplete address (Alvys accepts
it) and the driver arrives at the wrong stop. Every Waypoint write needs a client-side
validation that all address fields are present before the call fires.

---

## 2026-07-17 · Zero `dispatch_miles`, never `customer_miles`

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 15:55.

> "There's two mileages on a load. There's customer mileage and dispatch mileage. You only want
> to zero out the dispatch mileage because that's driver-facing and you only want to zero out
> the miles towards the driver for the child loads. But for the parent load, that's where
> you're going to keep all the miles."

**Decision.** For every child load in a consolidation plan, the tool zeros **only**
`dispatch_miles`. `customer_miles` is left alone. The parent load's `dispatch_miles` is not
touched (Alvys auto-recalculates once Waypoints are added).

This is a critical correction to what the prep sheet asked. My prior question named three
candidate fields (`loadedMiles`, `dispatchMiles`, `linehaulMiles`). The answer is
`dispatchMiles` explicitly, and there is a second field `customerMiles` that must be preserved.

**Payroll triple-pay guardrail (anti-failure map 3e).** Zeroing `dispatch_miles` is what
prevents the double-pay. That is the payroll-facing number. Confirming this field also confirms
the safety story: touch only that field, never `customer_miles`.

**Consequence for the click-card generator today.** The current click card in
[`ConsolidationClickCardBuilder.cs`](../src/LtlTool.Api/Features/Ltl/Consolidation/ConsolidationPlanService.cs)
should be updated to name the exact panel field `dispatch miles` rather than the ambiguous
"loaded miles" the yard-visit notes used.

---

## 2026-07-17 · Trip references are supported; discovery pending

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 18:18.

> "Trip references should be in the API. It is a little bit tricky — let me check. They used to
> be — let me check if they fixed it. Might have to test this. On the trip endpoint we do have
> stop references. So there's like load references, trip references, and stop references. But I
> don't remember if the trip references show up when you hit the stop references endpoint or
> when you hit the load references endpoint."

**Decision.** Trip references (both the `LTL` boolean on parent and the `main_load_id` string
on child) are real Alvys entities. The exact endpoint that returns them is not confirmed —
Reuben told us to test all three of: load-references, trip-references, stop-references.

**Reference value type.**

> **Joshua:** The main load id is a string. Is that expressible in the API or do we
> string-encode both?
> **Reuben:** Parameter ID string. Yeah.

So both references are transported as strings on the wire. `LTL` is `"true"` / `"false"`, not
a native boolean.

**Action.** A read-only discovery PR should test all three endpoints against a load that has
LTL and main-load-id references set in the UI, capture which endpoint returns them, and land
that answer in the "Discovered internal endpoints" table above.

---

## 2026-07-17 · Same driver / truck / trailer across siblings just works

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 24:19.

> "We do have like a setting that can be turned on where you can only allow one load or trip to
> be dispatched to the same driver. But as long as that's turned off, you can assign the same
> driver, truck, and trailer on the parent and the child and dispatch them all."
> …
> "The system won't block them."

**Decision.** Value Truck's tenant "one driver per trip" setting must be **OFF** for
consolidation to work. Confirm current state with Reuben; once off, the tool can chain single
`trip-assign` calls back-to-back without any override.

**Action.**

1. Confirm the setting is OFF for Value Truck production.
2. Add a Phase 2 readiness check that reads the setting via the Public API (or Admin API if
   exposed) and fails the plan if it's back ON.
3. No bulk / multi-trip-assign is needed. Chain single calls with an idempotency-key group so
   we can distinguish "assigned all N" from "assigned M<N".

---

## 2026-07-17 · No customer-level LTL flag exists; use the `notes` field

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 30:51.

> "In Alvis we don't have a way to flag that. And we currently — in the future I think we're
> going to add — but we currently don't have any custom references that you can set up where
> you can use a custom reference to specify if a customer is LTL or not."
> …
> "For instance, there's like general or internal notes section. You can store different notes
> and one of them you can utilize is customer equals LTL true or something like that."
> …
> "If you pull a customer profile, the notes come out. Yep. The notes are in the response on
> the endpoint in the public API. So you can store data there."

**Decision.** There is no first-class `AllowConsolidation` field on the Alvys customer record.
Store the value in the customer's `notes` field using a machine-parseable convention.

**Convention (this repo's proposal).** In the customer's Internal Notes, add one line per LTL
flag:

```
LTL_ALLOW=true
LTL_TIER=Allowed | NotifyRequired | Never
LTL_NOTIFY=alice@company.com
```

Anything else in the notes field is a normal human note and must be ignored by the parser.
Missing lines default to `LTL_TIER=Unknown → confirm with account owner`, never silent-allow.

**Consequence for the LTL tool today.** The
`ConsolidationCandidateService`'s customer-tier check currently reads a static config file.
Phase 2 replaces that with a `CustomerNotesLtlPolicyReader` that parses the customer's notes
field and falls back to the static config as a default when a customer has no LTL_* line.

**Follow-up when Alvys adds the real field.** Reuben said this is on Alvys' roadmap. When it
lands, deprecate the notes convention and read the real field. The `Ltl:CustomerPolicy:Source`
option will let us switch reader implementations without changing calling code.

---

## 2026-07-17 · Corridor scoping = webhooks + trips endpoint, not query params

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 27:00.

> "**Joshua:** Given an origin geohash / city / warehouse id plus a destination geohash / city
> / warehouse id + a status filter, is there a load search that returns only corridor-matching
> loads server-side?"
> "**Reuben:** Corridor matching points — are you referring to when you have a webhook set up?"
> "**Reuben:** If anything, take a look at the webhooks in the documentation."
> "**Reuben:** Your safer bet is to do the trips endpoint. It contains the pickup like the
> stops. So the origin destination — the trip will contain that data for sure."
> "**Reuben:** On the warehouse, well, we do store coordinates. You can pull coordinates for
> stops."

**Decision.** For real-time corridor-scoped candidate discovery:

1. Subscribe to Alvys **webhooks** so new / updated loads push into the LTL tool.
2. Filter incoming webhook events by corridor client-side using the **trips** endpoint (not the
   loads endpoint) — origin and destination live on the trip's stops.
3. Use the coordinates on each stop for radius filtering (X miles from the Laredo / Dallas
   warehouse).

**Consequence.** The current `ConsolidationCandidateService` scans loads and applies the
corridor filter client-side. That's fine for Phase 1 pilot volumes but hits the scan bound on
busy days (we surface `scanTruncated=true`). Phase 3+ replaces the scan with:

- Webhook ingestion → durable event log.
- Trip-endpoint lookup (not load-endpoint) to resolve origin/destination.
- Client-side coordinate-radius filter against the warehouse coordinates.

**Not urgent for Phase 1.** The pilot's scan-truncation banner is honest to the operator. This
is a Phase 3 optimization, not a Phase 2 blocker.

---

## 2026-07-17 · Driver RPM = driver rate ÷ mileage

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 33:06.

> "RPM is a rate per mile. So do you know if they're looking for the customer RPM or the
> driver RPM?"
> "**Joshua:** Driver."
> "**Reuben:** So you take the driver's rate and then divide it by the mileage."

**Decision.** When the LTL tool displays "combined RPM" on the plan preview, the formula is
**driver rate ÷ mileage**. That's the operator-facing number Junior and Holly look at.
`customer_rate ÷ mileage` is the billing number and is not what the yard-visit anecdote of
`$5/mi combined` referred to.

**Consequence for the plan preview.** Confirm the current `ConsolidationPlanService`
combined-RPM computation uses driver rate + dispatch miles. If it uses customer rate, fix in a
follow-up. This is the difference between showing an inflated billing number and the actual
number the driver-pay-based commission structure will pay against.

---

## 2026-07-17 · Trip rate is stored, editable, not computed

**Source.** [Reuben sync transcript, 2026-07-17](transcripts/2026-07-17-reuben-sync.md), 34:37.

> "The rate on the trip, I mean, it can change like if a user goes in and changes it, it'll
> update and change, but it gets stored in there."

**Decision.** Trip rates are stored values, not computed at read time. When the tool caches a
trip's rate to compute combined RPM, that cache is valid until either (a) the tool's TTL
expires or (b) a user edits the rate in Alvys. Webhook subscription (previous decision) is the
right way to catch the "user edited the rate" event and invalidate.

---

## Superseded / obsolete decisions

_None yet. When a Phase 2 or 3 change invalidates an entry above, replace this section with a
list of superseded entries pointing at the newer decisions._
