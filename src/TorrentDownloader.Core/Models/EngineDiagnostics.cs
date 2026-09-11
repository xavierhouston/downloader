namespace TorrentDownloader.Core.Models;

/// <summary>
/// Global engine-level health, surfaced so the UI (or a user report) can tell
/// "the swarm/network is the problem" apart from "the app is stuck".
/// </summary>
public sealed record EngineDiagnostics(
    string DhtStatus,
    int DhtNodeCount,
    string PortForwardingStatus,
    bool IsOnline
);
