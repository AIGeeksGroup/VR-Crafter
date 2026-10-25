# Vendored third-party code

## TRELLIS.2

`TRELLIS.2/` is an unmodified copy of the official repository, so the backend never
imports TRELLIS.2 from outside this project.

| | |
|---|---|
| Source | https://github.com/microsoft/TRELLIS.2 |
| Commit | `75fbf0183001` ("Merge pull request #166 from microsoft/copilot/fix-failing-github-actions-job") |
| Nested submodule | `o-voxel/third_party/eigen` from https://gitlab.com/libeigen/eigen.git at `21e4582d1739`, copied in as plain files |
| License | MIT (`TRELLIS.2/LICENSE`); Eigen is MPL-2.0 (`TRELLIS.2/o-voxel/third_party/eigen/COPYING.MPL2`) |
| Excluded | only the `.git` directories |

`backend/app/trellis_pipeline.py` appends `third_party/TRELLIS.2` to `sys.path` and
checks that `trellis2` was imported from here. The o-voxel extension is built from
`TRELLIS.2/o-voxel` (`cd backend/third_party/TRELLIS.2 && bash setup.sh --o-voxel`).

To update: clone the official repository with `--recursive` at the new commit, replace
`TRELLIS.2/` with its working tree minus `.git`, update the commit table above, and
re-check the calls in `trellis_pipeline.py` against the new code.

## BiRefNet

`BiRefNet/` holds the model code that TRELLIS.2's background-removal wrapper would
otherwise download from Hugging Face at runtime (`trust_remote_code=True`).

| | |
|---|---|
| Source | https://huggingface.co/ZhengPeng7/BiRefNet at commit `e2bf8e4460fc` |
| Files | `birefnet.py`, `BiRefNet_config.py`, `config.json`, `README.md` (model card), unmodified |
| License | MIT (`BiRefNet/LICENSE`, from https://github.com/ZhengPeng7/BiRefNet) |
| Added here | `__init__.py` only, re-exporting `BiRefNet` and `BiRefNetConfig` |

`backend/app/birefnet_local.py` imports this directory as a private package and
builds the model with the matching `ZhengPeng7/BiRefNet` weights (downloaded as
weights only). It replaces TRELLIS.2's default `briaai/RMBG-2.0`, whose code is
gated and differs from this public version.
