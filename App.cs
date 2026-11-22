using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AioLite;

// main app, tourne en fond avec l'icone dans la barre des taches
// la fenetre est creee seulement quand on l'ouvre (sinon ca bouffe de la ram pour rien)
public sealed class App : ApplicationContext
{
    public Settings Settings { get; }
    public Player Player { get; }
    public double PrepareProgress { get; private set; } = -1;
    public event Action? StateChanged;

    readonly NotifyIcon _tray;
    readonly SynchronizationContext _ui;
    readonly ToolStripMenuItem _pauseItem;
    MainForm? _form;
    CancellationTokenSource? _prepCts;
    DeviceInfo? _lastInfo;
    bool _suspended;

    public static Icon AppIcon { get; } = LoadIcon();
    static Icon LoadIcon()
    {
        using var s = typeof(App).Assembly.GetManifestResourceStream("app.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    public App(bool showWindow, bool startPlayer = true)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = Settings.Load();
        Settings.ImportLovingcoolThemes();
        if (startPlayer && AutoStart.Enabled) AutoStart.Enabled = true; // fix si l'exe a change de dossier
        if (Settings.Current == null && Settings.Library.Count > 0) Settings.Current = Settings.Library[0].Path;
        Settings.Save();

        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        menu.Items.Add("Ouvrir AIO Lite", null, (_, _) => ShowWindow());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitThread());
        menu.ForeColor = Color.White;

        _tray = new NotifyIcon { Icon = AppIcon, Text = "AIO Lite", ContextMenuStrip = menu, Visible = startPlayer };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        Player = new Player(Settings.Paused, startPlayer);
        Player.SetBrightness(100); // backlight a fond, la vraie luminosite est faite dans la video (voir Encoder)
        Player.Changed += () => _ui.Post(_ => OnPlayerChanged(), null);

        SystemEvents.PowerModeChanged += OnPower;
        UpdatePauseItem();
        if (showWindow) ShowWindow();
    }

    void OnPlayerChanged()
    {
        if (Player.Info != null && Player.Info != _lastInfo)
        {
            _lastInfo = Player.Info;
            _ = PrepareCurrentAsync();
        }
        var t = "AIO Lite — " + Player.StatusText;
        _tray.Text = t.Length > 63 ? t[..63] : t;
        StateChanged?.Invoke();
    }

    // convert the video if not in cache yet, then send it to the player
    public async Task PrepareCurrentAsync()
    {
        _prepCts?.Cancel();
        var info = Player.Info;
        var src = Settings.Current;
        if (info == null || src == null) return;
        if (!File.Exists(src)) { Player.ReportPreparing("Fichier introuvable : " + Path.GetFileName(src)); StateChanged?.Invoke(); return; }

        var cts = _prepCts = new CancellationTokenSource();
        var cache = Encoder.CachePath(src, info, Settings.Fill, Settings.MaxFps, Settings.Brightness);
        if (!File.Exists(cache))
        {
            Player.ReportPreparing("Préparation de la vidéo (une seule fois)…");
            PrepareProgress = 0;
            StateChanged?.Invoke();
        }
        try
        {
            var progress = new Progress<double>(p => { PrepareProgress = p; StateChanged?.Invoke(); });
            var ready = await Task.Run(() => Encoder.PrepareAsync(src, info, Settings.Fill, Settings.MaxFps, Settings.Brightness, progress, cts.Token));
            if (cts.IsCancellationRequested) return;
            PrepareProgress = -1;
            var name = Settings.Library.FirstOrDefault(l => string.Equals(l.Path, src, StringComparison.OrdinalIgnoreCase))?.Name
                       ?? Path.GetFileNameWithoutExtension(src);
            Player.Play(ready, name);
            CleanCache(keep: ready);
            TrimMemory();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            PrepareProgress = -1;
            Player.ReportPreparing("Erreur : " + e.Message);
        }
        StateChanged?.Invoke();
    }

    // cache max 2go, on vire les plus vieux
    static void CleanCache(string keep)
    {
        try
        {
            var files = new DirectoryInfo(Path.GetDirectoryName(keep)!).GetFiles("*.aio").OrderByDescending(f => f.LastAccessTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > 2L << 30 && !f.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase)) f.Delete();
            }
        }
        catch { }
    }

    public void SelectVideo(string path)
    {
        Settings.Current = path;
        Settings.Save();
        _ = PrepareCurrentAsync();
    }

    public void SetBrightness(int v)
    {
        if (Settings.Brightness == v) return;
        Settings.Brightness = v;
        Settings.Save();
        _ = PrepareCurrentAsync();
    }

    public void SetFill(bool fill)
    {
        if (Settings.Fill == fill) return;
        Settings.Fill = fill; Settings.Save();
        _ = PrepareCurrentAsync();
    }

    public void SetMaxFps(int fps)
    {
        if (Settings.MaxFps == fps) return;
        Settings.MaxFps = fps; Settings.Save();
        _ = PrepareCurrentAsync();
    }

    public void TogglePause()
    {
        Settings.Paused = !Settings.Paused;
        Settings.Save();
        Player.SetPaused(Settings.Paused || _suspended);
        UpdatePauseItem();
        StateChanged?.Invoke();
    }

    void UpdatePauseItem() => _pauseItem.Text = Settings.Paused ? "Reprendre" : "Pause";

    void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { _suspended = true; Player.SetPaused(true); }
        else if (e.Mode == PowerModes.Resume) { _suspended = false; Player.SetPaused(Settings.Paused); }
    }

    public void ShowWindow()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(this);
            _form.FormClosed += (_, _) => { _form = null; TrimMemory(); };
            _form.Show();
        }
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);

    // free the ram after closing the window or after a conversion
    // (sinon ca reste a 50mo+ a cause des gros buffers)
    static void TrimMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPower;
        _prepCts?.Cancel();
        _form?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        Player.Dispose();
        base.ExitThreadCore();
    }

    public void Quit() => ExitThread();
}
