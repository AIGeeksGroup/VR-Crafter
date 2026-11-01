"""Bake an existing GLB surface into editable, colored voxels for offline Unity use.

Run with Blender: blender -b --python Tools/voxelize_existing_glb.py -- input.glb output.json
No model generation or network connection is involved.
"""

import bpy
import hashlib
import json
import math
import sys
from collections import defaultdict
from mathutils import Vector

import numpy as np

source = sys.argv[sys.argv.index("--") + 1]
destination = sys.argv[sys.argv.index("--") + 2]
grid_size = 40

bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.gltf(filepath=source)
objects = [obj for obj in bpy.context.scene.objects if obj.type == "MESH" and
           any(material and material.node_tree and
               any(node.type == "TEX_IMAGE" and node.image is not None for node in material.node_tree.nodes)
               for material in obj.data.materials)]
if not objects:
    raise RuntimeError("The GLB has no mesh")

corners = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
minimum = Vector(tuple(min(point[axis] for point in corners) for axis in range(3)))
maximum = Vector(tuple(max(point[axis] for point in corners) for axis in range(3)))
pitch = max(maximum - minimum) / grid_size
if pitch <= 0:
    raise RuntimeError("The GLB has zero extent")

image_cache = {}


def texture_for(material):
    if material is None or material.node_tree is None:
        return None
    for node in material.node_tree.nodes:
        if node.type != "TEX_IMAGE" or node.image is None:
            continue
        image = node.image
        if image.name not in image_cache:
            width, height = image.size
            pixels = np.empty(width * height * 4, dtype=np.float32)
            image.pixels.foreach_get(pixels)
            image_cache[image.name] = pixels.reshape(height, width, 4)
        return image_cache[image.name]
    return None


def sample_color(texture, uv):
    if texture is None:
        return (0.78, 0.78, 0.78)
    height, width, _ = texture.shape
    u = max(0, min(width - 1, int((uv.x % 1.0) * width)))
    v = max(0, min(height - 1, int((uv.y % 1.0) * height)))
    return texture[v, u, :3]


def cell_for(position):
    # Blender's Z is up. Unity's Y is up; Blender's negative Y faces the viewer.
    return (
        max(0, min(grid_size - 1, int((position.x - minimum.x) / pitch))),
        max(0, min(grid_size - 1, int((position.z - minimum.z) / pitch))),
        max(0, min(grid_size - 1, int((position.y - minimum.y) / pitch))),
    )


samples = defaultdict(lambda: [0.0, 0.0, 0.0, 0])
for obj in objects:
    mesh = obj.data
    mesh.calc_loop_triangles()
    uv_layer = mesh.uv_layers.active
    materials = [texture_for(material) for material in mesh.materials]
    for triangle in mesh.loop_triangles:
        positions = [obj.matrix_world @ mesh.vertices[index].co for index in triangle.vertices]
        uvs = [uv_layer.data[index].uv.copy() for index in triangle.loops] if uv_layer else [Vector((0, 0))] * 3
        texture = materials[triangle.material_index] if triangle.material_index < len(materials) else None
        longest_edge = max((positions[1] - positions[0]).length,
                           (positions[2] - positions[1]).length,
                           (positions[0] - positions[2]).length)
        divisions = max(1, min(64, math.ceil(longest_edge / (pitch * 0.55))))
        for row in range(divisions + 1):
            for column in range(divisions + 1 - row):
                a = row / divisions
                b = column / divisions
                c = 1.0 - a - b
                position = positions[0] * a + positions[1] * b + positions[2] * c
                uv = uvs[0] * a + uvs[1] * b + uvs[2] * c
                rgb = sample_color(texture, uv)
                sample = samples[cell_for(position)]
                sample[0] += float(rgb[0])
                sample[1] += float(rgb[1])
                sample[2] += float(rgb[2])
                sample[3] += 1


def to_srgb(linear):
    linear = max(0.0, min(1.0, linear))
    return linear * 12.92 if linear <= 0.0031308 else 1.055 * linear ** (1 / 2.4) - 0.055


voxels = []
for voxel_id, (cell, values) in enumerate(sorted(samples.items())):
    color = "#" + "".join(f"{round(to_srgb(values[i] / values[3]) * 255):02X}" for i in range(3))
    voxels.append({"id": voxel_id, "x": cell[0], "y": cell[1], "z": cell[2],
                   "type": "mesh_surface", "color": color})

digest = hashlib.sha256(open(source, "rb").read()).hexdigest()
payload = {"status": "done", "source": "local_glb", "response_mode": "voxels",
           "generation_id": digest, "revision": 0, "grid_size": grid_size,
           "voxel_count": len(voxels), "voxels": voxels}
with open(destination, "w", encoding="utf-8") as output:
    json.dump(payload, output, separators=(",", ":"))
print(f"OFFLINE_VOXELS source_sha256={digest} count={len(voxels)} grid={grid_size} pitch={pitch:.6f}")
