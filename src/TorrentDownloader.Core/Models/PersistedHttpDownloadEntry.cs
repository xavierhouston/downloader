namespace TorrentDownloader.Core.Models;

/// <summary>Enough to resume a direct-link download (from the correct byte offset) on the next launch.</summary>
public sealed record PersistedHttpDownloadEntry(string Id, string Url, string SaveDirectory, string FileName, long DownloadedBytes);
