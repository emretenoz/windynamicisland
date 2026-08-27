using System.IO;
using System.Text.Json;

namespace WinDynamicIsland;

public sealed class IslandSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinDynamicIsland");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public bool NotificationsEnabled { get; set; } = true;
    public bool WeatherEnabled { get; set; } = true;
    public bool ScreenshotPreviewEnabled { get; set; } = true;
    public bool TimerEnabled { get; set; } = true;
    public bool SystemAlertsEnabled { get; set; } = true;
    public int DisplayIndex { get; set; }
    public List<DockPinnedApp> PinnedDockApps { get; set; } = new();
    public List<string> DockAppOrder { get; set; } = new();
    public List<string> HiddenLauncherApps { get; set; } = new();

    public static IslandSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new IslandSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<IslandSettings>(json) ?? new IslandSettings();
            settings.DisplayIndex = Math.Max(0, settings.DisplayIndex);
            settings.PinnedDockApps ??= new List<DockPinnedApp>();
            settings.DockAppOrder ??= new List<string>();
            settings.HiddenLauncherApps ??= new List<string>();
            settings.PinnedDockApps = settings.PinnedDockApps
                .Where(app => app is not null &&
                              !string.IsNullOrWhiteSpace(app.Key) &&
                              !string.IsNullOrWhiteSpace(app.Path))
                .GroupBy(app => app.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            settings.DockAppOrder = settings.DockAppOrder
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            settings.HiddenLauncherApps = settings.HiddenLauncherApps
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return settings;
        }
        catch
        {
            return new IslandSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ayar yazılamazsa çalışan arayüzün kapanmasına izin verme.
        }
    }
}

public sealed class DockPinnedApp
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
