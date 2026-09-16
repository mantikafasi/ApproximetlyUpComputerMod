using System.Security.Cryptography;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ApproximatelyUp.ComputerMod;

[BepInPlugin(Id, "ApproximetlyComputers", "0.5.0")]
public sealed class Plugin : BasePlugin
{
    public const string Id = "local.approximatelyup.computer";
    internal static Plugin? Current;
    private readonly Harmony harmony = new(Id);
    private readonly int mainThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<(int, int), Computer> computers = new();
    private readonly HashSet<(int, int)> clearOutputs = new();
    private World? world;
    private string worldKey = "";
    private long editorWorldGeneration;
    private PortTarget? selected, reloadRequest, runRequest;
    private string? runExpectedId;
    private SaveRequest? saveRequest;
    private bool startupSeen, hookSeen, failed;
    private int lastWorldTick = int.MinValue;
    private ComputerHud? hud;
    private ComputerEditor? editor;
    private ProgramStore store = null!;
    private ComputerLink link = null!;
    private GraphScreens graphs = null!;
    private readonly GraphVisualProbe? graphVisualProbe = Environment.GetEnvironmentVariable("APPROX_GRAPH_PROBE") == "1" ? new() : null;
    private string linkSession = "";
    private bool remotePeers;
    private long nextLinkPoll, nextSourcePoll, remoteDeadline, nextNetworkHeartbeat;
    private ComputerMessage? remoteCommand, remotePending;
    private (Entity Entity, uint Version)? flight;
    private readonly HashSet<(int, int, string)> autoAttempted = new();
    private bool exitPending, autoStartSuppressed;
    private long nextAutoScan;
    private readonly bool diagnosticProbe = Environment.GetEnvironmentVariable("APPROX_COMPUTER_PROBE") == "1";
    private readonly bool editorProbe = Environment.GetEnvironmentVariable("APPROX_COMPUTER_EDITOR") == "1";
    private bool editorProbeOpened;
    private readonly bool instanceProbe = Environment.GetEnvironmentVariable("APPROX_COMPUTER_INSTANCE") == "1";
    private bool instanceProbeDone;
    private double tickMilliseconds;

    private sealed class Computer
    {
        internal PortTarget Target;
        internal LuaComputer? Vm;
        internal bool Native;
        internal int Tick = int.MinValue;
        internal long Ticks;
        internal string Source = "";
        internal string? ProgramId;
        internal string? SourceId;
        internal bool SourceReady, RemoteRunning, NetworkPending, LabelPending;
        internal long NetworkRetryAt;
        internal ComputerMessage? RemoteState;
        internal double[] Inputs = Array.Empty<double>(), Outputs = Array.Empty<double>();
        internal Computer(PortTarget target, bool native) { Target = target; Native = native; }
    }
    private sealed record SaveRequest(PortTarget Target, string Source, string Expected, string? ExpectedId, bool Run);
    private static (int, int) Key(PortTarget target) => (target.Component.Index, target.Component.Version);
    private Computer? Selected => selected is null ? null : computers.GetValueOrDefault(Key(selected));
    internal string StarterSource => ProgramStore.StarterSource;
    internal string EditorScope => $"{editorWorldGeneration}:{link.IsClient}:{link.SourceAuthority}";
    internal PortTarget? SelectedTarget => selected;
    internal string SelectedSource => Selected?.Source ?? "";
    internal bool SelectedIsComputer => Selected?.Native == true;
    internal bool IsSelectedRunning => link.IsClient ? Selected?.RemoteRunning == true : Selected?.Vm is not null;
    internal bool SourceReady => selected is null || Selected?.SourceReady == true;
    internal bool CommandPending => remoteCommand is not null || remotePending is { Kind: not "get" };
    internal string NetworkStatus => link.Status;
    internal string Status { get; private set; } = "Waiting for simulation.";

    public override void Load()
    {
        CheckHash("GameAssembly.dll", ComputerPacket.GameHash);
        CheckHash("ApproximatelyUp_Data/il2cpp_data/Metadata/global-metadata.dat", "55842F242AA732F75DFBEBB2F90E449984C033AFF748AB5DA8E8805DDE2E3370");
        LuaComputer.SelfTest();
        ComputerEditorBuffer.SelfTest();
        Log.LogInfo("Lua CLR smoke test passed: input 3 -> output 6. Editor buffer checks passed.");
        string directory = Path.Combine(Paths.PluginPath, "ApproximatelyUpComputer");
        store = new ProgramStore(directory);
        link = new ComputerLink(text => Log.LogWarning(text));
        graphs = new GraphScreens(this, directory);
        ComputerNetwork.ResetForWorld();
        Current = this;
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ComponentSystemGroup), "UpdateAllSystems") ?? throw new MissingMethodException("UpdateAllSystems"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterGroup)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(UIManager), "Update") ?? throw new MissingMethodException("UIManager.Update"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(Controls)));
            ComputerItem.Install(harmony, Log, Path.Combine(directory, "assets"));
            ComputerEditor.Install(harmony);
            editor = new ComputerEditor(this);
            ComputerInteraction.Install(harmony, this);
            foreach (var method in new[] {
                AccessTools.DeclaredMethod(typeof(SpaceshipSystem), "DestroySpaceship"),
                AccessTools.DeclaredMethod(typeof(Core), "ClearGame", new[] { typeof(bool), typeof(bool) }),
                AccessTools.DeclaredMethod(typeof(Core), "Dispose") })
                harmony.Patch(method ?? throw new MissingMethodException("Native flight teardown"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeNativeExit)) { priority = Priority.First });
            hud = AddComponent<ComputerHud>();
        }
        catch { harmony.UnpatchSelf(); Current = null; throw; }
        Log.LogInfo("Prototype loaded DISARMED in build/menu mode. AU-08s automatically start host programs on entering game mode and stop on exit. E edits without running.");
    }
    private static void CheckHash(string path, string expected)
    {
        using var file = File.OpenRead(Path.Combine(Paths.GameRootPath, path));
        using var sha = SHA256.Create();
        if (Convert.ToHexString(sha.ComputeHash(file)) != expected) throw new InvalidOperationException("Unsupported game build: " + path);
    }
    private static void AfterGroup(ComponentSystemGroup __instance)
    {
        var plugin = Current;
        if (plugin is null || plugin.failed) return;
        try
        {
            if (__instance.TryCast<SpaceshipComponentsTickPostGroup>() is null) return;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            try { plugin.OnTick(__instance); }
            finally { plugin.tickMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        }
        catch (Exception ex)
        {
            plugin.failed = true;
            plugin.StopAll("Native integration stopped: " + Short(ex), false);
            plugin.Log.LogError(ex);
        }
    }
    private void OnTick(ComponentSystemGroup group)
    {
        if (Environment.CurrentManagedThreadId != mainThread) throw new InvalidOperationException("Wrong simulation thread.");
        if (!hookSeen) { hookSeen = true; Log.LogInfo("Post-physics UpdateAllSystems hook reached on the main thread."); }
        var active = group.World;
        if (active is null || !active.IsCreated) return;
        var save = Core.Get()?._save?._world;
        string key = save is null ? "" : save._guid.ToString();
        if (world is null || world.Pointer != active.Pointer || worldKey != key)
        {
            StopAll("World changed; all computers disarmed. Saved source retained.", false);
            computers.Clear(); selected = reloadRequest = null;
            world = active; worldKey = key; lastWorldTick = int.MinValue;
            editorWorldGeneration++;
            flight = null; exitPending = autoStartSuppressed = false; autoAttempted.Clear(); nextAutoScan = 0;
            link.ResetForWorld(); ComputerNetwork.ResetForWorld();
            remoteCommand = remotePending = null; linkSession = ""; nextLinkPoll = nextSourcePoll = 0;
            editor?.HidePreservingDraft();
            graphs.Reset();
        }
        if (!startupSeen)
        {
            startupSeen = true;
            if (diagnosticProbe)
            {
                var m = active.EntityManager;
                m.CompleteAllTrackedJobs();
                bool allowed = CheckSession(m, out string details, true);
                Log.LogInfo($"READ-ONLY PROBE PASS: tick={ReadSingleton<UniverseCoreSingleton>(m)._physicsTickID}, allowed={allowed}; {details}");
                Log.LogInfo("INPUT POINTER PROBE: " + ComputerInteraction.ProbeInputPointers(m));
            }
            SetStatus("Simulation ready. Aim at AU-08 and press E to edit. Native world self-test disabled.");
        }
        if (instanceProbe && !instanceProbeDone && ComputerItem.IsRegistered)
        {
            var menu = UIManager._singleton?._mainMenu;
            if (menu is not null && menu.gameObject.activeInHierarchy)
            {
                instanceProbeDone = true;
                var m = active.EntityManager;
                m.CompleteAllTrackedJobs();
                if (!CheckSession(m, out string details, true)) throw new InvalidOperationException("Instance diagnostic requires solo authority: " + details);
                ComputerNetwork.ProbeBindings(m);
                Log.LogInfo("COMPUTER NETWORK BINDINGS PASS: native port and label factories create/decode/dispose without enqueue.");
                Log.LogInfo("COMPUTER LINK BINDINGS PASS: " + link.ProbeBindings(m));
                ComputerItem.ProbeInstance(m, ReadSingleton<Core.Singleton>(m));
                GraphScreen.Probe(m);
            }
        }
        long now = Environment.TickCount64;
        var manager = active.EntityManager;
        manager.CompleteAllTrackedJobs();
        if(graphVisualProbe is not null && editor is not null)
        {
            try { graphVisualProbe.Tick(manager, editor); }
            catch { graphVisualProbe.Cleanup(); throw; }
        }
        RefreshGameMode(manager);
        if (now < nextLinkPoll && remoteCommand is null && reloadRequest is null && runRequest is null && saveRequest is null &&
            clearOutputs.Count == 0 && !computers.Values.Any(c => c.Vm is not null || c.NetworkPending || c.LabelPending) &&
            (flight is null || exitPending || autoStartSuppressed || now < nextAutoScan) && !graphs.Due(now)) return;
        int currentTick = ReadSingleton<UniverseCoreSingleton>(manager)._physicsTickID;
        bool forwardTick = currentTick > lastWorldTick;
        float speed = ReadSingleton<Core.Singleton>(manager)._simulationSpeed;
        bool simulationAdvancing = forwardTick && float.IsFinite(speed) && speed > 0;
        bool rewinding = currentTick < lastWorldTick;
        if (rewinding)
        {
            StopAll("Rewind detected. All VMs reset; native restored values left intact.", false);
            autoStartSuppressed = true;
            ComputerNetwork.ResetForWorld(); link.ResetForWorld(); nextLinkPoll = 0;
            remoteCommand = remotePending = null;
        }
        lastWorldTick = currentTick;
        // Poll the optional editor channel at 20 Hz with peers, 4 Hz in solo. Lua still uses native ticks.
        if (now >= nextLinkPoll || remoteCommand is not null)
        {
            link.Poll(manager, worldKey, (peer, message) => ReceiveRemote(manager, peer, message));
            ObserveLinkSession();
            nextLinkPoll = now + (link.IsClient || link.HasPeers ? 50 : 250);
            if (link.IsClient) PumpRemote(manager, now);
        }
        try { graphs.Tick(manager, link, simulationAdvancing, flight is not null && !exitPending, currentTick, Core.PhysicsDeltaTime, rewinding); }
        catch (Exception ex) { SetStatus("Graph update deferred: " + Short(ex)); }
        if (reloadRequest is { } choice)
        {
            reloadRequest = null;
            try
            {
                Ports.ValidateTarget(manager, choice);
                var c = GetComputer(manager, choice);
                LoadSource(manager, c);
                SetStatus("Computer source reloaded.");
            }
            catch (Exception ex) { SetStatus("Selection/source: " + Short(ex)); }
        }
        if (simulationAdvancing && flight is not null && !exitPending && !autoStartSuppressed && now >= nextAutoScan)
            AutoStart(manager, now);
        if (!link.IsClient && link.HasPeers && now >= nextNetworkHeartbeat)
        {
            nextNetworkHeartbeat = now + 1000;
            foreach (var c in computers.Values)
                if (c.Native && c.Outputs.Length == c.Target.Outputs.Length) c.NetworkPending = true;
        }
        bool wantsWrite = saveRequest is not null || runRequest is not null || clearOutputs.Count != 0 ||
            computers.Values.Any(c => c.Vm is not null || c.NetworkPending || c.LabelPending);
        if (!wantsWrite) return;
        try
        {
            if (!CheckSession(manager, out string session)) { StopAll("Lua executes on the host only. " + session, false); return; }
            int tick = currentTick;
            foreach (var k in clearOutputs.ToArray())
            {
                clearOutputs.Remove(k);
                if (computers.TryGetValue(k, out var c))
                    try { CommitOutputs(manager, c, new double[c.Target.Outputs.Length]); }
                    catch (Exception ex) { SetStatus("Stopped; output clear unavailable: " + Short(ex)); }
            }
            if (saveRequest is { } request)
            {
                saveRequest = null;
                try
                {
                    SaveProgram(manager, request.Target, request.Source, request.ExpectedId, request.Expected);
                    if (request.Run) { runRequest = request.Target; runExpectedId = ProgramId(manager, request.Target); }
                }
                catch (Exception ex) { SetStatus("Save failed; draft retained: " + Short(ex)); }
            }
            if (runRequest is { } run)
            {
                runRequest = null;
                try
                {
                    if (ProgramId(manager, run) != runExpectedId) throw new IOException("Queued Run's program reference changed; run the new source explicitly.");
                    RunComputer(manager, run);
                }
                catch (Exception ex)
                {
                    if (computers.TryGetValue(Key(run), out var old)) StopComputer(old, true);
                    SetStatus("Stopped; run refused: " + Short(ex));
                }
            }
            foreach (var c in computers.Values)
            {
                if (c.Vm is null) continue;
                try
                {
                    if (flight is null || exitPending || !Ports.IsFlightMember(manager, c.Target))
                    {
                        StopComputer(c, false); c.NetworkPending = c.LabelPending = false; c.Outputs = Array.Empty<double>();
                        continue;
                    }
                    if (!simulationAdvancing || c.Tick == tick || !Ports.IsActive(manager, c.Target)) continue;
                    if (c.Native && ComputerItem.GetProgramId(manager, c.Target.Component) != c.ProgramId)
                        throw new InvalidOperationException("Program reference changed; rearm explicitly to load it.");
                    c.Inputs = Ports.ReadInputs(manager, c.Target);
                    var values = c.Vm.Tick(c.Inputs, Core.PhysicsDeltaTime, tick);
                    CommitOutputs(manager, c, values);
                    c.Tick = tick; c.Ticks++;
                    if (c.Ticks == 1) Log.LogInfo($"First selected-block Lua output committed: {string.Join(", ", values)}; inputs: {string.Join(", ", c.Inputs)}; {session}");
                }
                catch (Exception ex) { StopComputer(c, true); SetStatus(c.Target.Name + " stopped: " + Short(ex)); }
            }
            foreach (var c in computers.Values)
            {
                if (!remotePeers) { c.NetworkPending = c.LabelPending = false; continue; }
                if ((!c.NetworkPending && !c.LabelPending) || now < c.NetworkRetryAt) continue;
                try
                {
                    if (c.LabelPending) { ComputerNetwork.PublishProgramId(manager, c.Target); c.LabelPending = false; }
                    if (c.NetworkPending) c.NetworkPending = !ComputerNetwork.PublishOutputs(manager, c.Target, c.Outputs);
                }
                catch (Exception ex)
                {
                    if (!manager.Exists(c.Target.Component)) { StopComputer(c, false); c.NetworkPending = c.LabelPending = false; }
                    else if (c.Vm is not null) StopComputer(c, true);
                    c.NetworkRetryAt = now + 1000;
                    SetStatus("Computer sync deferred; host VM stopped: " + Short(ex));
                }
            }
        }
        catch (Exception ex) { StopAll("Computer session stopped: " + Short(ex), false); }
    }
    private Computer GetComputer(EntityManager manager, PortTarget target)
    {
        Ports.ValidateTarget(manager, target);
        if (!ComputerItem.IsComputer(manager, target.Component)) throw new InvalidOperationException("Only AU-08 computers can run Lua.");
        if (computers.TryGetValue(Key(target), out var c))
        {
            if (c.Target.Guid != target.Guid) throw new InvalidOperationException("Computer identity changed; aim at it and press E again.");
            c.Target = target;
            return c;
        }
        if (computers.Count >= Ports.MaxTargets) throw new InvalidOperationException("Computer session limit reached; reload the world to clear stale selections.");
        c = new Computer(target, true);
        computers.Add(Key(target), c);
        return c;
    }
    private string? ProgramId(EntityManager manager, PortTarget target, bool allowInvalid = false) =>
        ComputerItem.GetProgramId(manager, target.Component, allowInvalid);
    private string ReadSource(EntityManager manager, PortTarget target, bool allowMissing)
    {
        string? id;
        try { id = ProgramId(manager, target); }
        catch (InvalidDataException) when (allowMissing) { return ""; }
        try { return id is null ? store.ReadTemplate() : store.Load(id); }
        catch (IOException ex) when (allowMissing && ex is FileNotFoundException or DirectoryNotFoundException) { return ""; }
    }
    private void LoadSource(EntityManager manager, Computer c)
    {
        if (!ReadSingleton<Netcore.Singleton>(manager)._isServer)
        {
            c.SourceReady = false; nextSourcePoll = 0;
            SetStatus("Waiting for host source. Client-local scripts are never substituted.");
            return;
        }
        c.Source = ReadSource(manager, c.Target, true);
        c.SourceId = ProgramId(manager, c.Target, true); c.SourceReady = true;
    }
    private void RequireHost(EntityManager manager, Computer c)
    {
        if (!CheckSession(manager, out string session) || (remotePeers && !c.Native))
            throw new InvalidOperationException("Host authority and an AU-08 are required in multiplayer. " + session);
    }
    private void SaveProgram(EntityManager manager, PortTarget target, string source, string? expectedId, string? expectedSource = null)
    {
        var c = GetComputer(manager, target);
        RequireHost(manager, c);
        ProgramStore.Validate(source);
        string current = ReadSource(manager, target, true);
        string? before = ProgramId(manager, target, true);
        if (before != expectedId || (expectedSource is not null && current != expectedSource))
        {
            c.Source = current; c.SourceId = before;
            throw new IOException("Source changed since this draft was loaded. Keep the draft and reload before saving.");
        }
        string id = store.Create(source);
        ComputerItem.SetProgramId(manager, target.Component, id);
        c.Source = source; c.SourceId = id; c.SourceReady = true;
        // A queued Run authorized the previous revision, not this newly saved source.
        if (runRequest is not null && Key(runRequest) == Key(target)) runRequest = null;
        StopComputer(c, true);
        c.LabelPending = c.Native && remotePeers;
        SetStatus("Saved on host as " + id + "; template untouched." + (c.LabelPending ? " Native reference sync queued." : ""));
    }
    private void RunComputer(EntityManager manager, PortTarget target)
    {
        var c = GetComputer(manager, target);
        RequireHost(manager, c);
        if (flight is null || exitPending || !Ports.IsFlightMember(manager, target))
            throw new InvalidOperationException("Save in build mode; programs run automatically after entering game mode on a launched ship.");
        float speed = ReadSingleton<Core.Singleton>(manager)._simulationSpeed;
        if (!float.IsFinite(speed) || speed <= 0 || !Ports.IsActive(manager, target))
            throw new InvalidOperationException("Resume simulation and activate this ship part before running.");
        if (c.Vm is null && computers.Values.Count(x => x.Vm is not null) >= 8) throw new InvalidOperationException("Eight-computer execution limit reached.");
        string source = ReadSource(manager, target, false);
        var vm = new LuaComputer(source, target.Inputs.Length, target.Outputs.Length);
        c.Vm = vm; c.Source = source; c.ProgramId = c.SourceId = ProgramId(manager, target);
        c.SourceReady = true; c.Tick = int.MinValue; c.Ticks = 0;
        clearOutputs.Remove(Key(target));
        SetStatus("RUNNING " + target.Name + " on host. F8 stops selected; F9 stops all.");
    }
    private void CommitOutputs(EntityManager manager, Computer c, double[] values)
    {
        Ports.WriteOutputs(manager, c.Target, values);
        c.Outputs = values; c.NetworkPending = c.Native && remotePeers;
    }
    private void RefreshGameMode(EntityManager manager)
    {
        (Entity Entity, uint Version)? current = Ports.TryGetFlight(manager, out var entity, out uint version) ? (entity, version) : null;
        if (current == flight) return;
        bool entering = current is not null;
        StopAll(entering ? "Game mode entered; discovering AU-08 programs automatically." :
            "Game mode exited; VMs stopped. Native reset owns restored port values.", false);
        ComputerNetwork.ResetForWorld();
        remoteCommand = remotePending = null;
        flight = current; exitPending = autoStartSuppressed = false;
        autoAttempted.Clear(); nextAutoScan = 0;
    }
    private static void BeforeNativeExit()
    {
        if (Current is not { } p || Environment.CurrentManagedThreadId != p.mainThread) return;
        // Cancel managed intent BEFORE native restoration, even when physics is paused.
        if (!p.exitPending) p.StopAll("Native game-mode exit: computers stopped before restoration.", false);
        p.exitPending = p.autoStartSuppressed = true;
        p.remoteCommand = p.remotePending = null;
        p.graphs.StopFlight();
        ComputerNetwork.ResetForWorld();
    }
    private void AutoStart(EntityManager manager, long now)
    {
        nextAutoScan = now + 1000;
        if (!CheckSession(manager, out _)) return;
        try
        {
            foreach (var dead in computers.Where(p => !manager.Exists(p.Value.Target.Component)).Select(p => p.Key).ToArray())
            { computers.Remove(dead); clearOutputs.Remove(dead); }
            var found = Ports.FindFlightComputers(manager);
            foreach (var target in found)
            {
                if (!autoAttempted.Add((target.Component.Index, target.Component.Version, target.Guid))) continue;
                try
                {
                    var c = GetComputer(manager, target);
                    if (c.Vm is null) RunComputer(manager, target);
                }
                catch (Exception ex) { SetStatus($"AU-08 [{target.Component.Index}:{target.Component.Version}] auto-start refused: {Short(ex)}"); }
            }
        }
        catch (Exception ex) { autoStartSuppressed = true; SetStatus("Automatic discovery stopped for this flight: " + Short(ex)); }
    }
    private void ObserveLinkSession()
    {
        if (linkSession == link.Session) return;
        linkSession = link.Session;
        ComputerNetwork.ResetForWorld();
        remoteCommand = remotePending = null; nextSourcePoll = 0;
        foreach (var c in computers.Values)
        {
            c.RemoteState = null; c.RemoteRunning = false;
            if (link.IsClient) { c.SourceReady = false; StopComputer(c, false); }
        }
    }
    private void QueueRemote(string kind, string? source = null, bool run = false)
    {
        if (!link.Ready) { SetStatus(link.Status); return; }
        bool stop = kind is "stop" or "stop-all";
        if (CommandPending && !stop) { SetStatus("A host command is pending. Wait for its reply."); return; }
        if (kind != "stop-all" && (Selected is not { Native: true } || !SourceReady && kind is "save" or "run"))
        { SetStatus("Select an AU-08 and wait for its host source first."); return; }
        remotePending = null; // A read-only refresh may be superseded by an explicit command.
        remoteCommand = new ComputerMessage { Kind = kind, Target = kind == "stop-all" ? "" : selected!.Guid,
            ProgramId = kind == "stop-all" ? null : Selected!.SourceId, Source = source, Run = run };
    }
    private void PumpRemote(EntityManager manager, long now)
    {
        if (remotePending is not null && now >= remoteDeadline)
        {
            SetStatus("Host reply timed out; outcome unknown. Draft retained. Requests are not automatically repeated.");
            remotePending = null;
        }
        foreach (var c in computers.Values)
        {
            if (!c.Native) continue;
            try
            {
                Ports.ValidateTarget(manager, c.Target);
                string? id = ProgramId(manager, c.Target);
                if (c.SourceId != id) c.SourceReady = false;
                if (c.RemoteState is { } state && state.ProgramId == id)
                {
                    c.Source = state.Source!; c.SourceId = id; c.SourceReady = true;
                    c.RemoteRunning = state.Running; c.Inputs = state.Inputs; c.Outputs = state.Outputs;
                    c.RemoteState = null;
                }
            }
            catch { c.SourceReady = false; c.RemoteRunning = false; c.RemoteState = null; }
        }
        if (!link.Ready) return;
        ComputerMessage? send = remoteCommand;
        remoteCommand = null;
        if (send is null && remotePending is null && now >= nextSourcePoll && Selected is { Native: true } selectedComputer)
            send = new ComputerMessage { Kind = "get", Target = selectedComputer.Target.Guid, ProgramId = selectedComputer.SourceId };
        if (send is null) return;
        nextSourcePoll = now + 1000;
        try
        {
            if (!link.SendToHost(send)) { SetStatus(link.Status); return; }
            remotePending = send; remoteDeadline = now + 10000;
            if (send.Kind != "get") SetStatus("Sent " + send.Kind + " to host; awaiting acknowledgement.");
        }
        catch (Exception ex) { SetStatus("Host request not sent; draft retained: " + Short(ex)); }
    }
    private void ReceiveRemote(EntityManager manager, ulong peer, ComputerMessage message)
    {
        ObserveLinkSession();
        if (message.Kind.StartsWith("graph-", StringComparison.Ordinal)) { graphs.Receive(manager, link, peer, message); return; }
        if (link.IsClient)
        {
            if (remotePending is not { } pending || pending.Id != message.Id || pending.Target != message.Target) return;
            remotePending = null;
            if (message.Kind == "error") { SetStatus("Host refused request; draft retained: " + message.Error); return; }
            if (message.Target.Length != 0)
            {
                var c = computers.Values.SingleOrDefault(c => c.Native && c.Target.Guid == message.Target);
                if (c is null || message.Source is null) return;
                // Native label events and source replies are different reliable streams.
                // Keep the snapshot pending until its native program reference is observed.
                c.RemoteState = message;
                c.SourceReady = false;
            }
            if (message.Error is not null) SetStatus(message.Error);
            else if (pending.Kind != "get") SetStatus("Host acknowledged " + pending.Kind + ". Waiting for matching native state if needed.");
            return;
        }
        try
        {
            if (!CheckSession(manager, out _)) throw new InvalidOperationException("Host authority unavailable.");
            if (message.Kind == "stop-all")
            {
                autoStartSuppressed = true;
                StopAll("Trusted teammate stopped all computers.", true);
                link.SendToPeer(peer, new ComputerMessage { Kind = "state", Id = message.Id });
                return;
            }
            var target = computers.Values.FirstOrDefault(c => c.Native && c.Target.Guid == message.Target)?.Target ??
                Ports.FindTargets(manager, message.Target).SingleOrDefault();
            if (target is null || !ComputerItem.IsComputer(manager, target.Component))
                throw new InvalidOperationException("AU-08 not found in this world; aim at it and press E again.");
            var c = GetComputer(manager, target);
            ComputerNetwork.ValidateNetworkTarget(manager, target);
            if (message.Kind is "save" or "run" && ProgramId(manager, target) != message.ProgramId)
                throw new IOException("Program reference changed. Reload before saving or running.");
            if (message.Kind == "save") SaveProgram(manager, target, message.Source!, message.ProgramId);
            string? runFailure = null;
            if (message.Kind == "run" || message.Kind == "save" && message.Run)
            {
                try { RunComputer(manager, target); }
                catch (Exception ex)
                {
                    StopComputer(c, true);
                    if (message.Kind != "save") throw;
                    runFailure = "Saved on host as " + c.SourceId + ", but Run failed: " + Short(ex);
                    if (runFailure.Length > 256) runFailure = runFailure[..256];
                }
            }
            if (message.Kind == "stop")
            {
                StopComputer(c, true);
                if (runRequest is not null && Key(runRequest) == Key(target)) runRequest = null;
                if (saveRequest is { } saving && Key(saving.Target) == Key(target)) saveRequest = saving with { Run = false };
            }
            string source = ReadSource(manager, target, false);
            try { c.Inputs = Ports.ReadInputs(manager, target); }
            catch { c.Inputs = Array.Empty<double>(); }
            link.SendToPeer(peer, new ComputerMessage { Kind = "state", Id = message.Id, Target = target.Guid,
                ProgramId = ProgramId(manager, target), Source = source, Running = c.Vm is not null,
                Inputs = c.Inputs, Outputs = c.Outputs, Error = runFailure });
        }
        catch (Exception ex)
        {
            link.SendToPeer(peer, new ComputerMessage { Kind = "error", Id = message.Id, Target = message.Target, Error = Short(ex) });
        }
    }
    private void RequestStopAll()
    {
        if (link.IsClient) QueueRemote("stop-all");
        else { autoStartSuppressed = true; StopAll("All computers stopped until explicit Run or the next game-mode entry.", true); }
    }
    internal void RequestReload()
    {
        if (selected is null) return;
        if (Selected is { } c) c.SourceReady = false;
        reloadRequest = selected;
    }

    internal bool OpenComputerEditor(EntityManager manager, Entity component)
    {
        if (Environment.CurrentManagedThreadId != mainThread || failed || editor is null || ComputerEditor.CapturesInput ||
            world is null || !world.IsCreated || world.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess ||
            !ComputerItem.IsComputer(manager, component)) return false;
        var save = Core.Get()?._save?._world;
        if ((save is null ? "" : save._guid.ToString()) != worldKey) return false;
        var target = Ports.GetTarget(manager, component);
        if (target is null) return false;
        var c = GetComputer(manager, target);
        LoadSource(manager, c);
        selected = target;
        reloadRequest = null;
        editor.Toggle();
        Log.LogInfo($"COMPUTER INTERACT: opened targeted {target.Name} [{target.Component.Index}:{target.Component.Version}], GUID={target.Guid}; source not executed.");
        return editor.IsOpen;
    }
    internal bool OpenGraphScreen(EntityManager manager, Entity component) => editor is not null && !failed && graphs.Open(manager, component, editor);
    internal void CloseGraphView() => editor?.Close();
    internal bool CanGraphWrite(EntityManager manager) => CheckSession(manager, out _);
    internal GraphFrame? GraphAt(EntityManager manager, Entity output)
    {
        if (!manager.Exists(output) || !manager.HasComponent<BelongingSC>(output)) return null;
        var owner = manager.GetComponentData<BelongingSC>(output)._sc;
        if (!computers.TryGetValue((owner.Index, owner.Version), out var c) || c.Vm is null ||
            flight is null || exitPending || !Ports.IsFlightMember(manager, c.Target) || !Ports.IsActive(manager, c.Target)) return null;
        Ports.ValidateTarget(manager, c.Target);
        if (ProgramId(manager, c.Target) != c.ProgramId) return null;
        int index = Array.IndexOf(c.Target.Outputs, output);
        if (index < 0) return null;
        if (c.Vm.Graphs.TryGetValue(index + 1, out var frame)) return frame;
        if (c.Vm.Graphs.TryGetValue(1, out frame)) return frame;
        foreach (var extra in c.Vm.Graphs.Values) return extra;
        return null;
    }
    internal void RequestRun()
    {
        if (selected is null) { SetStatus("Aim at a computer and press E first."); return; }
        if (link.IsClient) { QueueRemote("run"); return; }
        runRequest = selected; runExpectedId = Selected?.SourceId;
    }
    internal void RequestStop()
    {
        if (link.IsClient) { QueueRemote("stop"); return; }
        if (Selected is { } c) StopComputer(c, true);
        if (selected is not null)
        {
            if (runRequest is not null && Key(runRequest) == Key(selected)) runRequest = null;
            if (saveRequest is { } pending && Key(pending.Target) == Key(selected)) saveRequest = pending with { Run = false };
        }
        SetStatus("Selected computer stopped. Native computer outputs clear on the next safe tick.");
    }
    internal void RequestSave(string source, bool runAfterSave)
    {
        if (selected is null) throw new InvalidOperationException("Aim at a computer and press E first.");
        ProgramStore.Validate(source);
        if (link.IsClient) { QueueRemote("save", source, runAfterSave); return; }
        if (saveRequest is not null) throw new InvalidOperationException("A save is already pending.");
        saveRequest = new SaveRequest(selected, source, SelectedSource, Selected?.SourceId, runAfterSave);
    }
    private void StopComputer(Computer c, bool clear)
    {
        autoAttempted.Add((c.Target.Component.Index, c.Target.Component.Version, c.Target.Guid));
        c.Vm = null; c.Ticks = 0; c.Tick = int.MinValue;
        if (clear && c.Native) clearOutputs.Add(Key(c.Target));
    }
    private void StopAll(string message, bool clear)
    {
        foreach (var c in computers.Values) StopComputer(c, clear);
        if (!clear)
        {
            clearOutputs.Clear();
            foreach (var c in computers.Values) { c.NetworkPending = c.LabelPending = false; c.Outputs = Array.Empty<double>(); }
        }
        runRequest = null; saveRequest = null;
        SetStatus(message);
    }
    private bool CheckSession(EntityManager manager, out string details, bool requireSolo = false)
    {
        var net = ReadSingleton<Netcore.Singleton>(manager);
        var core = ReadSingleton<Core.Singleton>(manager);
        var steam = Core.Get()?._steam ?? throw new InvalidOperationException("Steam transport state unavailable.");
        var connections = steam._connToSteam;
        bool transport = connections.IsCreated && connections.Count != 0;
        bool remote = HasRemoteClients(manager, core._mySteamID.m_SteamID);
        if (remotePeers != (remote || transport)) ComputerNetwork.ResetForWorld();
        remotePeers = remote || transport;
        details = $"server={net._isServer}, status={net._networkStatus}, peers={remote}, transport={transport}";
        return SessionSafety.CanWrite(net._isServer, (int)net._networkStatus, remotePeers, !requireSolo);
    }
    private static unsafe T ReadSingleton<T>(EntityManager manager) where T : unmanaged
    {
        var type = ComponentType.ReadOnly<T>();
        var query = manager.CreateEntityQuery(new[] { type });
        try
        {
            if (query.CalculateEntityCount() != 1) throw new InvalidOperationException("Missing singleton " + typeof(T).Name);
            var pointer = (T*)manager.GetComponentDataRawRO(query.GetSingletonEntity(), type.TypeIndex);
            if (pointer == null) throw new InvalidOperationException("Missing singleton data.");
            return *pointer;
        }
        finally { query.Dispose(); }
    }
    private static unsafe bool HasRemoteClients(EntityManager manager, ulong local)
    {
        var type = ComponentType.ReadOnly<NetcoreClient>();
        var query = manager.CreateEntityQuery(new[] { type });
        try
        {
            if (query.CalculateEntityCount() > 128) return true;
            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var client = (NetcoreClient*)manager.GetComponentDataRawRO(entities[i], type.TypeIndex);
                    if (client == null || SessionSafety.IsRemotePeer(client->_isOnline, client->_steamID.m_SteamID, local)) return true;
                }
            }
            finally { entities.Dispose(); }
            return false;
        }
        finally { query.Dispose(); }
    }
    private static void Controls()
    {
        if (Current is not { } p) return;
        try
        {
            p.editor?.Update();
            if (p.editorProbe && p.hookSeen && !p.editorProbeOpened) { p.editorProbeOpened = true; p.editor?.Toggle(); }
            var keyboard = Keyboard.current;
            if (!Application.isFocused || keyboard is null) return;
            if (ComputerEditor.CapturesInput || p.failed) return;
            if (keyboard.f8Key.wasPressedThisFrame) { if (p.IsSelectedRunning) p.RequestStop(); else p.RequestRun(); }
            if (keyboard.f9Key.wasPressedThisFrame) p.RequestStopAll();
        }
        catch (Exception ex) { p.StopAll("Controls: " + Short(ex), true); }
    }
    private GUIStyle? promptLabel;
    private float hudScale;
    private static float UiScale()
    {
        var canvas = UIManager._singleton?._canvas;
        if (canvas != null && canvas.scaleFactor is > 0.25f and < 8f) return canvas.scaleFactor;
        return Math.Max(1f, Screen.height / 1080f);
    }
    internal void Draw()
    {
        if (editor?.IsOpen == true) { editor.Draw(); return; }
        if (ComputerInteraction.Prompt.Length == 0) return;
        float s = UiScale();
        if (promptLabel is null || Math.Abs(hudScale - s) > 0.01f)
        {
            hudScale = s;
            promptLabel = new GUIStyle { font = GUI.skin.font, fontSize = Math.Max(18, (int)Math.Round(22 * s)),
                alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            promptLabel.normal.textColor = Color.white;
        }
        float w = 520 * s, h = 40 * s;
        GUI.Label(new Rect((Screen.width - w) / 2f, Screen.height / 2f + 40 * s, w, h), ComputerInteraction.Prompt, promptLabel);
    }
    internal void SetStatus(string message) { Status = message; Log.LogInfo(message); }
    private static string Short(Exception ex) => ex.Message.Length <= 256 ? ex.Message : ex.Message[..256];
    public override bool Unload()
    {
        StopAll("Plugin unloaded.", false);
        link.Shutdown(); ComputerNetwork.ResetForWorld();
        graphs.Reset();
        graphVisualProbe?.Cleanup();
        editor?.Shutdown(); harmony.UnpatchSelf(); Current = null;
        if (hud is not null) UnityEngine.Object.Destroy(hud);
        return true;
    }
}

public sealed class ComputerHud : MonoBehaviour
{
    private bool announced;
    public ComputerHud(IntPtr pointer) : base(pointer) { }
    public void OnGUI()
    {
        if (Plugin.Current is not { } p) return;
        try { p.Draw(); if (!announced) { announced = true; p.Log.LogInfo("HUD OnGUI rendering reached."); } }
        catch (Exception ex) { enabled = false; p.Log.LogWarning("HUD disabled: " + ex.Message); }
    }
}
