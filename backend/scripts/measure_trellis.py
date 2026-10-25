"""Measure the TRELLIS.2 generator per image: voxel count, stage timings, GLB size, peak VRAM.

Run from backend/ in the conda env `trellis2`:

    CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=1 \
      python scripts/measure_trellis.py IMAGE... [--seed 42] [--out DIR]

Uses the vendored TRELLIS.2 in third_party/ and reads TRELLIS2_MODEL, TRELLIS2_DEVICE and
TRELLIS2_LOW_VRAM like the server. Example images: third_party/TRELLIS.2/assets/example_image/.
Prints one line per image: name  N  t_structure  t_mesh  glb_MB  peak_VRAM_GB
(times in seconds; peak VRAM is torch.cuda.max_memory_reserved for that image).
"""

import argparse
import os
from pathlib import Path
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.trellis_pipeline import TrellisGenerator  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("images", nargs="+", type=Path)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--out", type=Path, default=None, help="keep GLBs here (default: temp dir)")
    args = parser.parse_args()

    gen = TrellisGenerator(
        os.environ.get("TRELLIS2_MODEL", "microsoft/TRELLIS.2-4B"),
        device=os.environ.get("TRELLIS2_DEVICE", "cuda"),
        low_vram=os.environ.get("TRELLIS2_LOW_VRAM", "0") == "1",
    )
    t0 = time.perf_counter()
    gen.load()
    print(f"# load {time.perf_counter() - t0:.1f}s", file=sys.stderr)

    import torch

    with tempfile.TemporaryDirectory() as tmp:
        out_dir = args.out or Path(tmp)
        out_dir.mkdir(parents=True, exist_ok=True)
        print("name\tN\tt_structure\tt_mesh\tglb_MB\tpeak_VRAM_GB")
        for image in args.images:
            torch.cuda.empty_cache()
            torch.cuda.reset_peak_memory_stats()
            try:
                t0 = time.perf_counter()
                voxels = gen.structure(image, args.seed)
                t_structure = time.perf_counter() - t0
                coords = [(v["x"], v["y"], v["z"]) for v in voxels]
                glb = out_dir / f"{image.stem}.glb"
                t0 = time.perf_counter()
                gen.mesh(image, args.seed, coords, glb)
                t_mesh = time.perf_counter() - t0
            except Exception as exc:
                print(f"{image.name}\tERROR\t{type(exc).__name__}: {exc}", flush=True)
                continue
            peak = torch.cuda.max_memory_reserved() / 1024**3
            glb_mb = glb.stat().st_size / 1024**2
            print(
                f"{image.name}\t{len(voxels)}\t{t_structure:.1f}\t{t_mesh:.1f}\t{glb_mb:.1f}\t{peak:.1f}",
                flush=True,
            )
    return 0


if __name__ == "__main__":
    sys.exit(main())
