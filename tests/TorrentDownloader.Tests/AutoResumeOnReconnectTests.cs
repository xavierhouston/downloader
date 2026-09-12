using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class AutoResumeOnReconnectTests
{
    [Fact]
    public async Task HttpDownload_PausesOnConnectivityLoss_AndResumesWhenBackOnline()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        // A slow-ish, real file so the download is still in flight when we flip connectivity.
        var info = await service.AddAsync("https://releases.ubuntu.com/24.04.5/ubuntu-24.04.4-desktop-amd64.iso", saveDir);

        // Wait until it's actually started transferring.
        await WaitForStateAsync(service, info.Id, s => s is HttpDownloadState.Downloading, TimeSpan.FromSeconds(15));

        networkMonitor.ForceState(false);
        await WaitForStateAsync(service, info.Id, s => s == HttpDownloadState.Paused, TimeSpan.FromSeconds(5));

        var pausedSnapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
        Assert.Equal(HttpDownloadState.Paused, pausedSnapshot.State);
        var bytesAtPause = pausedSnapshot.DownloadedBytes;
        Assert.True(bytesAtPause > 0);

        networkMonitor.ForceState(true);
        await WaitForStateAsync(service, info.Id, s => s is HttpDownloadState.Downloading or HttpDownloadState.Connecting, TimeSpan.FromSeconds(5));

        // Give it a moment to actually make further progress after resuming.
        await Task.Delay(1500);
        var resumedSnapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
        Assert.True(resumedSnapshot.DownloadedBytes >= bytesAtPause);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    [Fact]
    public async Task ManualPause_IsNotAutoResumedOnReconnect()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        var info = await service.AddAsync("https://releases.ubuntu.com/24.04.5/ubuntu-24.04.4-desktop-amd64.iso", saveDir);
        await WaitForStateAsync(service, info.Id, s => s is HttpDownloadState.Downloading, TimeSpan.FromSeconds(15));

        await service.PauseAsync(info.Id);
        Assert.Equal(HttpDownloadState.Paused, service.GetSnapshot().Single(d => d.Id == info.Id).State);

        // A connectivity flip while manually paused must not resurrect it.
        networkMonitor.ForceState(false);
        networkMonitor.ForceState(true);
        await Task.Delay(1000);

        Assert.Equal(HttpDownloadState.Paused, service.GetSnapshot().Single(d => d.Id == info.Id).State);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    private static async Task WaitForStateAsync(HttpDownloadService service, Guid id, Func<HttpDownloadState, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = service.GetSnapshot().FirstOrDefault(d => d.Id == id);
            if (snapshot is not null && predicate(snapshot.State))
                return;
            await Task.Delay(100);
        }

        Assert.Fail($"Timed out waiting for expected state within {timeout}.");
    }
}
