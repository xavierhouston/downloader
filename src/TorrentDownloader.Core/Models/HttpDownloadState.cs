namespace TorrentDownloader.Core.Models;

public enum HttpDownloadState
{
    Connecting,
    Downloading,
    Paused,
    Completed,
    Error,
}
