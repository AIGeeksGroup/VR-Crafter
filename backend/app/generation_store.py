"""On-disk store for generations and their numbered revisions."""

import json
import os
import re
import uuid
from pathlib import Path

_GID_RE = re.compile(r"[0-9a-f]{32}")
_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp"}


class GenerationStore:
    def __init__(self, root: Path) -> None:
        self.root = Path(root)

    def _dir(self, gid: str) -> Path:
        if not isinstance(gid, str) or not _GID_RE.fullmatch(gid):
            raise KeyError(gid)
        path = self.root / gid
        if not path.is_dir():
            raise KeyError(gid)
        return path

    def _write_json(self, path: Path, data: dict) -> None:
        tmp = path.with_name(path.name + ".tmp")
        tmp.write_text(json.dumps(data), encoding="utf-8")
        os.replace(tmp, path)

    def create(self, image_bytes: bytes, suffix: str, seed: int, source: str) -> str:
        if suffix not in _SUFFIXES:
            suffix = ".png"
        gid = uuid.uuid4().hex
        path = self.root / gid
        path.mkdir(parents=True)
        (path / f"input{suffix}").write_bytes(image_bytes)
        self._write_json(path / "meta.json", {"seed": seed, "source": source, "next_id": 0})
        return gid

    def image_path(self, gid: str) -> Path:
        path = self._dir(gid)
        return next(p for p in path.glob("input.*"))

    def meta(self, gid: str) -> dict:
        return json.loads((self._dir(gid) / "meta.json").read_text(encoding="utf-8"))

    def allocate_ids(self, gid: str, count: int) -> list[int]:
        path = self._dir(gid) / "meta.json"
        meta = json.loads(path.read_text(encoding="utf-8"))
        start = meta["next_id"]
        meta["next_id"] = start + count
        self._write_json(path, meta)
        return list(range(start, start + count))

    def latest_revision(self, gid: str) -> int | None:
        revs = [
            int(p.name[4:])
            for p in self._dir(gid).glob("rev_*")
            if re.fullmatch(r"rev_\d+", p.name) and (p / "revision.json").is_file()
        ]
        return max(revs) if revs else None

    def revision_dir(self, gid: str, rev: int) -> Path:
        path = self._dir(gid) / f"rev_{int(rev)}"
        path.mkdir(exist_ok=True)
        return path

    def save_revision(self, gid: str, rev: int, voxels: list[dict], parent: int | None) -> None:
        path = self.revision_dir(gid, rev) / "revision.json"
        self._write_json(path, {"parent": parent, "voxels": voxels})

    def load_voxels(self, gid: str, rev: int) -> list[dict]:
        path = self._dir(gid) / f"rev_{int(rev)}" / "revision.json"
        return json.loads(path.read_text(encoding="utf-8"))["voxels"]

    def model_path(self, gid: str, rev: int) -> Path | None:
        path = self._dir(gid) / f"rev_{int(rev)}"
        if not (path / "revision.json").is_file() or not (path / "model.glb").is_file():
            return None
        return path / "model.glb"
