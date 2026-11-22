namespace AioLite;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--snap") { Snap(args[1], args.Length > 2 ? args[2] : null); return; }
        if (args.Length == 3 && args[0] == "--encode-test") { EncodeTest(args[1], args[2]); return; }
        if (args.Length == 2 && args[0] == "--takeover-check")
        {
            // debug: est ce qu'on voit l'ancienne app ?
            File.WriteAllText(args[1], $"installed={Takeover.IsInstalled} uninstaller={Takeover.FindUninstaller()} running={Takeover.AnyRunning}");
            return;
        }

        using var mutex = new Mutex(true, @"Local\AioLite.SingleInstance", out bool first);
        if (!first)
        {
            // already running -> on dit a l'autre d'ouvrir sa fenetre
            using var ev = EventWaitHandle.OpenExisting(@"Local\AioLite.Show");
            ev.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        bool tray = args.Contains("--tray");
        var app = new App(showWindow: !tray);

        // pour quand on relance l'exe une 2eme fois
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AioLite.Show");
        var ctx = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ctx.Post(_ => app.ShowWindow(), null), null, -1, false);

        Application.Run(app);
    }

    // debug: encode une video et check que tous les paquets sont ok
    static void EncodeTest(string src, string report)
    {
        var info = new DeviceInfo(2400, 1080, 90, "test-6.67", "4.0", 100);
        var cache = Encoder.PrepareAsync(src, info, true, 60, 70, null, CancellationToken.None).GetAwaiter().GetResult();
        var data = File.ReadAllBytes(cache);
        int pos = 8, frames = 0, bad = 0;
        while (pos + 4 <= data.Length)
        {
            int len = BitConverter.ToInt32(data, pos);
            int sum = 0;
            for (int i = pos; i < pos + 4 + len; i++) sum += data[i];
            bool ok = data[pos + 4] == 0xFF && data[pos + 5] == 0xD8 && data[pos + 2 + len] == 0xFF && data[pos + 3 + len] == 0xD9
                      && data[pos + 4 + len] == (byte)sum && data[pos + 5 + len] == (byte)(sum >> 8);
            if (!ok) bad++;
            frames++; pos += len + 6;
        }
        File.WriteAllText(report, $"fps={BitConverter.ToInt32(data, 4) / 1000.0} frames={frames} bad={bad} end={(pos == data.Length)} cache={cache}");
        File.Delete(cache);
    }

    // debug: screenshot de la fenetre dans un png
    static void Snap(string file, string? lang)
    {
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var app = new App(showWindow: false, startPlayer: false);
        if (lang != null) L.Apply(lang); // juste pour la capture, on sauvegarde pas
        var f = new MainForm(app) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, 0), ShowInTaskbar = false };
        f.Show();
        var until = Environment.TickCount64 + 2500;
        while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(20); }
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(file);
        f.Close();
        app.Quit();
    }
}
