using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class SettingsServiceTests
{
    private static string NewTempAppDataDir() =>
        Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

    [Fact]
    public void Load_WithNoExistingFile_ReturnsDefaults()
    {
        var service = new SettingsService(NewTempAppDataDir());

        var settings = service.Load();

        Assert.False(string.IsNullOrWhiteSpace(settings.DownloadDirectory));
        Assert.Equal(55123, settings.ListenPort);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsCustomValues()
    {
        var appDataDir = NewTempAppDataDir();
        var service = new SettingsService(appDataDir);

        var original = service.Load();
        original.DownloadDirectory = Path.Combine(appDataDir, "Downloads");
        original.ListenPort = 12345;
        original.MaxDownloadRateKBs = 500;

        service.Save(original);

        var reloaded = new SettingsService(appDataDir).Load();

        Assert.Equal(original.DownloadDirectory, reloaded.DownloadDirectory);
        Assert.Equal(original.ListenPort, reloaded.ListenPort);
        Assert.Equal(original.MaxDownloadRateKBs, reloaded.MaxDownloadRateKBs);
    }
}
