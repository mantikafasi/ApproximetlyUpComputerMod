using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace ApproximatelyUp.ComputerMod;

internal static unsafe class ComputerInteraction
{
    private static Plugin? plugin;
    private static int mainThread;
    private static string prompt = "";
    private static long promptUntil;
    private static bool errorLogged;
    private static long nextDiagnostic;
    private static World? inputWorld;
    private static SystemHandle actionableSystem;
    private static bool producerSeen, groupSeen, consumerSeen, producerWasDown;
    private static long nextProducerDiagnostic;
    private static int producerFrame;
    private static CRPInputSingleton.State producerState;
    private static int producerCalls, lastActionFrame;

    // HUD reads a managed cache, never a query or a native input accessor.
    internal static string Prompt => Plugin.Current == plugin && !ComputerEditor.CapturesInput &&
        Environment.TickCount64 < promptUntil ? prompt : "";

    internal static void Install(Harmony harmony, Plugin owner)
    {
        plugin = owner;
        mainThread = Environment.CurrentManagedThreadId;
        errorLogged = false;
        nextDiagnostic = 0;
        nextProducerDiagnostic = 0;
        inputWorld = null;
        actionableSystem = default;
        producerSeen = groupSeen = consumerSeen = producerWasDown = false;
        producerFrame = -1;
        producerState = CRPInputSingleton.State.None;
        producerCalls = 0;
        lastActionFrame = -1;
        prompt = "";
        CheckSize<CRPInputSingleton>(52);
        CheckSize<KeyConsumer>(1);
        CheckSize<ActionablesController>(40);
        // Observe the actual producer, AFTER the editor's snapshot suppression. Never reuse a
        // producer-time target. The physical edge is sampled again at the native consumer.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CustomRenderPipelineInputSystem), "Update") ??
            throw new MissingMethodException("CustomRenderPipelineInputSystem.Update"),
            postfix: new HarmonyMethod(typeof(ComputerInteraction), nameof(AfterNativeInput)) { priority = Priority.Last });
        // This IL2CPP dispatcher calls UnmanagedUpdate's Burst entry. Intercept the exact native
        // system HANDLE, not a Burst wrapper or an assumed enclosing group. The ray has completed
        // before this consumer, and its current target is read only here, immediately pre-assignment.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(WorldUnmanagedImpl), "UpdateSystem") ??
            throw new MissingMethodException("WorldUnmanagedImpl.UpdateSystem"),
            prefix: new HarmonyMethod(typeof(ComputerInteraction), nameof(BeforeSystem)) { priority = Priority.First });
        // Diagnostic only: distinguish a missing group/filter hook from a missing Down snapshot.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ComponentSystemGroup), "UpdateAllSystems") ??
            throw new MissingMethodException("ComponentSystemGroup.UpdateAllSystems"),
            prefix: new HarmonyMethod(typeof(ComputerInteraction), nameof(BeforeGroup)) { priority = Priority.First });
    }

    private static void BeforeGroup(ComponentSystemGroup __instance)
    {
        if (groupSeen || plugin is null || Plugin.Current != plugin ||
            Environment.CurrentManagedThreadId != mainThread) return;
        try
        {
            if (__instance.TryCast<PreTransformSystemUpdateGroup>() is null) return;
            groupSeen = true;
            var world = __instance.World;
            var active = World.DefaultGameObjectInjectionWorld;
            plugin.Log.LogInfo($"AU-08 HOOK PreTransform.UpdateAllSystems reached: world=0x{(world?.Pointer.ToInt64() ?? 0):X} " +
                $"default=0x{(active?.Pointer.ToInt64() ?? 0):X} frame={Time.frameCount}.");
        }
        catch (Exception ex) { LogFailure(ex); }
    }

    private static void AfterNativeInput(EntityManager __0)
    {
        if (plugin is null || Plugin.Current != plugin || Environment.CurrentManagedThreadId != mainThread) return;
        try
        {
            var active = World.DefaultGameObjectInjectionWorld;
            bool sameWorld = active is not null && active.IsCreated && __0.m_EntityDataAccess != IntPtr.Zero &&
                active.EntityManager.m_EntityDataAccess == __0.m_EntityDataAccess;
            if (!producerSeen)
            {
                producerSeen = true;
                plugin.Log.LogInfo($"AU-08 HOOK CRPInput.Update producer reached: manager=0x{__0.m_EntityDataAccess.ToInt64():X} " +
                    $"default=0x{(active?.Pointer.ToInt64() ?? 0):X} sameManager={sameWorld} frame={Time.frameCount}.");
            }
            if (!sameWorld || !ComputerItem.IsRegistered)
            {
                inputWorld = null;
                actionableSystem = default;
                return;
            }
            if (inputWorld is null || inputWorld.Pointer != active!.Pointer)
            {
                inputWorld = active;
                actionableSystem = default;
                producerWasDown = consumerSeen = false;
                lastActionFrame = -1;
            }
            // No creation or new AOT generic: resolve the game's existing registered system by Type.
            actionableSystem = active!.Unmanaged.GetExistingUnmanagedSystem(Il2CppType.Of<ActionablesControllerUpdate>());
            if (ComputerEditor.CapturesInput) { producerWasDown = false; return; }
            __0.CompleteAllTrackedJobs();
            var controls = Utility.GetSingleton<OptionsData>(__0)._controls;
            int action = (int)controls._keyAction;
            if (producerFrame != Time.frameCount) producerCalls = 0;
            producerCalls++;
            producerFrame = Time.frameCount;
            var rawInput = SingletonData<CRPInputSingleton>(__0, false, out _);
            var rawConsumer = SingletonData<KeyConsumer>(__0, false, out _);
            producerState = RawKey(rawInput, action);
            var keyboard = Keyboard.current;
            var key = action > 0 && action <= 110 && keyboard is not null ? keyboard[(Key)action] : null;
            bool devicePressed = key?.isPressed == true, deviceDown = key?.wasPressedThisFrame == true;
            bool down = producerState == CRPInputSingleton.State.Down || deviceDown;
            long now = Environment.TickCount64;
            if (down && !producerWasDown && now >= nextProducerDiagnostic)
            {
                nextProducerDiagnostic = now + 500;
                plugin.Log.LogInfo($"AU-08 INPUT final-producer: key={controls._keyAction}({action}) " +
                    $"getKey={controls.GetKey(OptionsData.Controls.KeyDefinition.Action)} state={(uint)producerState} " +
                    $"keyboard={keyboard is not null} devicePressed={devicePressed} deviceDown={deviceDown} " +
                    $"consumed={rawConsumer->_consumed} callsThisFrame={producerCalls} " +
                    $"system={actionableSystem.m_Handle}:{actionableSystem.m_Version}/{actionableSystem.m_WorldSeqNo} " +
                    $"sameManager={sameWorld} preTransformSeen={groupSeen} consumerSeen={consumerSeen} frame={producerFrame}.");
            }
            producerWasDown = down;
        }
        catch (Exception ex) { actionableSystem = default; LogFailure(ex); }
    }

    private static void BeforeSystem(SystemHandle __0)
    {
        // This is a hot dispatcher. Compare the complete versioned/world-scoped handle using CLR
        // fields before ANY native accessor/query; do not box WorldUnmanagedImpl as __instance.
        if ((actionableSystem.m_WorldSeqNo | actionableSystem.m_Handle | actionableSystem.m_Version) == 0 ||
            __0.m_Handle != actionableSystem.m_Handle ||
            __0.m_Version != actionableSystem.m_Version || __0.m_WorldSeqNo != actionableSystem.m_WorldSeqNo ||
            __0.m_Entity.Index != actionableSystem.m_Entity.Index || __0.m_Entity.Version != actionableSystem.m_Entity.Version ||
            plugin is null || Plugin.Current != plugin || Environment.CurrentManagedThreadId != mainThread) return;
        try
        {
            var world = inputWorld;
            var active = World.DefaultGameObjectInjectionWorld;
            if (world is null || !world.IsCreated || active is null || !active.IsCreated || active.Pointer != world.Pointer) return;
            if (!world.Unmanaged.IsSystemValid(__0)) return;
            var manager = world.EntityManager;
            if (!consumerSeen)
            {
                consumerSeen = true;
                plugin.Log.LogInfo($"AU-08 HOOK actionable native dispatch reached before Burst: " +
                    $"manager=0x{manager.m_EntityDataAccess.ToInt64():X} default=0x{active.Pointer.ToInt64():X} " +
                    $"sameManager={active.EntityManager.m_EntityDataAccess == manager.m_EntityDataAccess} " +
                    $"system={__0.m_Handle}:{__0.m_Version}/{__0.m_WorldSeqNo} frame={Time.frameCount}.");
            }
            prompt = "";
            if (ComputerEditor.CapturesInput) return;
            // ponytail: world-wide synchronization at the native consumer boundary; narrow only
            // after profiling. No new World, physics ray, target scan or retained native buffers.
            manager.CompleteAllTrackedJobs();
            TryInteract(manager);
        }
        catch (Exception ex) { LogFailure(ex); }
    }

    private static void LogFailure(Exception ex)
    {
        prompt = "";
        if (errorLogged) return;
        errorLogged = true;
        plugin?.Log.LogError("AU-08 interaction refused: " + ex);
    }

    private static void CheckSize<T>(int expected) where T : unmanaged
    {
        RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
        var klass = Il2CppClassPointerStore<T>.NativeClassPtr;
        IL2CPP.il2cpp_runtime_class_init(klass);
        uint alignment = 0;
        if (sizeof(T) != expected || IL2CPP.il2cpp_class_value_size(klass, ref alignment) != expected)
            throw new InvalidOperationException("Unexpected interaction ABI: " + typeof(T).Name);
    }

    private static T* SingletonData<T>(EntityManager manager, bool writable, out Entity entity) where T : unmanaged
    {
        var type = writable ? ComponentType.ReadWrite<T>() : ComponentType.ReadOnly<T>();
        var query = manager.CreateEntityQuery(new EntityQueryDesc {
            All = new[] { type }, Options = EntityQueryOptions.IncludeSystems
        });
        try
        {
            if (query.CalculateEntityCount() != 1) throw new InvalidOperationException("Missing/ambiguous " + typeof(T).Name);
            entity = query.GetSingletonEntity();
            var data = writable ? manager.GetComponentDataRawRW(entity, type.TypeIndex) : manager.GetComponentDataRawRO(entity, type.TypeIndex);
            if (data == null) throw new InvalidOperationException("Null native " + typeof(T).Name);
            return (T*)data;
        }
        finally { query.Dispose(); }
    }

    private static CRPInputSingleton.State RawKey(CRPInputSingleton* data, int key) => key > 0 && key < 128 ?
        (CRPInputSingleton.State)((((uint*)data)[key >> 4] >> ((key & 15) * 2)) & 3) : CRPInputSingleton.State.None;

    // Main's editor must use this instead of assigning through the generated RefRW.ValueRW.
    // Caller completes this live manager's jobs first. Never retain these pointers across UI calls.
    internal static void ClearNativeInput(EntityManager manager)
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (Environment.CurrentManagedThreadId != mainThread || world is null || !world.IsCreated ||
            manager.m_EntityDataAccess == IntPtr.Zero || world.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess)
            throw new InvalidOperationException("Editor input manager is not the live default world.");
        var input = SingletonData<CRPInputSingleton>(manager, true, out _);
        var consumer = SingletonData<KeyConsumer>(manager, true, out _);
        *input = default;
        consumer->_consumed = true;
    }

    internal static string ProbeInputPointers(EntityManager manager)
    {
        if (Environment.CurrentManagedThreadId != mainThread || plugin is null)
            throw new InvalidOperationException("Input probe requires the initialized main thread.");
        return CompareAccessors(manager, 19, SingletonData<CRPInputSingleton>(manager, false, out _),
            SingletonData<KeyConsumer>(manager, false, out _));
    }

    private static string CompareAccessors(EntityManager manager, int action, CRPInputSingleton* input, KeyConsumer* consumer)
    {
        // Read-only diagnostic: the generated byref getters return runtime_invoke's result
        // without conversion. Their address must NOT be assumed to be the ECS component address.
        var ir = Utility.GetSingletonRW<CRPInputSingleton>(manager);
        var kr = Utility.GetSingletonRW<KeyConsumer>(manager);
        ulong inputData = (ulong)ir._Data, consumerData = (ulong)kr._Data;
        var iro = (CRPInputSingleton*)Unsafe.AsPointer(ref ir.ValueRO);
        if (iro == null) return "generated-input-accessor-returned-null";
        uint accessorState = (uint)RawKey(iro, action);
        var irw = (CRPInputSingleton*)Unsafe.AsPointer(ref ir.ValueRW);
        var kro = (byte*)Unsafe.AsPointer(ref kr.ValueRO);
        if (kro == null) return "generated-consumer-accessor-returned-null";
        byte accessorConsumed = *kro;
        var krw = (byte*)Unsafe.AsPointer(ref kr.ValueRW);
        string text = $"rawInput=0x{(ulong)input:X} refData=0x{inputData:X} refRO=0x{(ulong)iro:X} refRW=0x{(ulong)irw:X} " +
            $"rawState={(uint)RawKey(input, action)} accessorState={accessorState} " +
            $"rawConsumer=0x{(ulong)consumer:X} consumerData=0x{consumerData:X} consumerRO=0x{(ulong)kro:X} consumerRW=0x{(ulong)krw:X} " +
            $"rawConsumed={consumer->_consumed} accessorConsumedByte={accessorConsumed}";
        GC.KeepAlive(ir); GC.KeepAlive(kr);
        return text;
    }

    // Main thread only, immediately after this manager's jobs have completed. The caller never
    // uses F7 selection: target/reach/occlusion come from the native actionable raycast result.
    private static bool TryInteract(EntityManager manager)
    {
        var current = plugin;
        if (current is null || Plugin.Current != current || ComputerEditor.CapturesInput ||
            Environment.CurrentManagedThreadId != mainThread || manager.m_EntityDataAccess == IntPtr.Zero) return false;
        var controls = Utility.GetSingleton<OptionsData>(manager)._controls;
        var input = SingletonData<CRPInputSingleton>(manager, false, out _);
        var rawConsumer = SingletonData<KeyConsumer>(manager, false, out var consumerEntity);
        int action = (int)controls._keyAction;
        var keyState = RawKey(input, action);
        var keyboard = Keyboard.current;
        var key = action > 0 && action <= 110 && keyboard is not null ? keyboard[(Key)action] : null;
        bool physicalDown = key?.wasPressedThisFrame == true;
        bool pressed = (keyState == CRPInputSingleton.State.Down || physicalDown) && lastActionFrame != Time.frameCount;
        if (pressed) lastActionFrame = Time.frameCount;
        long now = Environment.TickCount64;
        bool diagnose = pressed && now >= nextDiagnostic;
        if (diagnose) nextDiagnostic = now + 500;

        bool hasOverlay = Utility.TryGetSingleton<OverlaySingleton>(manager, out var overlay);
        bool hasPlayer = Utility.TryGetSingletonEntity<MyPlayerController>(manager, out var player) && manager.Exists(player);
        // Native __AssignQueries deliberately uses independent singleton queries.
        // Do not switch to a per-player/global scan: the imminent consumer reads this same entity.
        bool hasController = Utility.TryGetSingletonEntity<ActionablesController>(manager, out var controllerEntity) && manager.Exists(controllerEntity);
        var controller = hasController ? manager.GetComponentData<ActionablesController>(controllerEntity) : default;
        var target = controller._targetEntity;
        var computer = Entity.Null;
        var guid = default(SCGuid);
        bool hasGarage = Utility.TryGetSingleton<GarageGrabberSingleton>(manager, out var garage);
        bool inFlight = Utility.HasSingleton<SpaceshipSingleton>(manager);
        bool wasConsumed = rawConsumer->_consumed;
        bool consumed = false;
        bool recovering = controller._assignedEntity != Entity.Null && controller._assignedEntity == target;
        bool appFocused = Application.isFocused;
        var cursor = Cursor.lockState;
        // Utility.IsAnyInputFieldFocused only tests for a selected TMP component, not focus or
        // activity. Selection can outlive editing. See recon/interaction-fix/evidence.txt:246.
        var events = EventSystem.current;
        var selected = events == null ? null : events.currentSelectedGameObject;
        var field = selected == null ? null : selected.GetComponent<TMP_InputField>();
        bool textFocused = field != null && field.isActiveAndEnabled && field.isFocused;

        try
        {
            if (diagnose)
            {
                string comparison;
                try { comparison = CompareAccessors(manager, action, input, rawConsumer); }
                catch (Exception ex) { comparison = "accessor-comparison-failed=" + ex.GetType().Name; }
                current.Log.LogInfo($"AU-08 PRECONSUMER physicalDown={physicalDown} frame={Time.frameCount} producerCalls={producerCalls} " +
                    $"target={target.Index}:{target.Version} assigned={controller._assignedEntity.Index}:{controller._assignedEntity.Version} " + comparison);
            }
            if (action <= 0 || action >= 128) return Finish("action-key-invalid");
            if (!hasOverlay) return Finish("overlay-missing");
            if (overlay._opened != 0) return Finish("overlay-open");
            if (!hasPlayer) return Finish("local-player-missing");
            if (!hasController) return Finish("native-controller-missing-or-ambiguous");
            if (controller._assignedEntity != Entity.Null && !recovering) return Finish("native-actionable-assigned");
            if (target == Entity.Null) return Finish("native-ray-no-target");
            if (!manager.Exists(target)) return Finish("native-ray-stale-target");
            if (!manager.HasComponent(target, ComponentType.ReadOnly<BelongingSC>())) return Finish("native-ray-not-sc-child");
            var owner = manager.GetComponentData<BelongingSC>(target);
            computer = owner._sc;
            if (!ComputerItem.IsComputer(manager, computer)) return Finish("native-ray-not-au08");

            // Reserve only a matching computer's action, BEFORE validation/callback can fail.
            // A refused AU-08 open must never fall through into native program-ID label typing.
            if (pressed)
            {
                if (wasConsumed && !recovering) return Finish("key-already-consumed");
                var consumer = (KeyConsumer*)manager.GetComponentDataRawRW(consumerEntity, ComponentType.ReadWrite<KeyConsumer>().TypeIndex);
                if (consumer == null) throw new InvalidOperationException("Native key consumer disappeared.");
                consumer->_consumed = true;
                consumed = ((KeyConsumer*)manager.GetComponentDataRawRO(consumerEntity, ComponentType.ReadOnly<KeyConsumer>().TypeIndex))->_consumed;
                if (!consumed) throw new InvalidOperationException("Native key reservation did not stick.");
            }
            // Unlike the native consumer, our editor needs these UI gates. Reserve the matching
            // AU-08 first so a refused open cannot instead assign its program-ID label.
            if (!appFocused) return Finish("application-unfocused");
            if (cursor != CursorLockMode.Locked && !recovering) return Finish("cursor-unlocked");
            if (textFocused) return Finish("native-text-focused");
            if (manager.HasComponent(target, ComponentType.ReadOnly<Disabled>()) ||
                manager.HasComponent(target, ComponentType.ReadOnly<Prefab>())) return Finish("label-disabled-or-prefab");
            if (!manager.HasComponent(target, ComponentType.ReadOnly<ActionableLabel>()) ||
                !manager.HasComponent(target, ComponentType.ReadOnly<ActionableLabelString>()) ||
                !manager.HasComponent(target, ComponentType.ReadOnly<ActionableAssignOnKeyActionDown>())) return Finish("label-action-components-missing");
            if (!manager.HasComponent(computer, ComponentType.ReadOnly<SCGuid>()) ||
                !manager.HasComponent(computer, ComponentType.ReadOnly<LinkedEntityGroup>()) ||
                manager.GetComponentData<SCTypeLabel>(computer)._actionableLabelEntity != target) return Finish("computer-storage-link-invalid");
            {
                var links = manager.GetBuffer<LinkedEntityGroup>(computer, true);
                if (links.Length is < 2 or > 256 || owner._linkedEntityGroupIndex < 1 ||
                    owner._linkedEntityGroupIndex >= links.Length || links[owner._linkedEntityGroupIndex].Value != target) return Finish("label-group-link-invalid");
            }
            guid = manager.GetComponentData<SCGuid>(computer);
            if ((guid._a | guid._b | guid._c | guid._d) == 0) return Finish("computer-guid-zero");
            // Native raycasting checks the garage hand/state ONLY without SpaceshipSingleton.
            // The garage singleton can retain a hand/tool state during flight (RVA 0xD30862).
            if (!inFlight && (!hasGarage || !garage.CanDoActionablesWithCurrentState())) return Finish("garage-hand-or-mode-blocked");

            if (recovering)
            {
                if (!consumed || !physicalDown) return Finish("assigned-label-needs-physical-edge");
                if (!float.IsFinite(controller._assignedDistance) || controller._assignedDistance < 0 || controller._assignedDistance > 4.5f ||
                    manager.HasComponent(target, ComponentType.ReadOnly<ActionableData>()) ||
                    manager.HasComponent(controllerEntity, ComponentType.ReadOnly<ActWhiteboardCamTransformOverrider>()) ||
                    !manager.HasComponent(player, ComponentType.ReadOnly<PlayerControllerData>())) return Finish("assigned-label-release-contract-invalid");
                // Audited native helper and its rounding helper never read system-instance fields.
                // Use the original release with exact existing controller/player, NOT a partial null.
                uint freeze = manager.GetComponentData<PlayerControllerData>(player)._freeze;
                default(ActionablesControllerUpdate).OnReleaseActionable(manager, ref controller, controllerEntity, player);
                if (controller._assignedEntity != Entity.Null || manager.GetComponentData<PlayerControllerData>(player)._freeze != (freeze & ~2u))
                    throw new InvalidOperationException("Native label release failed its postconditions.");
                manager.SetComponentData(controllerEntity, controller);
                // Assigned mode does not refresh the native ray. Never open from its stale target;
                // release now, then require a new press on the next naturally raycast target.
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                prompt = "Native label released. Aim + " + controls._keyAction + " to edit AU-08";
                promptUntil = now + 2000;
                return Finish("owned-label-released-new-press-required");
            }

            // Only this live manager and the exact versioned/GUID-validated native target's root
            // reach main. Opening is not execution authorization; Save/Run own their role checks.
            prompt = controls._keyAction + " Edit AU-08";
            promptUntil = Environment.TickCount64 + 250;
            if (!consumed) return false;
            prompt = "";
            bool opened = current.OpenComputerEditor(manager, computer);
            return Finish(opened ? "opened" : "editor-callback-refused", opened);
        }
        catch (Exception ex)
        {
            Finish("exception-" + ex.GetType().Name);
            throw;
        }

        bool Finish(string reason, bool opened = false)
        {
            // ponytail: at most two reports/second, only on native Down edges, never while typing
            // in our editor. No source text, entity enumeration or diagnostic raycast.
            if (diagnose)
            {
                diagnose = false;
                current.Log.LogInfo($"AU-08 INTERACT {reason}: key={controls._keyAction}({action}) state={(uint)keyState}; " +
                    $"world=0x{manager.m_EntityDataAccess.ToInt64():X} player={player.Index}:{player.Version}/{hasPlayer} " +
                    $"controller={controllerEntity.Index}:{controllerEntity.Version}/{hasController} " +
                    $"target={target.Index}:{target.Version} assigned={controller._assignedEntity.Index}:{controller._assignedEntity.Version} " +
                    $"computer={computer.Index}:{computer.Version} guid={guid._a:X8}{guid._b:X8}{guid._c:X8}{guid._d:X8}; " +
                    $"overlay=0x{overlay._opened:X}/{hasOverlay} focus={appFocused} cursor={cursor} textFocused={textFocused} " +
                    $"selectedTMP={field != null} flight={inFlight} garage={hasGarage}" +
                    (hasGarage ? $"/{garage._state}/hand={garage._handPrefab.Index}:{garage._handPrefab.Version}" : "") +
                    $" consumedBefore={wasConsumed} reserved={consumed} physicalDown={physicalDown} frame={Time.frameCount} " +
                    $"producerFrame={producerFrame} producerState={(uint)producerState} preTransformSeen={groupSeen}");
            }
            return opened;
        }
    }
}
