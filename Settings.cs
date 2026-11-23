using System.Text.Json;
using Microsoft.Win32;

namespace AioLite;

public static class Paths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AioLite");
    public static readonly string SettingsFile = Path.Combine(Data, "settings.json");
}

// petit log texte pour debug (garde que les 200 dernieres lignes)
public static class Log
{
    static readonly string FilePath = Path.Combine(Paths.Data, "log.txt");
    static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Data);
                var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).TakeLast(199).ToList() : [];
                lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}");
                File.WriteAllLines(FilePath, lines);
            }
        }
        catch { }
    }
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
    public bool WantAutoStart { get; set; } = true; // ce que l'utilisateur a choisi dans les reglages
    public List<LibraryItem> Library { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        // si le fichier est casse on essaie la sauvegarde d'avant, sinon on repartirait de zero (= 1ere video)
        foreach (var f in new[] { Paths.SettingsFile, Paths.SettingsFile + ".bak" })
        {
            try
            {
                if (File.Exists(f) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(f)) is { } s) return s;
            }
            catch (Exception e) { Log.Write($"settings load failed ({Path.GetFileName(f)}): {e.Message}"); }
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            if (File.Exists(Paths.SettingsFile)) File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".bak", true);
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
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

    // la liste "applications de demarrage" du gestionnaire des taches
    // 02 = active, 03 = desactive. sur certaines versions de windows sans ca l'app se lance pas
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(Name) is not string) return false;
            using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // desactive dans le gestionnaire des taches = premier octet impair
            return a?.GetValue(Name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            using var a = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            if (value)
            {
                k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
                a.SetValue(Name, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }
            else
            {
                k.DeleteValue(Name, false);
                a.DeleteValue(Name, false);
            }
        }
    }
}
