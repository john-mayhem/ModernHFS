using System.Diagnostics;
using System.Net;
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;

namespace ModernHFS;

sealed class MainForm : Form
{
    readonly AppSettings settings = AppSettings.Load();
    readonly Traffic traffic = new();
    readonly Authenticator auth = new();
    readonly FileServer server;

    // header + share link
    readonly TextField portBox = new() { DigitsOnly = true };
    readonly FlatButton startButton = new("Start", primary: true);
    readonly FlatButton copyButton = new("Copy") { Glyph = Glyph.Copy };
    readonly FlatButton openButton = new("Open") { Glyph = Glyph.Open };

    // panes
    readonly SplitContainer outer = new(), inner = new();
    readonly Card graphCard = new() { Title = "Bandwidth", HeaderHeight96 = 34 };
    readonly BandwidthGraph graph = new();
    readonly Card itemsCard = new() { Title = "Shared" };
    readonly ItemList list = new();
    readonly DropPanel dropPanel = new();
    readonly Card sideCard = new() { Tabs = ["Log", "Stats", "Bans", "Accounts"] };
    readonly GridList logList = new(26) { VirtualMode = true, FillColumn = 2, EmptyText = "Nothing has happened yet" };
    readonly StatsView statsView = new();
    readonly GridList banList = new(30) { FillColumn = 1, EmptyText = "Nobody is banned" };
    readonly AccountsView accountsView = new();
    readonly FlatButton accRemove = new("Remove") { Backdrop = Theme.Surface, Glyph = Glyph.Delete };
    readonly Card connCard = new() { Title = "Connections" };
    readonly GridList connList = new(32) { FillColumn = 2, EmptyText = "Nobody is connected" };

    // side-card header controls
    readonly TextField logFilter = new() { Placeholder = "Filter", Backdrop = Theme.Surface };
    readonly FlatButton logSave = new("Save") { Backdrop = Theme.Surface, Glyph = Glyph.Save };
    readonly FlatButton logClear = new("Clear") { Backdrop = Theme.Surface, Glyph = Glyph.Clear };
    readonly FlatButton statsReset = new("Reset") { Backdrop = Theme.Surface, Glyph = Glyph.Restart };
    readonly TextField banInput = new() { Placeholder = "IP or 10.0.0.*", Backdrop = Theme.Surface };
    readonly FlatButton banAdd = new("Ban", primary: true) { Backdrop = Theme.Surface, Glyph = Glyph.Shield };
    readonly FlatButton banRemove = new("Unban") { Backdrop = Theme.Surface, Glyph = Glyph.Delete };

    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip itemMenu = new(), connMenu = new(), visitorMenu = new(), banMenu = new(), accMenu = new(), trayMenu = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer toastTimer = new() { Interval = 2500 };
    readonly Font titleFont = UiFont.Display(13f);
    readonly Font nameFont = UiFont.Semibold(10.25f);
    readonly Font cardFont = UiFont.Semibold(10f);
    readonly Font smallFont = UiFont.Body(8.75f);
    readonly Font addrFont = UiFont.Semibold(12f);
    readonly Font tileFont = UiFont.Display(14f);
    readonly Bitmap headerIcon;
    readonly Icon trayOn, trayOff;
    readonly ToolStripMenuItem miRename, miTrayToggle;
    readonly ToolStripMenuItem miDlAnyone = new("Anyone"), miDlAccounts = new("Accounts only");
    readonly ToolStripMenuItem miUpOff = new("Off"), miUpAnyone = new("Anyone, no password"), miUpAccounts = new("Accounts only");
    readonly ToolStripMenuItem miUploads = new("Uploads");

    // log
    readonly List<LogEntry> logAll = [];
    readonly List<int> logView = [];
    string logFilterText = "";

    // connections
    readonly Dictionary<string, ListViewItem> connRows = [];
    readonly Dictionary<string, (long Bytes, double Speed)> connSpeed = [];
    readonly Stopwatch sampleClock = Stopwatch.StartNew();
    long lastOut, lastIn, statsShown = -1;
    double speedOut, speedIn;
    int ticks;

    string lanIp = Net.LanIPv4() ?? "127.0.0.1";
    string? publicIp;
    readonly HostNames hosts = new();
    int hostsShown = -1;
    readonly FlatButton netButton = new("") { Glyph = Glyph.Network };
    readonly ContextMenuStrip netMenu = new();
    Rectangle toggleInternet, toggleLan;
    string? error, toast;
    int hover = -1;
    bool busy, dragging, closing;
    TextBox? renameBox;
    Rectangle header, addrCard, addrText, statusBar;

    public MainForm()
    {
        server = new FileServer(AppIcon.Png(32), traffic, auth) { MinFreeBytes = Math.Max(0, settings.MinFreeMB) * (1L << 20) };
        traffic.SetBans(settings.Bans);
        auth.SetAccounts(settings.Accounts);
        auth.AutoBan += address =>
        {
            if (IsHandleCreated && !closing) BeginInvoke(() => AddBan(address, $"{auth.BanAfter} wrong passwords"));
        };
        traffic.Guard.AutoBan += (address, reason) =>
        {
            if (IsHandleCreated && !closing) BeginInvoke(() => AddBan(address, reason));
        };
        lastOut = traffic.TotalOut;
        lastIn = traffic.TotalIn;

        Text = "Modern HTTP File Server";
        Font = UiFont.Body(9.75f);
        headerIcon = AppIcon.Draw(this.S(22), true);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        Icon = AppIcon.Make(64, true);
        int small = SystemInformation.SmallIconSize.Width;
        trayOn = AppIcon.Make(small, true);
        trayOff = AppIcon.Make(small, false);
        MinimumSize = new Size(this.S(980), this.S(700));
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Size = new Size(Math.Min(this.S(1220), work.Width - 40), Math.Min(this.S(860), work.Height - 40));
        StartPosition = FormStartPosition.CenterScreen;
        RestoreWindow();

        // header
        portBox.Font = Font;
        portBox.Box.TextAlign = HorizontalAlignment.Center;
        portBox.Box.MaxLength = 5;
        portBox.Box.Text = settings.Port.ToString();
        portBox.Box.TextChanged += (_, _) => UpdateUi();
        portBox.Committed += () => { if (!server.IsRunning || PortEdited) _ = StartStop(); };
        startButton.Font = Font;
        startButton.Click += (_, _) => _ = StartStop();
        netButton.Font = Font;
        netButton.Click += (_, _) => netMenu.Show(netButton, new Point(0, netButton.Height + this.S(4)));
        netMenu.Opening += (_, _) => BuildNetMenu();
        copyButton.Font = openButton.Font = Font;
        copyButton.Backdrop = openButton.Backdrop = Theme.Surface;
        copyButton.Width = copyButton.PreferredWidth(80);
        openButton.Width = openButton.PreferredWidth(80);
        copyButton.Click += (_, _) => CopyText(BaseUrl, "Link copied");
        openButton.Click += (_, _) => OpenUrl(BaseUrl);

        // graph
        graphCard.Font = smallFont;
        graphCard.TitleFont = cardFont;
        graph.Font = smallFont;
        graphCard.SetContent(graph);

        // shared items
        itemsCard.Font = smallFont;
        itemsCard.TitleFont = cardFont;
        list.Font = Font;
        list.SmallImageList = new ImageList { ImageSize = new Size(1, this.S(52)) };
        list.DrawItem += DrawRow;
        list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        list.MouseMove += (_, e) => SetHover(list.GetItemAt(e.X, e.Y)?.Index ?? -1);
        list.MouseLeave += (_, _) => SetHover(-1);
        list.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) CopySelectedLinks(); };
        list.KeyDown += ListKeyDown;
        list.ContextMenuStrip = itemMenu;
        dropPanel.Font = Font;
        dropPanel.TitleFont = addrFont;

        // side card: log / statistics / banned
        sideCard.Font = smallFont;
        sideCard.TitleFont = cardFont;
        foreach (var c in new Control[] { logFilter, logSave, logClear, statsReset, banInput, banAdd, banRemove }) c.Font = Font;
        logFilter.Width = this.S(170);
        logSave.Width = logSave.PreferredWidth(66);
        logClear.Width = logClear.PreferredWidth(66);
        statsReset.Width = statsReset.PreferredWidth(72);
        banInput.Width = this.S(170);
        banAdd.Width = banAdd.PreferredWidth(56);
        banRemove.Width = banRemove.PreferredWidth(72);
        logList.Font = Font;
        logList.ShowItemToolTips = true;
        logList.AddColumn("Time", 88);
        logList.AddColumn("Address", 128);
        logList.AddColumn("Event", 300);
        logList.RetrieveVirtualItem += (_, e) =>
        {
            var en = e.ItemIndex >= 0 && e.ItemIndex < logView.Count ? logAll[logView[e.ItemIndex]] : null;
            e.Item = en == null ? new ListViewItem(["", "", ""])
                : new ListViewItem([en.Time.ToString("HH:mm:ss"), en.Address, en.Text])
                {
                    Tag = en,
                    ToolTipText = en.Address.Length > 0 && hosts.Get(en.Address) is string host ? host : "",
                };
        };
        logList.CellColor = (item, col) => col == 2 && item.Tag is LogEntry en ? KindColor(en.Kind) : null;
        logFilter.Box.TextChanged += (_, _) => { logFilterText = logFilter.Box.Text.Trim(); RebuildLogView(); };
        logSave.Click += (_, _) => SaveLog();
        logClear.Click += (_, _) => { logAll.Clear(); RebuildLogView(); };

        statsView.Font = Font;
        statsView.Tiles.ValueFont = tileFont;
        statsView.Tiles.LabelFont = smallFont;
        statsView.Tiles.Font = Font;
        statsView.Visitors.ContextMenuStrip = visitorMenu;
        statsReset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset all statistics (totals, download counts, visitors)?", "Modern HTTP File Server",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            traffic.ResetStats();
            lastOut = lastIn = 0;
            RefreshStats(force: true);
            Toast("Statistics reset");
        };

        banList.Font = Font;
        banList.AddColumn("Address", 130);
        banList.AddColumn("Reason", 200);
        banList.AddColumn("Until", 110);
        banList.AddColumn("Blocked", 70, HorizontalAlignment.Right);
        banList.ContextMenuStrip = banMenu;
        banList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { RemoveSelectedBans(); e.Handled = true; } };
        banInput.Committed += () => { if (AddBan(banInput.Box.Text)) banInput.Box.Clear(); };
        banAdd.Click += (_, _) => { if (AddBan(banInput.Box.Text)) banInput.Box.Clear(); };

        accountsView.Font = Font;
        accRemove.Font = Font;
        accRemove.Width = accRemove.PreferredWidth(76);
        accountsView.AddButton.Glyph = Glyph.Add;
        accountsView.Hint = "Adding a name that exists changes its password.";
        accountsView.List.ContextMenuStrip = accMenu;
        accountsView.List.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { RemoveSelectedAccounts(); e.Handled = true; } };
        accountsView.NameBox.Committed += () => accountsView.PasswordBox.Box.Focus();
        accountsView.PasswordBox.Committed += AddAccount;
        accountsView.AddButton.Click += (_, _) => AddAccount();
        accRemove.Click += (_, _) => RemoveSelectedAccounts();
        banRemove.Click += (_, _) => RemoveSelectedBans();
        sideCard.TabChanged += ShowTab;

        // connections
        connCard.Font = smallFont;
        connCard.TitleFont = cardFont;
        connList.Font = Font;
        connList.AddColumn("Address", 150);
        connList.AddColumn("Host", 190);
        connList.AddColumn("File", 300);
        connList.AddColumn("Status", 110);
        connList.AddColumn("Speed", 90, HorizontalAlignment.Right);
        connList.AddColumn("Time", 70, HorizontalAlignment.Right);
        connList.AddColumn("Progress", 170);
        connList.CellColor = ConnCellColor;
        connList.CustomCell = DrawProgressCell;
        connList.ContextMenuStrip = connMenu;
        connList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { KickSelected(); e.Handled = true; } };
        connCard.SetContent(connList);

        // split panes: (shared | side) over connections
        foreach (var sc in new[] { outer, inner })
        {
            sc.BackColor = Theme.Bg;
            sc.Panel1.BackColor = sc.Panel2.BackColor = Theme.Bg;
            sc.SplitterWidth = this.S(12);
            sc.TabStop = false;
        }
        outer.Orientation = Orientation.Horizontal;
        outer.FixedPanel = FixedPanel.Panel2;
        inner.Orientation = Orientation.Vertical;
        itemsCard.Dock = sideCard.Dock = connCard.Dock = DockStyle.Fill;
        inner.Panel1.Controls.Add(itemsCard);
        inner.Panel2.Controls.Add(sideCard);
        outer.Panel1.Controls.Add(inner);
        inner.Dock = DockStyle.Fill;
        outer.Panel2.Controls.Add(connCard);

        foreach (var c in new Control[] { this, list, dropPanel, itemsCard, sideCard, connCard, graphCard, outer, inner,
                     outer.Panel1, outer.Panel2, inner.Panel1, inner.Panel2 })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragOver;
            c.DragOver += OnDragOver;
            c.DragLeave += (_, _) => SetDragging(false);
            c.DragDrop += OnDragDrop;
        }

        Controls.AddRange([netButton, portBox, startButton, copyButton, openButton, graphCard, outer]);

        BuildMenus(out miRename, out miTrayToggle);

        tray.ContextMenuStrip = trayMenu;
        tray.Icon = trayOff;
        tray.Text = "Modern HTTP File Server";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };

        toastTimer.Tick += (_, _) => { toast = null; toastTimer.Stop(); Invalidate(statusBar); };
        tick.Tick += (_, _) => OnTick();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        server.SetItems(settings.Items);
        RefreshList();
        ShowTab(Math.Clamp(settings.SideTab, 0, 3));
        sideCard.Tab = Math.Clamp(settings.SideTab, 0, 3);
        UpdateUi();
    }

    void BuildMenus(out ToolStripMenuItem rename, out ToolStripMenuItem trayToggle)
    {
        var renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        foreach (var m in new[] { itemMenu, connMenu, visitorMenu, banMenu, accMenu, trayMenu, netMenu })
        {
            m.Renderer = renderer;
            m.Font = Font;
        }

        rename = Item("Rename", "F2", BeginRename);
        var miDownload = new ToolStripMenuItem("Who can download") { ForeColor = Theme.Text };
        foreach (var mi in new[] { miDlAnyone, miDlAccounts, miUpOff, miUpAnyone, miUpAccounts }) mi.ForeColor = Theme.Text;
        miUploads.ForeColor = Theme.Text;
        miDownload.DropDownItems.AddRange(new ToolStripItem[] { miDlAnyone, miDlAccounts });
        miUploads.DropDownItems.AddRange(new ToolStripItem[] { miUpOff, miUpAnyone, miUpAccounts });
        foreach (var dd in new[] { miDownload.DropDown, miUploads.DropDown }) { dd.Renderer = renderer; dd.Font = Font; }
        miDlAnyone.Click += (_, _) => SetDownload(Access.Anyone);
        miDlAccounts.Click += (_, _) => SetDownload(Access.Accounts);
        miUpOff.Click += (_, _) => SetUpload(UploadAccess.Off);
        miUpAnyone.Click += (_, _) => SetUpload(UploadAccess.Anyone);
        miUpAccounts.Click += (_, _) => SetUpload(UploadAccess.Accounts);
        itemMenu.Items.AddRange(new ToolStripItem[]
        {
            Item("Copy link", "Ctrl+C", CopySelectedLinks),
            Item("Open in browser", "Enter", () => { foreach (var i in SelectedItems) OpenUrl(ItemUrl(i)); }),
            Item("Show in Explorer", "", ShowInExplorer),
            new ToolStripSeparator(),
            miDownload,
            miUploads,
            new ToolStripSeparator(),
            rename,
            Item("Remove from list", "Del", RemoveSelected),
        });
        var renameItem = rename;
        itemMenu.Opening += (_, e) =>
        {
            var sel = SelectedItems;
            if (sel.Count == 0) { e.Cancel = true; return; }
            renameItem.Enabled = sel.Count == 1;
            miDlAnyone.Checked = sel.All(i => i.Download == Access.Anyone);
            miDlAccounts.Checked = sel.All(i => i.Download == Access.Accounts);
            var folders = sel.Where(i => i.IsFolder).ToList();
            miUploads.Enabled = folders.Count > 0;
            miUploads.Text = folders.Count > 0 ? "Uploads" : "Uploads (folders only)";
            miUpOff.Checked = folders.Count > 0 && folders.All(i => i.Upload == UploadAccess.Off);
            miUpAnyone.Checked = folders.Count > 0 && folders.All(i => i.Upload == UploadAccess.Anyone);
            miUpAccounts.Checked = folders.Count > 0 && folders.All(i => i.Upload == UploadAccess.Accounts);
        };

        accMenu.Items.Add(Item("Remove account", "Del", RemoveSelectedAccounts));
        accMenu.Opening += (_, e) => { if (accountsView.List.SelectedItems.Count == 0) e.Cancel = true; };

        connMenu.Items.AddRange(new ToolStripItem[]
        {
            Item("Kick", "Del", KickSelected),
            Item("Kick everyone from this address", "", () => KickAddresses(SelectedConns.Select(c => c.Address))),
            Item("Ban this address", "", () => { foreach (var a in SelectedConns.Select(c => c.Address).Distinct().ToList()) AddBan(a); }),
            new ToolStripSeparator(),
            Item("Copy address", "", () => CopyText(string.Join(Environment.NewLine, SelectedConns.Select(c => c.Address).Distinct()), "Address copied")),
        });
        connMenu.Opening += (_, e) => { if (connList.SelectedItems.Count == 0) e.Cancel = true; };

        visitorMenu.Items.AddRange(new ToolStripItem[]
        {
            Item("Ban this address", "", () => { foreach (var a in SelectedVisitors().ToList()) AddBan(a); }),
            Item("Copy address", "", () => CopyText(string.Join(Environment.NewLine, SelectedVisitors()), "Address copied")),
        });
        visitorMenu.Opening += (_, e) => { if (statsView.Visitors.SelectedItems.Count == 0) e.Cancel = true; };

        banMenu.Items.Add(Item("Unban", "Del", RemoveSelectedBans));
        banMenu.Opening += (_, e) => { if (banList.SelectedItems.Count == 0) e.Cancel = true; };

        var miOpen = Item("Open Modern HFS", "", RestoreFromTray);
        miOpen.Font = new Font(Font, FontStyle.Bold);
        trayToggle = Item("Stop server", "", () => _ = StartStop());
        trayMenu.Items.AddRange(new ToolStripItem[]
        {
            miOpen,
            Item("Copy share link", "", () => CopyText(BaseUrl, "Link copied")),
            trayToggle,
            new ToolStripSeparator(),
            Item("Exit", "", Close),
        });
        var toggle = trayToggle;
        trayMenu.Opening += (_, _) => toggle.Text = server.IsRunning ? "Stop server" : "Start server";
    }

    // ---- server --------------------------------------------------------------------------------

    int CurrentPort => server.IsRunning ? server.Port : settings.Port;
    string BaseUrl => $"http://{LinkHost}:{CurrentPort}/";

    string? BoundTo => string.IsNullOrEmpty(settings.ListenAddress) ? null : settings.ListenAddress;

    /// <summary>The address that goes into copied links.</summary>
    string LinkHost =>
        BoundTo == "127.0.0.1" ? "127.0.0.1"
        : settings.LinkInternet && publicIp != null ? publicIp
        : BoundTo ?? lanIp;
    bool PortEdited => server.IsRunning && portBox.TryGetPort(out int p) && p != server.Port;

    async Task StartStop()
    {
        if (busy) return;
        if (!server.IsRunning) { await StartServer(); return; }
        bool restart = PortEdited;
        busy = true;
        UpdateUi();
        try { await server.StopAsync(); }
        finally { busy = false; }
        traffic.Log("", "Server stopped", LogKind.Info);
        if (restart) await StartServer();
        else { settings.ServerOn = false; Save(); UpdateUi(); }
    }

    async Task StartServer()
    {
        if (!portBox.TryGetPort(out int port))
        {
            error = "Port must be a number from 1 to 65535";
            UpdateUi();
            return;
        }
        busy = true;
        error = null;
        settings.Port = port;
        UpdateUi();
        IPAddress? bind = null;
        if (BoundTo is string wanted)
        {
            bool present = wanted == "127.0.0.1" || Network.Adapters().Any(a => a.Address == wanted);
            if (present && IPAddress.TryParse(wanted, out var b)) bind = b;
            else
            {
                traffic.Log("", $"The network card with {wanted} isn't connected, so listening on all of them", LogKind.Warn);
                settings.ListenAddress = "";
            }
        }
        try
        {
            await server.StartAsync(port, bind);
            settings.ServerOn = true;
            var where = bind == null ? "all network cards" : bind.ToString() == "127.0.0.1" ? "this PC only" : $"{bind} only";
            traffic.Log("", $"Server started on port {port} ({where}), sharing {Plural(settings.Items.Count, "item")}", LogKind.Info);
        }
        catch (Exception ex)
        {
            error = Describe(ex, port);
            traffic.Log("", error, LogKind.Bad);
        }
        finally { busy = false; }
        Save();
        UpdateUi();
    }

    static string Describe(Exception ex, int port)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e.GetType().Name == "AddressInUseException" ||
                e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
                return $"Port {port} is already used by another program";
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied })
                return $"Windows won't let us use port {port}";
        }
        return "Couldn't start: " + ex.Message;
    }

    void UpdateUi()
    {
        if (busy) { startButton.Text = "..."; startButton.Glyph = Glyph.None; }
        else if (!server.IsRunning) { startButton.Text = "Start"; startButton.Glyph = Glyph.Power; }
        else if (PortEdited) { startButton.Text = "Restart"; startButton.Glyph = Glyph.Restart; }
        else { startButton.Text = "Stop"; startButton.Glyph = Glyph.Power; }
        startButton.Primary = !server.IsRunning || PortEdited;
        startButton.Enabled = !busy;
        tray.Icon = server.IsRunning ? trayOn : trayOff;
        UpdateTrayText();
        Invalidate();
    }

    void UpdateTrayText()
    {
        var text = server.IsRunning
            ? $"Modern HFS: port {server.Port}, {Plural(traffic.LiveCount, "connection")}"
            : "Modern HFS: stopped";
        if (tray.Text != text) tray.Text = text;
    }

    static string Plural(long n, string word) => n == 1 ? $"1 {word}" : $"{Fmt.Count(n)} {word}s";

    // ---- once a second: graph, connections, log, stats -----------------------------------------

    void OnTick()
    {
        ticks++;
        double dt = Math.Max(0.2, sampleClock.Elapsed.TotalSeconds);
        sampleClock.Restart();
        long o = traffic.TotalOut, i = traffic.TotalIn;
        speedOut = Math.Max(0, o - lastOut) / dt;
        speedIn = Math.Max(0, i - lastIn) / dt;
        lastOut = o;
        lastIn = i;
        graph.Push(speedOut, speedIn);
        traffic.NoteSpeed(speedOut);
        graphCard.Note = $"Out {Fmt.Speed(speedOut)}   In {Fmt.Speed(speedIn)}   Peak {Fmt.Speed(graph.Peak)}";

        UpdateConnections(dt);
        DrainLog();
        if (sideCard.Tab == 1 && ticks % 2 == 0) RefreshStats();
        if (sideCard.Tab == 2 && ticks % 5 == 0) RefreshBanCounts();
        if (sideCard.Tab == 3 && ticks % 5 == 0) RefreshAccounts();
        if (hosts.Version != hostsShown)
        {
            hostsShown = hosts.Version;
            logList.Invalidate();
            if (sideCard.Tab == 1) RefreshStats(force: true);
        }
        if (ticks % 1800 == 0) RefreshPublicIp();
        if (ticks % 60 == 0)
        {
            traffic.SaveStats();
            traffic.Guard.Prune();
            auth.Prune();
            hosts.Prune();
            ExpireBans();
        }
        UpdateTrayText();
        Invalidate(statusBar);
    }

    void UpdateConnections(double dt)
    {
        var live = traffic.Live.OrderBy(c => c.Connected).ToList();
        var ids = live.Select(c => c.Id).ToHashSet();
        bool changed = false;
        connList.BeginUpdate();
        foreach (var gone in connRows.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            connList.Items.Remove(connRows[gone]);
            connRows.Remove(gone);
            connSpeed.Remove(gone);
            changed = true;
        }
        foreach (var c in live)
        {
            if (!connRows.TryGetValue(c.Id, out var row))
            {
                row = new ListViewItem(["", "", "", "", "", "", ""]) { Tag = c };
                connRows[c.Id] = row;
                connList.Items.Add(row);
                changed = true;
            }
            long bytes = c.BytesOut + c.BytesIn;
            double speed = connSpeed.TryGetValue(c.Id, out var prev) ? Math.Max(0, bytes - prev.Bytes) / dt : 0;
            connSpeed[c.Id] = (bytes, speed);
            SetCell(row, 0, $"{c.Address}:{c.Port}");
            SetCell(row, 1, hosts.Get(c.Address) ?? "");
            SetCell(row, 2, c.File);
            SetCell(row, 3, c.Status);
            SetCell(row, 4, c.Busy && speed > 0 ? Fmt.Speed(speed) : "");
            SetCell(row, 5, Fmt.Duration(DateTime.Now - (c.Busy ? c.RequestStart : c.Connected)));
            double p = c.Progress;
            SetCell(row, 6, p >= 0 && c.Status is "Downloading" or "Downloaded" or "Interrupted" or "Uploading" or "Uploaded" or "Upload failed"
                ? ((int)(p * 100)).ToString() : "");
        }
        connList.EndUpdate();
        connCard.Badge = live.Count > 0 ? live.Count.ToString() : "";
        if (changed) connList.Invalidate();
    }

    static void SetCell(ListViewItem row, int col, string text)
    {
        if (row.SubItems[col].Text != text) row.SubItems[col].Text = text;
    }

    Color? ConnCellColor(ListViewItem item, int col)
    {
        if (col != 3 || item.Tag is not Conn c) return null;
        return c.Status switch
        {
            "Downloading" or "Uploading" => Theme.AccentText,
            "Downloaded" or "Uploaded" => Theme.Good,
            "Interrupted" or "Upload failed" => Theme.Warn,
            _ => null,
        };
    }

    bool DrawProgressCell(DrawListViewSubItemEventArgs e, Rectangle r, Color bg)
    {
        if (e.ColumnIndex != 6 || e.Item?.Tag is not Conn c) return false;
        var text = e.SubItem?.Text ?? "";
        if (text.Length == 0) return true;
        double p = c.Progress;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = this.S(10), textW = this.S(42), h = this.S(6);
        var track = new Rectangle(r.X + pad, r.Y + (r.Height - h) / 2, Math.Max(10, r.Width - 2 * pad - textW), h);
        Theme.FillRound(g, track, Theme.Hover, null, h / 2f);
        if (p > 0)
        {
            var done = track with { Width = Math.Max(h, (int)(track.Width * p)) };
            Theme.FillRound(g, done, c.Status is "Interrupted" or "Upload failed" ? Theme.Warn
                : c.Status is "Downloaded" or "Uploaded" ? Theme.Good : Theme.Accent, null, h / 2f);
        }
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, text + "%", Font, new Rectangle(track.Right + this.S(6), r.Y, textW, r.Height), Theme.SubText, bg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        return true;
    }

    List<Conn> SelectedConns => connList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<Conn>().ToList();

    void KickSelected()
    {
        var sel = SelectedConns;
        foreach (var c in sel)
        {
            c.Kick();
            traffic.Log(c.Address, $"Kicked by you{(c.Busy ? " during " + c.File : "")}", LogKind.Bad);
        }
        if (sel.Count > 0) Toast(sel.Count == 1 ? "Kicked" : $"Kicked {sel.Count} connections");
    }

    void KickAddresses(IEnumerable<string> addresses)
    {
        var set = addresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var c in traffic.Live.Where(c => set.Contains(c.Address)).ToList()) { c.Kick(); n++; }
        foreach (var a in set) traffic.Log(a, "Kicked by you (all connections)", LogKind.Bad);
        if (n > 0) Toast($"Kicked {Plural(n, "connection")}");
    }

    // ---- log -----------------------------------------------------------------------------------

    static Color KindColor(LogKind k) => k switch
    {
        LogKind.Info => Theme.AccentText,
        LogKind.Done => Theme.Good,
        LogKind.Warn => Theme.Warn,
        LogKind.Bad => Theme.Bad,
        LogKind.Browse => Theme.SubText,
        _ => Theme.Text,
    };

    bool LogMatches(LogEntry e) =>
        logFilterText.Length == 0 ||
        e.Text.Contains(logFilterText, StringComparison.OrdinalIgnoreCase) ||
        e.Address.Contains(logFilterText, StringComparison.OrdinalIgnoreCase) ||
        (e.Address.Length > 0 && (hosts.Get(e.Address)?.Contains(logFilterText, StringComparison.OrdinalIgnoreCase) ?? false));

    void DrainLog()
    {
        bool follow = logList.AtBottom();
        int added = 0;
        while (traffic.TryDequeue(out var e))
        {
            logAll.Add(e);
            if (LogMatches(e)) logView.Add(logAll.Count - 1);
            added++;
        }
        if (added == 0) return;
        if (logAll.Count > 20000)
        {
            logAll.RemoveRange(0, 5000);
            RebuildLogView();
        }
        else logList.VirtualListSize = logView.Count;
        if (follow && logView.Count > 0) try { logList.EnsureVisible(logView.Count - 1); } catch { }
        logList.Invalidate();
    }

    void RebuildLogView()
    {
        logView.Clear();
        for (int i = 0; i < logAll.Count; i++)
            if (LogMatches(logAll[i])) logView.Add(i);
        logList.VirtualListSize = logView.Count;
        if (logView.Count > 0) try { logList.EnsureVisible(logView.Count - 1); } catch { }
        logList.Invalidate();
    }

    void SaveLog()
    {
        using var dlg = new SaveFileDialog
        {
            FileName = $"ModernHFS log {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            Filter = "Text file|*.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllLines(dlg.FileName, logView.Select(i => logAll[i]).Select(e =>
                $"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.Address,-16}  {e.Text}"));
            Toast($"Saved {Plural(logView.Count, "line")}");
        }
        catch (Exception ex) { Toast("Couldn't save: " + ex.Message); }
    }

    // ---- side card tabs, statistics, bans -----------------------------------------------------

    void ShowTab(int t)
    {
        sideCard.SetContent(t switch { 0 => logList, 1 => statsView, 2 => banList, _ => accountsView });
        sideCard.SetActions(t switch
        {
            0 => [logFilter, logSave, logClear],
            1 => [statsReset],
            2 => [banInput, banAdd, banRemove],
            _ => [accRemove],
        });
        sideCard.Note = t == 1 ? "since " + traffic.ReadStats(s => s.Since).ToString("yyyy-MM-dd") : "";
        if (t == 1) RefreshStats(force: true);
        if (t == 2) RefreshBans();
        if (t == 3) RefreshAccounts();
        if (t == 0) try { if (logView.Count > 0) logList.EnsureVisible(logView.Count - 1); } catch { }
        settings.SideTab = t;
    }

    void RefreshStats(bool force = false)
    {
        long version = traffic.StatsVersion;
        statsView.Tiles.Tiles =
        [
            ("Visits", Fmt.Count(traffic.ReadStats(s => s.Hits))),
            ("Downloads", Fmt.Count(traffic.ReadStats(s => s.Downloads))),
            ("Uploads", Fmt.Count(traffic.ReadStats(s => s.Uploads))),
            ("Sent", Fmt.Size(traffic.TotalOut)),
            ("Received", Fmt.Size(traffic.TotalIn)),
            ("Visitors", Fmt.Count(traffic.ReadStats(s => s.Visitors.Count))),
            ("Top speed", Fmt.Speed(traffic.ReadStats(s => s.TopSpeed))),
        ];
        sideCard.Note = "since " + traffic.ReadStats(s => s.Since).ToString("yyyy-MM-dd");
        if (!force && version == statsShown) return;
        statsShown = version;

        var files = traffic.ReadStats(s => s.Files.OrderByDescending(f => f.Value.Downloads).ThenByDescending(f => f.Value.Last)
            .Take(300).Select(f => (Path: f.Key, f.Value.Downloads)).ToList());
        var visitors = traffic.ReadStats(s => s.Visitors.OrderByDescending(v => v.Value.Last).Take(500)
            .Select(v => (Address: v.Key, v.Value.Hits, v.Value.Downloads, v.Value.BytesOut, v.Value.Last)).ToList());

        Fill(statsView.Files, files.Select(f => new ListViewItem([f.Path, Fmt.Count(f.Downloads)]) { Name = f.Path }));
        Fill(statsView.Visitors, visitors.Select(v => new ListViewItem(
            [v.Address, hosts.Get(v.Address) ?? "", Fmt.Count(v.Hits), Fmt.Count(v.Downloads), Fmt.Size(v.BytesOut), Fmt.Date(v.Last)])
            { Name = v.Address }));
    }

    /// <summary>Replace a list's rows but keep the scroll position and selection.</summary>
    static void Fill(GridList grid, IEnumerable<ListViewItem> rows)
    {
        var selected = grid.SelectedItems.Cast<ListViewItem>().Select(i => i.Name).ToHashSet();
        int top = grid.TopItem?.Index ?? 0;
        grid.BeginUpdate();
        grid.Items.Clear();
        foreach (var r in rows)
        {
            r.Selected = selected.Contains(r.Name);
            grid.Items.Add(r);
        }
        if (grid.Items.Count > 0) try { grid.TopItem = grid.Items[Math.Min(top, grid.Items.Count - 1)]; } catch { }
        grid.EndUpdate();
    }

    IEnumerable<string> SelectedVisitors() =>
        statsView.Visitors.SelectedItems.Cast<ListViewItem>().Select(i => i.Name).Distinct();

    void RefreshBans() =>
        Fill(banList, settings.Bans.Where(b => !b.Expired).OrderByDescending(b => b.Added).Select(b => new ListViewItem(
            [b.Pattern, b.Reason, b.Expires is DateTime e ? Fmt.Date(e) : "forever", Fmt.Count(traffic.BlockedCount(b.Pattern))])
            { Name = b.Pattern }));

    void RefreshBanCounts()
    {
        foreach (ListViewItem row in banList.Items) SetCell(row, 3, Fmt.Count(traffic.BlockedCount(row.Name)));
    }

    void ExpireBans()
    {
        var gone = settings.Bans.Where(b => b.Expired).ToList();
        if (gone.Count == 0) return;
        settings.Bans.RemoveAll(b => b.Expired);
        Save();
        traffic.SetBans(settings.Bans);
        foreach (var b in gone)
        {
            traffic.Guard.Forget(b.Pattern);
            auth.Forget(b.Pattern);
            traffic.Log(b.Pattern, "Auto-ban expired", LogKind.Info);
        }
        if (sideCard.Tab == 2) RefreshBans();
    }

    static readonly TimeSpan AutoBanFor = TimeSpan.FromDays(7);

    /// <summary>Manual ban (forever) or, with <paramref name="autoReason"/>, an automatic 7-day ban.</summary>
    bool AddBan(string text, string? autoReason = null)
    {
        bool auto = autoReason != null;
        if (!BanRule.TryParse(text, out var rule))
        {
            Toast("Type an address like 203.0.113.5, or a range like 203.0.113.*");
            return false;
        }
        if (settings.Bans.Any(b => !b.Expired && b.Pattern.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase)))
        {
            if (!auto) Toast($"{rule.Pattern} is already banned");
            return false;
        }
        settings.Bans.RemoveAll(b => b.Pattern.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase));
        settings.Bans.Add(new BanEntry
        {
            Pattern = rule.Pattern,
            Added = DateTime.Now,
            Reason = auto ? Traffic.Clean(autoReason!, 120) : "Banned by you",
            Expires = auto ? DateTime.Now + AutoBanFor : null,
        });
        Save();
        traffic.SetBans(settings.Bans);
        int kicked = 0;
        foreach (var c in traffic.Live.Where(c => rule.Matches(c.Address)).ToList()) { c.Kick("banned"); kicked++; }
        var who = auto ? $"Auto-banned for 7 days: {autoReason}" : "Banned by you";
        traffic.Log(rule.Pattern, kicked > 0 ? $"{who}, {Plural(kicked, "connection")} kicked" : who, LogKind.Bad);
        RefreshBans();
        Toast(auto ? $"Auto-banned {rule.Pattern}: {autoReason}" : $"Banned {rule.Pattern}");
        return true;
    }

    void RemoveSelectedBans()
    {
        var names = banList.SelectedItems.Cast<ListViewItem>().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) { Toast("Select an address in the list first"); return; }
        settings.Bans.RemoveAll(b => names.Contains(b.Pattern));
        Save();
        traffic.SetBans(settings.Bans);
        foreach (var n in names)
        {
            traffic.Guard.Forget(n);
            auth.Forget(n);
            traffic.Log(n, "Unbanned", LogKind.Info);
        }
        RefreshBans();
        Toast(names.Count == 1 ? $"Unbanned {names.First()}" : $"Unbanned {names.Count} addresses");
    }

    // ---- accounts ------------------------------------------------------------------------------

    void RefreshAccounts() =>
        Fill(accountsView.List, settings.Accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => new ListViewItem(
            [a.Name, Fmt.Date(a.Created), auth.LastSeen(a.Name) is DateTime t ? Fmt.Date(t) : "never"]) { Name = a.Name }));

    void AddAccount()
    {
        var name = accountsView.NameBox.Box.Text.Trim();
        var password = accountsView.PasswordBox.Box.Text;
        if (name.Length == 0 || name.Contains(':') || name.Any(char.IsControl))
        {
            Toast("Type a name (no colons)");
            accountsView.NameBox.Box.Focus();
            return;
        }
        if (password.Length < 4)
        {
            Toast("Use a password of at least 4 characters");
            accountsView.PasswordBox.Box.Focus();
            return;
        }
        var existing = settings.Accounts.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var account = Account.Create(name, password);
        if (existing >= 0)
        {
            account.Created = settings.Accounts[existing].Created;
            settings.Accounts[existing] = account;
        }
        else settings.Accounts.Add(account);
        Save();
        auth.SetAccounts(settings.Accounts);
        traffic.Log("", existing >= 0 ? $"Changed the password of \"{name}\"" : $"Added account \"{name}\"", LogKind.Info);
        accountsView.NameBox.Box.Clear();
        accountsView.PasswordBox.Box.Clear();
        accountsView.NameBox.Box.Focus();
        RefreshAccounts();
        Toast(existing >= 0 ? $"Password changed for {name}" : $"Account \"{name}\" added");
    }

    void RemoveSelectedAccounts()
    {
        var names = accountsView.List.SelectedItems.Cast<ListViewItem>().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) { Toast("Select an account in the list first"); return; }
        settings.Accounts.RemoveAll(a => names.Contains(a.Name));
        Save();
        auth.SetAccounts(settings.Accounts);
        foreach (var n in names) traffic.Log("", $"Removed account \"{n}\"", LogKind.Info);
        RefreshAccounts();
        Toast(names.Count == 1 ? $"Removed {names.First()}" : $"Removed {names.Count} accounts");
    }

    // ---- per-item rules ------------------------------------------------------------------------

    void SetDownload(Access access)
    {
        var sel = SelectedItems;
        foreach (var i in sel) i.Download = access;
        Changed();
        foreach (var i in sel)
            traffic.Log("", $"/{i.Name}{(i.IsFolder ? "/" : "")}: {(access == Access.Anyone ? "anyone can download" : "accounts only")}", LogKind.Info);
        if (access == Access.Accounts && settings.Accounts.Count == 0) NeedAccounts();
        else Toast(access == Access.Anyone ? "Anyone with the link can download it" : "Only people with an account can open it now");
    }

    void SetUpload(UploadAccess upload)
    {
        var folders = SelectedItems.Where(i => i.IsFolder).ToList();
        foreach (var i in folders) i.Upload = upload;
        Changed();
        foreach (var i in folders)
            traffic.Log("", $"/{i.Name}/: uploads {upload switch { UploadAccess.Off => "off", UploadAccess.Anyone => "open to anyone", _ => "for accounts" }}", LogKind.Info);
        if (upload == UploadAccess.Accounts && settings.Accounts.Count == 0) NeedAccounts();
        else Toast(upload switch
        {
            UploadAccess.Off => "Uploads turned off",
            UploadAccess.Anyone => "Anyone with the link can upload here now, no password",
            _ => "People with an account can upload here now",
        });
    }

    void NeedAccounts()
    {
        sideCard.Tab = 3;
        accountsView.NameBox.Box.Focus();
        Toast("Add an account so someone can sign in");
    }

    // ---- shared items --------------------------------------------------------------------------

    List<SharedItem> SelectedItems =>
        list.SelectedItems.Cast<ListViewItem>().Select(i => (SharedItem)i.Tag!).ToList();

    string ItemUrl(SharedItem i) => BaseUrl + Uri.EscapeDataString(i.Name) + (i.IsFolder ? "/" : "");

    void AddPaths(IEnumerable<string> paths)
    {
        var added = new List<SharedItem>();
        foreach (var raw in paths)
        {
            string p;
            try { p = Path.GetFullPath(raw); } catch { continue; }
            bool dir = Directory.Exists(p);
            if (!dir && !File.Exists(p)) continue;
            if (settings.Items.Any(i => string.Equals(Path.TrimEndingDirectorySeparator(i.Path),
                    Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase)))
                continue;
            var item = new SharedItem { Name = UniqueName(DefaultName(p)), Path = p, IsFolder = dir };
            settings.Items.Add(item);
            added.Add(item);
        }
        if (added.Count == 0) return;
        Changed();
        list.SelectedItems.Clear();
        foreach (ListViewItem lvi in list.Items)
            if (added.Contains((SharedItem)lvi.Tag!)) lvi.Selected = true;
        foreach (var a in added) traffic.Log("", $"Now sharing /{a.Name}{(a.IsFolder ? "/" : "")}", LogKind.Info);
        Toast(added.Count == 1 ? $"Sharing \"{added[0].Name}\"" : $"Sharing {added.Count} new items");
    }

    static string DefaultName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(name)) name = path.TrimEnd('\\', ':') + " drive";   // "D:\" -> "D drive"
        return CleanName(name) is { Length: > 0 } n ? n : "item";
    }

    static string CleanName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Where(ch => !bad.Contains(ch)).ToArray());
        return s.Trim().TrimEnd('.', ' ');
    }

    string UniqueName(string name, SharedItem? except = null)
    {
        bool Taken(string n) => settings.Items.Any(i => i != except && i.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                                || n.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase);
        if (!Taken(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Taken(candidate)) return candidate;
        }
    }

    void RemoveSelected()
    {
        var gone = SelectedItems;
        if (gone.Count == 0) return;
        foreach (var i in gone) settings.Items.Remove(i);
        Changed();
        foreach (var g in gone) traffic.Log("", $"Stopped sharing /{g.Name}{(g.IsFolder ? "/" : "")}", LogKind.Info);
        Toast(gone.Count == 1 ? $"Stopped sharing \"{gone[0].Name}\"" : $"Stopped sharing {gone.Count} items");
    }

    void Changed()
    {
        server.SetItems(settings.Items);
        Save();
        RefreshList();
    }

    void RefreshList()
    {
        var keep = SelectedItems.ToHashSet();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var item in settings.Items)
            list.Items.Add(new ListViewItem(item.Name) { Tag = item, Selected = keep.Contains(item) });
        list.EndUpdate();
        hover = -1;
        itemsCard.SetContent(settings.Items.Count == 0 ? dropPanel : list);
        itemsCard.Badge = settings.Items.Count > 0 ? settings.Items.Count.ToString() : "";
    }

    void CopySelectedLinks()
    {
        var links = SelectedItems.Select(ItemUrl).ToList();
        if (links.Count > 0)
            CopyText(string.Join(Environment.NewLine, links), links.Count == 1 ? "Link copied" : $"{links.Count} links copied");
    }

    void ShowInExplorer()
    {
        foreach (var i in SelectedItems.Take(5))
        {
            try
            {
                if (i.Exists) Process.Start("explorer.exe", $"/select,\"{i.Path}\"");
                else Toast($"\"{i.Name}\" is missing on disk");
            }
            catch { }
        }
    }

    // ---- rename (inline box over the row) ------------------------------------------------------

    void BeginRename()
    {
        if (list.SelectedItems.Count != 1) return;
        var lvi = list.SelectedItems[0];
        var item = (SharedItem)lvi.Tag!;
        lvi.EnsureVisible();
        var r = lvi.Bounds;
        int x = this.S(16) + this.S(32) + this.S(14) - this.S(4);
        var box = new TextBox
        {
            Font = nameFont,
            Text = item.Name,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
        };
        box.Bounds = new Rectangle(x, r.Y + this.S(5), Math.Max(this.S(140), list.ClientSize.Width - x - this.S(110)), box.PreferredHeight);
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            var text = box.Text;
            renameBox = null;
            list.Controls.Remove(box);
            box.Dispose();
            list.Focus();
            if (commit) CommitRename(item, text);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Finish(true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(false); }
        };
        box.LostFocus += (_, _) => BeginInvoke(() => Finish(true));
        renameBox = box;
        list.Controls.Add(box);
        box.Focus();
        box.SelectAll();
    }

    void CommitRename(SharedItem item, string text)
    {
        var name = CleanName(text);
        if (name.Length == 0 || name == item.Name) return;
        if (settings.Items.Any(i => i != item && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Toast($"\"{name}\" is already taken");
            return;
        }
        traffic.Log("", $"Renamed /{item.Name} to /{name}", LogKind.Info);
        item.Name = name;
        Changed();
    }

    // ---- layout + drawing ----------------------------------------------------------------------

    void LayoutAll()
    {
        if (ClientSize.Width < 300 || ClientSize.Height < 300) return;
        int m = this.S(18), W = ClientSize.Width, H = ClientSize.Height;
        header = new Rectangle(m, m - this.S(6), W - 2 * m, this.S(48));
        int bh = this.S(32);
        startButton.Bounds = new Rectangle(header.Right - this.S(100), header.Y + (header.Height - bh) / 2, this.S(100), bh);
        portBox.Bounds = new Rectangle(startButton.Left - this.S(8) - this.S(76), startButton.Top, this.S(76), bh);
        int capW = TextRenderer.MeasureText("Port", Font).Width;
        netButton.Bounds = new Rectangle(portBox.Left - this.S(8) - capW - this.S(12) - bh, startButton.Top, bh, bh);

        int rowTop = header.Bottom + this.S(6), rowH = this.S(96), gap = this.S(12);
        int shareW = Math.Min(this.S(520), (W - 2 * m - gap) * 45 / 100);
        addrCard = new Rectangle(m, rowTop, shareW, rowH);
        int ab = this.S(32);
        int by = addrCard.Bottom - this.S(12) - ab;
        copyButton.Bounds = new Rectangle(addrCard.X + this.S(12), by, copyButton.Width, ab);
        openButton.Bounds = new Rectangle(copyButton.Right + this.S(6), by, openButton.Width, ab);
        addrText = new Rectangle(addrCard.X + this.S(16), addrCard.Y + this.S(26), addrCard.Width - this.S(32), this.S(26));
        graphCard.Bounds = new Rectangle(addrCard.Right + gap, rowTop, W - m - (addrCard.Right + gap), rowH);

        statusBar = new Rectangle(m, H - this.S(10) - this.S(20), W - 2 * m, this.S(20));
        int top = addrCard.Bottom + gap;
        outer.Bounds = new Rectangle(m, top, W - 2 * m, Math.Max(this.S(200), statusBar.Top - this.S(6) - top));
        Invalidate();
    }

    void ApplyPaneSizes()
    {
        try
        {
            outer.Panel1MinSize = this.S(200);
            outer.Panel2MinSize = this.S(110);
            inner.Panel1MinSize = this.S(260);
            inner.Panel2MinSize = this.S(320);
            int connH = settings.ConnectionsHeight > 0 ? settings.ConnectionsHeight : this.S(210);
            outer.SplitterDistance = Math.Clamp(outer.Height - outer.SplitterWidth - connH,
                outer.Panel1MinSize, Math.Max(outer.Panel1MinSize, outer.Height - outer.SplitterWidth - outer.Panel2MinSize));
            int leftW = settings.SharedWidth > 0 ? settings.SharedWidth : inner.Width * 40 / 100;
            inner.SplitterDistance = Math.Clamp(leftW, inner.Panel1MinSize,
                Math.Max(inner.Panel1MinSize, inner.Width - inner.SplitterWidth - inner.Panel2MinSize));
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags one = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        // app icon + title + status pill, all on one line
        int cy = header.Y + header.Height / 2;
        g.DrawImage(headerIcon, header.X, cy - headerIcon.Height / 2);
        int tx = header.X + headerIcon.Width + this.S(10);
        var titleSize = TextRenderer.MeasureText("Modern HTTP File Server", titleFont);
        TextRenderer.DrawText(g, "Modern HTTP File Server", titleFont, new Point(tx, cy - titleSize.Height / 2), Theme.Text, one);
        var (dot, status) = Status();
        var statusSize = TextRenderer.MeasureText(status, smallFont);
        int pillH = this.S(24), maxPill = portBox.Left - this.S(60) - (tx + titleSize.Width + this.S(12));
        var pill = new Rectangle(tx + titleSize.Width + this.S(12), cy - pillH / 2,
            Math.Min(maxPill, statusSize.Width + this.S(30)), pillH);
        if (pill.Width > this.S(40))
        {
            var pillBg = Theme.Mix(Theme.Bg, dot, Theme.Dark ? 0.16f : 0.12f);
            Theme.FillRound(g, pill, pillBg, null, pillH / 2f);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, pill.X + this.S(10), cy - this.S(4), this.S(8), this.S(8));
            TextRenderer.DrawText(g, status, smallFont, new Rectangle(pill.X + this.S(22), pill.Y, pill.Width - this.S(28), pill.Height),
                error != null ? Theme.Bad : Theme.Text, pillBg, one | TextFormatFlags.VerticalCenter);
        }
        var cap = TextRenderer.MeasureText("Port", Font);
        TextRenderer.DrawText(g, "Port", Font, new Point(portBox.Left - this.S(8) - cap.Width, portBox.Top + (portBox.Height - cap.Height) / 2), Theme.SubText);

        // share-link card
        Theme.FillRound(g, addrCard, Theme.Surface, Theme.Border, this.S(8));
        string caption = !server.IsRunning ? "Share link (server is stopped)"
            : BoundTo == "127.0.0.1" ? "Share link (this PC only)"
            : settings.LinkInternet && publicIp == null ? "Share link (finding your Internet address...)"
            : settings.LinkInternet ? "Share link for friends anywhere" : "Share link for your home network";
        DrawLinkToggle(g);
        TextRenderer.DrawText(g, caption, smallFont,
            new Rectangle(addrCard.X + this.S(16), addrCard.Y + this.S(8), toggleInternet.Left - addrCard.X - this.S(24), this.S(20)),
            Theme.SubText, Theme.Surface, one | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, BaseUrl, addrFont, addrText, server.IsRunning ? Theme.AccentText : Theme.SubText, Theme.Surface,
            one | TextFormatFlags.VerticalCenter);

        // status bar
        var stats = $"Connections {traffic.LiveCount}     Out {Fmt.Speed(speedOut)}     In {Fmt.Speed(speedIn)}" +
                    $"     Total out {Fmt.Size(traffic.TotalOut)}     Total in {Fmt.Size(traffic.TotalIn)}";
        var statsSize = TextRenderer.MeasureText(stats, smallFont);
        TextRenderer.DrawText(g, stats, smallFont, statusBar, Theme.SubText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | one);
        var hint = toast ?? "Drag files or folders in to share them. Double-click copies a link, right-click for more.";
        var hintRect = statusBar with { Width = Math.Max(0, statusBar.Width - statsSize.Width - this.S(20)) };
        TextRenderer.DrawText(g, hint, smallFont, hintRect, toast != null ? Theme.AccentText : Theme.SubText, TextFormatFlags.VerticalCenter | one);
    }

    (Color, string) Status()
    {
        if (busy) return (Theme.Warn, server.IsRunning ? "Stopping..." : "Starting...");
        if (error != null) return (Theme.Bad, error);
        if (server.IsRunning) return (Theme.Good, $"Running \u00B7 port {server.Port}");
        return (Theme.SubText, "Stopped");
    }

    void DrawRow(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.Item.Tag is not SharedItem item) return;
        var g = e.Graphics;
        var r = new Rectangle(e.Bounds.X, e.Bounds.Y, list.ClientSize.Width, e.Bounds.Height);
        bool sel = e.Item.Selected;
        var bg = sel ? Theme.Selected : e.ItemIndex == hover ? Theme.Hover : Theme.Surface;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        if (sel) GridList.SelectionBar(g, list, r);
        if (!sel && e.ItemIndex < list.Items.Count - 1 && e.ItemIndex + 1 != hover && e.ItemIndex != hover)
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.X + this.S(14), r.Bottom - 1, r.Right - this.S(14), r.Bottom - 1);

        int pad = this.S(14), isz = this.S(32);
        if (ShellIcons.Get(item.Path, item.IsFolder) is Icon icon)
            g.DrawIcon(icon, new Rectangle(r.X + pad, r.Y + (r.Height - isz) / 2, isz, isz));

        bool exists = item.Exists;
        string right;
        if (!exists) right = "Missing";
        else if (item.IsFolder) right = "Folder";
        else { try { right = Fmt.Size(new FileInfo(item.Path).Length); } catch { right = ""; } }
        var rightSize = TextRenderer.MeasureText(right, smallFont);
        int rx = r.Right - pad - rightSize.Width;
        TextRenderer.DrawText(g, right, smallFont, new Point(rx, r.Y + (r.Height - rightSize.Height) / 2), exists ? Theme.SubText : Theme.Bad, bg);

        // rule badges, right to left: "Accounts only", "Upload: anyone" / "Upload: accounts"
        var pills = new List<(string Text, Color Color)>();
        if (item.Download == Access.Accounts) pills.Add(("Accounts only", Theme.Accent));
        if (item.IsFolder && item.Upload == UploadAccess.Anyone) pills.Add(("Upload: anyone", Theme.Warn));
        if (item.IsFolder && item.Upload == UploadAccess.Accounts) pills.Add(("Upload: accounts", Theme.Good));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int ph = this.S(20);
        foreach (var (text, color) in pills.AsEnumerable().Reverse())
        {
            var ts = TextRenderer.MeasureText(text, smallFont);
            var pr = new Rectangle(rx - this.S(10) - ts.Width - this.S(12), r.Y + (r.Height - ph) / 2, ts.Width + this.S(12), ph);
            if (pr.Left < r.X + pad + isz + this.S(120)) break;   // not enough room: the name wins
            var pillBg = Color.FromArgb(Theme.Dark ? 40 : 28, color);
            Theme.FillRound(g, pr, Blend(bg, pillBg), null, ph / 2f);
            TextRenderer.DrawText(g, text, smallFont, pr, color, Blend(bg, pillBg),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            rx = pr.Left;
        }
        g.SmoothingMode = SmoothingMode.None;

        int x = r.X + pad + isz + this.S(14);
        int w = Math.Max(10, rx - this.S(12) - x);
        const TextFormatFlags one = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, item.Name, nameFont, new Rectangle(x, r.Y + this.S(7), w, this.S(22)),
            exists ? Theme.Text : Theme.SubText, bg, one | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, item.Path, smallFont, new Rectangle(x, r.Y + this.S(29), w, this.S(18)),
            Theme.SubText, bg, one | TextFormatFlags.PathEllipsis);
    }

    static Color Blend(Color under, Color over)
    {
        float a = over.A / 255f;
        return Color.FromArgb((int)(under.R + (over.R - under.R) * a), (int)(under.G + (over.G - under.G) * a), (int)(under.B + (over.B - under.B) * a));
    }

    void SetHover(int index)
    {
        if (index == hover) return;
        int old = hover;
        hover = index;
        if (old >= 0 && old < list.Items.Count) list.Invalidate(list.Items[old].Bounds);
        if (index >= 0 && index < list.Items.Count) list.Invalidate(list.Items[index].Bounds);
    }

    // ---- input ---------------------------------------------------------------------------------

    void ListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Delete: RemoveSelected(); break;
            case Keys.F2: BeginRename(); break;
            case Keys.Enter: foreach (var i in SelectedItems) OpenUrl(ItemUrl(i)); break;
            case Keys.C when e.Control: CopySelectedLinks(); break;
            case Keys.A when e.Control: foreach (ListViewItem i in list.Items) i.Selected = true; break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        bool files = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = files ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragging(files);
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        SetDragging(false);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    void SetDragging(bool on)
    {
        if (dragging == on) return;
        dragging = on;
        dropPanel.Highlight = on;
        itemsCard.Highlight = on;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = AddressHit(e.Location) || toggleInternet.Contains(e.Location) || toggleLan.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && AddressHit(e.Location)) OpenUrl(BaseUrl);
        if (e.Button == MouseButtons.Left && toggleInternet.Contains(e.Location)) SetLinkInternet(true);
        if (e.Button == MouseButtons.Left && toggleLan.Contains(e.Location)) SetLinkInternet(false);
    }

    void DrawLinkToggle(Graphics g)
    {
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        int h = this.S(24), pad = this.S(2);
        int wi = TextRenderer.MeasureText("Internet", smallFont).Width + this.S(16);
        int wl = TextRenderer.MeasureText("LAN", smallFont).Width + this.S(16);
        var track = new Rectangle(addrCard.Right - this.S(12) - wi - wl - 2 * pad, addrCard.Y + this.S(8), wi + wl + 2 * pad, h);
        toggleInternet = new Rectangle(track.X + pad, track.Y + pad, wi, h - 2 * pad);
        toggleLan = new Rectangle(toggleInternet.Right, track.Y + pad, wl, h - 2 * pad);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, track, Theme.HeaderBg, Theme.Border, this.S(6));
        var on = settings.LinkInternet ? toggleInternet : toggleLan;
        Theme.FillRound(g, on, Theme.Surface, Theme.BorderStrong, this.S(5));
        g.SmoothingMode = mode;
        TextRenderer.DrawText(g, "Internet", smallFont, toggleInternet, settings.LinkInternet ? Theme.Text : Theme.SubText,
            settings.LinkInternet ? Theme.Surface : Theme.HeaderBg, flags);
        TextRenderer.DrawText(g, "LAN", smallFont, toggleLan, settings.LinkInternet ? Theme.SubText : Theme.Text,
            settings.LinkInternet ? Theme.HeaderBg : Theme.Surface, flags);
    }

    void SetLinkInternet(bool internet)
    {
        if (settings.LinkInternet == internet) return;
        settings.LinkInternet = internet;
        Save();
        Invalidate();
        if (internet && publicIp == null) RefreshPublicIp();
        Toast(internet ? "Links now use your Internet address" : "Links now use your home-network address");
    }

    async void RefreshPublicIp(bool announce = false)
    {
        var ip = await Network.PublicIPv4Async();
        if (closing) return;
        if (ip != null && ip != publicIp)
        {
            publicIp = ip;
            traffic.Log("", $"Internet address: {ip}", LogKind.Info);
        }
        if (announce) Toast(ip != null ? $"Internet address: {ip}" : "Couldn't find the Internet address (no connection?)");
        Invalidate();
    }

    void BuildNetMenu()
    {
        netMenu.Items.Clear();
        var title = new ToolStripMenuItem("Listen on") { Enabled = false };
        netMenu.Items.Add(title);
        void Choice(string text, string address)
        {
            var mi = new ToolStripMenuItem(text) { Checked = settings.ListenAddress == address, ForeColor = Theme.Text };
            mi.Click += (_, _) => _ = SetListen(address);
            netMenu.Items.Add(mi);
        }
        Choice("All network cards", "");
        foreach (var a in Network.Adapters()) Choice($"{a.Name}  \u00B7  {a.Address}", a.Address);
        Choice("This PC only  \u00B7  127.0.0.1", "127.0.0.1");
        netMenu.Items.Add(new ToolStripSeparator());
        netMenu.Items.Add(Item(publicIp != null ? $"Find Internet address again ({publicIp})" : "Find Internet address", "", () => RefreshPublicIp(announce: true)));
    }

    async Task SetListen(string address)
    {
        if (settings.ListenAddress == address) return;
        settings.ListenAddress = address;
        Save();
        if (server.IsRunning && !busy)
        {
            busy = true;
            UpdateUi();
            try { await server.StopAsync(); } finally { busy = false; }
            await StartServer();
        }
        Toast(address == "" ? "Listening on all network cards" : address == "127.0.0.1" ? "Only this PC can connect now" : $"Listening on {address} only");
        Invalidate();
    }

    bool AddressHit(Point p) =>
        addrText.Contains(p) && p.X < addrText.X + TextRenderer.MeasureText(BaseUrl, addrFont).Width;

    // ---- helpers -------------------------------------------------------------------------------

    ToolStripMenuItem Item(string text, string keys, Action action) =>
        new(text, null, (_, _) => action()) { ShortcutKeyDisplayString = keys, ForeColor = Theme.Text };

    void CopyText(string text, string message)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Toast(message); }
        catch { Toast("Couldn't reach the clipboard, try again"); }
    }

    void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void Toast(string message)
    {
        toast = message;
        toastTimer.Stop();
        toastTimer.Start();
        Invalidate(statusBar);
    }

    void Save()
    {
        try { settings.Save(); }
        catch (Exception ex) { Toast("Couldn't save settings: " + ex.Message); }
    }

    void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !closing)
            BeginInvoke(() => { lanIp = Net.LanIPv4() ?? "127.0.0.1"; RefreshPublicIp(); Invalidate(); });
    }

    // ---- window / tray -------------------------------------------------------------------------

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void RestoreWindow()
    {
        if (settings.Window is not { Width: > 0, Height: > 0 } w) return;
        var bounds = new Rectangle(w.X, w.Y, Math.Max(w.Width, MinimumSize.Width), Math.Max(w.Height, MinimumSize.Height));
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (w.Maximized) WindowState = FormWindowState.Maximized;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(Handle);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutAll();
        ApplyPaneSizes();
        tick.Start();
        RefreshPublicIp();
        if (settings.ServerOn) await StartServer();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && !closing && Visible)
        {
            Hide();
            if (!settings.TrayTipShown)
            {
                tray.ShowBalloonTip(4000, "Still sharing", "Modern HTTP File Server keeps running in the tray. Click the icon to bring it back.", ToolTipIcon.None);
                settings.TrayTipShown = true;
                Save();
            }
            return;
        }
        LayoutAll();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        list.SmallImageList = new ImageList { ImageSize = new Size(1, this.S(54)) };
        LayoutAll();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true;
        tick.Stop();
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.Window = new WindowPlacement
        {
            X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
            Maximized = WindowState == FormWindowState.Maximized,
        };
        if (WindowState != FormWindowState.Minimized)
        {
            settings.ConnectionsHeight = outer.Panel2.Height;
            settings.SharedWidth = inner.SplitterDistance;
        }
        Save();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        tray.Visible = false;
        try { Task.Run(server.StopAsync).Wait(3000); } catch { }
        traffic.SaveStats(wait: true);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            tick.Dispose();
            toastTimer.Dispose();
            renameBox?.Dispose();
        }
        base.Dispose(disposing);
    }
}
