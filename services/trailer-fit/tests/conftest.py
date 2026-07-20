import pathlib
import sys

import pytest
from fastapi.testclient import TestClient

# Make the service root importable (app/, ytl/).
SERVICE_ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SERVICE_ROOT))

from app.main import app  # noqa: E402


@pytest.fixture(scope="session")
def client() -> TestClient:
    return TestClient(app)


@pytest.fixture
def basic_request() -> dict:
    """A small, well-formed load that fits comfortably in a 53' dry van."""
    return {
        "equipment_code": "DV_53",
        "seed": 12345,
        "shipment_list": [
            {
                "length": 48,
                "width": 40,
                "height": 48,
                "weight": 500,
                "packing": "PALLET",
                "stack_limit": 2,
                "num_pieces": 6,
            },
            {
                "length": 30,
                "width": 30,
                "height": 30,
                "weight": 200,
                "packing": "BOX",
                "stack_limit": 3,
                "num_pieces": 4,
            },
        ],
    }
