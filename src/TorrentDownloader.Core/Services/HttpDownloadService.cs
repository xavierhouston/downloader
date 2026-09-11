using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using TorrentDownloader.Core.Models;

namespace TorrentDownloader.Core.Services;

/// <summary>
/// Plain HTTP/HTTPS file downloads (a regular download-manager, not BitTorrent) —
/// for direct links rather than .torrent files / magnet links.
/// </summary>
public sealed class HttpDownloadService : IDisposable
{
    private const int BufferSize = 81920;
    private const int MaxAutoRetriesWithNoProgress = 5;

    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly HttpClient _httpClient = new();
    private readonly ConcurrentDictionary<Guid, DownloadEntry> _entries = new();
    private readonly NetworkMonitorService _networkMonitor;
    private readonly HashSet<Guid> _autoPausedIds = new();
    private readonly object _autoPauseLock = new();

    // Global across all direct-link downloads, same as the torrent engine's rate limit -
    // matches the "one Download limit setting" the UI presents.
    private readonly RateLimiter _downloadRateLimiter = new();

    public HttpDownloadService(NetworkMonitorService networkMonitor, TimeSpan? stallTimeout = null, TimeSpan? retryDelay = null)
    {
        _networkMonitor = networkMonitor;
        _networkMonitor.ConnectivityChanged += OnConnectivityChanged;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(30);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>0 (or negative) means unlimited.</summary>
    public void SetDownloadRateLimitKBs(int maxDownloadKBs) =>
        _downloadRateLimiter.SetLimitBytesPerSecond(maxDownloadKBs > 0 ? maxDownloadKBs * 1024L : 0);

    public async Task<HttpDownloadInfo> AddAsync(string url, string saveDirectory)
    {
        Directory.CreateDirectory(saveDirectory);

        var uri = new Uri(url);
        var fileName = GetFileNameFromUrl(uri);
        var filePath = Path.Combine(saveDirectory, fileName);

        var entry = new DownloadEntry(Guid.NewGuid(), url, fileName, filePath);
        _entries[entry.Id] = entry;

        StartDownloadLoop(entry, resume: false);

        return ToInfo(entry);
    }

    public Task PauseAsync(Guid id)
    {
        // A manual pause always wins: forget any auto-pause bookkeeping so
        // reconnecting won't override the user's choice and resume it.
        lock (_autoPauseLock) _autoPausedIds.Remove(id);

        if (_entries.TryGetValue(id, out var entry))
        {
            entry.Cts?.Cancel();
            entry.State = HttpDownloadState.Paused;
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(Guid id)
    {
        lock (_autoPauseLock) _autoPausedIds.Remove(id);

        if (_entries.TryGetValue(id, out var entry) && entry.State is HttpDownloadState.Paused or HttpDownloadState.Error)
        {
            // A manual retry gets a fresh auto-retry budget rather than staying permanently
            // exhausted just because it used up its automatic attempts earlier.
            entry.ConsecutiveNoProgressFailures = 0;
            StartDownloadLoop(entry, resume: true);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid id, bool deleteFile)
    {
        lock (_autoPauseLock) _autoPausedIds.Remove(id);

        if (_entries.TryRemove(id, out var entry))
        {
            entry.Cts?.Cancel();
            if (deleteFile)
            {
                try { File.Delete(entry.FilePath); }
                catch (IOException) { /* best-effort */ }
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<HttpDownloadInfo> GetSnapshot() => _entries.Values.Select(ToInfo).ToList();

    /// <summary>Everything needed to resume these downloads (from the correct byte offset) on
    /// the next launch. Completed ones are excluded — nothing left to resume.</summary>
    public IReadOnlyList<PersistedHttpDownloadEntry> GetSessionEntries() =>
        _entries.Values
            .Where(e => e.State != HttpDownloadState.Completed)
            .Select(e => new PersistedHttpDownloadEntry(
                e.Id.ToString(),
                e.Url,
                Path.GetDirectoryName(e.FilePath) ?? "",
                e.FileName,
                e.DownloadedBytes))
            .ToList();

    /// <summary>Re-adds everything that was in progress in a previous session. The byte offset
    /// is re-checked against what's actually on disk (not just trusted from the saved record) in
    /// case the app was killed mid-write and the file is shorter than what we last recorded.</summary>
    public void Restore(IEnumerable<PersistedHttpDownloadEntry> entries)
    {
        foreach (var saved in entries)
        {
            if (!Guid.TryParse(saved.Id, out var id))
                continue;

            var filePath = Path.Combine(saved.SaveDirectory, saved.FileName);
            var actualBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;

            var entry = new DownloadEntry(id, saved.Url, saved.FileName, filePath) { DownloadedBytes = actualBytes };
            _entries[id] = entry;
            StartDownloadLoop(entry, resume: actualBytes > 0);
        }
    }

    private void OnConnectivityChanged(object? sender, bool isOnline)
    {
        if (isOnline)
        {
            List<Guid> ids;
            lock (_autoPauseLock)
            {
                ids = new List<Guid>(_autoPausedIds);
                _autoPausedIds.Clear();
            }

            foreach (var id in ids)
            {
                if (_entries.TryGetValue(id, out var entry))
                    StartDownloadLoop(entry, resume: true);
            }
        }
        else
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.State is not (HttpDownloadState.Connecting or HttpDownloadState.Downloading))
                    continue;

                try
                {
                    entry.Cts?.Cancel();
                    entry.State = HttpDownloadState.Paused;
                    lock (_autoPauseLock) _autoPausedIds.Add(entry.Id);
                }
                catch
                {
                    // One entry failing to pause must not stop the rest of the batch.
                }
            }
        }
    }

    private void StartDownloadLoop(DownloadEntry entry, bool resume)
    {
        var cts = new CancellationTokenSource();
        entry.Cts = cts;
        entry.State = HttpDownloadState.Connecting;
        entry.ErrorMessage = null;

        entry.RunTask = Task.Run(() => DownloadAsync(entry, resume, cts.Token));
    }

    private async Task DownloadAsync(DownloadEntry entry, bool resume, CancellationToken token)
    {
        var bytesAtAttemptStart = entry.DownloadedBytes;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, entry.Url);
            var resumeOffset = resume ? entry.DownloadedBytes : 0;
            if (resumeOffset > 0)
                request.Headers.Range = new RangeHeaderValue(resumeOffset, null);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            var isResumedTransfer = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (resumeOffset > 0 && !isResumedTransfer)
            {
                // Server ignored our Range request (200 OK with the full body) — restart clean
                // rather than append full content onto what we already have and corrupt the file.
                resumeOffset = 0;
                entry.DownloadedBytes = 0;
            }

            response.EnsureSuccessStatusCode();

            // Many proxy/mirror links have an opaque, non-descriptive path (or one that's too
            // long to use as a filename) but still report the real name via Content-Disposition.
            // Only safe to apply on a fresh start — renaming mid-resume would orphan the bytes
            // already written under the old name.
            if (resumeOffset == 0)
            {
                var suggestedName = response.Content.Headers.ContentDisposition?.FileNameStar
                    ?? response.Content.Headers.ContentDisposition?.FileName;
                if (!string.IsNullOrWhiteSpace(suggestedName))
                {
                    var sanitized = SanitizeFileName(suggestedName.Trim('"'));
                    if (sanitized != entry.FileName)
                    {
                        entry.FileName = sanitized;
                        entry.FilePath = Path.Combine(Path.GetDirectoryName(entry.FilePath)!, sanitized);
                    }
                }
            }

            entry.TotalBytes = isResumedTransfer
                ? (response.Content.Headers.ContentRange?.Length ?? resumeOffset + (response.Content.Headers.ContentLength ?? 0))
                : response.Content.Headers.ContentLength ?? 0;

            entry.State = HttpDownloadState.Downloading;
            entry.LastSpeedSampleBytes = entry.DownloadedBytes;
            entry.LastSpeedSampleTime = DateTime.UtcNow;

            await using var httpStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var fileStream = new FileStream(
                entry.FilePath,
                resumeOffset > 0 ? FileMode.Open : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true);

            if (resumeOffset > 0)
                fileStream.Seek(resumeOffset, SeekOrigin.Begin);

            var buffer = new byte[BufferSize];
            int bytesRead;

            // A stalled connection (server stops sending bytes without actually closing the
            // socket) otherwise hangs here forever: state stays "Downloading", speed correctly
            // shows 0 B/s, and nothing ever fails or retries. StallTimeout turns "silently stuck"
            // into a real, retryable failure by cancelling a read that's gone quiet too long.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            while (true)
            {
                stallCts.CancelAfter(_stallTimeout);
                try
                {
                    bytesRead = await httpStream.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new StallTimeoutException($"The download stalled - no data received for {_stallTimeout.TotalSeconds:0}s.");
                }

                if (bytesRead == 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token).ConfigureAwait(false);
                entry.DownloadedBytes += bytesRead;

                // Paces how fast we pull the *next* chunk from the socket - this is what
                // actually enforces the "Download limit" setting for direct links (previously
                // it only ever applied to torrents; HTTP downloads ran unthrottled).
                await _downloadRateLimiter.ThrottleAsync(bytesRead, token).ConfigureAwait(false);
            }

            entry.State = HttpDownloadState.Completed;
        }
        catch (OperationCanceledException)
        {
            // Pause/remove already set the desired state before cancelling.
        }
        catch (StallTimeoutException)
        {
            // Only count this against the retry budget if the attempt made zero progress -
            // a link that connects and transfers *something* each time before eventually
            // stalling again is still worth retrying indefinitely; one that never moves at
            // all is genuinely broken and shouldn't retry forever.
            var madeProgress = entry.DownloadedBytes > bytesAtAttemptStart;
            entry.ConsecutiveNoProgressFailures = madeProgress ? 0 : entry.ConsecutiveNoProgressFailures + 1;

            if (entry.ConsecutiveNoProgressFailures <= MaxAutoRetriesWithNoProgress)
            {
                entry.State = HttpDownloadState.Paused;
                _ = Task.Delay(_retryDelay).ContinueWith(_ => StartDownloadLoop(entry, resume: true));
            }
            else
            {
                entry.State = HttpDownloadState.Error;
                entry.ErrorMessage = $"Stalled repeatedly with no progress after {MaxAutoRetriesWithNoProgress} retries. Click Resume to try again.";
            }
        }
        catch (Exception ex)
        {
            // A dropped connection can throw here *faster* than our network-monitor poll/event
            // detects the outage and explicitly cancels the token (that path lands in the
            // OperationCanceledException branch above and is already tracked for auto-resume).
            // Without this check, that race left the download stuck in Error forever, since it
            // was never added to the auto-resume set - reconnecting had nothing to resume.
            if (!_networkMonitor.IsOnline)
            {
                entry.State = HttpDownloadState.Paused;
                lock (_autoPauseLock) _autoPausedIds.Add(entry.Id);
            }
            else
            {
                entry.State = HttpDownloadState.Error;
                entry.ErrorMessage = ex.Message;
            }
        }
    }

    private static HttpDownloadInfo ToInfo(DownloadEntry entry)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - entry.LastSpeedSampleTime).TotalSeconds;
        long rate = 0;

        if (entry.State == HttpDownloadState.Downloading && elapsed > 0.001)
        {
            rate = (long)((entry.DownloadedBytes - entry.LastSpeedSampleBytes) / elapsed);
            entry.LastSpeedSampleBytes = entry.DownloadedBytes;
            entry.LastSpeedSampleTime = now;
        }

        var progress = entry.TotalBytes > 0 ? (double)entry.DownloadedBytes / entry.TotalBytes * 100 : 0;

        return new HttpDownloadInfo(
            entry.Id,
            entry.Url,
            entry.FileName,
            entry.TotalBytes,
            entry.DownloadedBytes,
            progress,
            entry.State,
            rate,
            entry.FilePath,
            entry.ErrorMessage);
    }

    private static string GetFileNameFromUrl(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name)
            ? $"download-{Guid.NewGuid():N}"
            : SanitizeFileName(Uri.UnescapeDataString(name));
    }

    /// <summary>
    /// Strips characters Windows/Linux filesystems reject and caps the length well under
    /// NTFS's 255-char filename limit — some links (proxy/mirror tokens, signed URLs) use a
    /// path segment 300+ chars long, which otherwise fails with a path/filename-too-long error.
    /// </summary>
    internal static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            return $"download-{Guid.NewGuid():N}";

        const int maxLength = 150;
        if (cleaned.Length <= maxLength)
            return cleaned;

        var extension = Path.GetExtension(cleaned);
        if (extension.Length is 0 or > 20) // a 20+ char "extension" isn't really one — drop it
            return cleaned[..maxLength];

        return cleaned[..(maxLength - extension.Length)] + extension;
    }

    public void Dispose()
    {
        // The NetworkMonitorService is shared with other services and owned by the caller
        // (it outlives this one), so we only unsubscribe here rather than dispose it.
        _networkMonitor.ConnectivityChanged -= OnConnectivityChanged;

        foreach (var entry in _entries.Values)
            entry.Cts?.Cancel();

        _httpClient.Dispose();
    }

    private sealed class DownloadEntry(Guid id, string url, string fileName, string filePath)
    {
        public Guid Id { get; } = id;
        public string Url { get; } = url;

        // Mutable: DownloadAsync may rename these once a Content-Disposition header
        // reveals a better filename than the URL's (see the resumeOffset == 0 branch there).
        public string FileName = fileName;
        public string FilePath = filePath;

        public long TotalBytes;
        public long DownloadedBytes;
        public HttpDownloadState State = HttpDownloadState.Connecting;
        public string? ErrorMessage;
        public CancellationTokenSource? Cts;
        public Task? RunTask;

        public long LastSpeedSampleBytes;
        public DateTime LastSpeedSampleTime = DateTime.UtcNow;

        public int ConsecutiveNoProgressFailures;
    }

    /// <summary>Distinguishes "our own stall watchdog fired" from an ordinary transport
    /// failure, so it can get bounded auto-retry instead of the offline/online handling below.</summary>
    private sealed class StallTimeoutException(string message) : IOException(message);
}
