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
| `IAlvysClient.cs` | Client abstraction (`SearchLoadsAsync`, `GetLoadByNumberAsync`, `GetLoadAsync`, `ListLoadDocumentsAsync`, `ListLoadNotesAsync`, `SearchTripsAsync`, `GetTripAsync`, `ListTripStopsAsync`, `SearchTrailersAsync`, `SearchTrucksAsync`, `SearchDispatchPreferencesAsync`, `SearchLocationsAsync`, `SearchDriversAsync`, `SearchCustomersAsync`, `SearchUsersAsync`, `SearchTendersAsync`, `GetTenderByIdAsync`, `SearchInvoicesAsync`, `GetInvoiceAsync`, `ListInboundVisibilityHistoryAsync`, `ListOutboundVisibilityHistoryAsync`, `SearchTruckEventsAsync`, `SearchTrailerEventsAsync`). |
| `AlvysClient.cs` | Live client; default source of truth. Named `HttpClient` + bearer token per request. |
| `AlvysApiRoutes.cs` | Builds versioned `/api/p/v{version}/...` paths (search paths incl. invoices and truck/trailer events, the `tenders/{tenderId}` GET path, the load/trip/invoice detail GET paths with URL-encoded query parameters, the `trips/{tripId}/stops` and the `visibility/{direction}/{loadNumber}/history` GET paths) and normalizes the version (no double `v`). |
| `AlvysTokenProvider.cs` | OAuth2 token acquisition + caching (`IAlvysTokenProvider`). |
| `FallbackAlvysClient.cs` | Non-default empty-result stub for local/UAT only. |
| `AlvysDtos.cs` | Load + trip + trailer + truck + dispatch-preference + location + driver + customer + user + tender search request/response DTOs (+ tender/load/trip detail), `LoadLookup`/`TripLookup` query DTOs, polymorphic `AlvysTripStopDetail`, load-document/load-note list item DTOs, + token response. |
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
| `POST /api/alvys/dispatch-preferences/search` | `DispatchPreferenceSearchRequest` | `SearchDispatchPreferencesAsync` | `POST /api/p/v{version}/dispatchpreferences/search` |
| `POST /api/alvys/locations/search` | `LocationSearchRequest` | `SearchLocationsAsync` | `POST /api/p/v{version}/locations/search` |
| `POST /api/alvys/drivers/search` | `DriverSearchRequest` | `SearchDriversAsync` | `POST /api/p/v{version}/drivers/search` |
| `POST /api/alvys/customers/search` | `CustomerSearchRequest` | `SearchCustomersAsync` | `POST /api/p/v{version}/customers/search` |
| `POST /api/alvys/users/search` | `UserSearchRequest` | `SearchUsersAsync` | `POST /api/p/v{version}/users/search` |
| `POST /api/alvys/tenders/search` | `TenderSearchRequest` | `SearchTendersAsync` | `POST /api/p/v{version}/tenders/search` |
| `GET /api/alvys/tenders/{tenderId}` | _(path param)_ | `GetTenderByIdAsync` | `GET /api/p/v{version}/tenders/{tenderId}` |
| `GET /api/alvys/loads?id=…\|loadNumber=…\|orderNumber=…` | `LoadLookup` _(query)_ | `GetLoadAsync` | `GET /api/p/v{version}/loads?…` |
| `GET /api/alvys/trips?id=…\|tripNumber=…[&includeDeleted=…]` | `TripLookup` _(query)_ | `GetTripAsync` | `GET /api/p/v{version}/trips?…` |
| `GET /api/alvys/trips/{tripId}/stops` | _(path param)_ | `ListTripStopsAsync` | `GET /api/p/v{version}/trips/{tripId}/stops` |
| `GET /api/alvys/loads/{loadNumber}/documents` | _(path param)_ | `ListLoadDocumentsAsync` | `GET /api/p/v{version}/loads/{loadNumber}/documents` |
| `GET /api/alvys/loads/{loadNumber}/notes` | _(path param)_ | `ListLoadNotesAsync` | `GET /api/p/v{version}/loads/{loadNumber}/notes` |
| `POST /api/alvys/invoices/search` | `InvoiceSearchRequest` | `SearchInvoicesAsync` | `POST /api/p/v{version}/invoices/search` |
| `GET /api/alvys/invoices?id=…\|invoiceNumber=…` | `InvoiceLookup` _(query)_ | `GetInvoiceAsync` | `GET /api/p/v{version}/invoices?…` |
| `GET /api/alvys/visibility/inbound/{loadNumber}/history` | _(path param)_ | `ListInboundVisibilityHistoryAsync` | `GET /api/p/v{version}/visibility/inbound/{loadNumber}/history` |
| `GET /api/alvys/visibility/outbound/{loadNumber}/history` | _(path param)_ | `ListOutboundVisibilityHistoryAsync` | `GET /api/p/v{version}/visibility/outbound/{loadNumber}/history` |
| `POST /api/alvys/trucks/events/search` | `TruckEventSearchRequest` | `SearchTruckEventsAsync` | `POST /api/p/v{version}/trucks/events/search` |
| `POST /api/alvys/trailers/events/search` | `TrailerEventSearchRequest` | `SearchTrailerEventsAsync` | `POST /api/p/v{version}/trailers/events/search` |

- **Authorization.** All require the `AllowedEmailDomain` policy, same as
  `/api/me`. An unauthenticated request returns 401 (not 404) because the route is
  matched before authorization runs — see `AlvysSearchEndpointTests`. Health stays
  anonymous.
- **HTTP verb.** Searches are `POST` because the filter set is the request body, not
  because anything is mutated. Single-record reads (the tender-by-id, load and trip
  lookups) and the sub-resource listings (load documents/notes, trip stops) are `GET`.
  All are queries only.
- **Lookup query parameters.** The load lookup requires one of `id`/`loadNumber`/
  `orderNumber`; the trip lookup requires one of `id`/`tripNumber` (with optional
  `includeDeleted`); the invoice lookup requires one of `id`/`invoiceNumber`. The
  controller returns **400** when no criterion is supplied (before any upstream call) and
  **404** when the record is not found or upstream degrades to `null`.
- **No internal mutation endpoints exist** — there is no `PUT`/`PATCH`/`DELETE`, no POST
  note/document creation, and no Alvys writeback in this phase. The tender slice is
  read-only: no accept/reject/cancel/update/create.

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

### Route assembly + lifecycle detail: load/trip detail and trip stops

These three read-only `GET` paths give the planner/booker the stop-1-to-billed lifecycle
context: a single load or trip by identifier, and the polymorphic stop list that assembles
a trip's route. All degrade gracefully — a 404 (or any non-success/transport error) becomes
`null`/`[]` rather than an exception.

#### `loads?…` → `GET /api/p/v{version}/loads?id=…|loadNumber=…|orderNumber=…`

- **Request** (`LoadLookup`): one of `id`, `loadNumber`, `orderNumber` is required; values
  are URL-encoded into the query string by `AlvysApiRoutes.LoadDetail`, which calls
  `LoadLookup.Validate()` (throws before any HTTP call when none is supplied).
- **Response** (`AlvysLoad`): the same projection returned by `loads/search`, additively
  extended with detail-only fields — `CancelledBy`, `PickedUpAt`, `DeliveredAt`,
  `InvoicedAt`, `LastInvoiceSentAt` and `Payments[]` (`AlvysLoadPayment`:
  `Amount`/`PaidAt`/`Reference`/`Method`). A 404 can mean no such load **or** an abandoned
  creation with no trips — both degrade to `null`. Unknown JSON is tolerated.

#### `trips?…` → `GET /api/p/v{version}/trips?id=…|tripNumber=…[&includeDeleted=…]`

- **Request** (`TripLookup`): one of `id`/`tripNumber` is required (`includeDeleted` alone
  is insufficient); `includeDeleted` is appended as lowercase `true`/`false` only when set.
  URL-encoded by `AlvysApiRoutes.TripDetail`, which calls `TripLookup.Validate()`.
- **Response** (`AlvysTrip`): the same projection returned by `trips/search`, additively
  extended with detail fields — `OrderNumber`, `TenderAsSubsidiaryType`,
  `RequiredEquipment[]`, `PickedUpAt`, `DeliveredAt`, `CarrierAssignedAt`, `ReleasedAt`,
  `CarrierPaidAt`, `DueDate`, `Driver1`/`Driver2`, `DispatcherId`, `DispatchedBy`,
  `ReleasedBy`, `CarrierSalesAgentId`, `CarrierPayOnHold`, `References[]`, `UpdatedAt`,
  `UpdatedBy`. `Temperature` and `RatesV2` are deliberately not modelled (tolerated as
  unknown JSON). A 404 degrades to `null`.

#### `trips/{tripId}/stops` → `GET /api/p/v{version}/trips/{tripId}/stops`

- **Request:** `tripId` path parameter only (URL-encoded by `AlvysApiRoutes.TripStops`).
  No body.
- **Response** (`AlvysTripStopDetail[]`): the upstream array is **polymorphic** — Alvys
  discriminates each stop with a `$type` of `appointment`, `delivery_window` or `waypoint`.
  Rather than three subclasses, the union of fields is flattened into one tolerant
  projection that preserves the `$type` in `Type` (`[JsonPropertyName("$type")]`): common
  fields (`Id`, `StopType`, `Status`, `Address` (`AlvysContextAddress`), `Coordinates`,
  `ArrivedAt`, `DepartedAt`, `Company*`, `References[]`), appointment fields
  (`AppointmentRequested`/`AppointmentConfirmed`/`AppointmentDate`/`ScheduleType`/
  `LoadingType`) and the `delivery_window`/`waypoint` `StopWindow` (`Begin`/`End`). Only
  the members relevant to a given `$type` are populated; a 404 degrades to `[]`.

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

### LTL matching context resources

The following five resources add the context the LTL candidate matcher needs:
locations (geography), drivers (assignment/readiness), dispatch preferences
(dispatcher/driver/truck/trailer pairing), customers (billing separation / customer
matching) and users (dispatcher display names/roles). All are **read-only** queries.

> Note on shared shapes: these resources return `ZipCode` (not `Zip`) and no country, so
> they use a dedicated `AlvysContextAddress`. Their notes use a richer shape than load
> notes (`id`/`Description`/`NoteType`/`Time`/`User`/`UserId`), modelled as
> `AlvysContextNote`. Both are distinct from the load `AlvysAddress`/`AlvysNote` types.

#### `dispatchpreferences/search` → `POST /api/p/v{version}/dispatchpreferences/search`

Dispatcher/driver/truck/trailer assignment pairings. **The upstream response is a bare
array** (not a paged envelope), so the client returns `IReadOnlyList<AlvysDispatchPreference>`
and the internal endpoint returns a bare JSON array.

- **Request** (`DispatchPreferenceSearchRequest`): all optional — `DispatcherIds[]`,
  `DriverIds[]`, `TruckIds[]`, `TrailerIds[]`, `UpdatedAtStart`, `UpdatedAtEnd`. No
  `PageSize` (the endpoint is not paged), so there is no local validation.
- **Response** (`AlvysDispatchPreference[]`): `UpdatedAt` (required), `DispatcherId?`,
  `Driver1Id?`, `Driver2Id?`, `TruckId?`, `TrailerId?`.

#### `locations/search` → `POST /api/p/v{version}/locations/search`

Pickup/delivery/hub/yard geography and shipper/consignee/warehouse context.

- **Request** (`LocationSearchRequest`): `Page` (0-based), `PageSize` (> 0), optional
  `Status[]` (Active/Disabled/Inactive), `LocationIds[]`, `CreatedDateRange`.
- **Local validation** (`Validate`): only `PageSize > 0`.
- **Response** (`AlvysLocationsResponse` = paged envelope; `Facets`/`Aggregations` and any
  unknown JSON are tolerated): `AlvysLocation` — `Id`, `Name`, `CompanyNumber`, `Type`,
  `Status`, `PhysicalAddress` (`AlvysContextAddress`), `Email[]`, `Phone[]`, `Fax?`,
  `DateCreated?`, `ExternalId?`, `Notes[]` (`AlvysContextNote`).

#### `drivers/search` → `POST /api/p/v{version}/drivers/search`

Driver assignment/readiness context.

- **Request** (`DriverSearchRequest`): `Page` (0-based), `PageSize` (> 0), optional
  `Status[]`, `Name`, `EmployeeId`, `FleetName`, `IsActive`.
- **Local validation** (`Validate`): only `PageSize > 0`. Alvys enforces the conditional-
  filter requirement server-side.
- **Response** (`AlvysDriversResponse` = paged envelope): `AlvysDriver` — `Id`,
  `EmployeeId?`, `PhoneNumber?`, `UserId?`, `Email?`, `Name`, `Type`, `SubsidiaryId`,
  `Address?` (`AlvysContextAddress`), `Status`, `IsActive`, license fields and expiries,
  `MedicalExpiresAt?`, `HiredAt?`, `TerminatedAt?`, `Notes[]` (`AlvysContextNote`),
  `Fleet?` (`AlvysFleet`), `References[]` (`AlvysDriverReference`), `CreatedAt`.

#### `customers/search` → `POST /api/p/v{version}/customers/search`

Billing separation, customer policy/approval and customer-specific matching context.

- **Request** (`CustomerSearchRequest`): `Page` (0-based), `PageSize` (> 0), `Statuses[]`
  (**required** by Alvys), optional `CreatedDateRange`.
- **Local validation** (`Validate`): only `PageSize > 0`. Alvys enforces the required
  `Statuses` server-side.
- **Response** (`AlvysCustomersResponse` = paged envelope): `AlvysCustomer` — `Id`, `Name`,
  `CompanyNumber`, `Type`, `Status`, `BillingAddress?` (`AlvysContextAddress`), `Email[]`,
  `Phone[]`, `Fax?`, `DateCreated?`, `InvoicingInformation?` (`AlvysInvoicingInformation`:
  address/emails/phone/invoicing names/`PaymentType`/`PaymentTermsInDays`), `ExternalId?`,
  `Contacts[]` (`AlvysCustomerContact`), `SalesAgentId?`, `Notes[]` (`AlvysContextNote`).

#### `users/search` → `POST /api/p/v{version}/users/search`

Dispatcher display names/roles/filters.

- **Request** (`UserSearchRequest`): `Page` (0-based), `PageSize` (> 0), optional `Keyword`.
- **Local validation** (`Validate`): only `PageSize > 0`.
- **Response** (`AlvysUsersResponse` = paged envelope): `AlvysUser` — `Id`, `UserName`,
  `Name`, `Email?`, `UserType`, `Role` (Admin/Dispatcher/Driver/Biller/SalesAgent/DataEntry/
  Safety/OperationManager), `Phone?`, `CompanyCode`, `Status` (Active/Disabled/Deleted),
  `Permissions[]`, `CreatedAt?`, `ModifiedAt?`. `Role`/`Status`/`UserType` are kept as
  strings (not enums) for tolerance, consistent with the other read models.

### `tenders/search` → `POST /api/p/v{version}/tenders/search` and `tenders/{tenderId}` → `GET …`

Tenders are inbound EDI/tender offers — a planning source for LTL detection/planning/
booking. **Read-only** — this slice issues a search and a single-record lookup only; no
tender is accepted, rejected, cancelled, updated or created, and there is no writeback.

- **Search request** (`TenderSearchRequest`): `Page` (0-based, **required**), `PageSize`
  (> 0, **required**), optional `Sort` (`Field`/`Direction`) and optional `Filter`
  (`Status[]`, `CreatedAtRange` `{Start, End?}`, `Type`, `Source`, `SourceCustomer`,
  `ShipmentId`, `LoadNumber`, `ExternalTenderId`). Unlike the load/trip filters (which are
  flat), the tender filter and sort are **nested objects** matching the Alvys schema.
- **Local validation** (`TenderSearchRequest.Validate`): only `PageSize > 0` is locally
  enforced.
- **Search response** (`AlvysTendersResponse` = paged `{ Page, PageSize, Total, Items[] }`;
  `Facets`/`Aggregations` and any unknown JSON are tolerated): `AlvysTender` projection —
  `Id`, `CompanyCode`, `Status`, `DateImported?`, `ShipmentId?`, `LoadNumber?`,
  `Equipment?` (`Number`/`Length`/`Type`), `Entities[]` (EDI party identity/address),
  `PaymentMethod?`, `QtyPallets?`, `SCAC?`, `Weight?`/`WeightUnitCode?`, `Volume?`/
  `VolumeUnitCode?`, `Rate?`, `ExpirationDate?`, `Notes[]`, `Stops[]`, `References[]`
  (`Id`/`Qualifier`/`Description`), `RoutingSequenceCode?`, `TransportationMethodTypeCode?`,
  `Etag?`. Alvys casing is preserved.
  - **Tender date-times** (`DateImported`, `ExpirationDate`, and the stop
    arrival/departure/scheduled fields) use the Alvys `AlvysTenderDateTime` wrapper
    (`{ DateTime, TimeZoneCode? }`) rather than a bare instant.
  - **Stops** (`AlvysTenderStop`): `StopId` (required), `Type` (required), `Entity?`,
    `SequenceNumber?`, `Orders[]` (`AlvysTenderOrderDetail`), `References[]`,
    `WeightQualifier?`, `ArrivedAt?`, `DepartedAt?`, `ScheduledArrivalStart?`,
    `ScheduledArrivalEnd?`, `StopReasonCode?`, `Notes[]`.
- **Get-by-id** (`GET /api/p/v{version}/tenders/{tenderId}` → `GetTenderByIdAsync`): returns
  the same `AlvysTender`. The `tenderId` is URL-encoded into a single path segment. A 404
  (or any non-success / transport error) degrades to `null` upstream, and the internal
  `GET /api/alvys/tenders/{tenderId}` returns **404** in that case — mirroring the
  graceful-degradation stance of the other read paths (no exception is thrown to callers).

> Note on shared shapes: the tender reference shape (`Id`/`Qualifier`/`Description`) differs
> from the load `AlvysReference` (`Type`/`Value`), so tenders use a dedicated
> `AlvysTenderReference`. Tender `Notes` are modelled as a `string[]` (EDI tender note lines).

### Load context: documents and notes

Load documents and load notes give dispatchers per-load confidence (rate confirmation /
POD / customer backup visibility), operational comments and audit context. Both are
**read-only** `GET` listings keyed by load number; the upstream Alvys responses are bare
arrays, so the client returns `IReadOnlyList<…>` and the internal endpoints return bare
JSON arrays. The `loadNumber` path segment is URL-encoded (`AlvysApiRoutes.BuildLoadSubresourcePath`).

> This slice does **not** add create/update/delete for notes or documents — no POST note
> creation, no document upload, and no `PUT`/`PATCH`/`DELETE`.

#### `loads/{loadNumber}/documents` → `GET /api/p/v{version}/loads/{loadNumber}/documents`

- **Request:** `loadNumber` path parameter only (URL-encoded). No body.
- **Response** (`AlvysLoadDocument[]`): `id` (lowercase), `AttachmentPath`,
  `AttachmentType`, `AttachmentSize?`, `UploadedAt?`, `ParentId`, `ParentType`,
  `UploadedBy?`, `DownloadUrl?`, `ExpiresAt?`. `DownloadUrl` is a time-limited link
  (`ExpiresAt`) returned as **data only** — documents are not fetched/downloaded server-side
  in this slice. Unknown JSON properties are tolerated.

#### `loads/{loadNumber}/notes` → `GET /api/p/v{version}/loads/{loadNumber}/notes`

- **Request:** `loadNumber` path parameter only (URL-encoded). No body.
- **Response** (`AlvysLoadNote[]`): `Id`, `Description`, `NoteType`, `CreatedAt?`,
  `CreatedBy`, `CreatedById?`. Distinct from the inline load `AlvysNote`
  (`Text`/`CreatedAt`/`CreatedBy`) carried on the load search projection. Unknown JSON
  properties are tolerated.

#### Alvys docs reviewed for this slice

The five context endpoints were modelled from the Alvys public-API reference (discovered
via `https://docs.alvys.com/llms.txt`):

- Dispatch preferences — `https://docs.alvys.com/reference/post_api-p-v-version-dispatchpreferences-search.md`
- Locations — `https://docs.alvys.com/reference/post_api-p-v-version-locations-search.md`
- Drivers — `https://docs.alvys.com/reference/post_api-p-v-version-drivers-search.md`
- Customers — `https://docs.alvys.com/reference/post_api-p-v-version-customers-search.md`
- Users — `https://docs.alvys.com/reference/post_api-p-v-version-users-search.md`

Load documents and notes were modelled from the same reference (docs index
`https://docs.alvys.com/llms.txt`):

- List load documents — `https://docs.alvys.com/reference/get_api-p-v-version-loads-loadnumber-documents.md`
- List load notes — `https://docs.alvys.com/reference/get_api-p-v-version-loads-loadnumber-notes.md`

Registered in `Program.cs` via `builder.Services.AddAlvysIntegration(builder.Configuration)`.

### Billing, visibility and equipment-event context

This slice adds three read-only resources that strengthen billing confidence, exception
detection and match reliability. All are queries only — no writeback, and missing/
unavailable fields are surfaced explicitly rather than defaulted.

#### `invoices/search` → `POST /api/p/v{version}/invoices/search` and `invoices?…` → `GET …`

Invoices are the authoritative billing record — they confirm whether a delivered load is
already invoiced, the invoice status, the remaining (unpaid) balance and the per-load
line items. The LTL billing-readiness service consumes invoices on the load **detail**
path to refine already-invoiced state and surface unpaid-balance risks.

- **Search request** (`InvoiceSearchRequest`): `Page` (0-based), `PageSize` (> 0,
  default 100), optional `InvoicedDateRange`/`InvoiceSentRange`/`PaidDateRange`
  (`{Start, End}`), `Status[]`, `LoadNumbers[]`, `PONumbers[]`, `OrderNumbers[]`,
  `CustomerId`. Conditional ranges are omitted from the body when null.
- **Local validation** (`InvoiceSearchRequest.Validate`): only `PageSize > 0`.
- **Search response** (`AlvysInvoicesResponse` = paged `{ Page, PageSize, Total, Items[] }`):
  `AlvysInvoice` projection — `Id`, `Number`, `Type`, `Status`, `CreatedDate`,
  `InvoicedDate`, `DueDate`, `PaidDate`, `Total` (`AlvysMoney` `{Amount, Currency}`),
  `AmountPaid`, `RemainingBalance`, `OverPaymentAmount`, `IsSubmitted`, `LastSendDate`,
  `SupplementalInvoiceType`, `Vendor`/`Customer` (`AlvysInvoiceParty`), `LineItems[]`,
  `Loads[]` (`AlvysInvoiceLoadRef`), `Payments[]`. Monetary/`bool` fields are nullable so a
  missing value is never silently a zero. Unknown JSON is tolerated.
- **Get-by-id/number** (`GET /api/p/v{version}/invoices?id=…|invoiceNumber=…` →
  `GetInvoiceAsync`): one of `id`/`invoiceNumber` is required (`InvoiceLookup.Validate`).
  The internal endpoint returns **400** when no criterion is supplied and **404** when the
  invoice is not found or upstream degrades to `null`.

#### `visibility/{inbound,outbound}/{loadNumber}/history` → `GET …`

Visibility history is the macropoint-style tracking event log for a load. The error/event
records are exception context (e.g. a failed visibility share is a signal worth surfacing).

- **Request:** `loadNumber` path parameter only (URL-encoded). No body.
- **Response** (`AlvysVisibilityHistoryEvent[]`): `Id`, `ExternalId`, `TripNumber`,
  `LoadNumber`, `EventType`, `SharedAt`, `Destination`, `TruckNumber`, `DriverName`,
  `TrailerNumber`, `StopId`, `LocationId`, `SharedBy`, `Reason`, `Address`
  (`AlvysContextAddress`), `Coordinates` (`{Latitude, Longitude}`), `Status`, `Error`. A
  404 (or any non-success/transport error) degrades to `[]`. Inbound and outbound are
  separate directions.

#### `trucks/events/search` / `trailers/events/search` → `POST …`

Equipment events (maintenance, out-of-service, inspections) inform match risk and
explanation. **Availability is never fabricated** — when event data is unavailable the
event list is simply empty and the matcher must not assume the equipment is free.

- **Truck request** (`TruckEventSearchRequest`): `StartDate` (**required**), optional
  `EndDate` (omitted when null), `TruckIds[]` (**required**, non-empty). **Trailer
  request** (`TrailerEventSearchRequest`): same with `TrailerIds[]`.
- **Local validation** (`Validate`): throws when `StartDate` is null or the id list is
  empty — enforced before any upstream call.
- **Response** (`AlvysTruckEvent[]` / `AlvysTrailerEvent[]`, bare array upstream so the
  client returns `IReadOnlyList<…>`): `Id`, `TruckId`/`TrailerId`, `Title`, `EventType`,
  `Description`, `StartDate`, `EndDate`, `Address` (`AlvysContextAddress`), `CreatedBy`,
  `CreatedAt`. A 404 degrades to `[]`.

#### Invoice-driven billing readiness

`BillingReadinessService.Evaluate(load, documents?, invoices?, carrierPayable?)` now optionally
consumes the load's invoices (fetched on the detail path, and in bulk on the Billing Worklist
path, via `SearchInvoicesAsync` by load number). When a matching invoice looks posted
(`IsSubmitted == true`, an `InvoicedDate`, or a posted `Status`), the load is treated as
already-invoiced (never ready-to-bill) even when the load status alone would not say so; any
invoice with a positive `RemainingBalance` surfaces as an unpaid-balance risk **and** contributes
to `BillingReadinessResult.UnpaidBalance`/`AgingBucket`/`AgingDays` — a standard
Current/1-30/31-60/61-90/90+ aging bucket computed from the oldest unpaid invoice's `DueDate`.
There is no dedicated Alvys "Aging Report" endpoint (confirmed against the full OpenAPI
reference — Alvys exposes invoice search/detail only); this is derived entirely from data already
fetched, not a new integration. Omitting invoices leaves the existing load-only inference
unchanged.

#### Carrier payable / gross margin

`carrierPayable` (optional) is the carrier's `TotalPayable` for the load's trip, fetched via
`SearchTripsAsync` filtered by `LoadNumbers` — on the load detail path (single trip lookup) and
in bulk on the Billing Worklist path (same shape as the invoice bulk-fetch, one paged call
sequence keyed by load number, not an N-call fan-out). There is likewise no dedicated Alvys
"Carrier Settlements" list endpoint (only write endpoints for factoring platforms to push carrier
invoices/payments *into* Alvys) — `Carrier.TotalPayable` on trips/search is the closest read
signal and is sufficient for a per-load margin check. When both revenue and `carrierPayable` are
known, `LtlLoadSummary.GrossMargin`/`GrossMarginPercent` are computed and a negative margin (or a
margin at/below `LtlOptions.MarginRiskThresholdPercent`, default 10%) surfaces as a billing risk.
Neither is ever inferred from a missing value.

**Fixed while wiring this up:** `AlvysPartyPay` (used for the `Carrier`/`Driver`/`Driver1`/
`Driver2`/`OwnerOperator` trip parties) previously mapped its itemized accessorial list from the
JSON key `Accessorials`, which the actual Alvys OpenAPI schema (`TripResponseCarrierResponse`)
uses for a *different*, required field — the aggregate money total (`{Amount, Currency}`). The
itemized list is under `AccessorialsDetails`. Because System.Text.Json throws on an object-vs-array
type mismatch by default, this meant **any trip response where a party's `Accessorials` total was
present would fail to deserialize** — a real crash risk on `GetTripAsync`/`SearchTripsAsync`
(including the raw `/api/alvys/trips/*` passthrough), not just a silently-wrong mapping. It had
gone uncaught because nothing in the LTL layer called these endpoints until now, and the existing
trip-deserialization test fixture didn't include a `Carrier` object. Fixed by renaming the list
property to `AccessorialsDetails` and adding `Linehaul`/`Accessorials`/`TotalPayable` (all
`AlvysMoney`) plus `CarrierInvoiceNumber`; covered by a new test using the real schema shape.

#### LTL decision-support signals (how the context reaches the user)

The read-only resources above feed three LTL-layer analyzers that turn raw Alvys data into
actionable, explicit signals in the `/ltl` console. All three honour the same rule: an
**absent** upstream signal is reported as `Unavailable`/`NotEvaluated`/`Missing`, never as a
favourable default.

- **Visibility → exceptions & detail timeline.** `VisibilityAnalyzer` reads inbound/outbound
  visibility history and projects a `VisibilityContext` (`Evaluated` + newest-first
  `events[]`). A share whose `Status` is a failure, or that carries a non-empty `Error`, is an
  `IsFailure` event. The load detail drawer renders failures as blocking risks and noteworthy
  milestones in an expandable timeline; failed shares also raise a non-blocking
  `VISIBILITY_FAILED` exception on `/api/ltl/exceptions`.
- **Equipment events → match risk & assignment warning.** `EquipmentEventAnalyzer.Assess`
  checks truck/trailer maintenance/out-of-service events that overlap the load window. When
  events were not fetched (or there is no window) the result is
  `EquipmentEventAssessment.NotEvaluated` and the matcher emits an **"Equipment availability"**
  factor with `Status = Unavailable` and `MaxPoints = 0` — i.e. excluded from the score
  denominator so an unknown never silently lowers a score. When evaluated with a conflict the
  factor scores `Weak` (Points 0, MaxPoints > 0) and assignment validation adds a non-blocking
  `EQUIPMENT_EVENT_CONFLICT` warning. Availability is **never** asserted from absent data.
- **Invoices → billing readiness.** As above, the detail/worklist billing readiness treats an
  already-posted invoice as not-ready-to-bill and surfaces a positive `RemainingBalance` as an
  unpaid-balance risk, keeping the existing badge set intact.

##### Known limitations

- **Bounded visibility enrichment on the list path.** `/api/ltl/exceptions` enriches only the
  first `LtlOptions.MaxVisibilityEnriched` scanned loads (default **25**) with visibility
  history to bound upstream calls. Visibility-only failures on loads beyond that cap are **not**
  reflected in the exceptions list — they still surface on the load **detail** path, which
  always fetches visibility for the single selected load.
- **Detail-path-only context.** Invoice billing refinement and per-load visibility are fetched
  on the load **detail** path, so search/worklist list rows carry `NotEvaluated` visibility and
  load-only billing inference until a load is opened.
- **Read-only.** None of these signals write back to Alvys; they are decision support only.

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

This integration phase is **read-only**. The internal API endpoints — the
`/api/alvys/{loads,trips,trailers,trucks,dispatch-preferences,locations,drivers,customers,users,tenders,invoices}/search`
and the `/api/alvys/{trucks,trailers}/events/search` searches plus the
`GET /api/alvys/tenders/{tenderId}` lookup, the
`GET /api/alvys/{loads,trips,invoices}?…` detail lookups, the
`GET /api/alvys/loads/{loadNumber}/{documents,notes}`, the
`GET /api/alvys/trips/{tripId}/stops` and the
`GET /api/alvys/visibility/{inbound,outbound}/{loadNumber}/history` listings — and the
upstream Alvys client calls issue queries only. Alvys models searches as `POST` (the filter set is the
request body) and exposes single-record reads and the sub-resource listings as `GET`,
but no data is created, updated or deleted and there is no writeback to Alvys. For tenders
specifically there is no accept/reject/cancel/update/create. No `PUT`/`PATCH`/`DELETE`, no
POST note/document creation, and no POST-mutation (other than the Alvys-defined search
`POST`) calls are made. Live Alvys remains the default source of truth; the `Fallback`
provider is opt-in for local/UAT and returns empty (shape-preserving) results.

## Sandbox-gated writeback boundary

The read-only stance above still holds for live Alvys: **no operation in this phase performs a
live Alvys mutation.** On top of the read integration there is now a deliberately-gated
*writeback boundary* that lets dispatchers build, validate and preview the write-oriented
operations of the Search → Match → Assign → Bill workflow without ever sending them upstream. It
exists so the operational UI can state, explicitly and per-operation, whether an action is
audit-only, simulation-only, eligible for sandbox execution, or unsupported — and exactly what is
required to enable it.

All under `src/LtlTool.Api/Features/Integrations/Alvys/Writeback/` (+ the
`AlvysOperationsController` in `src/LtlTool.Api/Features/Alvys/`):

| File | Purpose |
| --- | --- |
| `AlvysWriteOptions.cs` | `AlvysWritebackMode` (`Disabled`/`Simulation`/`Sandbox`) + config (`Environment`, `SandboxBaseUrl`) and the sandbox-recognition guards. Bound from `Alvys:Writeback`. |
| `AlvysWriteOperations.cs` | The operation catalogue (`AlvysWriteOperationRegistry`). Each descriptor records its workflow stage, whether it needs an ETag, its live-execution support, and what is required to enable it. |
| `AlvysOperationModels.cs` | Request/disposition/issue/payload/outcome contracts shared by dry-run and execute. |
| `AlvysWriteGateway.cs` | The single boundary every operation passes through: validates inputs, builds the preview payload, and decides the disposition from mode + live support. **Never calls Alvys.** |
| `AlvysSyncTracker.cs` | In-memory "last read sync" outcome/time/detail for the readiness snapshot. |
| `AlvysReadinessService.cs` | Computes the readiness snapshot: provider/credential/config readiness, active mode, per-operation eligibility, blockers. Surfaces no secrets. |
| `AlvysOperationsController.cs` | `/api/alvys/ops/*` endpoints (below), same `AllowedEmailDomain` policy as the rest of the API. |

### Modes

| Mode | Disposition | Meaning |
| --- | --- | --- |
| `Disabled` (default) | `AuditOnly` | Payload is built/recorded for audit only; never sent. Safe default for a fresh clone, CI and production. |
| `Simulation` | `Simulated` | Dry-run: payload built and validated for preview; never sent. |
| `Sandbox` | `SandboxExecuted` / `SandboxFailed` | Eligible for non-production sandbox execution when fully configured (recognised sandbox environment + non-production sandbox base URL + credentials). All eight operations below are wired to real Alvys endpoints; a non-2xx/transport response surfaces as `SandboxFailed` (HTTP 502), never a false success. |

`Sandbox` is refused unless `Environment` is one of `sandbox`/`uat`/`staging`/`test` and
`SandboxBaseUrl` is set to a non-production host (it explicitly rejects `integrations.alvys.com`),
so flipping the mode alone can never reach a production tenant. Production execution is tracked
separately in `docs/ltl-tool.md` — it requires a per-operation contract sign-off, not just a config
change, and today's sandbox client cannot reach `integrations.alvys.com` even if `Mode=Sandbox`.

### Operations (catalogue)

All eight operations are `Supported` for sandbox execution — each is wired to a real Alvys
endpoint confirmed against the Alvys API docs (`AlvysWriteClient.ResolveEndpoint`), not invented.
Live execution still requires `Mode=Sandbox` fully configured; until then every attempt resolves
to `AuditOnly`/`Simulated`.

| Code | Stage | ETag | Live support | Endpoint |
| --- | --- | --- | --- | --- |
| `create-load-note` | Assign/Bill | no | Supported | `POST loads/{loadNumber}/notes` |
| `tender-accept` | Match/Assign | yes | Supported | `POST tenders/{tenderId}/accept` — body `{ StopCompanyLinks, FleetId? }` |
| `trip-stop-arrival` | Assign | no | Supported | `PUT trips/{tripId}/stops/{stopId}/arrival` |
| `trip-stop-departure` | Assign | no | Supported | `PUT trips/{tripId}/stops/{stopId}/departure` |
| `load-update` | Assign/Bill | yes | Supported | `PATCH loads/{loadNumber}` — only `OrderNumber` (≤30 chars) is writable today |
| `trip-assign` | Assign | no | Supported | `POST trips/{tripId}/assign` — carrier required, driver/truck/trailer optional |
| `trip-dispatch` | Assign | no | Supported | `POST trips/{tripId}/dispatch` — trip must already be covered |
| `carrier-status-update` | Assign | yes | Supported | `PATCH carriers/{carrierId}/status` |

ETag-gated operations (`tender-accept`, `load-update`, `carrier-status-update`) are **blocked** at
validation time without an ETag, so a concurrent change can never be silently clobbered.

### Internal endpoints

`AlvysOperationsController`, route prefix `/api/alvys/ops`, `AllowedEmailDomain` policy
(unauthenticated → 401, not 404).

| Endpoint | Purpose |
| --- | --- |
| `GET /api/alvys/ops/status` | The readiness snapshot (mode, per-operation eligibility, blockers, last read sync). No secrets. |
| `GET /api/alvys/ops/operations` | The operation catalogue + live-support + required-to-enable docs. |
| `POST /api/alvys/ops/{operation}/dry-run` | Builds + validates the payload and returns the preview. 404 for an unknown operation. Never sent. |
| `POST /api/alvys/ops/{operation}/execute` | Honours the configured mode: audit-only/simulated when `Mode` isn't `Sandbox`, otherwise a real sandbox call. 404 unknown, 422 when validation blocks, 502 when the sandbox call fails. |
| `POST /api/alvys/ops/sync/probe` | Opt-in bounded **read** (`users/search`, page size 1) that records a "last successful read" time. A read, never a mutation. |

### Configuration

| Key | Env var | Default | Notes |
| --- | --- | --- | --- |
| `Alvys:Writeback:Mode` | `ALVYS_WRITEBACK_MODE` | `Disabled` | `Disabled`/`Simulation`/`Sandbox`. |
| `Alvys:Writeback:Environment` | `ALVYS_WRITEBACK_ENVIRONMENT` | _(empty)_ | Must be `sandbox`/`uat`/`staging`/`test` for sandbox mode. |
| `Alvys:Writeback:SandboxBaseUrl` | `ALVYS_WRITEBACK_SANDBOX_BASE_URL` | _(empty)_ | Non-production sandbox host. Rejects the production host. |

Writeback reuses the same server-side OAuth credentials as the read client (never surfaced to the
SPA) but a distinct sandbox base URL so sandbox traffic is physically separated from the read
source-of-truth host.

### Operational UI

The Assign/Bill drawer in the `/ltl` console renders an Alvys writeback-readiness panel
(`web/src/app/features/ltl/alvys-ops-panel.*`, served by `AlvysOpsService`) that shows the headline
posture ("Audit only" / "Simulation only" / "Ready for sandbox note/writeback"), the explicit
blockers, per-operation eligibility, and a dry-run payload preview for the load note. It consumes
`/api/alvys/ops/*` only — it never holds Alvys credentials and never writes to browser storage.

### What is required to actually see a sandbox execution

All eight operations are already wired (`LiveSupport = Supported`); what's still required is
operator configuration, not more code:

1. Non-production sandbox credentials and a sandbox base URL, with `ALVYS_WRITEBACK_MODE=Sandbox`
   and `ALVYS_WRITEBACK_ENVIRONMENT` set to a recognised sandbox label.

Until that's in place the boundary stays audit/simulation-only. See `docs/ltl-tool.md` for what's
additionally required before any operation may target a **production** Alvys tenant.

## Next slice

The read-only endpoints now cover loads (search + detail), trips (search + detail + stops),
equipment (trucks/trailers + events), the LTL matching context (locations, drivers, dispatch
preferences, customers, users), inbound tenders (search + by-id), invoices (search + detail)
and load visibility history (inbound/outbound) — enough to assemble a trip's route, read the
stop-1-to-billed lifecycle with billing confirmation, and start the LTL candidate matcher.
Invoices are already wired into billing readiness on the detail path. The recommended next
slice wires the remaining context into the domain logic: visibility-history errors into
exception detection, and equipment events into match risk/explanation (without fabricating
availability). Beyond that: the matcher domain logic (consolidation candidate detection
using load geography + customer billing separation + equipment/driver readiness), richer
normalized read models for the planner/booker views, and the Angular services that consume
`/api/alvys/*`. Writeback (booking/assignment) remains out of scope until the read-only
phase is signed off.
