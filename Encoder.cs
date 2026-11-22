using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AioLite;

// convert video -> fichier .aio avec tous les paquets jpeg deja prets
// on le fait une seule fois par video, apres la lecture coute presque rien en cpu
// format: "AIO1" + fps*1000 (int) + les paquets a la suite
public static class Encoder
{
    static readonly string CacheDir = Path.Combine(Paths.Data, "cache");
    static readonly string ThumbDir = Path.Combine(Paths.Data, "thumbs");

    public static string? FfmpegPath { get; } = FindTool("ffmpeg.exe");
    public static string? FfprobePath { get; } = FindTool("ffprobe.exe");

    static string? FindTool(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        // sinon on prend celui de lovingcool
        var lc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "LOVINGCOOL MONITOR", "resources", "app.asar.unpacked", "node_modules");
        if (Directory.Exists(lc))
        {
            try { return Directory.EnumerateFiles(lc, exe, SearchOption.AllDirectories).FirstOrDefault(); } catch { }
        }
        return null;
    }

    // video size + rotation depending on the screen angle
    public static (int w, int h, string rot) Geometry(DeviceInfo info)
    {
        // half res on the 6.67", lovingcool fait pareil (sinon trop lourd pour l'ecran je pense)
        double scale = info.Width * info.Height >= 2000 * 1000 ? 0.5 : 1.0;
        int w = (int)Math.Round(info.Width * scale) & ~1, h = (int)Math.Round(info.Height * scale) & ~1;
        string rot = info.Angle switch { 90 => ",transpose=1", 270 => ",transpose=2", 180 => ",hflip,vflip", _ => "" };
        return (w, h, rot);
    }

    public static string CachePath(string source, DeviceInfo info, bool fill, int maxFps, int brightness)
    {
        var fi = new FileInfo(source);
        var (w, h, rot) = Geometry(info);
        var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{w}x{h}{rot}|{fill}|{maxFps}|b{brightness}|q3";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(CacheDir, hash + ".aio");
    }

    public static async Task<string> PrepareAsync(string source, DeviceInfo info, bool fill, int maxFps, int brightness,
        IProgress<double>? progress, CancellationToken ct)
    {
        var cache = CachePath(source, info, fill, maxFps, brightness);
        if (File.Exists(cache)) return cache;
        if (FfmpegPath == null) throw new InvalidOperationException("ffmpeg introuvable. Installe-le : winget install Gyan.FFmpeg");
        Directory.CreateDirectory(CacheDir);

        var (srcFps, duration) = await ProbeAsync(source, ct);
        double fps = srcFps is > 0 and < 1000 ? Math.Min(srcFps, maxFps) : maxFps;
        var (w, h, rot) = Geometry(info);
        var fit = fill
            ? $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}"
            : $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:-1:-1:black";
        var vf = $"fps={fps.ToString(CultureInfo.InvariantCulture)},{fit},setsar=1{rot},format=yuvj420p";
        // the screen ignores brightness in live mode so we darken the video itself
        // en gros on multiplie Y par k et on rapproche u/v de 128
        if (brightness < 100)
        {
            var k = (Math.Clamp(brightness, 1, 100) / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
            vf += $",lutyuv=y=val*{k}:u=128+(val-128)*{k}:v=128+(val-128)*{k}";
        }

        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-i", source, "-an", "-sn", "-dn", "-vf", vf, "-q:v", "3", "-f", "mjpeg", "pipe:1" })
            psi.ArgumentList.Add(a);

        var tmp = cache + ".tmp";
        using var proc = Process.Start(psi)!;
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        int frames = 0;
        double expected = duration > 0 ? duration * fps : 0;
        try
        {
            await using (var outFile = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var hdr = new byte[8];
                "AIO1"u8.CopyTo(hdr);
                BitConverter.TryWriteBytes(hdr.AsSpan(4), (int)Math.Round(fps * 1000));
                await outFile.WriteAsync(hdr, ct);

                var head = new byte[4];
                var tail = new byte[2];
                await foreach (var jpeg in SplitJpegs(proc.StandardOutput.BaseStream, ct))
                {
                    // on ecrit le paquet direct, pas de new byte[] a chaque frame (ram)
                    BitConverter.TryWriteBytes(head, jpeg.Length);
                    int sum = head[0] + head[1] + head[2] + head[3];
                    foreach (var b in jpeg.Span) sum += b;
                    tail[0] = (byte)sum; tail[1] = (byte)(sum >> 8);
                    await outFile.WriteAsync(head, ct);
                    await outFile.WriteAsync(jpeg, ct);
                    await outFile.WriteAsync(tail, ct);
                    frames++;
                    if (expected > 0 && frames % 10 == 0) progress?.Report(Math.Min(0.99, frames / expected));
                }
            }
            await proc.WaitForExitAsync(ct);
            if (frames == 0) throw new InvalidOperationException("Conversion échouée : " + (await errTask).Trim());
            File.Move(tmp, cache, true);
            progress?.Report(1);
            return cache;
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // split the mjpeg stream from ffmpeg into jpegs (FFD8 = debut, FFD9 = fin)
    // attention le buffer est reutilise, faut ecrire la frame avant la suivante
    static async IAsyncEnumerable<ReadOnlyMemory<byte>> SplitJpegs(Stream s, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        var cur = new MemoryStream(256 * 1024);
        byte prev = 0;
        bool inImage = false;
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (!inImage)
                {
                    if (prev == 0xFF && b == 0xD8) { inImage = true; cur.SetLength(0); cur.WriteByte(0xFF); cur.WriteByte(0xD8); }
                }
                else
                {
                    cur.WriteByte(b);
                    if (prev == 0xFF && b == 0xD9) { inImage = false; yield return cur.GetBuffer().AsMemory(0, (int)cur.Length); b = 0; }
                }
                prev = b;
            }
        }
    }

    static async Task<(double fps, double duration)> ProbeAsync(string source, CancellationToken ct)
    {
        if (FfprobePath == null) return (0, 0);
        var psi = new ProcessStartInfo(FfprobePath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate:format=duration", "-of", "default=nw=1", source })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var txt = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        double fps = 0, dur = 0;
        foreach (var line in txt.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("avg_frame_rate="))
            {
                var parts = line[15..].Split('/');
                if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a)
                    && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && b > 0) fps = a / b;
            }
            else if (line.StartsWith("duration=")) double.TryParse(line[9..], CultureInfo.InvariantCulture, out dur);
        }
        return (fps, dur);
    }

    // thumbnail pour la liste
    public static async Task<string?> ThumbnailAsync(string source)
    {
        if (FfmpegPath == null || !File.Exists(source)) return null;
        Directory.CreateDirectory(ThumbDir);
        var fi = new FileInfo(source);
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..16] + ".jpg";
        var outPath = Path.Combine(ThumbDir, name);
        if (File.Exists(outPath)) return outPath;
        var psi = new ProcessStartInfo(FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-y", "-ss", "1", "-i", source, "-frames:v", "1",
                     "-vf", "scale=400:180:force_original_aspect_ratio=increase,crop=400:180", "-q:v", "4", outPath })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (!File.Exists(outPath))
        {
            // video trop courte ou image -> first frame
            psi.ArgumentList[4] = "0";
            using var p2 = Process.Start(psi)!;
            await p2.StandardError.ReadToEndAsync();
            await p2.WaitForExitAsync();
        }
        return File.Exists(outPath) ? outPath : null;
    }
}
