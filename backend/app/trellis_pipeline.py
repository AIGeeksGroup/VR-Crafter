"""TRELLIS.2 generator: the pipeline_type='1024' path, split at the sparse structure.

structure() runs stage 1 (sparse structure at 64^3) and returns the voxels; mesh() runs
stages 2-3 (shape and texture SLat at 1024) on given voxels and exports a GLB with o_voxel.
torch, PIL, trellis2 and o_voxel are imported inside functions so that importing this module
(and app.main) stays light for the fixture backend.
"""

import os
from pathlib import Path
import sys

from .generators import GenerationError
from .voxel_mapping import GRID_SIZE, build_voxel_payload, trellis_to_unity, unity_to_trellis

# The only models the '1024' path needs; the 512 flow models are never loaded.
TRELLIS_MODEL_NAMES = [
    "sparse_structure_flow_model",
    "sparse_structure_decoder",
    "shape_slat_flow_model_1024",
    "shape_slat_decoder",
    "tex_slat_flow_model_1024",
    "tex_slat_decoder",
]
COND_RES_STRUCTURE = 512
COND_RES_SLAT = 1024
SLAT_RES = 1024
NVDIFFRAST_FACE_LIMIT = 16777216


def load_image(path: Path):
    """Open and fully decode an image, returning a PIL.Image.Image detached from the file."""
    from PIL import Image

    try:
        with Image.open(path) as img:
            img.load()
            return img.copy()
    except Exception as exc:
        raise GenerationError("Uploaded file is not a readable image.") from exc


class TrellisGenerator:
    source = "trellis2"
    model_space = "trellis_unit_cube"

    def __init__(self, model: str, trellis_root: str, device: str = "cuda", low_vram: bool = False) -> None:
        self.model = model
        self.trellis_root = trellis_root
        self.device = device
        self.low_vram = low_vram
        self._pipe = None

    def load(self) -> None:
        root = str(Path(self.trellis_root).resolve())
        if not (Path(root) / "trellis2" / "__init__.py").is_file():
            raise RuntimeError(f"TRELLIS2_ROOT={self.trellis_root!r} does not contain the trellis2 package.")
        if root not in sys.path:
            sys.path.append(root)  # appended, so it never shadows the backend's own modules
        # Same allocator setting as TRELLIS.2's app.py/example.py; only effective before torch loads.
        os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")

        import torch
        import o_voxel.postprocess  # noqa: F401  (fail at startup rather than on the first mesh)
        from trellis2.pipelines import Trellis2ImageTo3DPipeline

        class Pipeline1024(Trellis2ImageTo3DPipeline):
            model_names_to_load = TRELLIS_MODEL_NAMES

        pipe = Pipeline1024.from_pretrained(self.model)
        pipe.low_vram = self.low_vram
        pipe.to(torch.device(self.device))
        self._pipe = pipe

    def _preprocess(self, image_path: Path):
        return self._pipe.preprocess_image(load_image(Path(image_path)))

    def structure(self, image_path: Path, seed: int) -> list[dict]:
        import torch

        pipe = self._pipe
        with torch.no_grad():
            img = self._preprocess(image_path)
            torch.manual_seed(seed)
            cond = pipe.get_cond([img], COND_RES_STRUCTURE)
            coords = pipe.sample_sparse_structure(cond, GRID_SIZE, 1)  # (N, 4) int [batch, x, y, z]
            return build_voxel_payload(trellis_to_unity(coords[:, 1:].cpu().tolist()))

    def mesh(
        self,
        image_path: Path,
        seed: int,
        unity_coords: list[tuple[int, int, int]],
        glb_out: Path,
    ) -> None:
        import torch
        import o_voxel.postprocess

        pipe = self._pipe
        with torch.no_grad():
            img = self._preprocess(image_path)
            cond = pipe.get_cond([img], COND_RES_SLAT)
            torch.manual_seed(seed)
            # Sorted like torch.argwhere output in sample_sparse_structure, so the per-voxel noise
            # depends only on the voxel set, not on the order Unity submitted it in.
            coords = torch.tensor(
                [[0, *c] for c in sorted(unity_to_trellis(unity_coords))],
                dtype=torch.int32,
                device=pipe.device,
            )
            shape = pipe.sample_shape_slat(cond, pipe.models["shape_slat_flow_model_1024"], coords)
            tex = pipe.sample_tex_slat(cond, pipe.models["tex_slat_flow_model_1024"], shape)
            torch.cuda.empty_cache()
            m = pipe.decode_latent(shape, tex, SLAT_RES)[0]
            m.simplify(NVDIFFRAST_FACE_LIMIT)
            glb = o_voxel.postprocess.to_glb(
                vertices=m.vertices,
                faces=m.faces,
                attr_volume=m.attrs,
                coords=m.coords,
                attr_layout=pipe.pbr_attr_layout,
                grid_size=SLAT_RES,
                aabb=[[-0.5, -0.5, -0.5], [0.5, 0.5, 0.5]],
                decimation_target=500000,
                texture_size=2048,
                remesh=True,
                remesh_band=1,
                remesh_project=0,
            )
        glb.export(str(glb_out))  # PNG textures (no extension_webp)
        torch.cuda.empty_cache()
