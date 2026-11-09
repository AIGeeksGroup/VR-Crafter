# VR-Crafter

VR-Crafter turns a single photo into a 3D asset and lets a user refine it in VR
by editing a 64³ voxel grid. The backend runs
[Microsoft TRELLIS.2](https://github.com/microsoft/TRELLIS.2); the client is a
Unity/Quest project.

This repository contains both sides:

- **Backend** (`backend/`): FastAPI service that generates voxels + a GLB,
  stores numbered revisions, and regenerates the GLB after VR edits.
- **Unity client** (`Assets/`): loads the voxels and GLB, aligns them, provides
  Move / Fill / Erase / Add editing, and submits the edited grid.

## How it works

```
Unity (photo) ── POST /generate ──▶ FastAPI ──▶ TRELLIS.2
                                      │
                                      ├─ 64³ voxels
                                      ├─ textured GLB
                                      └─ save revision 0
Unity ◀── voxels + model_url ─────────┘
Unity ── GET model.glb ───────────────▶ downloads the GLB
VR edits voxels
Unity ── POST /revisions ─────────────▶ validate → regenerate GLB
Unity ◀── voxels + new model_url ──────┘ save revision n+1
```

One `POST /generate` produces both representations. The JSON response carries
the voxels and a `model_url`; the GLB binary is downloaded by a second `GET`.

## Status

| Area | State |
|---|---|
| Backend fixture mode, API, storage | Tested: 62 passed, 1 skipped (GPU test) |
| TRELLIS.2 generator | **Verified on an L40S** (2026-10-26): 6,899 voxels, 22.2 MB GLB, 3.2 s + 86.3 s, 14.5 GB peak VRAM |
| `/generate` and `/revisions` | Verified with real requests, including revision 1 |
| Unity client | **Editor Play Mode verified** on Unity 6000.6.3f1: GLB loaded, 13,676 voxels displayed |
| Six Unity validation commands | Not run yet |
| Headset (alignment, facing, frame rate) | Not verified yet |
| Production deployment (auth, async queue, NAS) | Not implemented |

The project targets **Unity 6000.3.15f1**. The 6000.6 run used test-copy
workarounds documented in [`backend/RUNBOOK-L40S.md`](backend/RUNBOOK-L40S.md);
do not treat 6000.6 as the supported version.

Verification evidence, numbers and images:
[`verification/`](verification/README.md).

## Quick start

### Backend, fixture mode (no GPU)

```bash
cd backend
python -m venv .venv
source .venv/bin/activate        # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Check `http://127.0.0.1:8000/status`: `"generator": "fixture"`.

### Backend, TRELLIS.2 (Linux + NVIDIA GPU)

```bash
cd backend
VRCRAFTER_GENERATOR=trellis2 \
VRCRAFTER_DATA_DIR=./data/generations \
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Model download, CUDA extensions, offline paths and the two compatibility fixes
are documented in [`backend/RUNBOOK-L40S.md`](backend/RUNBOOK-L40S.md) and
[`backend/README.md`](backend/README.md).

### Unity

1. Open the project with Unity 6000.3.15f1.
2. Run `Tools → Quest Pro → Configure VR Voxel Scene`.
3. Point `Generate Url` at the backend, e.g.
   `http://127.0.0.1:8000/generate`.
4. Enter Play Mode (Quest Link for VR; the scene also has a desktop preview).

Details: [`README-QuestPro.md`](README-QuestPro.md) and
[`backend/UNITY_INTEGRATION.md`](backend/UNITY_INTEGRATION.md).

## API

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/generate` | Upload an image; return voxels + `model_url` |
| `POST` | `/generations/{id}/revisions` | Submit the full edited voxel set; return revision n+1 |
| `GET` | `/generations/{id}/revisions/{n}/model.glb` | Download a revision's GLB |
| `GET` | `/generations/{id}` | Read the latest committed revision |
| `GET` | `/status` | Health and active generator |
| `GET` | `/health` | Liveness |

Error responses use HTTP 200 with `{"status": "error", "error": "..."}` so the
Unity client can display the server message.

## Repository layout

```
Assets/                  Unity project (scenes, scripts, editor validation)
Packages/                Embedded Meta XR Core package
backend/
  app/                   FastAPI app, TRELLIS.2 pipeline, storage, BiRefNet
  tests/                 Backend tests (fixture mode)
  third_party/           Vendored TRELLIS.2 and BiRefNet
  RUNBOOK-L40S.md        GPU setup and first-run record
verification/            Generated previews, response JSON, alignment check
README-Implementation.md Full design and change log
README-QuestPro.md       Unity controls and validation guide
```

## Documentation

- [Implementation notes](README-Implementation.md) — what each part does and
  what is verified.
- [Quest Pro guide](README-QuestPro.md) — controls, scene setup, validation.
- [Backend README](backend/README.md) — API, storage, environment variables.
- [L40S runbook](backend/RUNBOOK-L40S.md) — GPU environment, model sources,
  compatibility fixes, measured results.
- [Unity integration](backend/UNITY_INTEGRATION.md) — coordinate mapping and
  GLB alignment.

## License and third-party code

TRELLIS.2 is MIT-licensed; its source is vendored under
`backend/third_party/TRELLIS.2/` (see `backend/third_party/README.md`).
BiRefNet is MIT-licensed and vendored under `backend/third_party/BiRefNet/`.
