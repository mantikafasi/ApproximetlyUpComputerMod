"""Re-export the included AU library with Blender --background.

Only AU-owned data and exports beside this script are changed. No save_mainfile.
"""

import bpy
import json
import math
from pathlib import Path
from runpy import run_path
from mathutils import Matrix, Vector

ASSETS = Path(__file__).resolve().parent
OWNER = "approximately-up-computer-assets"
icon_rgba_bytes = run_path(str(ASSETS / "IconPixels.py"))["icon_rgba_bytes"]


def validate_library():
    # ponytail: background-only owned library; no live user-scene snapshot/restore.
    if not bpy.app.background or Path(bpy.data.filepath).resolve() != ASSETS / "computer.blend":
        raise RuntimeError("Run Blender --background with the computer.blend beside this script.")
    if len(bpy.data.scenes) != 1 or bpy.data.scenes[0].name != "AU_Computer_Preview" or bpy.data.libraries:
        raise RuntimeError("Expected the isolated AU_Computer_Preview library without linked files.")
    for blocks in (bpy.data.scenes, bpy.data.objects, bpy.data.collections, bpy.data.materials, bpy.data.worlds):
        for block in blocks:
            owned(block)
    for obj in bpy.data.objects:
        if obj.data is not None:
            owned(obj.data)


def owned(block):
    if block.get("au_owner") != OWNER:
        raise RuntimeError("Refusing to modify unowned data: " + block.name)
    return block


def material(name, rgb, metallic=0.0, roughness=0.4, emission=0.0):
    name = "AU_Mat_" + name
    existing = bpy.data.materials.get(name)
    if existing:
        return owned(existing)
    mat = bpy.data.materials.new(name)
    mat["au_owner"] = OWNER
    mat["au_rgba"] = [*rgb, 1.0]
    linear = [v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in rgb]
    mat.diffuse_color = (*linear, 1.0)
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (*linear, 1.0)
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Roughness"].default_value = roughness
    shader.inputs["Emission Color"].default_value = (*linear, 1.0)
    shader.inputs["Emission Strength"].default_value = emission
    return mat


def attach(obj, collection, parent=None):
    obj["au_owner"] = OWNER
    if obj.data:
        obj.data["au_owner"] = OWNER
    for old in tuple(obj.users_collection):
        old.objects.unlink(obj)
    collection.objects.link(obj)
    obj.parent = parent
    return obj


def box(name, size, location, mat, bevel=0.0):
    name = "AU_" + name
    # Leave a real side face; half-thickness bevels collapse after float32 baking.
    bevel = min(bevel, min(size) * 0.4)
    existing = bpy.data.objects.get(name)
    if existing:
        owned(existing)
        mod = existing.modifiers.get("AU_EdgeChamfer")
        if mod:
            mod.width = bevel
        return owned(existing)
    bpy.ops.mesh.primitive_cube_add(size=1, location=location)
    obj = bpy.context.object
    obj.name = name
    attach(obj, model, root)
    # Bake dimensions into the mesh, leaving clean object scale for bevels/export.
    for vertex in obj.data.vertices:
        vertex.co = Vector(tuple(vertex.co[i] * size[i] for i in range(3)))
    obj.data.materials.append(mat)
    if bevel:
        mod = obj.modifiers.new("AU_EdgeChamfer", "BEVEL")
        mod.width = bevel
        mod.segments = 1
    return obj


def mesh_object(name, vertices, faces, materials, indices=None):
    name = "AU_" + name
    existing = bpy.data.objects.get(name)
    if existing:
        return owned(existing)
    mesh = bpy.data.meshes.new(name + "_Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    for mat in materials:
        mesh.materials.append(mat)
    if indices:
        for face, index in zip(mesh.polygons, indices):
            face.material_index = index
    return attach(bpy.data.objects.new(name, mesh), model, root)


def text(name, body, location, size, mat):
    name = "AU_" + name
    existing = bpy.data.objects.get(name)
    if existing:
        return owned(existing)
    curve = bpy.data.curves.new(name + "_Type", "FONT")
    curve.body = body
    curve.size = size
    curve.space_character = 1.12
    curve.resolution_u = 2
    curve.fill_mode = "BOTH"
    obj = attach(bpy.data.objects.new(name, curve), model, root)
    obj.location = location
    curve.materials.append(mat)
    return obj


def octagon(width, depth, corner):
    x, y = width / 2, depth / 2
    return [(-x + corner, -y), (x - corner, -y), (x, -y + corner),
            (x, y - corner), (x - corner, y), (-x + corner, y),
            (-x, y - corner), (-x, -y + corner)]


def export_assets():
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    inverse_root = root.matrix_world.inverted()
    # Determinant +1: axis conversion preserves right-handed triangle winding.
    unity = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
    groups = {}
    for obj in sorted(model.objects, key=lambda o: o.name):
        if obj.type not in {"MESH", "FONT", "CURVE"}:
            continue
        owned(obj)
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        try:
            mesh.calc_loop_triangles()
            transform = unity @ inverse_root @ obj.matrix_world
            normal_matrix = transform.to_3x3().inverted().transposed()
            for tri in mesh.loop_triangles:
                mat = mesh.materials[tri.material_index]
                if mat.name not in groups:
                    groups[mat.name] = {
                        "name": mat.name.removeprefix("AU_Mat_"),
                        "color": list(mat["au_rgba"]),
                        "vertices": [], "normals": [], "triangles": [], "lookup": {},
                    }
                part = groups[mat.name]
                for loop_index in tri.loops:
                    vertex = mesh.vertices[mesh.loops[loop_index].vertex_index]
                    position = tuple(transform @ vertex.co)
                    normal = tuple((normal_matrix @ mesh.corner_normals[loop_index].vector).normalized())
                    key = (position, normal)
                    index = part["lookup"].get(key)
                    if index is None:
                        index = len(part["vertices"])
                        part["lookup"][key] = index
                        part["vertices"].append(position)
                        part["normals"].append(normal)
                    part["triangles"].append(index)
        finally:
            evaluated.to_mesh_clear()
    parts = list(groups.values())
    for part in parts:
        del part["lookup"]
    payload = {
        "version": 1, "name": "AU-08", "bounds": [1, 0.25, 1], "parts": parts,
        "ports": {
            role: [list(unity @ inverse_root @ bpy.data.objects.get(f"AU_Port_{role}_{i + 1:02}").matrix_world.translation)
                   for i in range(8)]
            for role in ("inputs", "outputs")
        },
    }
    triangle_count = 0
    min_alignment = 1.0
    all_vertices = []
    for part in parts:
        vertices, normals, triangles = part["vertices"], part["normals"], part["triangles"]
        assert len(vertices) == len(normals) and len(triangles) % 3 == 0
        assert all(math.isfinite(v) for rows in (vertices, normals) for row in rows for v in row)
        assert all(0 <= i < len(vertices) for i in triangles)
        assert all(abs(Vector(n).length - 1) < 1e-5 for n in normals)
        for offset in range(0, len(triangles), 3):
            a, b, c = triangles[offset:offset + 3]
            cross = (Vector(vertices[b]) - Vector(vertices[a])).cross(Vector(vertices[c]) - Vector(vertices[a]))
            assert cross.length > 1e-14, (part["name"], "degenerate", offset)
            alignment = min(cross.normalized().dot(Vector(normals[i])) for i in (a, b, c))
            assert alignment > 0.0, (part["name"], "winding", offset, alignment)
            min_alignment = min(min_alignment, alignment)
        triangle_count += len(triangles) // 3
        all_vertices.extend(vertices)
    lower = [min(v[i] for v in all_vertices) for i in range(3)]
    upper = [max(v[i] for v in all_vertices) for i in range(3)]
    bounds = [upper[i] - lower[i] for i in range(3)]
    assert all(abs(a - b) < 1e-5 for a, b in zip(bounds, payload["bounds"])), bounds
    assert all(abs(lower[i] + upper[i]) < 1e-5 for i in range(3)), (lower, upper)
    assert triangle_count < 8000, triangle_count
    for role, sign in (("inputs", -1), ("outputs", 1)):
        ports = payload["ports"][role]
        assert len(ports) == 8
        assert all(abs(p[0] - sign * 0.5) < 1e-6 and abs(p[1] + 0.0625) < 1e-6 for p in ports)
        assert all(abs(p[2] - (0.4375 - i * 0.125)) < 1e-6 for i, p in enumerate(ports))
        assert len({round(p[2], 6) for p in ports}) == 8
    validate_library()
    assert all(o.get("au_owner") == OWNER for o in scene.objects)
    assert not any(n.type == "TEX_IMAGE" for m in bpy.data.materials if m.get("au_owner") == OWNER and m.use_nodes for n in m.node_tree.nodes)
    ASSETS.joinpath("computer.mesh.json").write_text(json.dumps(payload, separators=(",", ":"), allow_nan=False), encoding="utf-8")
    report = {
        "model": "AU-08", "blender_bounds": [bounds[0], bounds[2], bounds[1]],
        "unity_bounds": bounds, "unity_min": lower, "unity_max": upper,
        "triangles": triangle_count, "export_vertices": sum(len(p["vertices"]) for p in parts),
        "parts": len(parts), "input_sockets": 8, "output_sockets": 8,
        "min_winding_normal_alignment": min_alignment,
        "finite_values": True, "unit_normals": True, "indices_in_bounds": True,
        "user_scene_unchanged": True, "external_textures": False,
        "axis_conversion": "[blender.x, blender.z, -blender.y]",
        "part_triangle_counts": {p["name"]: len(p["triangles"]) // 3 for p in parts},
    }
    ASSETS.joinpath("computer.validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    bpy.data.libraries.write(str(ASSETS / "computer.blend"), {scene}, path_remap="RELATIVE", fake_user=True, compress=True)
    if (ASSETS / "computer-icon.png").is_file():
        (ASSETS / "computer-icon.rgba").write_bytes(icon_rgba_bytes(ASSETS / "computer-icon.png"))
    return report


validate_library()
scene = bpy.data.scenes.get("AU_Computer_Preview")
if scene is None:
    scene = bpy.data.scenes.new("AU_Computer_Preview")
    scene["au_owner"] = OWNER
else:
    owned(scene)
scene.unit_settings.system = "METRIC"
scene.unit_settings.scale_length = 1.0
model = bpy.data.collections.get("AU_Computer")
if model is None:
    model = bpy.data.collections.new("AU_Computer")
    model["au_owner"] = OWNER
    scene.collection.children.link(model)
else:
    owned(model)
studio = bpy.data.collections.get("AU_Computer_Studio")
if studio is None:
    studio = bpy.data.collections.new("AU_Computer_Studio")
    studio["au_owner"] = OWNER
    scene.collection.children.link(studio)
else:
    owned(studio)
root = bpy.data.objects.get("AU_Computer_Root")
if root is None:
    root = attach(bpy.data.objects.new("AU_Computer_Root", None), model)
    root.empty_display_size = 0.08
else:
    owned(root)

case = material("Charcoal", (0.14, 0.17, 0.20), 0.62, 0.34)
lid = material("Titanium", (0.28, 0.33, 0.37), 0.7, 0.3)
dark = material("Recess", (0.025, 0.042, 0.052), 0.12, 0.5)
steel = material("Hardware", (0.48, 0.55, 0.59), 0.78, 0.27)
amber = material("InputAmber", (0.95, 0.51, 0.13), 0.35, 0.35)
blue = material("OutputBlue", (0.15, 0.48, 0.93), 0.38, 0.32)
glass = material("ScreenTeal", (0.035, 0.21, 0.24), 0.25, 0.22, 0.12)
cyan = material("DisplayCyan", (0.27, 0.92, 0.88), 0.0, 0.4, 0.6)
dim = material("DisplayMuted", (0.12, 0.42, 0.46), 0.0, 0.5, 0.25)
ink = material("Legend", (0.67, 0.76, 0.77), 0.1, 0.48)

box("Chassis", (0.92, 0.70, 0.185), (0, 0, -0.0175), case, 0.027)
box("LidGasket", (0.87, 0.665, 0.018), (0, 0, 0.053), dark, 0.022)
box("Lid", (0.852, 0.646, 0.045), (0, 0, 0.0675), lid, 0.02)
box("BottomRail", (0.84, 0.624, 0.02), (0, 0, -0.1), dark, 0.012)

for role, sign, color in (("inputs", -1, amber), ("outputs", 1, blue)):
    box(role + "_Strip", (0.035, 0.59, 0.027), (sign * 0.45, 0, 0.065), color, 0.006)
    for i in range(8):
        y = -0.4375 + i * 0.125
        name = f"Socket_{role}_{i + 1:02}"
        vertices, faces, indices = [], [], []
        # Native Port Input/Output outer radius; Data Cable Straight radius is 0.02.
        profile = ((0.040, -0.040), (0.042, -0.006), (0.03501129523, 0), (0.022, 0), (0.022, -0.032))
        for radius, z in profile:
            vertices.extend((radius * math.cos(j * math.tau / 12), radius * math.sin(j * math.tau / 12), z) for j in range(12))
        for ring in range(len(profile)):
            for j in range(12):
                faces.append((ring * 12 + j, ring * 12 + (j + 1) % 12,
                              ((ring + 1) % len(profile)) * 12 + (j + 1) % 12,
                              ((ring + 1) % len(profile)) * 12 + j))
                indices.append((0, 1, 2, 3, 3)[ring])
        socket = mesh_object(name, vertices, faces, (case, steel, color, dark), indices)
        assert len(socket.data.vertices) == len(vertices)
        for vertex, position in zip(socket.data.vertices, vertices):
            vertex.co = position
        socket.data.update()
        socket.location = (sign * 0.5, y, -0.0625)
        socket.rotation_euler[1] = sign * math.pi / 2
        contact_name = f"AU_Contact_{role}_{i + 1:02}"
        if bpy.data.objects.get(contact_name) is None:
            bpy.ops.mesh.primitive_cylinder_add(vertices=12, radius=0.014, depth=0.002, rotation=(0, sign * math.pi / 2, 0))
            contact = bpy.context.object
            contact.name = contact_name
            attach(contact, model, root)
            contact.data.materials.append(dark)
        contact = owned(bpy.data.objects.get(contact_name))
        contact.location = (sign * 0.472, y, -0.0625)
        contact.scale = (0.020 / 0.014, 0.020 / 0.014, 1)
        box(f"{role}_Index_{i + 1:02}", (0.01, 0.012, 0.001), (sign * 0.45, y, 0.079), ink)
        port_name = f"AU_Port_{role}_{i + 1:02}"
        if bpy.data.objects.get(port_name) is None:
            port = attach(bpy.data.objects.new(port_name, None), model, root)
            port.empty_display_size = 0.008
            port["unity_direction"] = [sign, 0, 0]
        owned(bpy.data.objects.get(port_name)).location = (sign * 0.5, y, -0.0625)

# An actual open frame with inner walls, not a screen decal on a solid slab.
vertices, faces = [], []
for width, depth, corner, z in ((0.60, 0.366, 0.016, 0.089), (0.60, 0.366, 0.016, 0.11),
                                (0.553, 0.319, 0.008, 0.11), (0.553, 0.319, 0.008, 0.095)):
    vertices.extend((x - 0.026, y + 0.06, z) for x, y in octagon(width, depth, corner))
for ring in range(4):
    for j in range(8):
        faces.append((ring * 8 + j, ring * 8 + (j + 1) % 8,
                      ((ring + 1) % 4) * 8 + (j + 1) % 8, ((ring + 1) % 4) * 8 + j))
mesh_object("DisplayBezel", vertices, faces, (dark,))
box("Screen", (0.553, 0.319, 0.003), (-0.026, 0.06, 0.098), glass, 0.004)
text("ScreenRun", "RUN", (-0.276, 0.173, 0.1001), 0.027, cyan)
box("ScreenRule", (0.49, 0.0015, 0.0006), (-0.026, 0.155, 0.100), dim)
for i, width in enumerate((0.026, 0.018, 0.031)):
    box(f"Status_{i}", (width, 0.008, 0.0006), (0.16 + i * 0.034, 0.19, 0.100), cyan if i == 0 else dim)
for row, widths in enumerate(((0.055, 0.105, 0.044), (0.033, 0.073, 0.124), (0.033, 0.094, 0.053))):
    x = -0.26
    for col, width in enumerate(widths):
        box(f"Code_{row}_{col}", (width, 0.006, 0.0006), (x + width / 2, 0.128 - row * 0.024, 0.100), cyan if col == 1 else dim)
        x += width + 0.012
for i in range(7):
    box(f"GraphGrid_{i}", (0.001, 0.077, 0.0004), (-0.264 + i * 0.078, -0.035, 0.100), dim)
points = [(-0.271, -0.055), (-0.18, -0.055), (-0.153, -0.003), (-0.12, -0.003),
          (-0.084, -0.06), (-0.042, -0.06), (-0.007, -0.024), (0.04, -0.024),
          (0.067, 0.009), (0.122, 0.009), (0.146, -0.035), (0.216, -0.035)]
for i, (a, b) in enumerate(zip(points, points[1:])):
    delta = Vector(b) - Vector(a)
    bar = box(f"Trace_{i:02}", (delta.length + 0.0015, 0.003, 0.0006), ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, 0.101), cyan)
    bar.rotation_euler.z = math.atan2(delta.y, delta.x)

text("Identity", "CTRL/08", (-0.312, -0.247, 0.091), 0.052, ink)
text("InputLegend", "IN", (-0.402, -0.21, 0.091), 0.022, amber)
text("OutputLegend", "OUT", (0.339, -0.21, 0.091), 0.018, blue)
box("RunIndicator", (0.027, 0.009, 0.002), (0.101, -0.225, 0.091), cyan, 0.002)
box("ArmButtonBase", (0.067, 0.05, 0.004), (0.229, -0.229, 0.091), dark, 0.006)
mesh_object("ArmArrow", [(0.217, -0.239, 0.094), (0.241, -0.229, 0.094), (0.217, -0.219, 0.094)], [(0, 1, 2)], (ink,))
for i in range(9):
    box(f"Vent_{i:02}", (0.046, 0.014, 0.002), (0.348, -0.126 + i * 0.04, 0.0905), dark, 0.003)
for x in (-0.374, 0.374):
    for y in (-0.275, 0.275):
        name = f"AU_Screw_{'L' if x < 0 else 'R'}_{'F' if y < 0 else 'B'}"
        if bpy.data.objects.get(name) is None:
            bpy.ops.mesh.primitive_cylinder_add(vertices=12, radius=0.013, depth=0.004, location=(x, y, 0.091))
            screw = bpy.context.object
            screw.name = name
            attach(screw, model, root)
            screw.data.materials.append(steel)
        slot = box(name.removeprefix("AU_") + "_Slot", (0.015, 0.003, 0.0005), (x, y, 0.0933), dark)
        slot.rotation_euler.z = 0.45
for x in (-0.30, 0.30):
    box(f"FrontNotch_{x}", (0.12, 0.001, 0.024), (x, -0.349, -0.026), dark, 0.002)

# Adjust the existing body, not the user's scene or a replacement model. Keep socket circles circular.
if root.get("au_geometry_version", 1) == 1:
    for obj in model.objects:
        if obj == root or obj.name.startswith(("AU_Socket_", "AU_Contact_", "AU_Port_")):
            continue
        obj.location.y *= 1 / 0.7
        obj.location.z *= 0.25 / 0.22
        obj.scale.y *= 1 / 0.7
        obj.scale.z *= 0.25 / 0.22
    root["au_geometry_version"] = 2
assert root["au_geometry_version"] == 2
for i, (a, b) in enumerate(zip(points, points[1:])):
    delta = Vector(b) - Vector(a)
    stretched = Vector((delta.x, delta.y / 0.7))
    bar = owned(bpy.data.objects.get(f"AU_Trace_{i:02}"))
    bar.rotation_euler.z = math.atan2(stretched.y, stretched.x)
    bar.scale.x = (stretched.length + 0.0015) / (delta.length + 0.0015)
    bar.scale.y = 1
for role, sign in (("inputs", -1), ("outputs", 1)):
    for i in range(8):
        owned(bpy.data.objects.get(f"AU_{role}_Index_{i + 1:02}")).location.y = -0.4375 + i * 0.125

ground_mat = material("Studio", (0.085, 0.11, 0.135), 0.05, 0.6)
ground = bpy.data.objects.get("AU_StudioGround")
if ground is None:
    bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, -0.112))
    ground = bpy.context.object
    ground.name = "AU_StudioGround"
    attach(ground, studio)
    ground.data.materials.append(ground_mat)
owned(ground).location.z = -0.127

camera = bpy.data.objects.get("AU_PreviewCamera")
if camera is None:
    camera = attach(bpy.data.objects.new("AU_PreviewCamera", bpy.data.cameras.new("AU_PreviewCamera_Data")), studio)
    camera.location = (-1.35, -1.65, 2.45)
    camera.rotation_euler = (Vector((0, 0, 0)) - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = 1.42
    camera.data.lens = 50
scene.camera = camera
owned(camera).data.ortho_scale = 1.8
for name, location, energy, size, color in (
    ("Key", (-1.8, -1.1, 3.4), 260, 2.4, (0.82, 0.92, 1.0)),
    ("Fill", (1.9, -0.3, 1.8), 165, 2.0, (0.60, 0.81, 1.0)),
    ("Rim", (0.3, 2.2, 2.4), 340, 1.8, (0.55, 0.92, 1.0)),
    ("Warm", (-1.8, 1.0, 0.9), 70, 1.2, (1.0, 0.58, 0.24)),
):
    if bpy.data.objects.get("AU_Light_" + name) is None:
        data = bpy.data.lights.new("AU_Light_" + name + "_Data", "AREA")
        light = attach(bpy.data.objects.new("AU_Light_" + name, data), studio)
        light.location = location
        light.rotation_euler = (-light.location).to_track_quat("-Z", "Y").to_euler()
        data.energy, data.shape, data.size, data.color = energy, "DISK", size, color
world = bpy.data.worlds.get("AU_StudioWorld")
if world is None:
    world = bpy.data.worlds.new("AU_StudioWorld")
    world["au_owner"] = OWNER
    world.use_nodes = True
    world.node_tree.nodes.get("Background").inputs["Color"].default_value = (0.12, 0.17, 0.22, 1)
    world.node_tree.nodes.get("Background").inputs["Strength"].default_value = 0.3
scene.world = owned(world)
scene.render.engine = "CYCLES"
scene.cycles.samples = 48
scene.cycles.use_denoising = True
scene.render.resolution_x = 1200
scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.view_settings.view_transform = "AgX"
scene.render.film_transparent = False
scene.render.filepath = "//computer-preview.png"

for obj in scene.objects:
    obj.select_set(False)
bpy.context.view_layer.objects.active = root
result = export_assets()
