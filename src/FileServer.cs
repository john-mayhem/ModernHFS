using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace ModernHFS;

enum UploadState { None, Allowed, NeedLogin }

/// <summary>What a page may show for the current visitor.</summary>
sealed class PageContext
{
    public string? User;
    public bool HasAccounts;
    public string LoginUrl = "/?login=%2F";
    public UploadState Upload;
    public bool ShowFiles = true;
    /// <summary>Per-response CSP nonce for the page's own upload script.</summary>
    public string Nonce = "";
    /// <summary>The folder had more entries than one page shows.</summary>
    public bool Truncated;
}

/// <param name="Status">HTTP status this result calls for: 0 = saved, 400 bad input, 500 server-side failure, 507 disk full.</param>
sealed record UploadResult(string Name, string? SavedAs, long Bytes, string? Error, int Status = 0);

/// <summary>
/// The web side. URL layout: "/" lists the shared items, "/{item}/sub/path" walks inside a shared folder.
/// A visitor can only ever reach paths under an item the owner dragged in.
/// </summary>
public sealed class FileServer
{
    sealed record Snapshot(IReadOnlyList<SharedItem> Order, IReadOnlyDictionary<string, SharedItem> ByName);

    sealed class DiskFullException() : IOException("The disk is full");
    sealed class VisitorBodyException(Exception inner) : Exception("Reading the upload failed", inner);

    static readonly FileExtensionContentTypeProvider Types = new();
    static readonly char[] BadChars = Path.GetInvalidFileNameChars();
    static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    readonly byte[] favicon;
    readonly Traffic traffic;
    // ZIP downloads read whole folders: a few at a time, at most two per address.
    readonly SemaphoreSlim zipSlots = new(4, 4);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> zipsPerAddress = new();
    readonly Authenticator auth;
    volatile Snapshot snap = new([], new Dictionary<string, SharedItem>());
    WebApplication? app;

    public FileServer(byte[] faviconPng, Traffic traffic, Authenticator auth)
    {
        favicon = faviconPng;
        this.traffic = traffic;
        this.auth = auth;
    }

    public bool IsRunning => app != null;
    public int Port { get; private set; }
    public long MinFreeBytes { get; set; } = 1L << 30;

    /// <summary>Called by the UI after every change; requests in flight keep the old snapshot.</summary>
    public void SetItems(IEnumerable<SharedItem> items)
    {
        var copy = items.Select(i => new SharedItem
        {
            Name = i.Name, Path = i.Path, IsFolder = i.IsFolder, Download = i.Download, Upload = i.Upload,
        }).ToList();
        var byName = new Dictionary<string, SharedItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in copy) byName.TryAdd(i.Name, i);
        snap = new Snapshot(copy, byName);
    }

    public async Task StartAsync(int port, IPAddress? bindTo = null)
    {
        if (app != null) return;
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        b.Configuration.Sources.Clear();          // no appsettings.json / ASPNETCORE_URLS surprises
        b.Logging.ClearProviders();
        b.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        b.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);      // slowloris: headers must arrive quickly
            k.Limits.MaxRequestHeaderCount = 60;
            k.Limits.MaxRequestHeadersTotalSize = 24 * 1024;
            k.Limits.MaxRequestLineSize = 8 * 1024;
            k.Limits.MaxConcurrentConnections = 500;
            k.Limits.MaxConcurrentUpgradedConnections = 0;                  // no websockets here
            k.Limits.MaxRequestBodySize = 64 * 1024;                        // raised per request for real uploads only
            void Hook(ListenOptions lo) => lo.Use(next => c => traffic.OnConnection(c, next));
            if (bindTo == null) k.ListenAnyIP(port, Hook); else k.Listen(bindTo, port, Hook);
        });

        var a = b.Build();
        a.Run(TrackAsync);
        try { await a.StartAsync(); }
        catch { await a.DisposeAsync(); throw; }
        app = a;
        Port = port;
    }

    public async Task StopAsync()
    {
        var a = app;
        if (a == null) return;
        app = null;
        try { await a.StopAsync(); }
        finally { await a.DisposeAsync(); }
    }

    /// <summary>Wraps every request: connection status, statistics and the log line.</summary>
    async Task TrackAsync(HttpContext ctx)
    {
        var conn = traffic.Find(ctx.Connection.Id);
        var address = conn?.Address ?? Traffic.Normalize(ctx.Connection.RemoteIpAddress).ToString();
        var path = ctx.Request.Path.Value ?? "/";
        var info = new RequestInfo();
        var started = DateTime.Now;
        conn?.BeginRequest(path);
        if (!path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)) traffic.Hit(address);
        var res = ctx.Response;
        if (conn != null)
            res.OnStarting(() => { if (!conn.Uploading) conn.Expected = res.ContentLength ?? -1; return Task.CompletedTask; });
        bool failed = false;
        try
        {
            await HandleAsync(ctx, info, conn, address, path);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failed = true;
            traffic.Log(address, $"Server error on {path}: {ex.GetType().Name}: {ex.Message}", LogKind.Bad);
            if (!res.HasStarted)
            {
                try { res.Clear(); await Html(ctx, Listing.Error(500, "Something went wrong on the server."), 500); }
                catch { ctx.Abort(); }
            }
            else ctx.Abort();
        }
        finally
        {
            bool aborted = ctx.RequestAborted.IsCancellationRequested || failed;
            traffic.Finish(address, ctx.Request.Method, path, info, res.StatusCode, aborted, conn, DateTime.Now - started);
            if (conn != null)
            {
                conn.Busy = false;
                conn.Status = info.Upload ? (info.UploadOk ? "Uploaded" : "Upload failed")
                    : !info.File || res.StatusCode is not (200 or 206) ? "Idle"
                    : aborted ? "Interrupted" : "Downloaded";
            }
        }
    }

    async Task HandleAsync(HttpContext ctx, RequestInfo info, Conn? conn, string address, string logPath)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers.XContentTypeOptions = "nosniff";
        res.Headers["Referrer-Policy"] = "same-origin";
        res.Headers.XFrameOptions = "DENY";
        var ip = Traffic.Normalize(ctx.Connection.RemoteIpAddress);
        var guard = traffic.Guard;

        if (!guard.AllowRequest(address, ip, out bool logThrottle))
        {
            if (logThrottle) traffic.Log(address, "Too many requests, slowing this address down", LogKind.Warn);
            guard.Strike(address, ip, 1, "Request flood");
            res.StatusCode = StatusCodes.Status429TooManyRequests;
            res.Headers.RetryAfter = "5";
            return;
        }

        bool post = HttpMethods.IsPost(req.Method);
        if (!post && !HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method))
        {
            res.StatusCode = StatusCodes.Status405MethodNotAllowed;
            res.Headers.Allow = "GET, HEAD, POST";
            return;
        }

        var s = snap;
        if (!TryParsePath(req.Path.Value, out var segs, out bool slash))
        {
            guard.Strike(address, ip, 3, "Path tricks (" + Traffic.Clean(req.Path.Value ?? "", 80) + ")");
            await Html(ctx, Listing.Error(400, "That address isn't valid."), 400);
            return;
        }

        // Forms posted from another site (a signed-in visitor's browser would send the password along) are refused.
        if (post && !SameOrigin(req))
        {
            traffic.Log(address, "Refused an upload posted from another website", LogKind.Bad);
            await Html(ctx, Listing.Error(403, "Uploads must come from this site's own upload form."), 403);
            return;
        }

        var login = await auth.CheckAsync(req, address, neverBan: Guard.IsPrivate(ip), ctx.RequestAborted);
        if (login.Outcome == LoginOutcome.Busy)
        {
            res.Headers.RetryAfter = "5";
            await Html(ctx, Listing.Error(503, "Too many sign-ins at the same moment. Try again in a few seconds."), 503);
            return;
        }
        if (login.Counted)
        {
            var tried = login.TriedName.Length > 0 ? $" for \"{(login.TriedName.Length > 40 ? login.TriedName[..40] : login.TriedName)}\"" : "";
            traffic.Log(address, $"Wrong user name or password{tried}", LogKind.Warn);
        }
        if (login.Delay > TimeSpan.Zero)
            try { await Task.Delay(login.Delay, ctx.RequestAborted); } catch { return; }
        string? user = login.User;
        var page = new PageContext
        {
            User = user,
            HasAccounts = auth.HasAccounts,
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
        };
        var here = segs.Length == 0 ? "/" : Url(segs) + (slash ? "/" : "");
        page.LoginUrl = "/?login=" + Uri.EscapeDataString(here);

        // ---- root: list + sign-in ----
        if (segs.Length == 0)
        {
            if (post) { await MethodNotAllowed(ctx); return; }
            if (req.Query.ContainsKey("login"))
            {
                if (user != null)
                {
                    traffic.Log(address, $"Signed in as {user}", LogKind.Info);
                    Redirect(ctx, SafeReturn(req.Query["login"].ToString()));
                }
                else await Challenge(ctx);
                return;
            }
            info.Listing = true;
            if (conn != null) conn.Status = "Browsing";
            var visible = s.Order.Where(i => i.Download == Access.Anyone || user != null).ToList();
            await Html(ctx, Listing.Root(visible, req.Query, page, hiddenCount: s.Order.Count - visible.Count), 200, page.Nonce);
            return;
        }

        if (!s.ByName.TryGetValue(segs[0], out var item))
        {
            if (segs.Length == 1 && !post && segs[0].Equals("favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = "image/png";
                res.Headers.CacheControl = "max-age=86400";
                res.ContentLength = favicon.Length;
                if (!HttpMethods.IsHead(req.Method)) await res.Body.WriteAsync(favicon);
                return;
            }
            if (Guard.IsTrap(segs)) guard.Strike(address, ip, 5, "Scanner probe " + Traffic.Clean(logPath, 80));
            else guard.Strike(address, ip, 1, "Too many not-found requests");
            await NotFound(ctx);
            return;
        }

        // ---- permissions for this item ----
        bool canDownload = item.Download == Access.Anyone || user != null;
        page.Upload = !item.IsFolder || item.Upload == UploadAccess.Off ? UploadState.None
            : item.Upload == UploadAccess.Anyone || user != null ? UploadState.Allowed
            : UploadState.NeedLogin;
        page.ShowFiles = canDownload;
        if (!canDownload && page.Upload != UploadState.Allowed)
        {
            await Challenge(ctx);
            return;
        }

        string root;
        try { root = Path.GetFullPath(item.Path); }
        catch { await NotFound(ctx); return; }

        if (!item.IsFolder)
        {
            if (post) { await MethodNotAllowed(ctx); return; }
            if (segs.Length > 1 || !File.Exists(root)) { await NotFound(ctx); return; }
            if (slash) { Redirect(ctx, Url(segs) + KeptQuery(req)); return; }
            await SendFile(ctx, root, info, conn, address, logPath);
            return;
        }

        if (!Directory.Exists(root)) { await NotFound(ctx); return; }
        var full = Resolve(root, segs.AsSpan(1));
        if (full == null)
        {
            guard.Strike(address, ip, 1, "Too many not-found requests");
            await NotFound(ctx);
            return;
        }

        if (Directory.Exists(full))
        {
            if (post)
            {
                if (page.Upload == UploadState.NeedLogin) { await Challenge(ctx); return; }
                if (page.Upload != UploadState.Allowed)
                {
                    await Html(ctx, Listing.Error(403, "Uploads aren't allowed in this folder."), 403);
                    return;
                }
                info.Upload = true;
                await ReceiveUpload(ctx, full, segs, info, conn, address, page);
                return;
            }
            if (!slash) { Redirect(ctx, Url(segs) + "/" + KeptQuery(req)); return; }
            if (req.Query.ContainsKey("zip"))
            {
                if (!canDownload) { await Challenge(ctx); return; }
                await SendZip(ctx, full, root, segs, info, conn, address);
                return;
            }
            info.Listing = true;
            if (conn != null) conn.Status = "Browsing";
            string html;
            try { html = Listing.Folder(full, root, segs, req.Query, page); }
            catch (UnauthorizedAccessException) { await Html(ctx, Listing.Error(403, "This folder can't be opened."), 403); return; }
            catch (IOException) { await NotFound(ctx); return; }
            await Html(ctx, html, 200, page.Nonce);
            return;
        }

        if (File.Exists(full))
        {
            if (post) { await MethodNotAllowed(ctx); return; }
            if (!canDownload) { await Challenge(ctx); return; }
            if (slash) { Redirect(ctx, Url(segs) + KeptQuery(req)); return; }
            await SendFile(ctx, full, info, conn, address, logPath);
            return;
        }

        await NotFound(ctx);
    }

    // ---- sign-in -------------------------------------------------------------------------------

    /// <summary>401 + Basic challenge (the browser shows its sign-in box). Wrong guesses were already counted in HandleAsync.</summary>
    async Task Challenge(HttpContext ctx)
    {
        ctx.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Authenticator.Realm}\", charset=\"UTF-8\"";
        await Html(ctx, Listing.Error(401, auth.HasAccounts
            ? "You need an account to open this. Ask the person who shared it."
            : "This is private."), 401);
    }

    /// <summary>Only the sort options survive a redirect, re-encoded.</summary>
    static string KeptQuery(HttpRequest req)
    {
        var parts = new List<string>();
        var sort = req.Query["s"].ToString();
        if (sort is "n" or "s" or "d") parts.Add("s=" + sort);
        if (req.Query.ContainsKey("r")) parts.Add("r=1");
        if (req.Query.ContainsKey("dl")) parts.Add("dl=1");
        if (req.Query.ContainsKey("zip")) parts.Add("zip");
        var q = req.Query["q"].ToString();
        if (q.Length is > 0 and <= 100) parts.Add("q=" + Uri.EscapeDataString(q));
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    /// <summary>
    /// A browser that sends Origin must match our own host. Old browsers send none, which is allowed:
    /// they also don't do the background cross-site posts this protects against.
    /// </summary>
    static bool SameOrigin(HttpRequest req)
    {
        var origin = req.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;   // includes "null"
        return string.Equals(o.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only local paths: "/x/y/", never "//host" or "/\host".</summary>
    static string SafeReturn(string to) =>
        to.StartsWith('/') && !to.StartsWith("//") && !to.Contains('\\') && !to.Any(char.IsControl) ? to : "/";

    // ---- upload --------------------------------------------------------------------------------

    async Task ReceiveUpload(HttpContext ctx, string folder, string[] segs, RequestInfo info, Conn? conn,
        string address, PageContext page)
    {
        var req = ctx.Request;
        var folderUrl = Url(segs) + "/";
        if (!MediaTypeHeaderValue.TryParse(req.ContentType, out var type) ||
            !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
            HeaderUtilities.RemoveQuotes(type.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            await Html(ctx, Listing.Error(400, "Use the upload form on the folder page."), 400);
            return;
        }

        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = null;   // no size cap on uploads; the free-space check guards the disk
        conn?.BeginUpload(req.ContentLength ?? -1);

        var results = new List<UploadResult>();
        bool stopped = false;
        try
        {
            var reader = new MultipartReader(boundary, req.Body) { HeadersLengthLimit = 16 * 1024 };
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ctx.RequestAborted)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)) continue;
                string raw = cd.FileNameStar.HasValue ? cd.FileNameStar.Value ?? ""
                    : HeaderUtilities.RemoveQuotes(cd.FileName).Value ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;   // a text field or an empty file box

                if (results.Count >= 1000)
                {
                    results.Add(new UploadResult(raw, null, 0, "Too many files in one upload (1000 at most)", 400));
                    break;
                }
                var name = SafeUploadName(raw);
                if (name == null)
                {
                    results.Add(new UploadResult(raw, null, 0, "This file name isn't allowed", 400));
                    traffic.Log(address, $"Upload refused: bad name \"{raw}\"", LogKind.Warn);
                    continue;
                }
                if (conn != null) conn.File = folderUrl + name;
                var result = await SaveOne(section.Body, folder, name, ctx.RequestAborted);
                results.Add(result);
                if (result.Error == null)
                {
                    traffic.Uploaded(address);
                    var renamed = result.SavedAs != name ? $", saved as \"{result.SavedAs}\"" : "";
                    traffic.Log(address, $"Uploaded {folderUrl}{name} ({Fmt.Size(result.Bytes)}{renamed})", LogKind.Done);
                }
                else
                {
                    traffic.Log(address, $"Upload failed: {folderUrl}{name}: {result.Error}", LogKind.Warn);
                    if (result.Status >= 500) { stopped = result.Status == 507; break; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
            // SaveOne already removed the partial file
            if (ctx.RequestAborted.IsCancellationRequested)
            {
                traffic.Log(address, $"Upload interrupted in {folderUrl}", LogKind.Warn);
                return;   // the visitor is gone, nobody reads the reply
            }
            // still connected: the form data itself was broken or incomplete
            results.Add(new UploadResult("(upload)", null, 0, "The upload arrived incomplete or broken, nothing was saved from it", 400));
            traffic.Log(address, $"Upload to {folderUrl} arrived broken ({Traffic.Clean(ex.Message, 100)})", LogKind.Warn);
        }

        info.UploadOk = results.Count > 0 && results.All(r => r.Error == null);
        // 200 only when every file was saved; otherwise the worst problem decides (the upload script reads this).
        int status = results.Count == 0 ? 400 : info.UploadOk ? 200 : results.Max(r => r.Status == 0 ? 400 : r.Status);
        await Html(ctx, Listing.UploadDone(segs, results, page, stopped), status);
    }

    async Task<UploadResult> SaveOne(Stream body, string folder, string name, CancellationToken ct)
    {
        if (FreeSpace(folder) is long free && free < MinFreeBytes)
            return new UploadResult(name, null, 0, "The disk is full", 507);

        // Write to a hidden temp file in the same folder (never listed or served), rename when complete.
        var temp = Path.Join(folder, $".{Guid.NewGuid():N}.uploading");
        long written = 0;
        try
        {
            File.Create(temp).Dispose();
            File.SetAttributes(temp, FileAttributes.Hidden);
            await using (var fs = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
                try
                {
                    long nextCheck = 8L << 20;
                    while (true)
                    {
                        int n;
                        try { n = await body.ReadAsync(buffer, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { throw new VisitorBodyException(ex); }
                        if (n == 0) break;
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                        written += n;
                        if (written >= nextCheck)
                        {
                            nextCheck += 8L << 20;   // several uploads at once share the reserve, so look often
                            if (FreeSpace(folder) is long left && left < MinFreeBytes) throw new DiskFullException();
                        }
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            var final = MoveToFreeName(temp, folder, name);
            try { File.SetAttributes(final, FileAttributes.Normal); } catch { }
            return new UploadResult(name, Path.GetFileName(final), written, null);
        }
        catch (DiskFullException)
        {
            TryDelete(temp);
            return new UploadResult(name, null, written, "The disk is full", 507);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new UploadResult(name, null, written, "Windows doesn't allow writing to this folder", 500);
        }
        catch (VisitorBodyException ex)
        {
            TryDelete(temp);
            throw ex.InnerException!;   // the visitor's side: ReceiveUpload decides (gone, or broken form)
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (IOException ex)
        {
            TryDelete(temp);
            return new UploadResult(name, null, written, "The server couldn't save the file: " + Traffic.Clean(ex.Message, 120), 500);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch { }
    }

    /// <summary>Never overwrites: "a.zip" taken -> "a (2).zip", "a (3).zip"...</summary>
    static string MoveToFreeName(string temp, string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = Path.Join(folder, i == 1 ? name : $"{stem} ({i}){ext}");
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            try
            {
                File.Move(temp, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new IOException("No free file name");
    }

    /// <summary>Just the file name: old IE sends the whole "C:\Users\...\file.txt" path.</summary>
    internal static string? SafeUploadName(string raw)
    {
        var name = raw.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        // also drop invisible direction marks ("photo\u202Egpj.exe" would show as "photoexe.jpg")
        name = new string(name.Where(ch => !BadChars.Contains(ch) && ch is not (>= '\u202A' and <= '\u202E') and not (>= '\u2066' and <= '\u2069')
                                           and not '\u200E' and not '\u200F' and not '\uFEFF').ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.StartsWith('.') && name.TrimStart('.').Length == 0) return null;
        if (name.Length > 200)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(200 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        if (DeviceNames.Contains(Path.GetFileNameWithoutExtension(name))) name = "_" + name;
        return name;
    }

    static long? FreeSpace(string folder) =>
        Native.GetDiskFreeSpaceEx(folder, out var free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : null;

    // ---- path safety ---------------------------------------------------------------------------

    /// <summary>Splits the (already %-decoded) request path. Rejects anything that isn't a plain file name segment.</summary>
    internal static bool TryParsePath(string? path, out string[] segs, out bool trailingSlash)
    {
        segs = [];
        trailingSlash = true;
        if (string.IsNullOrEmpty(path) || path == "/") return true;
        if (path[0] != '/') return false;
        trailingSlash = path[^1] == '/';
        var body = path.Substring(1, path.Length - 1 - (trailingSlash ? 1 : 0));
        var parts = body.Split('/');
        foreach (var p in parts)
            if (!ValidSegment(p)) return false;
        segs = parts;
        return true;
    }

    // No "..", no backslash, no drive colon / alternate data streams, no trailing dot or space tricks.
    static bool ValidSegment(string s) =>
        s.Length > 0 && s.IndexOfAny(BadChars) < 0 && s[^1] != '.' && s[^1] != ' ';

    /// <summary>Walks root + segments. Every step must exist, be visible, and links must not lead outside root.</summary>
    internal static string? Resolve(string root, ReadOnlySpan<string> rel)
    {
        string cur = root;
        foreach (var seg in rel)
        {
            cur = Path.Join(cur, seg);
            FileAttributes a;
            try { a = File.GetAttributes(cur); }
            catch { return null; }
            if (!Visible(a)) return null;
            if ((a & FileAttributes.ReparsePoint) != 0 && !LinkStaysInside(cur, a, root)) return null;
        }
        string full;
        try { full = Path.GetFullPath(cur); } catch { return null; }
        return IsUnder(full, root) ? full : null;
    }

    /// <summary>Hidden and system entries (desktop.ini, $RECYCLE.BIN, ...) are never listed or served.</summary>
    internal static bool Visible(FileAttributes a) => (a & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    internal static bool LinkStaysInside(string path, FileAttributes a, string root)
    {
        try
        {
            FileSystemInfo fsi = (a & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            var target = fsi.ResolveLinkTarget(returnFinalTarget: true);
            // null = a reparse point that isn't a link (OneDrive placeholder, dedup...) -> it's the file itself.
            return target == null || IsUnder(Path.GetFullPath(target.FullName), root);
        }
        catch { return false; }
    }

    internal static bool IsUnder(string path, string root)
    {
        var bare = root.TrimEnd('\\');
        if (path.Equals(bare, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(bare + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ---- responses -----------------------------------------------------------------------------

    async Task SendFile(HttpContext ctx, string path, RequestInfo info, Conn? conn, string address, string logPath)
    {
        var fi = new FileInfo(path);
        info.File = true;
        info.Length = fi.Length;
        var range = ctx.Request.GetTypedHeaders().Range;
        if (range?.Ranges.Count == 1 && range.Unit == "bytes")
        {
            var r = range.Ranges.First();
            info.RangeStart = r.From ?? (r.To is long suffix ? Math.Max(0, fi.Length - suffix) : 0);
        }
        if (conn != null) conn.Status = "Downloading";
        if (!HttpMethods.IsHead(ctx.Request.Method)) traffic.FileStarting(address, logPath, info);
        if (!Types.TryGetContentType(fi.Name, out var type)) type = "application/octet-stream";
        // Files that a browser could run as a page (html, svg, xml...) are shown sandboxed: no scripts.
        if (type.Contains("html") || type.Contains("xml") || type.Contains("svg") || type.Contains("javascript"))
            ctx.Response.Headers.ContentSecurityPolicy = "sandbox";
        bool download = ctx.Request.Query.ContainsKey("dl");
        var etag = new EntityTagHeaderValue($"\"{fi.Length:x}-{fi.LastWriteTimeUtc.Ticks:x}\"");
        await Results.File(fi.FullName, type, download ? fi.Name : null, fi.LastWriteTimeUtc, etag,
            enableRangeProcessing: true).ExecuteAsync(ctx);
    }

    /// <summary>The whole folder as one ZIP, streamed with an exact Content-Length.</summary>
    async Task SendZip(HttpContext ctx, string full, string root, string[] segs, RequestInfo info, Conn? conn, string address)
    {
        if (zipsPerAddress.AddOrUpdate(address, 1, (_, n) => n + 1) > 2)
        {
            ReleaseZip(address, slot: false);
            ctx.Response.Headers.RetryAfter = "30";
            await Html(ctx, Listing.Error(429, "You already have two ZIP downloads running. Let one finish first."), 429);
            return;
        }
        if (!zipSlots.Wait(0))
        {
            ReleaseZip(address, slot: false);
            ctx.Response.Headers.RetryAfter = "60";
            await Html(ctx, Listing.Error(503, "Several ZIP downloads are already running. Try again in a few minutes."), 503);
            return;
        }
        try { await SendZipCore(ctx, full, root, segs, info, conn, address); }
        finally { ReleaseZip(address, slot: true); }
    }

    void ReleaseZip(string address, bool slot)
    {
        if (slot) zipSlots.Release();
        if (zipsPerAddress.AddOrUpdate(address, 0, (_, n) => n - 1) <= 0) zipsPerAddress.TryRemove(address, out _);
    }

    async Task SendZipCore(HttpContext ctx, string full, string root, string[] segs, RequestInfo info, Conn? conn, string address)
    {
        ZipPlan? plan;
        try { plan = await Task.Run(() => ZipPlan.Build(full, root, ctx.RequestAborted)); }
        catch (OperationCanceledException) { return; }
        if (plan == null)
        {
            await Html(ctx, Listing.Error(413, $"This folder holds more than {ZipPlan.MaxEntries:N0} files and folders, too many for one ZIP. Open it and download the parts you need."), 413);
            return;
        }
        var name = segs[^1] + ".zip";
        info.File = true;
        info.Length = plan.Length;
        info.LogName = Url(segs) + "/ as ZIP";
        if (conn != null) { conn.Status = "Downloading"; conn.File = info.LogName; }
        if (!HttpMethods.IsHead(ctx.Request.Method)) traffic.FileStarting(address, info.LogName, info);

        var res = ctx.Response;
        res.ContentType = "application/zip";
        res.ContentLength = plan.Length;
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(name);
        res.Headers.ContentDisposition = disposition.ToString();
        res.Headers.CacheControl = "no-store";
        res.Headers.AcceptRanges = "none";
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        try
        {
            await plan.WriteAsync(res.Body, ctx.RequestAborted);
        }
        catch (ZipSourceException ex)
        {
            // Better a download that visibly fails than a ZIP holding a damaged file with a valid checksum.
            traffic.Log(address, $"ZIP of {info.LogName} stopped: {ex.Message}", LogKind.Bad);
            ctx.Abort();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }   // visitor went away
    }

    static Task NotFound(HttpContext ctx) =>
        Html(ctx, Listing.Error(404, "There's nothing shared at this address."), 404);

    static Task MethodNotAllowed(HttpContext ctx)
    {
        ctx.Response.Headers.Allow = "GET, HEAD";
        return Html(ctx, Listing.Error(405, "You can't upload here."), 405);
    }

    static async Task Html(HttpContext ctx, string html, int status = 200, string? nonce = null)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        var res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = "text/html; charset=utf-8";
        res.Headers.CacheControl = "no-cache";
        // No script runs except the page's own upload helper (nonce), which may only talk back to this server.
        res.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'" +
            (string.IsNullOrEmpty(nonce) ? "" : $"; script-src 'nonce-{nonce}'; connect-src 'self'");
        res.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(ctx.Request.Method)) await res.Body.WriteAsync(bytes);
    }

    static void Redirect(HttpContext ctx, string location)
    {
        ctx.Response.StatusCode = StatusCodes.Status302Found;
        ctx.Response.Headers.Location = location;
    }

    internal static string Url(IEnumerable<string> segs) => "/" + string.Join("/", segs.Select(Uri.EscapeDataString));
}
