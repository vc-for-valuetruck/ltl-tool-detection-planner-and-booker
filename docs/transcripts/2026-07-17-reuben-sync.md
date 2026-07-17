# Alvys × Value Truck — Weekly API Project Dev Sync

**Date.** 2026-07-17
**Participants.** Reuben Sheyko (Alvys), Joshua Davis (Value Truck)
**Purpose.** Weekly LTL API sync. Ended up covering the entire prep sheet
Section 1 (writeback blockers) plus MCP.

> This is the auto-generated Google Meet transcript, saved verbatim (light
> punctuation cleanup only) so any decision drawn from it in
> [`docs/ALVYS_API_DECISIONS.md`](../ALVYS_API_DECISIONS.md) can be traced back
> to the exact words that produced it.

---

## Ten headline findings (see decisions doc for source-tied write-ups)

1. Public API is **read-only** — that's the ceiling, not just a beta gate.
2. Writes go through the **internal API** the Alvys UI itself calls; Reuben
   sanctioned this path.
3. Internal API requires **an active user session token**, not a
   client-credentials token.
4. MCP sits in front of the Public API → MCP inherits the read-only ceiling.
5. No customer-level `AllowConsolidation` field exists. Use `notes` with a
   convention (returned on the customer API response).
6. `dispatch_miles` (driver-facing) vs `customer_miles` (billing) — zero
   `dispatch_miles` on children only. Parent keeps all miles.
7. Waypoint stop details **must be duplicated** on the parent — no auto-inherit.
8. Trip references are supported; not sure which endpoint (load / trip / stop
   references) actually surfaces them — needs testing.
9. Same driver/truck/trailer across siblings just works if the tenant
   "one driver per trip" setting is OFF.
10. Corridor scoping uses **webhooks**, not query params. `trips` endpoint is
    where origin/destination live; stops carry coordinates.

---

## Verbatim transcript

### 00:00 — Waypoint creation via internal API

**Reuben:** For the waypoint for the public API, we don't expose that to be able
to create waypoints. But what you can do potentially is scrape the internal API
endpoint for basically you can go in create a waypoint and then look in your
network tab to see what the internal endpoint is for that. The only thing if
you're going to use internal API endpoints, the public API credentials won't
work for the internal API endpoints. But if you have like uh if you have a
user, right, you can also scrape the bearer token and use that to go against
the internal API endpoint.

**Joshua:** And that will allow me to effectively post a waypoint successfully.

**Reuben:** Yeah.

### 01:11 — Public API can only read

**Reuben:** I've been keeping you guys on track on the public API endpoints,
but it's a little bit tricky for what you're trying to do because the public
API endpoints only let you read things, right?

**Joshua:** Yeah, that was my next follow-up — how am I going to do these
writes?

**Reuben:** If you wanted to look into utilizing the internal API endpoints —
it might be a little bit tricky because you would always have to have an
active session token from a user that signed in in order to authenticate the
internal API endpoint. But if you're able to figure that out, you can literally
do whatever you want to do. Meaning updates, creation, deletion, all of that
stuff.

**Joshua:** And when you say the internal endpoints, are you referring to the
endpoints that I'm building out to support the functionality or you guys have
a set of internal?

**Reuben:** Basically what the system runs off of.

### 02:13 — MCP first mention

**Reuben:** By the way, if you're using Claude or any other AI, we last week
just released Alvys MCP. So if that's — Yeah, last week we released Alvys MCP.
It's kind of a secret still, but if you need access to it, I could get you
access to it.

**Joshua:** Yes, please, my friend. Yes, please.

**Reuben:** If we could just kind of if you want to do like a little
walkthrough as to what you're referring to, that would be great.

### 03:07 — Network-tab demo of the internal endpoint

**Reuben:** So I'll go inside of load and I'm going to right-click, inspect,
open up a network tab. And then let's say I want to find out how do I create
a waypoint? So basically in the UI I would go to add stop → waypoint, just put
any address in here, date, and then click add stop. As soon as I do that, let's
look in our network tab and let's see what was this. This is darkly. Oh, here
we go. **Add extended stop.** Right. So I can see here that this is the
internal endpoint that triggers that. So, if you have — and you can see like
this is my bearer token. I'm not going to show the full thing, but each user
has this token.

**Reuben:** You can reuse this token to hit this endpoint, right? And
technically outside of the system to trigger these actions.

**Joshua:** It doesn't expire?

**Reuben:** It does expire — that's why you need an active session basically.

**Joshua:** I'll just have a dedicated method or something that handles that.

**Reuben:** It's a little bit nontraditional what I'm telling you right now,
usually — the only reason I'm telling you because it's I know how hard it is
right now for you to figure all this out to make it work without being able
to do all these things.

### 05:09 — MCP portal activation

**Reuben:** For MCP we have to activate it per portal so it's not currently
activated in your portal. This is also beta. The server is read — the write
tools are still disabled.

**Joshua:** All in production with production data?

**Reuben:** Yeah, you're hitting the internal API endpoint. So these internal
API endpoints — so see I did a post. I could go outside of the system and as
long as I hit this endpoint with the correct authentication token that I have
here with the correct payload, I could have created this way stop that it did
in the UI outside of the system and it would create it here in the system.

### 12:28 — Waypoint stop details do not auto-inherit

**Joshua:** I need to know whether the waypoint is going to auto-inherit the
siblings' stop details or we have to duplicate them.

**Reuben:** So you're going to be creating waypoints on one of the main loads,
right? And when you say duplicate — you mean duplicate a stop as a main load?

**Joshua:** Yes.

**Reuben:** Yeah. So you're basically going to be duplicating the stops that
are not on the main load into the main load as waypoints.

### 15:55 — dispatch_miles vs customer_miles

**Joshua:** On each child order, should we zero out miles in the dispatch
language panel? Or is there a load-update field name we can send or a
dedicated operation that sets loaded miles to zero without side effects to
driver-pay calculations that predate the change?

**Reuben:** Oh yeah — and to be careful also, there's two mileages on a load.
There's customer mileage and dispatch mileage. You only want to zero out the
dispatch mileage because that's driver-facing and you only want to zero out
the miles towards the driver for the child loads. But for the parent load,
that's where you're going to keep all the miles which they will automatically
calculate once you add the waypoints and the driver will be getting paid based
of those miles because the customer mileage is like for the customer. So it's
going towards billing and those miles are accurate for that customer but not
for the driver.

**Joshua:** The exact field name in the load-update payload — dispatch miles?

**Reuben:** Dispatch.

### 18:18 — LTL trip references, main-load-id references

**Joshua:** The parent gets a boolean true/false LTL trip reference, and then
each child gets a main-load-id reference pointing at the parent number. Are
these first-class trip references in the API or user-defined reference values?

**Reuben:** Trip references should be in the API. It is a little bit tricky —
let me check here really quick something. They used to be — let me check if
they fixed it. Might have to test this. On the trip endpoint we do have stop
references. So there's like load references, trip references, and stop
references. But I don't remember if the trip references show up when you hit
the stop references endpoint or when you hit the load references endpoint.
That's what I don't remember.

**Joshua:** I can just test both and let you know. But you pointed me in the
right direction, so that's all I needed.

**Joshua:** The reference data type — LTL is a boolean, main load id is a
string. Is that expressible in the API or do we string-encode both?

**Reuben:** Parameter ID string. Yeah.

**Joshua:** So there's no writes going on right now, correct — if I go this
route?

**Reuben:** For the public API endpoint. Yeah, there's no write for that one.

### 24:19 — Concurrent same-driver assignment across sibling trips

**Joshua:** Do we assign the same driver, same truck, same trailer across the
parent and the siblings? Our trip-assign operation in the sandbox writeback
works one trip at a time. Is there a bulk or a multi-trip assign operation, or
should we chain single trip-assign calls with an idempotency-key group?

**Reuben:** Um, now we do have like a setting that can be turned on where you
can only allow one load or trip to be dispatched to the same driver. But as
long as that's turned off, you can assign the same driver, truck, and trailer
on the parent and the child and dispatch them all.

**Joshua:** So then the endpoint won't reject the concurrent trip assignment
for the same drivers across overlapping trips?

**Reuben:** I mean, they will be kind of overlapping, but the system won't
block them.

### 27:00 — Corridor scoping = webhooks + trips endpoint

**Joshua:** Given an origin geohash / city / warehouse id plus a destination
geohash / city / warehouse id + a status filter, is there a load search that
returns only corridor-matching loads server-side?

**Reuben:** Corridor matching points — are you referring to when you have a
webhook set up?

**Joshua:** I think so. The public API is not doing all this. So I think that's
what I am referring to.

**Reuben:** If anything, take a look at the webhooks in the documentation.

**Joshua:** Load search request — does it support the origin and destination
pair filter, or can I only do one or the other?

**Reuben:** Your safer bet is to do the trips endpoint. It contains the pickup
like the stops. So the origin destination — the trip will contain that data
for sure.

**Joshua:** What about geographic radius — X miles from a warehouse?

**Reuben:** On the warehouse, well, we do store coordinates. You can pull
coordinates for stops.

### 30:51 — No customer-level LTL flag; use notes

**Joshua:** Is `AllowConsolidation` or an equivalent per-customer flag
something you guys store at the customer record, or does every carrier
maintain their own outside the API?

**Reuben:** In Alvis we don't have a way to flag that. And we currently — in
the future I think we're going to add — but we currently don't have any custom
references that you can set up where you can use a custom reference to specify
if a customer is LTL or not. One way you can do it probably is, if I go into
a customer profile, there's probably certain fields you can utilize if they're
being unused. For instance, there's like general or internal notes section.
You can store different notes and one of them you can utilize is customer
equals LTL true or something like that.

**Reuben:** Let me just verify. If you pull a customer profile, the notes come
out. Yep. The notes are in the response on the endpoint in the public API. So
you can store data there.

### 33:06 — Driver RPM formula

**Joshua:** Combined RPM in relation to LTL — do you know what that
relationship even looks like?

**Reuben:** Well, RPM is a rate per mile. So do you know if they're looking
for the customer RPM or the driver RPM?

**Joshua:** Driver.

**Reuben:** Driver RPM. So you take the driver's rate and then divide it by
the mileage.

### 34:37 — Trip rate: stored, editable

**Joshua:** For an individual trip like a $0 per mile value that shows up
before consolidation — is that a stored value or is it computed every time?

**Reuben:** So the rate on the trip, I mean, it can change like if a user goes
in and changes it, it'll update and change, but it gets stored in there.

### 36:00 — Wrap

**Joshua:** I think I've asked you all the questions I need to today. But I
would love to get that MCP set up.

**Reuben:** Sounds good.

---

## Transcript end marker

Transcription ended after 00:37:14. Editable transcript, computer-generated.
Text was lightly punctuated for readability; the substance was not changed. If
any decision cites this transcript, the timecode above is the source of truth.
