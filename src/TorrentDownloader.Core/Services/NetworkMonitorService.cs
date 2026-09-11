using System.Net.NetworkInformation;

namespace TorrentDownloader.Core.Services;

/// <summary>
/// Tracks whether the machine currently has a usable network adapter.
/// Combines the OS-level NetworkChange event (fast, but not always raised
/// reliably on every platform/adapter type) with a periodic fallback poll,
/// so connection loss/restore is still detected even if the event never fires.
/// </summary>
public sealed class NetworkMonitorService : IDisposable
{
    private readonly Timer _pollTimer;
    private readonly object _lock = new();
    private bool _isOnline;

    public event EventHandler<bool>? ConnectivityChanged;

    public bool IsOnline
    {
        get { lock (_lock) return _isOnline; }
    }

    /// <summary>When the current online/offline state was last (re)confirmed — either by the
    /// OS event or the fallback poll. Surfaced so the UI/diagnostics can show whether detection
    /// is actually running, rather than the user having to guess why a resume didn't happen.</summary>
    public DateTime LastCheckedUtc { get; private set; } = DateTime.UtcNow;

    public NetworkMonitorService(TimeSpan? pollInterval = null)
    {
        _isOnline = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        // 15s was too slow in practice — a user toggling Wi-Fi back on could wait that long
        // for the fallback poll if the OS event didn't fire. 5s keeps the same safety net
        // (GetIsNetworkAvailable() is a cheap, local check) with much less perceived lag.
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);
        _pollTimer = new Timer(_ => Poll(), null, interval, interval);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => SetOnline(e.IsAvailable);

    /// <summary>Test-only seam to simulate a connectivity change without touching real network state.</summary>
    internal void ForceState(bool isOnline) => SetOnline(isOnline);

    private void Poll() => SetOnline(NetworkInterface.GetIsNetworkAvailable());

    private void SetOnline(bool isOnline)
    {
        bool changed;
        lock (_lock)
        {
            changed = _isOnline != isOnline;
            _isOnline = isOnline;
            LastCheckedUtc = DateTime.UtcNow;
        }

        if (changed)
            ConnectivityChanged?.Invoke(this, isOnline);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _pollTimer.Dispose();
    }
}
