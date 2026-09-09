using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace ApproximatelyUp.ComputerMod;

public sealed record PortTarget(string Guid, string Name, Entity Component, Entity[] Inputs, Entity[] Outputs)
{
    internal World? OwnerWorld { get; init; }
}

public static class Ports
{
    public const int MaxTargets = 64;
    private const int MaxCandidates = 4096;
    private const int MaxPorts = 8;
    private const float DataLimit = 1e20f;

    // All calls require a live manager on the main thread AFTER CompleteAllTrackedJobs.
    public static List<PortTarget> FindTargets(EntityManager manager, string? targetGuid = null)
    {
        if (manager.m_EntityDataAccess == IntPtr.Zero)
            throw new InvalidOperationException("The entity manager is not initialized.");
        var world = manager.World;
        if (world is null || !world.IsCreated)
            throw new InvalidOperationException("The world has been disposed.");

        var targets = new List<PortTarget>(MaxTargets);
        // Default query options exclude Prefab and Disabled entities.
        var query = manager.CreateEntityQuery(new ComponentType[] {
            ComponentType.ReadOnly<SCGuid>(),
            ComponentType.ReadOnly<SpaceshipElectricPortRef>()
        });
        try
        {
            int count = query.CalculateEntityCount();
            // ponytail: cap the native snapshot at 32 KiB; use chunk paging if larger scenes need discovery.
            if (count < 0 || count > MaxCandidates)
                throw new InvalidOperationException($"Port discovery exceeds the {MaxCandidates}-candidate safety limit ({count}).");
            if (count == 0) return targets;
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length && targets.Count < MaxTargets; i++)
                {
                    if (targetGuid is not null && manager.GetComponentData<SCGuid>(entities[i]).ToString() != targetGuid) continue;
                    var target = Describe(manager, world, entities[i], targetGuid is null);
                    if (target is not null) targets.Add(target);
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
        return targets;
    }

    internal static bool TryGetFlight(EntityManager manager, out Entity singleton, out uint version)
    {
        singleton = default;
        version = 0;
        if (manager.m_EntityDataAccess == IntPtr.Zero || manager.World is not { IsCreated: true })
            throw new InvalidOperationException("The flight world is not initialized or has been disposed.");
        var query = manager.CreateEntityQuery(new[] { ComponentType.ReadWrite<SpaceshipSingleton>() });
        try
        {
            int count = query.CalculateEntityCount();
            if (count == 0) return false;
            if (count != 1) throw new InvalidOperationException($"Ambiguous flight singleton ({count}).");
            var flight = Utility.GetSingleton<SpaceshipSingleton>(manager);
            singleton = query.GetSingletonEntity();
            version = flight._versionIndex;
            return true;
        }
        finally { query.Dispose(); }
    }

    // Target overloads follow ValidateTarget; these predicates do not replace identity/layout validation.
    internal static bool IsFlightMember(EntityManager manager, PortTarget target) =>
        IsFlightMember(manager, target.Component);

    internal static bool IsFlightMember(EntityManager manager, Entity component)
    {
        if (!ComputerItem.IsComputer(manager, component) ||
            !manager.HasComponent(component, ComponentType.ReadOnly<Unity.Transforms.Parent>())) return false;
        var part = manager.GetComponentData<Unity.Transforms.Parent>(component).Value;
        return IsPlaced(manager, part) && manager.HasComponent(part, ComponentType.ReadOnly<SPData>());
    }

    internal static bool IsActive(EntityManager manager, PortTarget target) =>
        IsPlaced(manager, target.Component) && manager.HasComponent(target.Component, ComponentType.ReadWrite<SCActive>());

    internal static List<PortTarget> FindFlightComputers(EntityManager manager)
    {
        if (manager.m_EntityDataAccess == IntPtr.Zero)
            throw new InvalidOperationException("The entity manager is not initialized.");
        var world = manager.World;
        if (world is null || !world.IsCreated)
            throw new InvalidOperationException("The world has been disposed.");
        var targets = new List<PortTarget>(MaxTargets);
        var query = manager.CreateEntityQuery(new[] {
            ComponentType.ReadOnly<SCGuid>(),
            ComponentType.ReadOnly<SpaceshipElectricPortRef>(),
            ComponentType.ReadOnly<Unity.Transforms.Parent>(),
            ComponentType.ReadWrite<SCActive>()
        });
        try
        {
            int count = query.CalculateEntityCount();
            if (count < 0 || count > MaxCandidates)
                throw new InvalidOperationException($"Flight discovery exceeds the {MaxCandidates}-candidate safety limit ({count}).");
            if (count == 0) return targets;
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (!ComputerItem.IsComputer(manager, entities[i]) || !IsFlightMember(manager, entities[i])) continue;
                    var target = Describe(manager, world, entities[i], false);
                    if (target is not null) targets.Add(target);
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
        // Sort before truncation so native query order cannot change admission, even above 64 computers.
        return targets.OrderBy(t => t.Guid, StringComparer.Ordinal).ThenBy(t => t.Component.Index)
            .ThenBy(t => t.Component.Version).Take(MaxTargets).ToList();
    }

    // Read-only probe: zero means no eligible placed target was observed, not proof of access.
    public static int VerifyAccess(EntityManager manager) => FindTargets(manager).Count;

    internal static PortTarget? GetTarget(EntityManager manager, Entity component) =>
        Describe(manager, manager.World, component, false);

    public static double[] ReadInputs(EntityManager manager, PortTarget target)
    {
        ValidateTarget(manager, target);
        var values = new double[target.Inputs.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var port = manager.GetComponentData<SpaceshipElectricPort>(target.Inputs[i]);
            if (!IsNumeric(port._portValue))
                throw new InvalidOperationException("Input is non-finite, out of range, or a reserved camera signal.");
            values[i] = port._portValue;
        }
        return values;
    }

    public static void WriteOutputs(EntityManager manager, PortTarget target, double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateTarget(manager, target);
        if (values.Length != target.Outputs.Length)
            throw new ArgumentException("Supply exactly one value per output port.", nameof(values));

        var staged = new SpaceshipElectricPort[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!IsNumeric(values[i]))
                throw new ArgumentOutOfRangeException(nameof(values), "Outputs must be finite and within the native +/-1e20f data range.");
            staged[i] = manager.GetComponentData<SpaceshipElectricPort>(target.Outputs[i]);
            if (!IsNumeric(staged[i]._portValue))
                throw new InvalidOperationException("Output currently contains a non-numeric/reserved signal.");
        }
        // SetDataValue changes only these struct copies. No ECS writes until every result is checked.
        for (int i = 0; i < staged.Length; i++) staged[i].SetDataValue((float)values[i]);
        for (int i = 0; i < staged.Length; i++)
            manager.SetComponentData<SpaceshipElectricPort>(target.Outputs[i], staged[i]);
    }

    internal static void ValidateTarget(EntityManager manager, PortTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Inputs is null || target.Outputs is null || target.Inputs.Length > MaxPorts ||
            target.Outputs.Length is < 1 or > MaxPorts || string.IsNullOrEmpty(target.Guid))
            throw new ArgumentException("Invalid port target.", nameof(target));

        // Inspect the retained World first; never dereference a manager from a disposed/different world.
        var world = target.OwnerWorld;
        if (world is null || !world.IsCreated || manager.m_EntityDataAccess == IntPtr.Zero ||
            world.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess)
            throw new InvalidOperationException("The selected target's world is no longer active or does not match.");

        var current = Describe(manager, world, target.Component, false);
        if (current is null || !string.Equals(current.Guid, target.Guid, StringComparison.Ordinal) ||
            !current.Inputs.AsSpan().SequenceEqual(target.Inputs) ||
            !current.Outputs.AsSpan().SequenceEqual(target.Outputs))
            throw new InvalidOperationException("The selected component was deleted, replaced, disabled, or its numeric port bindings changed.");
    }

    private static unsafe PortTarget? Describe(EntityManager manager, World world, Entity component, bool validateValues = true)
    {
        if (!IsPlaced(manager, component) ||
            !manager.HasComponent(component, ComponentType.ReadOnly<SCGuid>()) ||
            !manager.HasComponent(component, ComponentType.ReadOnly<SpaceshipElectricPortRef>()))
            return null;

        if (!ComputerItem.IsComputer(manager, component)) return null;

        var guid = manager.GetComponentData<SCGuid>(component);
        if ((guid._a | guid._b | guid._c | guid._d) == 0) return null;

        var buffer = manager.GetBuffer<SpaceshipElectricPortRef>(component, true);
        // Never retain a native buffer, and reject oversized layouts instead of truncating their pins.
        int length = buffer.Length;
        if (length is < 1 or > MaxPorts * 2) return null;
        var ports = new List<(Entity Entity, SpaceshipElectricPort Data)>(length);
        int inputs = 0, outputs = 0;
        for (int i = 0; i < length; i++)
        {
            var entity = buffer[i]._portEntity;
            if (!IsPlaced(manager, entity) || !manager.HasComponent<SpaceshipElectricPort>(entity)) return null;
            var data = manager.GetComponentData<SpaceshipElectricPort>(entity);
            if (data._setupType is not (SpaceshipPortType.DataInput or SpaceshipPortType.DataOutput)) continue;
            if (data._runtimeType != data._setupType || (validateValues && !IsNumeric(data._portValue))) return null;
            foreach (var previous in ports)
                if (previous.Data._index == data._index ||
                    (previous.Entity.Index == entity.Index && previous.Entity.Version == entity.Version)) return null;
            ports.Add((entity, data));
            if (data._setupType == SpaceshipPortType.DataInput) inputs++; else outputs++;
            if (inputs > MaxPorts || outputs > MaxPorts) return null;
        }
        if (outputs == 0) return null;
        ports.Sort((a, b) => a.Data._index.CompareTo(b.Data._index));
        return new PortTarget(guid.ToString(), "AU-08 Lua Computer", component,
            ports.Where(p => p.Data._setupType == SpaceshipPortType.DataInput).Select(p => p.Entity).ToArray(),
            ports.Where(p => p.Data._setupType == SpaceshipPortType.DataOutput).Select(p => p.Entity).ToArray())
        { OwnerWorld = world };
    }

    private static bool IsPlaced(EntityManager manager, Entity entity) =>
        manager.Exists(entity) && !manager.HasComponent<Prefab>(entity) &&
        !manager.HasComponent(entity, ComponentType.ReadOnly<Disabled>());

    private static bool IsNumeric(double value) => double.IsFinite(value) && value >= -DataLimit && value <= DataLimit;
}
