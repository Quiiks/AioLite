using System.Runtime.InteropServices;

namespace AioLite;

// petit systeme de langue, juste fr / en
// usage: L.T("texte fr", "english text")
public static class L
{
    public static bool En { get; set; }

    public static string T(string fr, string en) => En ? en : fr;

    [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();

    // si windows est en francais on met fr par defaut, sinon english
    public static string SystemDefault()
    {
        try { return (GetUserDefaultUILanguage() & 0x3FF) == 0x0C ? "fr" : "en"; }
        catch { return "en"; }
    }

    public static void Apply(string? lang) => En = (lang ?? SystemDefault()) != "fr";
}
