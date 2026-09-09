# AU-08 Controller Assets

Compact game-mounted controller: chamfered metal enclosure, recessed teal
display, amber left input rail, blue right output rail, eight sockets per rail,
ventilation slots, four screws, and the CTRL/08 legend. All details are geometry
and solid-color materials; no external textures or fonts are required.

## Files

- `computer.blend`: isolated `AU_Computer_Preview` scene; append the `AU_Computer`
  collection for the model alone. `AU_Computer_Studio` contains only preview gear.
- `computer.mesh.json`: evaluated, triangulated runtime geometry, grouped into
  10 color parts, with hard-edge corner normals and all transforms baked.
- `computer-icon.png`: 512 x 512 transparent three-quarter product render.
- `computer-icon.rgba`: exact PNG channel samples for decoder-free Unity upload.
- `computer-preview.png`: 1200 x 1000 Cycles studio render.
- `computer.validation.json`: measured bounds, mesh checks, independent saved-file
  verification, icon alpha/cropping checks, and delivered asset SHA256 hashes.
- `BuildComputer.py`: background-only re-export source for the included library.
  Reuses existing owned objects; refuses interactive sessions and unowned data.
- `VerifyComputer.py`: repeatable independent verification of the delivered files.
- `IconPixels.py`: shared byte-exact PNG decoding and standalone raw-icon export.

## Re-export

Run from the project root with Blender 5.1. Commands below assume `blender` is on
`PATH`; otherwise substitute your Blender executable's full path (with `&` before
a quoted executable path in PowerShell).

```powershell
blender --background assets/computer.blend --python-exit-code 1 --python assets/BuildComputer.py
```

The included `assets/computer.blend` is the starting model, not an empty scene or
the personal `computer.blend` at the repository root. Paths resolve beside the
script. The exporter requires a single AU-owned preview scene, no linked
libraries, and owned objects, object data, collections, materials and worlds.
It checks these before modifying the model and again before writing exports.
Live user-scene editing and building from an empty scene are not supported.

This command **rewrites** the owned `assets/computer.blend`, mesh JSON and validation
report, plus the raw icon if its PNG exists. It does not render or replace either
PNG. Existing geometry-version handling and ownership checks remain in place.
Run the verifier below afterward to refresh the independent validation results.

## Integration Contract

Origin is the enclosure center. Blender dimensions are **1 x 1 x 0.25**;
Blender front is **-Y**, top is **+Z**. JSON is already in the requested Unity
axes: `[blender.x, blender.z, -blender.y]`, with bounds **[1, 0.25, 1]**.
Do not apply that axis conversion a second time.

The mesh contains **4,555 triangles** and **8,470 exported vertices**. Vertices
are split where normals differ; do not weld/recalculate normals indiscriminately.
Triangle cross products align with the supplied outward normals. No extra
winding flip is applied by the exporter.

Socket mouths have X=-0.5 for inputs and X=+0.5 for outputs, Unity Y=-0.0625.
Each array runs front-to-back: Unity Z=+0.4375 down to -0.4375, spaced by 0.125.
The outer mouth radius is 0.035011295, matching native port glyphs; opening radius
is 0.022 for the 0.02 data cable. Geometry is already in physical chassis units;
runtime CRP transforms are identity. Do not fit it down to a small math-block donor.
Native connection directions are **-X inputs / +X outputs**. Native port anchors
are inset from the mouth by 0.0625 along the direction; cable centers are a further
0.0625 outward from the mouth. See `ComputerItem` native conversion assertions.

`color` is an opaque sRGB RGBA base color, not a texture reference. Blender's
metallic/roughness/emission preview settings are intentionally not added to the
specified JSON schema. Runtime materials can use the supplied base colors.
The screen display is static visual geometry, not a script editor or live UI.

## Raw Icon Contract

`computer-icon.rgba` is **1,048,584 bytes**, with no padding or compression:

- Offset 0: little-endian uint32 width, 512.
- Offset 4: little-endian uint32 height, 512.
- Offset 8: exactly 1,048,576 RGBA8 bytes, **straight (not premultiplied) alpha**.
- Rows run **bottom-to-top**; pixels within each row run **left-to-right**.
- RGB and alpha are the exact delivered PNG samples. No linear/sRGB conversion,
  gamma correction, or alpha multiplication/division is performed.

For Unity, use `TextureFormat.RGBA32` with sRGB color sampling (`linear=false`).
Pass only the payload at offset 8, size 1,048,576, to
`Texture2D.LoadRawTextureData(IntPtr, size)`; do not upload the header or flip again.
Runtime loading is integration-owned; no C# or game files are changed here.

After rendering/replacing the PNG, regenerate only the raw asset without
constructing, changing, or saving any scene:

```powershell
blender --background --python-exit-code 1 --python assets/IconPixels.py
```

`BuildComputer.py` also exports the raw icon whenever the PNG already exists.
The verifier compares every raw channel byte against the decoded PNG and against
Blender's independent decoder in Non-Color/STRAIGHT mode, and checks the header,
exact byte count, row order, transparency, and SHA256.

## Repeat Verification

From the project root, using Blender on `PATH` or its full executable path:

```powershell
blender --background assets/computer.blend --python-exit-code 1 --python assets/VerifyComputer.py
```

The verifier opens only the exported library in a separate process and **rewrites
only `assets/computer.validation.json`**. It does not save the blend file or change
the mesh JSON, PNGs or raw icon. It checks JSON structure, finite values, integer index bounds, unit
normals, nondegenerate triangles, winding, socket positions, dimensions, exact
evaluated-geometry correspondence, library isolation, and icon transparency.
No C#, game files, saves or live Blender sessions are involved. The exported
library contains only the authored AU-08 model and its preview setup, not the
personal Blender files at the repository root.
