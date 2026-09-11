namespace TorrentDownloader.Core.Services;

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = "";
    public int ListenPort { get; set; } = 55123;
    public int MaxDownloadRateKBs { get; set; } = 0;
    public int MaxUploadRateKBs { get; set; } = 0;
}
