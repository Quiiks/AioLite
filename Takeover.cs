using System.Diagnostics;
using Microsoft.Win32;

namespace AioLite;

// tout ce qu'il faut pour remplacer le logiciel lovingcool d'origine
// (il garde le port COM de l'ecran donc les 2 peuvent pas tourner en meme temps)
public static class Takeover
{
    const string TaskName = "LOVINGCOOL_MONITOR_INTERNAL";
    static readonly string[] ProcNames = ["LOVINGCOOL MONITOR", "SystemInfos"];
    static readonly string OldData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LOVINGCOOL_MONITOR_INTERNAL");

    // cherche le desinstalleur dans le registre (HKLM et HKCU)
    public static string? FindUninstaller()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = root.OpenSubKey(path);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is string name && name.StartsWith("LOVINGCOOL MONITOR", StringComparison.OrdinalIgnoreCase)
                    && k.GetValue("UninstallString") is string un)
                {
                    var exe = un.Trim();
                    exe = exe.StartsWith('"') ? exe[1..exe.IndexOf('"', 1)] : exe.Split(" /")[0];
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        return null;
    }

    public static bool IsInstalled => FindUninstaller() != null;

    // force l'arret de l'ancienne app si elle tourne (marche pas si elle est lancee en admin, voir Replace)
    public static int KillRunning()
    {
        int killed = 0;
        foreach (var name in ProcNames)
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(true); p.WaitForExit(3000); killed++; } catch { }
            finally { p.Dispose(); }
        }
        return killed;
    }

    public static bool AnyRunning => ProcNames.Any(n => Process.GetProcessesByName(n).Length > 0);

    // retire l'ancienne app du demarrage cote utilisateur (pas besoin d'admin)
    public static void RemoveUserAutostart()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run == null) return;
            foreach (var n in run.GetValueNames())
                if (run.GetValue(n) is string v && v.Contains("LOVINGCOOL", StringComparison.OrdinalIgnoreCase)) run.DeleteValue(n, false);
        }
        catch { }
    }

    // copie les videos de la library qui sont dans le dossier lovingcool vers notre dossier
    // sinon elles risquent de partir avec la desinstallation
    public static int CopyLibraryVideos(Settings s)
    {
        var dst = Path.Combine(Paths.Data, "videos");
        int n = 0;
        foreach (var it in s.Library)
        {
            if (!it.Path.StartsWith(OldData, StringComparison.OrdinalIgnoreCase) || !File.Exists(it.Path)) continue;
            Directory.CreateDirectory(dst);
            var np = Path.Combine(dst, Path.GetFileName(it.Path));
            if (!File.Exists(np) || new FileInfo(np).Length != new FileInfo(it.Path).Length) File.Copy(it.Path, np, true);
            if (string.Equals(s.Current, it.Path, StringComparison.OrdinalIgnoreCase)) s.Current = np;
            it.Path = np;
            it.Thumb = null;
            n++;
        }
        if (n > 0) s.Save();
        return n;
    }

    // le vrai remplacement : arret force + tache planifiee + desinstallation silencieuse
    // tout dans UNE commande admin pour avoir une seule fenetre UAC
    public static async Task<bool> UninstallAsync()
    {
        var un = FindUninstaller();
        if (un == null) return true;
        var cmd = string.Join(" & ",
            "taskkill /F /T /IM \"LOVINGCOOL MONITOR.exe\"",
            "taskkill /F /T /IM SystemInfos.exe",
            $"schtasks /Delete /TN {TaskName} /F",
            "rmdir /S /Q \"%ProgramData%\\LOVINGCOOL_MONITOR_INTERNAL\"",
            $"\"{un}\" /allusers /S");
        var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC refuse

        // le desinstalleur NSIS se relance en copie temporaire, on attend qu'il ait fini
        for (int i = 0; i < 90 && File.Exists(un); i++) await Task.Delay(1000);
        RemoveUserAutostart();
        return !IsInstalled;
    }
}
