# Alvys TMS Integration

This document describes the server-side Alvys integration for the LTL tool and the
source-of-truth rules that govern it.

## Source of truth

- **Live Alvys API is the default source of truth for all LTL data.** The default
  provider is `Live` everywhere except the demo override.
- **Fallback is local/UAT only.** The `Fallback` provider returns empty results so
  the app boots without a live tenant. It must never be the configured provider in
  production-like environments.
- **Credentials are server-side only.** `Alvys:ClientId` / `Alvys:ClientSecret` are
  read by the API and are never surfaced to the Angular SPA. No Alvys value is added
  to `web/public/runtime-config.json` or any `RUNTIME_*` web env var.

## Configuration

Bound to `AlvysOptions` from the `Alvys` configuration section
(`src/LtlTool.Api/Features/Integrations/Alvys/AlvysOptions.cs`). Environment variables
use the double-underscore convention (`Alvys__ClientId`) and are fed from `ALVYS_*`
host env vars via Docker Compose.

| Key | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Alvys:Provider` | `ALVYS_PROVIDER` | `Live` | `Live` or `Fallback`. Live is the source of truth. |
| `Alvys:ApiBaseUrl` | `ALVYS_API_BASE_URL` | `https://integrations.alvys.com/api/p/v1` | API base (no trailing slash needed). |
| `Alvys:TokenUrl` | `Alvys__TokenUrl` | `https://auth.alvys.com/oauth/token` | OAuth2 token endpoint. |
| `Alvys:Audience` | `Alvys__Audience` | `https://api.alvys.com/public/` | OAuth2 audience. |
| `Alvys:TenantId` | `ALVYS_TENANT_ID` | _(empty)_ | Informational; scopes the credentials. |
| `Alvys:ClientId` | `ALVYS_CLIENT_ID` | _(empty)_ | OAuth2 client_id. Secret-adjacent. |
| `Alvys:ClientSecret` | `ALVYS_CLIENT_SECRET` | _(empty)_ | OAuth2 client_secret. **Secret.** |
| `Alvys:TimeoutSeconds` | `Alvys__TimeoutSeconds` | `30` | Per-request HTTP timeout. |

When `Provider=Live` (the default) but credentials are missing, the API logs a
startup warning and live calls fail until credentials are configured — the app still
boots so CI and fresh clones work.

## Authentication

OAuth 2.0 **client-credentials** grant, mirroring `freight-dna`:

```
POST {TokenUrl}
{ client_id, client_secret, audience, grant_type: "client_credentials" }
→ { access_token, expires_in, token_type }
```

`AlvysTokenProvider` caches the access token until `expires_in - 60s` and refreshes
under a semaphore so concurrent callers share one token request. The token is sent as
a `Bearer` header on every API call.

## Components

All under `src/LtlTool.Api/Features/Integrations/Alvys/`:

| File | Purpose |
| --- | --- |
| `AlvysOptions.cs` | Strongly-typed config + `AlvysProvider` enum (`Live`/`Fallback`). |
| `IAlvysClient.cs` | Client abstraction (`SearchLoadsAsync`, `GetLoadByNumberAsync`). |
| `AlvysClient.cs` | Live client; default source of truth. Named `HttpClient` + bearer token per request. |
| `AlvysTokenProvider.cs` | OAuth2 token acquisition + caching (`IAlvysTokenProvider`). |
| `FallbackAlvysClient.cs` | Non-default empty-result stub for local/UAT only. |
| `AlvysDtos.cs` | Load search request/response DTOs + token response. |
| `AlvysServiceCollectionExtensions.cs` | DI wiring + provider selection (live default). |

Registered in `Program.cs` via `builder.Services.AddAlvysIntegration(builder.Configuration)`.

## Safety

- **No secret logging.** Token failures log the HTTP status code only — never the
  response body, which can echo request parameters including `client_secret`. Covered
  by `AlvysTokenProviderTests.Failed_token_request_does_not_log_secret`.
- **Timeouts.** Both named clients (`AlvysAuth`, `AlvysApi`) apply `TimeoutSeconds`.
- **Graceful degradation.** Non-success API responses are logged (status only) and
  surfaced as empty results / `null` rather than thrown to callers.

## freight-dna files reviewed

`freight-dna` (`https://github.com/valuetruck-vc/freight-dna`) was used as the source
of truth for the auth/client pattern. Exact files reviewed at `main`:

- `src/FreightDna.Api/Services/Alvys/IAlvysService.cs`
- `src/FreightDna.Api/Services/Alvys/AlvysService.cs`
- `src/FreightDna.Api/Services/Alvys/NoOpAlvysService.cs`
- `src/FreightDna.Api/Services/Alvys/AlvysDtos.cs`
- `src/FreightDna.Api/Program.cs` (Alvys DI registration, lines ~35–44)

### Deliberate divergences from freight-dna

- **Strongly-typed `AlvysOptions`** instead of raw `IConfiguration[...]` lookups, so
  binding and provider selection are unit-testable.
- **Explicit `Alvys:Provider` flag** (`Live`/`Fallback`) with `Live` as the default,
  rather than inferring the provider purely from credential presence. This makes "live
  is the source of truth" an explicit, auditable decision and keeps Fallback opt-in.
- **`HttpClient`/`IHttpClientFactory`** for the API client instead of RestSharp, to
  avoid adding a dependency for this skeleton slice. The token-acquisition pattern is
  unchanged.

## Next slice

Expose Alvys-backed read endpoints (e.g. `GET /api/loads`) that call `IAlvysClient`,
plus richer load DTO mapping for the planner/booker views.
