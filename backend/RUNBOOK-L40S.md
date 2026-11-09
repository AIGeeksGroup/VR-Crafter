# L40S run log: TRELLIS.2 backend

This is the first machine on which the `trellis2` generator was actually run.
It records the exact setup, the two compatibility fixes found on the way, and
the measured results. Credentials are deliberately not stored here.

## Machine

- 1× NVIDIA L40S, 44 GiB, driver with CUDA 13.2, 128 CPU cores, 1 TiB RAM.
- System Python 3.12 with NVIDIA PyTorch 2.12 (`torch 2.12.0a0+nv26.4`), transformers 5.9.
- Only `/` (50 GB overlay) was writable; `/root/nas`, `/root/public` and `/anc-init`
  were read-only for this account. About 9 GB were free after setup.

## What was installed

| Component | How |
|---|---|
| fastapi, uvicorn, python-multipart, trimesh, timm, kornia, imageio, opencv, easydict, zstandard, plyfile | `pip install` from the Tsinghua mirror |
| nvdiffrast 0.4.0 | git clone of NVlabs/nvdiffrast through `ghfast.top`, then `pip install . --no-build-isolation` |
| cumesh | git clone of JeffreyXiang/CuMesh with submodules, `TORCH_CUDA_ARCH_LIST=8.9` |
| flex_gemm | git clone of JeffreyXiang/FlexGEMM, `TORCH_CUDA_ARCH_LIST=8.9` |
| o_voxel | `backend/third_party/TRELLIS.2/o-voxel`, `pip install . --no-deps --no-build-isolation` |

`o-voxel`'s own dependencies point at git URLs for CuMesh/FlexGEMM; with both
already installed, `--no-deps` avoids a second slow clone.

Building for `sm_89` only (`TORCH_CUDA_ARCH_LIST=8.9`) cut the CuMesh build from
many architectures down to a few minutes.

## Models

Downloaded to `~/vr-models` (not in the repo):

- `TRELLIS.2-4B` from `microsoft/TRELLIS.2-4B` through `HF_ENDPOINT=https://hf-mirror.com`,
  only the six files the 1024 path loads: `ss_flow_img_dit_1_3B_64_bf16`,
  `shape_dec_next_dc_f16c32_fp16`, `slat_flow_img2shape_dit_1_3B_1024_bf16`,
  `slat_flow_imgshape2tex_dit_1_3B_1024_bf16`, `tex_dec_next_dc_f16c32_fp16`
  and their `.json` files.
- `ss_dec_conv3d_16l8_fp16` from `microsoft/TRELLIS-image-large` (pipeline.json
  references it by repo id; it was copied into the local model's `ckpts/`).
- `ZhengPeng7/BiRefNet` from Hugging Face.
- `facebook/dinov3-vitl16-pretrain-lvd1689m` from **ModelScope** (the Hugging
  Face repo is gated and returns 403 through the mirror).

`pipeline.json` in the local model directory was edited to use local paths:

- `sparse_structure_decoder` → `ckpts/ss_dec_conv3d_16l8_fp16`
- `image_cond_model.args.model_name` → `~/vr-models/dinov3-vitl16`
- `rembg_model.args.model_name` → `~/vr-models/BiRefNet` (ignored by `LocalBiRefNet`)

## Fixes required on this machine

1. **BiRefNet dtype** — the checkpoint loads as fp16, while the upstream wrapper
   feeds float32 images: `RuntimeError: Input type (float) and bias type (c10::Half)
   should be the same`. `LocalBiRefNet` now calls `self.model.float()` after loading.
2. **transformers 5 DINOv3 layout** — `DinoV3ViTModel` stores the blocks in
   `model.model.layer`; the extractor expects `model.layer`. `TrellisGenerator.load()`
   aliases the block list before moving the pipeline to the GPU.
3. **Local BiRefNet weights** — `VRCRAFTER_BIREFNET` selects a directory instead of the
   Hub repo id, so the server can run fully offline.

## Result: one chair photo

Input: a wooden stool/chair photo (1024×1535).

```bash
cd backend
TRELLIS2_MODEL=~/vr-models/TRELLIS.2-4B \
VRCRAFTER_BIREFNET=~/vr-models/BiRefNet \
HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 \
python scripts/measure_trellis.py ~/chair.jpg --out ~/vr-out
```

```
load 79.3 s
chair.jpg  6899 voxels  structure 3.2 s  mesh 86.3 s  GLB 22.2 MB  peak VRAM 14.5 GB
```

## Result: HTTP API

With `VRCRAFTER_GENERATOR=trellis2` and `VRCRAFTER_DATA_DIR=~/vr-data`:

- `GET /status` → `{"generator":"trellis2","model_available":true}`, ready 80 s after start.
- `POST /generate` (chair image, `response_mode=both`) → HTTP 200 in 56.6 s,
  `voxel_count=6899`, `revision=0`, `model_space=trellis_unit_cube`, GLB 23.6 MB.
- `POST /generations/{id}/revisions` with the same voxels plus one new red voxel
  → `revision=1`, `voxel_count=6900`, the new voxel got `id=6899`, GLB 23.0 MB.
- The same calls through an SSH tunnel from Windows returned 200 and the GLB
  downloaded in 17.7 s.

## Connect Unity

On the Windows machine:

```powershell
ssh -N -L 8000:127.0.0.1:8000 -p <ssh-port> <user>@<server>
```

Then `http://127.0.0.1:8000/status` must show `"generator": "trellis2"`, and
Unity's Generate Url is `http://127.0.0.1:8000/generate`.
