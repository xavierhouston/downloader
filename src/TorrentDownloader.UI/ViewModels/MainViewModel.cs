using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.UI.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly TorrentEngineService _engine;
    private readonly HttpDownloadService _httpDownloadService;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<TorrentItemViewModel> Torrents { get; } = new();
    public ObservableCollection<HttpDownloadItemViewModel> Downloads { get; } = new();

    [ObservableProperty]
    public partial string MagnetInput { get; set; } = "";

    [ObservableProperty]
    public partial string LinkInput { get; set; } = "";

    [ObservableProperty]
    public partial string DownloadDirectory { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsOffline { get; set; }

    /// <summary>Global download rate limit in KB/s. 0 means unlimited.</summary>
    [ObservableProperty]
    public partial int DownloadLimitKBs { get; set; }

    /// <summary>Global upload rate limit in KB/s. 0 means unlimited.</summary>
    [ObservableProperty]
    public partial int UploadLimitKBs { get; set; }

    [ObservableProperty]
    public partial string DiagnosticsText { get; set; } = "";

    public MainViewModel(TorrentEngineService engine, HttpDownloadService httpDownloadService, SettingsService settingsService, AppSettings settings)
    {
        _engine = engine;
        _httpDownloadService = httpDownloadService;
        _settingsService = settingsService;
        _settings = settings;
        DownloadDirectory = settings.DownloadDirectory;
        DownloadLimitKBs = settings.MaxDownloadRateKBs;
        UploadLimitKBs = settings.MaxUploadRateKBs;
        IsOffline = !engine.IsOnline;

        _engine.ConnectivityChanged += OnEngineConnectivityChanged;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshSnapshot();
        _refreshTimer.Start();
    }

    private void OnEngineConnectivityChanged(object? sender, bool isOnline) =>
        Dispatcher.UIThread.Post(() => IsOffline = !isOnline);

    private void RefreshSnapshot()
    {
        var snapshot = _engine.GetSnapshot();
        var seenIds = new HashSet<Guid>();

        foreach (var info in snapshot)
        {
            seenIds.Add(info.Id);
            var existing = Torrents.FirstOrDefault(t => t.Id == info.Id);
            if (existing is null)
                Torrents.Add(new TorrentItemViewModel(info));
            else
                existing.UpdateFrom(info);
        }

        for (var i = Torrents.Count - 1; i >= 0; i--)
        {
            if (!seenIds.Contains(Torrents[i].Id))
                Torrents.RemoveAt(i);
        }

        var diagnostics = _engine.GetDiagnostics();
        DiagnosticsText = $"DHT: {diagnostics.DhtStatus} ({diagnostics.DhtNodeCount} nodes) | Port forwarding: {diagnostics.PortForwardingStatus}";

        var downloadSnapshot = _httpDownloadService.GetSnapshot();
        var seenDownloadIds = new HashSet<Guid>();

        foreach (var info in downloadSnapshot)
        {
            seenDownloadIds.Add(info.Id);
            var existing = Downloads.FirstOrDefault(d => d.Id == info.Id);
            if (existing is null)
                Downloads.Add(new HttpDownloadItemViewModel(info));
            else
                existing.UpdateFrom(info);
        }

        for (var i = Downloads.Count - 1; i >= 0; i--)
        {
            if (!seenDownloadIds.Contains(Downloads[i].Id))
                Downloads.RemoveAt(i);
        }
    }

    public async Task AddMagnetAsync()
    {
        var uri = MagnetInput.Trim();
        if (string.IsNullOrEmpty(uri))
            return;

        try
        {
            StatusMessage = null;
            await _engine.AddMagnetAsync(uri, DownloadDirectory);
            MagnetInput = "";
            RefreshSnapshot();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to add magnet link: {ex.Message}";
        }
    }

    public async Task AddTorrentFileAsync(string filePath)
    {
        try
        {
            StatusMessage = null;
            await _engine.AddTorrentFileAsync(filePath, DownloadDirectory);
            RefreshSnapshot();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to add torrent file: {ex.Message}";
        }
    }

    public async Task AddLinkAsync()
    {
        var url = LinkInput.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusMessage = "Please enter a valid http:// or https:// link.";
            return;
        }

        try
        {
            StatusMessage = null;
            await _httpDownloadService.AddAsync(url, DownloadDirectory);
            LinkInput = "";
            RefreshSnapshot();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start download: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ResumeDownloadAsync(HttpDownloadItemViewModel item)
    {
        await _httpDownloadService.ResumeAsync(item.Id);
        RefreshSnapshot();
    }

    [RelayCommand]
    private async Task UpdateLinkAndResumeAsync(HttpDownloadItemViewModel item)
    {
        var newUrl = item.NewUrlInput.Trim();
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusMessage = "Please enter a valid http:// or https:// link.";
            return;
        }

        await _httpDownloadService.UpdateUrlAndResumeAsync(item.Id, newUrl);
        item.NewUrlInput = "";
        RefreshSnapshot();
    }

    [RelayCommand]
    private async Task PauseDownloadAsync(HttpDownloadItemViewModel item)
    {
        await _httpDownloadService.PauseAsync(item.Id);
        RefreshSnapshot();
    }

    [RelayCommand]
    private async Task RemoveDownloadAsync(HttpDownloadItemViewModel item)
    {
        await _httpDownloadService.RemoveAsync(item.Id, deleteFile: false);
        RefreshSnapshot();
    }

    [RelayCommand]
    private async Task StartAsync(TorrentItemViewModel item)
    {
        await _engine.StartAsync(item.Id);
        RefreshSnapshot();
    }

    [RelayCommand]
    private async Task PauseAsync(TorrentItemViewModel item)
    {
        await _engine.PauseAsync(item.Id);
        RefreshSnapshot();
    }

    [RelayCommand]
    private async Task RemoveAsync(TorrentItemViewModel item)
    {
        await _engine.RemoveAsync(item.Id, deleteData: false);
        RefreshSnapshot();
    }

    partial void OnDownloadDirectoryChanged(string value)
    {
        _settings.DownloadDirectory = value;
        _settingsService.Save(_settings);
    }

    partial void OnDownloadLimitKBsChanged(int value)
    {
        _settings.MaxDownloadRateKBs = value;
        _settingsService.Save(_settings);
        _ = _engine.UpdateRateLimitsAsync(DownloadLimitKBs, UploadLimitKBs);
        _httpDownloadService.SetDownloadRateLimitKBs(value);
    }

    partial void OnUploadLimitKBsChanged(int value)
    {
        _settings.MaxUploadRateKBs = value;
        _settingsService.Save(_settings);
        _ = _engine.UpdateRateLimitsAsync(DownloadLimitKBs, UploadLimitKBs);
    }

    public void Dispose()
    {
        _engine.ConnectivityChanged -= OnEngineConnectivityChanged;
        _refreshTimer.Stop();
    }
}
