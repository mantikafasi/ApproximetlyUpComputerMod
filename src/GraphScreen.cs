using System.Text.Json;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ApproximatelyUp.ComputerMod;

internal static class GraphScreen
{
    internal const string Name = "GP-04 Graph Screen";
    internal static ulong PrefabId { get; private set; }
    private static EPC_SCLabel? authoring;
    private static Entity prefab;
    internal static Entity PrefabEntity => prefab;
    private static World? world;
    private static ManualLogSource? log;
    private static readonly List<Object> assets = new();
    internal static readonly CRPRendererData[] SurfaceTemplates = new CRPRendererData[6];
    internal static EPC_SCLabel Prepare(Core core, EPC_SCLabel prototype, string directory, ManualLogSource logger)
    {
        log = logger;
        if (authoring != null) { authoring.gameObject.SetActive(true); return authoring; }
        string path = Path.Combine(directory, "graph-screen.mesh.json");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Graph screen asset is oversized.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 12 });
        PrefabId = new SCPrefab("SC_ACGraphScreen")._prefab;
        var go = Object.Instantiate((Object)prototype.gameObject, prototype.transform.parent, false).Cast<GameObject>();
        go.name = "SC_ACGraphScreen";
        authoring = go.GetComponent<EPC_SCLabel>();
        authoring._mirroredVersion = authoring;
        var donors = go.GetComponentsInChildren(Il2CppType.Of<EPC_Renderer>(), true);
        var donor = donors[0].Cast<EPC_Renderer>();
        var layer = donor._layer; var specular = donor._specular;
        foreach (var c in donors) Object.DestroyImmediate(c);
        foreach (var part in json.RootElement.GetProperty("parts").EnumerateArray())
        {
            var vs = part.GetProperty("vertices").EnumerateArray().Select(Vec).ToArray();
            var ns = part.GetProperty("normals").EnumerateArray().Select(Vec).ToArray();
            var ts = part.GetProperty("triangles").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var color = part.GetProperty("color").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (vs.Length is < 3 or > 65535 || ns.Length != vs.Length || ts.Length % 3 != 0 || ts.Any(i => i < 0 || i >= vs.Length) || color.Length != 4 || color.Any(v => !float.IsFinite(v)))
                throw new InvalidDataException("Invalid graph screen mesh.");
            var mesh = new Mesh { name = part.GetProperty("name").GetString()! };
            mesh.vertices = new Il2CppStructArray<Vector3>(vs); mesh.normals = new Il2CppStructArray<Vector3>(ns);
            mesh.triangles = new Il2CppStructArray<int>(ts); mesh.RecalculateBounds();
            Add(mesh, new Color(color[0],color[1],color[2],color[3]));
        }
        for (int i = 0; i < 6; i++)
        {
            var mesh = new Mesh { name = "GP04_Surface" + i };
            mesh.vertices = new Il2CppStructArray<Vector3>(new[] { Vector3.zero, Vector3.zero, Vector3.zero });
            mesh.triangles = new Il2CppStructArray<int>(new[] { 0,1,2 });
            mesh.bounds = new Bounds(new Vector3(0,.13f,0),new Vector3(.8f,.002f,.7f));
            Add(mesh, GraphSurface.Palette[i]);
        }
        var ports = new EPC_SpaceshipComponent.ElectricPortSetup[4];
        for (int i = 0; i < 4; i++) ports[i] = new() { _position = new float3(-.4375f,-.0625f,.1875f-i*.125f),
            _direction = EPC_SpaceshipComponent.ElectricPortSetup.Direction.XMinus, _type = SpaceshipPortType.DataInput };
        authoring._electricPorts = new Il2CppStructArray<EPC_SpaceshipComponent.ElectricPortSetup>(ports);
        authoring._paintableRenderers = new Il2CppReferenceArray<EntityPrefabComponent>(0);
        go.SetActive(true);
        return authoring;

        void Add(Mesh mesh, Color color)
        {
            assets.Add(mesh);
            var material = Object.Instantiate((Object)core._materialLit).Cast<Material>();
            assets.Add(material); material.name = mesh.name; material.color = color;
            var child = new GameObject(mesh.name); child.transform.SetParent(go.transform, false);
            var renderer = child.AddComponent<EPC_Renderer>();
            renderer._position = new float3(0); renderer._rotation = new float3(0); renderer._scale = new float3(1);
            renderer._mesh = mesh; renderer._material = material; renderer._submeshID = 0;
            renderer._layer = layer; renderer._color = color; renderer._specular = specular;
        }
    }
    private static Vector3 Vec(JsonElement e)
    {
        if (e.GetArrayLength() != 3) throw new InvalidDataException("Expected 3D vertex.");
        var v = new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        if (!float.IsFinite(v.x+v.y+v.z) || v.sqrMagnitude > 4) throw new InvalidDataException("Graph mesh vertex/normal out of bounds.");
        return v;
    }
    internal static bool IsAuthoring(EPC_SpaceshipComponent item) => authoring != null && item.Pointer == authoring.Pointer;
    internal static void AfterInitialize()
    {
        if (authoring == null) return;
        try
        {
            world = World.DefaultGameObjectInjectionWorld;
            var manager = world.EntityManager; manager.CompleteAllTrackedJobs();
            if (!EntityPrefabComponent.GameObjectToEntityMap.TryGetValue(authoring.gameObject, out prefab) || !manager.Exists(prefab) ||
                manager.GetComponentData<SCBlueprintClass>(prefab)._class != 35 || manager.HasComponent<SCTypeSignalProcessor>(prefab))
                throw new InvalidOperationException("Graph screen conversion failed.");
            var ports = manager.GetBuffer<SpaceshipElectricPortRef>(prefab, true);
            var links = manager.GetBuffer<LinkedEntityGroup>(prefab,true);
            for (int i=0;i<links.Length;i++)
            {
                var child=links[i].Value;
                if (!manager.HasComponent<CRPRendererData>(child)) continue;
                var data=manager.GetComponentData<CRPRendererData>(child);
                if (data._header._type!=CRPRendererData.Type.MeshReference) continue;
                var mesh=data.GetDrawDataMeshReference()._meshReference.Managed();
                if (mesh!=null && mesh.name.StartsWith("GP04_Surface",StringComparison.Ordinal)) SurfaceTemplates[mesh.name[12]-'0']=data;
            }
            if (ports.Length != 4) throw new InvalidOperationException("Graph screen requires four inputs.");
            for (int i = 0; i < ports.Length; i++)
            {
                var p = manager.GetComponentData<SpaceshipElectricPort>(ports[i]._portEntity);
                if (p._index != i || p._setupType != SpaceshipPortType.DataInput) throw new InvalidOperationException("Invalid screen port layout.");
                manager.SetComponentData(ports[i]._portEntity, new PortInfoData { _stringId = Core.RegisterStringAsID(i == 0 ? "Input 1 / Plot source" : "Input " + (i+1)) });
            }
            log?.LogInfo("GRAPH SCREEN REGISTERED: GP-04, four inputs, native class 35, six independent drawing layers.");
        }
        catch (Exception ex) { prefab = default; log?.LogError("Graph screen unavailable: " + ex); }
        finally { authoring.gameObject.SetActive(false); }
    }
    internal static bool IsScreen(EntityManager manager, Entity e) => world is { IsCreated: true } && prefab != Entity.Null &&
        manager.m_EntityDataAccess == world.EntityManager.m_EntityDataAccess && manager.Exists(e) && !manager.HasComponent<Prefab>(e) &&
        !manager.HasComponent<Disabled>(e) && manager.HasComponent<SCPrefab>(e) && manager.GetComponentData<SCPrefab>(e)._prefab == PrefabId;
    internal static PortTarget Target(EntityManager manager, Entity e)
    {
        if (!IsScreen(manager,e) || !manager.HasComponent<SCGuid>(e)) throw new InvalidOperationException("Not a live graph screen.");
        var guid = manager.GetComponentData<SCGuid>(e);
        if ((guid._a|guid._b|guid._c|guid._d)==0) throw new InvalidOperationException("Graph screen has no identity.");
        ComputerItem.ValidateLabel(manager,e,false);
        var ps = manager.GetBuffer<SpaceshipElectricPortRef>(e,true); var ls = manager.GetBuffer<LinkedEntityGroup>(e,true);
        if (ps.Length!=4 || ls.Length is < 5 or > 256) throw new InvalidOperationException("Invalid graph group.");
        var inputs = new Entity[4];
        for (int i=0;i<4;i++)
        {
            var p=ps[i]._portEntity; var d=manager.GetComponentData<SpaceshipElectricPort>(p); var owner=manager.GetComponentData<BelongingSC>(p);
            if (d._index!=i || d._setupType!=SpaceshipPortType.DataInput || owner._sc!=e || owner._linkedEntityGroupIndex>=ls.Length || ls[owner._linkedEntityGroupIndex].Value!=p)
                throw new InvalidOperationException("Foreign graph input.");
            inputs[i]=p;
        }
        return new PortTarget(guid.ToString(),Name,e,inputs,Array.Empty<Entity>()) { OwnerWorld=world };
    }
    internal static string? ReadId(EntityManager manager, Entity e) => ComputerItem.ParseId(manager.GetComponentData<ActionableLabelString>(ComputerItem.ValidateLabel(manager,e,false)),false);
    internal static void WriteId(EntityManager manager, Entity e, string id)
    {
        _=Target(manager,e); if(!ProgramStore.ValidId(id)) throw new InvalidDataException("Invalid graph settings ID.");
        var text=default(ActionableLabelString); for(int i=0;i<id.Length;i++) text.SetCharacter(i,id[i]);
        manager.SetComponentData(ComputerItem.ValidateLabel(manager,e,false),text);
    }

    internal static void Probe(EntityManager manager)
    {
        if (prefab==Entity.Null) throw new InvalidOperationException("Graph prefab missing.");
        var roots=new List<Entity>(); var owned=new HashSet<Entity>(); var surfaces=new List<GraphSurface>();
        try
        {
            for(int n=0;n<2;n++)
            {
                var e=manager.Instantiate(n==0?prefab:roots[0]); roots.Add(e);
                var links=manager.GetBuffer<LinkedEntityGroup>(e,true);
                for(int i=0;i<links.Length;i++) if(!owned.Add(links[i].Value)) throw new InvalidOperationException("Shared probe entity.");
                var s=new GraphSurface(manager,e); surfaces.Add(s);
                var f=new GraphFrame { Title="SCREEN "+n };
                f.Series[n]=new GraphSeries { Name="PROBE",Color=n,Points=new[]{new[]{0.0,0.0},new[]{1.0,n+1.0}}};
                s.Update(f,"NATIVE DISPLAY CHECK");
                if(s.VertexCount<100) throw new InvalidOperationException("Graph geometry is empty.");
            }
            log?.LogInfo("GRAPH SURFACE PROBE PASS: two independent ECS instances; native renderer binding, six meshes each and graph tessellation succeeded.");
        }
        finally
        {
            foreach(var s in surfaces) s.Dispose();
            foreach(var e in owned) if(manager.Exists(e) && manager.HasComponent<LinkedEntityGroup>(e)) manager.RemoveComponent<LinkedEntityGroup>(e);
            foreach(var e in owned) if(manager.Exists(e)) manager.DestroyEntity(e);
        }
    }
}
