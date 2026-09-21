# Unity–Backend Integration

## Current flow

1. Enter Play Mode in Unity.
2. `QuestProLocalTestServer` starts the FastAPI app from `backend/app/main.py`.
3. Unity sends a PNG to `POST /generate` with `response_mode=both`.
4. The backend returns a GLB URL and voxel JSON in one response.
5. Unity loads the GLB with glTFast and builds the voxel object.
6. The user can move individual voxels in VR.

The current backend is a communication fixture: it stores the uploaded image,
returns `models/PropertyDemoModel.glb`, and generates a deterministic voxel
house. The image is not used for AI inference yet.

## Local setup

From the repository root:

```powershell
cd backend
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
```

Unity automatically uses `backend/.venv/Scripts/python.exe` when it exists.
Another Python executable can be selected from
`Tools > Quest Pro > Select Python for VR-Crafter Backend`.

The active scene uses `Both` mode and connects to
`http://127.0.0.1:8000/generate`. This address is correct for Unity Editor and
Quest Link. A standalone Quest build must use the development computer's LAN
address, while Uvicorn listens on `0.0.0.0`.

## Verified behavior

- Unity 6000.3.15f1 compiles the integration scripts.
- `/status` reports the backend and model as available.
- A real multipart PNG upload returns 9,791 voxels on a 64³ grid.
- The GLB URL downloads the expected model.

## Next phase goal

Create a closed VR co-creation loop. The user should be able to modify the
generated voxels in Unity, send the edited result back to the backend, and ask
the generator to continue from that exact state without losing previous work.

To support this goal, each result will need a generation ID, revision history,
stable voxel IDs, and a model URL tied to the same voxel state.
