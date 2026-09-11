using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;
using TorrentDownloader.UI.ViewModels;
using TorrentDownloader.UI.Views;

namespace TorrentDownloader.UI;

public partial class App : Application
{
    private TorrentEngineService? _engine;
    private HttpDownloadService? _httpDownloadService;
    private NetworkMonitorService? _networkMonitor;
    private SessionStore? _sessionStore;
    private Timer? _autoSaveTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsService = new SettingsService();
            var settings = settingsService.Load();
            Directory.CreateDirectory(settings.DownloadDirectory);

            var cacheDirectory = Path.Combine(settingsService.AppDataDirectory, "cache");
            Directory.CreateDirectory(cacheDirectory);

            _networkMonitor = new NetworkMonitorService();
            _engine = new TorrentEngineService(settings, cacheDirectory, _networkMonitor);
            _httpDownloadService = new HttpDownloadService(_networkMonitor);
            _httpDownloadService.SetDownloadRateLimitKBs(settings.MaxDownloadRateKBs);
            _sessionStore = new SessionStore(settingsService.AppDataDirectory);

            var mainViewModel = new MainViewModel(_engine, _httpDownloadService, settingsService, settings);

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };

            // Fire-and-forget: restoring shouldn't block the window from showing. Items pop into
            // the list as each one is re-added, which also reads fine as "resuming your downloads".
            var savedSession = _sessionStore.Load();
            _ = RestoreSessionAsync(savedSession);

            // Belt-and-suspenders against a non-graceful exit (crash, task-kill, power loss):
            // ShutdownRequested below covers the normal "close the window" path, but this catches
            // progress that would otherwise only be reflected in MonoTorrent's own fast-resume
            // cache (torrents) with nothing at all for plain HTTP downloads.
            _autoSaveTimer = new Timer(_ => SaveSession(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            desktop.ShutdownRequested += async (_, _) =>
            {
                SaveSession();
                mainViewModel.Dispose();
                _httpDownloadService.Dispose();
                await _engine.DisposeAsync();
                _networkMonitor.Dispose();
                _autoSaveTimer?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task RestoreSessionAsync(SessionState session)
    {
        if (_engine is not null)
            await _engine.RestoreAsync(session.Torrents);

        _httpDownloadService?.Restore(session.Downloads);
    }

    private void SaveSession()
    {
        if (_engine is null || _httpDownloadService is null || _sessionStore is null)
            return;

        _sessionStore.Save(new SessionState
        {
            Torrents = new(_engine.GetSessionEntries()),
            Downloads = new(_httpDownloadService.GetSessionEntries()),
        });
    }
}
