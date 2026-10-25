import json
import random

import pytest

from app.voxel_mapping import build_voxel_payload, trellis_to_unity, unity_to_trellis


def test_trellis_to_unity_axes():
    assert trellis_to_unity([(0, 0, 0)]) == [(63, 0, 63)]
    assert trellis_to_unity([(63, 0, 0)]) == [(0, 0, 63)]
    assert trellis_to_unity([(0, 63, 0)]) == [(63, 0, 0)]
    assert trellis_to_unity([(0, 0, 63)]) == [(63, 63, 63)]
    assert trellis_to_unity([(10, 20, 30)]) == [(53, 30, 43)]


def test_round_trip():
    rng = random.Random(0)
    coords = [tuple(rng.randrange(64) for _ in range(3)) for _ in range(1000)]
    assert unity_to_trellis(trellis_to_unity(coords)) == coords


@pytest.mark.parametrize("bad", [(64, 0, 0), (0, -1, 0), (0, 0, 64)])
def test_out_of_range_rejected(bad):
    with pytest.raises(ValueError):
        trellis_to_unity([bad])
    with pytest.raises(ValueError):
        unity_to_trellis([bad])


def test_payload_is_plain_json():
    payload = build_voxel_payload([(1, 2, 3)])
    assert payload == [{"x": 1, "y": 2, "z": 3, "type": "occupied", "color": "#C8C8C8"}]
    json.dumps(payload)
