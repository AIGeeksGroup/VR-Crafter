# VR-Crafter backend

This FastAPI service receives an image from the Unity client and returns either
the bundled GLB test model, a deterministic voxel house, or both. The current
voxel generator is a fixture and does not infer geometry from the uploaded
image yet.

## Setup

From the repository root:

```powershell
cd backend
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
python -m uvicorn app.main:app --reload --host 127.0.0.1 --port 8000
```

Open `http://127.0.0.1:8000/status` to verify the service.

## API

- `GET /status` and `GET /health`: service and model availability.
- `GET /model`: download the first available GLB in `models/`.
- `POST /generate`: multipart upload with `file`, optional `response_mode`
  (`model` by default, `voxels`, or `both`), and optional `max_voxels`.

Uploads are written to `data/received_images/` and ignored by Git. Reference
input images are kept in `examples/`; the test GLB is kept in `models/`.

See [UNITY_INTEGRATION.md](UNITY_INTEGRATION.md) for the Unity request flow,
verification results, and the remaining Play Mode integration work.
