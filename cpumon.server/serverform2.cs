using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

sealed class ServerForm2 : Form
{
    readonly ServerEngine _engine;
    readonly ServerDashboardController _dashboard;
    readonly IServerPlatformServices _platform;
    readonly Timer _tm;

    readonly Label _tokenLabel = new() { AutoSize = true, ForeColor = Th.Brt, Font = new Font("Consolas", 10f) };
    readonly Label _statusLabel = new() { AutoSize = true, ForeColor = Th.Dim, Font = new Font("Segoe UI", 8f) };
    readonly Button _regenBtn = new() { Text = "Regenerate", AutoSize = true };
    readonly Button _copyBtn = new() { Text = "Copy", AutoSize = true };
    readonly Button _osFilterBtn = new() { Text = "OS: all", AutoSize = true };
    readonly Button _sortBtn = new() { Text = "Sort: name", AutoSize = true };
    readonly Button _alertsBtn = new() { Text = "Alerts", AutoSize = true };
    readonly Button _approvedBtn = new() { Text = "Approved Clients", AutoSize = true };

    readonly ListView _clients = MakeList("Name", "OS", "Version", "Load %", "Temp", "RAM %", "State");
    readonly ListView _pending = MakeList("Machine", "IP", "Version", "Requested");
    readonly ListView _offline = MakeList("Machine", "Seen", "IP", "MAC");
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Th.TBg, ForeColor = Th.Brt, Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.FixedSingle };

    readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(10, 8, 10, 8), BackColor = Th.TBg, AutoScroll = true };
    string? _selectedClient;
    string? _selectedOffline;
    string? _selectedPending;
    string _lastActionKey = "";

    public ServerForm2(bool noBroadcast)
    {
        Text = $"CPU Monitor — Server (NEW UI)  v{Proto.AppVersion}";
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Th.Bg;
        ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);
        Icon = Th.MakeHexIcon(Th.Grn);

        _engine = new ServerEngine(noBroadcast);
        _platform = new WinFormsServerPlatformServices(this, _engine, Refresh);
        _dashboard = new ServerDashboardController(_engine, _platform);

        BuildLayout();
        WireEvents();

        _tm = new Timer { Interval = 500 };
        _tm.Tick += (_, _) => Refresh();

        Load += (_, _) => { _engine.Start(); _tm.Start(); Refresh(); };
        FormClosed += (_, _) => { _tm.Stop(); _tm.Dispose(); _engine.Dispose(); };
    }

    void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Th.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(10, 8, 10, 8), BackColor = Th.TBg };
        var tokWrap = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 2, 18, 0) };
        tokWrap.Controls.Add(_tokenLabel);
        tokWrap.Controls.Add(_statusLabel);
        top.Controls.Add(tokWrap);
        foreach (var b in new[] { _regenBtn, _copyBtn, _osFilterBtn, _sortBtn, _alertsBtn, _approvedBtn })
        { StyleBtn(b); top.Controls.Add(b); }

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, BackColor = Th.Bg };
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        middle.Controls.Add(WrapList("Connected clients", _clients), 0, 0);
        middle.Controls.Add(WrapList("Actions", _actions), 1, 0);
        middle.Controls.Add(WrapList("Pending approvals", _pending), 0, 1);
        middle.Controls.Add(WrapList("Offline", _offline), 1, 1);

        var logWrap = WrapList("Log", _log);
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(middle, 0, 1);
        root.Controls.Add(logWrap, 0, 2);
        Controls.Add(root);
    }

    static Control WrapList(string title, Control inner)
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(6), BackColor = Th.Bg };
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        p.Controls.Add(new Label { Text = title, ForeColor = Th.Dim, AutoSize = true, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), Margin = new Padding(2, 3, 0, 0) }, 0, 0);
        inner.Dock = DockStyle.Fill;
        inner.Margin = new Padding(0);
        p.Controls.Add(inner, 0, 1);
        return p;
    }

    static ListView MakeList(params string[] columns)
    {
        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            BackColor = Th.TBg,
            ForeColor = Th.Brt,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9f)
        };
        foreach (var c in columns) lv.Columns.Add(c, 110);
        return lv;
    }

    static void StyleBtn(Button b)
    {
        StyleButton(b, Th.Blu, subtle: true);
        b.Margin = new Padding(4, 6, 4, 4);
        b.MinimumSize = new Size(0, 30);
    }

    void WireEvents()
    {
        _regenBtn.Click += (_, _) => { _dashboard.RegenerateToken(); Refresh(); };
        _copyBtn.Click += (_, _) => _dashboard.CopyToken();
        _osFilterBtn.Click += (_, _) => { _dashboard.CycleOsFilter(); Refresh(); };
        _sortBtn.Click += (_, _) => { _dashboard.CycleSortMode(); Refresh(); };
        _alertsBtn.Click += (_, _) => _dashboard.ShowAlerts();
        _approvedBtn.Click += (_, _) => _dashboard.ShowApprovedClients();

        _clients.SelectedIndexChanged += (_, _) =>
        {
            _selectedClient = _clients.SelectedItems.Count > 0 ? _clients.SelectedItems[0].Tag as string : null;
            RebuildActionsIfChanged();
        };
        _pending.SelectedIndexChanged += (_, _) =>
        {
            _selectedPending = _pending.SelectedItems.Count > 0 ? _pending.SelectedItems[0].Tag as string : null;
            RebuildActionsIfChanged();
        };
        _offline.SelectedIndexChanged += (_, _) =>
        {
            _selectedOffline = _offline.SelectedItems.Count > 0 ? _offline.SelectedItems[0].Tag as string : null;
            RebuildActionsIfChanged();
        };

        _engine.SysInfoReceived += cl => _platform.ShowSysInfoDialog(cl);
        _engine.ServicesReceived += cl => _platform.ShowServicesDialog(cl);
        _engine.EventsReceived += cl => _platform.ShowEventsDialog(cl);
        _engine.ProcessListReceived += cl => _platform.UpdateProcessDialog(cl);
        _engine.CpuDetailReceived += (cl, d) => _platform.ShowCpuDetailDialog(cl.MachineName, d);
        _engine.ScreenshotReceived += (cl, s) => _platform.ShowScreenshotDialog(cl.MachineName, s);
        _engine.UpdateAvailable += () => BeginInvoke(Refresh);
        _engine.ReleaseStaged += () => BeginInvoke(Refresh);
    }

    public override void Refresh()
    {
        if (IsDisposed || !IsHandleCreated) return;
        var state = _dashboard.GetState();
        _tokenLabel.Text = state.Token;
        _statusLabel.Text = $"Authenticated: {state.AuthenticatedClientCount}  ·  Connections: {state.ConnectionCount}  ·  Pending: {state.PendingApprovals.Count}  ·  Offline: {state.OfflineClients.Count}";
        _osFilterBtn.Text = "OS: " + state.OsFilter;
        _sortBtn.Text = "Sort: " + state.SortMode;
        if (state.AvailableUpdate != null) Text = $"CPU Monitor — Server (NEW UI)  v{Proto.AppVersion}  ·  ↑ v{state.AvailableUpdate.Version} available";

        SyncList(_clients, state.Clients, c => c.MachineName, BuildClientRow);
        SyncList(_pending, state.PendingApprovals, p => p.MachineName, BuildPendingRow);
        SyncList(_offline, state.OfflineClients, o => o.MachineName, BuildOfflineRow);

        if (_log.Lines.Length != state.LogEntries.Count)
        {
            _log.Lines = state.LogEntries.Select(e => $"{e.Time:HH:mm:ss}  {e.Message}").ToArray();
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
        RebuildActionsIfChanged();
    }

    void RebuildActionsIfChanged()
    {
        string key = (_selectedClient ?? "") + "|p:" + (_selectedPending ?? "") + "|o:" + (_selectedOffline ?? "");
        if (key == _lastActionKey) return;
        _lastActionKey = key;
        RebuildActions();
    }

    static void SyncList<T>(ListView lv, IReadOnlyList<T> items, Func<T, string> keyOf, Func<T, ListViewItem> build)
    {
        var keys = items.Select(keyOf).ToList();
        var existingKeys = new List<string>();
        foreach (ListViewItem it in lv.Items) existingKeys.Add((string)it.Tag);
        if (keys.SequenceEqual(existingKeys))
        {
            for (int i = 0; i < items.Count; i++)
            {
                var built = build(items[i]);
                var existing = lv.Items[i];
                for (int c = 0; c < built.SubItems.Count && c < existing.SubItems.Count; c++)
                    if (existing.SubItems[c].Text != built.SubItems[c].Text)
                        existing.SubItems[c].Text = built.SubItems[c].Text;
            }
            return;
        }
        var sel = lv.SelectedItems.Count > 0 ? (string)lv.SelectedItems[0].Tag : null;
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var item in items)
        {
            var lvi = build(item);
            lv.Items.Add(lvi);
            if (sel != null && (string)lvi.Tag == sel) lvi.Selected = true;
        }
        lv.EndUpdate();
    }

    static ListViewItem BuildClientRow(ClientCardState card)
    {
        var r = card.Report;
        var row = new ListViewItem(card.DisplayName) { Tag = card.MachineName };
        row.SubItems.Add(card.OsLabel);
        row.SubItems.Add(card.ClientVersion + (card.IsOutdated ? " ⚠" : ""));
        row.SubItems.Add(r?.TotalLoadPercent is { } load ? $"{load:0}" : "—");
        row.SubItems.Add(r?.PackageTemperatureC is > 0 ? $"{r.PackageTemperatureC:0}°" : "—");
        row.SubItems.Add(r != null && r.RamTotalGB > 0 ? $"{(int)(r.RamUsedGB / r.RamTotalGB * 100)}" : "—");
        string state = card.IsWaitingForFirstReport ? "waiting" : card.IsStale ? "stale" : card.SendMode;
        row.SubItems.Add(state);
        if (card.IsOutdated) row.ForeColor = Th.Org;
        else if (card.IsStale) row.ForeColor = Th.Yel;
        else if (card.IsWaitingForFirstReport) row.ForeColor = Th.Dim;
        return row;
    }

    static ListViewItem BuildPendingRow(PendingApprovalState p)
    {
        var row = new ListViewItem(p.MachineName) { Tag = p.MachineName, ForeColor = Th.Yel };
        row.SubItems.Add(p.Ip);
        row.SubItems.Add(p.ClientVersion);
        row.SubItems.Add(p.RequestedAt.ToLocalTime().ToString("HH:mm:ss"));
        return row;
    }

    static ListViewItem BuildOfflineRow(OfflineClientState o)
    {
        var row = new ListViewItem(o.DisplayName) { Tag = o.MachineName, ForeColor = Th.Dim };
        row.SubItems.Add(o.Seen.ToLocalTime().ToString("MM-dd HH:mm"));
        row.SubItems.Add(o.Ip);
        row.SubItems.Add(string.IsNullOrEmpty(o.Mac) ? "—" : o.Mac);
        return row;
    }

    void RebuildActions()
    {
        _actions.SuspendLayout();
        _actions.Controls.Clear();

        if (_selectedClient != null)
        {
            var state = _dashboard.GetState();
            var card = state.Clients.FirstOrDefault(c => c.MachineName == _selectedClient);
            if (card != null)
            {
                AddAction("Procs", Th.Blu, () => _dashboard.RequestProcesses(card.MachineName));
                AddAction("Info", Th.Cyan, () => _dashboard.RequestSysInfo(card.MachineName));
                if (card.CanServices) AddAction("Services", Th.Grn, () => _dashboard.RequestServices(card.MachineName));
                AddAction("Health", Th.Grn, () => _dashboard.ShowHealth(card.MachineName));
                if (card.CanScreenshot) AddAction("Screenshot", Th.Cyan, () => _dashboard.RequestScreenshot(card.MachineName));
                AddAction("Files", Th.Yel, () => _dashboard.OpenFileBrowser(card.MachineName));
                AddAction("Terminal", Th.Cyan, () => _dashboard.OpenTerminal(card.MachineName, card.IsLinux ? "bash" : "powershell"));
                if (card.CanRdp) AddAction("RDP", Th.Cyan, () => _dashboard.OpenRdp(card.MachineName));
                AddAction("Msg", Th.Dim, () => _dashboard.SendUserMessage(card.MachineName));
                AddSep();
                AddAction("Restart", Th.Org, () => _dashboard.RestartClient(card.MachineName));
                AddAction("Shutdown", Th.Red, () => _dashboard.ShutdownClient(card.MachineName));
                AddAction("Push Update", Th.Org, () => _dashboard.PushUpdate(card.MachineName));
                AddAction("Forget", Th.Dim, () => _dashboard.ForgetClient(card.MachineName));
            }
        }
        else if (_selectedPending != null)
        {
            var name = _selectedPending;
            AddAction("Approve", Th.Grn, () => { _dashboard.ApprovePending(name); Refresh(); });
            AddAction("Reject", Th.Red, () => { _dashboard.RejectPending(name); Refresh(); });
        }
        else if (_selectedOffline != null)
        {
            var name = _selectedOffline;
            AddAction("Wake", Th.Cyan, () => _dashboard.WakeOffline(name));
            AddAction("Set MAC", Th.Dim, () => _dashboard.RequestSetOfflineMac(name));
            AddAction("Forget", Th.Dim, () => _dashboard.ForgetOffline(name));
        }
        else
        {
            _actions.Controls.Add(new Label { Text = "Select a client to see available actions", ForeColor = Th.Dim, AutoSize = true, Margin = new Padding(6, 8, 6, 0) });
        }
        _actions.ResumeLayout();
    }

    void AddAction(string text, Color accent, Action onClick)
    {
        var b = new Button { Text = text };
        StyleButton(b, accent);
        b.Click += (_, _) => onClick();
        _actions.Controls.Add(b);
    }

    static void StyleButton(Button b, Color accent, bool subtle = false)
    {
        var baseColor = subtle ? Th.TBg : Th.Card;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.UseVisualStyleBackColor = false;
        b.BackColor = Mix(baseColor, accent, subtle ? 0.08f : 0.14f);
        b.ForeColor = subtle ? Th.Brt : accent;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Mix(Th.Brd, accent, subtle ? 0.28f : 0.52f);
        b.FlatAppearance.MouseOverBackColor = Mix(Th.Card, accent, subtle ? 0.16f : 0.24f);
        b.FlatAppearance.MouseDownBackColor = Mix(Th.Bg, accent, subtle ? 0.18f : 0.30f);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        b.Margin = new Padding(4, 4, 4, 6);
        b.Padding = new Padding(12, 4, 12, 4);
        b.MinimumSize = new Size(96, 34);
    }

    static Color Mix(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        int r = (int)(a.R + (b.R - a.R) * amount);
        int g = (int)(a.G + (b.G - a.G) * amount);
        int bl = (int)(a.B + (b.B - a.B) * amount);
        return Color.FromArgb(r, g, bl);
    }

    void AddSep() => _actions.Controls.Add(new Label { Text = "|", ForeColor = Th.Brd, AutoSize = true, Margin = new Padding(8, 10, 8, 0) });
}
