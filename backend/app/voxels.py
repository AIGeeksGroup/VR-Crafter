"""Deterministic voxel fixtures used until an AI generator is connected."""


def _set_voxel(voxels: dict, x: int, y: int, z: int, kind: str, color: str) -> None:
    if 0 <= x < 64 and 0 <= y < 64 and 0 <= z < 64:
        voxels[(x, y, z)] = {
            "x": x,
            "y": y,
            "z": z,
            "type": kind,
            "color": color,
        }


def _add_box(
    voxels: dict,
    x0: int,
    x1: int,
    y0: int,
    y1: int,
    z0: int,
    z1: int,
    kind: str,
    color: str,
) -> None:
    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            for z in range(z0, z1 + 1):
                _set_voxel(voxels, x, y, z, kind, color)


def _add_wall_box(
    voxels: dict,
    x0: int,
    x1: int,
    y0: int,
    y1: int,
    z0: int,
    z1: int,
    kind: str,
    color: str,
) -> None:
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            _set_voxel(voxels, x, y, z0, kind, color)
            _set_voxel(voxels, x, y, z1, kind, color)
        for z in range(z0, z1 + 1):
            _set_voxel(voxels, x0, y, z, kind, color)
            _set_voxel(voxels, x1, y, z, kind, color)


def _add_post(voxels: dict, x: int, z: int, y0: int, y1: int, color: str) -> None:
    for y in range(y0, y1 + 1):
        _set_voxel(voxels, x, y, z, "wood", color)


def generate_test_house_voxels(max_voxels: int = 12_000) -> list[dict]:
    """Build the existing Minecraft-style 64-cubed test house fixture."""
    voxels: dict[tuple[int, int, int], dict] = {}

    wall = "#E8DDC7"
    wood = "#A66A3D"
    dark_wood = "#6B3F24"
    roof = "#5F6264"
    roof_dark = "#3D3F40"
    foundation = "#B8B8B8"
    window = "#F4F6F7"
    stone = "#9A9A8A"
    plant = "#4D7C3A"

    _add_box(voxels, 10, 54, 0, 0, 12, 48, "foundation", foundation)
    _add_box(voxels, 14, 50, 1, 1, 18, 42, "wood_floor", dark_wood)
    _add_wall_box(voxels, 16, 48, 2, 16, 18, 42, "wall", wall)

    post_positions = (
        (16, 18),
        (32, 18),
        (48, 18),
        (16, 42),
        (32, 42),
        (48, 42),
        (16, 30),
        (48, 30),
    )
    for x, z in post_positions:
        _add_post(voxels, x, z, 1, 20, wood)

    _add_box(voxels, 16, 48, 17, 17, 18, 18, "wood", wood)
    _add_box(voxels, 16, 48, 17, 17, 42, 42, "wood", wood)
    _add_box(voxels, 16, 16, 17, 17, 18, 42, "wood", wood)
    _add_box(voxels, 48, 48, 17, 17, 18, 42, "wood", wood)

    _add_box(voxels, 20, 26, 3, 13, 17, 17, "door", "#D8D2C2")
    _add_box(voxels, 30, 44, 6, 14, 17, 17, "window", window)
    _add_box(voxels, 49, 49, 6, 14, 26, 38, "window", window)

    for x in range(30, 45, 4):
        _add_box(voxels, x, x, 6, 14, 16, 16, "wood", wood)
    for y in range(6, 15, 4):
        _add_box(voxels, 30, 44, y, y, 16, 16, "wood", wood)
    for z in range(26, 39, 4):
        _add_box(voxels, 50, 50, 6, 14, z, z, "wood", wood)

    center_z = 30
    for x in range(8, 57):
        for z in range(8, 53):
            slope = abs(z - center_z)
            y = 29 - slope // 2
            if y >= 18:
                color = roof_dark if x % 6 == 0 or z % 6 == 0 else roof
                _set_voxel(voxels, x, y, z, "roof", color)
                _set_voxel(voxels, x, y - 1, z, "roof", color)

    _add_box(voxels, 8, 56, 30, 30, 30, 30, "roof_ridge", roof_dark)
    _add_box(voxels, 8, 56, 18, 18, 8, 8, "roof_edge", roof_dark)
    _add_box(voxels, 8, 56, 18, 18, 52, 52, "roof_edge", roof_dark)

    for x in range(10, 30):
        for z in range(8, 17):
            y = 15 + (z - 8) // 4
            _set_voxel(voxels, x, y, z, "small_roof", roof)
            _set_voxel(voxels, x, y - 1, z, "small_roof", roof)

    _add_post(voxels, 10, 10, 1, 15, wood)
    _add_post(voxels, 28, 10, 1, 15, wood)

    for x, z in ((12, 6), (18, 6), (24, 7), (30, 7), (42, 52), (48, 54), (54, 55)):
        _add_box(voxels, x, x + 2, 1, 1, z, z + 1, "stone", stone)

    for x, z in ((8, 48), (10, 50), (56, 18), (58, 20)):
        _add_box(voxels, x, x + 1, 1, 4, z, z + 1, "plant", plant)

    return list(voxels.values())[:max_voxels]
