using System.Runtime.InteropServices;

namespace AioLite;

public enum PlayerState { Searching, NoScreen, NoVideo, Preparing, Playing, Paused, Error }

// player thread: lit le fichier .aio et envoie les frames sur le port com
// no decoding here, juste read + write + wait, c'est pour ca que ca prend 0% cpu
public sealed class Player : IDisposable
{
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    volatile bool _quit;
    volatile string? _clip;          // le .aio a jouer
    volatile int _brightness = -1;   // -1 = nothing to send
    volatile bool _paused;
    int _clipVersion;

    public DeviceInfo? Info { get; private set; }
    public PlayerState State { get; private set; } = PlayerState.Searching;
    string _detail = "";

    // le texte est calcule a chaque fois comme ca il change direct quand on change la langue
    public string StatusText => State switch
    {
        PlayerState.NoScreen => L.T("Écran non détecté", "Screen not detected"),
        PlayerState.NoVideo => L.T("Aucune vidéo choisie", "No video selected"),
        PlayerState.Playing => L.T("Lecture", "Playing") + " — " + _detail,
        PlayerState.Paused => L.T("En pause", "Paused"),
        PlayerState.Preparing or PlayerState.Error => _detail,
        _ => L.T("Recherche de l'écran…", "Looking for the screen…"),
    };
    public double Fps { get; private set; }

    // fired from the player thread (attention pas le thread UI)
    public event Action? Changed;

    public Player(bool paused, bool start = true)
    {
        _paused = paused;
        _thread = new Thread(Run) { IsBackground = true, Name = "AioLite player", Priority = ThreadPriority.AboveNormal };
        if (start) _thread.Start(); else _quit = true;
    }

    public void Play(string cacheFile, string name) { _name = name; _clip = cacheFile; Interlocked.Increment(ref _clipVersion); _wake.Set(); }
    volatile string _name = "";

    // fps of the cached file
    public double SourceFps { get; private set; }
    public void SetBrightness(int v) { _brightness = v; _wake.Set(); }
    public void SetPaused(bool p) { _paused = p; _wake.Set(); }

    void Set(PlayerState s, string detail = "")
    {
        if (State == s && _detail == detail) return;
        State = s; _detail = detail;
        Changed?.Invoke();
    }

    public void ReportPreparing(string text) => Set(PlayerState.Preparing, text);

    void Run()
    {
        using var timer = new HiResTimer();
        while (!_quit)
        {
            var portName = Device.FindPort();
            if (portName == null) { Set(PlayerState.NoScreen); _wake.WaitOne(3000); continue; }

            Device dev;
            try { dev = Device.Connect(portName); }
            catch (Exception e) { Set(PlayerState.Error, $"{portName} : {e.Message}"); _wake.WaitOne(3000); continue; }

            Info = dev.Info;
            Changed?.Invoke();
            try { PlayLoop(dev, timer); }
            catch (Exception e) when (!_quit) { Set(PlayerState.Error, L.T("Connexion perdue : ", "Connection lost: ") + e.Message); }
            finally
            {
                try { dev.Stop(); } catch { }
                dev.Dispose();
            }
            if (!_quit) _wake.WaitOne(2000);
        }
    }

    void PlayLoop(Device dev, HiResTimer timer)
    {
        var buf = new byte[512 * 1024];
        bool live = false;
        while (!_quit)
        {
            var clip = _clip;
            if (_paused || clip == null || !File.Exists(clip))
            {
                if (live) { dev.Stop(); live = false; }
                if (_paused) Set(PlayerState.Paused);
                else if (State != PlayerState.Preparing) Set(PlayerState.NoVideo);
                _wake.WaitOne(1500);
                ApplyBrightness(dev);
                dev.DrainInput();
                continue;
            }

            int version = _clipVersion;
            using var fs = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var hdr = new byte[8];
            fs.ReadExactly(hdr);
            double fps = BitConverter.ToInt32(hdr, 4) / 1000.0;
            if (fps <= 0) fps = 30;
            SourceFps = fps;
            Fps = 0;
            long frameTicks = (long)(TimeSpan.TicksPerSecond / fps);

            dev.StartLive(); live = true;
            ApplyBrightness(dev, force: true);
            Set(PlayerState.Playing, _name);

            long nextKeepAlive = Environment.TickCount64 + 1500;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long due = 0;
            int sentSinceMeasure = 0;
            long measureStart = 0;

            while (!_quit && !_paused && version == _clipVersion)
            {
                // next frame, si fin du fichier on recommence (loop)
                if (fs.Read(buf, 0, 4) < 4) { fs.Position = 8; continue; }
                int len = BitConverter.ToInt32(buf, 0);
                if (len <= 0 || len + 6 > buf.Length) throw new InvalidDataException(L.T("cache corrompu", "corrupted cache"));
                fs.ReadExactly(buf, 4, len + 2);

                dev.SendPacket(buf.AsSpan(0, len + 6));

                // real fps, on recalcule toutes les 2s
                sentSinceMeasure++;
                long el = sw.Elapsed.Ticks - measureStart;
                if (el >= 2 * TimeSpan.TicksPerSecond)
                {
                    double real = sentSinceMeasure * (double)TimeSpan.TicksPerSecond / el;
                    sentSinceMeasure = 0; measureStart = sw.Elapsed.Ticks;
                    if (Math.Abs(real - Fps) >= 0.5) { Fps = real; Changed?.Invoke(); }
                }

                long now = Environment.TickCount64;
                if (now >= nextKeepAlive) { dev.StartLive(); dev.DrainInput(); nextKeepAlive = now + 1500; }
                ApplyBrightness(dev);

                due += frameTicks;
                long late = sw.Elapsed.Ticks - due;
                if (late > TimeSpan.TicksPerSecond) due = sw.Elapsed.Ticks; // trop de retard, reset
                else if (late < 0) timer.Wait(-late);
            }
        }
    }

    void ApplyBrightness(Device dev, bool force = false)
    {
        int b = _brightness;
        if (b >= 0) { _brightness = -1; dev.SetBrightness(b); _lastBrightness = b; }
        else if (force && _lastBrightness >= 0) dev.SetBrightness(_lastBrightness);
    }
    int _lastBrightness = -1;

    public void Dispose()
    {
        _quit = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(3000);
    }
}

// Thread.Sleep c'est 15ms de precision c nul pour 60fps, donc timer high res de windows
sealed class HiResTimer : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    readonly IntPtr _h;
    public HiResTimer()
    {
        _h = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);
        if (_h == IntPtr.Zero) _h = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
    }
    public void Wait(long ticks100ns)
    {
        if (ticks100ns <= 0) return;
        long due = -ticks100ns;
        if (_h != IntPtr.Zero && SetWaitableTimer(_h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_h, 0xFFFFFFFF);
        else Thread.Sleep(TimeSpan.FromTicks(ticks100ns));
    }
    public void Dispose() { if (_h != IntPtr.Zero) CloseHandle(_h); }
}
