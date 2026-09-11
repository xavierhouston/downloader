using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class HttpDownloadServiceSessionTests
{
    [Fact]
    public async Task GetSessionEntries_ExcludesCompletedDownloads()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        var info = await service.AddAsync("https://releases.ubuntu.com/24.04.5/SHA256SUMS", saveDir);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && service.GetSnapshot().Single(d => d.Id == info.Id).State != HttpDownloadState.Completed)
            await Task.Delay(200);

        Assert.Empty(service.GetSessionEntries());
    }

    [Fact]
    public void Restore_ReadsActualBytesFromDiskRatherThanTrustingSavedRecord()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);

        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(saveDir);
        var filePath = Path.Combine(saveDir, "partial.bin");
        var actualBytesOnDisk = new byte[777];
        File.WriteAllBytes(filePath, actualBytesOnDisk);

        // Saved record claims far more progress than what's actually on disk (simulating a
        // crash mid-write after the last periodic session save) - restore must trust the file,
        // not the stale record, or resuming would corrupt the download.
        var id = Guid.NewGuid();
        var staleRecord = new PersistedHttpDownloadEntry(id.ToString(), "https://example.invalid/partial.bin", saveDir, "partial.bin", DownloadedBytes: 999_999);

        service.Restore(new[] { staleRecord });

        var snapshot = service.GetSnapshot().Single(d => d.Id == id);
        Assert.Equal(777, snapshot.DownloadedBytes);
    }

    [Fact]
    public void Restore_WithMissingFile_StartsFromZero()
    {
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor);

        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());
        var id = Guid.NewGuid();
        var record = new PersistedHttpDownloadEntry(id.ToString(), "https://example.invalid/gone.bin", saveDir, "gone.bin", DownloadedBytes: 500);

        service.Restore(new[] { record });

        var snapshot = service.GetSnapshot().Single(d => d.Id == id);
        Assert.Equal(0, snapshot.DownloadedBytes);
    }
}
