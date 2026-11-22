using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AioLite;

// settings window, cree quand on l'ouvre et detruite quand on la ferme
sealed class MainForm : Form
{
    static readonly string[] VideoExts = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v", ".gif", ".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    readonly App _app;
    readonly Label _title = new() { Text = "AIO Lite", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, BackColor = Color.Transparent };
    readonly Label _device = new() { Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, BackColor = Color.Transparent };
    readonly StatusPill _status = new() { Width = 320 };
    readonly FlatButton _pause = new() { Width = 110, Primary = true };
    readonly ThinProgress _progress = new() { Visible = false };
    readonly Card _libCard = new();
    readonly Label _libTitle = new() { Text = "Bibliothèque", Font = Theme.H2, ForeColor = Theme.Text, AutoSize = true, BackColor = Color.Transparent };
    readonly Label _libHint = new() { Text = "Clique pour afficher · glisse des vidéos ici · clic droit pour retirer", Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, BackColor = Color.Transparent };
    readonly FlatButton _add = new() { Text = "+  Ajouter", Width = 110 };
    readonly FlowLayoutPanel _flow = new() { AutoScroll = true, BackColor = Theme.Panel, Padding = new Padding(0) };
    readonly Card _setCard = new();
    readonly Label _lBright = Lbl("Luminosité"), _lFit = Lbl("Cadrage"), _lFps = Lbl("Fluidité max"), _lStart = Lbl("Lancer avec Windows");
    readonly Label _brightVal = new() { Font = Theme.H2, ForeColor = Theme.Text, AutoSize = false, Width = 56, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent };
    readonly Slider _bright = new();
    readonly Segmented _fit = new() { Items = ["Ajuster", "Remplir"], Width = 200 };
    readonly Segmented _fps = new() { Items = ["30 i/s", "60 i/s"], Width = 200 };
    readonly Toggle _start = new();
    readonly Label _usage = new() { Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, BackColor = Color.Transparent };
    readonly System.Windows.Forms.Timer _usageTimer = new() { Interval = 1000 };
    TimeSpan _lastCpu; long _lastTick;

    static Label Lbl(string t) => new() { Text = t, Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, BackColor = Color.Transparent };

    public MainForm(App app)
    {
        _app = app;
        Text = "AIO Lite";
        Icon = App.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 680);
        MinimumSize = new Size(780, 600);
        DoubleBuffered = true;
        AllowDrop = true;

        Controls.AddRange([_title, _device, _status, _pause, _progress, _libCard, _setCard]);
        _libCard.Controls.AddRange([_libTitle, _libHint, _add, _flow]);
        _setCard.Controls.AddRange([_lBright, _bright, _brightVal, _lFit, _fit, _lFps, _fps, _lStart, _start, _usage]);

        _bright.Value = app.Settings.Brightness;
        _brightVal.Text = _bright.Value + " %";
        _bright.Min = 5;
        _bright.ValueChanged += (_, _) => _brightVal.Text = _bright.Value + " %";
        _bright.Committed += (_, _) => _app.SetBrightness(_bright.Value);
        _fit.Selected = app.Settings.Fill ? 1 : 0;
        _fit.Changed += (_, _) => _app.SetFill(_fit.Selected == 1);
        _fps.Selected = app.Settings.MaxFps >= 60 ? 1 : 0;
        _fps.Changed += (_, _) => _app.SetMaxFps(_fps.Selected == 1 ? 60 : 30);
        _start.On = AutoStart.Enabled;
        _start.Changed += (_, _) => AutoStart.Enabled = _start.On;
        _pause.Click += (_, _) => _app.TogglePause();
        _add.Click += (_, _) => AddViaDialog();

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files); };

        _app.StateChanged += RefreshState;
        _usageTimer.Tick += (_, _) => UpdateUsage();
        _usageTimer.Start();

        BuildLibrary();
        RefreshState();
        UpdateUsage();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string? id);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SetWindowTheme(_flow.Handle, "DarkMode_Explorer", null); // dark scrollbar sinon elle est blanche c moche
        DoLayout();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

    void DoLayout()
    {
        int W = ClientSize.Width, H = ClientSize.Height, m = 24;
        _title.Location = new Point(m + 2, 18);
        _device.Location = new Point(m + 4, 54);
        _pause.Location = new Point(W - m - _pause.Width, 24);
        _status.Location = new Point(_pause.Left - 12 - _status.Width, 24);
        _progress.SetBounds(m, 82, W - 2 * m, 4);

        int setH = 168;
        _setCard.SetBounds(m, H - m - setH, W - 2 * m, setH);
        _libCard.SetBounds(m, 96, W - 2 * m, _setCard.Top - 96 - 16);

        // library
        int cw = _libCard.Width;
        _libTitle.Location = new Point(20, 18);
        _libHint.Location = new Point(20, 44);
        _add.Location = new Point(cw - 20 - _add.Width, 18);
        _flow.SetBounds(16, 72, cw - 24, _libCard.Height - 84);
        LayoutTiles();

        // settings en 2 colonnes
        int sw = _setCard.Width, col = sw / 2;
        _lBright.Location = new Point(22, 26);
        _brightVal.SetBounds(sw - 22 - 56, 20, 56, 30);
        _bright.SetBounds(160, 23, _brightVal.Left - 170, 26);
        _lFit.Location = new Point(22, 78);
        _fit.Location = new Point(160, 72);
        _lFps.Location = new Point(col + 10, 78);
        _fps.Location = new Point(sw - 22 - _fps.Width, 72);
        _lStart.Location = new Point(22, 124);
        _start.Location = new Point(160, 122);
        _usage.Location = new Point(sw - 22 - _usage.PreferredWidth, 126);
    }

    // resize les vignettes pour que ca rentre (sinon y'avait une scrollbar horizontale)
    void LayoutTiles()
    {
        const int gap = 12, minW = 190;
        int avail = _flow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (avail <= 0) return;
        int cols = Math.Max(1, (avail + gap) / (minW + gap));
        int w = (avail + gap) / cols - gap;
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls) c.Size = new Size(w, 128);
        _flow.ResumeLayout();
    }

    void BuildLibrary()
    {
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls) c.Dispose();
        _flow.Controls.Clear();
        foreach (var item in _app.Settings.Library)
        {
            var tile = new VideoTile(item) { Selected = PathEq(item.Path, _app.Settings.Current) };
            tile.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) Select(item);
                else if (e.Button == MouseButtons.Right) ShowTileMenu(tile, e.Location);
            };
            _flow.Controls.Add(tile);
            _ = LoadThumbAsync(tile);
        }
        _flow.ResumeLayout();
        LayoutTiles();
    }

    async Task LoadThumbAsync(VideoTile tile)
    {
        var item = tile.Item;
        if (item.Thumb == null || !File.Exists(item.Thumb))
        {
            item.Thumb = await Task.Run(() => Encoder.ThumbnailAsync(item.Path));
            if (item.Thumb != null) _app.Settings.Save();
        }
        if (item.Thumb == null || tile.IsDisposed) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(item.Thumb);
            if (!tile.IsDisposed) tile.Thumb = Image.FromStream(new MemoryStream(bytes));
        }
        catch { }
    }

    void ShowTileMenu(VideoTile tile, Point p)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        menu.Items.Add("Afficher", null, (_, _) => Select(tile.Item));
        menu.Items.Add("Ouvrir l'emplacement", null, (_, _) =>
            Process.Start("explorer.exe", $"/select,\"{tile.Item.Path}\""));
        menu.Items.Add("Retirer de la liste", null, (_, _) =>
        {
            _app.Settings.Library.Remove(tile.Item);
            _app.Settings.Save();
            _flow.Controls.Remove(tile);
            tile.Dispose();
        });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(tile, p);
    }

    void Select(LibraryItem item)
    {
        foreach (VideoTile t in _flow.Controls) t.Selected = t.Item == item;
        _app.SelectVideo(item.Path);
    }

    void AddViaDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Ajouter des vidéos",
            Multiselect = true,
            Filter = "Vidéos et images|" + string.Join(";", VideoExts.Select(x => "*" + x)) + "|Tous les fichiers|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
    }

    void AddFiles(IEnumerable<string> files)
    {
        LibraryItem? last = null;
        foreach (var f in files)
        {
            if (!VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            var existing = _app.Settings.Library.FirstOrDefault(l => PathEq(l.Path, f));
            if (existing != null) { last = existing; continue; }
            last = new LibraryItem { Name = Path.GetFileNameWithoutExtension(f), Path = f };
            _app.Settings.Library.Add(last);
        }
        _app.Settings.Save();
        BuildLibrary();
        if (last != null) Select(last);
    }

    void RefreshState()
    {
        if (IsDisposed) return;
        var p = _app.Player;
        var info = p.Info;
        _device.Text = info == null
            ? "Aucun écran connecté"
            : $"{info.Model}  ·  {info.Width}×{info.Height}  ·  firmware {info.Version}";
        _status.Text = p.StatusText + (p.State == PlayerState.Playing
            ? (p.Fps > 0 ? $"  ·  {p.Fps:0} / {p.SourceFps:0.##} i/s" : $"  ·  {p.SourceFps:0.##} i/s")
            : "");
        _status.Dot = p.State switch
        {
            PlayerState.Playing => Theme.Green,
            PlayerState.Preparing or PlayerState.Paused => Theme.Orange,
            PlayerState.Error => Theme.Red,
            _ => Theme.Muted,
        };
        _status.Invalidate();
        _pause.Text = _app.Settings.Paused ? "▶  Reprendre" : "❚❚  Pause";
        _progress.Visible = _app.PrepareProgress >= 0;
        if (_app.PrepareProgress >= 0)
        {
            _progress.Value = _app.PrepareProgress;
            _status.Text = $"Préparation… {_app.PrepareProgress:P0} (une seule fois)";
        }
    }

    void UpdateUsage()
    {
        using var proc = Process.GetCurrentProcess();
        var cpu = proc.TotalProcessorTime;
        long now = Environment.TickCount64;
        string cpuTxt = "";
        if (_lastTick != 0)
        {
            double pct = (cpu - _lastCpu).TotalMilliseconds / Math.Max(1, now - _lastTick) / Environment.ProcessorCount * 100;
            cpuTxt = $"CPU {pct:0.0} %   ·   ";
        }
        _lastCpu = cpu; _lastTick = now;
        _usage.Text = $"{cpuTxt}RAM {proc.PrivateMemorySize64 / 1048576.0:0} Mo";
        _usage.Left = _setCard.Width - 22 - _usage.PreferredWidth;
    }

    static bool PathEq(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _app.StateChanged -= RefreshState;
        _usageTimer.Dispose();
        base.OnFormClosed(e);
    }
}
