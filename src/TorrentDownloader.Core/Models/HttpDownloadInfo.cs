namespace TorrentDownloader.Core.Models;

public sealed record HttpDownloadInfo(
    Guid Id,
    string Url,
    string FileName,
    long TotalBytes,
    long DownloadedBytes,
    double ProgressPercent,
    HttpDownloadState State,
    long DownloadRateBytesPerSecond,
    string SavePath,
    string? ErrorMessage
);
