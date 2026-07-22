# Phase 2 · Sprint 1 — Consolidation-plan narrative service

Backend service that produces a short, sourced narrative for a consolidation plan
("why review this, what to verify, next action") via Azure OpenAI. This document covers the
**service, config, cache, and fail-closed semantics only**. The HTTP endpoint (#150) and the
Angular component (#151) ship in parallel PRs and are out of scope here.

## What it does

Given a recorded consolidation-plan id (`planId`), `NarrativeService`:

1. Checks the kill switch. If off → returns `(null, false)` immediately.
2. Rebuilds the plan from **live Alvys data** (read-only) via the existing
   `ConsolidationPlanService`, projected to a minimal `NarrativePlanPayload`.
3. Computes a content hash of that payload and checks the cache. On a hit → returns the cached
   narrative with `Cached = true` and **does not** call the model.
4. On a miss → calls Azure OpenAI (JSON-mode, temperature 0), validates the output, caches it for
   10 minutes, and returns `(response, false)`.

The service returns `Task<(NarrativeResponse? Response, bool Cached)>`. When `Response` is non-null
the flag is the `X-Ai-Cached` seam the endpoint PR uses. When `Response` is null the flag is a
**failure discriminator** the endpoint uses to pick 404 vs 503 (see below).

### Tuple-discriminator contract (relied on by endpoint #150)

| Tuple | Meaning | Endpoint status |
| --- | --- | --- |
| `(response, false)` | Freshly generated | 200 |
| `(response, true)` | Served from cache | 200 (`X-Ai-Cached: true`) |
| `(null, false)` | Kill switch off, or plan not found | **404** |
| `(null, true)` | Plan found, but the model failed or returned unusable output | **503** |

There is no collision: a cache hit always carries a non-null response, so `(null, true)`
unambiguously signals an AI failure.

## Config keys

Bound from the top-level `AI` section (`appsettings.json`):

```json
"AI": {
  "NarrativeEnabled": false,
  "AzureOpenAI": { "Endpoint": "", "Deployment": "", "ApiVersion": "2024-06-01" }
}
```

| Key | Bound to | Meaning |
| --- | --- | --- |
| `AI:NarrativeEnabled` | `AiFeatureFlags` (`IOptionsMonitor`) | Kill switch. Default `false`. |
| `AI:AzureOpenAI:Endpoint` | `AzureOpenAiOptions` (`IOptions`) | Azure OpenAI resource endpoint. Server-side only. |
| `AI:AzureOpenAI:Deployment` | `AzureOpenAiOptions` | Chat-completions deployment name. |
| `AI:AzureOpenAI:ApiVersion` | `AzureOpenAiOptions` | Retained for config parity; the SDK negotiates a compatible service version. |

There is **no API-key field**. Authentication uses `DefaultAzureCredential` (managed identity in
Azure, developer credentials locally), so no secret ever lands in config, source, tests, or a
screenshot.

## Kill switch behavior

`AI:NarrativeEnabled` is read through `IOptionsMonitor<AiFeatureFlags>` so it can be flipped without
a restart. When `false` (the default), `GenerateAsync` returns `(null, false)` **before** any plan
fetch or model call — a fresh clone / CI / the demo never touch Azure.

## Cache

- Store: `IMemoryCache` (in-memory — **no EF `DbSet` added this sprint**).
- TTL: **10 minutes**.
- Key: `narrative:{planId}:{planHash}` where `planHash` is the lowercase hex SHA-256 of the
  canonical JSON of the plan fields sent to the model (`NarrativePlanPayload`).
- Because the key includes the content hash, a plan whose underlying Alvys data changed produces a
  new key → cache miss → fresh narrative. Only successful narratives are cached; failures are not.
- The plan is re-read from Alvys on every call (cheap, read-only); the cache saves the **model**
  call, not the plan fetch.

## Fail-closed semantics

The service never throws to the caller and never partially populates a response:

- Kill switch off, or unknown/blank `planId`, or a plan that cannot be resolved → `(null, false)`
  (endpoint → 404). The model is never called.
- Any Azure OpenAI exception (transport, credential, timeout) → logged as a **Warning with a
  correlation id**, returns `(null, true)` (endpoint → 503).
- Model output that is not valid JSON → `(null, true)`.
- Model output missing any of the four required fields (`whyReview`, `whatToVerify`, `nextAction`,
  or a non-empty `citations` array) → `(null, true)`.

The system prompt forces a single JSON object with exactly
`{ whyReview, whatToVerify, nextAction, citations[] }`, instructs the model to cite **at least one
specific plan field name**, and forbids inventing any value not present in the plan payload.

## Alvys posture

Read-only. The plan is rebuilt from live Alvys reads via `ConsolidationPlanService`; the
consolidation audit store is read (never written). **No Alvys writeback path is added.**

## Files

- `src/LtlTool.Api/Features/Ai/Narrative/` — options, `NarrativeService`, plan source,
  `AzureOpenAiNarrativeChatClient`. The `INarrativeService` / `NarrativeResponse` contract and
  the `NullNarrativeService` fallback live in `Features/Ai/Narrative/Contracts/` (landed by the
  endpoint PR #150/#152).
- DI is consolidated in `AiServiceCollectionExtensions.AddAiNarrative`: it binds the options and
  registers the real `NarrativeService` when `AI:NarrativeEnabled=true`, else `NullNarrativeService`.
- Wired in `Program.cs` via `builder.Services.AddAiNarrative(...)`.
- Unit tests: `src/LtlTool.Api.Tests/Ai/Narrative/`.
