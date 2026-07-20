# Trailer-Fit Sidecar (Phase 2 · Milestone 2)

A small, stateless FastAPI microservice that computes a **trailer load plan** (3D
packing) from shipment geometry. It wraps the MIT-licensed
[`ytl` / yat-trailer-loading](https://github.com/yat-co/yat-trailer-loading) optimizer,
which minimizes the **linear feet** a set of pieces occupies in a trailer.

This is a decision-support sidecar for the LTL tool's Search → Match → Assign → Bill
workflow (specifically consolidation / partial-truckload planning). It **ingests no
Alvys data and holds no state** — the .NET dispatcher passes shipment
dimensions/weights (inches + pounds) in the request body and gets a load plan back. All
operational data continues to originate from Alvys upstream.

## Provenance & license

- Upstream: `yat-co/yat-trailer-loading`, `main` @ `c0e3b08` (v1.0.1). MIT © 2022 YAT.
- The vendored package includes a copy of `py3dbp` (enzoruiz, MIT) under `ytl/py3dbp/`.
- The upstream MIT license is retained verbatim at [`ytl/LICENSE`](ytl/LICENSE). No
  copyleft obligations; attribution notices are preserved.

### Local patches applied to the vendored source

The `ytl/` tree is upstream code with the minimal changes needed to run headless and
safely as a service:

1. **NumPy 2 compatibility** — `np.Infinity → np.inf` (greedy shipment arranger); the
   JSON encoder now uses `np.integer`/`np.floating`/`np.bool_` instead of the removed
   `np.int_`/`np.float_` aliases. (The container still pins `numpy<2`.)
2. **Optional matplotlib** — the module-level `from matplotlib import pyplot` imports are
   now guarded. The headless server never imports matplotlib; `plot()`/`fill()` raise a
   clear error if called without it installed.
3. **Deterministic plans** — optional `seed` request field seeds `numpy.random`, so an
   identical request yields an identical `load_order`.
4. **Structured logging + root-cause errors** — the API wrapper logs JSON-shaped events
   and surfaces the underlying exception detail instead of a bare label.
5. **Input guards** — a hard **max 500 pieces** cap and a per-request wall-clock
   **timeout** (`max_seconds`, default 25s → HTTP 503 on overrun).

## API

| Method | Path               | Description                                                        |
|--------|--------------------|--------------------------------------------------------------------|
| POST   | `/optimize-load`   | Compute a load plan. Returns the ytl wrapper's body + status code. |
| GET    | `/health`          | `{ "status": "ok", "version": "..." }`                             |
| GET    | `/equipment-types` | The predefined `STANDARD_TRAILER_DIMS` equipment list.             |

The committed [`openapi.json`](openapi.json) is the contract (regenerate with
`python scripts/export_openapi.py`). Generate a typed C# client from it (NSwag/Kiota).

### Request (inches + pounds)

```json
{
  "equipment_code": "DV_53",
  "seed": 42,
  "shipment_list": [
    {"length": 48, "width": 40, "height": 48, "weight": 500, "packing": "PALLET", "stack_limit": 2, "num_pieces": 6},
    {"length": 30, "width": 30, "height": 30, "weight": 200, "packing": "BOX", "stack_limit": 3, "num_pieces": 4}
  ]
}
```

Provide **either** `equipment_code` (a code from `/equipment-types`) **or**
`trailer_dims` (`inner_width`/`inner_length`/`inner_height`/`max_weight`).

## Run locally

```bash
cd services/trailer-fit
python -m venv .venv && source .venv/bin/activate
pip install -r requirements-dev.txt          # runtime + pytest/ruff
uvicorn app.main:app --reload --port 8080     # http://127.0.0.1:8080/docs
```

> The pinned `requirements.txt` targets Python 3.12 (matches the Docker image + CI). On
> newer interpreters some pinned wheels may be unavailable; use 3.12 for a faithful env.

### Test & lint

```bash
pytest
ruff check
python scripts/export_openapi.py   # keep openapi.json in sync
```

## Run in Docker

```bash
cd services/trailer-fit
docker build -t trailer-fit .
docker run --rm -p 8080:8080 trailer-fit
curl localhost:8080/health
```

The image uses `python:3.12-slim`, pins `numpy<2`, runs as a non-root user, and defines
a `HEALTHCHECK` against `/health`.

## Limitations

- The optimizer is a **heuristic** (not exact) and models only a single trailer
  `max_weight` cap — **no axle-group limits, weight distribution, or bridge-formula
  compliance**. Any axle/compliance logic must be layered downstream in the .NET
  pipeline using the returned `load_order` positions + weights.
- No Alvys reads/writes. This service is pure geometry in / plan out.
