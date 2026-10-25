# Unity–Backend Integration

## Current flow

1. Enter Play Mode in Unity. `QuestProLocalTestServer` starts the fixture
   backend from `backend/app/main.py` unless something already answers on
   port 8000 (for example a TRELLIS.2 backend through a tunnel).
2. Click **Get 3D Model**. Unity sends a PNG to `POST /generate` with
   `response_mode=both`.
3. One response carries the `generation_id`, `revision` 0, the voxels (each with
   a stable `id`) and the `model_url` of that revision's GLB.
4. Unity builds the voxel object and loads the GLB with glTFast, scaled and
   offset to overlap the voxels.
5. The user edits in VR: Grip selects voxels, then Move, Fill, Erase or Add
   (controls in [../README-QuestPro.md](../README-QuestPro.md)).
6. **Submit Edits** posts the complete voxel list to
   `POST /generations/{generation_id}/revisions` with the `base_revision`.
7. The backend answers with the next revision: new voxels with assigned ids and
   a new `model_url`. Unity rebuilds both from the response and the status bar
   shows `Revision {n}`.

With `VRCRAFTER_GENERATOR=fixture` (the default) the backend ignores the image
and returns a deterministic voxel house and the bundled GLB, so the loop can be
tested without a GPU. With `trellis2`, structure sampling gives the voxels and
the 1024 pipeline gives a GLB for exactly the submitted voxels. Check
`generator` in `/status` to see which one is running. The API is described in
[README.md](README.md).

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

Verified earlier with the original fixture: Unity 6000.3.15f1 compiled the
integration scripts, a real multipart PNG upload returned 9,791 voxels on a
64³ grid, and the GLB URL downloaded the expected model.

The backend (generation store, revisions API, fixture generator) is covered by
the pytest suite in `backend/tests`.

## Next phase: status

Goal: a closed VR co-creation loop where the user edits the generated voxels,
sends the result back, and the generator continues from that exact state.

Implemented:

- Generation ID, and a revision history on disk (`data/generations/`).
- Stable voxel IDs, kept across revisions; new voxels get an id from the
  backend.
- A model URL tied to each revision's voxel state.
- Submit Edits, and the generator's `mesh` step run on the edited voxels
  (TRELLIS.2 reuses the stored image and seed).

Still unverified (none of this has been run):

- All Unity validations on Windows (commands in
  [../README-QuestPro.md](../README-QuestPro.md)); Unity itself has not run the
  new editor code.
- The TRELLIS.2 GPU path: o-voxel and trimesh are not installed, the weights
  are not downloaded, and no timing, voxel count or VRAM has been measured.
- Whether the GLB and the voxels overlap, and whether the model's front faces
  the user, in the headset. The alignment assumes glTFast flips the X axis on
  import. If the front is wrong, rotate the mapping and the GLB root together
  around Y (never flip a single axis, which mirrors the shape).
- `Max Voxels` and `Request Timeout Seconds` are not tuned for TRELLIS.2.
