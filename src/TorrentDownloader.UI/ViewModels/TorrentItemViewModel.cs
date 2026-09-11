using CommunityToolkit.Mvvm.ComponentModel;
using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Utils;

namespace TorrentDownloader.UI.ViewModels;

public partial class TorrentItemViewModel : ViewModelBase
{
    public Guid Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; }

    [ObservableProperty]
    public partial string DownloadRateText { get; set; }

    [ObservableProperty]
    public partial string UploadRateText { get; set; }

    [ObservableProperty]
    public partial string PeersText { get; set; }

    [ObservableProperty]
    public partial string TrackerStatusText { get; set; }

    [ObservableProperty]
    public partial string EtaText { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    public TorrentItemViewModel(TorrentInfo info)
    {
        Id = info.Id;
        Name = info.Name;
        ProgressPercent = info.ProgressPercent * 100;
        StateText = info.State.ToString();
        SizeText = Formatting.ByteSize(info.TotalSizeBytes);
        DownloadRateText = Formatting.Rate(info.DownloadRateBytesPerSecond);
        UploadRateText = Formatting.Rate(info.UploadRateBytesPerSecond);
        PeersText = BuildPeersText(info);
        TrackerStatusText = info.TrackerStatus;
        EtaText = BuildEtaText(info);
        IsPaused = info.State is TorrentDownloadState.Paused or TorrentDownloadState.Stopped;
    }

    public void UpdateFrom(TorrentInfo info)
    {
        Name = info.Name;
        ProgressPercent = info.ProgressPercent * 100;
        StateText = info.State.ToString();
        SizeText = Formatting.ByteSize(info.TotalSizeBytes);
        DownloadRateText = Formatting.Rate(info.DownloadRateBytesPerSecond);
        UploadRateText = Formatting.Rate(info.UploadRateBytesPerSecond);
        PeersText = BuildPeersText(info);
        TrackerStatusText = info.TrackerStatus;
        EtaText = BuildEtaText(info);
        IsPaused = info.State is TorrentDownloadState.Paused or TorrentDownloadState.Stopped;
    }

    private static string BuildPeersText(TorrentInfo info) =>
        $"{info.PeersConnected} peers (S:{info.SeedsConnected} L:{info.LeechesConnected})";

    private static string BuildEtaText(TorrentInfo info)
    {
        if (info.State is TorrentDownloadState.Paused or TorrentDownloadState.Stopped or TorrentDownloadState.Error)
            return "—";

        var remainingBytes = Math.Max(0, (long)(info.TotalSizeBytes * (1 - info.ProgressPercent)));
        return Formatting.Eta(remainingBytes, info.DownloadRateBytesPerSecond);
    }
}
