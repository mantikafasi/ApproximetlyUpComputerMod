using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ApproximatelyUp.ComputerMod;

internal static class ComputerItem
{
    private const string AuthoringName = "SC_ProgrammableComputer";
    private const float PortPitch = 0.125f, PortFaceOffset = 0.0625f;
    private const float SocketOuterRadius = 0.03501129523f, SocketInnerRadius = 0.022f;
    private static readonly float3 DeviceBounds = new(1, 0.25f, 1);
    private static readonly float3 InteractionBounds = new(1.01f, 0.26f, 1.01f);
    private static ManualLogSource? _log;
    private static string _assetDirectory = "";
    private static int _mainThread;
    private static bool _installed, _registered, _published;
    private static EPC_SCLabel? _clone;
    private static GameObject? _holder;
    private static World? _world;
    private static Entity _prefabEntity;
    private static readonly List<Object> Assets = new();
    private static readonly List<EPC_Renderer> Renderers = new();
    private static Vector3[] _mouths = Array.Empty<Vector3>();

    internal static bool IsRegistered => _registered && _world is not null && _world.IsCreated;
    internal static ulong PrefabId { get; private set; }

    // Install during plugin Load, after the caller's supported-build check, before Core.Initialize.
    internal static void Install(Harmony harmony, ManualLogSource log, string assetDirectory)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(assetDirectory);
        if (_installed) return;
        _log = log;
        _assetDirectory = Path.GetFullPath(assetDirectory);
        _mainThread = Environment.CurrentManagedThreadId;
        try
        {
            var core = Core._singleton;
            if (core != null && core._componentsMap != null && core._componentsMap.Count != 0)
                throw new InvalidOperationException("Core is already initialized; restart to register the computer safely.");
            if (EPC_SCLabel.Blueprint.ClassID != 35)
                throw new InvalidOperationException("Unsupported native label blueprint class.");
            harmony.Patch(AccessTools.Method(typeof(Core), nameof(Core.Initialize)),
                prefix: new HarmonyMethod(typeof(ComputerItem), nameof(BeforeInitialize)),
                postfix: new HarmonyMethod(typeof(ComputerItem), nameof(AfterInitialize)));
            _installed = true;
            harmony.Patch(AccessTools.Method(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.GetName)),
                prefix: new HarmonyMethod(typeof(ComputerItem), nameof(GetName)));
            harmony.Patch(AccessTools.Method(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.GetDescription)),
                prefix: new HarmonyMethod(typeof(ComputerItem), nameof(GetDescription)));
        }
        catch (Exception ex) { log.LogError($"Computer item installation: {ex.Message}"); }
    }

    private static bool GetName(EPC_SpaceshipComponent __instance, ref string __result)
    {
        if (GraphScreen.IsAuthoring(__instance)) { __result = GraphScreen.Name; return false; }
        if (_clone == null || __instance.Pointer != _clone.Pointer) return true;
        __result = "AU-08 Lua Computer";
        return false;
    }

    private static bool GetDescription(EPC_SpaceshipComponent __instance, ref string __result)
    {
        if (GraphScreen.IsAuthoring(__instance)) { __result = "Four-channel signal history or Lua-programmed plots. Press E for graph view and settings."; return false; }
        if (_clone == null || __instance.Pointer != _clone.Pointer) return true;
        __result = "Programmable computer with 8 numeric inputs and 8 outputs. Press E to edit. Programs run on the host in game mode and stop on exit.";
        return false;
    }

    private static void BeforeInitialize(Core __instance)
    {
        _registered = false;
        _world = null;
        _prefabEntity = default;
        try
        {
            MainThread();
            var originals = __instance._spaceshipComponents;
            if (originals == null || originals.Length is < 1 or > 4096)
                throw new InvalidOperationException("Missing or oversized native authoring array.");
            if (_clone != null)
            {
                foreach (var item in originals)
                    if (item != null && item.Pointer == _clone.Pointer)
                    {
                        _clone.gameObject.SetActive(true);
                        _holder!.SetActive(true);
                        GraphScreen.Prepare(__instance, _clone, _assetDirectory, _log!);
                        return;
                    }
                throw new InvalidOperationException("Computer authoring array changed; refusing late re-registration.");
            }

            // Validate the complete local asset before allocating any Unity objects or publishing the array.
            using var json = JsonDocument.Parse(ReadBounded(Path.Combine(_assetDirectory, "computer.mesh.json"), 4 * 1024 * 1024),
                new JsonDocumentOptions { MaxDepth = 16 });
            var model = ReadModel(json.RootElement);
            EPC_SCLabel? labelTemplate = null;
            EPC_SCSignalProcessor? chassis = null;
            int areaCount = 0;
            ulong id = new SCPrefab(AuthoringName)._prefab;
            if (id == 0) throw new InvalidOperationException("Native prefab hash is zero.");
            foreach (var item in originals)
            {
                if (item == null) continue;
                if (new SCPrefab(item)._prefab == id)
                    throw new InvalidOperationException("Computer prefab name/hash already belongs to another authoring object.");
                var label = item.TryCast<EPC_SCLabel>();
                if (labelTemplate == null && label != null && label.enabled && label._actionableLabel != null &&
                    label._actionableLabel._rendererText3D != null &&
                    OwnedTransform(label._actionableLabel.transform, label.transform) &&
                    OwnedTransform(label._actionableLabel._rendererText3D.transform, label.transform)) labelTemplate = label;
                var processor = item.TryCast<EPC_SCSignalProcessor>();
                if (processor == null || !processor.enabled || processor._scGroup != SpaceshipComponentsGroup.MathBlock ||
                    processor._type != SCTypeSignalProcessor.Type.AdditionArray) continue;
                if (!TryGetSupport(processor, out _, out int candidateAreas)) continue;
                var visuals = processor.gameObject.GetComponentsInChildren(Il2CppType.Of<EPC_Renderer>(), true);
                if (visuals.Length is < 1 or > 64 ||
                    processor.gameObject.GetComponentsInChildren(Il2CppType.Of<EPC_SpaceshipComponent>(), true).Length != 1) continue;
                chassis = processor;
                areaCount = candidateAreas;
            }
            if (labelTemplate == null) throw new InvalidOperationException("No configured native label storage donor found.");
            if (chassis == null) throw new InvalidOperationException("No native MathBlock signal-processor chassis with owned colliders and valid bottom Normal joint polygons found.");
            var sourceBounds = chassis._bounds;
            if (sourceBounds.x != 0.5f || sourceBounds.y != 0.125f || sourceBounds.z != 0.25f || areaCount != 6)
                throw new InvalidOperationException("AdditionArray chassis geometry changed; refusing unknown support/collider layout.");
            ValidateSocketRings(model.Parts.Find(p => p.Name == "InputAmber")!.Vertices, model.Ports, 0);
            ValidateSocketRings(model.Parts.Find(p => p.Name == "OutputBlue")!.Vertices, model.Ports, 8);
            MeasureNativePort(__instance._portMeshInput, "input");
            MeasureNativePort(__instance._portMeshOutput, "output");

            _holder = new GameObject("AU08_AuthoringOnly");
            _holder.SetActive(false);
            Object.DontDestroyOnLoad(_holder);
            var root = Object.Instantiate((Object)chassis.gameObject, _holder.transform, false).Cast<GameObject>();
            root.SetActive(false);
            root.name = AuthoringName;
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            var processorClone = root.GetComponent(Il2CppType.Of<EPC_SCSignalProcessor>()).Cast<EPC_SCSignalProcessor>();
            processorClone.enabled = false;
            _clone = root.AddComponent(Il2CppType.Of<EPC_SCLabel>()).Cast<EPC_SCLabel>();
            // Complete native base-field whitelist: recon/registration/evidence.txt, offsets 0x20..0xD0.
            // References come from the OWNED math clone; geometry is resized below, never on the donor.
            _clone._mirroredVersion = processorClone._mirroredVersion != null ? _clone : null!;
            _clone._bounds = DeviceBounds;
            _clone._mass = processorClone._mass;
            _clone._oceanFloatingSetup = processorClone._oceanFloatingSetup;
            _clone._maxTemperature = processorClone._maxTemperature;
            _clone._colliders = processorClone._colliders;
            _clone._scGroup = processorClone._scGroup;
            _clone._scSecondaryGroup = processorClone._scSecondaryGroup;
            _clone._availableAmount = 10;
            _clone._inventoryDependency = new Il2CppReferenceArray<EPC_SpaceshipComponent>(0);
            _clone._boundsCollider = processorClone._boundsCollider;
            _clone._defaultPaintableColor = processorClone._defaultPaintableColor;
            _clone._paintableRenderers = processorClone._paintableRenderers;
            _clone._collisionSoundMaterial = processorClone._collisionSoundMaterial;
            _clone._soundObstacleMin = processorClone._soundObstacleMin;
            _clone._soundObstacleMax = processorClone._soundObstacleMax;
            _clone._electricPorts = processorClone._electricPorts;
            _clone._iconTexture2D = processorClone._iconTexture2D;
            _clone._uiPreviewBounds = DeviceBounds;
            _clone._categories = processorClone._categories;
            _clone._customProperties = processorClone._customProperties;
            _clone._propertiesMaterial = processorClone._propertiesMaterial;
            // Never inherit a cached blob built for the donor's old dimensions (or dispose that shared blob).
            _clone._colliderBAR = new BlobAssetReference<Unity.Physics.Collider>();
            Object.DestroyImmediate(processorClone);
            _clone.enabled = true;
            if (_clone._colliders.Length != 1 || _clone._colliders[0].TryCast<DOTSColliderBox>() is not { } box ||
                !OwnedTransform(box.transform, root.transform) || _clone._boundsCollider != null)
                throw new InvalidOperationException("Expected one owned AdditionArray box and native generated bounds collider.");
            box._offset = new float3(0, 0, 0);
            box._rotation = new float3(0, 0, 0);
            box._scale = DeviceBounds;
            box._bevelRadius = 0.02f;
            foreach (var component in root.GetComponentsInChildren(Il2CppType.Of<SpaceshipComponentAreaPoly>(), true))
            {
                var polygon = component.Cast<SpaceshipComponentAreaPoly>();
                var vertices = polygon._vertices;
                var resized = new float3[vertices.Length];
                // Native C1A110..C1A3A5 reads these in chassis space, ignoring the child Unity transform.
                for (int i = 0; i < resized.Length; i++)
                    resized[i] = new float3(vertices[i].x * 2, vertices[i].y * 2, vertices[i].z * 4);
                polygon._vertices = new Il2CppStructArray<float3>(resized);
            }

            // Clone the configured label hierarchy for its remapped actionable/text references, NOT its chassis.
            var storage = Object.Instantiate((Object)labelTemplate.gameObject, root.transform, false).Cast<GameObject>();
            storage.name = "AU08_ProgramStorage";
            var storageLabel = storage.GetComponent(Il2CppType.Of<EPC_SCLabel>()).Cast<EPC_SCLabel>();
            var labelAuthoring = storageLabel._actionableLabel;
            var text = labelAuthoring?._rendererText3D;
            if (labelAuthoring == null || text == null || !OwnedTransform(labelAuthoring.transform, root.transform) ||
                !OwnedTransform(text.transform, root.transform))
                throw new InvalidOperationException("Cloned label references were not remapped into the clone.");
            var hitTemplate = labelAuthoring.GetComponent(Il2CppType.Of<EPC_Collider>())?.Cast<EPC_Collider>();
            if (hitTemplate == null || hitTemplate._collisionFilterType != EPC_Collider.CollisionFilterType.Actionable ||
                hitTemplate._materialType != EPC_Collider.MaterialType.NoCollisions)
                throw new InvalidOperationException("Native label donor lacks the expected non-physical actionable collider convention.");
            var hitMaterial = hitTemplate._materialType;
            foreach (var type in new[] { Il2CppType.Of<EPC_SpaceshipComponent>(), Il2CppType.Of<SpaceshipComponentAreaPoly>(),
                Il2CppType.Of<EPC_Collider>(), Il2CppType.Of<DOTSCollider>() })
                foreach (var component in storage.GetComponentsInChildren(type, true)) Object.DestroyImmediate(component);
            if (storage.GetComponent(Il2CppType.Of<EPC_Transform>()) == null)
            {
                var storageTransform = storage.AddComponent(Il2CppType.Of<EPC_Transform>()).Cast<EPC_Transform>();
                storageTransform._position = new float3(0, 0, 0);
                storageTransform._rotation = new float3(0, 0, 0);
                storageTransform._scale = new float3(1, 1, 1);
            }
            _clone._actionableLabel = labelAuthoring;
            labelAuthoring.enabled = true;
            labelAuthoring._inputType = EPC_Actionable_Label.InputType.Text;
            labelAuthoring._maxLength = 16;
            text.enabled = true;
            text._text = "";
            // Class 35 FromBinary unconditionally uses this renderer's CRPText3DString buffer. Keep it.
            text._textScale = 0;
            for (var t = labelAuthoring.transform; t != root.transform; t = t.parent) t.gameObject.SetActive(true);
            for (var t = text.transform; t != root.transform; t = t.parent) t.gameObject.SetActive(true);

            var oldRenderers = root.GetComponentsInChildren(Il2CppType.Of<EPC_Renderer>(), true);
            if (oldRenderers.Length is < 1 or > 64 || __instance._materialLit == null)
                throw new InvalidOperationException("Native chassis CRP renderer/material conventions unavailable.");
            var rendererTemplate = oldRenderers[0].Cast<EPC_Renderer>();
            var layer = rendererTemplate._layer;
            var specular = rendererTemplate._specular;
            bool hasHeat = rendererTemplate._hasHeat;
            _clone._paintableRenderers = new Il2CppReferenceArray<EntityPrefabComponent>(0);
            // Remove only our clone's visual components, retaining authoring transforms and all structural colliders.
            foreach (var component in oldRenderers)
            {
                var renderer = component.Cast<EPC_Renderer>();
                var go = renderer.gameObject;
                var position = renderer._position;
                var rotation = renderer._rotation;
                var size = renderer._scale;
                Object.DestroyImmediate(renderer);
                // The spaceship initializer supplies the root's LocalTransform/LocalToWorld itself.
                if (go != root && go.GetComponent(Il2CppType.Of<EPC_Transform>()) == null)
                {
                    var transform = go.AddComponent(Il2CppType.Of<EPC_Transform>()).Cast<EPC_Transform>();
                    transform._position = position;
                    transform._rotation = rotation;
                    transform._scale = size;
                }
            }
            // Keep the storage/text hierarchy and entity ownership, but place the entire hit-volume path at
            // the chassis origin. EPC_Transform, not the Unity Transform, drives native conversion.
            for (var t = labelAuthoring.transform; t != root.transform; t = t.parent)
            {
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
                var transforms = t.GetComponents(Il2CppType.Of<EPC_Transform>());
                if (transforms.Length > 1) throw new InvalidOperationException("Ambiguous label hit-volume transform authoring.");
                var transform = transforms.Length == 1 ? transforms[0].Cast<EPC_Transform>() :
                    t.gameObject.AddComponent(Il2CppType.Of<EPC_Transform>()).Cast<EPC_Transform>();
                transform._position = new float3(0, 0, 0);
                transform._rotation = new float3(0, 0, 0);
                transform._scale = new float3(1, 1, 1);
                transform.enabled = true;
            }
            // A68310 gathers DOTSCollider on this SAME GameObject. Never add this box to structural _colliders.
            var hitBox = labelAuthoring.gameObject.AddComponent(Il2CppType.Of<DOTSColliderBox>()).Cast<DOTSColliderBox>();
            hitBox._offset = new float3(0, 0, 0);
            hitBox._rotation = new float3(0, 0, 0);
            hitBox._scale = InteractionBounds;
            hitBox._bevelRadius = 0;
            var hitCollider = labelAuthoring.gameObject.AddComponent(Il2CppType.Of<EPC_Collider>()).Cast<EPC_Collider>();
            hitCollider._collisionFilterType = EPC_Collider.CollisionFilterType.Actionable;
            hitCollider._materialType = hitMaterial;
            hitCollider._colliderBAR = new BlobAssetReference<Unity.Physics.Collider>();
            if (_clone._colliders.Length != 1 || _clone._colliders[0].Pointer != box.Pointer)
                throw new InvalidOperationException("Interaction volume changed the structural collider list.");
            foreach (var part in model.Parts)
            {
                var mesh = new Mesh { name = "AU08_" + part.Name };
                Assets.Add(mesh);
                // Exported geometry is already in physical chassis units. CRP transform stays identity.
                mesh.vertices = new Il2CppStructArray<Vector3>(part.Vertices);
                mesh.normals = new Il2CppStructArray<Vector3>(part.Normals);
                mesh.triangles = new Il2CppStructArray<int>(part.Triangles);
                mesh.RecalculateBounds();
                var material = Object.Instantiate((Object)__instance._materialLit).Cast<Material>();
                Assets.Add(material);
                material.name = "AU08_" + part.Name;
                var go = new GameObject("AU08_" + part.Name);
                go.transform.SetParent(root.transform, false);
                var renderer = go.AddComponent(Il2CppType.Of<EPC_Renderer>()).Cast<EPC_Renderer>();
                renderer._position = new float3(0, 0, 0);
                renderer._rotation = new float3(0, 0, 0);
                renderer._scale = new float3(1, 1, 1);
                renderer._mesh = mesh;
                renderer._submeshID = 0;
                renderer._material = material;
                renderer._layer = layer;
                renderer._color = part.Color;
                renderer._specular = specular;
                renderer._hasHeat = hasHeat;
                renderer._spaceshipColor = (SpaceshipComponentColor)0;
                Renderers.Add(renderer);
            }
            var ports = new EPC_SpaceshipComponent.ElectricPortSetup[16];
            _mouths = model.Ports;
            for (int i = 0; i < ports.Length; i++)
            {
                var p = model.Ports[i];
                // C1B850..C1B879 copies this anchor verbatim. Native glyph/cable face is anchor + forward/16,
                // NOT the anchor itself: Port Input/Output meshes span local Z=[0.0425,0.0625].
                float direction = i < 8 ? -1 : 1;
                ports[i] = new EPC_SpaceshipComponent.ElectricPortSetup {
                    _position = new float3(p.x - direction * PortFaceOffset, p.y, p.z),
                    _direction = i < 8 ? EPC_SpaceshipComponent.ElectricPortSetup.Direction.XMinus : EPC_SpaceshipComponent.ElectricPortSetup.Direction.XPlus,
                    _type = i < 8 ? SpaceshipPortType.DataInput : SpaceshipPortType.DataOutput
                };
            }
            _clone._electricPorts = new Il2CppStructArray<EPC_SpaceshipComponent.ElectricPortSetup>(ports);
            _clone._categories = SpaceshipComponentUICategory.Electronics | SpaceshipComponentUICategory.Math;
            _clone._scGroup = SpaceshipComponentsGroup.MathBlock;
            if (_clone._mirroredVersion != null) _clone._mirroredVersion = _clone;
            TryLoadIcon(_clone);
            if (root.GetComponentsInChildren(Il2CppType.Of<EPC_SpaceshipComponent>(), true).Length != 1 ||
                root.GetComponentsInChildren(Il2CppType.Of<EPC_SCSignalProcessor>(), true).Length != 0 ||
                !TryGetSupport(_clone, out var clonedContact, out int clonedAreas) || clonedAreas != areaCount ||
                clonedContact.x != 0 || clonedContact.y != -DeviceBounds.y / 2 || clonedContact.z != 0)
                throw new InvalidOperationException("Hybrid chassis lost its native support metadata or retained a second spaceship/processor authoring component.");
            // C1A05A passes includeInactive=false when gathering joint polygons. CRP's activeSelf traversal is different.
            // Activate our authoring hierarchy ONLY for synchronous conversion; AfterInitialize hides it again.
            foreach (var renderer in root.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true)) renderer.Cast<Renderer>().enabled = false;
            root.SetActive(true);
            _holder.SetActive(true);
            if (root.GetComponentsInChildren(Il2CppType.Of<SpaceshipComponentAreaPoly>(), false).Length != areaCount)
                throw new InvalidOperationException("Native joint conversion cannot see every retained chassis support polygon.");
            var graphScreen = GraphScreen.Prepare(__instance, _clone, _assetDirectory, _log!);
            var appended = new Il2CppReferenceArray<EPC_SpaceshipComponent>(originals.Length + 2);
            for (int i = 0; i < originals.Length; i++) appended[i] = originals[i];
            appended[originals.Length] = _clone;
            appended[originals.Length + 1] = graphScreen;
            PrefabId = id;
            _log!.LogInfo($"Computer authoring prepared: chassis={chassis.name}, storage={labelTemplate.name}, bounds=({DeviceBounds.x},{DeviceBounds.y},{DeviceBounds.z}), modelScale=1, contact=(0,-0.125,0), colliderSize=({box._scale.x},{box._scale.y},{box._scale.z}), activeJointPolygons={areaCount}, pitch={PortPitch}, anchorToMouth={PortFaceOffset}, ports=8/8, parts={model.Parts.Count}, prefab={id:X16}, class=35, available={_clone._availableAmount}.");
            // This is the only registration write. Native Core.Initialize builds conversion/maps/availability/headers.
            __instance._spaceshipComponents = appended;
            _published = true;
        }
        catch (Exception ex)
        {
            if (_holder != null) _holder.SetActive(false);
            _log?.LogError($"Computer item registration refused: {ex.Message}");
            _log?.LogError(ex);
            // Pre-publication objects have never been converted. Never dispose native worlds/maps to recover.
            if (!_published && !ContainsClone(__instance))
            {
                if (_holder != null) Object.Destroy(_holder);
                foreach (var asset in Assets) if (asset != null) Object.Destroy(asset);
                Assets.Clear();
                Renderers.Clear();
                _mouths = Array.Empty<Vector3>();
                _holder = null;
                _clone = null;
                PrefabId = 0;
            }
        }
    }

    private static bool ContainsClone(Core core)
    {
        if (_clone == null || core._spaceshipComponents == null) return false;
        foreach (var item in core._spaceshipComponents)
            if (item != null && item.Pointer == _clone.Pointer) return true;
        return false;
    }

    private static string V(float3 p) => $"({p.x:R},{p.y:R},{p.z:R})";

    private static void MeasureNativePort(Mesh mesh, string role)
    {
        if (mesh == null) throw new InvalidOperationException($"Missing native {role} glyph mesh.");
        var bounds = mesh.bounds;
        var low = bounds.min;
        var high = bounds.max;
        float radius = MathF.Max(MathF.Max(MathF.Abs(low.x), MathF.Abs(high.x)),
            MathF.Max(MathF.Abs(low.y), MathF.Abs(high.y)));
        _log!.LogInfo($"AU08 native {role} glyph: mesh={mesh.name}, min={V(low)}, max={V(high)}, radialExtent={radius:R}, socketOuter={SocketOuterRadius:R}, socketInner={SocketInnerRadius:R}, dataCableRadius=0.02, pitch={PortPitch:R}.");
        if (!FinitePositive(radius) || MathF.Abs(radius - SocketOuterRadius) > 0.00001f ||
            MathF.Abs(low.x + high.x) > 0.00001f || MathF.Abs(low.y + high.y) > 0.00001f ||
            MathF.Abs(low.z - 0.0425f) > 0.00001f || MathF.Abs(high.z - PortFaceOffset) > 0.00001f)
            throw new InvalidOperationException("Native glyph dimensions changed; socket/cable coordinate contract is unverified.");
    }

    private static void ValidateSocketRings(IEnumerable<Vector3> vertices, Vector3[] mouths, int first)
    {
        // Measure the colored annulus itself, both before upload and through CRPRendererData's actual Mesh.
        var points = new List<Vector3>(vertices);
        for (int i = first; i < first + 8; i++)
        {
            var mouth = mouths[i];
            var face = new HashSet<(float X, float Y, float Z)>();
            foreach (var p in points)
                if (MathF.Abs(p.x - mouth.x) < 0.00001f && MathF.Abs(p.y - mouth.y) < 0.05f && MathF.Abs(p.z - mouth.z) < 0.05f)
                    face.Add((p.x, p.y, p.z));
            var center = Vector3.zero;
            int outer = 0, inner = 0;
            foreach (var p in face)
            {
                center += new Vector3(p.X, p.Y, p.Z);
                float radius = MathF.Sqrt((p.Y - mouth.y) * (p.Y - mouth.y) + (p.Z - mouth.z) * (p.Z - mouth.z));
                if (MathF.Abs(radius - SocketOuterRadius) < 0.00001f) outer++;
                if (MathF.Abs(radius - SocketInnerRadius) < 0.00001f) inner++;
            }
            if (face.Count != 24 || outer != 12 || inner != 12 || (center / 24 - mouth).sqrMagnitude > 1e-10f)
                throw new InvalidOperationException($"Mesh socket {i} mouth/radius mismatch: faceVertices={face.Count}, outer={outer}, inner={inner}.");
        }
    }

    private static void ValidatePortGeometry(EntityManager manager, Entity root, bool log)
    {
        var rootTransform = manager.GetComponentData<LocalTransform>(root);
        if (log) _log!.LogInfo($"AU08 root: entity={root.Index}:{root.Version}, position={V(rootTransform.Position)}, scale={rootTransform.Scale:R}.");
        if (rootTransform.Scale != 1 || manager.HasComponent(root, ComponentType.ReadOnly(Il2CppType.Of<PostTransformMatrix>())))
            throw new InvalidOperationException("Computer root scales native cable/glyph dimensions.");
        var ports = manager.GetBuffer<SpaceshipElectricPortRef>(root, true);
        if (ports.Length != 16 || _mouths.Length != 16) throw new InvalidOperationException("Expected 16 native ports and socket mouths.");
        for (int i = 0; i < 16; i++)
        {
            var child = ports[i]._portEntity;
            var port = manager.GetComponentData<SpaceshipElectricPort>(child);
            var local = manager.GetComponentData<LocalTransform>(child);
            var parent = manager.GetComponentData<Parent>(child).Value;
            var mouth = local.TransformPoint(new float3(0, 0, PortFaceOffset));
            var forward = local.Forward();
            var expected = _mouths[i];
            float direction = i < 8 ? -1 : 1;
            var anchor = new Vector3(expected.x - direction * PortFaceOffset, expected.y, expected.z);
            float error = ((Vector3)mouth - expected).magnitude;
            if (log) _log!.LogInfo($"AU08 port {i}: entity={child.Index}:{child.Version}, parent={parent.Index}:{parent.Version}, anchor={V(local.Position)}, forward={V(forward)}, scale={local.Scale:R}, nativeMouth={V(mouth)}, modelMouth={V(expected)}, error={error:R}.");
            if (parent != root || local.Scale != 1 ||
                manager.HasComponent(child, ComponentType.ReadOnly(Il2CppType.Of<PostTransformMatrix>())) ||
                port._index != i || port._setupType != (i < 8 ? SpaceshipPortType.DataInput : SpaceshipPortType.DataOutput) ||
                !float.IsFinite(error) || error > 0.00001f || ((Vector3)local.Position - anchor).sqrMagnitude > 1e-10f ||
                ((Vector3)forward - new Vector3(direction, 0, 0)).sqrMagnitude > 1e-10f)
                throw new InvalidOperationException($"Converted port {i} does not meet its rendered socket mouth (error={error:R}).");
        }
    }

    private static void ValidatePortNames(EntityManager manager, Entity root, bool initialize = false)
    {
        MainThread();
        if (_world == null || !_world.IsCreated || manager.m_EntityDataAccess == IntPtr.Zero ||
            _world.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess || !manager.Exists(root) ||
            !manager.HasComponent(root, ComponentType.ReadOnly<SCPrefab>()) ||
            manager.GetComponentData<SCPrefab>(root)._prefab != PrefabId ||
            !manager.HasComponent(root, ComponentType.ReadOnly<SpaceshipElectricPortRef>()) ||
            !manager.HasComponent(root, ComponentType.ReadOnly<LinkedEntityGroup>()) ||
            (initialize && !manager.HasComponent(root, ComponentType.ReadOnly(Il2CppType.Of<Prefab>()))))
            throw new InvalidOperationException("Port names require the owned computer; initialization is prefab-only.");
        var ports = manager.GetBuffer<SpaceshipElectricPortRef>(root, true);
        var links = manager.GetBuffer<LinkedEntityGroup>(root, true);
        if (ports.Length != 16 || links.Length is < 17 or > 256 || links[0].Value != root)
            throw new InvalidOperationException("Invalid computer port-name layout.");
        for (int i = 0; i < ports.Length; i++)
        {
            var child = ports[i]._portEntity;
            if (child == root || !manager.Exists(child) ||
                !manager.HasComponent(child, ComponentType.ReadOnly<BelongingSC>()) ||
                !manager.HasComponent(child, ComponentType.ReadOnly<SpaceshipElectricPort>()) ||
                !manager.HasComponent(child, ComponentType.ReadOnly<PortInfoData>()))
                throw new InvalidOperationException($"Computer port {i} lacks native tooltip data or ownership.");
            var owner = manager.GetComponentData<BelongingSC>(child);
            var port = manager.GetComponentData<SpaceshipElectricPort>(child);
            if (owner._sc != root || owner._linkedEntityGroupIndex < 1 || owner._linkedEntityGroupIndex >= links.Length ||
                links[owner._linkedEntityGroupIndex].Value != child || port._index != i ||
                port._setupType != (i < 8 ? SpaceshipPortType.DataInput : SpaceshipPortType.DataOutput))
                throw new InvalidOperationException($"Computer port {i} has foreign ownership or incorrect role/index.");
            string expected = (i < 8 ? "Input " : "Output ") + (i % 8 + 1);
            if (initialize)
            {
                // C1BBA9 registers resolved text, not a localization key. IDs are session-local,
                // sequential, and inherited by native clones. Get/Set AOT: recon/port-names/fields-aot.txt.
                var id = Core.RegisterStringAsID(expected);
                if (id == CoreStringID.Invalid || Core.GetStringByID(id) != expected)
                    throw new InvalidOperationException($"Native tooltip string registration failed for {expected}.");
                manager.SetComponentData(child, new PortInfoData { _stringId = id });
            }
            var stored = manager.GetComponentData<PortInfoData>(child)._stringId;
            if (stored == CoreStringID.Invalid || Core.GetStringByID(stored) != expected)
                throw new InvalidOperationException($"Native tooltip for computer port {i} does not resolve to {expected}.");
        }
        _log?.LogInfo($"AU08 PORT NAMES PASS: root={root.Index}:{root.Version}, initialized={initialize}; native GetStringByID resolves Input 1..8 / Output 1..8.");
    }

    private static unsafe void ValidateInteractionGeometry(EntityManager manager, Entity root, bool prefab, bool log)
    {
        var label = ValidateLabel(manager, root, prefab);
        if (!manager.HasComponent(label, ComponentType.ReadOnly(Il2CppType.Of<Unity.Physics.PhysicsCollider>())) ||
            !manager.HasComponent(label, ComponentType.ReadOnly(Il2CppType.Of<ActionableAssignOnKeyActionDown>())) ||
            !manager.HasComponent(label, ComponentType.ReadOnly(Il2CppType.Of<Unity.Physics.PhysicsWorldIndex>())) ||
            manager.HasComponent(label, ComponentType.ReadOnly(Il2CppType.Of<Unity.Physics.PhysicsMass>())) ||
            manager.HasComponent(label, ComponentType.ReadOnly(Il2CppType.Of<Unity.Physics.PhysicsVelocity>())))
            throw new InvalidOperationException("Label lacks its non-physical native actionable hit volume or assignment tag.");
        var links = manager.GetBuffer<LinkedEntityGroup>(root, true);
        var ancestor = label;
        int depth = 0;
        while (ancestor != root)
        {
            bool owned = false;
            for (int i = 1; i < links.Length; i++) if (links[i].Value == ancestor) owned = true;
            if (!owned || ++depth > 64 || !manager.Exists(ancestor))
                throw new InvalidOperationException("Actionable transform chain escaped its owned linked group.");
            var local = manager.GetComponentData<LocalTransform>(ancestor);
            if (local.Position.x != 0 || local.Position.y != 0 || local.Position.z != 0 || local.Scale != 1 ||
                local.Rotation.value.x != 0 || local.Rotation.value.y != 0 || local.Rotation.value.z != 0 ||
                MathF.Abs(local.Rotation.value.w) != 1 ||
                manager.HasComponent(ancestor, ComponentType.ReadOnly(Il2CppType.Of<PostTransformMatrix>())))
                throw new InvalidOperationException($"Actionable ancestor {ancestor.Index}:{ancestor.Version} is not identity in chassis space.");
            ancestor = manager.GetComponentData<Parent>(ancestor).Value;
        }
        var physics = manager.GetComponentData<Unity.Physics.PhysicsCollider>(label);
        if (!physics.IsValid || physics.ColliderPtr == null)
            throw new InvalidOperationException("Native label collider blob is missing.");
        // Call on the native blob pointer: copying the small Collider header would truncate its box payload.
        var collider = physics.ColliderPtr;
        if (collider->Type != Unity.Physics.ColliderType.Box)
            throw new InvalidOperationException("Native label hit volume is not the expected box.");
        var filter = collider->GetCollisionFilter();
        var response = collider->GetCollisionResponse();
        var bounds = collider->CalculateAabb();
        var expected = (Vector3)InteractionBounds * 0.5f;
        if (filter.BelongsTo != 0x80 || filter.CollidesWith != 0x80 || filter.GroupIndex != 0 ||
            response != Unity.Physics.CollisionResponsePolicy.None ||
            !float.IsFinite(bounds.Min.x + bounds.Min.y + bounds.Min.z + bounds.Max.x + bounds.Max.y + bounds.Max.z) ||
            ((Vector3)bounds.Min + expected).sqrMagnitude > 1e-10f ||
            ((Vector3)bounds.Max - expected).sqrMagnitude > 1e-10f)
            throw new InvalidOperationException($"Native actionable filter/material/bounds mismatch: belongs=0x{filter.BelongsTo:X}, collides=0x{filter.CollidesWith:X}, response={response}, min={V(bounds.Min)}, max={V(bounds.Max)}.");
        if (log) _log!.LogInfo($"AU08 ACTIONABLE GEOMETRY PASS: label={label.Index}:{label.Version}, root={root.Index}:{root.Version}, identityAncestorCount={depth}, rootRelativePosition=(0,0,0), min={V(bounds.Min)}, max={V(bounds.Max)}, belongs=0x80, collides=0x80, response=None, assignOnKeyDown=True; structural collider unchanged.");
    }

    private static void AfterInitialize(Core __instance)
    {
        if (_clone == null || !ContainsClone(__instance)) return;
        try
        {
            MainThread();
            var key = new SCPrefab { _prefab = PrefabId };
            var map = __instance._componentsMap;
            if (map == null || !map.TryGetValue(key, out var authoring) || authoring == null || authoring.Pointer != _clone.Pointer)
                throw new InvalidOperationException("Core._componentsMap did not register the clone.");
            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null || !_world.IsCreated) throw new InvalidOperationException("Missing owning world.");
            var manager = _world.EntityManager;
            manager.CompleteAllTrackedJobs();
            var converted = EntityPrefabComponent.GameObjectToEntityMap;
            if (converted == null || !converted.TryGetValue(_clone.gameObject, out var entity) || !manager.Exists(entity) ||
                !manager.HasComponent(entity, ComponentType.ReadOnly(Il2CppType.Of<SCTypeLabel>())) ||
                manager.GetComponentData<SCPrefab>(entity)._prefab != PrefabId ||
                manager.GetComponentData<SCBlueprintClass>(entity)._class != 35)
                throw new InvalidOperationException("Native label prefab conversion did not complete.");
            if (manager.HasComponent(entity, ComponentType.ReadOnly(Il2CppType.Of<SCTypeSignalProcessor>())) ||
                !manager.HasComponent(entity, ComponentType.ReadOnly(Il2CppType.Of<Unity.Physics.PhysicsCollider>())))
                throw new InvalidOperationException("Hybrid conversion retained a vanilla processor or lost the math chassis collider.");
            ValidateInteractionGeometry(manager, entity, true, true);
            ValidatePortGeometry(manager, entity, true);
            ValidatePortNames(manager, entity, initialize: true);
            var renderedBounds = new Bounds();
            bool firstMesh = true;
            foreach (var renderer in Renderers)
            {
                if (!converted.TryGetValue(renderer.gameObject, out var child) || !manager.Exists(child) ||
                    !manager.HasComponent(child, ComponentType.ReadOnly(Il2CppType.Of<CRPRendererData>())))
                    throw new InvalidOperationException("Custom mesh did not enter the native CRP renderer path.");
                var transform = manager.GetComponentData<LocalTransform>(child);
                var parent = manager.GetComponentData<Parent>(child).Value;
                var mesh = manager.GetComponentData<CRPRendererData>(child).GetDrawDataMeshReference()._meshReference.Managed();
                var localBounds = manager.GetComponentData<CRPLocalBounds>(child)._localBounds;
                _log!.LogInfo($"AU08 CRP {renderer.name}: entity={child.Index}:{child.Version}, parent={parent.Index}:{parent.Version}, position={V(transform.Position)}, scale={transform.Scale:R}, rotation=({transform.Rotation.value.x:R},{transform.Rotation.value.y:R},{transform.Rotation.value.z:R},{transform.Rotation.value.w:R}), postTransform={manager.HasComponent(child, ComponentType.ReadOnly(Il2CppType.Of<PostTransformMatrix>()))}, mesh={mesh?.name}, boundsCenter={V(localBounds.Center)}, boundsExtents={V(localBounds.Extents)}.");
                if (parent != entity || transform.Position.x != 0 || transform.Position.y != 0 || transform.Position.z != 0 ||
                    transform.Scale != 1 || transform.Rotation.value.x != 0 || transform.Rotation.value.y != 0 ||
                    transform.Rotation.value.z != 0 || MathF.Abs(transform.Rotation.value.w) != 1 ||
                    manager.HasComponent(child, ComponentType.ReadOnly(Il2CppType.Of<PostTransformMatrix>())) ||
                    mesh == null || mesh.Pointer != renderer._mesh.Pointer)
                    throw new InvalidOperationException("CRP model transform/mesh differs from the physical chassis-space export.");
                var bounds = mesh.bounds;
                if (((Vector3)localBounds.Center - bounds.center).sqrMagnitude > 1e-10f ||
                    ((Vector3)localBounds.Extents - bounds.extents).sqrMagnitude > 1e-10f)
                    throw new InvalidOperationException("Native CRP bounds differ from the uploaded mesh bounds.");
                if (firstMesh) { renderedBounds = bounds; firstMesh = false; }
                else renderedBounds.Encapsulate(bounds);
                if (renderer.name is "AU08_InputAmber" or "AU08_OutputBlue")
                    ValidateSocketRings(mesh.vertices, _mouths, renderer.name == "AU08_InputAmber" ? 0 : 8);
            }
            if (firstMesh || renderedBounds.center.sqrMagnitude > 1e-10f ||
                (renderedBounds.size - (Vector3)DeviceBounds).sqrMagnitude > 1e-10f)
                throw new InvalidOperationException("Converted model no longer fills the physical device bounds.");
            _registered = true;
            _prefabEntity = entity;
            GraphScreen.AfterInitialize();
            _log!.LogInfo($"Computer item registered: AU-08, prefab={PrefabId:X16}, native class=35, ports=8/8, CRP parts={Renderers.Count}. GEOMETRY PASS: 16 native mouth centers match actual mesh rings, glyph/ring radius matched, CRP transforms identity, bounds={V(renderedBounds.size)}. Programs remain disarmed.");
        }
        catch (Exception ex)
        {
            _registered = false;
            // Retain converted assets/ECS data for native lifetime management, but remove only our inventory entry.
            try
            {
                _clone._categories = (SpaceshipComponentUICategory)0;
                _clone._availableAmount = 0;
                var items = __instance._spaceshipComponents;
                if (items != null)
                {
                    int count = 0;
                    foreach (var item in items) if (item == null || item.Pointer != _clone.Pointer) count++;
                    var filtered = new Il2CppReferenceArray<EPC_SpaceshipComponent>(count);
                    int index = 0;
                    foreach (var item in items)
                        if (item == null || item.Pointer != _clone.Pointer) filtered[index++] = item!;
                    __instance._spaceshipComponents = filtered;
                }
                var map = __instance._componentsMap;
                var key = new SCPrefab { _prefab = PrefabId };
                if (map != null && map.TryGetValue(key, out var itemInMap) && itemInMap != null && itemInMap.Pointer == _clone.Pointer)
                    map.Remove(key);
            }
            catch (Exception hideError) { _log?.LogError($"Could not completely hide the failed computer entry: {hideError.Message}"); }
            _log?.LogError($"Computer conversion verification failed; execution disabled. Do not use/save the item; restart. {ex.Message}");
        }
        finally { if (_holder != null) _holder.SetActive(false); }
    }

    // Explicit diagnostic only. Caller owns dedicated-process/environment, main-menu and solo-session authorization.
    // Run synchronously in one post-group callback: no frames, systems, scripts, file saves or world lifecycle calls.
    internal static unsafe void ProbeInstance(EntityManager manager, Core.Singleton core)
    {
        MainThread();
        if (!IsRegistered || _clone == null || manager.m_EntityDataAccess == IntPtr.Zero ||
            _world!.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess || core._scPrefabDataMap == IntPtr.Zero)
            throw new InvalidOperationException("Instance probe requires the registered world and its fresh Core.Singleton.");
        manager.CompleteAllTrackedJobs();
        var prefab = _prefabEntity;
        uint alignment = 0;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(EPC_SCLabel.Blueprint.BlueprintData).TypeHandle);
        var nativeBlueprintClass = Il2CppClassPointerStore<EPC_SCLabel.Blueprint.BlueprintData>.NativeClassPtr;
        if (nativeBlueprintClass == IntPtr.Zero) throw new InvalidOperationException("Native blueprint class unavailable.");
        int blueprintSize = IL2CPP.il2cpp_class_value_size(nativeBlueprintClass, ref alignment);
        // The generated CLR wrapper omits native tail padding (53 versus 56 bytes).
        if (blueprintSize != 56)
            throw new InvalidOperationException($"Class-35 blueprint layout is {blueprintSize} bytes; expected 56. No instance created.");
        if (!manager.Exists(prefab))
            throw new InvalidOperationException($"Cached computer prefab {prefab.Index}:{prefab.Version} no longer exists in the registered world (ID={PrefabId:X16}). No instance created.");
        if (!manager.HasComponent(prefab, ComponentType.ReadOnly(Il2CppType.Of<Prefab>())))
            throw new InvalidOperationException($"Cached computer entity {prefab.Index}:{prefab.Version} lacks Prefab. No instance created.");
        if (!manager.HasComponent(prefab, ComponentType.ReadOnly(Il2CppType.Of<SCBlueprintClass>())))
            throw new InvalidOperationException("Cached computer prefab lacks SCBlueprintClass. No instance created.");
        byte blueprintClass = manager.GetComponentData<SCBlueprintClass>(prefab)._class;
        if (blueprintClass != 35)
            throw new InvalidOperationException($"Cached computer blueprint class is {blueprintClass}, expected 35. No instance created.");
        var prefabLabel = ValidateLabel(manager, prefab, true);
        var originalLabel = manager.GetComponentData<ActionableLabelString>(prefabLabel);
        var prefabMembers = new HashSet<(int, int)>();
        var prefabLinks = manager.GetBuffer<LinkedEntityGroup>(prefab, true);
        if (prefabLinks.Length is < 2 or > 256 || prefabLinks[0].Value != prefab)
            throw new InvalidOperationException("Invalid prefab linked group.");
        for (int i = 0; i < prefabLinks.Length; i++)
        {
            var member = prefabLinks[i].Value;
            // Native port children need not carry Prefab themselves; linked-group membership owns their cloning.
            if (!manager.Exists(member) || !prefabMembers.Add((member.Index, member.Version)))
                throw new InvalidOperationException("Prefab linked group contains duplicate or missing members.");
        }
        var prefabPorts = manager.GetBuffer<SpaceshipElectricPortRef>(prefab, true);
        if (prefabPorts.Length != 16) throw new InvalidOperationException("Expected 16 prefab ports.");
        var originalPorts = new Entity[16];
        var originalValues = new float[16];
        for (int i = 0; i < 16; i++)
        {
            originalPorts[i] = prefabPorts[i]._portEntity;
            if (!prefabMembers.Contains((originalPorts[i].Index, originalPorts[i].Version)))
                throw new InvalidOperationException("Prefab port is outside its linked group.");
            originalValues[i] = manager.GetComponentData<SpaceshipElectricPort>(originalPorts[i])._portValue;
        }

        // A read-only baseline lets cleanup refuse EVERY pre-existing entity, not just the source prefab.
        var preexisting = new HashSet<(int, int)>();
        var snapshot = manager.GetAllEntities(Allocator.Temp);
        try
        {
            if (snapshot.Length > 131072) throw new InvalidOperationException("Instance probe world snapshot exceeds its bound.");
            for (int i = 0; i < snapshot.Length; i++) preexisting.Add((snapshot[i].Index, snapshot[i].Version));
        }
        finally { snapshot.Dispose(); }
        if (!preexisting.IsSupersetOf(prefabMembers))
            throw new InvalidOperationException("World snapshot omitted prefab entities; refusing an incomplete ownership baseline.");

        var roots = new List<Entity>(2);
        var owned = new List<Entity>(512);
        var owners = new Dictionary<(int, int), Entity>();
        var errors = new List<Exception>();
        const string firstId = "A00800000001", forkId = "A00800000002";
        int groupSize = prefabMembers.Count;
        try
        {
            var instance = manager.Instantiate(prefab);
            RememberRoot(instance);
            CaptureGroup(instance);
            ValidatePortGeometry(manager, instance, false);
            ValidateInteractionGeometry(manager, instance, false, false);
            ValidatePortNames(manager, instance);
            if (!IsComputer(manager, instance) || GetProgramId(manager, instance) != null)
                throw new InvalidOperationException("Fresh instance is not an unbound computer.");
            SetProgramId(manager, instance, firstId);
            if (GetProgramId(manager, instance) != firstId) throw new InvalidOperationException("Program ID write/read failed.");

            // Avoid native AddNewGUID: it can use the garage GUID pool. Only our owned root gets this CLR-generated GUID.
            var bytes = Guid.NewGuid().ToByteArray();
            var guid = new SCGuid { _a = BitConverter.ToUInt32(bytes, 0), _b = BitConverter.ToUInt32(bytes, 4),
                _c = BitConverter.ToUInt32(bytes, 8), _d = BitConverter.ToUInt32(bytes, 12) };
            if (!manager.HasComponent(instance, ComponentType.ReadOnly<SCGuid>()))
                manager.AddComponent(instance, ComponentType.ReadOnly<SCGuid>());
            manager.SetComponentData<SCGuid>(instance, guid);
            if (!manager.HasComponent(instance, ComponentType.ReadOnly<GarageTransform>()))
                manager.AddComponentData<GarageTransform>(instance, GarageTransform.identity);
            else manager.SetComponentData<GarageTransform>(instance, GarageTransform.identity);

            var ports = manager.GetBuffer<SpaceshipElectricPortRef>(instance, true);
            if (ports.Length != 16) throw new InvalidOperationException("Instance port count changed.");
            var firstPorts = new Entity[16];
            for (int i = 0; i < 16; i++)
            {
                var portEntity = firstPorts[i] = ports[i]._portEntity;
                if (!owners.TryGetValue((portEntity.Index, portEntity.Version), out var owner) || owner != instance)
                    throw new InvalidOperationException("Instance port was not remapped to its owned group.");
                var port = manager.GetComponentData<SpaceshipElectricPort>(portEntity);
                if (port._index != i || port._setupType != (i < 8 ? SpaceshipPortType.DataInput : SpaceshipPortType.DataOutput))
                    throw new InvalidOperationException("Instance port roles/indexes changed.");
                float value = i < 8 ? i + 0.25f : -(i + 0.25f);
                port.SetDataValue(value);
                manager.SetComponentData<SpaceshipElectricPort>(portEntity, port);
                if (manager.GetComponentData<SpaceshipElectricPort>(portEntity)._portValue != value)
                    throw new InvalidOperationException("Instance scalar port read/write failed.");
            }
            var copy = manager.Instantiate(instance);
            RememberRoot(copy);
            CaptureGroup(copy);
            ValidatePortGeometry(manager, copy, false);
            ValidateInteractionGeometry(manager, copy, false, false);
            ValidatePortNames(manager, copy);
            if (!IsComputer(manager, copy) || GetProgramId(manager, copy) != firstId ||
                ValidateLabel(manager, copy, false) == ValidateLabel(manager, instance, false))
                throw new InvalidOperationException("ECS copy did not preserve the ID in an independent storage child.");
            bytes = Guid.NewGuid().ToByteArray();
            var copyGuid = new SCGuid { _a = BitConverter.ToUInt32(bytes, 0), _b = BitConverter.ToUInt32(bytes, 4),
                _c = BitConverter.ToUInt32(bytes, 8), _d = BitConverter.ToUInt32(bytes, 12) };
            if (copyGuid == guid) throw new InvalidOperationException("Duplicate diagnostic GUID.");
            manager.SetComponentData<SCGuid>(copy, copyGuid);
            var copiedPorts = manager.GetBuffer<SpaceshipElectricPortRef>(copy, true);
            if (copiedPorts.Length != 16) throw new InvalidOperationException("Copied port count changed.");
            for (int i = 0; i < 16; i++)
            {
                var entity = copiedPorts[i]._portEntity;
                if (entity == firstPorts[i] || !owners.TryGetValue((entity.Index, entity.Version), out var owner) || owner != copy ||
                    manager.GetComponentData<SpaceshipElectricPort>(entity)._portValue != (i < 8 ? i + 0.25f : -(i + 0.25f)))
                    throw new InvalidOperationException("Copied ports alias the original or lost their values.");
            }
            SetProgramId(manager, copy, forkId);
            if (GetProgramId(manager, copy) != forkId || GetProgramId(manager, instance) != firstId ||
                manager.GetComponentData<SCGuid>(instance) != guid || manager.GetComponentData<SCGuid>(copy) != copyGuid)
                throw new InvalidOperationException("Copied program reference/GUID isolation failed.");

            // Pinned build native B338B0 writes 56 bytes, ignores extraData; B31010 puts UTF-16 ID at +16.
            // No NativeList is constructed or guessed. FromBinary is deliberately NOT called: it also loads placement/color.
            byte* binary = stackalloc byte[16 + 56 + 16];
            for (int n = 0; n < 2; n++)
            {
                for (int i = 0; i < 88; i++) binary[i] = 0xA5;
                var entity = n == 0 ? instance : copy;
                var expectedGuid = n == 0 ? guid : copyGuid;
                string expectedId = n == 0 ? firstId : forkId;
                EPC_SCLabel.Blueprint.ToBinary(manager, core, entity, binary + 16, null);
                for (int i = 0; i < 16; i++)
                    if (binary[i] != 0xA5 || binary[72 + i] != 0xA5) throw new InvalidOperationException("Native serializer guard changed.");
                if (*(uint*)(binary + 16) != expectedGuid._a || *(uint*)(binary + 20) != expectedGuid._b ||
                    *(uint*)(binary + 24) != expectedGuid._c || *(uint*)(binary + 28) != expectedGuid._d)
                    throw new InvalidOperationException("Native serializer GUID mismatch.");
                for (int i = 0; i < 16; i++)
                    if (*(ushort*)(binary + 32 + i * 2) != (i < 12 ? expectedId[i] : '\0'))
                        throw new InvalidOperationException("Native serializer program ID/termination mismatch.");
            }
            // Diagnostic-only normalization: this does not prove natural role initialization or cabling.
            foreach (var probeRoot in new[] { instance, copy })
            {
                var probePorts = manager.GetBuffer<SpaceshipElectricPortRef>(probeRoot, true);
                for (int i = 0; i < probePorts.Length; i++)
                {
                    var child = probePorts[i]._portEntity;
                    var port = manager.GetComponentData<SpaceshipElectricPort>(child);
                    port._runtimeType = port._setupType;
                    manager.SetComponentData(child, port);
                }
                var probeTarget = Ports.GetTarget(manager, probeRoot)
                    ?? throw new InvalidOperationException("Owned probe computer was not discoverable through Ports.GetTarget.");
                if (probeTarget.Inputs.Length != 8 || probeTarget.Outputs.Length != 8 ||
                    probeTarget.Outputs[0] != probePorts[8]._portEntity ||
                    manager.GetComponentData<SpaceshipElectricPort>(probeTarget.Outputs[0])._index != 8)
                    throw new InvalidOperationException("Lua Output 1 does not map to native port index 8.");
                var constant = new LuaComputer("function tick() output(1, 1) end", 8, 8);
                Ports.WriteOutputs(manager, probeTarget, constant.Tick(Ports.ReadInputs(manager, probeTarget), 1.0 / 60, 0));
                for (int i = 8; i < 16; i++)
                    if (manager.GetComponentData<SpaceshipElectricPort>(probePorts[i]._portEntity)._portValue != (i == 8 ? 1 : 0))
                        throw new InvalidOperationException("Constant Output 1 probe did not write native port 8=1 and ports 9..15=0.");
            }
            // Bounded timing sample on owned temporary entities, not a saved ship or a new World.
            var target = Ports.GetTarget(manager, instance)
                ?? throw new InvalidOperationException("Owned timing target was not discoverable through Ports.GetTarget.");
            var vm = new LuaComputer("function tick() for i=1,8 do output(i,input(i)*2) end end", 8, 8);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 60; i++)
            {
                if (GetProgramId(manager, instance) != firstId) throw new InvalidOperationException("Probe program changed.");
                Ports.WriteOutputs(manager, target, vm.Tick(Ports.ReadInputs(manager, target), 1.0 / 60, i));
            }
            timer.Stop();
            _log?.LogInfo($"COMPUTER TIMING: {timer.Elapsed.TotalMilliseconds / 60:F3} ms/step for 8-in/8-out, 60 steps including validation + Lua + writes. Excludes real-world job wait.");
            for (int i = 8; i < 16; i++)
                if (manager.GetComponentData<SpaceshipElectricPort>(firstPorts[i])._portValue != ((i - 8) + 0.25f) * 2)
                    throw new InvalidOperationException("8-port Lua timing sample output mismatch.");
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            // Collect owned children even if an earlier check failed; never sweep newly appearing unrelated entities.
            foreach (var root in roots)
                try { CaptureGroup(root); } catch (Exception ex) { errors.Add(ex); }
            foreach (var entity in owned)
            {
                try
                {
                    // Disable implicit linked-group cascades, so even malformed references cannot delete existing entities.
                    if (manager.Exists(entity) && manager.HasComponent(entity, ComponentType.ReadOnly<LinkedEntityGroup>()))
                        manager.RemoveComponent(entity, ComponentType.ReadOnly<LinkedEntityGroup>());
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            foreach (var entity in owned)
            {
                try
                {
                    if (!manager.Exists(entity)) continue;
                    if (manager.HasComponent(entity, ComponentType.ReadOnly<LinkedEntityGroup>()))
                        throw new InvalidOperationException("Refusing unsafe linked-group cascade during probe cleanup.");
                    manager.DestroyEntity(entity);
                    if (manager.Exists(entity)) throw new InvalidOperationException("Owned probe entity survived destruction.");
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            try
            {
                if (!manager.Exists(prefab) || ValidateLabel(manager, prefab, true) != prefabLabel)
                    throw new InvalidOperationException("Source prefab/storage changed during probe.");
                var currentLabel = manager.GetComponentData<ActionableLabelString>(prefabLabel);
                for (int i = 0; i < 16; i++)
                    if (originalLabel.GetCharacter(i) != currentLabel.GetCharacter(i))
                        throw new InvalidOperationException("Source prefab label was modified.");
                var currentPorts = manager.GetBuffer<SpaceshipElectricPortRef>(prefab, true);
                if (currentPorts.Length != 16) throw new InvalidOperationException("Source prefab port layout changed.");
                for (int i = 0; i < 16; i++)
                    if (currentPorts[i]._portEntity != originalPorts[i] ||
                        manager.GetComponentData<SpaceshipElectricPort>(originalPorts[i])._portValue != originalValues[i])
                        throw new InvalidOperationException("Source prefab ports were modified.");
                ValidatePortNames(manager, prefab);
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count != 0)
        {
            var failure = new AggregateException("Computer instance probe failed (including cleanup checks).", errors);
            _log?.LogError($"COMPUTER INSTANCE PROBE FAIL: {failure}");
            throw failure;
        }
        _log?.LogInfo($"COMPUTER INSTANCE PROBE PASS: native instantiate + ECS copy ({groupSize} entities each); " +
            $"16 scalar ports read/write + copy remapping; independent label IDs + distinct GUIDs; " +
            $"class35 ToBinary=56 bytes, GUID/UTF16-ID/terminators/guards verified for both; " +
            $"all {owned.Count} owned entities destroyed; source prefab label/ports unchanged. " +
            "Constant Lua Output 1 via Ports.GetTarget verified native port 8=1 and ports 9..15=0 on both owned copies; runtime roles were normalized by the probe. " +
            "Fixed built-in Lua benchmark verified on all 8 outputs. NOT TESTED: natural role initialization, FromBinary, file save/load, blueprint transport, cable propagation, rendering, rewind. No user script executed.");

        void RememberRoot(Entity root)
        {
            if (preexisting.Contains((root.Index, root.Version)) || !manager.Exists(root) ||
                manager.HasComponent(root, ComponentType.ReadOnly(Il2CppType.Of<Prefab>())))
                throw new InvalidOperationException("Instantiate did not return a new non-prefab entity.");
            roots.Add(root);
            owned.Add(root);
            owners.Add((root.Index, root.Version), root);
        }

        void CaptureGroup(Entity root)
        {
            var links = manager.GetBuffer<LinkedEntityGroup>(root, true);
            if (links.Length is < 1 or > 256) throw new InvalidOperationException("Instance linked-group size is outside its bound.");
            bool invalid = links.Length != groupSize || links[0].Value != root;
            var seen = new HashSet<(int, int)>();
            for (int i = 0; i < links.Length; i++)
            {
                var member = links[i].Value;
                var key = (member.Index, member.Version);
                if (preexisting.Contains(key) || !manager.Exists(member) || !seen.Add(key) ||
                    manager.HasComponent(member, ComponentType.ReadOnly(Il2CppType.Of<Prefab>()))) { invalid = true; continue; }
                if (owners.TryGetValue(key, out var owner))
                {
                    if (owner != root) invalid = true;
                }
                else { owners.Add(key, root); owned.Add(member); }
            }
            if (invalid) throw new InvalidOperationException("Instance linked group contains existing, shared, duplicate or prefab entities.");
        }
    }

    // Caller must complete the owning world's jobs before these methods, including reads.
    internal static bool IsComputer(EntityManager manager, Entity component)
    {
        MainThread();
        return IsRegistered && manager.m_EntityDataAccess != IntPtr.Zero &&
            _world!.EntityManager.m_EntityDataAccess == manager.m_EntityDataAccess &&
            manager.Exists(component) && !manager.HasComponent(component, ComponentType.ReadOnly(Il2CppType.Of<Prefab>())) &&
            !manager.HasComponent(component, ComponentType.ReadOnly<Disabled>()) &&
            manager.HasComponent(component, ComponentType.ReadOnly<SCPrefab>()) &&
            manager.HasComponent(component, ComponentType.ReadOnly(Il2CppType.Of<SCTypeLabel>())) &&
            manager.GetComponentData<SCPrefab>(component)._prefab == PrefabId;
    }

    internal static string? GetProgramId(EntityManager manager, Entity component, bool allowInvalid = false)
    {
        if (!IsComputer(manager, component)) throw new InvalidOperationException("Not a placed computer in the registered world.");
        var value = manager.GetComponentData<ActionableLabelString>(ValidateLabel(manager, component, false));
        return ParseId(value, allowInvalid);
    }

    internal static string? ParseId(ActionableLabelString value, bool allowInvalid)
    {
        Span<char> id = stackalloc char[12];
        bool empty = true, terminated = true;
        for (int i = 0; i < 16; i++)
        {
            char c = value.GetCharacter(i);
            empty &= c == '\0';
            if (i < 12) id[i] = c;
            else terminated &= c == '\0';
        }
        if (empty) return null;
        if (terminated && ValidId(id)) return new string(id);
        // Only explicit editor repair may treat malformed text as an unbound reference.
        if (allowInvalid) return null;
        throw new InvalidDataException("Invalid program reference; open editor and Save to replace it.");
    }

    internal static void SetProgramId(EntityManager manager, Entity component, string id)
    {
        if (id == null || !ValidId(id.AsSpan())) throw new ArgumentException("Program ID must be exactly 12 uppercase hexadecimal characters.", nameof(id));
        if (!IsComputer(manager, component)) throw new InvalidOperationException("Not a placed computer in the registered world.");
        var label = ValidateLabel(manager, component, false);
        var value = default(ActionableLabelString);
        for (int i = 0; i < id.Length; i++) value.SetCharacter(i, id[i]);
        // Native class 35 persists this component; source, trust and fork-on-save live in the caller's local library.
        manager.SetComponentData<ActionableLabelString>(label, value);
    }

    internal static bool ValidId(ReadOnlySpan<char> id)
    {
        if (id.Length != 12) return false;
        foreach (char c in id) if (!(c is >= '0' and <= '9' or >= 'A' and <= 'F')) return false;
        return true;
    }

    internal static Entity ValidateLabel(EntityManager manager, Entity component, bool prefab)
    {
        var label = manager.GetComponentData<SCTypeLabel>(component)._actionableLabelEntity;
        if (label == component || !manager.Exists(label) ||
            manager.HasComponent(label, ComponentType.ReadOnly(Il2CppType.Of<Prefab>())) != prefab ||
            manager.HasComponent(label, ComponentType.ReadOnly<Disabled>()) ||
            !manager.HasComponent(label, ComponentType.ReadOnly<ActionableLabelString>()) ||
            !manager.HasComponent(label, ComponentType.ReadOnly<ActionableLabel>()) ||
            !manager.HasComponent(label, ComponentType.ReadOnly<BelongingSC>()) ||
            !manager.HasComponent(component, ComponentType.ReadOnly<LinkedEntityGroup>()))
            throw new InvalidOperationException("Missing, stale or foreign actionable label child.");
        var owner = manager.GetComponentData<BelongingSC>(label);
        var links = manager.GetBuffer<LinkedEntityGroup>(component, true);
        if (owner._sc != component || links.Length is < 2 or > 256 || owner._linkedEntityGroupIndex < 1 ||
            owner._linkedEntityGroupIndex >= links.Length || links[owner._linkedEntityGroupIndex].Value != label)
            throw new InvalidOperationException("Actionable label does not belong to this computer's linked group.");
        var data = manager.GetComponentData<ActionableLabel>(label);
        if (data._maxLength is < 12 or > 16 || !manager.Exists(data._text3DRendererEntity) ||
            !manager.HasComponent(data._text3DRendererEntity, ComponentType.ReadOnly(Il2CppType.Of<CRPText3DString>())))
            throw new InvalidOperationException("Native label storage or serializer text buffer is missing.");
        bool ownsText = false;
        for (int i = 1; i < links.Length; i++)
            if (links[i].Value == data._text3DRendererEntity) ownsText = true;
        if (!ownsText) throw new InvalidOperationException("Native label text renderer belongs to another component.");
        return label;
    }

    private static void MainThread()
    {
        if (_mainThread == 0 || Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException("Computer item access requires the plugin's main thread.");
    }

    private static bool OwnedTransform(Transform child, Transform root)
    {
        for (int depth = 0; child != null && depth < 64; depth++, child = child.parent)
            if (child == root) return true;
        return false;
    }

    private static bool TryGetSupport(EPC_SpaceshipComponent chassis, out Vector3 contact, out int areaCount)
    {
        contact = new Vector3();
        areaCount = 0;
        var colliders = chassis._colliders;
        if (colliders == null || colliders.Length is < 1 or > 64) return false;
        foreach (var collider in colliders)
            if (collider == null || !OwnedTransform(collider.transform, chassis.transform) ||
                collider.GetShapeType() == DOTSCollider.ShapeType.None) return false;
        var polygons = chassis.gameObject.GetComponentsInChildren(Il2CppType.Of<SpaceshipComponentAreaPoly>(), true);
        if (polygons.Length is < 1 or > 64) return false;
        float floor = float.MaxValue;
        foreach (var component in polygons)
        {
            var polygon = component.Cast<SpaceshipComponentAreaPoly>();
            bool visiblePath = true;
            var ancestor = polygon.transform;
            for (int depth = 0; ancestor != chassis.transform; depth++, ancestor = ancestor.parent)
            {
                if (ancestor == null || depth == 64) return false;
                if (!ancestor.gameObject.activeSelf) visiblePath = false;
            }
            if (!visiblePath) continue;
            if (polygon._type != SpaceshipJointType.Normal || polygon._material is not
                (SpaceshipJointMaterial.Iron or SpaceshipJointMaterial.Batteries or SpaceshipJointMaterial.Nano or SpaceshipJointMaterial.Glass)) return false;
            var vertices = polygon._vertices;
            if (vertices == null || vertices.Length is < 3 or > 64) return false;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var p in vertices)
            {
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z) ||
                    MathF.Abs(p.x) > 10000 || MathF.Abs(p.y) > 10000 || MathF.Abs(p.z) > 10000) return false;
                minX = MathF.Min(minX, p.x); maxX = MathF.Max(maxX, p.x);
                minY = MathF.Min(minY, p.y); maxY = MathF.Max(maxY, p.y);
                minZ = MathF.Min(minZ, p.z); maxZ = MathF.Max(maxZ, p.z);
            }
            int axes = (maxX - minX > 0.000001f ? 1 : 0) + (maxY - minY > 0.000001f ? 1 : 0) +
                (maxZ - minZ > 0.000001f ? 1 : 0);
            if (axes != 2) return false;
            areaCount++;
            // Native C1A110..C1A3A5 uses these vertices directly in chassis coordinates, not the child Unity transform.
            // Bottom normal joint polygon defines both the contact plane and the native footprint's X/Z center.
            if (maxY - minY <= 0.000001f && minY < floor)
            {
                var center = new float2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
                if (!EPC_SpaceshipComponent.IsPointInsidePolygonXZ(vertices, center)) return false;
                floor = minY;
                contact = new Vector3(center.x, minY, center.y);
            }
        }
        return areaCount != 0 && floor != float.MaxValue;
    }

    private static bool FinitePositive(float value) => float.IsFinite(value) && value > 0 && value <= 10000;

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Asset exceeds its size limit.");
        var bytes = new byte[(int)file.Length];
        int read = 0;
        while (read < bytes.Length)
        {
            int count = file.Read(bytes, read, bytes.Length - read);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
        if (file.ReadByte() != -1) throw new InvalidDataException("Asset changed while reading.");
        return bytes;
    }

    private static unsafe void TryLoadIcon(EPC_SCLabel clone)
    {
        string path = Path.Combine(_assetDirectory, "computer-icon.rgba");
        if (!File.Exists(path)) return;
        Texture2D? icon = null;
        try
        {
            var bytes = ReadBounded(path, 8 + 512 * 512 * 4);
            ValidateIcon(bytes);
            icon = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            // Bottom-up, straight-alpha RGBA8. The pointer overload avoids the broken unstripped span bridge.
            fixed (byte* pixels = &bytes[8]) icon.LoadRawTextureData((IntPtr)pixels, bytes.Length - 8);
            icon.Apply(false, true);
            Assets.Add(icon);
            clone._iconTexture2D = icon;
            _log?.LogInfo("Computer icon loaded: 512x512 raw RGBA8.");
        }
        catch (Exception ex)
        {
            if (icon != null) Object.Destroy(icon);
            _log?.LogWarning($"Computer uses native label icon: {ex.Message}");
        }
    }

    private static void ValidateIcon(byte[] bytes)
    {
        if (bytes.Length != 8 + 512 * 512 * 4 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 512 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 512)
            throw new InvalidDataException("Raw icon requires a 512x512 little-endian size header and exactly 1048576 RGBA8 bytes.");
    }

    private sealed record Part(string Name, Color Color, Vector3[] Vertices, Vector3[] Normals, int[] Triangles);
    private sealed record Model(List<Part> Parts, Vector3[] Ports);

    private static Model ReadModel(JsonElement root)
    {
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("name").GetString() != "AU-08")
            throw new InvalidDataException("Unsupported computer mesh contract.");
        var bounds = Vector(root.GetProperty("bounds"));
        if (bounds.x != DeviceBounds.x || bounds.y != DeviceBounds.y || bounds.z != DeviceBounds.z)
            throw new InvalidDataException("AU-08 requires the physical 1 x 0.25 x 1 mesh; redeploy updated assets.");
        var parts = root.GetProperty("parts");
        if (parts.GetArrayLength() is < 1 or > 64) throw new InvalidDataException("Invalid part count.");
        var result = new List<Part>();
        var minimum = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var maximum = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        int vertices = 0, indices = 0;
        foreach (var part in parts.EnumerateArray())
        {
            string name = part.GetProperty("name").GetString() ?? "";
            if (name.Length is < 1 or > 64) throw new InvalidDataException("Invalid mesh part name.");
            foreach (char c in name) if (c is < ' ' or > '~') throw new InvalidDataException("Mesh names must be ASCII.");
            var color = part.GetProperty("color");
            if (color.GetArrayLength() != 4) throw new InvalidDataException("Expected RGBA color.");
            var rgba = new float[4];
            for (int i = 0; i < 4; i++)
            {
                rgba[i] = color[i].GetSingle();
                if (!float.IsFinite(rgba[i]) || rgba[i] < 0 || rgba[i] > 1) throw new InvalidDataException("Invalid color.");
            }
            var v = part.GetProperty("vertices");
            var n = part.GetProperty("normals");
            var t = part.GetProperty("triangles");
            int count = v.GetArrayLength(), indexCount = t.GetArrayLength();
            vertices += count;
            indices += indexCount;
            if (count < 3 || vertices > 24576 || indexCount < 3 || indexCount % 3 != 0 || indices > 8192 * 3 || n.GetArrayLength() != count)
                throw new InvalidDataException("Mesh exceeds 8192 triangles/24576 vertices or has invalid arrays.");
            var positions = new Vector3[count];
            var normals = new Vector3[count];
            var triangles = new int[indexCount];
            for (int i = 0; i < count; i++)
            {
                var p = positions[i] = Vector(v[i]);
                minimum = new Vector3(MathF.Min(minimum.x, p.x), MathF.Min(minimum.y, p.y), MathF.Min(minimum.z, p.z));
                maximum = new Vector3(MathF.Max(maximum.x, p.x), MathF.Max(maximum.y, p.y), MathF.Max(maximum.z, p.z));
                var normal = Vector(n[i]);
                float length = MathF.Sqrt(normal.x * normal.x + normal.y * normal.y + normal.z * normal.z);
                if (length is < 0.5f or > 1.5f) throw new InvalidDataException("Invalid vertex normal.");
                normals[i] = new Vector3(normal.x / length, normal.y / length, normal.z / length);
            }
            for (int i = 0; i < indexCount; i++)
            {
                triangles[i] = t[i].GetInt32();
                if (triangles[i] < 0 || triangles[i] >= count) throw new InvalidDataException("Triangle index outside vertex array.");
            }
            result.Add(new Part(name, new Color(rgba[0], rgba[1], rgba[2], rgba[3]), positions, normals, triangles));
        }
        var center = new Vector3((minimum.x + maximum.x) * 0.5f, (minimum.y + maximum.y) * 0.5f, (minimum.z + maximum.z) * 0.5f);
        if ((maximum - minimum - bounds).sqrMagnitude > 1e-10f || center.sqrMagnitude > 1e-10f ||
            result.FindAll(p => p.Name == "InputAmber").Count != 1 || result.FindAll(p => p.Name == "OutputBlue").Count != 1)
            throw new InvalidDataException("Geometry must fill centered physical bounds and contain both socket ring parts.");
        var inputs = root.GetProperty("ports").GetProperty("inputs");
        var outputs = root.GetProperty("ports").GetProperty("outputs");
        if (inputs.GetArrayLength() != 8 || outputs.GetArrayLength() != 8) throw new InvalidDataException("Expected 8 input and 8 output positions.");
        var ports = new Vector3[16];
        for (int i = 0; i < ports.Length; i++)
        {
            var p = ports[i] = Vector(i < 8 ? inputs[i] : outputs[i - 8]);
            var expected = new Vector3(i < 8 ? -0.5f : 0.5f, -0.0625f, 0.4375f - i % 8 * PortPitch);
            if ((p - expected).sqrMagnitude > 1e-10f)
                throw new InvalidDataException("Socket mouth is not on the native 0.125-pitch layout.");
        }
        return new Model(result, ports);
    }

    private static Vector3 Vector(JsonElement array)
    {
        if (array.GetArrayLength() != 3) throw new InvalidDataException("Expected xyz vector.");
        float x = array[0].GetSingle(), y = array[1].GetSingle(), z = array[2].GetSingle();
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || MathF.Abs(x) > 16 || MathF.Abs(y) > 16 || MathF.Abs(z) > 16)
            throw new InvalidDataException("Non-finite or oversized model coordinate.");
        return new Vector3(x, y, z);
    }
}
