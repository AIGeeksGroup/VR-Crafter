"""Generator interface and the deterministic fixture generator."""

from pathlib import Path
import shutil
from typing import Protocol

from .voxels import generate_test_house_voxels

MODEL_NAMES = ("test_model.glb", "sample.glb", "PropertyDemoModel.glb")


class GenerationError(Exception):
    """Raised for failures whose message is returned to the client verbatim."""


class Generator(Protocol):
    source: str
    model_space: str | None

    def structure(self, image_path: Path, seed: int) -> list[dict]:
        """Return occupied voxels in Unity coordinates (x/y/z/type/color, no id)."""
        ...

    def mesh(
        self,
        image_path: Path,
        seed: int,
        unity_coords: list[tuple[int, int, int]],
        glb_out: Path,
    ) -> None:
        """Write a GLB for the given voxel coordinates to glb_out."""
        ...


def find_local_model(model_dir: Path) -> Path | None:
    for name in MODEL_NAMES:
        candidate = model_dir / name
        if candidate.is_file():
            return candidate
    return next(model_dir.glob("*.glb"), None)


class FixtureGenerator:
    source = "fixture"
    model_space = None

    def __init__(self, model_dir: Path) -> None:
        self.model_dir = Path(model_dir)

    def structure(self, image_path: Path, seed: int) -> list[dict]:
        return generate_test_house_voxels(12_000)

    def mesh(self, image_path, seed, unity_coords, glb_out) -> None:
        model = find_local_model(self.model_dir)
        if model is not None:
            shutil.copyfile(model, glb_out)
