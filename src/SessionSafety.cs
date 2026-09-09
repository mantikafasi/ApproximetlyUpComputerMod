namespace ApproximatelyUp.ComputerMod;

internal static class SessionSafety
{
    // Pinned native Netcore statuses: 2 = ServerOpen (also solo), 3 = ServerClosed.
    internal static bool CanWrite(bool isServer, int status, bool remotePeer, bool nativeComputer = false) =>
        isServer && (status is 2 or 3) && (!remotePeer || nativeComputer);

    internal static bool IsRemotePeer(bool online, ulong peer, ulong local) =>
        online && (local == 0 || peer == 0 || peer != local);
}
