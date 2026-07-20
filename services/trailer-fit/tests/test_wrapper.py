"""Tests exercising the vendored ytl wrapper directly (below the HTTP layer).

These lock in the local patches: numpy-2-safe JSON encoding, seed determinism, the
piece-count guard, and the timeout guard.
"""

import copy

import numpy as np
import pytest

from ytl import optimize_trailer_load_plan_wrapper
from ytl.services.trailer_load_api import NumpyArrayEncoder, _run_with_timeout


def _req(**overrides):
    base = {
        "equipment_code": "DV_53",
        "seed": 999,
        "shipment_list": [
            {
                "length": 48,
                "width": 40,
                "height": 48,
                "weight": 500,
                "packing": "PALLET",
                "stack_limit": 2,
                "num_pieces": 5,
            }
        ],
    }
    base.update(overrides)
    return base


def test_seed_determinism_wrapper():
    c1, b1 = optimize_trailer_load_plan_wrapper(copy.deepcopy(_req()))
    c2, b2 = optimize_trailer_load_plan_wrapper(copy.deepcopy(_req()))
    assert c1 == 200 and c2 == 200
    assert b1["load_order"] == b2["load_order"]


def test_numpy_encoder_handles_numpy2_scalars():
    import json

    payload = {
        "i": np.int64(3),
        "f": np.float64(2.5),
        "b": np.bool_(True),
    }
    out = json.loads(json.dumps(payload, cls=NumpyArrayEncoder))
    assert out == {"i": 3, "f": 2.5, "b": True}


def test_invalid_seed_returns_400():
    code, body = optimize_trailer_load_plan_wrapper(_req(seed="not-an-int"))
    assert code == 400
    assert body["error_code"] == "InvalidRequestException"


def test_invalid_max_seconds_returns_400():
    code, body = optimize_trailer_load_plan_wrapper(_req(max_seconds="soon"))
    assert code == 400


def test_timeout_guard_trips():
    code, body = optimize_trailer_load_plan_wrapper(_req(max_seconds=0.0001))
    assert code == 503
    assert body["error_code"] == "OptimizationTimeoutException"


def test_run_with_timeout_reraises_inner_error():
    def boom():
        raise ValueError("inner failure")

    with pytest.raises(ValueError, match="inner failure"):
        _run_with_timeout(boom, 5)


def test_run_with_timeout_passthrough_when_no_budget():
    assert _run_with_timeout(lambda: 41 + 1, None) == 42
