using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class HttpDownloadServiceTests
{
    [Fact]
    public async Task AddAsync_DownloadsSmallFile_CompletesSuccessfully()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        var info = await service.AddAsync("https://releases.ubuntu.com/24.04.5/SHA256SUMS", saveDir);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        HttpDownloadInfo? final = null;
        while (DateTime.UtcNow < deadline)
        {
            final = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (final.State is HttpDownloadState.Completed or HttpDownloadState.Error)
                break;
            await Task.Delay(200);
        }

        Assert.NotNull(final);
        Assert.Equal(HttpDownloadState.Completed, final!.State);
        Assert.True(final.DownloadedBytes > 0);
        Assert.True(File.Exists(Path.Combine(saveDir, "SHA256SUMS")));
        Assert.Equal(final.DownloadedBytes, new FileInfo(Path.Combine(saveDir, "SHA256SUMS")).Length);
    }
}
