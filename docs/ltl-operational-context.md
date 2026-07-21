# LTL knowledge base — Buckeye call + Jason demo framing (2026-07-20)

Source: owner's Buckeye office asset meeting notes + Jason executive-status analysis.

## Operational nexus (who drives UX)
- Direct nexus between people on the dock and their bosses re: UX. Dock workers = PDF people — the paper Level-1 inspection sheet is their native format. Digital input must be tap-simple; output must always render as a clean PDF they recognize.
- Dock workers will photograph everything during check-in/scanning; photos + completed inspection PDFs flow into the LTL tool (yard-artifacts intake). They are a huge determinant of how much LTL we can do (verified pallet counts/dims).
- Ben Beddes / Jordan Baumgart personas: monitor arrivals to spot LTL opportunities. Trucks/trailers arriving in Laredo each day = FIRST data on the LTL home page; must update on arrival and departure.
- Pass 1 mental model: Laredo → Dallas → destination(s) re-split.
- Fleet ownership: own trucks/trailers easy to track; one trailer set is 3P-owned and leased — ownership labels must be honest (Fleet / 3P-leased / Unknown).

## Jason's executive framing (what the tool IS)
- Decision-support workbench for dispatch and billing, NOT booking automation: search LTL candidates → explainable match recommendations → assignment preflight (blockers stop; warnings overridable with stated reason) → audit-only assignment record → billing readiness.
- Read-only Alvys intelligence; Alvys remains the operational source of truth. Honest scoring: unavailable data excluded from denominators; hard disqualifiers (inactive driver, expired license/medical, over-capacity) cap recommendation at "not recommended".
- Never claim: books freight in Alvys today; pushes assignments to Alvys; production-ready without UAT; complete Alvys data; billing automation replacing billing work.

## Recommended demo sequence (Jason)
/ltl goal statement → Search + saved views (views = dispatcher questions) → load detail + match factors → warning/blocker in assignment validation → audit-only boundary → Billing readiness/missing-data → sandbox/writeback posture panel as the future plug-in point.

## QA runway (Jason's estimates)
- Concept/demo review: ready now with decision-support framing.
- Internal technical QA: 2–4 business days after Azure/Entra/Alvys env values confirmed.
- Ops UAT (selected dispatch/billing users): 5–7 business days after technical QA starts + representative data.
- Production: gated on writeback decision, security review, UAT feedback, deploy hardening.
- Pre-QA dependencies: UAT URL/App Service config; Entra app registrations + redirect URI; Fallback vs Live Alvys provider decision; secrets as secrets; 2–3 representative load numbers (clean match / blocker case / billing-risk case); empty-state vs live-data decision for early testers.

## Questions to bring to stakeholders (from the pilot doc + call)
- Jordan: intake data-quality ETA; missing-data flag routed back to intake team?
- Junior: corridor radii right-sized? click-card behavior when dispatcher prefers a different driver?
- Holly: click-card field-name accuracy; copy vs print priority.
- Brandon (Dallas): re-split-side data needs; dock-door availability signal for Phase 2?
- Jose Skoog: customer relationships to filter out of the corridor.
- Jason: sign-off corridor-only Phase 1; click card as delivery mechanism; Alvys writeback conversation timeline.
