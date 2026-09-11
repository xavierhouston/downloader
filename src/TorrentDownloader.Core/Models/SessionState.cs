namespace TorrentDownloader.Core.Models;

public sealed class SessionState
{
    public List<PersistedTorrentEntry> Torrents { get; set; } = new();
    public List<PersistedHttpDownloadEntry> Downloads { get; set; } = new();
}
