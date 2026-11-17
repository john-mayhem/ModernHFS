using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace ModernHFS;

/// <summary>
/// Visitor pages. Semantic HTML + inline CSS that works with no JavaScript at all (old machines);
/// a small inline script adds drag-and-drop uploads with progress where the browser supports it.
/// </summary>
static class Listing
{
    sealed record Entry(string Name, bool Dir, long Size, DateTime Modified);

    public static string Root(IReadOnlyList<SharedItem> items, IQueryCollection q, PageContext page, int hiddenCount)
    {
        var list = new List<Entry>();
        foreach (var i in items)
        {
            try
            {
                if (i.IsFolder)
                {
                    var d = new DirectoryInfo(i.Path);
                    if (d.Exists) list.Add(new(i.Name, true, -1, d.LastWriteTime));
                }
                else
                {
                    var f = new FileInfo(i.Path);
                    if (f.Exists) list.Add(new(i.Name, false, f.Length, f.LastWriteTime));
                }
            }
            catch { }
        }
        return Render([], list, q, isRoot: true, page, hiddenCount);
    }

    public static string Folder(string full, string root, string[] segs, IQueryCollection q, PageContext page)
    {
        var list = new List<Entry>();
        if (!page.ShowFiles) return Render(segs, list, q, isRoot: false, page, 0);   // drop box: upload only
        foreach (var fsi in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            FileAttributes a;
            try { a = fsi.Attributes; } catch { continue; }
            if (!FileServer.Visible(a)) continue;
            if ((a & FileAttributes.ReparsePoint) != 0 && !FileServer.LinkStaysInside(fsi.FullName, a, root)) continue;
            bool dir = (a & FileAttributes.Directory) != 0;
            long size = -1;
            if (!dir) { try { size = ((FileInfo)fsi).Length; } catch { continue; } }
            list.Add(new(fsi.Name, dir, size, fsi.LastWriteTime));
        }
        return Render(segs, list, q, isRoot: false, page, 0);
    }

    // ---- pages ---------------------------------------------------------------------------------

    static string Render(string[] segs, List<Entry> all, IQueryCollection q, bool isRoot, PageContext page, int hiddenCount)
    {
        string sort = q["s"].ToString();
        if (sort is not ("n" or "s" or "d")) sort = isRoot ? "" : "n";   // root keeps the owner's order by default
        bool rev = q.ContainsKey("r");
        string filter = q["q"].ToString().Trim();
        if (filter.Length > 100) filter = filter[..100];
        var list = filter.Length == 0 ? all : all.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sort.Length > 0) Sort(list, sort, rev);

        var sb = new StringBuilder(8192 + list.Count * 420);
        string title = segs.Length == 0 ? "Shared files" : segs[^1];
        Head(sb, title, page);
        Crumbs(sb, segs);

        int dirs = all.Count(e => e.Dir), files = all.Count - dirs;
        long total = all.Where(e => !e.Dir).Sum(e => e.Size);
        sb.Append("<div class=\"head\"><div><h1>").Append(H(title)).Append("</h1><p class=\"meta\">");
        var meta = new List<string>();
        if (dirs > 0) meta.Add(Plural(dirs, "folder"));
        if (files > 0) meta.Add(Plural(files, "file") + ", " + Fmt.Size(total));
        sb.Append(meta.Count > 0 ? string.Join(" &middot; ", meta) : page.ShowFiles ? "Empty" : "Drop box").Append("</p></div>");
        if (page.ShowFiles && all.Count > 0)
        {
            sb.Append("<div class=\"tools\">");
            if (!isRoot)
                sb.Append("<a class=\"btn ghost zipbtn\" href=\"?zip\" rel=\"nofollow\" title=\"Everything in this folder as one ZIP file\">")
                  .Append("<span class=\"ic i-zipdl\"></span>Download all</a>");
            sb.Append("<form class=\"search\" method=\"get\" action=\"\" role=\"search\">");
            if (sort.Length > 0 && sort != (isRoot ? "" : "n")) sb.Append("<input type=\"hidden\" name=\"s\" value=\"").Append(sort).Append("\">");
            if (rev) sb.Append("<input type=\"hidden\" name=\"r\" value=\"1\">");
            sb.Append("<input type=\"search\" name=\"q\" value=\"").Append(H(filter)).Append("\" placeholder=\"Filter by name\" aria-label=\"Filter by name\">")
              .Append("<button type=\"submit\" class=\"btn ghost\">Filter</button></form></div>");
        }
        sb.Append("</div>");

        UploadPanel(sb, segs, page);
        if (!page.ShowFiles)
        {
            sb.Append("<p class=\"note center\">This is a drop box: you can upload here, but only people with an account can see what's inside.</p>");
            Foot(sb, page);
            return sb.ToString();
        }

        if (filter.Length > 0)
            sb.Append("<p class=\"filtered\">").Append(Plural(list.Count, "match")).Append(" for &ldquo;").Append(H(filter))
              .Append("&rdquo; &middot; <a href=\"").Append(SortHref(sort, rev, "")).Append("\">Show everything</a></p>");

        sb.Append("<div class=\"list\"><table><thead><tr>");
        Th(sb, "Name", "n", "name", sort, rev, filter);
        Th(sb, "Size", "s", "size", sort, rev, filter);
        Th(sb, "Modified", "d", "date", sort, rev, filter);
        sb.Append("<th class=\"act\" scope=\"col\"><span class=\"vh\">Download</span></th></tr></thead><tbody>");

        if (segs.Length > 0 && filter.Length == 0)
            sb.Append("<tr class=\"up\"><td class=\"name\" colspan=\"4\"><a href=\"../\"><span class=\"ic i-up\"></span><span class=\"nm\">Up one level</span></a></td></tr>");
        if (list.Count == 0)
            sb.Append("<tr><td class=\"empty\" colspan=\"4\">").Append(
                filter.Length > 0 ? "Nothing here matches that." :
                isRoot ? (hiddenCount > 0 && page.User == null ? "Nothing is shared publicly. Sign in to see more." : "Nothing is shared right now.")
                : "This folder is empty.").Append("</td></tr>");

        foreach (var e in list)
        {
            string href = Uri.EscapeDataString(e.Name) + (e.Dir ? "/" : "");
            string size = e.Dir ? "" : Fmt.Size(e.Size);
            string date = Fmt.Date(e.Modified);
            sb.Append("<tr><td class=\"name\"><a href=\"").Append(href).Append("\"><span class=\"ic ").Append(IconClass(e)).Append("\"></span>")
              .Append("<span class=\"nm\">").Append(H(e.Name)).Append("</span><span class=\"sub\">")
              .Append(e.Dir ? "Folder" : size).Append(" &middot; ").Append(date).Append("</span></a></td>")
              .Append("<td class=\"size\">").Append(e.Dir ? "<span class=\"dim\">&mdash;</span>" : size).Append("</td>")
              .Append("<td class=\"date\">").Append(date).Append("</td><td class=\"act\">");
            if (!e.Dir)
                sb.Append("<a class=\"dl\" href=\"").Append(href).Append("?dl=1\" title=\"Download\" aria-label=\"Download ").Append(H(e.Name))
                  .Append("\">&#8595;</a>");
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");
        Foot(sb, page);
        return sb.ToString();
    }

    static void UploadPanel(StringBuilder sb, string[] segs, PageContext page)
    {
        if (page.Upload == UploadState.Allowed)
        {
            var action = segs.Length == 0 ? "/" : FileServer.Url(segs) + "/";
            sb.Append("<form id=\"up\" class=\"drop\" method=\"post\" enctype=\"multipart/form-data\" action=\"").Append(action).Append("\">")
              .Append("<div class=\"drop-row\"><span class=\"ic i-upload\"></span><div class=\"drop-text\"><strong>Upload files</strong>")
              .Append("<span class=\"js-only\"> &middot; drag them anywhere on this page</span>")
              .Append("<span class=\"hint\">Nothing gets overwritten: if a name is taken, the new file gets a number.</span></div>")
              .Append("<div class=\"drop-act\"><input type=\"file\" name=\"file\" multiple id=\"file\" aria-label=\"Choose files\"> ")
              .Append("<button type=\"submit\" class=\"btn\">Upload</button></div></div>")
              .Append("<ul id=\"queue\" class=\"queue\"></ul></form>");
        }
        else if (page.Upload == UploadState.NeedLogin)
        {
            sb.Append("<div class=\"drop muted\"><div class=\"drop-row\"><span class=\"ic i-lock\"></span><div class=\"drop-text\"><strong>Uploads need an account</strong>")
              .Append("<span class=\"hint\"><a href=\"").Append(H(page.LoginUrl)).Append("\">Sign in</a> to upload files here.</span></div></div></div>");
        }
    }

    public static string UploadDone(string[] segs, List<UploadResult> results, PageContext page, bool diskFull)
    {
        int ok = results.Count(r => r.Error == null);
        var sb = new StringBuilder();
        string title = ok == results.Count && ok > 0 ? "Upload finished" : ok == 0 ? "Upload failed" : "Upload partly finished";
        Head(sb, title, page);
        Crumbs(sb, segs);
        sb.Append("<div class=\"head\"><div><h1>").Append(title).Append("</h1><p class=\"meta\">")
          .Append(Plural(ok, "file")).Append(" saved").Append("</p></div></div>");
        sb.Append("<div class=\"list\"><table><thead><tr><th class=\"name\" scope=\"col\">File</th><th class=\"size\" scope=\"col\">Size</th>")
          .Append("<th class=\"date\" scope=\"col\">Result</th></tr></thead><tbody>");
        if (results.Count == 0)
            sb.Append("<tr><td class=\"empty\" colspan=\"3\">No file was chosen.</td></tr>");
        foreach (var r in results)
        {
            sb.Append("<tr><td class=\"name\"><span class=\"cell\"><span class=\"ic ").Append(IconClass(new Entry(r.Name, false, 0, default)))
              .Append("\"></span><span class=\"nm\">").Append(H(r.Name)).Append("</span></span></td><td class=\"size\">")
              .Append(r.Error == null ? Fmt.Size(r.Bytes) : "").Append("</td><td class=\"date result\">");
            if (r.Error != null) sb.Append("<span class=\"bad\">").Append(H(r.Error)).Append("</span>");
            else if (r.SavedAs != r.Name) sb.Append("<span class=\"ok\">Saved as ").Append(H(r.SavedAs ?? "")).Append("</span>");
            else sb.Append("<span class=\"ok\">Saved</span>");
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");
        if (diskFull) sb.Append("<p class=\"note bad\">The server's disk is almost full, so the upload stopped.</p>");
        sb.Append("<p class=\"back\"><a class=\"btn ghost\" href=\"").Append(FileServer.Url(segs)).Append("/\">&larr; Back to ")
          .Append(H(segs[^1])).Append("</a></p>");
        Foot(sb, page);
        return sb.ToString();
    }

    public static string Error(int code, string message)
    {
        var sb = new StringBuilder();
        string title = code switch { 404 => "Not found", 401 => "Sign in needed", 403 => "Not allowed", 405 => "Not allowed", _ => "Something went wrong" };
        Head(sb, title, null);
        sb.Append("<div class=\"msg\"><div class=\"code\">").Append(code).Append("</div><h1>").Append(title).Append("</h1><p>")
          .Append(H(message)).Append("</p><p><a class=\"btn ghost\" href=\"/\">Go to the shared files</a></p></div>");
        Foot(sb, null);
        return sb.ToString();
    }

    // ---- pieces --------------------------------------------------------------------------------

    static void Crumbs(StringBuilder sb, string[] segs)
    {
        if (segs.Length == 0) return;
        sb.Append("<nav class=\"crumbs\" aria-label=\"Breadcrumb\"><a href=\"/\">Home</a>");
        for (int i = 0; i < segs.Length; i++)
        {
            sb.Append("<span class=\"sep\" aria-hidden=\"true\">/</span>");
            if (i == segs.Length - 1) sb.Append("<span aria-current=\"page\">").Append(H(segs[i])).Append("</span>");
            else sb.Append("<a href=\"").Append(FileServer.Url(segs.Take(i + 1))).Append("/\">").Append(H(segs[i])).Append("</a>");
        }
        sb.Append("</nav>");
    }

    static string SortHref(string key, bool rev, string filter)
    {
        var parts = new List<string>();
        if (key.Length > 0) parts.Add("s=" + key);
        if (rev) parts.Add("r=1");
        if (filter.Length > 0) parts.Add("q=" + Uri.EscapeDataString(filter));
        return parts.Count == 0 ? "./" : "?" + string.Join("&amp;", parts);
    }

    static void Th(StringBuilder sb, string label, string key, string cls, string sort, bool rev, string filter)
    {
        bool active = sort == key;
        sb.Append("<th class=\"").Append(cls).Append("\" scope=\"col\"");
        if (active) sb.Append(" aria-sort=\"").Append(rev ? "descending" : "ascending").Append('"');
        sb.Append("><a href=\"").Append(SortHref(key, active && !rev, filter)).Append("\">").Append(label);
        if (active) sb.Append("<span class=\"arrow\" aria-hidden=\"true\">").Append(rev ? "&#8595;" : "&#8593;").Append("</span>");
        sb.Append("</a></th>");
    }

    static void Sort(List<Entry> list, string sort, bool rev)
    {
        Comparison<Entry> by = sort switch
        {
            "s" => (a, b) => a.Size.CompareTo(b.Size),
            "d" => (a, b) => a.Modified.CompareTo(b.Modified),
            _ => (a, b) => Native.StrCmpLogicalW(a.Name, b.Name),
        };
        list.Sort((a, b) =>
        {
            if (a.Dir != b.Dir) return a.Dir ? -1 : 1;           // folders always on top
            int c = by(a, b);
            if (c == 0) c = Native.StrCmpLogicalW(a.Name, b.Name);
            return rev ? -c : c;
        });
    }

    static string Plural(int n, string word) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? word : word.EndsWith("ch") ? word + "es" : word + "s");

    static string H(string s) => WebUtility.HtmlEncode(s);

    static void Head(StringBuilder sb, string title, PageContext? page)
    {
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<meta name=\"color-scheme\" content=\"light dark\">")
          .Append("<title>").Append(H(title)).Append(" &middot; Modern HTTP File Server</title><style>").Append(Css).Append("</style></head><body>")
          .Append("<header class=\"bar\"><div class=\"in\"><a class=\"brand\" href=\"/\"><span class=\"logo\"></span>Modern HTTP File Server</a>");
        if (page?.User != null)
            sb.Append("<span class=\"who\"><span class=\"avatar\">").Append(H(page.User[..1].ToUpperInvariant())).Append("</span>")
              .Append(H(page.User)).Append("</span>");
        else if (page is { HasAccounts: true })
            sb.Append("<a class=\"who btn ghost small\" href=\"").Append(H(page.LoginUrl)).Append("\">Sign in</a>");
        sb.Append("</div></header><main class=\"in\">");
    }

    static void Foot(StringBuilder sb, PageContext? page)
    {
        sb.Append("</main>");
        if (page?.Upload == UploadState.Allowed && page.Nonce.Length > 0)
            sb.Append("<script nonce=\"").Append(page.Nonce).Append("\">").Append(UploadScript).Append("</script>");
        sb.Append("</body></html>");
    }

    // ---- file type icons (inline SVG in CSS, so old browsers simply show none) -----------------

    static readonly Dictionary<string, string> Kinds = BuildKinds();

    static Dictionary<string, string> BuildKinds()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, string exts) { foreach (var e in exts.Split(' ')) d["." + e] = kind; }
        Add("img", "jpg jpeg png gif webp bmp tif tiff heic heif avif svg ico raw cr2 nef arw dng psd");
        Add("vid", "mp4 mkv avi mov wmv webm m4v mpg mpeg ts m2ts flv 3gp vob");
        Add("aud", "mp3 flac wav ogg opus m4a aac wma aiff ape alac mid midi dsf dff");
        Add("zip", "zip 7z rar tar gz tgz bz2 xz zst iso cab lz lzma");
        Add("doc", "pdf doc docx odt rtf txt md xls xlsx ods csv ppt pptx odp epub fb2 djvu");
        Add("code", "cs js ts py c cpp h hpp java go rs php rb sh ps1 bat cmd json xml yml yaml ini cfg html htm css sql lua");
        Add("app", "exe msi apk dmg deb rpm appx msix jar dll");
        return d;
    }

    static string IconClass(Entry e) =>
        e.Dir ? "i-dir" : Kinds.TryGetValue(Path.GetExtension(e.Name), out var k) ? "i-" + k : "i-file";

    static string Svg(string body, string color, bool fill = false) =>
        "url(\"data:image/svg+xml," + Uri.EscapeDataString(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='" + (fill ? color : "none") + "' stroke='" + color +
            "' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'>" + body + "</svg>") + "\")";

    const string DocShape = "<path d='M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z'/><path d='M14 3v5h5'/>";

    static readonly string IconCss = string.Concat(
        ".i-dir{background-image:", Svg("<path d='M3 7.5A2.5 2.5 0 0 1 5.5 5H9l2 2h7.5A2.5 2.5 0 0 1 21 9.5v7a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 16.5z' stroke='none'/>", "#E3A33B", true), "}",
        ".i-file{background-image:", Svg(DocShape, "#8C959F"), "}",
        ".i-doc{background-image:", Svg(DocShape + "<path d='M8.5 13h7M8.5 16.5h5'/>", "#3B82C4"), "}",
        ".i-code{background-image:", Svg(DocShape + "<path d='M10 12.5l-2 2 2 2M14 12.5l2 2-2 2'/>", "#7C8590"), "}",
        ".i-img{background-image:", Svg("<rect x='3.5' y='4.5' width='17' height='15' rx='2.5'/><circle cx='9' cy='10' r='1.8'/><path d='M20.5 15.5l-4.5-4.5-8.5 8.5'/>", "#2EA05A"), "}",
        ".i-vid{background-image:", Svg("<rect x='3.5' y='5.5' width='17' height='13' rx='2.5'/><path d='M10.5 9.5v5l4-2.5z' fill='#8B5CF6'/>", "#8B5CF6"), "}",
        ".i-aud{background-image:", Svg("<path d='M9 17.5V6.5l10-2v11'/><circle cx='6.5' cy='17.5' r='2.5'/><circle cx='16.5' cy='15.5' r='2.5'/>", "#D9488F"), "}",
        ".i-zip{background-image:", Svg("<rect x='4.5' y='3.5' width='15' height='17' rx='2.5'/><path d='M12 3.5v2M11 6.5h2M12 8.5v2M11 11.5h2'/><rect x='10.5' y='13.5' width='3' height='3' rx='.8'/>", "#B7791F"), "}",
        ".i-app{background-image:", Svg("<rect x='3.5' y='4.5' width='17' height='15' rx='2.5'/><path d='M3.5 9h17'/><path d='M6.5 6.8h.01M9 6.8h.01'/>", "#64748B"), "}",
        ".i-up{background-image:", Svg("<path d='M9 14L4.5 9.5 9 5'/><path d='M4.5 9.5H15a4.5 4.5 0 0 1 4.5 4.5v5'/>", "#8C959F"), "}",
        ".i-upload{background-image:", Svg("<path d='M12 15V4.5M7.5 9L12 4.5 16.5 9'/><path d='M4.5 15v2.5A2.5 2.5 0 0 0 7 20h10a2.5 2.5 0 0 0 2.5-2.5V15'/>", "#3B82C4"), "}",
        ".i-zipdl{background-image:", Svg("<path d='M12 4v10.5M7.5 10L12 14.5 16.5 10'/><path d='M4.5 16v1.5A2.5 2.5 0 0 0 7 20h10a2.5 2.5 0 0 0 2.5-2.5V16'/>", "#8C959F"), "}",
        ".i-lock{background-image:", Svg("<rect x='5' y='10.5' width='14' height='10' rx='2.5'/><path d='M8 10.5V8a4 4 0 0 1 8 0v2.5'/>", "#8C959F"), "}");

    // ---- style ---------------------------------------------------------------------------------
    // Plain colour values (no CSS variables) so even very old browsers get the light theme.

    static readonly string Css = BuildCss();

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static string BuildCss()
    {
        // The owner's Windows accent colour, in the shades Windows uses for light and dark mode.
        string lightAccent = Hex(Theme.Dark ? Theme.Mix(Theme.AccentHover, Color.Black, 0.25f) : Theme.Accent);
        string darkAccent = Hex(Theme.Dark ? Theme.Accent : Theme.Mix(Theme.Accent, Color.White, 0.45f));
        return CssTemplate.Replace("$A", lightAccent).Replace("$D", darkAccent) + IconCss;
    }

    const string CssTemplate = """
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:#fafafa;color:#1b1b1f;font:15px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,"Helvetica Neue",Roboto,Arial,sans-serif}
a{color:$A;text-decoration:none}
a:hover{text-decoration:underline}
a:focus-visible,button:focus-visible,input:focus-visible{outline:2px solid $A;outline-offset:2px;border-radius:6px}
.in{max-width:1080px;margin:0 auto;padding:0 20px}
.vh{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)}
.bar{background:#fafafa;border-bottom:1px solid #e7e7eb;position:sticky;top:0;z-index:5}
.bar .in{display:flex;align-items:center;justify-content:space-between;height:56px}
.brand{color:#1b1b1f;font-weight:600;font-size:14px;display:flex;align-items:center}
.brand:hover{text-decoration:none}
.logo{display:inline-block;width:20px;height:20px;border-radius:6px;background:$A;margin-right:10px;vertical-align:-4px}
.who{color:#5b5b66;font-size:14px;display:flex;align-items:center}
.avatar{display:inline-block;width:26px;height:26px;line-height:26px;text-align:center;border-radius:50%;background:#ececf0;color:#1b1b1f;font-weight:600;font-size:12px;margin-right:8px}
main{padding-top:26px;padding-bottom:56px}
.crumbs{font-size:14px;color:#6b6b76;margin-bottom:8px;word-wrap:break-word}
.crumbs a{color:#6b6b76}
.crumbs .sep{color:#c4c4cc;margin:0 8px}
.crumbs [aria-current]{color:#1b1b1f}
.head{display:flex;align-items:flex-end;justify-content:space-between;gap:16px;flex-wrap:wrap;margin-bottom:18px}
h1{font-size:28px;line-height:1.2;font-weight:600;letter-spacing:-.01em;margin:0;word-wrap:break-word}
.meta{margin:6px 0 0;color:#6b6b76;font-size:14px;font-variant-numeric:tabular-nums}
.tools{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.search{display:flex;gap:8px}
.zipbtn{display:inline-flex;align-items:center}
.zipbtn .ic{width:18px;height:18px;background-size:18px 18px;margin-right:8px}
input[type=search]{font:inherit;font-size:14px;width:240px;max-width:60vw;padding:7px 12px;border:1px solid #d9d9df;border-radius:8px;background:#fff;color:inherit}
.btn{display:inline-block;font:inherit;font-size:14px;font-weight:600;line-height:20px;padding:7px 16px;border-radius:8px;border:1px solid $A;background:$A;color:#fff;cursor:pointer}
.btn:hover{text-decoration:none;filter:brightness(1.08)}
.btn.ghost{background:transparent;color:#1b1b1f;border-color:#d9d9df;font-weight:500}
.btn.ghost:hover{background:#f0f0f3}
.btn.small{padding:4px 12px}
.filtered{color:#6b6b76;font-size:14px;margin:-6px 0 12px}
.list{background:#fff;border:1px solid #e7e7eb;border-radius:12px;overflow:hidden}
table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
th{text-align:left;font-size:13px;font-weight:600;color:#6b6b76;padding:10px 16px;border-bottom:1px solid #e7e7eb;white-space:nowrap}
th a{color:#6b6b76}
th a:hover{color:#1b1b1f;text-decoration:none}
th[aria-sort] a{color:#1b1b1f}
.arrow{margin-left:4px}
td{padding:0 16px;height:48px;border-bottom:1px solid #f0f0f3;vertical-align:middle}
tbody tr:last-child td{border-bottom:0}
tbody tr:hover td{background:#f6f6f9}
td.name a,.cell{display:flex;align-items:center;color:#1b1b1f;min-height:48px}
td.name a:hover{text-decoration:none}
td.name a:hover .nm{text-decoration:underline}
.nm{word-break:break-word}
.sub{display:none}
.ic{display:inline-block;flex:none;width:22px;height:22px;margin-right:14px;background-repeat:no-repeat;background-position:center;background-size:22px 22px}
th.size,td.size{text-align:right}
td.size,td.date{white-space:nowrap;color:#6b6b76;font-size:14px}
th.date,td.date{padding-left:28px}
.dim{color:#c4c4cc}
.act{width:1%;padding:0 10px 0 4px;text-align:right}
.dl{display:inline-block;width:34px;height:34px;line-height:32px;text-align:center;border-radius:8px;color:#6b6b76;font-size:17px;border:1px solid transparent}
.dl:hover{background:#f0f0f3;border-color:#e7e7eb;color:$A;text-decoration:none}
.up a{color:#6b6b76}
.empty{color:#6b6b76;text-align:center;height:120px}
.drop{background:#fff;border:1.5px dashed #cfcfd6;border-radius:12px;padding:14px 16px;margin:0 0 16px}
.drop.muted{border-style:solid;border-width:1px;border-color:#e7e7eb}
.drop.over{border-color:$A;background:#f3f7fd}
.drop-row{display:flex;align-items:center;gap:14px;flex-wrap:wrap}
.drop .ic{margin-right:0;width:28px;height:28px;background-size:28px 28px}
.drop-text{flex:1;min-width:200px}
.drop-text strong{font-weight:600}
.hint{display:block;color:#6b6b76;font-size:13px;margin-top:2px}
.drop-act{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.drop-act input{font-size:13px;max-width:260px}
input[type=file]::file-selector-button{font:inherit;font-size:13px;font-weight:500;padding:6px 12px;margin-right:10px;border:1px solid #d9d9df;border-radius:8px;background:#fff;color:#1b1b1f;cursor:pointer}
input[type=file]::file-selector-button:hover{background:#f0f0f3}
.js-only{display:none;color:#6b6b76}
.js .js-only{display:inline}
.queue{list-style:none;margin:0;padding:0}
.queue li{display:flex;align-items:center;gap:12px;margin-top:10px;font-size:14px}
.queue .qn{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.queue .qs{color:#6b6b76;font-size:13px;width:110px;text-align:right;font-variant-numeric:tabular-nums}
.track{width:180px;height:6px;border-radius:3px;background:#ececf0;overflow:hidden}
.fill{height:100%;width:0;background:$A;transition:width .2s}
.ok{color:#1a7f37}
.bad{color:#c42b1c}
.note{color:#6b6b76;font-size:14px}
.center{text-align:center;margin-top:28px}
.back{margin-top:18px}
.msg{max-width:460px;margin:12vh auto 0;text-align:center}
.msg .code{font-size:56px;font-weight:700;letter-spacing:-.03em;color:#d4d4da;line-height:1}
.msg h1{margin:10px 0 8px}
.msg p{color:#5b5b66}
@media (max-width:700px){
.in{padding:0 14px}
h1{font-size:23px}
th.size,td.size,th.date,td.date{display:none}
.sub{display:block;color:#6b6b76;font-size:13px;margin-top:1px}
td.name a{flex-wrap:wrap;padding:8px 0}
.nm{flex:1;min-width:0}
.sub{flex-basis:100%;padding-left:36px}
.tools,.search,input[type=search]{width:100%;max-width:none}
.search{flex:1}
.head>div{width:100%}
.track{width:90px}
}
@media (hover:none){.dl{border-color:#e7e7eb}}
@media (prefers-reduced-motion:reduce){*{transition:none!important}}
@media (prefers-color-scheme:dark){
body,.bar{background:#151517;color:#ececf1}
a{color:$D}
a:focus-visible,button:focus-visible,input:focus-visible{outline-color:$D}
.bar{border-color:#28282d}
.brand,.crumbs [aria-current],td.name a,.cell,th[aria-sort] a,th a:hover{color:#ececf1}
.logo{background:$D}
.who,.crumbs,.crumbs a,.meta,th,th a,td.size,td.date,.up a,.empty,.hint,.js-only,.queue .qs,.note,.filtered,.dl,.sub,.msg p{color:#9c9ca8}
.avatar{background:#2a2a30;color:#ececf1}
.crumbs .sep,.dim{color:#4a4a52}
.list,.drop,input[type=search]{background:#1c1c1f;border-color:#2c2c32}
.drop{border-color:#3a3a42}
.drop.over{border-color:$D;background:#1b2330}
th{border-color:#2c2c32}
td{border-color:#242428}
tbody tr:hover td{background:#212125}
.btn{background:$D;border-color:$D;color:#0b0b0d}
.btn.ghost{background:transparent;color:#ececf1;border-color:#34343b}
.btn.ghost:hover,.dl:hover{background:#26262b;border-color:#34343b}
.dl:hover{color:$D}
.track{background:#2a2a30}
input[type=file]::file-selector-button{background:#1c1c1f;color:#ececf1;border-color:#34343b}
input[type=file]::file-selector-button:hover{background:#26262b}
.fill{background:$D}
.ok{color:#6ccb5f}
.bad{color:#ff99a4}
.msg .code{color:#34343b}
}
@media (prefers-color-scheme:dark) and (hover:none){.dl{border-color:#2c2c32}}
""";

    // Drag-and-drop + per-file progress. Runs only where XHR uploads exist; the plain form is the fallback.
    const string UploadScript = """
(function(){
var f=document.getElementById('up');if(!f||!window.FormData||!window.XMLHttpRequest)return;
var x0=new XMLHttpRequest();if(!x0.upload)return;
document.documentElement.className+=' js';
var input=document.getElementById('file'),q=document.getElementById('queue'),busy=false,list=[],done=0,failed=0;
function size(n){var u=['B','KB','MB','GB','TB'],i=0;while(n>=1024&&i<4){n/=1024;i++}return (i?n.toFixed(n<10?2:n<100?1:0):n)+' '+u[i]}
function add(files){for(var i=0;i<files.length;i++){var li=document.createElement('li');
li.innerHTML='<span class="qn"></span><span class="track"><span class="fill"></span></span><span class="qs">Waiting</span>';
li.firstChild.textContent=files[i].name;q.appendChild(li);list.push({file:files[i],li:li});}
if(!busy)next();}
function next(){var it=list.shift();if(!it){busy=false;if(done>0&&failed==0)setTimeout(function(){location.reload()},700);return}
busy=true;var x=new XMLHttpRequest(),fd=new FormData(),fill=it.li.querySelector('.fill'),st=it.li.querySelector('.qs');
fd.append('file',it.file,it.file.name);
x.upload.onprogress=function(e){if(e.lengthComputable){fill.style.width=(e.loaded/e.total*100)+'%';st.textContent=size(e.loaded)+' / '+size(e.total)}};
x.onload=function(){if(x.status==200){fill.style.width='100%';st.textContent='Done';st.className='qs ok';done++}
else{st.textContent=x.status==507?'Disk full':x.status==401?'Sign in first':'Failed ('+x.status+')';st.className='qs bad';failed++}next()};
x.onerror=function(){st.textContent='Connection lost';st.className='qs bad';failed++;next()};
x.open('POST',f.action);x.send(fd);}
f.onsubmit=function(e){if(input.files&&input.files.length){e.preventDefault();add(input.files);input.value=''}};
var depth=0;
document.addEventListener('dragenter',function(e){if(e.dataTransfer&&e.dataTransfer.types&&Array.prototype.indexOf.call(e.dataTransfer.types,'Files')>=0){depth++;f.className='drop over'}});
document.addEventListener('dragleave',function(){if(--depth<=0){depth=0;f.className='drop'}});
document.addEventListener('dragover',function(e){e.preventDefault()});
document.addEventListener('drop',function(e){e.preventDefault();depth=0;f.className='drop';if(e.dataTransfer&&e.dataTransfer.files.length)add(e.dataTransfer.files)});
})();
""";
}
