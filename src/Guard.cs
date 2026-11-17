using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ModernHFS;

/// <summary>
/// Bot protection: per-address connection cap, request rate limit, and a strike counter that auto-bans
/// scanners (wp-login.php, .env, path tricks, floods of not-found requests).
/// LAN and loopback addresses are never limited or auto-banned.
/// </summary>
public sealed class Guard
{
    sealed class State
    {
        public double Tokens;
        public DateTime LastRefill = DateTime.UtcNow;
        public DateTime LastSeen = DateTime.UtcNow;
        public DateTime LastThrottleLog;
        public int Connections;
        public readonly List<(DateTime At, int Weight)> Strikes = [];
        public bool Banned;
    }

    static readonly TimeSpan StrikeWindow = TimeSpan.FromMinutes(30);

    // First path segment (or file extension) that only vulnerability scanners ask for.
    static readonly HashSet<string> TrapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wp-admin", "wp-login.php", "wp-content", "wp-includes", "wordpress", "xmlrpc.php", ".env", ".git", ".svn",
        ".aws", ".ssh", ".vscode", ".DS_Store", "phpmyadmin", "pma", "myadmin", "cgi-bin", "boaform", "HNAP1",
        "actuator", "vendor", "owa", "ecp", "autodiscover", "solr", "jenkins", "manager", "console", "shell",
        "setup.cgi", "server-status", "config.json", "docker-compose.yml", "telescope", "geoserver", "remote",
        "cgi", "admin.php", "login.action", "struts", "druid", "nacos", "jmx-console", "invoker", "hudson",
    };
    static readonly string[] TrapExtensions = [".php", ".asp", ".aspx", ".jsp", ".cgi", ".pl", ".env", ".action", ".do"];

    readonly ConcurrentDictionary<string, State> states = new();

    /// <summary>Raised (on a server thread) with (address, reason) when an address should be banned.</summary>
    public event Action<string, string>? AutoBan;

    public bool ExemptPrivate { get; set; } = true;
    public int MaxConnectionsPerAddress { get; set; } = 24;
    public double RequestsPerSecond { get; set; } = 30;
    public double Burst { get; set; } = 150;
    public int BanAtStrikes { get; set; } = 10;

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
    }

    bool Exempt(IPAddress ip) => ExemptPrivate && IsPrivate(ip);

    State Get(string address) => states.GetOrAdd(address, _ => new State { Tokens = Burst });

    // ---- connections ---------------------------------------------------------------------------

    public bool TryOpenConnection(string address, IPAddress ip)
    {
        if (Exempt(ip)) return true;
        var s = Get(address);
        lock (s)
        {
            s.LastSeen = DateTime.UtcNow;
            if (s.Connections >= MaxConnectionsPerAddress) return false;
            s.Connections++;
            return true;
        }
    }

    public void CloseConnection(string address, IPAddress ip)
    {
        if (Exempt(ip) || !states.TryGetValue(address, out var s)) return;
        lock (s) s.Connections = Math.Max(0, s.Connections - 1);
    }

    // ---- requests ------------------------------------------------------------------------------

    /// <summary>Token bucket. False = too many requests; <paramref name="log"/> = first refusal this minute.</summary>
    public bool AllowRequest(string address, IPAddress ip, out bool log)
    {
        log = false;
        if (Exempt(ip)) return true;
        var s = Get(address);
        lock (s)
        {
            var now = DateTime.UtcNow;
            s.Tokens = Math.Min(Burst, s.Tokens + (now - s.LastRefill).TotalSeconds * RequestsPerSecond);
            s.LastRefill = now;
            s.LastSeen = now;
            if (s.Tokens >= 1) { s.Tokens -= 1; return true; }
            if (now - s.LastThrottleLog > TimeSpan.FromMinutes(1)) { s.LastThrottleLog = now; log = true; }
            return false;
        }
    }

    /// <summary>Scanner probe? Only checked when the first segment isn't a shared item.</summary>
    public static bool IsTrap(string[] segs)
    {
        foreach (var seg in segs)
        {
            if (TrapNames.Contains(seg)) return true;
            var ext = Path.GetExtension(seg);
            if (ext.Length > 0 && TrapExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Adds suspicion; at <see cref="BanAtStrikes"/> within 30 minutes the address gets auto-banned.</summary>
    public void Strike(string address, IPAddress ip, int weight, string reason)
    {
        if (Exempt(ip)) return;
        var s = Get(address);
        bool ban = false;
        lock (s)
        {
            var now = DateTime.UtcNow;
            s.Strikes.RemoveAll(x => now - x.At > StrikeWindow);
            s.Strikes.Add((now, weight));
            if (!s.Banned && s.Strikes.Sum(x => x.Weight) >= BanAtStrikes) { s.Banned = true; ban = true; }
        }
        if (ban) AutoBan?.Invoke(address, reason);
    }

    /// <summary>After an unban: start from a clean slate.</summary>
    public void Forget(string address) => states.TryRemove(address, out _);

    /// <summary>Drops idle entries; call now and then.</summary>
    public void Prune()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var kv in states)
            lock (kv.Value)
                if (kv.Value.Connections == 0 && kv.Value.LastSeen < cutoff) states.TryRemove(kv.Key, out _);
    }
}
