using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SoundSplit.Services;

internal sealed class CalibrationEntry
{
    public double Direct { get; set; }
    public double Stream { get; set; }
    public double Snr { get; set; }
}

/// <summary>What the app remembers between runs. Stored as JSON in %AppData%\SoundSplit.</summary>
internal sealed class AppSettings
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundSplit");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Speakers switched on in the list.</summary>
    public List<string> Selected { get; set; } = new();

    public string? MicId { get; set; }

    /// <summary>Switch Windows back to the speaker it was using when sharing stops.</summary>
    public bool RestoreDefault { get; set; } = true;

    /// <summary>The user's own timing correction per speaker, in milliseconds.</summary>
    public Dictionary<string, double> TrimMs { get; set; } = new();

    /// <summary>The last microphone measurement of each speaker. Entries are only comparable with each other.</summary>
    public Dictionary<string, CalibrationEntry> Calibration { get; set; } = new();

    public DateTime? CalibratedAt { get; set; }

    /// <summary>If sharing changed the Windows default output, what it was before. Cleared on a clean stop.</summary>
    public string? PendingRestoreDefault { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
        }
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundSplit");

    public static string FilePath { get; } = Path.Combine(Folder, "soundsplit.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
