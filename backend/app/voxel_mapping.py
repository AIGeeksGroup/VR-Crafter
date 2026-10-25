"""Coordinate mapping between TRELLIS.2 voxel indices (Z-up) and Unity grid indices (Y-up)."""

from typing import Iterable, Sequence

GRID_SIZE = 64
DEFAULT_COLOR = "#C8C8C8"
DEFAULT_TYPE = "occupied"


def _checked(coord: Sequence[int], grid_size: int) -> tuple[int, int, int]:
    a, b, c = (int(v) for v in coord)
    if not all(0 <= v < grid_size for v in (a, b, c)):
        raise ValueError(f"coordinate {(a, b, c)} outside [0, {grid_size})")
    return a, b, c


def trellis_to_unity(
    coords: Iterable[Sequence[int]], grid_size: int = GRID_SIZE
) -> list[tuple[int, int, int]]:
    """(ix, iy, iz) Z-up -> (R-1-ix, iz, R-1-iy) Y-up."""
    r = grid_size
    out = []
    for coord in coords:
        ix, iy, iz = _checked(coord, r)
        out.append((r - 1 - ix, iz, r - 1 - iy))
    return out


def unity_to_trellis(
    coords: Iterable[Sequence[int]], grid_size: int = GRID_SIZE
) -> list[tuple[int, int, int]]:
    """(ux, uy, uz) Y-up -> (R-1-ux, R-1-uz, uy) Z-up."""
    r = grid_size
    out = []
    for coord in coords:
        ux, uy, uz = _checked(coord, r)
        out.append((r - 1 - ux, r - 1 - uz, uy))
    return out


def build_voxel_payload(
    unity_coords: Iterable[Sequence[int]],
    color: str = DEFAULT_COLOR,
    kind: str = DEFAULT_TYPE,
) -> list[dict]:
    """Voxel dicts in the JSON shape Unity consumes (no id)."""
    return [
        {"x": int(x), "y": int(y), "z": int(z), "type": kind, "color": color}
        for x, y, z in unity_coords
    ]
