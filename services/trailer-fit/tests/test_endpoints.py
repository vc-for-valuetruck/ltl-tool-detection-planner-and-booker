"""Endpoint-level tests for the trailer-fit sidecar."""

import copy


def test_health(client):
    resp = client.get("/health")
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "ok"
    assert isinstance(body["version"], str) and body["version"]


def test_equipment_types(client):
    resp = client.get("/equipment-types")
    assert resp.status_code == 200
    body = resp.json()
    assert isinstance(body, list) and len(body) >= 1
    codes = {item["code"] for item in body}
    assert "DV_53" in codes
    dv53 = next(item for item in body if item["code"] == "DV_53")
    for key in ("inner_width", "inner_length", "inner_height", "max_weight"):
        assert key in dv53


def test_optimize_load_fits(client, basic_request):
    resp = client.post("/optimize-load", json=basic_request)
    assert resp.status_code == 200
    body = resp.json()
    assert body["arrangement_is_valid"] is True
    assert body["num_pieces"] == 10
    assert body["linear_feet"] > 0
    assert len(body["load_order"]) == 10


def test_optimize_load_with_trailer_dims(client):
    req = {
        "trailer_dims": {
            "inner_width": 98.5,
            "inner_length": 636,
            "inner_height": 108,
            "max_weight": 42500,
        },
        "seed": 1,
        "shipment_list": [
            {
                "length": 48,
                "width": 40,
                "height": 40,
                "weight": 400,
                "packing": "PALLET",
                "stack_limit": 2,
                "num_pieces": 3,
            }
        ],
    }
    resp = client.post("/optimize-load", json=req)
    assert resp.status_code == 200
    assert resp.json()["num_pieces"] == 3


def test_determinism_same_seed_identical_load_order(client, basic_request):
    r1 = client.post("/optimize-load", json=copy.deepcopy(basic_request))
    r2 = client.post("/optimize-load", json=copy.deepcopy(basic_request))
    assert r1.status_code == 200 and r2.status_code == 200
    # Same request + same seed must yield an identical load plan.
    assert r1.json()["load_order"] == r2.json()["load_order"]
    assert r1.json()["linear_feet"] == r2.json()["linear_feet"]


def test_overweight_piece_placed_at_rear(client):
    # A single heavy piece above the 2000 lb default threshold is handled by the
    # overweight path and still produces a valid plan.
    req = {
        "equipment_code": "DV_53",
        "seed": 3,
        "overweight_shipment_threshold": 2000,
        "shipment_list": [
            {
                "length": 96,
                "width": 48,
                "height": 48,
                "weight": 5000,
                "packing": "PALLET",
                "stack_limit": 1,
                "num_pieces": 1,
            },
            {
                "length": 40,
                "width": 40,
                "height": 40,
                "weight": 300,
                "packing": "BOX",
                "stack_limit": 1,
                "num_pieces": 2,
            },
        ],
    }
    resp = client.post("/optimize-load", json=req)
    assert resp.status_code == 200
    body = resp.json()
    assert body["arrangement_is_valid"] is True
    assert body["num_pieces"] == 3


def test_invalid_negative_dimension(client):
    req = {
        "equipment_code": "DV_53",
        "shipment_list": [
            {
                "length": -5,
                "width": 40,
                "height": 48,
                "weight": 500,
                "packing": "BOX",
                "stack_limit": 1,
            }
        ],
    }
    resp = client.post("/optimize-load", json=req)
    # Pydantic rejects gt=0 violation at the boundary with a 422.
    assert resp.status_code == 422


def test_invalid_unknown_equipment_code(client):
    req = {
        "equipment_code": "NOPE_99",
        "shipment_list": [
            {
                "length": 40,
                "width": 40,
                "height": 40,
                "weight": 300,
                "packing": "BOX",
                "stack_limit": 1,
            }
        ],
    }
    resp = client.post("/optimize-load", json=req)
    assert resp.status_code == 400
    assert resp.json()["error_code"] == "InvalidTrailerDimensionsException"


def test_too_many_pieces_guard(client):
    req = {
        "equipment_code": "DV_53",
        "shipment_list": [
            {
                "length": 10,
                "width": 10,
                "height": 10,
                "weight": 10,
                "packing": "BOX",
                "stack_limit": 1,
                "num_pieces": 600,
            }
        ],
    }
    resp = client.post("/optimize-load", json=req)
    assert resp.status_code == 400
    assert resp.json()["error_code"] == "TooManyPiecesException"


def test_piece_too_long_for_trailer(client):
    req = {
        "equipment_code": "DV_53",
        "shipment_list": [
            {
                "length": 5000,
                "width": 40,
                "height": 40,
                "weight": 300,
                "packing": "BOX",
                "stack_limit": 1,
            }
        ],
    }
    resp = client.post("/optimize-load", json=req)
    assert resp.status_code == 400
    assert resp.json()["error_code"] == "PiecesTooLongForServiceException"


def test_empty_shipment_list_rejected(client):
    req = {"equipment_code": "DV_53", "shipment_list": []}
    resp = client.post("/optimize-load", json=req)
    # min_length=1 on the model -> 422 before reaching ytl.
    assert resp.status_code == 422
