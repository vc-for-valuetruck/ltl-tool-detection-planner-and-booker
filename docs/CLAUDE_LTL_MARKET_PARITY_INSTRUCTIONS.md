# Claude Code Instructions — LTL Market-Parity Search, Match, Assign, and Bill

Use this document as the working instruction set for Claude Code when continuing the Value Truck LTL Tool Detection, Planner, and Booker project.

## Product objective

Build the LTL tool into a modern dispatch and revenue-protection workbench that competes with paid logistics tools that perform the same practical job: find available freight, match it to the best driver/truck/trailer, support an auditable assignment decision, and help operations/accounting bill accurately.

The product is not a raw Alvys grid. It is the decision-support layer on top of Alvys.

The target business workflow is:

1. **Search** live Alvys freight and expose meaningful dispatch worklists.
2. **Match** the load to the best driver/truck/trailer using explainable scoring.
3. **Assign** internally with validation, blockers, warnings, override reasons, and audit history.
4. **Bill** effectively by surfacing missing billing data, invoice state, documents, POD, exceptions, visibility risks, and accessorial review needs.

This is the “secret sauce”: the app should convert Alvys operational data into prioritized work, defensible recommendations, and revenue protection.

## Current repo posture to preserve

The current implementation already has a good foundation:

- `/api/ltl/search` returns normalized, filtered, sorted, paged loads.
- `/api/ltl/loads/{idOrNumber}` resolves a detail view.
- `/api/ltl/loads/{idOrNumber}/matches` returns ranked, explainable match recommendations.
- `/api/ltl/loads/{idOrNumber}/billing-readiness` exposes billing readiness.
- `/api/ltl/loads/{idOrNumber}/assign/validate` validates before assignment.
- `/api/ltl/loads/{idOrNumber}/assign` records an internal audit decision.
- `/api/ltl/loads/{idOrNumber}/assignments` returns assignment history.
- `/api/ltl/billing/worklist` returns a billing attention list.
- `/api/ltl/exceptions` returns exception-bearing loads.
- Angular `/ltl` already has Search, Billing Worklist, Exceptions, detail drawer, recommended matches, assignment validation, billing readiness, visibility, and saved views.

Preserve the current safety principles:

- **Never expose Alvys credentials to the SPA.**
- **Never invent missing operational or billing values.** Missing data must be surfaced.
- **Keep Alvys writeback gated and explicit.** If live writeback is not formally supported, the UI must say so.
- **Do not seed fake production data to make demos look real.** Fallback/empty states are acceptable; fake “live” data is not.
- **Do not remove auditability.** Assignment and billing decisions need traceable reasons.

## Required reads before writing code

Read these files first and keep implementation consistent with them:

### Product / demo docs

- `README.md`
- `docs/LTL_DEMO_RUNBOOK.md`
- `docs/ALVYS_INTEGRATION.md`
- `.env.example`

### Backend LTL slice

- `src/LtlTool.Api/Features/Ltl/LtlController.cs`
- `src/LtlTool.Api/Features/Ltl/LtlLoadService.cs`
- `src/LtlTool.Api/Features/Ltl/LtlNormalizationService.cs`
- `src/LtlTool.Api/Features/Ltl/LtlReadModels.cs`
- `src/LtlTool.Api/Features/Ltl/LtlSearchQuery.cs`
- `src/LtlTool.Api/Features/Ltl/MatchService.cs`
- `src/LtlTool.Api/Features/Ltl/MatchScoringService.cs`
- `src/LtlTool.Api/Features/Ltl/BillingReadinessService.cs`
- `src/LtlTool.Api/Features/Ltl/Assignment/AssignmentValidationService.cs`
- `src/LtlTool.Api/Features/Ltl/SavedViews/*`
- `src/LtlTool.Api/Features/Integrations/Alvys/*`

### Frontend LTL workspace

- `web/src/app/features/ltl/ltl-search.ts`
- `web/src/app/features/ltl/ltl-search.html`
- `web/src/app/features/ltl/ltl-search.css`
- `web/src/app/features/ltl/ltl.models.ts`
- `web/src/app/features/ltl/ltl.service.ts`
- `web/src/app/features/ltl/saved-views.ts`
- `web/src/app/features/ltl/*spec.ts`

### Tests / CI

- `src/LtlTool.Api.Tests/Ltl/*`
- `.github/workflows/ci.yml`
- `web/package.json`
- `LtlTool.sln`

## Write targets and implementation areas

Use focused, vertical-slice changes. Prefer improving the existing LTL feature structure over introducing unrelated architecture.

### Backend writes

Allowed/expected backend write areas:

- `src/LtlTool.Api/Features/Ltl/LtlSearchQuery.cs`
- `src/LtlTool.Api/Features/Ltl/LtlReadModels.cs`
- `src/LtlTool.Api/Features/Ltl/LtlLoadService.cs`
- `src/LtlTool.Api/Features/Ltl/LtlNormalizationService.cs`
- `src/LtlTool.Api/Features/Ltl/MatchService.cs`
- `src/LtlTool.Api/Features/Ltl/MatchScoringService.cs`
- `src/LtlTool.Api/Features/Ltl/BillingReadinessService.cs`
- `src/LtlTool.Api/Features/Ltl/WorkflowStageService.cs`
- `src/LtlTool.Api/Features/Ltl/Assignment/*`
- `src/LtlTool.Api/Features/Ltl/SavedViews/*`
- `src/LtlTool.Api/Features/Integrations/Alvys/*` only when adding read-model coverage or safe sandbox-gated boundaries.
- `src/LtlTool.Api.Tests/Ltl/*`

Do not write secrets or real customer credentials into any file.

### Frontend writes

Allowed/expected frontend write areas:

- `web/src/app/features/ltl/ltl-search.ts`
- `web/src/app/features/ltl/ltl-search.html`
- `web/src/app/features/ltl/ltl-search.css`
- `web/src/app/features/ltl/ltl.models.ts`
- `web/src/app/features/ltl/ltl.service.ts`
- `web/src/app/features/ltl/saved-views.ts`
- `web/src/app/features/ltl/*spec.ts`

Keep the UI enterprise-grade: readable, fast, explainable, audit-friendly, and not cluttered.

### Documentation writes

Update docs when behavior changes:

- `README.md`
- `docs/LTL_DEMO_RUNBOOK.md`
- `docs/ALVYS_INTEGRATION.md`
- new docs under `docs/` only if they help future development or UAT.

## Functional requirements

### 1. Search page / freight discovery

The search page must behave like a modern dispatch console, not a static report.

It should support, preserve, or improve:

- Search by load number, order number, PO number, customer, origin, destination, pickup window, delivery window, equipment type, status, assignment state, billing state, workflow stage, and exception state.
- Filters for unassigned, ready to bill, missing billing data, exceptions, blocked-only, and LTL-only.
- Saved views for recurring dispatcher/accounting questions.
- Sortable columns for pickup date, delivery date, customer, status, weight, miles, revenue, revenue per mile, and billing readiness.
- Clear empty/loading/error states.
- Missing data rendered visibly as `missing` or `—`, never coerced to false certainty.
- Pagination and bounded Alvys sweeps with honest truncated messaging.

Enhancement priority:

- Add more actionable saved-view presets only when they map to actual query fields.
- Improve search result usefulness before adding visual polish.
- Do not add fake “AI” labels unless they are backed by deterministic data.

### 2. Alvys data normalization

Use Alvys as the system of record for operational data. Normalize, do not blindly mirror.

Where available, surface:

- Load/order ID
- Customer
- Pickup location
- Delivery location
- Pickup window
- Delivery window
- Equipment type
- Status
- Assignment state
- Driver/truck/trailer assignment
- Commodity/class/dimensions when available
- Weight
- Pallets/pieces/linear feet when available
- Revenue/rate
- Mileage
- Documents/POD
- Notes
- Visibility events
- Invoices
- Exceptions
- Billing readiness

When Alvys does not provide a field, add a missing-data flag or an unavailable factor. Do not default it to zero, false, or “good”.

### 3. Match engine

The matching system should assign loads to the best practical driver/truck/trailer, with the reason visible to the user.

Preserve and improve the existing explainable scoring model. It should consider:

- Equipment compatibility
- Weight capacity
- Driver readiness / credential status
- Fleet alignment
- Origin proximity / geography
- Equipment events / maintenance / OOS conflicts
- Pickup and delivery window feasibility where data exists
- HOS only when real HOS data exists
- Historical performance only when real history exists
- Revenue / revenue-per-mile only when enough data exists
- Missing data as not-scored, not as a penalty unless business rules say it blocks action

Hard disqualifiers should continue to cap the label at **Not Recommended**.

Match labels should remain plain-language:

- Excellent Match
- Good Match
- Possible Match
- Risky Match
- Not Recommended

Every recommendation must explain why it was recommended or why it was risky.

### 4. Assignment workflow

Assignment should stay audit-friendly and validation-first.

Before assignment, validate:

- Driver selected
- Driver active/not terminated
- Driver credentials valid
- Truck/trailer/equipment compatibility where data exists
- Capacity is not exceeded
- Pickup/delivery windows are not obviously infeasible
- Missing rate/weight/lane/billing-critical fields are warnings or blockers as appropriate
- Maintenance/OOS conflicts are surfaced

Behavior rules:

- Blockers disable assignment and return 422 from the API.
- Warnings can be overridden only with a stated reason.
- The audit record should store selected driver/truck/trailer, match score/label, warnings, override reason, notes, user, timestamp, and Alvys writeback status.
- The UI must clearly state whether assignment was pushed to Alvys or internal only.

### 5. Billing effectiveness

This is critical. The tool should help Value Truck avoid missed revenue and billing delay.

Billing readiness should check and show:

- Customer present
- Rate/revenue present
- POD/document evidence where available
- Weight present
- Mileage present when needed
- Accessorial review needed
- Exceptions resolved or blocking
- Invoice state
- Already invoiced state
- Unpaid balance risk
- Visibility/tracking failures that can block customer billing

Billing badges should remain or be expanded carefully:

- Ready to Bill
- Missing Rate
- Missing POD
- Missing Accessorial Review
- Missing Weight
- Customer Review Needed
- Exception Blocking Billing
- Already Invoiced

The billing worklist should be useful to accounting: readiness-first, risk-visible, and filterable by badge.

### 6. UX standard

The UI should feel like serious logistics software:

- Sticky headers or at least readable table behavior
- Clean filter area
- Saved view chips
- Sort indicators
- Billing and exception badges
- Workflow stage indicator: Search → Match → Assign → Bill → Billed
- Detail drawer with match explanation, billing readiness, exceptions, visibility, assignment panel, and audit history
- No misleading claims
- No hidden errors
- No browser-storage-only persistence for important user work
- No modal clutter unless it improves focus

## Industry-standard parity checklist

Use this checklist when deciding what to build next. The tool is moving toward market parity when it can answer these operational questions quickly:

- What LTL freight is available right now?
- Which loads are unassigned?
- Which loads should operations act on first?
- Which driver/truck/trailer is the best match?
- Why is that match recommended?
- What risks make a match weak or blocked?
- Which loads are ready to bill?
- Which loads are blocked from billing?
- What missing data is delaying billing?
- Where are we likely missing accessorial revenue?
- What has already been invoiced?
- What exceptions need attention?
- What was assigned, by whom, when, and why?

## Non-goals / do not do

- Do not claim live booking/writeback to Alvys unless the supported write API contract is confirmed and implemented safely.
- Do not create a fake booking path.
- Do not put secrets in source, tests, screenshots, docs, or committed env files.
- Do not fake HOS, location, lane history, POD, accessorials, or revenue.
- Do not replace deterministic business rules with vague AI wording.
- Do not remove the explicit “not pushed to Alvys” messaging until real writeback exists.
- Do not trade auditability for UI speed.

## Testing expectations

For any code change, run or at least keep CI aligned with:

```bash
dotnet restore
dotnet build
dotnet test

cd web
npm ci
npm run build -- --configuration production
npm test -- --watch=false
```

If the local environment lacks the .NET SDK or Node, say so in the PR body and rely on CI. Do not mark API testing complete unless it ran.

Tests should cover:

- Search query/filter/sort behavior
- Normalization of missing values
- Billing readiness badges and blockers
- Match scoring labels and unavailable factors
- Assignment validation blockers/warnings
- Assignment audit behavior
- Saved view persistence
- Angular service/query mapping
- Angular UI behavior for major workbench flows

## PR expectations

Every PR should include:

- A business summary in CFO/operations language.
- A technical summary in developer language.
- Screens or UI notes if the frontend changed.
- Explicit statement of Alvys read/write posture.
- Test commands run and their result.
- Known limitations or next required data/API contracts.

Suggested PR body structure:

```markdown
## Business outcome

## Technical changes

## Alvys posture

## Reads / writes

## Testing

## Known limitations
```

## Acceptance criteria for next meaningful slice

A strong next implementation PR should deliver at least one of these measurable improvements:

1. Search page becomes more operationally useful through better filters, stage logic, worklists, or saved views.
2. Match scoring becomes materially stronger using real available Alvys signals.
3. Assignment validation catches more real-world bad assignments while preserving override auditability.
4. Billing readiness catches more revenue leakage or billing blockers.
5. UI makes Search → Match → Assign → Bill easier to understand for dispatch/accounting leadership.

The goal is not just to add features. The goal is to make Value Truck faster, more accurate, and harder to leak revenue.