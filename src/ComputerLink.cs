using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Steamworks;
using Unity.Entities;

namespace ApproximatelyUp.ComputerMod;

internal sealed class ComputerLink
{
    internal const int Channel = 0x41553038; // AU08, Steam Messages namespace only; not a Netcore packet ID.
    private const int MaxPeers = 32, BatchSize = 4, MaxBatches = 8;
    private readonly int mainThread = Environment.CurrentManagedThreadId;
    private readonly Action<string>? log;
    private readonly Dictionary<ulong, Peer> peers = new();
    private Il2CppStructArray<IntPtr>? incoming;
    private string epoch = ComputerPacket.NewNonce(), worldKey = "";
    private IntPtr world;
    private World? owner;
    private ulong local, host, lobby, lobbyOwner;
    private uint hostHandle;
    private bool observed, disabled, stopped, warned, polling;
    private long nextId, nextSequence;

    private sealed class Peer
    {
        internal readonly uint Handle;
        internal readonly Entity Admission;
        internal readonly long TimeToken;
        internal readonly ComputerLinkPeer State;
        internal int Received, Sent;
        internal bool Opened;
        internal double GraphBytes = 262144, GraphRefilled;
        internal Peer(uint handle, Entity admission, long timeToken, bool client, string epoch, double now)
        { Handle = handle; Admission = admission; TimeToken = timeToken; State = new(client, epoch, now); }
    }

    internal ComputerLink(Action<string>? log = null) => this.log = log;
    // Unknown/unavailable authority never turns a client into a solo host.
    internal bool IsClient { get; private set; } = true;
    internal bool Ready => !disabled && !stopped && IsClient && peers.TryGetValue(host, out var peer) && peer.State.Ready;
    internal bool HasPeers => peers.Count != 0;
    internal ulong SourceAuthority => IsClient ? host : local;
    internal string Status { get; private set; } = "Computer channel awaiting native session.";
    // Client grant includes the per-connection challenge so cache users notice reconnects.
    internal string Session => IsClient ? Ready ? peers[host].State.Session + ":" + peers[host].State.Challenge : "" : epoch;

    // Caller owns job completion. Call every post-group pass, even with no running VMs.
    internal void Poll(EntityManager manager, string worldKey, Action<ulong, ComputerMessage> receive)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(receive);
        if (polling) throw new InvalidOperationException("ComputerLink.Poll cannot be reentered.");
        polling = true;
        try
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            ReadRoster(manager, worldKey, now);
            foreach (var peer in peers.Values) { peer.Received = peer.Sent = 0; peer.State.Expire(now); }
            if (disabled || stopped || peers.Count == 0) return;
            if (CSteamAPIContext.GetSteamNetworkingMessages() == IntPtr.Zero)
                throw new InvalidOperationException("Native Steam Messages interface unavailable.");
            incoming ??= new Il2CppStructArray<IntPtr>(BatchSize);
            foreach (var entry in peers)
            {
                if (disabled || stopped) break;
                var peer = entry.Value;
                if (now < peer.State.NextHeartbeat) continue;
                peer.State.NextHeartbeat = now + 3;
                var identity = Identity(entry.Key);
                peer.Opened = true;
                _ = SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
                var hello = peer.State.Heartbeat();
                if (hello is not null) Send(entry.Key, peer, hello);
            }
            // ponytail: bounded channel drain, not a second queue. At most 32 messages
            // globally and four per authenticated peer per poll; excess packets are released.
            for (int batch = 0; batch < MaxBatches && !disabled && !stopped; batch++)
            {
                int count;
                try
                {
                    count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, incoming, BatchSize);
                    if (count < 0) throw new InvalidOperationException("Steam Messages receive failed.");
                    if (count > BatchSize) throw new InvalidOperationException("Steam Messages returned an invalid count.");
                    if (count == 0) break;
                    for (int i = 0; i < count; i++)
                    {
                        if (disabled || stopped) break;
                        IntPtr pointer = incoming[i];
                        if (pointer == IntPtr.Zero) continue;
                        var native = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        if (native.m_nChannel != Channel || native.m_cbSize is < 2 or > ComputerPacket.MaxBytes ||
                            native.m_pData == IntPtr.Zero || native.m_identityPeer.m_eType != ESteamNetworkingIdentityType.k_ESteamNetworkingIdentityType_SteamID)
                            continue;
                        ulong sender = native.m_identityPeer.GetSteamID64();
                        if (!peers.TryGetValue(sender, out var peer) || ++peer.Received > 4 || !Live(sender, peer)) continue;
                        byte[] bytes = new byte[native.m_cbSize];
                        Marshal.Copy(native.m_pData, bytes, 0, bytes.Length);
                        ComputerPacket packet;
                        try { packet = ComputerPacket.Decode(bytes, IsClient); }
                        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.Text.DecoderFallbackException or
                            System.Text.EncoderFallbackException or InvalidDataException or InvalidOperationException or FormatException)
                        { WarnOnce("Rejected malformed or incompatible computer packet."); continue; }
                        if (packet.Kind != "message")
                        {
                            var reply = peer.State.AcceptControl(packet, now);
                            if (reply is not null) Send(sender, peer, reply);
                            continue;
                        }
                        if (!peer.State.AcceptMessage(packet, now)) continue;
                        try { receive(sender, packet.Message!); }
                        catch (Exception ex) { WarnOnce("Computer message handler failed (" + ex.GetType().Name + ")."); }
                        if (disabled || stopped || !peers.TryGetValue(sender, out var current) || current != peer) break;
                    }
                }
                finally
                {
                    Exception? releaseError = null;
                    // Also release populated slots if receive/count validation throws.
                    for (int i = 0; i < BatchSize; i++)
                    {
                        IntPtr pointer = incoming[i];
                        incoming[i] = IntPtr.Zero;
                        if (pointer == IntPtr.Zero) continue;
                        try { SteamNetworkingMessage_t.Release(pointer); }
                        catch (Exception ex) { releaseError ??= ex; }
                    }
                    if (releaseError is not null) throw new InvalidOperationException("Steam message release failed.", releaseError);
                }
                if (count < BatchSize) break;
            }
            if (!disabled && !stopped)
                Status = IsClient ? Ready ? "Connected to host; trusted teammates may edit/run." : "Waiting for compatible host handshake." :
                    $"Computer channel: {peers.Values.Count(p => p.State.Ready)}/{peers.Count} teammates ready.";
        }
        catch (Exception ex) { Disable("Computer channel unavailable (" + ex.GetType().Name + "). Native gameplay is unchanged."); }
        finally { polling = false; }
    }

    internal bool SendToHost(ComputerMessage message)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(message);
        if (!Ready || !peers.TryGetValue(host, out var peer))
        { Status = "Host computer channel is not ready; request was not sent."; return false; }
        message.Id = checked(++nextId);
        return Send(host, peer, peer.State.Wrap(message, message.Id));
    }

    // Keep message.Id from the request; the independent wire sequence deduplicates
    // replies even when asynchronous save replies finish out of request-ID order.
    internal bool SendToPeer(ulong peerId, ComputerMessage message)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(message);
        if (IsClient || disabled || stopped || !peers.TryGetValue(peerId, out var peer) || !peer.State.Ready)
        { Status = "Teammate computer channel is not ready; reply was not sent."; return false; }
        return Send(peerId, peer, peer.State.Wrap(message, checked(++nextSequence)));
    }

    private bool Send(ulong id, Peer peer, ComputerPacket packet)
    {
        if (disabled || stopped || peer.Sent >= 4) { Status = "Computer channel send budget reached; message was not sent."; return false; }
        byte[] bytes = ComputerPacket.Encode(packet, !IsClient); // Local schema errors remain actionable to the caller.
        if (packet.Message?.Kind is "graph-state" or "graph-get")
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            peer.GraphBytes = Math.Min(262144, peer.GraphBytes + Math.Max(0, now - peer.GraphRefilled) * 262144);
            peer.GraphRefilled = now;
            // Leave a send slot for editor commands/acknowledgements; snapshots have a byte budget too.
            if (peer.Sent >= 3 || peer.GraphBytes < bytes.Length) return false;
            peer.GraphBytes -= bytes.Length;
        }
        try
        {
            if (!Live(id, peer)) { peer.State.Invalidate(); Status = "Computer peer disconnected; message was not sent."; return false; }
            var identity = Identity(id);
            peer.Opened = true;
            peer.Sent++;
            unsafe
            {
                fixed (byte* pointer = bytes)
                {
                    var result = SteamNetworkingMessages.SendMessageToUser(ref identity, (IntPtr)pointer, (uint)bytes.Length, 8, Channel);
                    if (result != EResult.k_EResultOK)
                    { Disable("Steam computer send failed: " + result + ". Channel stopped; request was not retried."); return false; }
                }
            }
            return true;
        }
        catch (Exception ex) { Disable("Computer channel send unavailable (" + ex.GetType().Name + ")."); return false; }
    }

    private unsafe void ReadRoster(EntityManager manager, string key, double now)
    {
        if (manager.m_EntityDataAccess == IntPtr.Zero) throw new InvalidOperationException("Missing computer link world.");
        var net = ReadSingleton<Netcore.Singleton>(manager);
        bool client = !net._isServer;
        bool roleChanged = observed && client != IsClient;
        IsClient = client; // Update before optional Steam/roster operations can fail.
        var currentWorld = manager.World;
        if (currentWorld is null || !currentWorld.IsCreated) throw new InvalidOperationException("Disposed computer link world.");
        var core = ReadSingleton<Core.Singleton>(manager);
        var steam = Core.Get()?._steam ?? throw new InvalidOperationException("Missing Steam manager.");
        ulong myId = core._mySteamID.m_SteamID;
        ulong lobbyId = steam._currentLobby.m_SteamID;
        ulong ownerId = steam._lobbyOwner.m_SteamID;
        ulong hostId = client ? ownerId : myId;
        uint handle = client ? steam._clientConnection.m_HSteamNetConnection : 0;
        if (!observed || roleChanged || world != manager.m_EntityDataAccess || worldKey != key || local != myId ||
            host != hostId || hostHandle != handle || lobby != lobbyId || lobbyOwner != ownerId)
        {
            ClearPeers();
            epoch = ComputerPacket.NewNonce();
            disabled = warned = false;
        }
        observed = true;
        owner = currentWorld;
        world = manager.m_EntityDataAccess; worldKey = key; local = myId; host = hostId; hostHandle = handle; lobby = lobbyId; lobbyOwner = ownerId;
        var roster = new Dictionary<ulong, (uint Handle, Entity Admission, long Token)>();
        bool active = client ? net._networkStatus == Netcore.NetworkStatus.Client :
            (net._networkStatus is Netcore.NetworkStatus.ServerOpen or Netcore.NetworkStatus.ServerClosed) && (lobbyId == 0 || ownerId == myId);
        if (active && !string.IsNullOrWhiteSpace(key) && core._mySteamID.IsValid() && core._mySteamID.BIndividualAccount())
        {
            var forward = steam._steamToConn;
            var reverse = steam._connToSteam;
            if (forward.IsCreated && reverse.IsCreated)
            {
                var type = ComponentType.ReadOnly<NetcoreClient>();
                var query = manager.CreateEntityQuery(new[] { type });
                try
                {
                    if (query.CalculateEntityCount() > 128) throw new InvalidOperationException("Too many native client records.");
                    var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
                    try
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            var value = (NetcoreClient*)manager.GetComponentDataRawRO(entities[i], type.TypeIndex);
                            if (value == null || !value->_isOnline || value->_steamID.m_SteamID == myId) continue;
                            var steamId = value->_steamID;
                            ulong id = steamId.m_SteamID;
                            if (!steamId.IsValid() || !steamId.BIndividualAccount() || (client && id != hostId) ||
                                !forward.TryGetValue(steamId, out var connection) || connection.m_HSteamNetConnection == 0 ||
                                (client && connection.m_HSteamNetConnection != handle) ||
                                !reverse.TryGetValue(connection, out var remote) || remote.m_SteamID != id ||
                                !ConnectionMatches(connection, id)) continue;
                            if (roster.Count >= MaxPeers || !roster.TryAdd(id, (connection.m_HSteamNetConnection, entities[i], value->_timeToken)))
                                throw new InvalidOperationException("Ambiguous or oversized native peer roster.");
                        }
                    }
                    finally { entities.Dispose(); }
                }
                finally { query.Dispose(); }
            }
        }
        foreach (ulong id in peers.Keys.ToArray())
        {
            var old = peers[id];
            if (!roster.TryGetValue(id, out var value) || value.Handle != old.Handle || value.Admission != old.Admission || value.Token != old.TimeToken)
            {
                // Conservatively invalidate the public epoch too, so main-owned queued
                // requests/cache entries cannot survive a disconnected or rebound peer.
                ClearPeers(); epoch = ComputerPacket.NewNonce(); break;
            }
        }
        foreach (var entry in roster)
            if (!peers.ContainsKey(entry.Key)) peers.Add(entry.Key, new Peer(entry.Value.Handle, entry.Value.Admission, entry.Value.Token, client, epoch, now));
        if (!disabled && peers.Count == 0) Status = client ? "Client awaiting admitted host connection; remote commands disabled." : "Host computer channel: no admitted teammates.";
    }

    private unsafe bool Live(ulong id, Peer peer)
    {
        if (owner is null || !owner.IsCreated || owner.EntityManager.m_EntityDataAccess != world) return false;
        var manager = owner.EntityManager;
        var type = ComponentType.ReadOnly<NetcoreClient>();
        if (!manager.Exists(peer.Admission) || !manager.HasComponent(peer.Admission, type)) return false;
        var admitted = (NetcoreClient*)manager.GetComponentDataRawRO(peer.Admission, type.TypeIndex);
        if (admitted == null || !admitted->_isOnline || admitted->_steamID.m_SteamID != id || admitted->_timeToken != peer.TimeToken) return false;
        var steam = Core.Get()?._steam;
        if (steam is null || steam._currentLobby.m_SteamID != lobby || steam._lobbyOwner.m_SteamID != lobbyOwner ||
            (IsClient && (id != host || steam._lobbyOwner.m_SteamID != host || steam._clientConnection.m_HSteamNetConnection != hostHandle))) return false;
        var forward = steam._steamToConn;
        var reverse = steam._connToSteam;
        return forward.IsCreated && reverse.IsCreated && forward.TryGetValue(new CSteamID { m_SteamID = id }, out var connection) &&
            connection.m_HSteamNetConnection == peer.Handle && reverse.TryGetValue(connection, out var remote) && remote.m_SteamID == id &&
            ConnectionMatches(connection, id);
    }

    private static bool ConnectionMatches(HSteamNetConnection connection, ulong id) =>
        SteamNetworkingSockets.GetConnectionInfo(connection, out var info) && info is not null &&
        info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected &&
        info.m_identityRemote.m_eType == ESteamNetworkingIdentityType.k_ESteamNetworkingIdentityType_SteamID &&
        info.m_identityRemote.GetSteamID64() == id;

    private static SteamNetworkingIdentity Identity(ulong id)
    {
        var identity = default(SteamNetworkingIdentity);
        identity.SetSteamID64(id);
        return identity;
    }

    private static unsafe T ReadSingleton<T>(EntityManager manager) where T : unmanaged
    {
        var type = ComponentType.ReadOnly<T>();
        var query = manager.CreateEntityQuery(new[] { type });
        try
        {
            if (query.CalculateEntityCount() != 1) throw new InvalidOperationException("Missing native link singleton.");
            var pointer = (T*)manager.GetComponentDataRawRO(query.GetSingletonEntity(), type.TypeIndex);
            if (pointer == null) throw new InvalidOperationException("Missing native link singleton data.");
            return *pointer;
        }
        finally { query.Dispose(); }
    }

    // Explicit owned-process -Instance diagnostic only; caller verifies solo and completes
    // jobs first. No Poll, session acceptance, sends, callback dispatch or ECS/file writes.
    internal unsafe string ProbeBindings(EntityManager manager)
    {
        CheckThread();
        if (polling || peers.Count != 0)
            throw new InvalidOperationException("Binding probe requires an idle link with zero cached peers.");
        var active = World.DefaultGameObjectInjectionWorld;
        if (active is null || !active.IsCreated || manager.m_EntityDataAccess == IntPtr.Zero ||
            active.EntityManager.m_EntityDataAccess != manager.m_EntityDataAccess)
            throw new InvalidOperationException("Binding probe requires the live default world.");
        var menu = UIManager._singleton?._mainMenu;
        if (menu is null || !menu.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Binding probe is restricted to the main menu.");
        var net = ReadSingleton<Netcore.Singleton>(manager);
        if (!net._isServer || net._networkStatus is not (Netcore.NetworkStatus.ServerOpen or Netcore.NetworkStatus.ServerClosed))
            throw new InvalidOperationException("Binding probe requires authoritative solo state.");
        var self = ReadSingleton<Core.Singleton>(manager)._mySteamID;
        if (!self.IsValid() || !self.BIndividualAccount() || self.m_SteamID == 0)
            throw new InvalidOperationException("Binding probe requires a known local Steam identity.");
        var steam = Core.Get()?._steam ?? throw new InvalidOperationException("Unknown Steam transport state.");
        var forward = steam._steamToConn;
        var reverse = steam._connToSteam;
        if (!forward.IsCreated || !reverse.IsCreated || forward.Count != 0 || reverse.Count != 0 ||
            steam._clientConnection.m_HSteamNetConnection != 0)
            throw new InvalidOperationException("Binding probe requires known zero native transport peers.");
        var type = ComponentType.ReadOnly<NetcoreClient>();
        var query = manager.CreateEntityQuery(new[] { type });
        try
        {
            if (query.CalculateEntityCount() is < 0 or > 128)
                throw new InvalidOperationException("Unknown native peer roster size.");
            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var client = (NetcoreClient*)manager.GetComponentDataRawRO(entities[i], type.TypeIndex);
                    if (client == null || (client->_isOnline && client->_steamID.m_SteamID != self.m_SteamID))
                        throw new InvalidOperationException("Binding probe found an online or unknown remote peer.");
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
        if (CSteamAPIContext.GetSteamNetworkingMessages() == IntPtr.Zero)
            throw new InvalidOperationException("Native Steam Messages interface unavailable.");
        var identity = Identity(self.m_SteamID);
        if (identity.m_eType != ESteamNetworkingIdentityType.k_ESteamNetworkingIdentityType_SteamID || identity.GetSteamID64() != self.m_SteamID)
            throw new InvalidOperationException("Native local Steam identity did not roundtrip.");
        var messages = new Il2CppStructArray<IntPtr>(BatchSize);
        int received, released = 0;
        try
        {
            received = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, messages, BatchSize);
            if (received is < 0 or > BatchSize)
                throw new InvalidOperationException("Native binding probe receive returned an invalid count.");
            for (int i = 0; i < received; i++)
                if (messages[i] == IntPtr.Zero)
                    throw new InvalidOperationException("Native binding probe receive returned a null message pointer.");
        }
        finally
        {
            Exception? releaseError = null;
            // All slots, even after a partial receive failure; discard without decoding.
            for (int i = 0; i < BatchSize; i++)
            {
                IntPtr pointer = messages[i];
                messages[i] = IntPtr.Zero;
                if (pointer == IntPtr.Zero) continue;
                try { SteamNetworkingMessage_t.Release(pointer); released++; }
                catch (Exception ex) { releaseError ??= ex; }
            }
            if (releaseError is not null) throw new InvalidOperationException("Native binding probe message release failed.", releaseError);
        }
        return System.Text.Json.JsonSerializer.Serialize(new {
            probe = "computer-link-bindings", available = true, peers = 0, identityRoundtrip = true,
            channel = Channel, receiveCapacity = BatchSize, received, released,
            releaseExercised = released != 0, singleBatch = true, connectivityTested = false
        });
    }

    internal void ResetForWorld()
    {
        CheckThread();
        ClearPeers();
        epoch = ComputerPacket.NewNonce();
        observed = disabled = warned = false;
        owner = null;
        Status = "Computer channel reset; waiting for native session.";
    }

    internal void Shutdown()
    {
        CheckThread();
        stopped = true;
        ClearPeers();
        epoch = ComputerPacket.NewNonce();
        owner = null;
        Status = "Computer channel shut down.";
    }

    private void ClearPeers()
    {
        foreach (var entry in peers) Close(entry.Key, entry.Value);
        peers.Clear();
    }

    private void Close(ulong id, Peer peer)
    {
        peer.State.Invalidate();
        if (!peer.Opened) return;
        peer.Opened = false;
        try { var identity = Identity(id); _ = SteamNetworkingMessages.CloseChannelWithUser(ref identity, Channel); }
        catch (Exception ex) { WarnOnce("Computer channel close failed (" + ex.GetType().Name + ")."); }
    }

    private void Disable(string reason)
    {
        if (!disabled) epoch = ComputerPacket.NewNonce();
        disabled = true;
        foreach (var entry in peers) Close(entry.Key, entry.Value);
        WarnOnce(reason);
    }

    private void WarnOnce(string text)
    {
        Status = text.Length <= 256 ? text : text[..256];
        if (warned) return;
        warned = true;
        try { if (log is not null) log(Status); else Trace.TraceWarning(Status); } catch { }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != mainThread) throw new InvalidOperationException("ComputerLink requires its owning main thread.");
    }
}
