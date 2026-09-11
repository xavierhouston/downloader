using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class HttpDownloadServiceNetworkRaceTests
{
    [Fact]
    public async Task ConnectionFailureWhileOffline_IsTreatedAsAutoPause_NotAHardError()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        // Simulates the real-world race: the network is already down when the transport
        // throws (a DNS failure stands in for "connection aborted" - both land in the same
        // catch block), which previously left the download stuck in Error with no way to
        // auto-resume since it was never added to the auto-pause set.
        networkMonitor.ForceState(false);

        // A local port with nothing listening fails almost instantly with "connection refused" -
        // unlike a DNS lookup against an unreachable host, which can take many seconds to time out.
        var info = await service.AddAsync("http://127.0.0.1:1/file.zip", saveDir);

        HttpDownloadInfo? snapshot = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (snapshot.State != HttpDownloadState.Connecting)
                break;
            await Task.Delay(100);
        }

        Assert.NotNull(snapshot);
        Assert.Equal(HttpDownloadState.Paused, snapshot!.State);

        // Reconnecting must actually retry it, proving it was tracked for auto-resume
        // rather than silently dropped.
        networkMonitor.ForceState(true);
        await Task.Delay(500);

        var afterReconnect = service.GetSnapshot().Single(d => d.Id == info.Id);
        Assert.NotEqual(HttpDownloadState.Paused, afterReconnect.State);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    [Fact]
    public async Task ConnectionFailureWhileOnline_IsStillAGenuineError()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        networkMonitor.ForceState(true);

        var info = await service.AddAsync("http://127.0.0.1:1/file.zip", saveDir);

        HttpDownloadInfo? snapshot = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (snapshot.State != HttpDownloadState.Connecting)
                break;
            await Task.Delay(100);
        }

        Assert.NotNull(snapshot);
        Assert.Equal(HttpDownloadState.Error, snapshot!.State);
        Assert.NotNull(snapshot.ErrorMessage);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }
}
