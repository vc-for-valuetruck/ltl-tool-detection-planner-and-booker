"""Pydantic request/response models mirroring the ytl input schema.

All dimensions are in **inches** and all weights in **pounds** — the ytl API layer
hardcodes these units (see docs/yat-trailer-loading analysis). Callers convert upstream
if they work in metric.
"""

from __future__ import annotations

from typing import List, Optional

from pydantic import BaseModel, ConfigDict, Field


class ShipmentItem(BaseModel):
    """A single shipment line. `num_pieces` expands into N identical pieces."""

    model_config = ConfigDict(extra="allow")

    length: float = Field(..., gt=0, description="Piece length in inches")
    width: float = Field(..., gt=0, description="Piece width in inches")
    height: float = Field(..., gt=0, description="Piece height in inches")
    weight: float = Field(..., gt=0, description="Piece weight in pounds")
    packing: str = Field(..., description="Packing type: 'PALLET' or 'BOX'")
    stack_limit: int = Field(
        ..., gt=0, description="Max pieces in a vertical stack; 1 = not stackable"
    )
    num_pieces: int = Field(
        1, gt=0, description="Number of identical pieces this line expands into"
    )
    id: Optional[str] = Field(None, description="Optional passthrough identifier")
    desc: Optional[str] = Field(None, description="Optional passthrough description")
    commodity: Optional[str] = Field(None, description="Optional passthrough commodity")
    value: Optional[float] = Field(None, description="Optional passthrough declared value")


class TrailerDims(BaseModel):
    """Explicit trailer inner dimensions (inches) and max weight (pounds)."""

    inner_width: float = Field(..., gt=0, description="Trailer inner width in inches")
    inner_length: float = Field(..., gt=0, description="Trailer inner length in inches")
    inner_height: float = Field(..., gt=0, description="Trailer inner height in inches")
    max_weight: float = Field(..., gt=0, description="Trailer max payload in pounds")


class ShipmentOptimizationStage(BaseModel):
    """One stage of the shipment-arrangement pipeline."""

    model_config = ConfigDict(extra="allow")

    algorithm: str = Field(..., description="Shipment-arrangement algorithm key")
    max_iter: Optional[int] = Field(None, description="Max iterations for this stage")
    timeout: Optional[float] = Field(None, description="Per-stage timeout in seconds")


class OptimizeLoadRequest(BaseModel):
    """Request body for POST /optimize-load.

    Provide exactly one of `equipment_code` (a STANDARD_TRAILER_DIMS code) or
    `trailer_dims` (explicit dimensions).
    """

    model_config = ConfigDict(extra="forbid")

    shipment_list: List[ShipmentItem] = Field(
        ..., min_length=1, description="Shipment lines to load"
    )
    equipment_code: Optional[str] = Field(
        None, description="Standard equipment code, e.g. DV_53 (see GET /equipment-types)"
    )
    trailer_dims: Optional[TrailerDims] = Field(
        None, description="Explicit trailer dimensions; alternative to equipment_code"
    )
    allow_rotations: bool = Field(
        True, description="Allow 90-degree rotation in the length x width plane"
    )
    overweight_shipment_threshold: Optional[float] = Field(
        None, description="Pieces heavier than this (lbs) are placed at the trailer rear"
    )
    piece_arrangement_algorithm: Optional[str] = Field(
        None, description="Piece-arrangement algorithm key (default GREEDY_STACK)"
    )
    shipment_optimization_ls: Optional[List[ShipmentOptimizationStage]] = Field(
        None, description="Ordered shipment-arrangement pipeline; default used if omitted"
    )
    seed: Optional[int] = Field(
        None,
        description="Seed numpy.random for a reproducible plan (same request -> same load_order)",
    )
    max_seconds: Optional[float] = Field(
        None, gt=0, description="Wall-clock budget for the optimization (seconds)"
    )


class HealthResponse(BaseModel):
    status: str
    version: str
