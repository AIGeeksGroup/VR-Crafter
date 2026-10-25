import re
import subprocess
import sys
import threading
import time
from urllib.parse import urlparse

import pytest
from fastapi.testclient import TestClient

from app.generation_store import GenerationStore
from app.generators import FixtureGenerator, GenerationError
from app.main import BACKEND_DIR, app
from app.voxel_mapping import build_voxel_payload

COORDS = [(0, 0, 0), (10, 20, 30), (63, 63, 63)]
fake = None


class FakeGenerator:
    source = "trellis2"
    model_space = "trellis_unit_cube"

    def __init__(self, coords, structure_error=None, mesh_error=None, delay=0.0):
        self.coords = coords
        self.structure_error = structure_error
        self.mesh_error = mesh_error
        self.delay = delay
        self.mesh_calls = []
        self.seeds = []
        self.mesh_seeds = []
        self.skip_glb = False
        self.max_active = 0
        self._active = 0
        self._guard = threading.Lock()

    def _enter(self):
        with self._guard:
            self._active += 1
            self.max_active = max(self.max_active, self._active)
        time.sleep(self.delay)

    def _exit(self):
        with self._guard:
            self._active -= 1

    def structure(self, image_path, seed):
        self._enter()
        try:
            self.seeds.append(seed)
            if self.structure_error is not None:
                err, self.structure_error = self.structure_error, None
                raise err
            return build_voxel_payload(self.coords)
        finally:
            self._exit()

    def mesh(self, image_path, seed, unity_coords, glb_out):
        self._enter()
        try:
            self.mesh_calls.append(list(unity_coords))
            self.mesh_seeds.append(seed)
            if self.mesh_error is not None:
                err, self.mesh_error = self.mesh_error, None
                raise err
            if not self.skip_glb:
                glb_out.write_bytes(b"glb")
        finally:
            self._exit()


def ev(id, x, y, z, type="occupied", color="#C8C8C8"):
    return {"id": id, "x": x, "y": y, "z": z, "type": type, "color": color}


@pytest.fixture
def client(request, tmp_path, monkeypatch):
    global fake
    monkeypatch.setenv("VRCRAFTER_DATA_DIR", str(tmp_path / "lifespan"))
    monkeypatch.delenv("VRCRAFTER_GENERATOR", raising=False)
    marker = request.node.get_closest_marker("fake")
    kwargs = dict(marker.kwargs) if marker else {}
    use_fixture = kwargs.pop("fixture", False)
    coords = kwargs.pop("coords", COORDS)
    fake = FakeGenerator(coords, **kwargs)
    with TestClient(app) as c:
        app.state.store = GenerationStore(tmp_path)
        app.state.generator = FixtureGenerator(BACKEND_DIR / "models") if use_fixture else fake
        yield c


def post_generate(c, mode="both", max_voxels=12000, seed=None, data=None):
    form = {"response_mode": mode, "max_voxels": str(max_voxels)}
    if seed is not None:
        form["seed"] = str(seed)
    files = {"file": ("a.png", data or b"imagebytes", "image/png")}
    return c.post("/generate", data=form, files=files).json()


def post_revision(c, r, voxels, base=None, max_voxels=12000):
    body = {
        "base_revision": r["revision"] if base is None else base,
        "max_voxels": max_voxels,
        "voxels": voxels,
    }
    return c.post(urlparse(r["revisions_url"]).path, json=body).json()


def client_store_root(c):
    return app.state.store.root


def run_two_posts_in_threads():
    results = []

    def work():
        results.append(post_generate(TestClient(app)))

    threads = [threading.Thread(target=work) for _ in range(2)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    return results


def test_generate_response(client):
    r = post_generate(client)
    gid = r["generation_id"]
    assert r["status"] == "done" and re.fullmatch(r"[0-9a-f]{32}", gid) and r["revision"] == 0
    assert r["grid_size"] == 64 and r["voxel_count"] == 3 and [v["id"] for v in r["voxels"]] == [0, 1, 2]
    assert r["model_space"] == "trellis_unit_cube"
    assert r["model_url"].endswith(f"/generations/{gid}/revisions/0/model.glb")
    assert r["revisions_url"].endswith(f"/generations/{gid}/revisions")
    assert r["generation_url"].endswith(f"/generations/{gid}") and r["generation_url"].startswith("http")
    assert client.get(urlparse(r["model_url"]).path).content == b"glb"


def test_response_mode_fields(client):
    voxels_only, model_only = post_generate(client, mode="voxels"), post_generate(client, mode="model")
    assert "model_url" not in voxels_only and "voxels" not in model_only
    assert "generation_url" in voxels_only and "generation_url" in model_only


def test_over_limit_is_error_not_truncated(client):
    r = post_generate(client, max_voxels=2)
    assert r["error"].startswith("Generator produced 3 voxels, above max_voxels=2.")
    assert "voxels" not in r and fake.mesh_calls == []


@pytest.mark.fake(coords=[])
def test_empty_structure_is_error(client):
    assert post_generate(client)["error"] == "Generator produced no occupied voxels."


@pytest.mark.fake(mesh_error=RuntimeError("CUDA out of memory"))
def test_generation_failure_records_nothing_and_recovers(client):
    assert post_generate(client)["error"] == "Generation failed: CUDA out of memory"
    assert [p for p in client_store_root(client).iterdir() if re.fullmatch(r"[0-9a-f]{32}", p.name)] == []
    assert post_generate(client)["status"] == "done"


@pytest.mark.fake(structure_error=GenerationError("Uploaded file is not a readable image."))
def test_generator_error_message_passthrough(client):
    assert post_generate(client)["error"] == "Uploaded file is not a readable image."


def test_seed_forwarded(client):
    assert post_generate(client, seed=7)["seed"] == 7 and post_generate(client)["seed"] == 42
    assert fake.seeds == [7, 42]


def test_revision_move_and_delete(client):
    r0 = post_generate(client)
    r1 = post_revision(client, r0, [ev(0, 5, 5, 5), ev(1, 53, 30, 43, color="#FF0000")])
    assert r1["status"] == "done" and r1["revision"] == 1 and r1["voxel_count"] == 2
    assert r1["voxels"][1] == {"id": 1, "x": 53, "y": 30, "z": 43, "type": "occupied", "color": "#FF0000"}
    assert fake.mesh_calls[-1] == [(5, 5, 5), (53, 30, 43)]
    assert r1["model_url"].endswith("/revisions/1/model.glb") and r1["generation_id"] == r0["generation_id"]


def test_revision_adds_new_voxels_without_id_reuse(client):
    r0 = post_generate(client)  # id 0,1,2
    r1 = post_revision(client, r0, [ev(0, 0, 0, 0), ev(1, 10, 20, 30)])  # delete id 2
    r2 = post_revision(client, r1, [ev(0, 0, 0, 0), ev(1, 10, 20, 30), ev(-1, 1, 0, 0, color="#00FF00")])
    new = [v for v in r2["voxels"] if v["x"] == 1][0]
    assert new["id"] == 3 and new["color"] == "#00FF00"  # id 2 not reused


def test_revision_merges_same_cell(client):
    r1 = post_revision(client, post_generate(client),
                       [ev(-1, 1, 1, 1), ev(2, 1, 1, 1), ev(0, 1, 1, 1), ev(-1, 2, 2, 2), ev(-1, 2, 2, 2, color="#123456")])
    assert r1["voxel_count"] == 2
    assert {(v["x"], v["id"] if v["id"] < 3 else "new", v["color"]) for v in r1["voxels"]} == {(1, 0, "#C8C8C8"), (2, "new", "#C8C8C8")}


def test_stale_base_revision_rejected(client):
    r0 = post_generate(client)
    post_revision(client, r0, [ev(0, 1, 1, 1)])
    assert post_revision(client, r0, [ev(0, 2, 2, 2)], base=0)["error"] == "Revision 0 is stale; latest is 1."


@pytest.mark.parametrize("voxels,max_voxels,error", [
    ([ev(99, 1, 1, 1)], 12000, "Unknown voxel id 99."),
    ([ev(-5, 1, 1, 1)], 12000, "Unknown voxel id -5."),
    ([ev(0, 1, 1, 1), ev(0, 2, 2, 2)], 12000, "Duplicate voxel id 0."),
    ([ev(0, 64, 1, 1)], 12000, "Voxel 0 is outside the 64^3 grid."),
    ([ev(0, 1, 1, 1, color="red")], 12000, "Voxel 0 has invalid color red."),
    ([], 12000, "Edited model has no voxels."),
    ([ev(0, 1, 1, 1), ev(-1, 2, 2, 2), ev(-1, 3, 3, 3)], 2, "Edited model has 3 voxels, above max_voxels=2."),
])
def test_revision_invalid_edits(client, voxels, max_voxels, error):
    assert post_revision(client, post_generate(client), voxels, max_voxels=max_voxels)["error"] == error


def test_unknown_generation_is_error(client):
    for gid in ["0" * 32, "not-a-generation"]:
        r = client.post(f"/generations/{gid}/revisions", json={"base_revision": 0, "max_voxels": 1, "voxels": []}).json()
        assert r["error"] == f"Unknown generation {gid}."
    assert client.get(f"/generations/{'0' * 32}/revisions/0/model.glb").status_code == 404


@pytest.mark.fake(delay=0.2)
def test_concurrent_requests_serialized(client):
    assert all(r["status"] == "done" for r in run_two_posts_in_threads()) and fake.max_active == 1


@pytest.mark.fake(delay=0.2)
def test_concurrent_generate_and_revision_serialized(client):
    r0 = post_generate(client)
    results = {}
    work = {
        "generate": lambda: post_generate(TestClient(app)),
        "revision": lambda: post_revision(TestClient(app), r0, [ev(0, 1, 1, 1)]),
    }
    threads = [threading.Thread(target=lambda k=k: results.__setitem__(k, work[k]())) for k in work]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert results["generate"]["status"] == "done" and results["revision"]["revision"] == 1
    assert fake.max_active == 1


@pytest.mark.fake(fixture=True)
def test_fixture_mode_round_trip(client):
    r0 = post_generate(client)
    assert r0["voxel_count"] == 9791 and "model_space" not in r0
    assert len(client.get(urlparse(r0["model_url"]).path).content) == 2554988  # PropertyDemoModel.glb
    v = r0["voxels"][0]
    r1 = post_revision(client, r0, [ev(v["id"], v["x"], v["y"] + 1, v["z"], v["type"], v["color"])])
    assert r1["revision"] == 1 and r1["voxels"][0]["type"] == v["type"]


@pytest.mark.fake(fixture=True)
def test_fixture_resubmit_whole_model_like_unity(client):
    r0 = post_generate(client)
    keys = ("id", "x", "y", "z", "type", "color")
    r1 = post_revision(client, r0, [{k: v[k] for k in keys} for v in r0["voxels"]])
    assert r1["revision"] == 1 and r1["voxel_count"] == r0["voxel_count"] == 9791
    assert r1["voxels"] == r0["voxels"]


def test_get_generation_returns_latest_committed_revision(client):
    r0 = post_generate(client)
    url = urlparse(r0["generation_url"]).path
    assert client.get(url).json() == r0  # a "both" response, before any edit
    r1 = post_revision(client, r0, [ev(0, 5, 5, 5), ev(-1, 6, 5, 5)])
    g = client.get(url)
    assert g.status_code == 200 and g.json() == r1
    assert g.json()["revision"] == 1 and g.json()["generation_url"] == r0["generation_url"]
    assert {"voxels", "model_url", "model_space", "revisions_url", "generation_url"} <= set(g.json())


def test_get_generation_unknown_or_uncommitted_is_error(client):
    uncommitted = app.state.store.create(b"x", ".png", 42, "trellis2")  # revision 0 not saved yet
    for gid in ["0" * 32, "not-a-generation", uncommitted]:
        r = client.get(f"/generations/{gid}")
        assert r.status_code == 200 and r.json() == {"status": "error", "error": f"Unknown generation {gid}."}


def test_generation_from_other_generator_is_error(client):
    r0 = post_generate(client)
    app.state.generator = FixtureGenerator(BACKEND_DIR / "models")
    error = f"Generation {r0['generation_id']} was created by trellis2; this server runs fixture."
    assert post_revision(client, r0, [ev(0, 1, 1, 1)]) == {"status": "error", "error": error}
    assert client.get(urlparse(r0["generation_url"]).path).json() == {"status": "error", "error": error}
    assert len(fake.mesh_calls) == 1 and app.state.store.latest_revision(r0["generation_id"]) == 0


def test_status_reports_generator(client):
    assert client.get("/status").json()["generator"] == "trellis2"


def test_unsupported_generator_env_raises(monkeypatch, tmp_path):
    monkeypatch.setenv("VRCRAFTER_DATA_DIR", str(tmp_path))
    monkeypatch.setenv("VRCRAFTER_GENERATOR", "bogus")
    with pytest.raises(RuntimeError, match="bogus"):
        with TestClient(app):
            pass


def test_main_does_not_import_torch():
    code = "import sys, app.main; assert not {'torch', 'trellis2', 'PIL'} & set(sys.modules)"
    assert subprocess.run([sys.executable, "-c", code], cwd=BACKEND_DIR).returncode == 0


def test_invalid_response_mode_is_error_json(client):
    r = client.post("/generate", data={"response_mode": "bogus"}, files={"file": ("a.png", b"x")})
    assert r.status_code == 200 and r.json() == {
        "status": "error", "error": "response_mode must be model, voxels, or both"}


def test_malformed_requests_are_error_json(client):
    r = client.post("/generate", data={"response_mode": "both"})
    assert r.status_code == 200 and r.json()["status"] == "error" and r.json()["error"].startswith("Invalid request:")
    r0 = post_generate(client)
    r = client.post(urlparse(r0["revisions_url"]).path,
                    json={"base_revision": 0, "max_voxels": 10, "voxels": [ev(0, 1.5, 1, 1)]})
    assert r.status_code == 200 and r.json()["status"] == "error" and r.json()["error"].startswith("Invalid request:")


def test_glb_route_bad_revision_or_gid_is_404(client):
    gid = post_generate(client)["generation_id"]
    assert client.get(f"/generations/{gid}/revisions/abc/model.glb").status_code == 404
    assert client.get("/generations/not-a-gid/revisions/0/model.glb").status_code == 404


@pytest.mark.fake(mesh_error=RuntimeError("boom"))
def test_revision_mesh_failure_keeps_latest(client):
    fake.mesh_error = None
    r0 = post_generate(client)
    fake.mesh_error = RuntimeError("boom")
    assert post_revision(client, r0, [ev(0, 1, 1, 1)])["error"] == "Generation failed: boom"
    assert app.state.store.latest_revision(r0["generation_id"]) == 0
    assert post_revision(client, r0, [ev(0, 1, 1, 1)])["revision"] == 1


def test_revision_reuses_seed(client):
    r0 = post_generate(client, seed=7)
    post_revision(client, r0, [ev(0, 1, 1, 1)])
    assert fake.mesh_seeds == [7, 7]


def test_model_error_when_no_glb(client):
    fake.skip_glb = True
    r = post_generate(client)
    assert r["model_error"] == "No GLB was produced." and "model_url" not in r
