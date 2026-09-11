using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class HttpDownloadServiceFileNameTests
{
    [Fact]
    public void SanitizeFileName_TruncatesOverlyLongName_ButKeepsExtension()
    {
        // Mirrors the real-world case: a proxy/signed-URL path segment 300+ chars long,
        // which previously crashed the download with a path/filename-too-long error.
        var longName = new string('a', 300) + ".zip";

        var result = HttpDownloadService.SanitizeFileName(longName);

        Assert.True(result.Length <= 150);
        Assert.EndsWith(".zip", result);
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var result = HttpDownloadService.SanitizeFileName("bad:name*with?invalid<chars>.txt");

        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
        Assert.DoesNotContain('?', result);
        Assert.EndsWith(".txt", result);
    }

    [Fact]
    public void SanitizeFileName_EmptyOrWhitespace_ReturnsGeneratedName()
    {
        var result = HttpDownloadService.SanitizeFileName("   ");

        Assert.StartsWith("download-", result);
    }

    [Fact]
    public void SanitizeFileName_ShortValidName_IsUnchanged()
    {
        var result = HttpDownloadService.SanitizeFileName("report.pdf");

        Assert.Equal("report.pdf", result);
    }
}
