"""FastAPI wrapper around the vendored ytl trailer-load optimizer.

Phase 2 Milestone 2 packing sidecar. This service is a decision-support sidecar: it
computes an explainable trailer load plan from shipment geometry. It ingests NO Alvys
data and holds no state — the .NET dispatcher passes shipment dimensions/weights in
inches+pounds and receives a load plan back. See services/trailer-fit/README.md.
"""

from __future__ import annotations

import logging

from fastapi import FastAPI, Response
from fastapi.responses import JSONResponse

from ytl import STANDARD_TRAILER_DIMS, optimize_trailer_load_plan_wrapper

from .models import HealthResponse, OptimizeLoadRequest

SERVICE_VERSION = "0.1.0"

# Structured-ish logging: one line per event, JSON-shaped where the ytl layer emits it.
logging.basicConfig(
    level=logging.INFO,
    format='{"time":"%(asctime)s","level":"%(levelname)s","logger":"%(name)s","msg":%(message)s}',
)
logger = logging.getLogger("trailer_fit.app")

app = FastAPI(
    title="Value Truck Trailer-Fit Sidecar",
    version=SERVICE_VERSION,
    description=(
        "Containerized wrapper around the MIT-licensed ytl (yat-trailer-loading) 3D "
        "packing optimizer. Computes a linear-feet-minimizing trailer load plan from "
        "shipment geometry. Inputs are inches + pounds. Ingests no Alvys data."
    ),
)


@app.get("/health", response_model=HealthResponse, tags=["ops"])
def health() -> HealthResponse:
    """Liveness/readiness probe."""
    return HealthResponse(status="ok", version=SERVICE_VERSION)


@app.get("/equipment-types", tags=["reference"])
def equipment_types() -> list:
    """Return the predefined standard trailer/equipment dimensions (STANDARD_TRAILER_DIMS)."""
    return STANDARD_TRAILER_DIMS


@app.post("/optimize-load", tags=["optimize"])
def optimize_load(request: OptimizeLoadRequest) -> Response:
    """Compute a trailer load plan.

    Delegates to ytl's `optimize_trailer_load_plan_wrapper`, returning its body with the
    exact HTTP status code the wrapper produced (200 success; 4xx invalid input; 5xx /
    503 optimization failure or timeout).
    """
    # Serialize back to the plain dict the ytl wrapper expects. exclude_none keeps the
    # request payload clean so ytl's own defaults apply for omitted optional fields.
    request_data = request.model_dump(exclude_none=True)
    status_code, body = optimize_trailer_load_plan_wrapper(request_data)
    return JSONResponse(status_code=status_code, content=body)
