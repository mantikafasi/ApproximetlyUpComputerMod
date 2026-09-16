using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace ApproximatelyUp.ComputerMod;

internal static class ComputerNetwork
{
    private const float DataLimit = 1e20f;
    private static readonly long SendInterval = (Stopwatch.Frequency + 59) / 60;
    private static readonly Dictionary<(int, int, string), Sent> Published = new();
    private static int mainThread;
    private static IntPtr world;
    private static bool layoutsChecked;
    private static IntPtr createLabelMethod, decodeLabelMethod;

    // 1.0.204 adds a rotation byte: generated size 43, native size still 44.
    [StructLayout(LayoutKind.Explicit, Size = 44)]
    private struct LabelBuffer
    {
        [FieldOffset(0)] internal NetcoreEvent_SetActionableLabel Value;
    }

    private sealed record Sent(int NetworkId, ushort[] Indices, float[] Values, long At);

    // Call on the plugin main thread at initialization, world/session changes and rewind.
    // Already-enqueued native events belong to Netcore; never dispose/clear its shared queue here.
    internal static void ResetForWorld()
    {
        if (mainThread != 0 && mainThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Computer networking requires the plugin main thread.");
        mainThread = Environment.CurrentManagedThreadId;
        Published.Clear();
        world = IntPtr.Zero;
    }

    // AFTER successful Ports.WriteOutputs. True: all values queued or unchanged. False: retry
    // the latest committed values next post-group, INCLUDING stopped computers' final zeros.
    // There is no background writer, retained native buffer, source transfer or client Lua path.
    internal static unsafe bool PublishOutputs(EntityManager manager, PortTarget target, double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var (singleton, net, identity) = PrepareTarget(manager, target);
        if (values.Length != target.Outputs.Length || values.Length is < 1 or > 8)
            throw new ArgumentException("Supply exactly one value per output, at most eight.", nameof(values));

        var owner = target.OwnerWorld!;
        if (world != owner.Pointer) { ResetForWorld(); world = owner.Pointer; }
        var key = (target.Component.Index, target.Component.Version, target.Guid);
        Published.TryGetValue(key, out var previous);
        long now = Stopwatch.GetTimestamp();
        bool refresh = previous is not null && now - previous.At >= Stopwatch.Frequency;
        if (previous is null && Published.Count >= Ports.MaxTargets)
            throw new InvalidOperationException("Computer replication target limit reached; reload the world.");

        var packets = new NetcoreEvent_SyncPortValue[values.Length];
        var indices = new ushort[values.Length];
        var committed = new float[values.Length];
        var links = manager.GetBuffer<LinkedEntityGroup>(target.Component, true);
        if (links.Length is < 2 or > 256 || links[0].Value != target.Component)
            throw new InvalidOperationException("Invalid AU-08 linked group.");
        int changed = 0;
        for (int i = 0; i < values.Length; i++)
        {
            var portEntity = target.Outputs[i];
            if (!manager.HasComponent(portEntity, ComponentType.ReadOnly<BelongingSC>()))
                throw new InvalidOperationException("Output port has no owning component.");
            var belonging = manager.GetComponentData<BelongingSC>(portEntity);
            int index = belonging._linkedEntityGroupIndex;
            if (belonging._sc != target.Component || index < 1 || index >= links.Length || index > ushort.MaxValue ||
                links[index].Value != portEntity)
                throw new InvalidOperationException("Output is outside its owning computer's linked group.");
            indices[i] = checked((ushort)index);
            var port = manager.GetComponentData<SpaceshipElectricPort>(portEntity);
            if (port._setupType != SpaceshipPortType.DataOutput || port._runtimeType != SpaceshipPortType.DataOutput ||
                !double.IsFinite(values[i]) || values[i] < -DataLimit || values[i] > DataLimit ||
                !float.IsFinite(port._portValue) || port._portValue != (float)values[i])
                throw new InvalidOperationException("Publish requires finite numeric outputs matching the successful native commit.");
            committed[i] = port._portValue;
            if (refresh || previous is null || previous.NetworkId != identity._id || previous.Values.Length != values.Length ||
                previous.Indices[i] != indices[i] || previous.Values[i] != committed[i])
                packets[changed++] = new NetcoreEvent_SyncPortValue {
                    _netcoreEntity = identity, _linkedEntityGroupIndex = indices[i], _portValue = committed[i]
                };
        }
        if (changed == 0) return true;
        if (previous is not null && now - previous.At < SendInterval) return false;

        var queue = manager.GetBuffer<NetcoreNewEvent>(singleton, false);
        // ponytail: bound additions to the shared queue; caller retries latest values rather than building a second queue.
        if (queue.Length < 0 || queue.Length > 8192 - changed) return false;
        queue.EnsureCapacity(queue.Length + changed);
        var events = new NetcoreEvent[changed];
        var staged = new NetcoreNewEvent[changed];
        int created = 0, enqueued = 0;
        try
        {
            for (; created < changed; created++)
                events[created] = NetcoreEvent.Create<NetcoreEvent_SyncPortValue>(packets[created], true);
            for (int i = 0; i < changed; i++)
                staged[i] = new NetcoreNewEvent(events[i], (int*)net._eventInterlockedCounterPtr);
            for (; enqueued < changed; enqueued++) queue.Add(staged[enqueued]);
            Published[key] = new Sent(identity._id, indices, committed, Stopwatch.GetTimestamp());
            return true;
        }
        finally
        {
            // Native broadcast clones and then frees queued events. Only untransferred allocations are ours.
            for (int i = enqueued; i < created; i++) events[i].Dispose();
        }
    }

    // AFTER the host's successful SetProgramId, including authorized teammate requests.
    // One reliable native reference update; source messages and acknowledgements belong to the caller.
    internal static unsafe void PublishProgramId(EntityManager manager, PortTarget target)
    {
        var (singleton, net, identity) = PrepareTarget(manager, target);
        // GetProgramId also validates the placed label, text-renderer ownership, bounds and 12-hex/NUL encoding.
        string id = (GraphScreen.IsScreen(manager, target.Component) ? GraphScreen.ReadId(manager, target.Component) : ComputerItem.GetProgramId(manager, target.Component))
            ?? throw new InvalidOperationException("The computer has no valid committed program ID.");
        if (!ComputerItem.ValidId(id.AsSpan()))
            throw new InvalidOperationException("Program ID must be exactly 12 uppercase hexadecimal characters.");
        var label = manager.GetComponentData<SCTypeLabel>(target.Component)._actionableLabelEntity;
        if (manager.HasComponent(label, ComponentType.ReadOnly<ActionableLabelAsFloat>()))
            throw new InvalidOperationException("Program storage must be a text label, not a numeric actionable.");
        var belonging = manager.GetComponentData<BelongingSC>(label);
        var links = manager.GetBuffer<LinkedEntityGroup>(target.Component, true);
        int index = belonging._linkedEntityGroupIndex;
        if (label == target.Component || belonging._sc != target.Component || links.Length is < 2 or > 256 ||
            links[0].Value != target.Component || index < 1 || index >= links.Length || links[index].Value != label)
            throw new InvalidOperationException("Program label is outside its owning computer's linked group.");
        var text = manager.GetComponentData<ActionableLabelString>(label);
        for (int i = 0; i < 16; i++)
            if (text.GetCharacter(i) != (i < id.Length ? id[i] : '\0'))
                throw new InvalidOperationException("Native program ID changed before publication.");
        var packet = new NetcoreEvent_SetActionableLabel {
            _label = text, _netcoreEntity = identity, _linkedEntityGroupIndex = checked((ushort)index),
            // Native +infinity sentinel skips ActionableLabelAsFloat writes. Zero is NOT equivalent.
            _asFloatValue = ActionableLabelAsFloat.NONE,
            _rotated = manager.GetComponentData<ActionableLabel>(label)._rotated
        };
        var queue = manager.GetBuffer<NetcoreNewEvent>(singleton, false);
        if (queue.Length is < 0 or >= 8192)
            throw new InvalidOperationException("Native event queue is full; program ID committed locally but not published. Retry the current ID.");
        queue.EnsureCapacity(queue.Length + 1);
        var ev = CreateLabelEvent(packet);
        bool enqueued = false;
        try
        {
            queue.Add(new NetcoreNewEvent(ev, (int*)net._eventInterlockedCounterPtr));
            enqueued = true;
        }
        finally { if (!enqueued) ev.Dispose(); }
    }

    // Explicit owned-process main-menu diagnostic only. Allocates/decodes/frees two scratch
    // payloads; no enqueue, ordering-counter increment, real identity lookup or world write.
    internal static void ProbeBindings(EntityManager manager)
    {
        if (mainThread == 0 || mainThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Call ResetForWorld on the plugin main thread before probing.");
        var active = World.DefaultGameObjectInjectionWorld;
        if (active is null || !active.IsCreated || manager.m_EntityDataAccess == IntPtr.Zero ||
            active.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess)
            throw new InvalidOperationException("Binding probe requires the live default world.");
        var menu = UIManager._singleton?._mainMenu;
        if (menu is null || !menu.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Binding probe is restricted to the main menu.");
        manager.CompleteAllTrackedJobs();
        ReadHost(manager);
        CheckLayouts();
        var port = new NetcoreEvent_SyncPortValue { _portValue = 0.25f, _linkedEntityGroupIndex = 23 };
        var ev = NetcoreEvent.Create<NetcoreEvent_SyncPortValue>(port, true);
        try
        {
            if (ev._dataPtr == IntPtr.Zero || ev.GetEventID() != NetcoreEventID.SyncPortValue || !ev.IsImportant())
                throw new InvalidOperationException("Native port event factory did not preserve its ID/reliability.");
            ev.ReinterpretAs<NetcoreEvent_SyncPortValue>(out var decoded);
            if (decoded._netcoreEntity._id != 0 || decoded._portValue != port._portValue ||
                decoded._linkedEntityGroupIndex != port._linkedEntityGroupIndex)
                throw new InvalidOperationException("Native port event payload did not roundtrip.");
        }
        finally { ev.Dispose(); }
        const string probeId = "012345ABCDEF";
        var label = new NetcoreEvent_SetActionableLabel {
            _linkedEntityGroupIndex = 17, _asFloatValue = ActionableLabelAsFloat.NONE
        };
        for (int i = 0; i < probeId.Length; i++) label._label.SetCharacter(i, probeId[i]);
        ev = CreateLabelEvent(label);
        try
        {
            if (ev._dataPtr == IntPtr.Zero || ev.GetEventID() != NetcoreEventID.SetActionableLabel || !ev.IsImportant())
                throw new InvalidOperationException("Native label event factory did not preserve its ID/reliability.");
            var decoded = DecodeLabelEvent(ev);
            if (decoded._netcoreEntity._id != 0 || decoded._linkedEntityGroupIndex != label._linkedEntityGroupIndex ||
                decoded._asFloatValue != ActionableLabelAsFloat.NONE)
                throw new InvalidOperationException("Native label event address/sentinel did not roundtrip.");
            for (int i = 0; i < 16; i++)
                if (decoded._label.GetCharacter(i) != (i < probeId.Length ? probeId[i] : '\0'))
                    throw new InvalidOperationException("Native label event text did not roundtrip.");
        }
        finally { ev.Dispose(); }
    }

    internal static void ValidateNetworkTarget(EntityManager manager, PortTarget target) => PrepareTarget(manager, target);

    private static (Entity Singleton, Netcore.Singleton Net, NetcoreEntity Identity) PrepareTarget(EntityManager manager, PortTarget target)
    {
        if (mainThread == 0 || mainThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Call ResetForWorld on the plugin main thread before publishing.");
        ArgumentNullException.ThrowIfNull(target);
        var owner = target.OwnerWorld;
        if (owner is null || !owner.IsCreated || manager.m_EntityDataAccess == IntPtr.Zero ||
            owner.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess)
            throw new InvalidOperationException("Cannot publish from a stale or foreign world.");

        manager.CompleteAllTrackedJobs();
        bool screen = GraphScreen.IsScreen(manager, target.Component);
        if (screen)
        {
            var live = GraphScreen.Target(manager, target.Component);
            if (live.Guid != target.Guid || !live.Inputs.AsSpan().SequenceEqual(target.Inputs)) throw new InvalidOperationException("Graph target changed before publication.");
        }
        else Ports.ValidateTarget(manager, target);
        if ((!screen && !ComputerItem.IsComputer(manager, target.Component)) ||
            !manager.HasComponent(target.Component, ComponentType.ReadOnly<SCBlueprintClass>()) ||
            manager.GetComponentData<SCBlueprintClass>(target.Component)._class != 35 ||
            !manager.HasComponent(target.Component, ComponentType.ReadOnly<NetcoreEntity>()) ||
            !manager.HasComponent(target.Component, ComponentType.ReadOnly<LinkedEntityGroup>()))
            throw new InvalidOperationException("Replication requires a placed native AU-08 with class 35 and a network identity.");
        var (singleton, net) = ReadHost(manager);
        CheckLayouts();

        var identity = manager.GetComponentData<NetcoreEntity>(target.Component);
        // IsAuthorized is exactly id > 0 in this game. Box a COPY of the native map through
        // IL2CPP, not a guessed CLR layout; TryGetValue has an original AOT specialization.
        if (!identity.IsAuthorized()) throw new InvalidOperationException("Computer network identity is not authorized.");
        var map = new UnsafeHashMap<NetcoreEntity, Entity>(IL2CPP.il2cpp_value_box(
            Il2CppClassPointerStore<UnsafeHashMap<NetcoreEntity, Entity>>.NativeClassPtr, net._netcoreToEntityMap));
        if (!map.TryGetValue(identity, out var mapped) || mapped != target.Component)
            throw new InvalidOperationException("Computer network identity does not resolve to its owning entity.");
        return (singleton, net, identity);
    }

    private static (Entity Singleton, Netcore.Singleton Net) ReadHost(EntityManager manager)
    {
        var query = manager.CreateEntityQuery(new[] { ComponentType.ReadOnly<Netcore.Singleton>() });
        Entity singleton;
        Netcore.Singleton net;
        try
        {
            if (query.CalculateEntityCount() != 1)
                throw new InvalidOperationException("Missing unique Netcore singleton.");
            singleton = query.GetSingletonEntity();
            net = manager.GetComponentData<Netcore.Singleton>(singleton);
        }
        finally { query.Dispose(); }
        if (!net._isServer || net._networkStatus is not (Netcore.NetworkStatus.ServerOpen or Netcore.NetworkStatus.ServerClosed))
            throw new InvalidOperationException("Only the authoritative host may publish computer state.");
        if (net._eventInterlockedCounterPtr == IntPtr.Zero || net._netcoreToEntityMap == IntPtr.Zero ||
            !manager.HasComponent(singleton, ComponentType.ReadOnly<NetcoreNewEvent>()))
            throw new InvalidOperationException("Native event queue, ordering counter or network map unavailable.");
        return (singleton, net);
    }

    private static unsafe void CheckLayouts()
    {
        if (layoutsChecked) return;
        var details = new List<string>();
        // Evaluate EVERY layout before failing, not a short-circuit that hides the mismatch.
        bool valid = CheckLayout<NetcoreEntity>(4, 4, details, ("_id", 0));
        valid &= CheckLayout<NetcoreEvent_SyncPortValue>(12, 12, details,
            ("_netcoreEntity", 0), ("_portValue", 4), ("_linkedEntityGroupIndex", 8));
        valid &= CheckLayout<NetcoreEvent_SetActionableLabel>(44, 43, details,
            ("_label", 0), ("_netcoreEntity", 32), ("_asFloatValue", 36), ("_linkedEntityGroupIndex", 40), ("_rotated", 42));
        valid &= CheckLayout<ActionableLabelString>(32, 32, details, ("_data0", 0), ("_data15", 30));
        valid &= CheckLayout<NetcoreEvent>(16, 16, details, ("_dataPtr", 0), ("_flags", 8));
        valid &= CheckLayout<NetcoreNewEvent>(24, 24, details, ("_event", 0), ("_sortValue", 16));
        RuntimeHelpers.RunClassConstructor(typeof(ActionableLabelAsFloat).TypeHandle);
        float none = ActionableLabelAsFloat.NONE;
        details.Add($"LabelBuffer={sizeof(LabelBuffer)}, NONE=0x{BitConverter.SingleToInt32Bits(none):X8}, CLR={Environment.Version}");
        valid &= sizeof(LabelBuffer) == 44 && float.IsPositiveInfinity(none);
        string diagnostic = "COMPUTER NETWORK ABI: " + string.Join("; ", details);
        Plugin.Current?.Log.LogInfo(diagnostic);
        if (!valid) throw new InvalidOperationException("Native event ABI mismatch; replication disabled. " + diagnostic);
        createLabelMethod = LabelMethod("MethodInfoStoreGeneric_Create_Public_Static_NetcoreEvent_T_Boolean_0`1");
        decodeLabelMethod = LabelMethod("MethodInfoStoreGeneric_ReinterpretAs_Public_Void_byref_T_0`1");
        layoutsChecked = true;
    }

    private static unsafe bool CheckLayout<T>(int expected, int alternateManaged, List<string> details,
        params (string Field, int Offset)[] fields) where T : unmanaged
    {
        int managed = sizeof(T), native = -1;
        uint alignment = 0;
        IntPtr pointer = IntPtr.Zero;
        string error = "";
        bool offsetsMatch = true;
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
            pointer = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (pointer != IntPtr.Zero) native = IL2CPP.il2cpp_class_value_size(pointer, ref alignment);
            foreach (var field in fields)
            {
                int offset = Marshal.OffsetOf<T>(field.Field).ToInt32();
                if (offset != field.Offset) { offsetsMatch = false; error += $" {field.Field}@{offset}!={field.Offset}"; }
            }
        }
        catch (Exception ex) { error += " " + ex.GetType().Name + ": " + ex.Message; offsetsMatch = false; }
        details.Add($"{typeof(T).Name} managed={managed} native={native} expected={expected} class=0x{pointer.ToInt64():X} align={alignment}{error}");
        return pointer != IntPtr.Zero && native == expected && (managed == expected || managed == alternateManaged) && offsetsMatch;
    }

    private static IntPtr LabelMethod(string name)
    {
        // Resolve the SAME closed AOT MethodInfo used by the generated wrapper, not a new T or hard-coded RVA.
        var type = typeof(NetcoreEvent).GetNestedType(name, BindingFlags.NonPublic)?.MakeGenericType(typeof(NetcoreEvent_SetActionableLabel))
            ?? throw new InvalidOperationException("Missing native label MethodInfo store: " + name);
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        var pointer = (IntPtr?)type.GetField("Pointer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        return pointer is { } p && p != IntPtr.Zero ? p : throw new InvalidOperationException("Unresolved native label method: " + name);
    }

    private static unsafe NetcoreEvent CreateLabelEvent(NetcoreEvent_SetActionableLabel value)
    {
        var buffer = new LabelBuffer { Value = value }; // zero-initialized native tail padding
        bool important = true;
        void** args = stackalloc void*[2];
        args[0] = &buffer; args[1] = &important;
        IntPtr exception = IntPtr.Zero;
        var boxed = IL2CPP.il2cpp_runtime_invoke(createLabelMethod, IntPtr.Zero, args, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        if (boxed == IntPtr.Zero) throw new InvalidOperationException("Native label factory returned no event.");
        // NetcoreEvent's complete 16-byte layout was checked before this invocation.
        var data = IL2CPP.il2cpp_object_unbox(boxed);
        if (data == IntPtr.Zero) throw new InvalidOperationException("Native label factory returned no event value.");
        return *(NetcoreEvent*)data;
    }

    private static unsafe NetcoreEvent_SetActionableLabel DecodeLabelEvent(NetcoreEvent ev)
    {
        byte* buffer = stackalloc byte[44 + 16];
        new Span<byte>(buffer, 44).Clear();
        new Span<byte>(buffer + 44, 16).Fill(0xA5);
        void** args = stackalloc void*[1]; args[0] = buffer;
        IntPtr exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(decodeLabelMethod, (IntPtr)(&ev), args, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        for (int i = 44; i < 60; i++)
            if (buffer[i] != 0xA5) throw new InvalidOperationException("Native label decode exceeded its 44-byte buffer.");
        // Read only CLR-size bytes from the full native output, never decode directly into the short wrapper.
        return *(NetcoreEvent_SetActionableLabel*)buffer;
    }
}
