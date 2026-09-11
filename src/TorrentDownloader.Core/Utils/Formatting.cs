namespace TorrentDownloader.Core.Utils;

public static class Formatting
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

    public static string ByteSize(long bytes)
    {
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < SizeUnits.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {SizeUnits[unitIndex]}";
    }

    public static string Rate(long bytesPerSecond) => $"{ByteSize(bytesPerSecond)}/s";

    public static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m {span.Seconds}s";

        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
    }

    /// <summary>ETA from remaining bytes and current speed. "Done" once nothing is left,
    /// "—" when it can't be estimated (stalled/paused, or an absurdly long estimate).</summary>
    public static string Eta(long remainingBytes, long rateBytesPerSecond)
    {
        if (remainingBytes <= 0)
            return "Done";
        if (rateBytesPerSecond <= 0)
            return "—";

        var seconds = remainingBytes / (double)rateBytesPerSecond;
        if (double.IsInfinity(seconds) || seconds > 99 * 24 * 3600) // >99 days isn't a useful estimate
            return "—";

        return Duration(TimeSpan.FromSeconds(seconds));
    }
}
