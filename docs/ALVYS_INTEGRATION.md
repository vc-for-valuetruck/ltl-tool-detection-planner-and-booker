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
| `Alvys:ApiBaseUrl` | `ALVYS_API_BASE_URL` | `https://integrations.alvys.com` | Host root (no version, no trailing slash needed). |
| `Alvys:ApiVersion` | `ALVYS_API_VERSION` | `v1` | API version segment for `/api/p/v{version}/...`. With or without the `v` prefix. |
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
| `AlvysOptions.cs` | Strongly-typed config (`ApiBaseUrl` host + `ApiVersion`) + `AlvysProvider` enum (`Live`/`Fallback`). |
| `IAlvysClient.cs` | Client abstraction (`SearchLoadsAsync`, `GetLoadByNumberAsync`, `SearchTripsAsync`, `SearchTrailersAsync`, `SearchTrucksAsync`). |
| `AlvysClient.cs` | Live client; default source of truth. Named `HttpClient` + bearer token per request. |
| `AlvysApiRoutes.cs` | Builds versioned `/api/p/v{version}/...` paths and normalizes the version (no double `v`). |
| `AlvysTokenProvider.cs` | OAuth2 token acquisition + caching (`IAlvysTokenProvider`). |
| `FallbackAlvysClient.cs` | Non-default empty-result stub for local/UAT only. |
| `AlvysDtos.cs` | Load + trip + trailer + truck search request/response DTOs + token response. |
| `AlvysServiceCollectionExtensions.cs` | DI wiring + provider selection (live default). |

## Internal read-only API endpoints

The dispatcher Angular SPA must not hold Alvys credentials, so it never calls Alvys
directly. Instead the API exposes server-side **read-only** search endpoints
(`AlvysSearchController`, `src/LtlTool.Api/Features/Alvys/AlvysSearchController.cs`) that
proxy `IAlvysClient`. Each action passes the request body straight through to the
matching client method and returns the paged Alvys read model — no field is added, and
no token/config/secret is ever in the response (covered by
`AlvysSearchControllerTests.Responses_carry_no_credential_or_secret_fields`).

| Internal endpoint | Request DTO | Client method | Upstream Alvys call |
| --- | --- | --- | --- |
| `POST /api/alvys/loads/search` | `LoadSearchRequest` | `SearchLoadsAsync` | `POST /api/p/v{version}/loads/search` |
| `POST /api/alvys/trips/search` | `TripSearchRequest` | `SearchTripsAsync` | `POST /api/p/v{version}/trips/search` |
| `POST /api/alvys/trailers/search` | `TrailerSearchRequest` | `SearchTrailersAsync` | `POST /api/p/v{version}/trailers/search` |
| `POST /api/alvys/trucks/search` | `TruckSearchRequest` | `SearchTrucksAsync` | `POST /api/p/v{version}/trucks/search` |

- **Authorization.** All four require the `AllowedEmailDomain` policy, same as
  `/api/me`. An unauthenticated request returns 401 (not 404) because the route is
  matched before authorization runs — see `AlvysSearchEndpointTests`. Health stays
  anonymous.
- **HTTP verb.** `POST` is used because the search filter set is the request body, not
  because anything is mutated. These endpoints are queries only.
- **No internal mutation endpoints exist** — there is no `PUT`/`PATCH`/`DELETE` and no
  Alvys writeback in this phase.

## Upstream Alvys search paths

Both endpoints are `POST` under the versioned path built from `Alvys:ApiVersion`.
The `v` prefix is fixed in the route, so the configured version is normalized to avoid
a double `v` — `v2.0` and `2.0` both resolve to the segment `v2.0`
(`AlvysApiRoutes.NormalizeVersion`).

### `loads/search` → `POST /api/p/v{version}/loads/search`

Loads are the core open-freight source for LTL detection, planning and booking.

- **Request** (`LoadSearchRequest`): `Page` (0-based), `PageSize` (> 0), optional
  `DateRange`, `Status[]`, `OrderNumbers[]`, `LoadNumbers[]` (≤ 150), `PONumbers[]`,
  `CustomerId`, `UpdatedAtRange`, `UpdatedBy`, `IncludeDeleted`. When no status is
  supplied, the full status list is sent so a bare paged sweep is still valid.
- **Local validation** (`LoadSearchRequest.Validate`): only the locally enforceable
  rules — `PageSize > 0` and `LoadNumbers ≤ 150`. Alvys enforces the conditional-filter
  requirement (at least one of Status/OrderNumbers/LoadNumbers/PONumbers/CustomerId/
  UpdatedBy) server-side.
- **Response** (`AlvysLoadsResponse` = paged `{ Page, PageSize, Total, Items[] }`):
  pragmatic `AlvysLoad` projection covering planner/booker-relevant fields (ids,
  customer, status, stops, money fields, mileage/weight/volume, scheduled/actual dates,
  notes, references, equipment/type, rep/planner/manager ids, audit + `IsDeleted`).
  Unknown JSON properties are tolerated.

### `trips/search` → `POST /api/p/v{version}/trips/search`

Trips are the core movement/payroll source for main/child trip logic, loaded/empty
mileage, assigned truck/trailer and driver/carrier/operator pay context.

- **Request** (`TripSearchRequest`): `Page` (0-based), `PageSize` (> 0), optional
  `Status[]`, `LoadNumbers[]`, `TripNumbers[]`, `PickupDateRange`, `DeliveryDateRange`,
  `UpdatedAtRange`, `UpdatedBy`, `IncludeDeleted`.
- **Local validation** (`TripSearchRequest.Validate`): only `PageSize > 0`. Alvys
  enforces the conditional-filter requirement (Status/LoadNumbers/TripNumbers/UpdatedBy)
  server-side.
- **Response** (`AlvysTripsResponse` = paged envelope): pragmatic `AlvysTrip` projection
  covering trip number/status/load, stops (address/coordinates/status/schedule/windows/
  arrived-departed/references), total/empty/loaded mileage, pickup/delivery/picked-up/
  delivered/carrier-assigned/released dates, `TripValue`, `Truck.Id`,
  `Trailer.{Id,EquipmentType,EquipmentLength}`, driver/carrier/owner-operator pay
  context, and `IsDeleted`. Nested payroll/accessorial classes are kept flexible and
  unknown JSON is tolerated.

### `trailers/search` → `POST /api/p/v{version}/trailers/search`

Trailers are equipment master data used for LTL capacity, equipment-compatibility and
assignment-readiness decisions in the planner/booker. **Read-only** — this slice issues
queries only; no trailer is created or mutated.

- **Request** (`TrailerSearchRequest`): `Page` (0-based), `PageSize` (> 0), optional
  `Status[]`, `TrailerNumber`, `FleetName`, `VinNumber`.
- **Local validation** (`TrailerSearchRequest.Validate`): only `PageSize > 0` is locally
  enforced. Alvys enforces the conditional-filter requirement server-side
  (`TrailerNumber`/`FleetName`/`VinNumber` are conditionally required when the other
  conditional filters are empty).
- **Response** (`AlvysTrailersResponse` = paged `{ Page, PageSize, Total, Items[] }`):
  pragmatic `AlvysTrailerEquipment` projection — `Id`, `TrailerNum`, `Fleet`
  (`Id`/`Name`/`InvoiceNumberPrefix`), `Year`, `Make`, license/plate fields and expiries,
  `VinNum`, `Status`, `SubsidiaryId`, `EquipmentType`, `EquipmentSize`, `Capacity`
  (`Pallets`/`Weight`), insurance/inspection fields, `Notes[]`, `References[]`, `CreatedAt`.
  Unknown JSON properties are tolerated.

### `trucks/search` → `POST /api/p/v{version}/trucks/search`

Trucks are equipment master data used for the same capacity/compatibility/assignment-
readiness decisions. **Read-only** — queries only.

- **Request** (`TruckSearchRequest`): `Page` (0-based), `PageSize` (> 0), optional
  `Status[]`, `TruckNumber`, `FleetName`, `VinNumber`, `IsActive`, `RegisteredName`.
- **Local validation** (`TruckSearchRequest.Validate`): only `PageSize > 0` is locally
  enforced. Alvys enforces the conditional-filter requirement server-side
  (`TruckNumber`/`FleetName`/`VinNumber`/`IsActive`/`RegisteredName` are conditionally
  required when the other conditional filters are empty).
- **Response** (`AlvysTrucksResponse` = paged envelope): pragmatic `AlvysTruck` projection —
  `Id`, `TruckNum`, `VinNumber`, `Year`, `Make`, `Model`, license/plate fields and expiries,
  `Status`, `SubsidiaryId`, `NumberOfAxles`, `Fleet`, `GrossWeight`, `EmptyWeight`, `Color`,
  `FuelType`, `FuelCards[]`, insurance/inspection fields, `Notes[]`, `References[]`,
  `CreatedAt`. Nested fuel/weight fields are kept minimal and unknown JSON is tolerated.

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

## Read-only stance

This integration phase is **read-only**. Both the internal API endpoints
(`/api/alvys/{loads,trips,trailers,trucks}/search`) and the upstream Alvys client calls
issue queries only. Alvys models searches as `POST` (the filter set is the request
body), but no data is created, updated or deleted and there is no writeback to Alvys. No
`PUT`/`PATCH`/`DELETE` or POST-mutation calls are made. Live Alvys remains the default
source of truth; the `Fallback` provider is opt-in for local/UAT and returns empty
(shape-preserving) results.

## Next slice

Dispatcher UI and domain logic on top of these endpoints: richer normalized read models
for the planner/booker views, equipment compatibility/capacity logic, and the Angular
services that consume `/api/alvys/*`. Writeback (booking/assignment) remains out of
scope until the read-only phase is signed off.
