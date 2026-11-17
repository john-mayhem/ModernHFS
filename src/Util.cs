using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ModernHFS;

static class Native
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);

    [DllImport("user32.dll")]
    public static extern bool ShowScrollBar(IntPtr hwnd, int bar, bool show);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
}

public static class Theme
{
    public static readonly bool Dark = ReadDark();

    // Windows 11 / Fluent neutrals (no blue tint), hairline strokes.
    public static readonly Color Bg = Dark ? C(0x202020) : C(0xF3F3F3);
    public static readonly Color Surface = Dark ? C(0x2B2B2B) : C(0xFFFFFF);
    public static readonly Color HeaderBg = Dark ? C(0x272727) : C(0xF9F9F9);
    public static readonly Color Hover = Dark ? C(0x333333) : C(0xF2F2F2);
    public static readonly Color Selected = Dark ? C(0x383838) : C(0xEBEBEB);
    public static readonly Color Border = Dark ? C(0x383838) : C(0xE5E5E5);
    public static readonly Color BorderStrong = Dark ? C(0x4D4D4D) : C(0xCFCFCF);
    public static readonly Color Text = Dark ? C(0xF3F3F3) : C(0x1B1B1B);
    public static readonly Color SubText = Dark ? C(0xADADAD) : C(0x616161);

    // The user's Windows accent colour, in the shades Windows itself uses for dark and light mode.
    static readonly Color[] Palette = ReadAccentPalette();
    public static readonly Color Accent = Dark ? Palette[1] : Palette[4];          // fills and links
    public static readonly Color AccentHover = Dark ? Palette[2] : Palette[3];
    public static readonly Color AccentText = Dark ? Palette[1] : Palette[5];      // accent-coloured text on a surface
    public static readonly Color OnAccent = Dark ? C(0x000000) : C(0xFFFFFF);      // text on an accent fill
    public static readonly Color AccentSoft = Mix(Surface, Accent, Dark ? 0.16f : 0.10f);

    // Fluent status colours: used only for status, never decoration.
    public static readonly Color Good = Dark ? C(0x6CCB5F) : C(0x0F7B0F);
    public static readonly Color Warn = Dark ? C(0xFCE100) : C(0x9D5D00);
    public static readonly Color Bad = Dark ? C(0xFF99A4) : C(0xC42B1C);

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static Color[] ReadAccentPalette()
    {
        // HKCU\...\Explorer\Accent\AccentPalette = 8 RGBA entries: Light3, Light2, Light1, Base, Dark1, Dark2, Dark3, (unused)
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] b && b.Length >= 28)
                return Enumerable.Range(0, 7).Select(i => Color.FromArgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2])).ToArray();
        }
        catch { }
        // Windows default blue
        return [C(0x99EBFF), C(0x4CC2FF), C(0x0091F8), C(0x0078D4), C(0x005FB8), C(0x004275), C(0x002642)];
    }

    static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static int S(this Control c, float px) => (int)Math.Round(px * c.DeviceDpi / 96f);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Rectangle r, Color fill, Color? border, float radius, float borderWidth = 1)
    {
        var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        using var path = Round(rf, radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is Color bc)
        {
            using var pen = new Pen(bc, borderWidth);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Dark/light title bar matching the window background (Windows 11).</summary>
    public static void ApplyTitleBar(IntPtr hwnd)
    {
        int dark = Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);           // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
        Native.DwmSetWindowAttribute(hwnd, 35, ref caption, 4);        // DWMWA_CAPTION_COLOR
        int text = Text.R | (Text.G << 8) | (Text.B << 16);
        Native.DwmSetWindowAttribute(hwnd, 36, ref text, 4);           // DWMWA_TEXT_COLOR
    }

    static bool ReadDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}

/// <summary>Windows 11 type: Segoe UI Variable (falls back to Segoe UI on Windows 10).</summary>
public static class UiFont
{
    static readonly HashSet<string> Installed =
        new(new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
    static readonly bool Variable = Installed.Contains("Segoe UI Variable Text");

    public static Font Body(float pt = 9.75f) => new(Variable ? "Segoe UI Variable Text" : "Segoe UI", pt);
    public static Font Semibold(float pt) => new(Variable ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold", pt);
    // GDI truncates family names to 31 characters, hence "Semib".
    public static Font Display(float pt) => Installed.Contains("Segoe UI Variable Display Semib")
        ? new("Segoe UI Variable Display Semib", pt) : new("Segoe UI Semibold", pt);

    static readonly string IconFamily = Installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public static Font Icon(float pt) => new(IconFamily, pt);
}

/// <summary>Segoe Fluent Icons code points.</summary>
public static class Glyph
{
    public const char None = '\0';
    public const char Copy = '\uE8C8';
    public const char Open = '\uE8A7';
    public const char Play = '\uE768';
    public const char Stop = '\uE71A';
    public const char Restart = '\uE72C';
    public const char Power = '';
    public const char Network = '';
    public const char Upload = '';
    public const char Download = '\uE896';
    public const char Lock = '\uE72E';
    public const char Add = '\uE710';
    public const char Delete = '\uE74D';
    public const char Save = '\uE74E';
    public const char Clear = '\uE894';
    public const char Shield = '\uEA18';
    public const char Globe = '\uE774';
}

public static class AppIcon
{
    static readonly Color On = Theme.Dark ? Theme.AccentHover : Theme.Accent;
    static readonly Color Off = Color.FromArgb(0x80, 0x86, 0x90);

    public static Bitmap Draw(int s, bool on)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = s * 0.04f;
        using (var path = Theme.Round(new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad), s * 0.24f))
        using (var b = new SolidBrush(on ? On : Off))
            g.FillPath(b, path);
        using var pen = new Pen(Color.White, Math.Max(1.6f, s * 0.095f))
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        float cx = s / 2f;
        g.DrawLine(pen, cx, s * 0.22f, cx, s * 0.58f);
        g.DrawLines(pen, [new PointF(s * 0.33f, s * 0.43f), new PointF(cx, s * 0.60f), new PointF(s * 0.67f, s * 0.43f)]);
        g.DrawLine(pen, s * 0.27f, s * 0.77f, s * 0.73f, s * 0.77f);
        return bmp;
    }

    public static Icon Make(int size, bool on)
    {
        using var bmp = Draw(size, on);
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { Native.DestroyIcon(h); }
    }

    public static byte[] Png(int size)
    {
        using var bmp = Draw(size, true);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

static class ShellIcons
{
    static readonly Dictionary<string, Icon?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Icon? Get(string path, bool folder)
    {
        bool exists = folder ? Directory.Exists(path) : File.Exists(path);
        string key = exists ? path : (folder ? "\0dir" : "\0file" + Path.GetExtension(path));
        if (Cache.TryGetValue(key, out var icon)) return icon;

        const uint SHGFI_ICON = 0x100, SHGFI_USEFILEATTRIBUTES = 0x10;
        uint flags = SHGFI_ICON, attrs = 0;
        string query = path;
        if (!exists)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attrs = folder ? 0x10u : 0x80u;
            if (!folder) query = "x" + Path.GetExtension(path);
        }
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(query, attrs, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), flags);
        icon = null;
        if (info.hIcon != IntPtr.Zero)
        {
            using (var tmp = Icon.FromHandle(info.hIcon)) icon = (Icon)tmp.Clone();
            Native.DestroyIcon(info.hIcon);
        }
        Cache[key] = icon;
        return icon;
    }
}

static class Net
{
    /// <summary>IPv4 of the adapter that has a default gateway (the real LAN, not Hyper-V/VPN stubs).</summary>
    public static string? LanIPv4()
    {
        try
        {
            string? fallback = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var props = ni.GetIPProperties();
                var v4 = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null) continue;
                bool gateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                if (gateway) return v4.Address.ToString();
                fallback ??= v4.Address.ToString();
            }
            return fallback;
        }
        catch { return null; }
    }
}

static class Fmt
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + Units[i];
    }

    public static string Date(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
