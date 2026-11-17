using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModernHFS;

// 1) exe icon
if (args.Length > 0 && args[0] == "ico")
{
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var pngs = sizes.Select(s => { using var b = AppIcon.Draw(s, true); using var ms = new MemoryStream(); b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }).ToList();
    using var f = File.Create(args[1]); using var w = new BinaryWriter(f);
    w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
    int off = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++) { w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(off); off += pngs[i].Length; }
    foreach (var p in pngs) w.Write(p);
    Console.WriteLine("ico written"); return;
}

// 2) server tests
var root = Path.Combine(Path.GetTempPath(), "mhfs_test_" + Guid.NewGuid().ToString("N")[..6]);
var share = Path.Combine(root, "share"); var outside = Path.Combine(root, "outside");
Directory.CreateDirectory(Path.Combine(share, "sub")); Directory.CreateDirectory(outside);
File.WriteAllText(Path.Combine(share, "a.txt"), "hello world");
File.WriteAllText(Path.Combine(share, "sp ace & ünï%.txt"), "unicode");
var big = new byte[1 << 20]; new Random(1).NextBytes(big); File.WriteAllBytes(Path.Combine(share, "sub", "b.bin"), big);
File.WriteAllText(Path.Combine(share, "hidden.txt"), "secret-hidden"); File.SetAttributes(Path.Combine(share, "hidden.txt"), FileAttributes.Hidden);
File.WriteAllText(Path.Combine(share, "page.html"), "<script>alert(1)</script>");
File.WriteAllText(Path.Combine(outside, "secret.txt"), "TOP SECRET");
File.WriteAllText(Path.Combine(root, "single.txt"), "single file");
using (var fs = File.Create(Path.Combine(share, "huge.bin"))) fs.SetLength(64L << 20);
void Junction(string link, string target) => Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!.WaitForExit();
Junction(Path.Combine(share, "link_out"), outside);
Junction(Path.Combine(share, "link_in"), Path.Combine(share, "sub"));

var traffic = new Traffic();
var auth = new Authenticator();
var srv = new FileServer(AppIcon.Png(32), traffic, auth);
srv.SetItems([new SharedItem { Name = "Share", Path = share, IsFolder = true }, new SharedItem { Name = "single.txt", Path = Path.Combine(root, "single.txt") }]);
int port = 18080;
await srv.StartAsync(port, IPAddress.Loopback);

int fails = 0;
(int code, Dictionary<string, string> h, byte[] body) Raw(string method, string target, string extra = "")
{
    using var c = new TcpClient("127.0.0.1", port); var s = c.GetStream();
    var req = $"{method} {target} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n{extra}\r\n";
    s.Write(Encoding.UTF8.GetBytes(req));
    var ms = new MemoryStream(); s.CopyTo(ms); var all = ms.ToArray();
    int split = IndexOf(all, "\r\n\r\n"u8.ToArray());
    var head = Encoding.UTF8.GetString(all, 0, split).Split("\r\n");
    int code = int.Parse(head[0].Split(' ')[1]);
    var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var l in head.Skip(1)) { int i = l.IndexOf(':'); h[l[..i]] = l[(i + 1)..].Trim(); }
    return (code, h, all[(split + 4)..]);
}
static int IndexOf(byte[] a, byte[] p) { for (int i = 0; i <= a.Length - p.Length; i++) if (a.AsSpan(i, p.Length).SequenceEqual(p)) return i; return -1; }
void Check(string name, bool ok, string info = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : info)}"); if (!ok) fails++; }
string B(byte[] b) => Encoding.UTF8.GetString(b);

var r = Raw("GET", "/"); Check("root lists items", r.code == 200 && B(r.body).Contains("Share/") && B(r.body).Contains("single.txt"), r.code.ToString());
r = Raw("GET", "/Share"); Check("folder w/o slash redirects", r.code == 302 && r.h["Location"] == "/Share/", r.code + " " + r.h.GetValueOrDefault("Location"));
r = Raw("GET", "/Share/"); var body = B(r.body);
Check("folder listing", r.code == 200 && body.Contains("a.txt") && body.Contains("sub/") && body.Contains("link_in/"), r.code.ToString());
Check("listing hides hidden + outward junction", !body.Contains("hidden.txt") && !body.Contains("link_out"));
Check("listing escapes names", body.Contains("sp%20ace%20%26%20%C3%BCn%C3%AF%25.txt") && WebUtility.HtmlDecode(body).Contains("sp ace & ünï%.txt") && body.Contains("sp ace &amp; "), body.Substring(body.IndexOf("sp%20"), 160));
r = Raw("GET", "/Share/a.txt"); Check("download", r.code == 200 && B(r.body) == "hello world" && r.h["Accept-Ranges"] == "bytes", r.code.ToString());
r = Raw("GET", "/Share/a.txt", "Range: bytes=6-\r\n"); Check("resume (range)", r.code == 206 && B(r.body) == "world", r.code + " " + B(r.body));
r = Raw("GET", "/Share/sub/b.bin", "Range: bytes=1000-1099\r\n"); Check("range on 1MB", r.code == 206 && r.body.Length == 100 && r.body.AsSpan().SequenceEqual(big.AsSpan(1000, 100)), r.code.ToString());
r = Raw("GET", "/Share/sub/b.bin"); Check("full 1MB intact", r.code == 200 && r.body.AsSpan().SequenceEqual(big), r.code.ToString());
r = Raw("HEAD", "/Share/a.txt"); Check("HEAD", r.code == 200 && r.body.Length == 0 && r.h["Content-Length"] == "11", r.code.ToString());
r = Raw("GET", "/Share/a.txt?dl=1"); Check("?dl=1 forces download", r.h.GetValueOrDefault("Content-Disposition", "").StartsWith("attachment"), r.h.GetValueOrDefault("Content-Disposition", "none"));
r = Raw("GET", "/Share/page.html"); Check("html served sandboxed", r.h.GetValueOrDefault("Content-Security-Policy") == "sandbox");
r = Raw("GET", "/Share/sp%20ace%20%26%20%C3%BCn%C3%AF%25.txt"); Check("unicode/%/& name", r.code == 200 && B(r.body) == "unicode", r.code.ToString());
r = Raw("GET", "/Share/SUB/B.BIN", "Range: bytes=0-0\r\n"); Check("case-insensitive", r.code == 206, r.code.ToString());
r = Raw("GET", "/single.txt"); Check("single file item", r.code == 200 && B(r.body) == "single file", r.code.ToString());
r = Raw("GET", "/favicon.ico"); Check("favicon", r.code == 200 && r.h["Content-Type"] == "image/png");
r = Raw("GET", "/Share/?s=s&r=1"); Check("sorted listing", r.code == 200);
r = Raw("POST", "/Share/"); Check("POST to folder w/o uploads -> 403", r.code == 403, r.code.ToString()); r = Raw("PUT", "/Share/a.txt"); Check("PUT -> 405", r.code == 405, r.code.ToString());
r = Raw("GET", "/nope"); Check("unknown -> 404", r.code == 404, r.code.ToString());
r = Raw("GET", "/Share/link_in/b.bin", "Range: bytes=0-0\r\n"); Check("inward junction works", r.code == 206, r.code.ToString());

string[] attacks = [
  "/Share/hidden.txt", "/Share/link_out/secret.txt", "/Share/link_out/",
  "/Share/../outside/secret.txt", "/Share/%2e%2e/outside/secret.txt", "/Share/%2E%2E%2Foutside%2Fsecret.txt",
  "/Share/..%5coutside%5csecret.txt", "/Share/..\\outside\\secret.txt", "/Share/a.txt::$DATA", "/Share/a.txt%3a%3a$DATA",
  "/Share/a.txt.", "/Share/a.txt%20", "/Share/C:/Windows/win.ini", "/Share/C%3A%5CWindows%5Cwin.ini",
  "/Share//Windows/win.ini", "//Windows/win.ini", "/Share/%00", "/Share/sub/../../outside/secret.txt",
  "/Share/HIDDEN~1.TXT", "/Share/con", "/Share/nul", "/single.txt/../outside/secret.txt",
  "/Share/%252e%252e/outside/secret.txt", "/Share/..%c0%af../outside/secret.txt", "/%5c%5clocalhost%5cc$%5cwindows%5cwin.ini",
];
foreach (var a in attacks)
{
    try { r = Raw("GET", a); var t = B(r.body); Check($"attack {a} -> {r.code}", r.code is 400 or 404 && !t.Contains("TOP SECRET") && !t.Contains("secret-hidden") && !t.Contains("[fonts]"), t.Length > 80 ? t[..80] : t); }
    catch (Exception ex) { Check($"attack {a} (conn error ok)", true, ex.Message); }
}


// ---- step 2: traffic -------------------------------------------------------------------------
List<LogEntry> Logs() { var l = new List<LogEntry>(); while (traffic.TryDequeue(out var e)) l.Add(e); return l; }
Logs();
long out0 = traffic.TotalOut;
r = Raw("GET", "/Share/a.txt");
Thread.Sleep(200);
var logs = Logs();
Check("log: fully downloaded", logs.Any(l => l.Text.StartsWith("Fully downloaded /Share/a.txt") && l.Kind == LogKind.Done && l.Address == "127.0.0.1"), string.Join(" | ", logs.Select(l => l.Text)));
Check("stats: file count", traffic.ReadStats(s => s.Files.TryGetValue("/Share/a.txt", out var f) ? f.Downloads : 0) >= 1);
Check("bytes out counted", traffic.TotalOut - out0 > 11, (traffic.TotalOut - out0).ToString());
Raw("GET", "/Share/"); Raw("GET", "/nope"); Thread.Sleep(100); logs = Logs();
Check("log: browsing + not found", logs.Any(l => l.Text == "Browsing /Share/") && logs.Any(l => l.Text == "Not found /nope" && l.Kind == LogKind.Warn), string.Join(" | ", logs.Select(l => l.Text)));
Raw("GET", "/Share/a.txt", "Range: bytes=6-\r\n"); Thread.Sleep(100); logs = Logs();
Check("partial range is not a full download", !logs.Any(l => l.Text.StartsWith("Fully")), string.Join(" | ", logs.Select(l => l.Text)));

// live connection: slow reader on a 64 MB file -> progress, status, then kick
{
    using var c = new TcpClient("127.0.0.1", port) { ReceiveBufferSize = 8192 };
    var st = c.GetStream();
    st.Write(Encoding.ASCII.GetBytes("GET /Share/huge.bin HTTP/1.1\r\nHost: x\r\n\r\n"));
    var buf = new byte[65536]; long got = 0;
    while (got < 4 << 20) got += st.Read(buf);
    Thread.Sleep(300);
    var conn = traffic.Live.FirstOrDefault(x => x.File == "/Share/huge.bin");
    Check("live conn visible", conn != null, string.Join(",", traffic.Live.Select(x => x.File + ":" + x.Status)));
    if (conn != null)
    {
        Check("status Downloading + progress", conn.Status == "Downloading" && conn.Busy && conn.Progress > 0 && conn.Progress < 1, $"{conn.Status} {conn.Progress:0.000} exp={conn.Expected}");
        conn.Kick();
        bool closed = false;
        st.ReadTimeout = 3000;
        try { for (int k = 0; k < 10000; k++) { if (st.Read(buf) == 0) { closed = true; break; } } } catch { closed = true; }
        Check("kick closes the connection", closed);
        Thread.Sleep(300);
        logs = Logs();
        Check("log: downloading + interrupted", logs.Any(l => l.Text.StartsWith("Downloading /Share/huge.bin")) && logs.Any(l => l.Text.StartsWith("Interrupted /Share/huge.bin") && l.Kind == LogKind.Warn), string.Join(" | ", logs.Select(l => l.Text)));
        Check("conn removed after close", !traffic.Live.Any(x => x.Id == conn.Id));
    }
}
// resume log
{
    using var c = new TcpClient("127.0.0.1", port); var st = c.GetStream();
    st.Write(Encoding.ASCII.GetBytes("GET /Share/huge.bin HTTP/1.1\r\nHost: x\r\nRange: bytes=33554432-\r\nConnection: close\r\n\r\n"));
    var buf = new byte[65536]; long got = 0; int n; while ((n = st.Read(buf)) > 0) got += n;
    Thread.Sleep(200); logs = Logs();
    Check("log: resuming + sent part", logs.Any(l => l.Text.StartsWith("Resuming /Share/huge.bin from 32 MB")) && logs.Any(l => l.Text.StartsWith("Sent 32 MB of /Share/huge.bin")), string.Join(" | ", logs.Select(l => l.Text)));
}
// ban
traffic.SetBans([new BanEntry { Pattern = "127.0.0.*" }]);
bool refused;
try { var rb = Raw("GET", "/"); refused = false; } catch { refused = true; }
Thread.Sleep(100); logs = Logs();
Check("banned address is dropped", refused && traffic.BlockedCount("127.0.0.*") == 1 && logs.Any(l => l.Text.StartsWith("Blocked")), $"{refused} {traffic.BlockedCount("127.0.0.*")}");
traffic.SetBans([]);
Check("unban works", Raw("GET", "/").code == 200);
Check("ban rule parse", BanRule.TryParse("203.0.113.*", out _) && BanRule.TryParse("::1", out _) && !BanRule.TryParse("hello", out _) && !BanRule.TryParse("*", out _));

// ---- step 3: accounts, permissions, uploads ---------------------------------------------------
var dropDir = Path.Combine(root, "drop"); Directory.CreateDirectory(dropDir); File.WriteAllText(Path.Combine(dropDir, "dropsecret.txt"), "x");
var upAccDir = Path.Combine(root, "upacc"); Directory.CreateDirectory(upAccDir);
srv.SetItems([
    new SharedItem { Name = "Share", Path = share, IsFolder = true, Upload = UploadAccess.Anyone },
    new SharedItem { Name = "Private", Path = Path.Combine(share, "sub"), IsFolder = true, Download = Access.Accounts },
    new SharedItem { Name = "Drop", Path = dropDir, IsFolder = true, Download = Access.Accounts, Upload = UploadAccess.Anyone },
    new SharedItem { Name = "UpAcc", Path = upAccDir, IsFolder = true, Upload = UploadAccess.Accounts },
    new SharedItem { Name = "single.txt", Path = Path.Combine(root, "single.txt") },
]);
auth.SetAccounts([Account.Create("bob", "secret1")]);
string Basic(string u, string pw) => "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(u + ":" + pw)) + "\r\n";
var bob = Basic("bob", "secret1");
Check("password verify", Account.Create("x", "pw").Verify("pw") && !Account.Create("x", "pw").Verify("pW"));

r = Raw("GET", "/"); body = B(r.body);
Check("anon root hides private, offers sign in", r.code == 200 && body.Contains("Share/") && !body.Contains("Private/") && body.Contains("Sign in"));
r = Raw("GET", "/Private/");
Check("anon private -> 401 + Basic", r.code == 401 && r.h.GetValueOrDefault("WWW-Authenticate", "").StartsWith("Basic realm="), r.code.ToString());
r = Raw("GET", "/Private/b.bin", "Range: bytes=0-0\r\n");
Check("anon private file -> 401", r.code == 401, r.code.ToString());
r = Raw("GET", "/Private/", bob); body = B(r.body);
Check("bob private -> 200", r.code == 200 && body.Contains("b.bin") && body.Contains("class=\"avatar\""), r.code.ToString());
r = Raw("GET", "/", bob); Check("bob root shows private", B(r.body).Contains("Private/"));
Logs();
r = Raw("GET", "/Private/", Basic("bob", "nope")); Thread.Sleep(100); logs = Logs();
Check("wrong password -> 401 + logged", r.code == 401 && logs.Any(l => l.Text == "Wrong user name or password for \"bob\""), string.Join(" | ", logs.Select(l => l.Text)));
r = Raw("GET", "/Share/a.txt", Basic("bob", "nope"));
Check("wrong creds on public file still served", r.code == 200);
r = Raw("GET", "/?login=%2FPrivate%2F", bob);
Check("login redirects back", r.code == 302 && r.h["Location"] == "/Private/", r.code + " " + r.h.GetValueOrDefault("Location"));
r = Raw("GET", "/?login=%2F%2Fevil.example", bob);
Check("login refuses open redirect", r.code == 302 && r.h["Location"] == "/", r.h.GetValueOrDefault("Location") ?? "");
r = Raw("GET", "/?login=%2F"); Check("anon login -> 401", r.code == 401);

(int code, Dictionary<string, string> h, byte[] body) Post(string target, string extra, params (string Name, byte[] Data)[] files)
{
    var bnd = "----mhfs" + Guid.NewGuid().ToString("N");
    var ms = new MemoryStream();
    void W(string t) => ms.Write(Encoding.UTF8.GetBytes(t));
    foreach (var (name, data) in files)
    {
        W($"--{bnd}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{name}\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        ms.Write(data); W("\r\n");
    }
    W($"--{bnd}--\r\n");
    var payload = ms.ToArray();
    using var c = new TcpClient("127.0.0.1", port); var st = c.GetStream();
    st.Write(Encoding.UTF8.GetBytes($"POST {target} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Type: multipart/form-data; boundary={bnd}\r\nContent-Length: {payload.Length}\r\n{extra}\r\n"));
    try { st.Write(payload); } catch { }
    var o = new MemoryStream(); try { st.CopyTo(o); } catch { }
    var all = o.ToArray();
    int split = IndexOf(all, "\r\n\r\n"u8.ToArray());
    var head = Encoding.UTF8.GetString(all, 0, split).Split("\r\n");
    var hh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var l in head.Skip(1)) { int i = l.IndexOf(':'); hh[l[..i]] = l[(i + 1)..].Trim(); }
    return (int.Parse(head[0].Split(' ')[1]), hh, all[(split + 4)..]);
}
byte[] T(string t) => Encoding.UTF8.GetBytes(t);
bool NoTemp(string dir) => !Directory.EnumerateFiles(dir, "*.uploading", SearchOption.AllDirectories).Any();

r = Raw("GET", "/Share/"); Check("upload form shown", B(r.body).Contains("enctype=\"multipart/form-data\""));
Logs();
r = Post("/Share/", "", ("up.txt", T("U1")), ("C:\\Users\\old\\Desktop\\ie.txt", T("IE")));
Check("upload 2 files", r.code == 200 && File.ReadAllText(Path.Combine(share, "up.txt")) == "U1" && File.ReadAllText(Path.Combine(share, "ie.txt")) == "IE", r.code + " " + B(r.body)[..Math.Min(200, r.body.Length)]);
r = Post("/Share/", "", ("up.txt", T("U2")));
Check("no overwrite -> up (2).txt", File.ReadAllText(Path.Combine(share, "up.txt")) == "U1" && File.ReadAllText(Path.Combine(share, "up (2).txt")) == "U2" && B(r.body).Contains("Saved as up (2).txt"));
r = Post("/Share/", "", ("..\\..\\evil.txt", T("E")), ("../../evil2.txt", T("E2")), ("CON.txt", T("C")), ("a:b.txt", T("ab")));
Check("path tricks stay inside", File.Exists(Path.Combine(share, "evil.txt")) && File.Exists(Path.Combine(share, "evil2.txt")) && !File.Exists(Path.Combine(root, "evil.txt")) && !File.Exists(Path.Combine(root, "evil2.txt")) && File.Exists(Path.Combine(share, "_CON.txt")) && File.Exists(Path.Combine(share, "ab.txt")), string.Join(",", Directory.GetFiles(share).Select(Path.GetFileName)));
Check("upload empty name skipped", Post("/Share/", "", ("", T("zzz"))).code == 200);
r = Post("/Share/sub/", "", ("insub.txt", T("S")));
Check("upload into subfolder", r.code == 200 && File.Exists(Path.Combine(share, "sub", "insub.txt")));
Thread.Sleep(100); logs = Logs();
Check("log: uploaded", logs.Any(l => l.Text.StartsWith("Uploaded /Share/up.txt") && l.Kind == LogKind.Done) && logs.Any(l => l.Text.Contains("saved as \"up (2).txt\"")), string.Join(" | ", logs.Select(l => l.Text)));
Check("stats: uploads", traffic.ReadStats(s => s.Uploads) >= 8, traffic.ReadStats(s => s.Uploads).ToString());
Check("no temp files left", NoTemp(root));

r = Post("/single.txt", "", ("x.txt", T("x"))); Check("POST to a file -> 405", r.code == 405, r.code.ToString());
r = Post("/Private/", bob, ("x.txt", T("x"))); Check("upload off -> 403", r.code == 403, r.code.ToString());
r = Post("/Private/", "", ("x.txt", T("x"))); Check("anon upload to private -> 401", r.code == 401, r.code.ToString());
r = Post("/UpAcc/", "", ("x.txt", T("x"))); Check("upload accounts, anon -> 401", r.code == 401 && !File.Exists(Path.Combine(upAccDir, "x.txt")), r.code.ToString());
r = Post("/UpAcc/", bob, ("x.txt", T("x"))); Check("upload accounts, bob -> ok", r.code == 200 && File.Exists(Path.Combine(upAccDir, "x.txt")), r.code.ToString());
r = Raw("GET", "/UpAcc/"); Check("anon sees 'sign in to upload'", B(r.body).Contains("Sign in</a> to upload"));
r = Post("/", "", ("x.txt", T("x"))); Check("POST to root -> 405", r.code == 405);

r = Raw("GET", "/Drop/"); body = B(r.body);
Check("drop box: form, no listing", r.code == 200 && body.Contains("drop box") && body.Contains("multipart/form-data") && !body.Contains("dropsecret"), r.code.ToString());
r = Raw("GET", "/Drop/dropsecret.txt"); Check("drop box: files private", r.code == 401, r.code.ToString());
r = Post("/Drop/", "", ("gift.txt", T("G"))); Check("drop box: anon upload ok", r.code == 200 && File.Exists(Path.Combine(dropDir, "gift.txt")));
r = Raw("GET", "/Drop/", bob); Check("drop box: bob sees files", B(r.body).Contains("dropsecret.txt") && B(r.body).Contains("gift.txt"));

var bigUp = new byte[20 << 20]; new Random(2).NextBytes(bigUp);
r = Post("/Share/", "", ("big.bin", bigUp));
Check("20 MB upload intact", r.code == 200 && File.ReadAllBytes(Path.Combine(share, "big.bin")).AsSpan().SequenceEqual(bigUp), r.code.ToString());

// interrupted upload: promise 10 MB, send 1 MB, hang up
{
    var bnd = "----cut";
    using var c = new TcpClient("127.0.0.1", port); var st = c.GetStream();
    st.Write(T($"POST /Share/ HTTP/1.1\r\nHost: x\r\nContent-Type: multipart/form-data; boundary={bnd}\r\nContent-Length: 10485760\r\n\r\n--{bnd}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"cut.bin\"\r\n\r\n"));
    st.Write(new byte[1 << 20]);
    Thread.Sleep(300);
    c.Client.Close();
}
Thread.Sleep(800); logs = Logs();
Check("interrupted upload cleaned up", !File.Exists(Path.Combine(share, "cut.bin")) && NoTemp(root) && logs.Any(l => l.Text.StartsWith("Upload interrupted")), string.Join(" | ", logs.Select(l => l.Text)));

srv.MinFreeBytes = long.MaxValue;
r = Post("/Share/", "", ("full.txt", T("F")));
Check("disk-full guard", r.code == 507 && !File.Exists(Path.Combine(share, "full.txt")) && NoTemp(root), r.code.ToString());
srv.MinFreeBytes = 1L << 30;

var a2 = new Authenticator(); string? banned = null; a2.AutoBan += ip => banned = ip;
var delays = Enumerable.Range(0, 10).Select(_ => a2.Fail("203.0.113.9")).ToList();
Check("auto-ban after 10 wrong passwords", banned == "203.0.113.9" && delays[0] == TimeSpan.Zero && delays[2] == TimeSpan.FromSeconds(1) && delays[9] == TimeSpan.FromSeconds(5));
banned = null; for (int k = 0; k < 12; k++) a2.Fail("127.0.0.1");
Check("loopback never auto-banned", banned == null);

auth.SetAccounts([]);
r = Raw("GET", "/Private/", bob); Check("removed account locked out", r.code == 401, r.code.ToString());

// ---- hardening -------------------------------------------------------------------------------
auth.SetAccounts([Account.Create("bob", "secret1")]);
r = Raw("GET", "/");
Check("X-Frame-Options + frame-ancestors", r.h.GetValueOrDefault("X-Frame-Options") == "DENY" && r.h.GetValueOrDefault("Content-Security-Policy", "").Contains("frame-ancestors 'none'"));
r = Post("/Share/", "Origin: http://evil.example\r\n", ("csrf.txt", T("C")));
Check("cross-site upload refused", r.code == 403 && !File.Exists(Path.Combine(share, "csrf.txt")), r.code.ToString());
r = Post("/Share/", "Origin: null\r\n", ("csrf2.txt", T("C")));
Check("Origin: null refused", r.code == 403 && !File.Exists(Path.Combine(share, "csrf2.txt")), r.code.ToString());
r = Post("/Share/", "Origin: http://x\r\n", ("sameorigin.txt", T("S")));
Check("same-origin upload ok", r.code == 200 && File.Exists(Path.Combine(share, "sameorigin.txt")), r.code.ToString());
r = Raw("GET", "/Share?s=d&evil=%0d%0aSet-Cookie:x");
Check("redirect keeps only known query", r.code == 302 && r.h["Location"] == "/Share/?s=d" && !r.h.ContainsKey("Set-Cookie"), r.h.GetValueOrDefault("Location") ?? "");
r = Raw("GET", "/Share?x=%E2%80%AE%C3%BC");
Check("non-ASCII query can't break redirect", r.code == 302 && r.h["Location"] == "/Share/", r.code.ToString());
Logs();
Raw("GET", "/Share/%0aFAKE%20LINE"); Raw("GET", "/nope%E2%80%AEtxt.exe"); Thread.Sleep(100); logs = Logs();
Check("log lines can't be forged", logs.Count >= 2 && logs.All(l => !l.Text.Any(char.IsControl) && !l.Text.Contains('\u202E')), string.Join(" | ", logs.Select(l => l.Text)));
r = Post("/Share/", "", ("photo\u202Egpj.exe", T("P")));
Check("upload name: direction marks stripped", File.Exists(Path.Combine(share, "photogpj.exe")), string.Join(",", Directory.GetFiles(share).Select(Path.GetFileName)));
Check("IsPrivate", Guard.IsPrivate(IPAddress.Parse("192.168.18.5")) && Guard.IsPrivate(IPAddress.Parse("10.1.2.3")) && Guard.IsPrivate(IPAddress.Parse("172.20.0.1"))
    && Guard.IsPrivate(IPAddress.Loopback) && !Guard.IsPrivate(IPAddress.Parse("8.8.8.8")) && !Guard.IsPrivate(IPAddress.Parse("172.32.0.1")));
Check("traps", Guard.IsTrap(["wp-login.php"]) && Guard.IsTrap([".env"]) && Guard.IsTrap(["x", "shell.php"]) && !Guard.IsTrap(["Movies"]) && !Guard.IsTrap(["song.flac"]));

var g = traffic.Guard;
g.ExemptPrivate = false;
string? gb = null, gr = null; g.AutoBan += (a, why) => { gb = a; gr = why; };
g.Forget("127.0.0.1");
Raw("GET", "/wp-login.php"); Check("one probe: no ban yet", gb == null);
Raw("GET", "/.env");
Check("two scanner probes -> auto-ban", gb == "127.0.0.1" && gr!.StartsWith("Scanner probe"), gr ?? "none");
gb = null; g.Forget("127.0.0.1");
for (int k = 0; k < 4; k++) Raw("GET", "/Share/..%5c..%5cwin.ini");
Check("path tricks -> auto-ban", gb == "127.0.0.1" && gr!.StartsWith("Path tricks"), gr ?? "none");
gb = null; g.Forget("127.0.0.1");
for (int k = 0; k < 9; k++) Raw("GET", "/Share/missing" + k + ".txt");
Check("9 not-found: no ban", gb == null);
Raw("GET", "/Share/missing-again.txt");
Check("10 not-found -> auto-ban", gb == "127.0.0.1");
gb = null; g.Forget("127.0.0.1");

g.Burst = 20; g.RequestsPerSecond = 5; g.BanAtStrikes = 1000; g.Forget("127.0.0.1");
int throttled = 0; string? retry = null;
for (int k = 0; k < 40; k++) { var rr = Raw("GET", "/"); if (rr.code == 429) { throttled++; retry = rr.h.GetValueOrDefault("Retry-After"); } }
Check("rate limit -> 429", throttled >= 15 && retry == "5", $"{throttled} retry={retry}");
g.Burst = 150; g.RequestsPerSecond = 30; g.Forget("127.0.0.1");

g.MaxConnectionsPerAddress = 5;
var held = new List<TcpClient>();
for (int k = 0; k < 5; k++) { var c = new TcpClient("127.0.0.1", port); held.Add(c); }
Thread.Sleep(300);
bool sixthRefused;
try { var rr = Raw("GET", "/"); sixthRefused = false; } catch { sixthRefused = true; }
Check("connection cap per address", sixthRefused);
foreach (var c in held) c.Dispose();
Thread.Sleep(300);
Check("cap frees up after close", Raw("GET", "/").code == 200);
g.MaxConnectionsPerAddress = 24; g.BanAtStrikes = 10; g.ExemptPrivate = true; g.Forget("127.0.0.1");
for (int k = 0; k < 6; k++) Raw("GET", "/wp-login.php");
Check("LAN/loopback never auto-banned", gb == null);
Logs();

// ---- ZIP, network, host names ---------------------------------------------------------------
srv.SetItems([
    new SharedItem { Name = "Share", Path = share, IsFolder = true },
    new SharedItem { Name = "Private", Path = Path.Combine(share, "sub"), IsFolder = true, Download = Access.Accounts },
]);
Logs();
r = Raw("GET", "/Share/"); Check("folder page offers Download all", B(r.body).Contains("href=\"?zip\""));
r = Raw("GET", "/"); Check("root page has no zip button", !B(r.body).Contains("href=\"?zip\""));
r = Raw("GET", "/Share?zip"); Check("zip redirect keeps ?zip", r.code == 302 && r.h["Location"] == "/Share/?zip", r.h.GetValueOrDefault("Location") ?? "");
r = Raw("HEAD", "/Share/?zip"); long headLen = long.Parse(r.h.GetValueOrDefault("Content-Length", "-1"));
Check("HEAD zip: length, no body", r.code == 200 && headLen > 0 && r.body.Length == 0, r.code.ToString());
r = Raw("GET", "/Share/?zip");
Check("zip: exact Content-Length", r.code == 200 && r.h["Content-Type"] == "application/zip" && long.Parse(r.h["Content-Length"]) == r.body.Length && r.body.Length == headLen,
    $"{r.code} cl={r.h.GetValueOrDefault("Content-Length")} body={r.body.Length}");
Check("zip: attachment name", r.h.GetValueOrDefault("Content-Disposition", "").Contains("Share.zip"));
using (var za = new System.IO.Compression.ZipArchive(new MemoryStream(r.body)))
{
    var names = za.Entries.Select(e => e.FullName).ToList();
    Check("zip: visible files only", names.Contains("a.txt") && names.Contains("sub/b.bin") && names.Contains("sp ace & ünï%.txt") && names.Contains("sub/")
        && !names.Any(n => n.Contains("hidden")) && !names.Any(n => n.StartsWith("link_out")) && !names.Any(n => n.StartsWith("link_in")), string.Join(",", names));
    bool crcOk = true; string bad = "";
    foreach (var e in za.Entries.Where(e => !e.FullName.EndsWith("/")))
    {
        try { using var es = e.Open(); var ms2 = new MemoryStream(); es.CopyTo(ms2);
              var disk = File.ReadAllBytes(Path.Combine(share, e.FullName.Replace('/', '\\')));
              if (!ms2.ToArray().AsSpan().SequenceEqual(disk)) { crcOk = false; bad = e.FullName; } }
        catch (Exception ex) { crcOk = false; bad = e.FullName + " " + ex.Message; }
    }
    Check("zip: every file byte-identical (CRC checked)", crcOk, bad);
}
var zipFile = Path.Combine(root, "check.zip"); File.WriteAllBytes(zipFile, r.body);
var tarOut = Path.Combine(root, "tarcheck"); Directory.CreateDirectory(tarOut);
var tp = Process.Start(new ProcessStartInfo("tar.exe", $"-xf \"{zipFile}\" -C \"{tarOut}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
var tarErr = tp.StandardError.ReadToEnd(); tp.WaitForExit();
Check("zip: Windows tar extracts it", tp.ExitCode == 0 && File.ReadAllText(Path.Combine(tarOut, "a.txt")) == "hello world", tarErr);
Thread.Sleep(150); logs = Logs();
Check("log: zip download", logs.Any(l => l.Text.StartsWith("Fully downloaded /Share/ as ZIP")), string.Join(" | ", logs.Select(l => l.Text)));
r = Raw("GET", "/Private/?zip"); Check("zip of private folder needs sign-in", r.code == 401, r.code.ToString());

// ZIP64 path: more than 65,535 entries
var many = Path.Combine(root, "many"); Directory.CreateDirectory(many);
for (int k = 0; k < 66000; k++) File.WriteAllBytes(Path.Combine(many, $"f{k:D5}.txt"), k % 1000 == 0 ? Encoding.ASCII.GetBytes("x" + k) : []);
srv.SetItems([new SharedItem { Name = "Many", Path = many, IsFolder = true }]);
r = Raw("GET", "/Many/?zip");
Check("zip64: exact length", r.code == 200 && long.Parse(r.h["Content-Length"]) == r.body.Length, $"{r.code} {r.h.GetValueOrDefault("Content-Length")} {r.body.Length}");
using (var za = new System.IO.Compression.ZipArchive(new MemoryStream(r.body)))
{
    var e = za.GetEntry("f05000.txt");
    string got = e == null ? "missing" : new StreamReader(e.Open()).ReadToEnd();
    Check("zip64: 66,000 entries readable", za.Entries.Count == 66000 && got == "x5000", $"{za.Entries.Count} {got}");
}

var hn = new HostNames();
hn.Get("127.0.0.1"); hn.Get("8.8.8.8");
for (int k = 0; k < 50 && (hn.Get("127.0.0.1") == null || hn.Get("8.8.8.8") == null); k++) Thread.Sleep(100);
Check("host names: loopback + reverse DNS", hn.Get("127.0.0.1") == "this PC" && (hn.Get("8.8.8.8") ?? "").Contains("google"), $"{hn.Get("127.0.0.1")} / {hn.Get("8.8.8.8")}");
Check("adapters listed", Network.Adapters().Any(a => a.Address == "192.168.18.13"), string.Join(",", Network.Adapters().Select(a => a.Name + "=" + a.Address)));
var pub = Network.PublicIPv4Async().GetAwaiter().GetResult();
Check("public IPv4 found", pub != null && !Guard.IsPrivate(IPAddress.Parse(pub)), pub ?? "null");
Logs();

srv.SetItems([]);
r = Raw("GET", "/Share/a.txt"); Check("removed item -> 404 immediately", r.code == 404, r.code.ToString());
await srv.StopAsync();
try { Raw("GET", "/"); Check("stopped refuses", false); } catch { Check("stopped refuses", true); }
await srv.StartAsync(port, IPAddress.Loopback); Check("restart works", Raw("GET", "/").code == 200); await srv.StopAsync();

// busy port
var blocker = new TcpListener(IPAddress.Loopback, port + 1); blocker.Start();
try { await srv.StartAsync(port + 1, IPAddress.Loopback); Check("busy port throws", false); }
catch (Exception ex) { Check("busy port throws", true); Console.WriteLine("   -> " + ex.GetType().Name + " / inner " + ex.InnerException?.GetType().Name); }
blocker.Stop();

Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{Path.Combine(share, "link_out")}\" & rmdir \"{Path.Combine(share, "link_in")}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
Directory.Delete(root, true);
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
