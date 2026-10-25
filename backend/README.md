# VR-Crafter backend

This FastAPI service receives an image from the Unity client and returns a
64³ voxel model, a GLB, or both. It has two generators, chosen at startup with
`VRCRAFTER_GENERATOR`:

- `fixture` (default): a deterministic voxel house and the bundled
  `models/PropertyDemoModel.glb`. It does not look at the uploaded image. It
  needs only `requirements.txt` and is what Unity starts automatically on
  Windows.
- `trellis2`: TRELLIS.2 on a Linux GPU machine. Stage 1 (sparse structure)
  gives the 64³ voxels, and the 1024 shape and texture stages give a GLB for
  the same voxels. This path has never been run yet (see
  [Not done yet](#not-done-yet-trellis2-gpu-path)).

Every generation gets a `generation_id` and numbered revisions. Revision 0 is
the generated result; each edit submitted from VR creates the next revision,
with its own voxels and its own GLB.

## Run the fixture backend (Windows or Linux)

From the repository root:

```powershell
cd backend
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
python -m uvicorn app.main:app --reload --host 127.0.0.1 --port 8000
```

Open `http://127.0.0.1:8000/status` and check that `generator` is `fixture`.

## Run the TRELLIS.2 backend (Linux GPU machine)

Use the conda env `trellis2`, which already has torch, fastapi, uvicorn and
python-multipart. Pick the GPU with `CUDA_DEVICE_ORDER=PCI_BUS_ID
CUDA_VISIBLE_DEVICES=<n>` and leave `TRELLIS2_DEVICE` at `cuda`: upstream
TRELLIS.2 hard-codes `.cuda()` in the DINOv3 extractor and BiRefNet, so other
`TRELLIS2_DEVICE` values are only partly honored.

```bash
conda activate trellis2
cd backend
CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=1 \
HF_HOME=/mnt/zicheng_ssd/huggingface_cache \
VRCRAFTER_GENERATOR=trellis2 TRELLIS2_ROOT=/mnt/zicheng_ssd/JT_ssd/TRELLIS.2 \
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Use `--host 0.0.0.0` instead if Unity connects over the LAN (deployment option
A in [../README-QuestPro.md](../README-QuestPro.md)); the port then has no
authentication. The model is loaded at startup, so a missing `TRELLIS2_ROOT`
or o-voxel stops the server immediately. Check `/status` afterwards:
`generator` must be `trellis2`.

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `VRCRAFTER_GENERATOR` | `fixture` | `fixture` or `trellis2`. Any other value stops startup. |
| `VRCRAFTER_DATA_DIR` | `backend/data/generations` | Where generations are stored. |
| `TRELLIS2_ROOT` | none (required for `trellis2`) | Path of the TRELLIS.2 checkout (contains `trellis2/`). |
| `TRELLIS2_MODEL` | `microsoft/TRELLIS.2-4B` | Hugging Face id, or a local directory with a `pipeline.json`. |
| `TRELLIS2_DEVICE` | `cuda` | Keep at `cuda`; choose the card with `CUDA_VISIBLE_DEVICES`. |
| `TRELLIS2_LOW_VRAM` | `0` | `0` or `1`; anything else stops startup. |

Only six TRELLIS.2 models are loaded: the sparse-structure flow, the 1024
shape and texture flows, and the shape and texture decoders.

## Not done yet (TRELLIS.2 GPU path)

The code is written and its CPU-side tests pass, but nothing of the following
has been run, and no timings, voxel counts, GLB sizes or VRAM figures have been
measured:

1. Install o-voxel and trimesh in the env (`trimesh` may come with it; if not,
   `pip install trimesh`):

   ```bash
   conda activate trellis2
   cd /mnt/zicheng_ssd/JT_ssd/TRELLIS.2 && bash setup.sh --o-voxel
   python -c "import o_voxel.postprocess, cumesh, nvdiffrast, flex_gemm, trimesh; print('ok')"
   ```

2. Get access to two gated models on Hugging Face:
   `facebook/dinov3-vitl16-pretrain-lvd1689m` (needs manual approval) and
   `briaai/RMBG-2.0`.
3. Download the weights (the six checkpoints plus the two gated models):

   ```bash
   export HF_HOME=/mnt/zicheng_ssd/huggingface_cache && hf auth login
   hf download microsoft/TRELLIS.2-4B --include "pipeline.json" "ckpts/ss_flow_img_dit_1_3B_64_bf16.*" \
     "ckpts/slat_flow_img2shape_dit_1_3B_1024_bf16.*" "ckpts/shape_dec_next_dc_f16c32_fp16.*" \
     "ckpts/slat_flow_imgshape2tex_dit_1_3B_1024_bf16.*" "ckpts/tex_dec_next_dc_f16c32_fp16.*"
   hf download facebook/dinov3-vitl16-pretrain-lvd1689m && hf download briaai/RMBG-2.0
   ```

4. If `briaai/RMBG-2.0` fails to load under the installed transformers, use the
   BiRefNet fallback: make a local directory containing a copy of
   `pipeline.json` with `rembg_model.args.model_name` set to
   `ZhengPeng7/BiRefNet`, and point `TRELLIS2_MODEL` at that directory. No
   code change is needed.
5. Run the GPU test and the measuring script:

   ```bash
   cd backend
   CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=1 VRCRAFTER_GPU_TESTS=1 \
   HF_HOME=/mnt/zicheng_ssd/huggingface_cache TRELLIS2_ROOT=/mnt/zicheng_ssd/JT_ssd/TRELLIS.2 \
     python -m pytest tests -v
   CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=1 HF_HOME=/mnt/zicheng_ssd/huggingface_cache \
   TRELLIS2_ROOT=/mnt/zicheng_ssd/JT_ssd/TRELLIS.2 \
     python scripts/measure_trellis.py examples/*.png --seed 42
   ```

   `measure_trellis.py` prints voxel count, stage times, GLB size and peak VRAM
   per image. Use its numbers to choose `Max Voxels` and the Unity request
   timeout (600 s until then).

## API

- `GET /status` and `GET /health`: service and model availability, plus
  `generator` (`fixture` or `trellis2`).
- `POST /generate`: multipart upload with `file`, optional `response_mode`
  (`model` by default, `voxels`, or `both`), optional `max_voxels` (default
  12000) and optional integer `seed` (default 42). The backend always builds
  and stores both the voxels and the GLB; `response_mode` only chooses what the
  response contains. A `both` response looks like:

  ```json
  {"status": "done", "response_mode": "both", "source": "trellis2", "seed": 42,
   "generation_id": "<32 hex>", "revision": 0, "grid_size": 64, "voxel_count": 2,
   "voxels": [{"id": 0, "x": 53, "y": 30, "z": 43, "type": "occupied", "color": "#C8C8C8"}],
   "model_url": "http://<host>/generations/<id>/revisions/0/model.glb",
   "model_space": "trellis_unit_cube",
   "revisions_url": "http://<host>/generations/<id>/revisions",
   "generation_url": "http://<host>/generations/<id>"}
  ```

  `voxels` appears only in `voxels`/`both`, and `model_url` only in
  `model`/`both`. If no GLB exists, `model_error` ("No GLB was produced.")
  replaces `model_url`. `model_space` is present only for `trellis2`. URLs are
  built from the request's base URL; every success response has
  `revisions_url` and `generation_url`.
- `POST /generations/{generation_id}/revisions`: JSON body

  ```json
  {"base_revision": 0, "max_voxels": 12000, "voxels": [
    {"id": 0,  "x": 5, "y": 5, "z": 5, "type": "occupied", "color": "#C8C8C8"},
    {"id": -1, "x": 6, "y": 5, "z": 5, "type": "occupied", "color": "#C8C8C8"}]}
  ```

  `voxels` is the complete edited model in Unity grid coordinates. An existing
  id that is not listed counts as deleted, and `id: -1` is a new voxel. The
  response has the same shape as a `both` response with
  `revision = base_revision + 1`, and new voxels carry their assigned ids.
- `GET /generations/{generation_id}`: the latest committed revision, in the
  same shape as a revisions response (`response_mode` `both`). It is read-only
  and does not wait for a running generation. Unity calls it when a submit is
  answered with `Revision {base} is stale; latest is {latest}.`, which happens
  when Unity timed out on a submit that the backend still committed; Unity then
  rebuilds from that revision.
- `GET /generations/{generation_id}/revisions/{revision}/model.glb`: the GLB of
  that revision (`model/gltf-binary`).
- `GET /model`: the legacy endpoint; downloads the first available GLB in
  `models/`.

### Errors

Failures of `/generate`, `/revisions` and `GET /generations/{id}` return
HTTP 200 with `{"status": "error", "error": "<message>"}`. Only a missing GLB
returns HTTP 404. Messages include: `Invalid request: {field} {reason}` (a
missing or mistyped field), `response_mode must be model, voxels, or both`,
`Generator produced {n} voxels, above max_voxels={m}. Raise Max Voxels on the
Unity Server component.`, `Generator produced no occupied voxels.`, `Uploaded
file is not a readable image.`, `Generation failed: {exception}`, `Unknown
generation {id}.` (also for a malformed id, or a generation whose revision 0
is not finished), `Generation {id} was created by {source}; this server runs
{generator}.` (revisions and the latest revision are served only by the
generator that made the generation), `Revision {base} is stale; latest is
{latest}.`, `Unknown voxel id {id}.`, `Duplicate voxel id {id}.`, `Voxel {id}
is outside the 64^3 grid.`, `Voxel {id} has invalid color {color}.`, `Edited
model has no voxels.` and `Edited model has {n} voxels, above
max_voxels={m}.`

## Data

Each generation lives in `data/generations/<generation_id>/` (or
`VRCRAFTER_DATA_DIR`): the uploaded image, a metadata file, and one directory
per revision with its voxels and `model.glb`. Uploads no longer go to
`data/received_images/`. The folder is ignored by Git, grows with every
request and is never cleaned up automatically. Reference input images are in
`examples/`; the test GLB is in `models/`.

## Tests

```powershell
cd backend
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements-dev.txt
python -m pytest tests -v
```

On Linux use `.venv/bin/python`. The default run needs no GPU, torch or
trimesh; the real TRELLIS.2 test is skipped unless `VRCRAFTER_GPU_TESTS=1`
(see step 5 above).

See [UNITY_INTEGRATION.md](UNITY_INTEGRATION.md) for the Unity request flow,
verification status, and what still has to be checked.
