using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;

namespace ModernHFS;

public enum LogKind { Info, Browse, Download, Done, Warn, Bad }

public sealed record LogEntry(DateTime Time, string Address, string Text, LogKind Kind);

/// <summary>One live TCP connection. Counters are written by Kestrel threads and read by the UI.</summary>
public sealed class Conn
{
    long bytesIn, bytesOut, requestOutStart, requestInStart, expected = -1;
    readonly Action<string> abort;

    public Conn(string id, IPAddress ip, int port, Action<string> abort)
    {
        Id = id;
        Ip = ip;
        Address = ip.ToString();
        Port = port;
        this.abort = abort;
    }

    public string Id { get; }
    public IPAddress Ip { get; }
    public string Address { get; }
    public int Port { get; }
    public DateTime Connected { get; } = DateTime.Now;
    public long BytesIn => Interlocked.Read(ref bytesIn);
    public long BytesOut => Interlocked.Read(ref bytesOut);
    internal void AddIn(long n) => Interlocked.Add(ref bytesIn, n);
    internal void AddOut(long n) => Interlocked.Add(ref bytesOut, n);

    // current / last request
    public volatile string File = "";
    public volatile string Status = "Connected";
    public volatile bool Busy;
    public volatile bool Uploading;
    public DateTime RequestStart = DateTime.Now;
    public long Expected { get => Interlocked.Read(ref expected); set => Interlocked.Exchange(ref expected, value); }
    internal void BeginRequest(string path)
    {
        File = path;
        Status = "Requesting";
        Busy = true;
        Uploading = false;
        Expected = -1;
        RequestStart = DateTime.Now;
        Interlocked.Exchange(ref requestOutStart, BytesOut);
        Interlocked.Exchange(ref requestInStart, BytesIn);
    }

    internal void BeginUpload(long contentLength)
    {
        Uploading = true;
        Status = "Uploading";
        Expected = contentLength;
        Interlocked.Exchange(ref requestInStart, BytesIn);
    }

    public long Received => Math.Max(0, BytesIn - Interlocked.Read(ref requestInStart));
    /// <summary>Bytes written for the current request (body + a few hundred bytes of headers).</summary>
    public long Sent => Math.Max(0, BytesOut - Interlocked.Read(ref requestOutStart));
    public double Progress
    {
        get { long e = Expected; return e > 0 ? Math.Clamp((double)(Uploading ? Received : Sent) / e, 0, 1) : -1; }
    }

    public void Kick(string reason = "Kicked") => abort(reason);
}

public sealed class BanEntry
{
    public string Pattern { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    /// <summary>Why: "Banned by you", "Scanner probe /wp-login.php", "10 wrong passwords"...</summary>
    public string Reason { get; set; } = "Banned by you";
    /// <summary>Auto-bans expire; manual bans don't (null).</summary>
    public DateTime? Expires { get; set; }
    public bool Expired => Expires is DateTime e && e <= DateTime.Now;
}

/// <summary>"203.0.113.5" (exact, IPv4 or IPv6) or "203.0.113.*" (prefix).</summary>
public sealed class BanRule
{
    public string Pattern { get; private init; } = "";
    string? prefix;
    string? exact;

    public static bool TryParse(string text, out BanRule rule)
    {
        rule = null!;
        var p = text.Trim();
        if (p.EndsWith('*'))
        {
            var pre = p[..^1];
            if (pre.Length < 2 || pre.Any(ch => !(char.IsAsciiHexDigit(ch) || ch is '.' or ':'))) return false;
            rule = new BanRule { Pattern = p, prefix = pre };
            return true;
        }
        if (!IPAddress.TryParse(p, out var ip)) return false;
        var norm = Traffic.Normalize(ip).ToString();
        rule = new BanRule { Pattern = norm, exact = norm };
        return true;
    }

    public bool Matches(string address) =>
        exact != null ? address.Equals(exact, StringComparison.OrdinalIgnoreCase)
                      : address.StartsWith(prefix!, StringComparison.OrdinalIgnoreCase);
}

public sealed class FileStat
{
    public long Downloads { get; set; }
    public DateTime Last { get; set; }
}

public sealed class VisitorStat
{
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    public long Hits { get; set; }
    public long Downloads { get; set; }
    public long Uploads { get; set; }
    public long BytesOut { get; set; }
}

public sealed class StatsData
{
    public DateTime Since { get; set; } = DateTime.Now;
    public long TotalIn { get; set; }
    public long TotalOut { get; set; }
    public long Hits { get; set; }
    public long Downloads { get; set; }
    public long Uploads { get; set; }
    public double TopSpeed { get; set; }
    public Dictionary<string, FileStat> Files { get; set; } = [];
    public Dictionary<string, VisitorStat> Visitors { get; set; } = [];

    static string PathOnDisk => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "stats.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static StatsData Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<StatsData>(File.ReadAllText(PathOnDisk), Json) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        // Internet scanners hit forwarded ports from thousands of addresses; keep the file bounded.
        if (Visitors.Count > 5000)
            foreach (var k in Visitors.OrderBy(v => v.Value.Last).Take(Visitors.Count - 5000).Select(v => v.Key).ToList())
                Visitors.Remove(k);
        if (Files.Count > 5000)
            foreach (var k in Files.OrderBy(v => v.Value.Last).Take(Files.Count - 5000).Select(v => v.Key).ToList())
                Files.Remove(k);
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        var tmp = PathOnDisk + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, PathOnDisk, true);
    }
}

/// <summary>What one HTTP request turned out to be; filled in by FileServer.</summary>
sealed class RequestInfo
{
    public bool Listing;
    public bool File;
    public bool Upload;
    public bool UploadOk;
    /// <summary>What the log and statistics call this download, when not the request path ("/Photos/ as ZIP").</summary>
    public string? LogName;
    public long Length;
    public long RangeStart;
}

/// <summary>Connections, byte counters, log queue, statistics and bans. Shared by the server and the window.</summary>
public sealed class Traffic
{
    const long BigFile = 4 * 1024 * 1024;

    readonly ConcurrentDictionary<string, Conn> live = new();
    readonly ConcurrentQueue<LogEntry> pending = new();
    readonly ConcurrentDictionary<string, long> blocked = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, DateTime> blockedLogged = new();
    readonly object statsLock = new();
    long totalIn, totalOut, statsVersion;
    volatile BanRule[] bans = [];
    StatsData stats;

    public Traffic()
    {
        stats = StatsData.Load();
        totalIn = stats.TotalIn;
        totalOut = stats.TotalOut;
    }

    public Guard Guard { get; } = new();

    public long TotalIn => Interlocked.Read(ref totalIn);
    public long TotalOut => Interlocked.Read(ref totalOut);
    public long StatsVersion => Interlocked.Read(ref statsVersion);
    public ICollection<Conn> Live => live.Values;   // snapshot
    public int LiveCount => live.Count;

    public static IPAddress Normalize(IPAddress? a) =>
        a == null ? IPAddress.None : a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    // ---- log -----------------------------------------------------------------------------------

    public void Log(string address, string text, LogKind kind) =>
        pending.Enqueue(new(DateTime.Now, Clean(address, 64), Clean(text, 400), kind));

    /// <summary>Visitor-controlled text (paths, user names) can't forge log lines or flip text direction.</summary>
    public static string Clean(string text, int max)
    {
        var chars = text.Length > max ? text[..max].ToCharArray() : text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i]) || chars[i] is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F')
                chars[i] = '?';
        return new string(chars) + (text.Length > max ? "..." : "");
    }
    public bool TryDequeue(out LogEntry entry) => pending.TryDequeue(out entry!);

    // ---- bans ----------------------------------------------------------------------------------

    public void SetBans(IEnumerable<BanEntry> entries)
    {
        var list = new List<BanRule>();
        foreach (var e in entries)
            if (!e.Expired && BanRule.TryParse(e.Pattern, out var r)) list.Add(r);
        bans = list.ToArray();
    }

    public string? BannedBy(string address)
    {
        foreach (var b in bans)
            if (b.Matches(address)) return b.Pattern;
        return null;
    }

    public long BlockedCount(string pattern) => blocked.TryGetValue(pattern, out var n) ? n : 0;

    // ---- connection + request hooks (called from Kestrel threads) ------------------------------

    internal async Task OnConnection(ConnectionContext ctx, ConnectionDelegate next)
    {
        var ep = ctx.RemoteEndPoint as IPEndPoint;
        var ip = Normalize(ep?.Address);
        var address = ip.ToString();

        if (BannedBy(address) is string pattern)
        {
            blocked.AddOrUpdate(pattern, 1, (_, n) => n + 1);
            var now = DateTime.Now;
            if (!blockedLogged.TryGetValue(address, out var last) || now - last > TimeSpan.FromMinutes(1))
            {
                blockedLogged[address] = now;
                Log(address, "Blocked: this address is banned", LogKind.Bad);
            }
            ctx.Abort(new ConnectionAbortedException("banned"));
            return;
        }

        if (!Guard.TryOpenConnection(address, ip))
        {
            Guard.Strike(address, ip, 1, $"More than {Guard.MaxConnectionsPerAddress} connections at once");
            ctx.Abort(new ConnectionAbortedException("too many connections"));
            return;
        }

        var conn = new Conn(ctx.ConnectionId, ip, ep?.Port ?? 0, reason => ctx.Abort(new ConnectionAbortedException(reason)));
        var original = ctx.Transport;
        ctx.Transport = new CountingPipe(original, conn, this);
        live[conn.Id] = conn;
        try { await next(ctx); }
        finally
        {
            Guard.CloseConnection(address, ip);
            live.TryRemove(conn.Id, out _);
            ctx.Transport = original;
            lock (statsLock)
            {
                Visitor(address).BytesOut += conn.BytesOut;
                statsVersion++;
            }
        }
    }

    internal Conn? Find(string connectionId) => live.TryGetValue(connectionId, out var c) ? c : null;

    internal void AddIn(long n) => Interlocked.Add(ref totalIn, n);
    internal void AddOut(long n) => Interlocked.Add(ref totalOut, n);

    internal void Hit(string address)
    {
        lock (statsLock)
        {
            stats.Hits++;
            Visitor(address).Hits++;
            statsVersion++;
        }
    }

    internal void Finish(string address, string method, string path, RequestInfo info, int status, bool aborted,
        Conn? conn, TimeSpan took)
    {
        if (info.Upload) return;   // the upload handler logs each file itself
        if (info.LogName != null) path = info.LogName;
        if (info.File)
        {
            if (status is 200 or 206)
            {
                long expected = conn?.Expected ?? -1;
                long sent = conn?.Sent ?? 0;
                if (expected > 0) sent = Math.Min(sent, expected);
                string speed = took.TotalSeconds >= 1 ? " at " + Fmt.Size((long)(sent / took.TotalSeconds)) + "/s" : "";
                if (!aborted)
                {
                    bool full = info.RangeStart == 0 && (expected < 0 || expected >= info.Length);
                    if (full)
                    {
                        Downloaded(address, path);
                        Log(address, $"Fully downloaded {path} ({Fmt.Size(info.Length)}{speed})", LogKind.Done);
                    }
                    else if (sent >= BigFile)
                        Log(address, $"Sent {Fmt.Size(sent)} of {path}{speed}", LogKind.Download);
                }
                else
                {
                    var of = expected > 0 ? " of " + Fmt.Size(expected) : "";
                    Log(address, $"Interrupted {path} after {Fmt.Size(sent)}{of}", LogKind.Warn);
                }
            }
            else if (status == 416) Log(address, $"Bad range for {path}", LogKind.Warn);
            return;
        }
        if (info.Listing)
        {
            if (status == 200) Log(address, $"Browsing {path}", LogKind.Browse);
            return;
        }
        switch (status)
        {
            case 404: Log(address, $"Not found {path}", LogKind.Warn); break;
            case 400: Log(address, $"Bad request {path}", LogKind.Bad); break;
            case 403: Log(address, $"Refused {path}", LogKind.Warn); break;
            case 401: break;   // the sign-in prompt; wrong passwords are logged by FileServer
            case 405: Log(address, $"Refused {method} {path}", LogKind.Bad); break;
        }
    }

    internal void FileStarting(string address, string path, RequestInfo info)
    {
        if (info.LogName != null) path = info.LogName;
        if (info.Length - info.RangeStart < BigFile) return;
        Log(address, info.RangeStart > 0
            ? $"Resuming {path} from {Fmt.Size(info.RangeStart)} ({Fmt.Size(info.Length)} total)"
            : $"Downloading {path} ({Fmt.Size(info.Length)})", LogKind.Download);
    }

    void Downloaded(string address, string path)
    {
        lock (statsLock)
        {
            stats.Downloads++;
            if (!stats.Files.TryGetValue(path, out var f)) stats.Files[path] = f = new FileStat();
            f.Downloads++;
            f.Last = DateTime.Now;
            Visitor(address).Downloads++;
            statsVersion++;
        }
    }

    internal void Uploaded(string address)
    {
        lock (statsLock)
        {
            stats.Uploads++;
            Visitor(address).Uploads++;
            statsVersion++;
        }
    }

    VisitorStat Visitor(string address)
    {
        if (!stats.Visitors.TryGetValue(address, out var v))
            stats.Visitors[address] = v = new VisitorStat { First = DateTime.Now };
        v.Last = DateTime.Now;
        return v;
    }

    // ---- statistics access ---------------------------------------------------------------------

    public T ReadStats<T>(Func<StatsData, T> read)
    {
        lock (statsLock) return read(stats);
    }

    public void NoteSpeed(double bytesPerSecond)
    {
        lock (statsLock)
        {
            if (bytesPerSecond > stats.TopSpeed) { stats.TopSpeed = bytesPerSecond; statsVersion++; }
        }
    }

    public void ResetStats()
    {
        lock (statsLock)
        {
            stats = new StatsData();
            Interlocked.Exchange(ref totalIn, 0);
            Interlocked.Exchange(ref totalOut, 0);
            statsVersion++;
        }
        SaveStats();
    }

    public void SaveStats()
    {
        lock (statsLock)
        {
            stats.TotalIn = TotalIn;
            stats.TotalOut = TotalOut;
            try { stats.Save(); } catch { }
        }
    }
}

// ---- byte counting on the raw connection ------------------------------------------------------

sealed class CountingPipe(IDuplexPipe inner, Conn conn, Traffic traffic) : IDuplexPipe
{
    public PipeReader Input { get; } = new CountingReader(inner.Input, conn, traffic);
    public PipeWriter Output { get; } = new CountingWriter(inner.Output, conn, traffic);
}

sealed class CountingReader(PipeReader inner, Conn conn, Traffic traffic) : PipeReader
{
    ReadOnlySequence<byte> last;

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        long n = 0;
        try { n = last.Slice(last.Start, consumed).Length; } catch { }
        if (n > 0) { conn.AddIn(n); traffic.AddIn(n); }
        last = default;
        inner.AdvanceTo(consumed, examined);
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken ct = default)
    {
        var r = await inner.ReadAsync(ct);
        last = r.Buffer;
        return r;
    }

    public override bool TryRead(out ReadResult result)
    {
        if (!inner.TryRead(out result)) return false;
        last = result.Buffer;
        return true;
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();
    public override void Complete(Exception? exception = null) => inner.Complete(exception);
    public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
}

sealed class CountingWriter(PipeWriter inner, Conn conn, Traffic traffic) : PipeWriter
{
    public override void Advance(int bytes)
    {
        conn.AddOut(bytes);
        traffic.AddOut(bytes);
        inner.Advance(bytes);
    }

    public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        conn.AddOut(source.Length);
        traffic.AddOut(source.Length);
        return inner.WriteAsync(source, ct);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    public override ValueTask<FlushResult> FlushAsync(CancellationToken ct = default) => inner.FlushAsync(ct);
    public override void CancelPendingFlush() => inner.CancelPendingFlush();
    public override void Complete(Exception? exception = null) => inner.Complete(exception);
    public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
    public override long UnflushedBytes => inner.UnflushedBytes;
}
