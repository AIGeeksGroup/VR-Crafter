"""FastAPI bridge used by the Unity VR client."""

from pathlib import Path
import shutil
import uuid

from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse

from .voxels import generate_test_house_voxels


BACKEND_DIR = Path(__file__).resolve().parent.parent
UPLOAD_DIR = BACKEND_DIR / "data" / "received_images"
MODEL_DIR = BACKEND_DIR / "models"
MODEL_NAMES = ("test_model.glb", "sample.glb", "PropertyDemoModel.glb")

UPLOAD_DIR.mkdir(parents=True, exist_ok=True)
MODEL_DIR.mkdir(parents=True, exist_ok=True)

app = FastAPI(title="VR-Crafter Backend")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)


def _save_uploaded_image(file: UploadFile) -> Path:
    suffix = Path(file.filename or "upload.png").suffix or ".png"
    image_path = UPLOAD_DIR / f"{uuid.uuid4()}{suffix.lower()}"
    with image_path.open("wb") as buffer:
        shutil.copyfileobj(file.file, buffer)
    return image_path


def _find_local_model() -> Path | None:
    for name in MODEL_NAMES:
        candidate = MODEL_DIR / name
        if candidate.is_file():
            return candidate
    return next(MODEL_DIR.glob("*.glb"), None)


@app.get("/status")
def status() -> dict:
    return {
        "status": "ok",
        "message": "VR-Crafter backend is running.",
        "model_available": _find_local_model() is not None,
    }


@app.get("/health")
def health() -> dict:
    return status()


@app.get("/model")
def get_model() -> FileResponse:
    model_path = _find_local_model()
    if model_path is None:
        raise HTTPException(status_code=404, detail="No GLB file found in backend/models.")
    return FileResponse(
        path=model_path,
        media_type="model/gltf-binary",
        filename=model_path.name,
    )


@app.post("/generate")
async def generate(
    request: Request,
    file: UploadFile = File(...),
    response_mode: str = Form("model"),
    max_voxels: int = Form(12_000),
) -> dict:
    mode = response_mode.lower().strip()
    if mode not in {"model", "voxels", "both"}:
        raise HTTPException(status_code=400, detail="response_mode must be model, voxels, or both")

    _save_uploaded_image(file)
    result: dict = {"status": "done", "response_mode": mode}

    if mode in {"model", "both"}:
        if _find_local_model() is None:
            result["model_error"] = "No local GLB found in backend/models."
        else:
            result["model_url"] = str(request.base_url).rstrip("/") + "/model"

    if mode in {"voxels", "both"}:
        voxels = generate_test_house_voxels(max_voxels=max(1, min(max_voxels, 12_000)))
        result.update(grid_size=64, voxel_count=len(voxels), voxels=voxels)

    return result
