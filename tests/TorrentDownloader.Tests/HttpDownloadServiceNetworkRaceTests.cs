using System.Net;
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
    public async Task ConnectionFailureWhileOnline_IsTreatedAsTransientAndAutoRetried()
    {
        // A refused/dropped connection is exactly the kind of transient blip a multi-hour,
        // many-GB download needs to survive on its own - it must not become a hard error that
        // sits there requiring a manual click every time, the way it used to.
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor, retryDelay: TimeSpan.FromMilliseconds(200));
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
            await Task.Delay(50);
        }

        Assert.NotNull(snapshot);
        Assert.Equal(HttpDownloadState.Paused, snapshot!.State);
        Assert.NotNull(snapshot.ErrorMessage);

        // And it should actually retry on its own rather than sitting idle forever.
        var sawRetry = false;
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (service.GetSnapshot().Single(d => d.Id == info.Id).State == HttpDownloadState.Connecting)
            {
                sawRetry = true;
                break;
            }
            await Task.Delay(50);
        }

        Assert.True(sawRetry, "Expected the download to automatically retry after the transient connection failure.");

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    [Fact]
    public async Task PermanentHttpError_FailsImmediately_WithoutRetrying()
    {
        // A 404/403/etc. will never succeed no matter how many times we ask - retrying would
        // just burn through the retry budget on something that can't work, and delay the user
        // finding out the link is actually dead.
        var handler = new AlwaysRespondHandler(HttpStatusCode.NotFound);
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor, retryDelay: TimeSpan.FromMilliseconds(200), httpMessageHandler: handler);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        var info = await service.AddAsync("http://fake.test/gone-forever.zip", saveDir);

        HttpDownloadInfo? snapshot = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (snapshot.State != HttpDownloadState.Connecting)
                break;
            await Task.Delay(50);
        }

        Assert.NotNull(snapshot);
        Assert.Equal(HttpDownloadState.Error, snapshot!.State);
        Assert.Contains("404", snapshot.ErrorMessage);

        // Confirm it really did fail fast rather than quietly retrying - only one request sent.
        await Task.Delay(500);
        Assert.Equal(1, handler.RequestCount);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    private sealed class AlwaysRespondHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Yield();
            return new HttpResponseMessage(statusCode) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        }
    }
}
