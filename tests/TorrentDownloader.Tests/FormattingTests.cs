using TorrentDownloader.Core.Utils;

namespace TorrentDownloader.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(30, "30s")]
    [InlineData(90, "1m 30s")]
    [InlineData(3665, "1h 1m")]
    [InlineData(90000, "1d 1h")]
    public void Duration_FormatsAtTheAppropriateGranularity(int totalSeconds, string expected)
    {
        Assert.Equal(expected, Formatting.Duration(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void Eta_NoBytesRemaining_ReturnsDone()
    {
        Assert.Equal("Done", Formatting.Eta(remainingBytes: 0, rateBytesPerSecond: 500));
    }

    [Fact]
    public void Eta_ZeroRate_ReturnsUnknownMarker()
    {
        Assert.Equal("—", Formatting.Eta(remainingBytes: 1000, rateBytesPerSecond: 0));
    }

    [Fact]
    public void Eta_ComputesFromRemainingBytesAndRate()
    {
        // 5000 bytes remaining at 1000 B/s = 5 seconds.
        Assert.Equal("5s", Formatting.Eta(remainingBytes: 5000, rateBytesPerSecond: 1000));
    }

    [Fact]
    public void Eta_AbsurdlyLongEstimate_ReturnsUnknownMarkerRatherThanGarbage()
    {
        // 1 byte/s over a multi-terabyte file would be "years" - not a useful estimate.
        Assert.Equal("—", Formatting.Eta(remainingBytes: 10_000_000_000_000, rateBytesPerSecond: 1));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024 * 5, "5 MB")]
    public void ByteSize_FormatsWithAppropriateUnit(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.ByteSize(bytes));
    }
}
