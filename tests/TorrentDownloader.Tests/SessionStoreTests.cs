using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class SessionStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsTorrentsAndDownloads()
    {
        var appDataDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());
        var store = new SessionStore(appDataDir);

        var session = new SessionState
        {
            Torrents = new List<PersistedTorrentEntry>
            {
                new(Guid.NewGuid().ToString(), "magnet", "magnet:?xt=urn:btih:abc123", @"C:\Downloads"),
            },
            Downloads = new List<PersistedHttpDownloadEntry>
            {
                new(Guid.NewGuid().ToString(), "https://example.com/file.zip", @"C:\Downloads", "file.zip", 12345),
            },
        };

        store.Save(session);
        var reloaded = new SessionStore(appDataDir).Load();

        Assert.Single(reloaded.Torrents);
        Assert.Equal(session.Torrents[0].Source, reloaded.Torrents[0].Source);
        Assert.Single(reloaded.Downloads);
        Assert.Equal(session.Downloads[0].DownloadedBytes, reloaded.Downloads[0].DownloadedBytes);
    }

    [Fact]
    public void Load_WithNoExistingFile_ReturnsEmptySession()
    {
        var appDataDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());
        var store = new SessionStore(appDataDir);

        var session = store.Load();

        Assert.Empty(session.Torrents);
        Assert.Empty(session.Downloads);
    }
}
