namespace TorrentDownloader.Core.Models;

public enum TorrentDownloadState
{
    Stopped,
    Paused,
    Starting,
    Downloading,
    Seeding,
    Hashing,
    HashingPaused,
    Stopping,
    Error,
    Metadata,
    FetchingHashes,
}
