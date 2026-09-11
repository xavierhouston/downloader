using System.Text.Json;
using TorrentDownloader.Core.Models;

namespace TorrentDownloader.Core.Services;

/// <summary>Persists which torrents/downloads were active so they can be restored after a restart.</summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _sessionFilePath;

    public SessionStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _sessionFilePath = Path.Combine(appDataDirectory, "session.json");
    }

    public SessionState Load()
    {
        if (!File.Exists(_sessionFilePath))
            return new SessionState();

        try
        {
            var json = File.ReadAllText(_sessionFilePath);
            return JsonSerializer.Deserialize<SessionState>(json) ?? new SessionState();
        }
        catch (JsonException)
        {
            return new SessionState();
        }
    }

    public void Save(SessionState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(_sessionFilePath, json);
    }
}
