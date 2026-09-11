using CommunityToolkit.Mvvm.ComponentModel;
using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Utils;

namespace TorrentDownloader.UI.ViewModels;

public partial class HttpDownloadItemViewModel : ViewModelBase
{
    public Guid Id { get; }

    [ObservableProperty]
    public partial string FileName { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; }

    [ObservableProperty]
    public partial string DownloadRateText { get; set; }

    [ObservableProperty]
    public partial string EtaText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    public HttpDownloadItemViewModel(HttpDownloadInfo info)
    {
        Id = info.Id;
        FileName = info.FileName;
        UpdateFrom(info);
    }

    public void UpdateFrom(HttpDownloadInfo info)
    {
        FileName = info.FileName;
        ProgressPercent = info.ProgressPercent;
        StateText = info.ErrorMessage is not null ? $"Error: {info.ErrorMessage}" : info.State.ToString();
        SizeText = info.TotalBytes > 0
            ? $"{Formatting.ByteSize(info.DownloadedBytes)} / {Formatting.ByteSize(info.TotalBytes)}"
            : Formatting.ByteSize(info.DownloadedBytes);
        DownloadRateText = info.State == HttpDownloadState.Downloading ? Formatting.Rate(info.DownloadRateBytesPerSecond) : "";
        EtaText = info.State == HttpDownloadState.Downloading
            ? Formatting.Eta(Math.Max(0, info.TotalBytes - info.DownloadedBytes), info.DownloadRateBytesPerSecond)
            : "";
        IsPaused = info.State is HttpDownloadState.Paused or HttpDownloadState.Error;
        IsCompleted = info.State == HttpDownloadState.Completed;
    }
}
