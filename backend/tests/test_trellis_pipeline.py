"""TrellisGenerator tests.

Only test_real_structure_and_mesh needs the GPU, the TRELLIS.2 weights and the conda env
`trellis2`; it is skipped unless VRCRAFTER_GPU_TESTS=1 (and then also needs TRELLIS2_ROOT).
Everything else runs in the plain backend venv without torch.
"""

import os
import subprocess
import sys

import pytest
from fastapi.testclient import TestClient

import app.main as main
from app.generators import GenerationError
from app.main import BACKEND_DIR, app
from app.trellis_pipeline import TrellisGenerator, load_image

IMAGE = BACKEND_DIR / "examples" / "upload_square.png"

gpu = pytest.mark.skipif(
    os.environ.get("VRCRAFTER_GPU_TESTS") != "1",
    reason="real TRELLIS.2 run; set VRCRAFTER_GPU_TESTS=1 and TRELLIS2_ROOT",
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
    code = "import sys, app.trellis_pipeline; assert not {'torch', 'trellis2', 'PIL', 'o_voxel'} & set(sys.modules)"
    assert subprocess.run([sys.executable, "-c", code], cwd=BACKEND_DIR).returncode == 0


def test_load_rejects_root_without_trellis2(tmp_path):
    gen = TrellisGenerator("microsoft/TRELLIS.2-4B", str(tmp_path))
    assert gen.source == "trellis2" and gen.model_space == "trellis_unit_cube"
    with pytest.raises(RuntimeError, match="trellis2 package"):
        gen.load()
    assert str(tmp_path) not in sys.path


class RecordingGenerator:
    source = "trellis2"
    model_space = "trellis_unit_cube"
    instances = []

    def __init__(self, model, trellis_root, device="cuda", low_vram=False):
        self.args = (model, trellis_root, device, low_vram)
        self.loaded = False
        RecordingGenerator.instances.append(self)

    def load(self):
        self.loaded = True


@pytest.fixture
def trellis_env(monkeypatch, tmp_path):
    monkeypatch.setenv("VRCRAFTER_DATA_DIR", str(tmp_path))
    monkeypatch.setenv("VRCRAFTER_GENERATOR", "trellis2")
    for name in ("TRELLIS2_ROOT", "TRELLIS2_MODEL", "TRELLIS2_DEVICE", "TRELLIS2_LOW_VRAM"):
        monkeypatch.delenv(name, raising=False)
    RecordingGenerator.instances = []
    monkeypatch.setattr(main, "TrellisGenerator", RecordingGenerator)
    return monkeypatch


def test_trellis2_without_root_fails_startup(trellis_env):
    with pytest.raises(RuntimeError, match="TRELLIS2_ROOT"):
        with TestClient(app):
            pass
    assert RecordingGenerator.instances == []


@pytest.mark.parametrize("env, expected", [
    ({}, ("microsoft/TRELLIS.2-4B", "/opt/TRELLIS.2", "cuda", False)),
    ({"TRELLIS2_MODEL": "/local/cfg", "TRELLIS2_DEVICE": "cuda:1", "TRELLIS2_LOW_VRAM": "1"},
     ("/local/cfg", "/opt/TRELLIS.2", "cuda:1", True)),
    ({"TRELLIS2_LOW_VRAM": "0"}, ("microsoft/TRELLIS.2-4B", "/opt/TRELLIS.2", "cuda", False)),
])
def test_trellis2_env_builds_and_loads_generator(trellis_env, env, expected):
    trellis_env.setenv("TRELLIS2_ROOT", "/opt/TRELLIS.2")
    for name, value in env.items():
        trellis_env.setenv(name, value)
    with TestClient(app) as c:
        [gen] = RecordingGenerator.instances
        assert gen.args == expected and gen.loaded and app.state.generator is gen
        assert c.get("/status").json()["generator"] == "trellis2"


def test_trellis2_bad_low_vram_fails_startup(trellis_env):
    trellis_env.setenv("TRELLIS2_ROOT", "/opt/TRELLIS.2")
    trellis_env.setenv("TRELLIS2_LOW_VRAM", "true")
    with pytest.raises(RuntimeError, match="TRELLIS2_LOW_VRAM"):
        with TestClient(app):
            pass


@pytest.fixture(scope="module")
def real_gen():
    root = os.environ.get("TRELLIS2_ROOT")
    if not root:
        pytest.fail("VRCRAFTER_GPU_TESTS=1 needs TRELLIS2_ROOT")
    gen = TrellisGenerator(
        os.environ.get("TRELLIS2_MODEL", "microsoft/TRELLIS.2-4B"),
        root,
        device=os.environ.get("TRELLIS2_DEVICE", "cuda"),
        low_vram=os.environ.get("TRELLIS2_LOW_VRAM", "0") == "1",
    )
    gen.load()  # once per module
    return gen


@gpu
def test_real_structure_and_mesh(real_gen, tmp_path):
    import trimesh

    gen = real_gen
    voxels = gen.structure(IMAGE, seed=42)
    again = gen.structure(IMAGE, seed=42)
    cells = {(v["x"], v["y"], v["z"]) for v in voxels}
    assert voxels and all(0 <= c < 64 for cell in cells for c in cell)
    # flash-attn may introduce tiny numeric differences between identical runs
    assert len(cells & {(v["x"], v["y"], v["z"]) for v in again}) / len(cells) >= 0.99
    gen.mesh(IMAGE, 42, sorted(cells), tmp_path / "model.glb")
    bounds = trimesh.load(tmp_path / "model.glb", force="scene").bounds
    assert (bounds >= -0.51).all() and (bounds <= 0.51).all()
