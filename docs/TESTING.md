# Testing

## API (.NET)

```bash
dotnet test
```

Runs the xUnit suite in `src/LtlTool.Api.Tests`. CI runs the same against
`-c Release` (see `.github/workflows/ci.yml`).

### Alvys integration tests

The Alvys tests live in `src/LtlTool.Api.Tests/Alvys/` and run fully offline — no
live Alvys tenant or network access is required. HTTP is faked with hand-rolled test
doubles (`AlvysTestDoubles.cs`): `StubHttpMessageHandler` returns scripted responses
and records the requests/bodies it received, `StubHttpClientFactory` hands out clients
backed by it, and `CapturingLogger<T>` captures log output for assertions.

Coverage:

- **Options binding** (`AlvysOptionsBindingTests`) — config binds to `AlvysOptions`,
  `Provider` defaults to `Live`, `HasCredentials` requires both id and secret.
- **Provider selection** (`AlvysProviderSelectionTests`) — DI resolves the live
  `AlvysClient` by default and when `Provider=Live`, and only resolves
  `FallbackAlvysClient` when `Provider=Fallback` is explicitly set. Live stays the
  default even when credentials are absent. The fallback returns empty paged shapes
  for both loads and trips search.
- **Token provider** (`AlvysTokenProviderTests`) — token is acquired and cached
  (one network call for repeated reads), missing credentials throw before any network
  call, and a failed token request **never logs the client secret** (logs status only).
- **Route/version normalization** (`AlvysApiRoutesTests`) — `loads/search` and
  `trips/search` build `/api/p/v{version}/...` relative paths, and the configured
  version is normalized so `v2.0` and `2.0` both yield `v2.0` (no double `v`).
- **Loads & trips search** (`AlvysClientTests`) — paged responses parse for both
  endpoints, the bearer token is attached, 1-based pages translate to Alvys 0-based
  pages, requests target the versioned path, and only supplied filters serialize
  (PascalCase, nulls omitted). Request validation (`PageSize > 0`, `LoadNumbers ≤ 150`)
  throws **before** any network call, and non-success responses (incl. 429) surface as
  empty results / `null` instead of throwing.

## Source-of-truth & fallback behavior under test

- **Live is the default source of truth.** Provider-selection tests assert that the
  live client is wired by default and whenever `Provider=Live`, including when no
  credentials are present (so a fresh clone / CI still boots).
- **Fallback is opt-in only.** The `FallbackAlvysClient` (empty results) is resolved
  only when `Alvys:Provider=Fallback` is explicitly configured — it is never the
  default. Use it for local development or UAT without a live Alvys tenant via
  `ALVYS_PROVIDER=Fallback` (the demo compose override sets this automatically).
- **No secret leakage.** A dedicated test feeds the secret back in a failed token
  response body and asserts it never reaches the logs.

## Web (Angular)

```bash
cd web
npm ci
npm run build -- --configuration production
```
