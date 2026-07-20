"""Export the FastAPI OpenAPI spec to services/trailer-fit/openapi.json.

Run from the service root:  python scripts/export_openapi.py
Keeps the committed openapi.json artifact in sync so the .NET side can generate a typed
client (NSwag/Kiota) without booting the service.
"""

from __future__ import annotations

import json
import pathlib
import sys

# Ensure the service root is importable when run as a script.
SERVICE_ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SERVICE_ROOT))

from app.main import app  # noqa: E402

OUTPUT_PATH = SERVICE_ROOT / "openapi.json"


def main() -> None:
    spec = app.openapi()
    OUTPUT_PATH.write_text(json.dumps(spec, indent=2, sort_keys=True) + "\n")
    print(f"Wrote OpenAPI spec to {OUTPUT_PATH}")


if __name__ == "__main__":
    main()
