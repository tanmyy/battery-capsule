using System.IO;
using System.Text.Json;

namespace BatteryCapsule.Services;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public double CapsuleX { get; set; } = 100;
    public double CapsuleY { get; set; } = 100;
    public bool ShowEstimatedPercent { get; set; } = true;
    public bool ShowReportedPercent { get; set; } = false; // capsule shows estimated by default; reported lives in the panel
    public bool ShowTimeRemaining { get; set; } = true;
    public double Transparency { get; set; } = 0.90; // 0-1 opacity
    public double CapsuleScale { get; set; } = 1.0;   // 0.8 - 1.4
    public int UpdateFrequencySeconds { get; set; } = 10;
    public bool NotificationsEnabled { get; set; } = false; // disabled by default per spec
    public bool NotifyAt20Percent { get; set; } = true;
    public bool NotifyAt10Percent { get; set; } = true;
    public bool NotifyLowTime { get; set; } = true;
    public bool NotifyReportedMismatch { get; set; } = false;
    public bool NotifyLowHealth { get; set; } = true;
}

/// <summary>
/// Loads/saves settings as JSON in the user's local app data folder. No cloud sync,
/// no telemetry - purely local persistence.
/// </summary>
public sealed class SettingsService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryCapsule");
    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) Current = loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults rather than crash.
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort; a failed save shouldn't crash a lightweight widget.
        }
    }
}
