using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class TorrentEngineServiceTests
{
    private static (AppSettings settings, string cacheDir) NewTestConfig()
    {
        var settings = new AppSettings
        {
            DownloadDirectory = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString()),
            ListenPort = 0, // let the OS pick a free port so parallel test runs don't collide
        };
        var cacheDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());
        return (settings, cacheDir);
    }

    [Fact]
    public async Task UpdateRateLimitsAsync_AppliesAndExposesNewLimits()
    {
        var (settings, cacheDir) = NewTestConfig();
        using var networkMonitor = new NetworkMonitorService();
        await using var engine = new TorrentEngineService(settings, cacheDir, networkMonitor);

        Assert.Equal(0, engine.MaxDownloadRateKBs);
        Assert.Equal(0, engine.MaxUploadRateKBs);

        await engine.UpdateRateLimitsAsync(maxDownloadKBs: 500, maxUploadKBs: 100);

        Assert.Equal(500, engine.MaxDownloadRateKBs);
        Assert.Equal(100, engine.MaxUploadRateKBs);
    }

    [Fact]
    public async Task UpdateRateLimitsAsync_ZeroMeansUnlimited()
    {
        var (settings, cacheDir) = NewTestConfig();
        using var networkMonitor = new NetworkMonitorService();
        await using var engine = new TorrentEngineService(settings, cacheDir, networkMonitor);

        await engine.UpdateRateLimitsAsync(maxDownloadKBs: 200, maxUploadKBs: 200);
        await engine.UpdateRateLimitsAsync(maxDownloadKBs: 0, maxUploadKBs: 0);

        Assert.Equal(0, engine.MaxDownloadRateKBs);
        Assert.Equal(0, engine.MaxUploadRateKBs);
    }

    [Fact]
    public async Task AddMagnetAsync_RecordsSessionEntry_AndRemoveClearsIt()
    {
        var (settings, cacheDir) = NewTestConfig();
        using var networkMonitor = new NetworkMonitorService();
        await using var engine = new TorrentEngineService(settings, cacheDir, networkMonitor);

        const string magnet = "magnet:?xt=urn:btih:0123456789012345678901234567890123456789&dn=test-torrent";
        var info = await engine.AddMagnetAsync(magnet, settings.DownloadDirectory);

        var sessionEntries = engine.GetSessionEntries();
        Assert.Single(sessionEntries);
        Assert.Equal("magnet", sessionEntries[0].Kind);
        Assert.Equal(magnet, sessionEntries[0].Source);

        await engine.RemoveAsync(info.Id, deleteData: false);

        Assert.Empty(engine.GetSessionEntries());
    }

    [Fact]
    public async Task RestoreAsync_ReAddsSavedMagnet_WithSameId()
    {
        var (settings, cacheDir) = NewTestConfig();
        using var networkMonitor = new NetworkMonitorService();
        await using var engine = new TorrentEngineService(settings, cacheDir, networkMonitor);

        const string magnet = "magnet:?xt=urn:btih:9876543210987654321098765432109876543210&dn=restored-torrent";
        var savedId = Guid.NewGuid();
        var saved = new TorrentDownloader.Core.Models.PersistedTorrentEntry(savedId.ToString(), "magnet", magnet, settings.DownloadDirectory);

        await engine.RestoreAsync(new[] { saved });

        var snapshot = engine.GetSnapshot();
        Assert.Contains(snapshot, t => t.Id == savedId);
    }
}
