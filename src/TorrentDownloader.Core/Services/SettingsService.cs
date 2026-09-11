using System.Text.Json;

namespace TorrentDownloader.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsFilePath;

    public string AppDataDirectory { get; }

    public SettingsService(string? appDataDirectory = null)
    {
        AppDataDirectory = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TorrentDownloader");
        Directory.CreateDirectory(AppDataDirectory);
        _settingsFilePath = Path.Combine(AppDataDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.DownloadDirectory))
                    return settings;
            }
            catch (JsonException)
            {
                // Fall through to defaults if the settings file is corrupt.
            }
        }

        return new AppSettings
        {
            DownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "TorrentDownloader"),
        };
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsFilePath, json);
    }
}
