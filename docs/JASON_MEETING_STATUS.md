# Jason Meeting Status — LTL Tool Detection, Planner, and Booker

_Date: 2026-07-09_

## Executive status

The LTL Tool is the stronger of the two non-FreightDNA applications for a live concept walkthrough. It is not positioned as production-ready booking automation yet. It is positioned as a **decision-support workbench** for dispatch and billing: search LTL candidates, review match recommendations, validate assignment blockers/warnings, record an internal audit-only assignment decision, and evaluate billing readiness.

The safest message for Jason:

> FreightDNA is closest to QA. The LTL Tool is ready for a concept/demo review if we frame it correctly: read-only Alvys intelligence, explainable dispatch matching, audit-only assignment, and billing-readiness signals. It should not be represented as live booking/writeback automation yet.

## Demo framing

Use this as the opening narrative:

> The Buckeye trip and the follow-up conversations clarified that the value is not just showing another load grid. The value is helping a dispatcher or operations lead answer: what LTL freight should I look at first, why is this driver/equipment match recommended, what would block assignment, what can be billed, and where are we risking cash delay? This app packages that logic into a single workbench while keeping Alvys as the operational source of truth.

## What is implemented enough to demo

1. **LTL search workbench**
   - Normalized LTL search rather than a raw Alvys grid.
   - Saved workflow views such as unassigned LTL, high revenue / low complexity, today's pickup, this week's deliveries, missing billing data, ready to bill, and exceptions.
   - Filters for customer, lane, equipment, assignment state, date windows, billing badge, ready-to-bill, missing billing, and exception states.

2. **Explainable matching**
   - Match recommendations use scored factors rather than a black-box ranking.
   - Scoring is intentionally honest: unavailable data is excluded from the denominator instead of being treated as good or bad.
   - Hard disqualifiers such as inactive/terminated driver, expired license/medical, or over-capacity equipment cap the recommendation as not recommended.

3. **Assignment preflight and audit boundary**
   - The app validates proposed assignments before recording them.
   - Blockers prevent assignment; warnings can be overridden only with a stated reason.
   - Current assignment action is **internal audit only** and explicitly does **not** write back to Alvys.

4. **Billing readiness and revenue-protection signals**
   - Billing worklist surfaces readiness, missing document/data risks, invoice aging, unpaid balance, and gross margin when enough Alvys evidence exists.
   - The app avoids fabricating numbers. Missing revenue, mileage, weight, invoice, or payable data should display as missing / not evaluated rather than zero.

5. **Sandbox/writeback posture panel**
   - The app has a visible boundary for future Alvys writeback.
   - Current live operations remain disabled/unsupported until sandbox credentials, non-production host, and writeback contract are approved.

## What not to claim yet

Do not claim any of the following in the meeting:

- That the app books LTL freight in Alvys today.
- That assignment decisions are pushed back to Alvys today.
- That the app is production-ready for dispatchers without UAT.
- That all Alvys data fields are complete; the point is to surface incomplete data honestly.
- That billing automation fully replaces current billing work.

## Recommended walkthrough sequence

1. Start at `/ltl` and explain the product goal: Search → Match → Assign → Bill.
2. Show Search and saved views first. Make the point that saved views encode dispatcher questions, not just filters.
3. Open a load detail and expand match factors.
4. Show a warning/blocker in assignment validation.
5. Show the audit-only assignment boundary: not pushed to Alvys.
6. Switch to Billing and discuss readiness, missing-data, aging, margin, and cash-delay prevention.
7. End on the sandbox/writeback posture: this is where controlled Alvys writeback would plug in after approval.

## Current development state

| Area | State | Meeting language |
|---|---|---|
| Core app scaffold | Implemented | Full-stack app exists with .NET API, Angular SPA, SQL Server, auth plumbing, Docker, and Azure deployment docs. |
| LTL read model | Implemented for first slice | Converts Alvys context into dispatcher-facing LTL work items. |
| Search/filter UI | Demo-ready concept | Good enough to explain workflow and gather feedback. |
| Matching engine | Demo-ready concept | Explainable ranking and blockers/warnings are the key value. |
| Assignment | Audit-only | Intentional safety boundary; no Alvys mutation yet. |
| Billing readiness | Demo-ready concept | Shows revenue-protection direction; needs live representative data to land best. |
| Writeback | Not live | Disabled/unsupported until sandbox contract and credentials are approved. |
| UAT deployment | Partially configured | Workflows were made manual until Azure/Entra/GitHub secrets are fully configured. |

## QA readiness estimate

Assuming FreightDNA remains the priority and this app receives focused but secondary attention:

- **Concept/demo review:** ready now if framed as decision-support and audit-only.
- **Internal technical QA:** 2-4 business days after Azure/Entra/Alvys environment values are confirmed.
- **Operations UAT with selected dispatch/billing users:** 5-7 business days after technical QA starts and representative live/sandbox Alvys data is available.
- **Production readiness:** not before writeback decision, security review, UAT feedback, deployment hardening, and a clear decision on whether assignment/writeback remains audit-only or becomes sandbox/live-enabled.

## Dependencies before real QA

1. Confirm deployed UAT URL or finish Azure App Service UAT configuration.
2. Confirm Entra app registrations and redirect URI.
3. Confirm whether demo/UAT uses `Fallback` or `Live` Alvys provider.
4. If live/sandbox Alvys is used, provide tenant/client credentials as secrets only.
5. Identify 2-3 representative load numbers:
   - one with a clean match,
   - one with a blocker/warning,
   - one with billing readiness or invoice/payment risk.
6. Decide whether early testers should see empty-state behavior, live data, or a recorded walkthrough.

## Recommendation for Jason

Ask Jason to treat this as a **product concept validation** meeting, not final QA. The right questions are:

- Does this match how dispatch/billing actually think about LTL work?
- Are the saved views the right operational buckets?
- Are the blockers/warnings aligned with Value Truck's real assignment rules?
- What data from Alvys is missing or unreliable?
- Which writeback actions should stay manual, simulation-only, or eventually automated?

## Suggested message to say live

> FreightDNA is closest to formal QA. The LTL Tool is in a strong concept-demo state. It reflects what I learned from the Buckeye trip and the conversations around dispatch, billing, and operational handoffs: the value is not another grid, it is explainable decision support. I would like to use this meeting to validate whether the workflow feels right before I push it into UAT hardening.
