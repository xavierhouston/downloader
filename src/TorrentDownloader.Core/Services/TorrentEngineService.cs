using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using TorrentDownloader.Core.Models;

namespace TorrentDownloader.Core.Services;

public sealed class TorrentEngineService : IAsyncDisposable
{
    private readonly ClientEngine _engine;
    private readonly ConcurrentDictionary<Guid, TorrentManager> _managers = new();
    private readonly ConcurrentDictionary<Guid, PersistedTorrentEntry> _sessionEntries = new();
    private readonly string _torrentStoreDirectory;
    private readonly NetworkMonitorService _networkMonitor;
    private readonly HashSet<Guid> _autoPausedIds = new();
    private readonly object _autoPauseLock = new();

    /// <summary>Raised when the machine's network connectivity comes online/offline.</summary>
    public event EventHandler<bool>? ConnectivityChanged
    {
        add => _networkMonitor.ConnectivityChanged += value;
        remove => _networkMonitor.ConnectivityChanged -= value;
    }

    public bool IsOnline => _networkMonitor.IsOnline;

    public int MaxDownloadRateKBs => _engine.Settings.MaximumDownloadRate > 0 ? _engine.Settings.MaximumDownloadRate / 1024 : 0;
    public int MaxUploadRateKBs => _engine.Settings.MaximumUploadRate > 0 ? _engine.Settings.MaximumUploadRate / 1024 : 0;

    public TorrentEngineService(AppSettings settings, string cacheDirectory, NetworkMonitorService networkMonitor)
    {
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowPortForwarding = true,
            AllowLocalPeerDiscovery = true,
            // The default DhtEndPoint is 0.0.0.0:0, which never binds a UDP socket, so DHT
            // peer discovery silently never starts and the client is limited to whatever
            // peers the tracker happens to hand out. Bind it explicitly to get DHT working.
            DhtEndPoint = new IPEndPoint(IPAddress.Any, settings.ListenPort),
            // Default of 8 throttles how fast we can open new peer connections when a lot of
            // candidates are available (e.g. right after a tracker/DHT response), which slows
            // down how quickly the swarm ramps up to full speed. 25 lets us dial more at once.
            MaximumHalfOpenConnections = 25,
            MaximumDownloadRate = settings.MaxDownloadRateKBs > 0 ? settings.MaxDownloadRateKBs * 1024 : 0,
            MaximumUploadRate = settings.MaxUploadRateKBs > 0 ? settings.MaxUploadRateKBs * 1024 : 0,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Any, settings.ListenPort) },
            },
        };

        _engine = new ClientEngine(builder.ToSettings());

        _torrentStoreDirectory = Path.Combine(cacheDirectory, "saved-torrents");
        Directory.CreateDirectory(_torrentStoreDirectory);

        _networkMonitor = networkMonitor;
        _networkMonitor.ConnectivityChanged += OnConnectivityChanged;
    }

    public async Task<TorrentInfo> AddTorrentFileAsync(string torrentFilePath, string saveDirectory)
    {
        // Copy into our own store rather than depending on the user's original file staying put -
        // it may get moved/deleted, which would otherwise silently break restoring this torrent
        // after a restart.
        var storedPath = Path.Combine(_torrentStoreDirectory, $"{Guid.NewGuid():N}.torrent");
        File.Copy(torrentFilePath, storedPath, overwrite: true);
        return await AddTorrentFromStoredFileAsync(storedPath, saveDirectory, Guid.NewGuid()).ConfigureAwait(false);
    }

    public Task<TorrentInfo> AddMagnetAsync(string magnetUri, string saveDirectory) =>
        AddMagnetInternalAsync(magnetUri, saveDirectory, Guid.NewGuid());

    /// <summary>Re-adds everything that was active in a previous session. Broken/missing entries
    /// are skipped individually so one bad entry can't stop the rest of the session restoring.</summary>
    public async Task RestoreAsync(IEnumerable<PersistedTorrentEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!Guid.TryParse(entry.Id, out var id))
                continue;

            try
            {
                if (entry.Kind == "file" && File.Exists(entry.Source))
                    await AddTorrentFromStoredFileAsync(entry.Source, entry.SaveDirectory, id).ConfigureAwait(false);
                else if (entry.Kind == "magnet")
                    await AddMagnetInternalAsync(entry.Source, entry.SaveDirectory, id).ConfigureAwait(false);
            }
            catch
            {
                // Skip it - a stale/corrupt saved entry must not block the rest of the session.
            }
        }
    }

    private async Task<TorrentInfo> AddTorrentFromStoredFileAsync(string storedTorrentPath, string saveDirectory, Guid id)
    {
        Directory.CreateDirectory(saveDirectory);
        var torrent = await Torrent.LoadAsync(storedTorrentPath).ConfigureAwait(false);
        var manager = await _engine.AddAsync(torrent, saveDirectory).ConfigureAwait(false);
        _managers[id] = manager;
        _sessionEntries[id] = new PersistedTorrentEntry(id.ToString(), "file", storedTorrentPath, saveDirectory);
        await manager.StartAsync().ConfigureAwait(false);
        return ToTorrentInfo(id, manager);
    }

    private async Task<TorrentInfo> AddMagnetInternalAsync(string magnetUri, string saveDirectory, Guid id)
    {
        Directory.CreateDirectory(saveDirectory);
        var magnet = MagnetLink.Parse(magnetUri);
        var manager = await _engine.AddAsync(magnet, saveDirectory).ConfigureAwait(false);
        _managers[id] = manager;
        _sessionEntries[id] = new PersistedTorrentEntry(id.ToString(), "magnet", magnetUri, saveDirectory);
        await manager.StartAsync().ConfigureAwait(false);
        return ToTorrentInfo(id, manager);
    }

    public async Task StartAsync(Guid id)
    {
        lock (_autoPauseLock) _autoPausedIds.Remove(id);
        if (_managers.TryGetValue(id, out var manager))
            await manager.StartAsync().ConfigureAwait(false);
    }

    public async Task PauseAsync(Guid id)
    {
        // A manual pause always wins: forget any auto-pause bookkeeping so
        // reconnecting won't override the user's choice and resume it.
        lock (_autoPauseLock) _autoPausedIds.Remove(id);
        if (_managers.TryGetValue(id, out var manager))
            await manager.PauseAsync().ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid id, bool deleteData)
    {
        lock (_autoPauseLock) _autoPausedIds.Remove(id);

        if (_sessionEntries.TryRemove(id, out var sessionEntry) && sessionEntry.Kind == "file")
        {
            try { File.Delete(sessionEntry.Source); }
            catch (IOException) { /* best-effort cleanup of our internal copy */ }
        }

        if (!_managers.TryRemove(id, out var manager))
            return;

        await manager.StopAsync().ConfigureAwait(false);
        await _engine.RemoveAsync(
            manager,
            deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly).ConfigureAwait(false);
    }

    public async Task UpdateRateLimitsAsync(int maxDownloadKBs, int maxUploadKBs)
    {
        var builder = new EngineSettingsBuilder(_engine.Settings)
        {
            MaximumDownloadRate = maxDownloadKBs > 0 ? maxDownloadKBs * 1024 : 0,
            MaximumUploadRate = maxUploadKBs > 0 ? maxUploadKBs * 1024 : 0,
        };

        await _engine.UpdateSettingsAsync(builder.ToSettings()).ConfigureAwait(false);
    }

    public IReadOnlyList<TorrentInfo> GetSnapshot()
    {
        var list = new List<TorrentInfo>(_managers.Count);
        foreach (var (id, manager) in _managers)
            list.Add(ToTorrentInfo(id, manager));
        return list;
    }

    /// <summary>Everything needed to restore the current set of torrents on the next launch.</summary>
    public IReadOnlyList<PersistedTorrentEntry> GetSessionEntries() => _sessionEntries.Values.ToList();

    /// <summary>
    /// Engine-wide health that explains *why* speed might be poor: whether DHT ever
    /// bootstrapped, and whether the listen port actually got forwarded. Both are outside
    /// the app's control once attempted, but hiding them makes "slow" look like a bug.
    /// </summary>
    public EngineDiagnostics GetDiagnostics()
    {
        var mappings = _engine.PortMappings;
        var portForwardingStatus = mappings.Created.Count > 0
            ? $"Open ({mappings.Created.Count} port{(mappings.Created.Count == 1 ? "" : "s")} mapped via UPnP)"
            : mappings.Pending.Count > 0
                ? "Requesting UPnP mapping..."
                : mappings.Failed.Count > 0
                    ? "Failed - forward the listen port manually on your router"
                    : "Not attempted";

        return new EngineDiagnostics(
            _engine.Dht.State.ToString(),
            _engine.Dht.NodeCount,
            portForwardingStatus,
            IsOnline);
    }

    private async void OnConnectivityChanged(object? sender, bool isOnline)
    {
        if (isOnline)
            await ResumeAutoPausedAsync().ConfigureAwait(false);
        else
            await PauseActiveTorrentsForConnectivityLossAsync().ConfigureAwait(false);
    }

    private async Task PauseActiveTorrentsForConnectivityLossAsync()
    {
        foreach (var (id, manager) in _managers)
        {
            if (!IsActiveState(manager.State))
                continue;

            try
            {
                await manager.PauseAsync().ConfigureAwait(false);
                lock (_autoPauseLock) _autoPausedIds.Add(id);
            }
            catch
            {
                // One torrent failing to pause must not stop the rest of the batch.
            }
        }
    }

    private async Task ResumeAutoPausedAsync()
    {
        List<Guid> ids;
        lock (_autoPauseLock)
        {
            ids = new List<Guid>(_autoPausedIds);
            _autoPausedIds.Clear();
        }

        foreach (var id in ids)
        {
            try
            {
                if (_managers.TryGetValue(id, out var manager))
                    await manager.StartAsync().ConfigureAwait(false);
            }
            catch
            {
                // Put it back so the *next* reconnect (or the fallback poll) retries it,
                // instead of silently dropping it from auto-resume forever.
                lock (_autoPauseLock) _autoPausedIds.Add(id);
            }
        }
    }

    private static bool IsActiveState(TorrentState state) =>
        state is TorrentState.Starting or TorrentState.Downloading or TorrentState.Seeding
            or TorrentState.Hashing or TorrentState.Metadata or TorrentState.FetchingHashes;

    private static TorrentInfo ToTorrentInfo(Guid id, TorrentManager manager)
    {
        var name = manager.HasMetadata ? manager.Torrent!.Name : (manager.MagnetLink?.Name ?? "(fetching metadata...)");
        var size = manager.HasMetadata ? manager.Torrent!.Size : manager.MagnetLink?.Size ?? 0;

        return new TorrentInfo(
            id,
            name,
            size,
            manager.Progress,
            Enum.Parse<TorrentDownloadState>(manager.State.ToString()),
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.OpenConnections,
            manager.Peers.Available,
            manager.Peers.Seeds,
            manager.Peers.Leechs,
            TrackerStatusSummary(manager),
            manager.SavePath,
            manager.HasMetadata);
    }

    private static string TrackerStatusSummary(TorrentManager manager)
    {
        var trackers = manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).ToList();
        if (trackers.Count == 0)
            return "no trackers";

        if (trackers.Any(t => t.Status.ToString() == "Ok"))
            return "ok";

        var failure = trackers.FirstOrDefault(t => !string.IsNullOrEmpty(t.FailureMessage))?.FailureMessage;
        return failure ?? trackers[0].Status.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        // The NetworkMonitorService is shared with other services and owned by the caller
        // (it outlives this engine), so we only unsubscribe here rather than dispose it.
        _networkMonitor.ConnectivityChanged -= OnConnectivityChanged;
        await _engine.StopAllAsync().ConfigureAwait(false);
        _engine.Dispose();
    }
}
