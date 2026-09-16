using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ApproximatelyUp.ComputerMod;

internal sealed class GraphSurface : IDisposable
{
    internal static readonly Color[] Palette = { new(.04f,.08f,.09f), new(.82f,.92f,.90f),
        new(.18f,1f,.70f), new(1f,.58f,.10f), new(.32f,.68f,1f), new(1f,.36f,.66f) };
    private readonly World world;
    private readonly Entity[] children = new Entity[6];
    private readonly CRPRendererData[] original = new CRPRendererData[6];
    private readonly Mesh[] meshes = new Mesh[6];
    private Texture2D? texture;
    private GUIStyle? style;
    private List<GraphDrawing.Line> lines = new();
    private bool textureDirty = true;
    private GraphFrame? lastFrame;
    private string lastStatus = "";
    internal GraphSurface(EntityManager manager, Entity root)
    {
        world = manager.World;
        var links = manager.GetBuffer<LinkedEntityGroup>(root, true);
        for (int i = 0; i < links.Length; i++)
        {
            var child = links[i].Value;
            if (!manager.HasComponent<CRPRendererData>(child)) continue;
            var data = manager.GetComponentData<CRPRendererData>(child);
            if (data._header._type != CRPRendererData.Type.MeshReference) continue;
            var mesh = data.GetDrawDataMeshReference()._meshReference.Managed();
            if (mesh == null || !mesh.name.StartsWith("GP04_Surface", StringComparison.Ordinal)) continue;
            int slot = mesh.name[12] - '0';
            if (slot is < 0 or > 5 || children[slot] != Entity.Null) throw new InvalidOperationException("Ambiguous graph surface.");
            children[slot] = child;
            original[slot] = new CRPRendererData(GraphScreen.SurfaceTemplates[slot].GetDrawDataMeshReference()._meshReference, 0, data._material.Managed());
        }
        if (children.Any(e => e == Entity.Null)) throw new InvalidOperationException("Missing graph surface renderers.");
        try
        {
            for (int i = 0; i < 6; i++)
            {
                meshes[i] = new Mesh { name = "GP04_Surface" + i + "_" + root.Index + "_" + root.Version };
            }
        }
        catch { Dispose(); throw; }
    }
    internal void Update(GraphFrame frame, string status)
    {
        if (ReferenceEquals(frame,lastFrame) && status==lastStatus) return;
        lines = GraphDrawing.Build(frame, status); textureDirty = true;
        for (int color = 0; color < 6; color++)
        {
            var vertices = new List<Vector3>(); var indices = new List<int>();
            foreach (var l in lines)
            {
                if (l.Color != color) continue;
                float dx = l.X2 - l.X, dy = l.Y2 - l.Y, len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < .00001f) continue;
                float w = MathF.Max(l.Thickness * 2.5f, 3f);
                float ox = -dy / len * w * .5f, oy = dx / len * w * .5f;
                int n = vertices.Count;
                vertices.Add(Point(l.X + ox, l.Y + oy)); vertices.Add(Point(l.X2 + ox,l.Y2 + oy));
                vertices.Add(Point(l.X2 - ox,l.Y2 - oy)); vertices.Add(Point(l.X - ox,l.Y - oy));
                indices.AddRange(new[] { n, n + 2, n + 1, n, n + 3, n + 2, n, n + 1, n + 2, n, n + 2, n + 3 });
            }
            var mesh = meshes[color]; mesh.Clear();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = new Il2CppStructArray<Vector3>(vertices.ToArray());
            mesh.normals = new Il2CppStructArray<Vector3>(Enumerable.Repeat(Vector3.up, vertices.Count).ToArray());
            mesh.colors = new Il2CppStructArray<Color>(Enumerable.Repeat(Palette[color], vertices.Count).ToArray());
            mesh.triangles = new Il2CppStructArray<int>(indices.ToArray());
            mesh.bounds = new Bounds(new Vector3(0,.13f,0), new Vector3(.8f,.002f,.7f));
            var manager=world.EntityManager;
            var data=new CRPRendererData(mesh,0,original[color]._material.Managed());
            manager.SetComponentData(children[color],data);
            if (manager.HasComponent<CRPLocalBounds>(children[color]))
                manager.SetComponentData(children[color], new CRPLocalBounds { _localBounds = new Unity.Mathematics.AABB { Center = mesh.bounds.center, Extents = mesh.bounds.extents } });
            if (manager.HasComponent<CRPWorldBounds>(children[color]))
                manager.SetComponentData(children[color], CRPWorldBounds.Infinity);
            if (vertices.Count>0 && (data._header._type!=CRPRendererData.Type.MeshReference ||
                data.GetDrawDataMeshReference()._meshReference.Managed()?.Pointer!=mesh.Pointer))
                throw new InvalidOperationException("Native graph renderer did not bind its populated mesh.");
        }
        lastFrame=frame;lastStatus=status;
    }
    private static Vector3 Point(float x, float y) => new((x / GraphDrawing.Width - .5f) * .8f, .132f, (.5f - y / GraphDrawing.Height) * .7f);
    internal void Draw(Rect rect)
    {
        if (texture is null)
        {
            texture = new Texture2D(GraphDrawing.Width, GraphDrawing.Height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            style = new GUIStyle(); style.normal.background = texture;
        }
        if (textureDirty)
        {
            var bytes = new byte[GraphDrawing.Width * GraphDrawing.Height * 4];
            for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 3; bytes[i + 1] = 8; bytes[i + 2] = 10; bytes[i + 3] = 255; }
            foreach (var l in lines)
            {
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(l.X2-l.X),Math.Abs(l.Y2-l.Y))));
                var c = Palette[l.Color];
                for (int j = 0; j <= steps; j++)
                {
                    int x = (int)Math.Round(l.X + (l.X2-l.X)*j/steps), y = (int)Math.Round(l.Y + (l.Y2-l.Y)*j/steps);
                    for (int k = 0; k < (int)Math.Ceiling(l.Thickness); k++)
                    {
                        int py = y + k;
                        if (x < 0 || x >= GraphDrawing.Width || py < 0 || py >= GraphDrawing.Height) continue;
                        int at = ((GraphDrawing.Height - 1 - py) * GraphDrawing.Width + x) * 4;
                        bytes[at] = (byte)(c.r * 255); bytes[at+1] = (byte)(c.g * 255); bytes[at+2] = (byte)(c.b * 255);
                    }
                }
            }
            unsafe { fixed (byte* p = bytes) texture.LoadRawTextureData((IntPtr)p, bytes.Length); }
            texture.Apply(false, false); textureDirty = false;
        }
        GUI.Box(rect, "", style!);
    }
    public void Dispose()
    {
        if (world.IsCreated)
        {
            var manager = world.EntityManager; manager.CompleteAllTrackedJobs();
            for (int i = 0; i < 6; i++)
                if (meshes[i] != null && manager.Exists(children[i]) && manager.HasComponent<CRPRendererData>(children[i]))
                    manager.SetComponentData(children[i], original[i]);
        }
        foreach (var mesh in meshes) if (mesh != null) { MeshReference.FreeReference(mesh); Object.Destroy(mesh); }
        if (texture != null) Object.Destroy(texture);
    }
    internal int VertexCount => meshes.Where(m => m != null).Sum(m => m.vertexCount);
}
