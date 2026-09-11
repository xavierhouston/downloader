namespace TorrentDownloader.Core.Models;

/// <summary>
/// Enough to re-add a torrent on the next launch. For file-added torrents, Source points at
/// our own copy under the app's data directory (not the user's original file, which might get
/// moved or deleted) rather than the original path the user picked.
/// </summary>
public sealed record PersistedTorrentEntry(string Id, string Kind, string Source, string SaveDirectory);
