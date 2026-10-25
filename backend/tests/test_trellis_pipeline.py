"""TrellisGenerator tests.

Only test_real_structure_and_mesh needs the GPU, the TRELLIS.2 weights and the conda env
`trellis2`; it is skipped unless VRCRAFTER_GPU_TESTS=1.
Everything else runs in the plain backend venv without torch.
"""

import os
import subprocess
import sys

import pytest
from fastapi.testclient import TestClient

import app.main as main
import app.trellis_pipeline as trellis_pipeline
from app.generators import GenerationError
from app.main import BACKEND_DIR, app
from app.trellis_pipeline import TRELLIS2_ROOT, TrellisGenerator, load_image

IMAGE = BACKEND_DIR / "examples" / "upload_square.png"

gpu = pytest.mark.skipif(
    os.environ.get("VRCRAFTER_GPU_TESTS") != "1",
    reason="real TRELLIS.2 run; set VRCRAFTER_GPU_TESTS=1",
)


def test_load_image_rejects_non_image(tmp_path):
    (tmp_path / "x.png").write_bytes(b"not an image")
    with pytest.raises(GenerationError, match="Uploaded file is not a readable image."):
        load_image(tmp_path / "x.png")


def test_load_image_reads_png():
    img = load_image(IMAGE)
    assert img.size == (1024, 1024) and img.mode == "RGBA"
    img.getpixel((0, 0))  # pixel data is usable after the file is closed


def test_trellis_pipeline_import_is_light():
    code = ("import sys, app.trellis_pipeline; "
            "assert not {'torch', 'torchvision', 'trellis2', 'PIL', 'o_voxel', 'app.birefnet_local'} & set(sys.modules)")
    assert subprocess.run([sys.executable, "-c", code], cwd=BACKEND_DIR).returncode == 0


def test_trellis2_is_vendored_in_repo():
    assert TRELLIS2_ROOT == BACKEND_DIR / "third_party" / "TRELLIS.2"
    for rel in ("trellis2/__init__.py", "trellis2/pipelines/trellis2_image_to_3d.py", "o-voxel/setup.py",
                "o-voxel/o_voxel/postprocess.py", "o-voxel/third_party/eigen/Eigen/Core", "LICENSE"):
        assert (TRELLIS2_ROOT / rel).is_file(), rel


def test_birefnet_is_vendored_in_repo():
    root = BACKEND_DIR / "third_party" / "BiRefNet"
    for rel in ("__init__.py", "birefnet.py", "BiRefNet_config.py", "config.json", "LICENSE"):
        assert (root / rel).is_file(), rel


def test_load_rejects_missing_vendored_copy(monkeypatch, tmp_path):
    monkeypatch.setattr(trellis_pipeline, "TRELLIS2_ROOT", tmp_path)
    gen = TrellisGenerator("microsoft/TRELLIS.2-4B")
    assert gen.source == "trellis2" and gen.model_space == "trellis_unit_cube"
    with pytest.raises(RuntimeError, match="vendored TRELLIS.2 code is missing"):
        gen.load()
    assert str(tmp_path) not in sys.path


def test_load_rejects_trellis2_from_outside_repo(monkeypatch):
    foreign = type(sys)("trellis2")
    foreign.__file__ = "/elsewhere/TRELLIS.2/trellis2/__init__.py"
    monkeypatch.setitem(sys.modules, "trellis2", foreign)
    monkeypatch.setattr(sys, "path", list(sys.path))
    with pytest.raises(RuntimeError, match="not the vendored copy"):
        TrellisGenerator("microsoft/TRELLIS.2-4B").load()


class RecordingGenerator:
    source = "trellis2"
    model_space = "trellis_unit_cube"
    instances = []

    def __init__(self, model, device="cuda", low_vram=False):
        self.args = (model, device, low_vram)
        self.loaded = False
        RecordingGenerator.instances.append(self)

    def load(self):
        self.loaded = True


@pytest.fixture
def trellis_env(monkeypatch, tmp_path):
    monkeypatch.setenv("VRCRAFTER_DATA_DIR", str(tmp_path))
    monkeypatch.setenv("VRCRAFTER_GENERATOR", "trellis2")
    for name in ("TRELLIS2_MODEL", "TRELLIS2_DEVICE", "TRELLIS2_LOW_VRAM"):
        monkeypatch.delenv(name, raising=False)
    RecordingGenerator.instances = []
    monkeypatch.setattr(main, "TrellisGenerator", RecordingGenerator)
    return monkeypatch


@pytest.mark.parametrize("env, expected", [
    ({}, ("microsoft/TRELLIS.2-4B", "cuda", False)),
    ({"TRELLIS2_MODEL": "/local/cfg", "TRELLIS2_DEVICE": "cuda:1", "TRELLIS2_LOW_VRAM": "1"},
     ("/local/cfg", "cuda:1", True)),
    ({"TRELLIS2_LOW_VRAM": "0"}, ("microsoft/TRELLIS.2-4B", "cuda", False)),
])
def test_trellis2_env_builds_and_loads_generator(trellis_env, env, expected):
    for name, value in env.items():
        trellis_env.setenv(name, value)
    with TestClient(app) as c:
        [gen] = RecordingGenerator.instances
        assert gen.args == expected and gen.loaded and app.state.generator is gen
        assert c.get("/status").json()["generator"] == "trellis2"


def test_trellis2_bad_low_vram_fails_startup(trellis_env):
    trellis_env.setenv("TRELLIS2_LOW_VRAM", "true")
    with pytest.raises(RuntimeError, match="TRELLIS2_LOW_VRAM"):
        with TestClient(app):
            pass


@pytest.fixture(scope="module")
def real_gen():
    gen = TrellisGenerator(
        os.environ.get("TRELLIS2_MODEL", "microsoft/TRELLIS.2-4B"),
        device=os.environ.get("TRELLIS2_DEVICE", "cuda"),
        low_vram=os.environ.get("TRELLIS2_LOW_VRAM", "0") == "1",
    )
    gen.load()  # once per module
    return gen


@gpu
def test_real_structure_and_mesh(real_gen, tmp_path):
    import trimesh
    from trellis2.pipelines import rembg

    from app.birefnet_local import LocalBiRefNet

    gen = real_gen
    assert isinstance(gen._pipe.rembg_model, LocalBiRefNet) and rembg.BiRefNet is not LocalBiRefNet
    voxels = gen.structure(IMAGE, seed=42)
    again = gen.structure(IMAGE, seed=42)
    cells = {(v["x"], v["y"], v["z"]) for v in voxels}
    assert voxels and all(0 <= c < 64 for cell in cells for c in cell)
    # flash-attn may introduce tiny numeric differences between identical runs
    assert len(cells & {(v["x"], v["y"], v["z"]) for v in again}) / len(cells) >= 0.99
    gen.mesh(IMAGE, 42, sorted(cells), tmp_path / "model.glb")
    bounds = trimesh.load(tmp_path / "model.glb", force="scene").bounds
    assert (bounds >= -0.51).all() and (bounds <= 0.51).all()
