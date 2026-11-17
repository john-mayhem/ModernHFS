# Modern HTTP File Server

Own replacement for HFS 2.3m (which has an unauthenticated RCE, CVE-2024-23692).
C# WinForms window + Kestrel web server, one self-contained exe.

- Run: `publish\ModernHFS.exe`
- Build: `cd src && dotnet publish -c Release -p:SelfContained=true -o ..\publish`
- Settings + share list + bans: `%APPDATA%\ModernHFS\settings.json`; statistics: `stats.json` next to it (saved every minute + on exit)

## Status

| Step | What | State |
|---|---|---|
| 1 | Window, drag-and-drop sharing, start/stop, port, folder pages, resumable downloads, saved list, tray | done 2026-11-17 (owner: "looks good") |
| 2 | Connections list (kick/ban), log, statistics, bandwidth graph | done 2026-11-17 |
| 3 | Upload per folder, accounts, per-item download/upload rules ("anyone" for old machines) | **built 2026-11-17, owner testing** |
| 4 | External IP + which address goes into links, final polish | **network part built 2026-11-17** (+ ZIP, host names) |

Open question: how old the "older machines" are (decides how plain the visitor HTML must be). Owner: later.

## How it works

- `/` lists shared items; `/{item}/sub/path` walks inside a shared folder. Folders are live (read from disk on every request).
- Path safety (`FileServer.TryParsePath` / `Resolve`): segments with `\ : * ? " < > |`, control chars, or trailing dot/space are rejected (400);
  every step must exist, hidden/system entries are invisible, junctions/symlinks that point outside the shared folder are refused.
- Downloads: Range/If-Range/ETag via `Results.File(enableRangeProcessing)`, `?dl=1` forces "save as".
  HTML/SVG/XML files are served with `CSP: sandbox` so they can't run script on the server's origin.
- Visitor pages: plain HTML + inline CSS, no JavaScript, light/dark via `prefers-color-scheme`.

- Monitoring (`Traffic.cs`): a Kestrel connection middleware wraps each connection's transport pipe to count bytes in/out
  (feeds the graph, per-connection speed/progress, totals) and drops banned addresses before HTTP starts. `FileServer.TrackAsync`
  wraps every request for status, statistics and the log line. A download counts as "downloaded" only when it starts at byte 0
  and finishes; video players' range chunks don't count. Start lines are logged only for files over 4 MB.
- Layout follows HFS 2: share link + bandwidth graph on top; shared items | Log/Statistics/Banned tabs; connections at the bottom;
  totals in the status bar. Pane sizes and the open tab are remembered.

- Accounts (`Auth.cs`): HTTP Basic sign-in (works back to IE6, no JS/cookies; like HFS 2 it's plain HTTP, so passwords
  cross the network unencrypted). PBKDF2-SHA256 hashes in settings.json; verified logins cached 15 min. Wrong passwords:
  reply stalls 1-5 s from the 3rd try, 10 in 15 min = auto-ban (never for 127.0.0.1/::1).
- Rules per shared item (right-click): download = anyone / accounts only; uploads (folders) = off / anyone / accounts only.
  Accounts-only items are hidden from anonymous visitors on `/`. Accounts-only download + anyone upload = "drop box"
  (upload form, no file list). Sign-in link = `/?login=<return path>`.
- Uploads: POST multipart to the folder URL, streamed to a hidden `.<guid>.uploading` temp file, renamed on success,
  never overwrite (`name (2).ext`). IE full-path names, `..\`, ADS colons, device names (CON...) are cleaned.
  Stops when the disk would drop under `MinFreeMB` (settings, default 1024). Partial files are deleted on abort.

- Bot protection (`Guard.cs`, 2026-11-17): per-address connection cap (24), token-bucket rate limit (30 req/s, burst 150,
  else 429), strike points (scanner probe like /wp-login.php or /.env = 5, path tricks = 3, not-found / flood = 1);
  10 points in 30 min = auto-ban for 7 days (Bans tab shows reason + expiry). LAN/loopback never limited or auto-banned.
  NOTE: behind a reverse proxy/tunnel every visitor looks like 127.0.0.1, so a trusted forwarded-IP header is needed first.
- Hardening: Kestrel header/line/count limits, 15 s header timeout (slowloris), 64 KB body cap except real uploads,
  X-Frame-Options DENY + CSP frame-ancestors/base-uri, uploads refused when Origin is another site (CSRF; old browsers
  without Origin still work), redirects rebuild the query from known keys only, log text cleaned of control/bidi chars,
  upload names cleaned of bidi marks, at most 2 password hashes computed at once.

- Design (2026-11-17 pass, from 2025-26 research: Fluent 2 + "barely-there"/data-dense utilitarian):
  window = Windows 11 neutrals, the user's own accent colour (AccentPalette registry), Segoe UI Variable, Segoe Fluent
  Icons on buttons, segmented tabs, accent selection bar, gradient bandwidth graph. Web page = one system font, hairline
  borders, file-type icons (inline SVG in CSS), tabular numbers, `?q=` filter (no JS needed), semantic HTML
  (nav/aria-sort/scope), focus rings, reduced-motion, phone layout; owner's accent colour on the page too.
  Drag-and-drop uploads with per-file progress via a nonce'd inline script (CSP `script-src 'nonce-...'`,
  `connect-src 'self'`); without JS the plain form still works.

- ZIP (`Zip.cs`): `/{folder}/?zip` streams the folder as a stored (uncompressed) ZIP with an exact Content-Length
  (progress + time left in the browser), CRC32 via System.IO.Hashing, ZIP64 past 4 GB / 65k entries, UTF-8 names.
  Same visibility rules as browsing; links into the same folder skipped, loops guarded; max 200k entries (413).
- Network (`Network.cs`): public IPv4 from ipify / icanhazip / checkip.amazonaws at start, on network change and every
  30 min; share card toggle Internet/LAN decides the copied-link host; network button (left of Port) picks the
  listen address (all cards / one card / this PC only). Host names: background reverse DNS, cached 6 h, shown in
  Connections + Visitors and as a tooltip in the Log.

## Testing

`tests/Harness` = 136 end-to-end checks against a real FileServer on 127.0.0.1 (raw TCP requests, because HttpClient
normalizes `..` and can't test traversal): downloads/resume, ~25 traversal/ADS/junction attacks, log/progress/kick/ban,
sign-in/permissions/uploads/drop box/auto-ban, bot guard + CSRF + header tricks, ZIP (byte-identical + tar + ZIP64),
host names, adapters, public IP. Run: `cd tests\Harness && dotnet run` (needs Internet for the DNS/public-IP checks;
creates and deletes its own temp folders). `dotnet run -- ico <path>` regenerates `src\app.ico`.
