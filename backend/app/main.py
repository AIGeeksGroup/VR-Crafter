"""FastAPI bridge used by the Unity VR client."""

from contextlib import asynccontextmanager
import os
from pathlib import Path
import re
import shutil
import threading

from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.concurrency import run_in_threadpool
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel

from .generation_store import GenerationStore
from .generators import FixtureGenerator, GenerationError, find_local_model
from .voxel_mapping import GRID_SIZE


BACKEND_DIR = Path(__file__).resolve().parent.parent
MODEL_DIR = BACKEND_DIR / "models"
DEFAULT_SEED = 42
_COLOR_RE = re.compile(r"#[0-9A-Fa-f]{6}")

MODEL_DIR.mkdir(parents=True, exist_ok=True)

# Serializes all GPU-side work; also guards GenerationStore.allocate_ids.
_gpu_lock = threading.Lock()


def _build_generator():
    kind = os.environ.get("VRCRAFTER_GENERATOR", "fixture")
    if kind == "fixture":
        return FixtureGenerator(MODEL_DIR)
    raise RuntimeError(f"Unsupported VRCRAFTER_GENERATOR value: {kind!r}")


@asynccontextmanager
async def lifespan(app: FastAPI):
    data_dir = Path(os.environ.get("VRCRAFTER_DATA_DIR", BACKEND_DIR / "data" / "generations"))
    data_dir.mkdir(parents=True, exist_ok=True)
    app.state.store = GenerationStore(data_dir)
    app.state.generator = _build_generator()
    yield


app = FastAPI(title="VR-Crafter Backend", lifespan=lifespan)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.exception_handler(RequestValidationError)
async def _validation_error(request: Request, exc: RequestValidationError):
    if request.url.path.endswith("/model.glb"):
        return JSONResponse({"detail": "No GLB for this revision."}, status_code=404)
    first = exc.errors()[0] if exc.errors() else {}
    where = ".".join(str(p) for p in first.get("loc", ()))
    return JSONResponse(_error(f"Invalid request: {where} {first.get('msg', '')}".strip()))


class EditedVoxel(BaseModel):
    id: int
    x: int
    y: int
    z: int
    type: str
    color: str


class RevisionRequest(BaseModel):
    base_revision: int
    max_voxels: int
    voxels: list[EditedVoxel]


def _error(message: str) -> dict:
    return {"status": "error", "error": message}


def _failure(exc: Exception) -> dict:
    if isinstance(exc, GenerationError):
        return _error(str(exc))
    return _error(f"Generation failed: {exc}")


def _success(request: Request, mode: str, gid: str, rev: int, voxels: list[dict]) -> dict:
    store: GenerationStore = request.app.state.store
    generator = request.app.state.generator
    base = str(request.base_url).rstrip("/")
    result: dict = {
        "status": "done",
        "response_mode": mode,
        "source": generator.source,
        "seed": store.meta(gid)["seed"],
        "generation_id": gid,
        "revision": rev,
        "grid_size": GRID_SIZE,
        "voxel_count": len(voxels),
    }
    if mode in {"voxels", "both"}:
        result["voxels"] = voxels
    if mode in {"model", "both"}:
        if store.model_path(gid, rev) is None:
            result["model_error"] = "No GLB was produced."
        else:
            result["model_url"] = f"{base}/generations/{gid}/revisions/{rev}/model.glb"
    if generator.model_space is not None:
        result["model_space"] = generator.model_space
    result["revisions_url"] = f"{base}/generations/{gid}/revisions"
    return result


@app.get("/status")
def status(request: Request) -> dict:
    return {
        "status": "ok",
        "message": "VR-Crafter backend is running.",
        "model_available": find_local_model(MODEL_DIR) is not None,
        "generator": request.app.state.generator.source,
    }


@app.get("/health")
def health(request: Request) -> dict:
    return status(request)


@app.get("/model")
def get_model() -> FileResponse:
    model_path = find_local_model(MODEL_DIR)
    if model_path is None:
        raise HTTPException(status_code=404, detail="No GLB file found in backend/models.")
    return FileResponse(
        path=model_path,
        media_type="model/gltf-binary",
        filename=model_path.name,
    )


def _generate_worker(store, generator, gid: str, seed: int, max_voxels: int) -> list[dict]:
    with _gpu_lock:
        image_path = store.image_path(gid)
        voxels = generator.structure(image_path, seed)
        if not voxels:
            raise GenerationError("Generator produced no occupied voxels.")
        if len(voxels) > max_voxels:
            raise GenerationError(
                f"Generator produced {len(voxels)} voxels, above max_voxels={max_voxels}. "
                "Raise Max Voxels on the Unity Server component."
            )
        ids = store.allocate_ids(gid, len(voxels))
        voxels = [{**v, "id": i} for i, v in zip(ids, voxels)]
        coords = [(v["x"], v["y"], v["z"]) for v in voxels]
        generator.mesh(image_path, seed, coords, store.revision_dir(gid, 0) / "model.glb")
        store.save_revision(gid, 0, voxels, None)
        return voxels


@app.post("/generate")
async def generate(
    request: Request,
    file: UploadFile = File(...),
    response_mode: str = Form("model"),
    max_voxels: int = Form(12_000),
    seed: int | None = Form(None),
) -> dict:
    mode = response_mode.lower().strip()
    if mode not in {"model", "voxels", "both"}:
        return _error("response_mode must be model, voxels, or both")
    seed = DEFAULT_SEED if seed is None else seed

    store: GenerationStore = request.app.state.store
    generator = request.app.state.generator
    gid = None
    try:
        data = await file.read()
        suffix = Path(file.filename or "upload.png").suffix.lower() or ".png"
        gid = store.create(data, suffix, seed, generator.source)
        voxels = await run_in_threadpool(_generate_worker, store, generator, gid, seed, max_voxels)
        return _success(request, mode, gid, 0, voxels)
    except Exception as exc:
        if gid is not None:
            shutil.rmtree(store.root / gid, ignore_errors=True)
        return _failure(exc)


def _merge_edits(store, gid: str, latest: int, body: RevisionRequest) -> list[dict]:
    """Validate the submitted voxels and return the merged list (without new ids)."""
    known = {v["id"] for v in store.load_voxels(gid, latest)}
    seen: set[int] = set()
    for v in body.voxels:
        if v.id < -1 or (v.id >= 0 and v.id not in known):
            raise GenerationError(f"Unknown voxel id {v.id}.")
        if v.id >= 0:
            if v.id in seen:
                raise GenerationError(f"Duplicate voxel id {v.id}.")
            seen.add(v.id)
        if not all(0 <= c < GRID_SIZE for c in (v.x, v.y, v.z)):
            raise GenerationError(f"Voxel {v.id} is outside the {GRID_SIZE}^3 grid.")
        if not _COLOR_RE.fullmatch(v.color):
            raise GenerationError(f"Voxel {v.id} has invalid color {v.color}.")

    existing = sorted((v for v in body.voxels if v.id >= 0), key=lambda v: v.id)
    new = [v for v in body.voxels if v.id < 0]
    cells: set[tuple[int, int, int]] = set()
    merged: list[dict] = []
    for v in existing + new:
        cell = (v.x, v.y, v.z)
        if cell in cells:
            continue
        cells.add(cell)
        merged.append({"id": v.id, "x": v.x, "y": v.y, "z": v.z, "type": v.type, "color": v.color})
    if not merged:
        raise GenerationError("Edited model has no voxels.")
    if len(merged) > body.max_voxels:
        raise GenerationError(
            f"Edited model has {len(merged)} voxels, above max_voxels={body.max_voxels}."
        )
    return merged


def _revision_worker(store, generator, gid: str, body: RevisionRequest) -> tuple[int, list[dict]]:
    with _gpu_lock:
        try:
            latest = store.latest_revision(gid)
        except KeyError:
            raise GenerationError(f"Unknown generation {gid}.") from None
        if latest is None:
            raise GenerationError(f"Unknown generation {gid}.")
        if body.base_revision != latest:
            raise GenerationError(f"Revision {body.base_revision} is stale; latest is {latest}.")
        voxels = _merge_edits(store, gid, latest, body)
        new_slots = [v for v in voxels if v["id"] < 0]
        for slot, new_id in zip(new_slots, store.allocate_ids(gid, len(new_slots))):
            slot["id"] = new_id
        rev = latest + 1
        coords = [(v["x"], v["y"], v["z"]) for v in voxels]
        meta = store.meta(gid)
        generator.mesh(
            store.image_path(gid), meta["seed"], coords, store.revision_dir(gid, rev) / "model.glb"
        )
        store.save_revision(gid, rev, voxels, latest)
        return rev, voxels


@app.post("/generations/{generation_id}/revisions")
async def create_revision(generation_id: str, body: RevisionRequest, request: Request) -> dict:
    store: GenerationStore = request.app.state.store
    generator = request.app.state.generator
    try:
        rev, voxels = await run_in_threadpool(_revision_worker, store, generator, generation_id, body)
        return _success(request, "both", generation_id, rev, voxels)
    except Exception as exc:
        return _failure(exc)


@app.get("/generations/{generation_id}/revisions/{revision}/model.glb")
def get_revision_model(generation_id: str, revision: int, request: Request) -> FileResponse:
    try:
        path = request.app.state.store.model_path(generation_id, revision)
    except KeyError:
        path = None
    if path is None:
        raise HTTPException(status_code=404, detail="No GLB for this revision.")
    return FileResponse(path=path, media_type="model/gltf-binary", filename="model.glb")
