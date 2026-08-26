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

    public static IslandSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new IslandSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<IslandSettings>(json) ?? new IslandSettings();
        }
        catch
        {
            return new IslandSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
