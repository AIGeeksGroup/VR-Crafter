import re

import pytest

from app.generation_store import GenerationStore


@pytest.fixture
def store(tmp_path):
    return GenerationStore(tmp_path)


@pytest.fixture
def gid(store):
    return store.create(b"png-bytes", ".png", seed=7, source="fixture")


def test_create_and_read_back(tmp_path):
    store = GenerationStore(tmp_path)
    gid = store.create(b"png-bytes", ".png", seed=7, source="fixture")
    assert re.fullmatch(r"[0-9a-f]{32}", gid)
    assert store.image_path(gid).read_bytes() == b"png-bytes"
    assert store.meta(gid)["seed"] == 7 and store.meta(gid)["source"] == "fixture"
    assert store.latest_revision(gid) is None


def test_allocate_ids_is_monotonic_and_persistent(tmp_path, store, gid):
    assert store.allocate_ids(gid, 3) == [0, 1, 2]
    assert GenerationStore(tmp_path).allocate_ids(gid, 2) == [3, 4]     # 新实例从磁盘读取 next_id


def test_revision_commit(store, gid):
    (store.revision_dir(gid, 0) / "model.glb").write_bytes(b"glb")
    store.save_revision(gid, 0, [{"id": 0, "x": 1, "y": 2, "z": 3, "type": "occupied", "color": "#C8C8C8"}], parent=None)
    assert store.latest_revision(gid) == 0
    assert store.load_voxels(gid, 0)[0]["x"] == 1
    assert store.model_path(gid, 0).read_bytes() == b"glb"


def test_uncommitted_revision_is_ignored(store, gid):   # 已写 GLB，但未调用 save_revision
    (store.revision_dir(gid, 0) / "model.glb").write_bytes(b"glb")
    assert store.latest_revision(gid) is None and store.model_path(gid, 0) is None


def test_unsafe_suffix_falls_back_to_png(store):
    assert store.image_path(store.create(b"x", "/../evil", 1, "fixture")).name == "input.png"


@pytest.mark.parametrize("bad", ["../etc", "abc", "0" * 31, "G" * 32, "0" * 32])  # 最后一个：格式合法但不存在
def test_rejects_malformed_generation_id(tmp_path, bad):
    with pytest.raises(KeyError):
        GenerationStore(tmp_path).latest_revision(bad)
