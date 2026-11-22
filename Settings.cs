using System.Text.Json;
using Microsoft.Win32;

namespace AioLite;

public static class Paths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AioLite");
    public static readonly string SettingsFile = Path.Combine(Data, "settings.json");
}

public sealed class LibraryItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Thumb { get; set; }
}

public sealed class Settings
{
    public string? Current { get; set; }
    public int Brightness { get; set; } = 100;
    public bool Fill { get; set; } = true;
    public int MaxFps { get; set; } = 30;
    public bool Paused { get; set; }
    public bool ImportedLovingcool { get; set; }
    public string? Language { get; set; } // "fr" ou "en", null = langue de windows
    public bool TakeoverAsked { get; set; }   // deja propose de virer lovingcool ?
    public bool AutoStartDone { get; set; }   // demarrage auto active au 1er lancement
    public List<LibraryItem> Library { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Data);
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Paths.SettingsFile, true);
    }

    // import the videos from lovingcool themes (only first launch)
    public void ImportLovingcoolThemes()
    {
        if (ImportedLovingcool) return;
        ImportedLovingcool = true;
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LOVINGCOOL_MONITOR_INTERNAL");
        var themes = System.IO.Path.Combine(root, "themes");
        if (!Directory.Exists(themes)) return;
        foreach (var f in Directory.EnumerateFiles(themes, "*.json").OrderBy(f => f))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                if (!r.TryGetProperty("deviceWidth", out var dw) || dw.GetInt32() < 1000) continue; // skip les themes pour les petits ecrans
                if (!r.TryGetProperty("bgMedia", out var media) || media.GetArrayLength() == 0) continue;
                var src = System.IO.Path.Combine(root, media[0].GetString()!.Replace('/', '\\'));
                if (!File.Exists(src) || Library.Any(l => l.Path.Equals(src, StringComparison.OrdinalIgnoreCase))) continue;
                var label = r.TryGetProperty("label", out var l) ? l.GetString() : null;
                Library.Add(new LibraryItem { Name = label ?? System.IO.Path.GetFileNameWithoutExtension(src), Path = src });
            }
            catch { }
        }
    }
}

public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AioLite";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(Name, false);
        }
    }
}
