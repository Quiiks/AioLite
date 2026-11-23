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
    readonly ToolStripMenuItem _pauseItem, _openItem, _quitItem, _replaceItem;
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
        L.Apply(Settings.Language);
        Settings.ImportLovingcoolThemes();
        if (startPlayer && AutoStart.Enabled) AutoStart.Enabled = true; // fix si l'exe a change de dossier
        if (startPlayer) Log.Write($"start v{Application.ProductVersion} | video={Path.GetFileName(Settings.Current)} | autostart={AutoStart.Enabled} | exe={Environment.ProcessPath}");
        if (Settings.Current == null && Settings.Library.Count > 0) Settings.Current = Settings.Library[0].Path;
        Settings.Save();

        _pauseItem = new ToolStripMenuItem("", null, (_, _) => TogglePause());
        _openItem = new ToolStripMenuItem("", null, (_, _) => ShowWindow());
        _quitItem = new ToolStripMenuItem("", null, (_, _) => ExitThread());
        _replaceItem = new ToolStripMenuItem("", null, (_, _) => _ = OfferTakeoverAsync(auto: false));
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        menu.Items.Add(_openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_replaceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_quitItem);
        // l'option apparait seulement si l'ancienne app est encore installee
        menu.Opening += (_, _) => _replaceItem.Visible = Takeover.IsInstalled;
        menu.ForeColor = Color.White;

        _tray = new NotifyIcon { Icon = AppIcon, Text = "AIO Lite", ContextMenuStrip = menu, Visible = startPlayer };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        Player = new Player(Settings.Paused, startPlayer);
        Player.SetBrightness(100); // backlight a fond, la vraie luminosite est faite dans la video (voir Encoder)
        Player.Changed += () => _ui.Post(_ => OnPlayerChanged(), null);

        SystemEvents.PowerModeChanged += OnPower;
        UpdateMenuTexts();

        if (startPlayer)
        {
            // l'ancienne app garde le port de l'ecran -> on la coupe direct
            Takeover.RemoveUserAutostart();
            Takeover.KillRunning();
            // on se (re)met au demarrage de windows si l'utilisateur le veut et que l'entree a saute
            if (!Settings.AutoStartDone) { Settings.WantAutoStart = true; Settings.AutoStartDone = true; Settings.Save(); }
            if (Settings.WantAutoStart && !AutoStart.Enabled)
            {
                AutoStart.Enabled = true;
                Log.Write("autostart re-active: " + AutoStart.Enabled);
            }
            _ui.Post(_ => _ = OfferTakeoverAsync(auto: true), null);
        }
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
        if (!File.Exists(src)) { Player.ReportPreparing(L.T("Fichier introuvable : ", "File not found: ") + Path.GetFileName(src)); StateChanged?.Invoke(); return; }

        var cts = _prepCts = new CancellationTokenSource();
        var cache = Encoder.CachePath(src, info, Settings.Fill, Settings.MaxFps, Settings.Brightness);
        if (!File.Exists(cache))
        {
            Player.ReportPreparing(L.T("Préparation de la vidéo (une seule fois)…", "Preparing the video (only once)…"));
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
            Player.ReportPreparing(L.T("Erreur : ", "Error: ") + e.Message);
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
        Log.Write("video choisie: " + Path.GetFileName(path));
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

    void UpdatePauseItem() => _pauseItem.Text = Settings.Paused ? L.T("Reprendre", "Resume") : "Pause";

    // propose de desinstaller lovingcool (auto = au lancement, une seule fois)
    async Task OfferTakeoverAsync(bool auto)
    {
        if (!Takeover.IsInstalled) return;
        if (auto && Settings.TakeoverAsked) return;
        Settings.TakeoverAsked = true;
        Settings.Save();

        var answer = MessageBox.Show(
            L.T("Le logiciel LOVINGCOOL MONITOR est encore installé. Il utilise l'écran en même temps qu'AIO Lite et se relance à chaque démarrage.\n\n" +
                "AIO Lite peut le remplacer : il copie tes vidéos, ferme l'ancien logiciel et le désinstalle.\n\nLe faire maintenant ?",
                "LOVINGCOOL MONITOR is still installed. It uses the screen at the same time as AIO Lite and starts again at every boot.\n\n" +
                "AIO Lite can replace it: it copies your videos, closes the old app and uninstalls it.\n\nDo it now?"),
            "AIO Lite", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly);
        if (answer != DialogResult.Yes) return;

        Player.ReportPreparing(L.T("Remplacement de LOVINGCOOL MONITOR…", "Replacing LOVINGCOOL MONITOR…"));
        StateChanged?.Invoke();
        await Task.Run(() => Takeover.CopyLibraryVideos(Settings));
        bool ok = await Takeover.UninstallAsync();

        _tray.ShowBalloonTip(5000, "AIO Lite", ok
            ? L.T("LOVINGCOOL MONITOR a été désinstallé. AIO Lite le remplace.", "LOVINGCOOL MONITOR was uninstalled. AIO Lite replaces it.")
            : L.T("La désinstallation n'a pas été faite (refusée ou erreur). Tu peux réessayer depuis le menu de l'icône.",
                  "Uninstall wasn't done (declined or error). You can retry from the tray icon menu."),
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);

        // les chemins des videos ont change -> on relance la video et on rafraichit la fenetre
        if (_form != null && !_form.IsDisposed) { var b = _form.Bounds; var old = _form; _form = null; old.Close(); ShowWindow(); _form!.Bounds = b; }
        _ = PrepareCurrentAsync();
        StateChanged?.Invoke();
    }

    void UpdateMenuTexts()
    {
        _openItem.Text = L.T("Ouvrir AIO Lite", "Open AIO Lite");
        _quitItem.Text = L.T("Quitter", "Quit");
        _replaceItem.Text = L.T("Remplacer LOVINGCOOL MONITOR…", "Replace LOVINGCOOL MONITOR…");
        UpdatePauseItem();
    }

    // change la langue et recree la fenetre (plus simple que de tout re-traduire a la main)
    public void SetLanguage(string lang)
    {
        if (Settings.Language == lang) return;
        Settings.Language = lang;
        Settings.Save();
        L.Apply(lang);
        UpdateMenuTexts();
        OnPlayerChanged();
        if (_form != null && !_form.IsDisposed)
        {
            var bounds = _form.Bounds;
            var old = _form;
            _form = null;
            old.Close();
            ShowWindow();
            _form!.StartPosition = FormStartPosition.Manual;
            _form.Bounds = bounds;
        }
    }

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
