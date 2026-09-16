using Unity.Entities;
using UnityEngine;

namespace ApproximatelyUp.ComputerMod;

// Opt-in, owned-process, main-menu diagnostic. No saved world is loaded.
internal sealed class GraphVisualProbe
{
    private readonly List<Entity> owned = new();
    private readonly List<GraphSurface> surfaces = new();
    private World? world;
    private long began;
    private int phase;
    private readonly long startAfter = Environment.TickCount64 + 10000;
    internal void Tick(EntityManager manager, ComputerEditor editor)
    {
        if (phase == 3) return;
        if (phase == 0)
        {
            if (Environment.TickCount64 < startAfter) return;
            if (UIManager._singleton?._mainMenu?.gameObject.activeInHierarchy != true || Utility.HasSingleton<SpaceshipSingleton>(manager))
                throw new InvalidOperationException("Graph visual probe is menu-only.");
            if (GraphScreen.PrefabEntity == Entity.Null) throw new InvalidOperationException("Graph prefab missing.");
            world = manager.World;
            for (int n = 0; n < 2; n++)
            {
                var root = manager.Instantiate(n == 0 ? GraphScreen.PrefabEntity : owned[0]);
                var links = manager.GetBuffer<LinkedEntityGroup>(root, true);
                for (int i = 0; i < links.Length; i++) owned.Add(links[i].Value);
                if (manager.HasComponent<SCGuid>(root)) manager.RemoveComponent<SCGuid>(root);
                var surface = new GraphSurface(manager, root);
                surfaces.Add(surface);
                surface.Update(Frame(n), n == 0 ? "NATIVE MODEL AND DYNAMIC GRAPH" : "INDEPENDENT SCREEN B");
                if (surface.VertexCount < 100) throw new InvalidOperationException("Graph geometry is empty.");
            }
            editor.OpenPanel((w, h) =>
            {
                GUI.Label(new Rect(12, 6, w - 24, 30), "GP-04 / TWO INDEPENDENT GRAPH SURFACES");
                float width = (w - 36) / 2;
                for (int i = 0; i < 2; i++) surfaces[i].Draw(new Rect(12 + i * (width + 12), 55, width, width * .75f));
            });
            phase = 1; began = Environment.TickCount64;
            Plugin.Current?.Log.LogInfo("GRAPH VISUAL WORLD READY");
            Plugin.Current?.Log.LogInfo("GRAPH VISUAL UI READY");
        }
        if (phase == 1 && Environment.TickCount64 - began > 6000)
        { editor.HidePreservingDraft(); Cleanup(); phase = 3; Plugin.Current?.Log.LogInfo("GRAPH VISUAL COMPLETE"); }
    }
    private static GraphFrame Frame(int n)
    {
        var f = new GraphFrame { Title = "SCREEN " + (n == 0 ? "A" : "B"), XLabel = "TIME", YLabel = "VALUE" };
        for (int ch = 0; ch < 4; ch++) f.Series[ch] = new GraphSeries { Name = "CHANNEL " + (ch + 1), Color = ch,
            Points = Enumerable.Range(0, 80).Select(i => new[] { i * .1, Math.Sin(i * .11 + ch + n * 2) * (ch + 1) }).ToArray() };
        return f;
    }
    internal void Cleanup()
    {
        foreach (var s in surfaces) s.Dispose(); surfaces.Clear();
        if (world is { IsCreated: true })
        {
            var m = world.EntityManager; m.CompleteAllTrackedJobs();
            foreach (var e in owned) if (m.Exists(e) && m.HasComponent<LinkedEntityGroup>(e)) m.RemoveComponent<LinkedEntityGroup>(e);
            foreach (var e in owned) if (m.Exists(e)) m.DestroyEntity(e);
        }
        owned.Clear();
    }
}
