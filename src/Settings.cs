using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernHFS;

public sealed class SharedItem
{
    /// <summary>Name in the URL: http://host:port/{Name}</summary>
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
    /// <summary>Who can browse/download this item and everything inside it.</summary>
    public Access Download { get; set; } = Access.Anyone;
    /// <summary>Who can upload into this folder (and its subfolders). Ignored for files.</summary>
    public UploadAccess Upload { get; set; } = UploadAccess.Off;

    public bool Exists => IsFolder ? Directory.Exists(Path) : File.Exists(Path);
}

public enum Access { Anyone, Accounts }
public enum UploadAccess { Off, Anyone, Accounts }

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public int Port { get; set; } = 8080;
    /// <summary>Server state at last exit; restored on launch.</summary>
    public bool ServerOn { get; set; } = true;
    public bool TrayTipShown { get; set; }
    public WindowPlacement? Window { get; set; }
    public List<SharedItem> Items { get; set; } = [];
    public List<BanEntry> Bans { get; set; } = [];
    public List<Account> Accounts { get; set; } = [];
    /// <summary>Uploads stop when the target disk would drop below this much free space.</summary>
    public int MinFreeMB { get; set; } = 1024;
    /// <summary>"" = every network card, "127.0.0.1" = this PC only, otherwise one card's IPv4 address.</summary>
    public string ListenAddress { get; set; } = "";
    /// <summary>Copied links use the Internet address (true) or the LAN one (false).</summary>
    public bool LinkInternet { get; set; } = true;
    /// <summary>Pane sizes in pixels (0 = default).</summary>
    public int ConnectionsHeight { get; set; }
    public int SharedWidth { get; set; }
    public int SideTab { get; set; }

    static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModernHFS");
    public static string FilePath => System.IO.Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch
        {
            // Keep the broken file for inspection instead of silently overwriting the share list.
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }
}
