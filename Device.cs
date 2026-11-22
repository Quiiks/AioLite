using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AioLite;

// infos que l'ecran renvoie quand on lui envoie 0x06
public sealed record DeviceInfo(int Width, int Height, int Angle, string Model, string Version, int Brightness);

// serial stuff for the AIO screen (lovingcool SC70, vid 33C3)
// trouvé en regardant le code du logiciel lovingcool
// commande : 55 AA + taille (2 octets, payload+7) + cmd + payload + checksum (somme de tout, 2 octets)
// image : taille (4 octets) + le jpeg + checksum (2 octets)
public sealed class Device : IDisposable
{
    public const string Vid = "33C3";
    static readonly byte[] StopBytes = [0xFF, 0xD9, 0xFF, 0xD9];
    static readonly byte[] ZeroBytes = [0, 0, 0, 0];

    readonly SerialPort _port;
    readonly Stream _stream;

    public string PortName { get; }
    public DeviceInfo Info { get; private set; } = null!;

    Device(string portName)
    {
        PortName = portName;
        _port = new SerialPort(portName, 2_000_000) { ReadTimeout = 500, WriteTimeout = 5000, ReadBufferSize = 4096 };
        _port.Open();
        _stream = _port.BaseStream;
    }

    // find the COM port in the registry (WMI was too slow)
    public static string? FindPort()
    {
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null) return null;
        foreach (var dev in usb.GetSubKeyNames())
        {
            if (!dev.StartsWith("VID_" + Vid, StringComparison.OrdinalIgnoreCase)) continue;
            using var devKey = usb.OpenSubKey(dev);
            if (devKey == null) continue;
            foreach (var inst in devKey.GetSubKeyNames())
            {
                using var p = devKey.OpenSubKey(inst + @"\Device Parameters");
                if (p?.GetValue("PortName") is string name && present.Contains(name)) return name;
            }
        }
        return null;
    }

    public static Device Connect(string portName)
    {
        var d = new Device(portName);
        try
        {
            d.Raw(StopBytes);
            d.Raw(ZeroBytes);
            Thread.Sleep(200);
            d._port.DiscardInBuffer();
            d.Info = d.ReadInfo();
            return d;
        }
        catch { d.Dispose(); throw; }
    }

    DeviceInfo ReadInfo()
    {
        Command(0x06);
        var buf = new MemoryStream();
        var tmp = new byte[2048];
        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            int n;
            try { n = _stream.Read(tmp, 0, tmp.Length); } catch (TimeoutException) { continue; }
            buf.Write(tmp, 0, n);
            var all = buf.GetBuffer().AsSpan(0, (int)buf.Length);
            if (all.Length < 8) continue;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(all[5..^2]));
                var d = doc.RootElement.GetProperty("data");
                return new DeviceInfo(
                    d.GetProperty("width").GetInt32(),
                    d.GetProperty("height").GetInt32(),
                    d.TryGetProperty("angle", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0,
                    d.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                    d.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    d.TryGetProperty("brightness", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 100);
            }
            catch (JsonException) { /* pas tout recu encore */ }
            catch (KeyNotFoundException) { }
        }
        throw new IOException(L.T("L'écran ne répond pas à la demande d'infos.", "The screen didn't answer the info request."));
    }

    // live mode, faut le renvoyer genre toutes les 1.5s sinon l'ecran lache
    public void StartLive() => Command(0x11);

    // brightness 0-100 (marche pas en live mode btw)
    public void SetBrightness(int value) => Command(0x03, (byte)Math.Clamp(value, 0, 100));

    // stop le live, l'ecran repasse sur son truc a lui
    public void Stop() => Raw(StopBytes);

    public void SendPacket(ReadOnlySpan<byte> packet) => _stream.Write(packet);

    // l'ecran renvoie des acks, on les jette sinon le buffer se remplit
    public void DrainInput()
    {
        if (_port.BytesToRead > 0) _port.DiscardInBuffer();
    }

    void Command(byte cmd, params byte[] payload)
    {
        int len = payload.Length + 7;
        var b = new byte[len];
        b[0] = 0x55; b[1] = 0xAA; b[2] = (byte)len; b[3] = (byte)(len >> 8); b[4] = cmd;
        payload.CopyTo(b, 5);
        int sum = 0;
        for (int i = 0; i < len - 2; i++) sum += b[i];
        b[len - 2] = (byte)sum; b[len - 1] = (byte)(sum >> 8);
        Raw(b);
    }

    void Raw(byte[] data) => _stream.Write(data, 0, data.Length);

    // build the image packet (taille + jpeg + checksum)
    public static byte[] MakePacket(ReadOnlySpan<byte> jpeg)
    {
        var b = new byte[jpeg.Length + 6];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), jpeg.Length);
        jpeg.CopyTo(b.AsSpan(4));
        int sum = 0;
        for (int i = 0; i < jpeg.Length + 4; i++) sum += b[i];
        b[^2] = (byte)sum; b[^1] = (byte)(sum >> 8);
        return b;
    }

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
    }
}
