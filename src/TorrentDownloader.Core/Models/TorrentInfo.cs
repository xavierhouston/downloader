namespace TorrentDownloader.Core.Models;

public sealed record TorrentInfo(
    Guid Id,
    string Name,
    long TotalSizeBytes,
    double ProgressPercent,
    TorrentDownloadState State,
    long DownloadRateBytesPerSecond,
    long UploadRateBytesPerSecond,
    int PeersConnected,
    int PeersAvailable,
    int SeedsConnected,
    int LeechesConnected,
    string TrackerStatus,
    string SavePath,
    bool HasMetadata
);
