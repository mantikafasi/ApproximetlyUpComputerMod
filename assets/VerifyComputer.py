"""Run with Blender --background assets/computer.blend --python this_file.

Reads the delivered files, checks them independently, and updates only the
validation report beside the blend file. Does not save the opened scene.
"""

import bpy
import hashlib
import json
import math
import struct
from array import array
from collections import Counter
from pathlib import Path
from runpy import run_path
from mathutils import Matrix, Vector

assets = Path(bpy.data.filepath).parent
icon_rgba_bytes = run_path(str(assets / "IconPixels.py"))["icon_rgba_bytes"]
assert Path(bpy.data.filepath).name == "computer.blend"
data = json.loads(assets.joinpath("computer.mesh.json").read_text(encoding="utf-8"))
assert set(data) == {"version", "name", "bounds", "parts", "ports"}
assert data["version"] == 1 and data["name"] == "AU-08"
assert data["bounds"] == [1, 0.25, 1]
assert set(data["ports"]) == {"inputs", "outputs"}
assert len({p["name"] for p in data["parts"]}) == len(data["parts"])


def vector(values, size):
    assert len(values) == size
    assert all(type(v) in (int, float) and math.isfinite(v) for v in values)
    return Vector(values)


def signature(positions, normals):
    return tuple(tuple(round(v, 7) for v in (*p, *n)) for p, n in zip(positions, normals))


json_triangles = {}
all_positions = []
minimum_dot = 1.0
for part in data["parts"]:
    assert set(part) == {"name", "color", "vertices", "normals", "triangles"}
    assert isinstance(part["name"], str) and part["name"]
    assert all(0 <= v <= 1 for v in vector(part["color"], 4))
    positions = [vector(v, 3) for v in part["vertices"]]
    normals = [vector(n, 3) for n in part["normals"]]
    indices = part["triangles"]
    assert len(positions) == len(normals) and positions
    assert all(abs(n.length - 1) < 1e-5 for n in normals)
    assert len(indices) % 3 == 0
    assert all(type(i) is int and 0 <= i < len(positions) for i in indices)
    triangles = Counter()
    for offset in range(0, len(indices), 3):
        ids = indices[offset:offset + 3]
        a, b, c = [positions[i] for i in ids]
        cross = (b - a).cross(c - a)
        assert cross.length > 1e-14
        alignment = min(cross.normalized().dot(normals[i]) for i in ids)
        assert alignment > 0
        minimum_dot = min(minimum_dot, alignment)
        triangles[signature([positions[i] for i in ids], [normals[i] for i in ids])] += 1
    json_triangles[part["name"]] = triangles
    all_positions.extend(positions)
low = [min(p[i] for p in all_positions) for i in range(3)]
high = [max(p[i] for p in all_positions) for i in range(3)]
assert all(abs(high[i] - low[i] - data["bounds"][i]) < 1e-5 for i in range(3))
assert all(abs(high[i] + low[i]) < 1e-5 for i in range(3))
count = sum(sum(triangles.values()) for triangles in json_triangles.values())
assert 0 < count < 8000
for role, sign in (("inputs", -1), ("outputs", 1)):
    ports = [vector(p, 3) for p in data["ports"][role]]
    assert len(ports) == 8
    for i, port in enumerate(ports):
        assert (port - Vector((sign * 0.5, -0.0625, 0.4375 - i * 0.125))).length < 1e-6
        # Compare exported face geometry, not merely duplicated marker positions.
        ring_part = next(p for p in data["parts"] if p["name"] == ("InputAmber" if sign < 0 else "OutputBlue"))
        face = {tuple(p) for p in ring_part["vertices"]
                if abs(p[0] - port.x) < 1e-6 and abs(p[2] - port.z) < 0.05 and abs(p[1] - port.y) < 0.05}
        assert len(face) == 24
        center = sum((Vector(p) for p in face), Vector()) / len(face)
        assert (center - port).length < 1e-6
        radii = sorted((Vector(p) - center).length for p in face)
        assert all(abs(r - 0.022) < 1e-6 for r in radii[:12])
        assert all(abs(r - 0.03501129523) < 1e-6 for r in radii[12:])
        # C1B850..C1B879: LocalTransform.Position is the anchor. Native mesh face is +Z/16.
        anchor = port - Vector((sign * 0.0625, 0, 0))
        cable_center = anchor + Vector((sign * 0.125, 0, 0))
        assert (cable_center - Vector((sign * 0.0625, 0, 0)) - center).length < 1e-6
        assert all(abs(v * 16 - round(v * 16)) < 1e-6 for v in anchor)

assert [s.name for s in bpy.data.scenes] == ["AU_Computer_Preview"]
assert bpy.data.objects.get("Cube") is None
assert all(o.get("au_owner") == "approximately-up-computer-assets" for o in bpy.data.objects)
assert not bpy.data.libraries
assert not any(n.type == "TEX_IMAGE" for m in bpy.data.materials if m.use_nodes for n in m.node_tree.nodes)
assert not [f for f in bpy.data.fonts if f.filepath and f.filepath != "<builtin>" and not f.packed_file]
root = bpy.data.objects.get("AU_Computer_Root")
assert root and root.location.length == 0 and tuple(root.scale) == (1, 1, 1)
assert root["au_geometry_version"] == 2
assert bpy.context.scene.camera.name == "AU_PreviewCamera"
assert set(c.name for c in bpy.data.collections) == {"AU_Computer", "AU_Computer_Studio"}
convert = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
root_inverse = root.matrix_world.inverted()
evaluated_triangles = {}
graph = bpy.context.evaluated_depsgraph_get()
for obj in bpy.data.collections.get("AU_Computer").objects:
    if obj.type not in {"MESH", "CURVE", "FONT"}:
        continue
    evaluated = obj.evaluated_get(graph)
    mesh = evaluated.to_mesh()
    mesh.calc_loop_triangles()
    transform = convert @ root_inverse @ evaluated.matrix_world
    normal_transform = transform.to_3x3().inverted().transposed()
    for tri in mesh.loop_triangles:
        name = mesh.materials[tri.material_index].name.removeprefix("AU_Mat_")
        positions = [transform @ mesh.vertices[mesh.loops[i].vertex_index].co for i in tri.loops]
        normals = [(normal_transform @ mesh.corner_normals[i].vector).normalized() for i in tri.loops]
        evaluated_triangles.setdefault(name, Counter())[signature(positions, normals)] += 1
    evaluated.to_mesh_clear()
assert evaluated_triangles == json_triangles, "Saved evaluated geometry differs from JSON"
for role in ("inputs", "outputs"):
    for i, port in enumerate(data["ports"][role]):
        marker = bpy.data.objects.get(f"AU_Port_{role}_{i + 1:02}")
        assert (convert @ root_inverse @ marker.matrix_world.translation - Vector(port)).length < 1e-6

images = {}
raw_icon = assets.joinpath("computer-icon.rgba").read_bytes()
assert len(raw_icon) == 8 + 512 * 512 * 4
assert struct.unpack_from("<II", raw_icon) == (512, 512)
assert raw_icon == icon_rgba_bytes(assets / "computer-icon.png"), "Raw icon differs from PNG samples/row order"
for name, expected in (("computer-icon.png", (512, 512)), ("computer-preview.png", (1200, 1000))):
    image = bpy.data.images.load(str(assets / name), check_existing=False)
    assert tuple(image.size) == expected and image.channels == 4
    info = {"size": list(image.size), "channels": image.channels}
    if name == "computer-icon.png":
        # Independent Blender decoder, explicitly without an RGB transfer function.
        image.colorspace_settings.name = "Non-Color"
        image.alpha_mode = "STRAIGHT"
        pixels = array("f", [0]) * len(image.pixels)
        image.pixels.foreach_get(pixels)
        assert bytes(round(v * 255) for v in pixels) == raw_icon[8:], "Blender PNG decoder disagrees with raw RGBA8"
        alpha = pixels[3::4]
        assert min(alpha) == 0 and max(alpha) == 1
        width, height = expected
        assert all(alpha[x] == alpha[(height - 1) * width + x] == 0 for x in range(width))
        assert all(alpha[y * width] == alpha[y * width + width - 1] == 0 for y in range(height))
        occupied = [i for i, a in enumerate(alpha) if a > 0.01]
        assert len(occupied) > 20000
        info.update({"transparent": True, "unclipped": True, "visible_pixels": len(occupied),
                     "pixel_bounds": [min(i % width for i in occupied), min(i // width for i in occupied),
                                      max(i % width for i in occupied), max(i // width for i in occupied)]})
    images[name] = info
images["computer-icon.rgba"] = {
    "size": [512, 512], "file_bytes": len(raw_icon), "header_bytes": 8,
    "payload_bytes": len(raw_icon) - 8, "format": "RGBA8", "alpha": "straight",
    "row_order": "bottom-to-top", "column_order": "left-to-right",
    "exact_png_samples": True, "independent_blender_decode_matches": True,
}
report_path = assets / "computer.validation.json"
report = json.loads(report_path.read_text(encoding="utf-8"))
report.update({"independent_file_validation": "PASS", "isolated_library": True,
               "native_socket_contract": {"bounds": data["bounds"], "pitch": 0.125,
                                          "anchor_to_mouth": 0.0625, "mouth_outer_radius": 0.03501129523,
                                          "mouth_inner_radius": 0.022, "data_cable_radius": 0.02,
                                          "all_16_mesh_ring_centers_match_markers": True},
               "saved_evaluated_mesh_matches_json": True, "verified_images": images,
               "triangles": count, "min_winding_normal_alignment": minimum_dot,
               "sha256": {name: hashlib.sha256((assets / name).read_bytes()).hexdigest()
                          for name in ("computer.blend", "computer.mesh.json", "computer-icon.png", "computer-preview.png", "computer-icon.rgba")}})
report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
