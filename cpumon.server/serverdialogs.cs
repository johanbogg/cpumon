// "serverdialogs.cs"
using System;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

sealed class ApprovedClientsDialog : Form
{
    public ApprovedClientsDialog(ApprovedClientStore store, ConcurrentDictionary<string, RemoteClient> live, CLog log)
    {
        Text = "Approved Clients"; Size = new Size(600, 400); MinimumSize = new Size(400, 250);
        StartPosition = FormStartPosition.CenterParent; BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Th.Bg, ForeColor = Th.Brt, GridColor = Th.Brd,
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.Card, ForeColor = Th.Brt, SelectionBackColor = Color.FromArgb(50, 80, 160) },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.TBg, ForeColor = Th.Blu },
            EnableHeadersVisualStyles = false, RowHeadersVisible = false, AllowUserToAddRows = false, ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
        };
        grid.Columns.Add("Name", "Machine"); grid.Columns.Add("Alias", "Alias");
        grid.Columns.Add("IP", "IP");
        grid.Columns.Add("Seen", "Last Seen"); grid.Columns.Add("Status", "Status");

        foreach (var c in store.All())
        {
            bool on = live.ContainsKey(c.Name);
            grid.Rows.Add(c.Name, store.GetAlias(c.Name), c.Ip, c.Seen.ToLocalTime().ToString("g"), c.Revoked ? "Revoked" : on ? "Online" : "Offline");
        }

        var bp = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Th.TBg };
        var fg = MkBtn("Forget", Th.Org); fg.Location = new Point(8, 6);
        fg.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            var n = grid.SelectedRows[0].Cells["Name"].Value?.ToString();
            if (n != null) { store.Forget(n); if (live.TryRemove(n, out var rc)) rc.Dispose(); Close(); }
        };
        var rv = MkBtn("Revoke", Th.Red); rv.Location = new Point(116, 6);
        rv.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            var n = grid.SelectedRows[0].Cells["Name"].Value?.ToString();
            if (n != null) { store.Revoke(n); if (live.TryRemove(n, out var rc)) rc.Dispose(); Close(); }
        };
        var sa = MkBtn("Set Alias…", Th.Cyan); sa.Location = new Point(224, 6);
        sa.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            var n = grid.SelectedRows[0].Cells["Name"].Value?.ToString();
            if (n == null) return;
            using var dlg = new Form { Text = "Set Alias", ClientSize = new Size(320, 132), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Th.Bg, ForeColor = Th.Brt, AutoScaleMode = AutoScaleMode.Dpi };
            var lbl = new Label { Text = $"Alias for {n}:", Location = new Point(12, 12), AutoSize = true, ForeColor = Th.Dim };
            var txt = new TextBox { Text = store.GetAlias(n), Location = new Point(12, 38), Size = new Size(296, 26), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(152, 88), Size = new Size(75, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(233, 88), Size = new Size(75, 30), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                store.SetAlias(n, txt.Text.Trim());
                grid.SelectedRows[0].Cells["Alias"].Value = txt.Text.Trim();
            }
        };

        bp.Controls.AddRange(new Control[] { fg, rv, sa });
        Controls.Add(grid); Controls.Add(bp);
    }

    static Button MkBtn(string t, Color fg)
    {
        var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(100, 28), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = fg;
        return b;
    }
}

sealed class SysInfoDialog : Form
{
    public SysInfoDialog(RemoteClient cl)
    {
        var si = cl.LastSysInfo!;
        Text = $"SysInfo — {cl.MachineName}"; Size = new Size(560, 520); StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        var rtb = new RichTextBox { Dock = DockStyle.Fill, BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 9.5f), ReadOnly = true, BorderStyle = BorderStyle.None };
        var sb = new StringBuilder();
        sb.AppendLine($"  {si.Hostname}"); sb.AppendLine(new string('─', 50));
        sb.AppendLine($"  OS: {si.OsName}"); sb.AppendLine($"  Build: {si.OsBuild}");
        sb.AppendLine($"  CPU: {si.CpuName} ({si.CpuCores}c/{si.CpuThreads}t)");
        sb.AppendLine($"  RAM: {si.RamTotalGB:0.0} GB total, {si.RamAvailGB:0.0} GB free");
        sb.AppendLine($"  GPU: {si.GpuName}"); sb.AppendLine($"  Uptime: {si.UptimeHours:0.0}h");
        sb.AppendLine($"  User: {si.Domain}\\{si.UserName}"); sb.AppendLine($"  .NET: {si.DotNetVersion}"); sb.AppendLine();
        foreach (var ip in si.IpAddresses) sb.AppendLine($"  IP: {ip}");
        foreach (var m in si.MacAddresses) sb.AppendLine($"  MAC: {m}"); sb.AppendLine();
        foreach (var d in si.Disks) sb.AppendLine($"  {d.Name} {d.Label}: {d.TotalGB - d.FreeGB:0.0}/{d.TotalGB:0.0} GB [{d.Format}]");
        rtb.Text = sb.ToString();

        var cp = new Button { Text = "📋 Copy", Dock = DockStyle.Bottom, Height = 34, BackColor = Th.Card, ForeColor = Th.Blu, FlatStyle = FlatStyle.Flat };
        cp.Click += (_, _) => Clipboard.SetText(rtb.Text);
        Controls.Add(rtb); Controls.Add(cp);
    }
}

sealed class CpuDetailDialog : Form
{
    public CpuDetailDialog(string machine, CpuDetailReport detail)
    {
        Text = $"CPU Detail - {machine}";
        Size = new Size(620, 500);
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg;
        ForeColor = Th.Brt;
        FormBorderStyle = FormBorderStyle.Sizable;

        var top = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 118,
            BackColor = Th.Card,
            ForeColor = Th.Brt,
            Font = new Font("Consolas", 9.5f),
            ReadOnly = true,
            BorderStyle = BorderStyle.None
        };

        var sb = new StringBuilder();
        sb.AppendLine($"  {machine}");
        sb.AppendLine($"  CPU: {detail.CpuName}");
        sb.AppendLine($"  Load: {Fmt(detail.TotalLoadPercent, "0.0", "%")}");
        sb.AppendLine($"  Frequency: {FmtFreq(detail.PackageFrequencyMHz)}");
        sb.AppendLine($"  Temperature: {Fmt(detail.PackageTemperatureC, "0.0", " C")}");
        if (detail.PackagePowerW.HasValue) sb.AppendLine($"  Power: {Fmt(detail.PackagePowerW, "0.0", " W")}");
        sb.AppendLine($"  Cores reported: {detail.Cores.Count} / {detail.CoreCount}");
        top.Text = sb.ToString();

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Th.Bg,
            ForeColor = Th.Brt,
            GridColor = Th.Brd,
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.Card, ForeColor = Th.Brt, SelectionBackColor = Color.FromArgb(50, 80, 160) },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.TBg, ForeColor = Th.Blu },
            EnableHeadersVisualStyles = false,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle = BorderStyle.None
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Core", HeaderText = "Core", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
        grid.Columns.Add("Load", "Load");
        grid.Columns.Add("Freq", "Frequency");
        grid.Columns.Add("Temp", "Temperature");

        foreach (var c in detail.Cores.OrderBy(c => c.Index))
            grid.Rows.Add(c.Index + 1, Fmt(c.LoadPercent, "0.0", "%"), FmtFreq(c.FrequencyMHz), Fmt(c.TemperatureC, "0.0", " C"));

        if (detail.Cores.Count == 0)
            grid.Rows.Add("-", "No per-core sensors reported", "", "");

        Controls.Add(grid);
        Controls.Add(top);
    }

    static string Fmt(float? value, string format, string suffix) => value.HasValue ? value.Value.ToString(format) + suffix : "N/A";
    static string FmtFreq(float? mhz) => mhz.HasValue ? (mhz.Value >= 1000 ? $"{mhz.Value / 1000f:0.00} GHz" : $"{mhz.Value:0} MHz") : "N/A";
}

sealed class ProcDialog : Form
{
    readonly RemoteClient _cl;
    readonly DataGridView _grid;
    readonly TextBox _search;
    readonly Timer _timer;
    List<ProcessInfo> _all = new();

    public ProcDialog(RemoteClient cl)
    {
        _cl = cl;
        Text = $"Processes — {cl.MachineName}"; Size = new Size(740, 560); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        _search = new TextBox
        {
            Dock = DockStyle.Top, Height = 28,
            BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle
        };
        _search.PlaceholderText = "Filter processes...";
        _search.TextChanged += (_, _) => ApplyFilter();

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Th.Bg, ForeColor = Th.Brt, GridColor = Th.Brd,
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.Card, ForeColor = Th.Brt, SelectionBackColor = Color.FromArgb(50, 80, 160) },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.TBg, ForeColor = Th.Blu },
            EnableHeadersVisualStyles = false, RowHeadersVisible = false, AllowUserToAddRows = false, ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CPU", HeaderText = "CPU %", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mem", HeaderText = "Memory", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });

        var bp = new Panel { Dock = DockStyle.Bottom, Height = 80, BackColor = Th.TBg };
        var kill = MkBtn("Kill", Th.Red); kill.Location = new Point(8, 6);
        kill.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0) return;
            var pv = _grid.SelectedRows[0].Cells["PID"].Value;
            if (pv != null && MessageBox.Show($"Kill PID {pv}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                _cl.Send(new ServerCommand { Cmd = "kill", CmdId = Guid.NewGuid().ToString("N")[..8], Pid = (int)pv });
        };
        var lbl = new Label { Text = "Start:", Location = new Point(8, 44), AutoSize = true, ForeColor = Th.Dim };
        var path = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Location = new Point(50, 42), Size = new Size(300, 24), BorderStyle = BorderStyle.FixedSingle };
        path.PlaceholderText = "e.g. notepad.exe";
        var args = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Location = new Point(356, 42), Size = new Size(180, 24), BorderStyle = BorderStyle.FixedSingle };
        args.PlaceholderText = "arguments";
        var go = MkBtn("▶", Th.Grn); go.Location = new Point(542, 42); go.Size = new Size(40, 24);
        go.Click += (_, _) =>
        {
            if (path.Text.Trim() != "")
                _cl.Send(new ServerCommand { Cmd = "start", FileName = path.Text.Trim(), Args = args.Text.Trim() });
        };
        bp.Controls.AddRange(new Control[] { kill, lbl, path, args, go });

        _timer = new Timer { Interval = 2000 };
        _timer.Tick += (_, _) => _cl.Send(new ServerCommand { Cmd = "listprocesses" });
        _timer.Start();

        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };

        Controls.Add(_grid);
        Controls.Add(_search);
        Controls.Add(bp);
    }

    public void UpdateList(List<ProcessInfo> procs)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateList(procs)); return; }
        _all = procs;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var filter = _search.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? _all
            : _all.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        int? selPid = _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Cells["PID"].Value is int pid ? pid : null;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var p in filtered.OrderByDescending(p => p.CpuPercent))
        {
            int idx = _grid.Rows.Add(p.Pid, p.Name, p.CpuPercent.ToString("0.0") + "%", (p.MemoryBytes / 1048576.0).ToString("0.0") + " MB");
            if (p.Pid == selPid) _grid.Rows[idx].Selected = true;
        }
        _grid.ResumeLayout();
    }

    static Button MkBtn(string t, Color fg)
    {
        var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 28), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = fg;
        return b;
    }
}

sealed class ServicesDialog : Form
{
    public ServicesDialog(RemoteClient cl)
    {
        Text = $"Services — {cl.MachineName}"; Size = new Size(780, 520); StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Th.Bg, ForeColor = Th.Brt, GridColor = Th.Brd,
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.Card, ForeColor = Th.Brt, SelectionBackColor = Color.FromArgb(50, 80, 160) },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.TBg, ForeColor = Th.Blu },
            EnableHeadersVisualStyles = false, RowHeadersVisible = false, AllowUserToAddRows = false, ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
        };
        grid.Columns.Add("DisplayName", "Service"); grid.Columns.Add("Status", "Status"); grid.Columns.Add("StartType", "Start"); grid.Columns.Add("Name", "Name");
        if (grid.Columns["Name"] is { } nc) nc.Visible = false;
        if (grid.Columns["Status"] is { } stc) stc.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        if (grid.Columns["StartType"] is { } stac) stac.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;

        if (cl.LastServiceList != null)
            foreach (var s in cl.LastServiceList)
            {
                int row = grid.Rows.Add(s.DisplayName, s.Status, s.StartType, s.Name);
                grid.Rows[row].DefaultCellStyle.ForeColor = s.Status == "Running" ? Th.Grn : s.Status == "Stopped" ? Th.Dim : Th.Org;
            }

        var bp = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Th.TBg };
        var start   = MkBtn("▶ Start",   Th.Grn); start.Location   = new Point(8,   6);
        var stop    = MkBtn("■ Stop",    Th.Red);  stop.Location    = new Point(116, 6);
        var restart = MkBtn("⟳ Restart", Th.Org);  restart.Location = new Point(224, 6); restart.Size = new Size(100, 28);
        var cancel  = MkBtn("✕ Cancel",  Th.Dim);  cancel.Location  = new Point(332, 6);
        var status  = new Label { Text = "", ForeColor = Th.Dim, Font = new Font("Segoe UI", 8.5f), AutoSize = false,
                                  Location = new Point(440, 12), Size = new Size(300, 18), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        cancel.Click += (_, _) => Close();

        void Send(string cmd)
        {
            if (grid.SelectedRows.Count == 0) return;
            var n = grid.SelectedRows[0].Cells["Name"].Value?.ToString();
            if (n == null) return;
            start.Enabled = stop.Enabled = restart.Enabled = false;
            status.Text = "Working…"; status.ForeColor = Th.Org;
            cl.ServiceResultCallback = (ok, msg) =>
            {
                if (!IsHandleCreated) return;
                BeginInvoke(() =>
                {
                    start.Enabled = stop.Enabled = restart.Enabled = true;
                    status.Text = msg; status.ForeColor = ok ? Th.Grn : Th.Red;
                    cl.ServiceResultCallback = null;
                });
            };
            cl.Send(new ServerCommand { Cmd = cmd, FileName = n, CmdId = Guid.NewGuid().ToString("N")[..8] });
        }

        start.Click   += (_, _) => Send("service_start");
        stop.Click    += (_, _) => Send("service_stop");
        restart.Click += (_, _) => Send("service_restart");

        FormClosed += (_, _) => cl.ServiceResultCallback = null;

        bp.Controls.AddRange(new Control[] { start, stop, restart, cancel, status });
        Controls.Add(grid); Controls.Add(bp);
    }

    static Button MkBtn(string t, Color fg)
    {
        var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(100, 28), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = fg;
        return b;
    }
}

sealed class EventViewerDialog : Form
{
    public EventViewerDialog(RemoteClient cl)
    {
        var events = cl.LastEvents ?? new List<EventLogEntry>();
        Text = $"⚠ Events — {cl.MachineName}"; Size = new Size(900, 500); MinimumSize = new Size(600, 350);
        StartPosition = FormStartPosition.CenterParent; BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        var grid = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 8.5f), BorderStyle = BorderStyle.None, GridLines = true, MultiSelect = false };
        grid.Columns.Add("Time", 140); grid.Columns.Add("Level", 70); grid.Columns.Add("Source", 150); grid.Columns.Add("Message", 500);

        foreach (var e in events)
        {
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(e.TimestampUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            var item = new ListViewItem(ts);
            item.SubItems.Add(e.Level); item.SubItems.Add(e.Source); item.SubItems.Add(e.Message);
            item.ForeColor = e.Level == "Error" ? Th.Red : Th.Org;
            grid.Items.Add(item);
        }

        var detail = new TextBox { Dock = DockStyle.Bottom, Height = 80, BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 8.5f), ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.FixedSingle, ScrollBars = ScrollBars.Vertical };
        grid.SelectedIndexChanged += (_, _) =>
        {
            if (grid.SelectedItems.Count > 0)
            {
                int idx = grid.SelectedItems[0].Index;
                if (idx < events.Count) detail.Text = $"[{events[idx].Level}] {events[idx].Source}\r\n{DateTimeOffset.FromUnixTimeMilliseconds(events[idx].TimestampUtcMs).LocalDateTime:yyyy-MM-dd HH:mm:ss}\r\n\r\n{events[idx].Message}";
            }
        };

        var cp = new Button { Text = "📋 Copy", Dock = DockStyle.Bottom, Height = 30, BackColor = Th.Card, ForeColor = Th.Blu, FlatStyle = FlatStyle.Flat };
        cp.Click += (_, _) => { if (grid.SelectedItems.Count > 0) { int idx = grid.SelectedItems[0].Index; if (idx < events.Count) Clipboard.SetText(events[idx].Message); } };
        Controls.Add(grid); Controls.Add(detail); Controls.Add(cp);
    }
}

sealed class HealthDialog : Form
{
    readonly RemoteClient _cl;
    readonly ApprovedClientStore _store;
    readonly RichTextBox _rtb;
    readonly Timer _timer;

    public HealthDialog(RemoteClient cl, ApprovedClientStore store)
    {
        _cl = cl;
        _store = store;
        Text = $"Health — {cl.MachineName}";
        Size = new Size(560, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg; ForeColor = Th.Brt;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(420, 320);

        _rtb = new RichTextBox
        {
            Dock = DockStyle.Fill, BackColor = Th.Card, ForeColor = Th.Brt,
            Font = new Font("Consolas", 9.5f), ReadOnly = true, BorderStyle = BorderStyle.None
        };

        var bp = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Th.TBg };
        var copy = new Button
        {
            Text = "📋 Copy summary", BackColor = Th.Card, ForeColor = Th.Blu, FlatStyle = FlatStyle.Flat,
            Size = new Size(140, 28), Location = new Point(8, 5), Cursor = Cursors.Hand
        };
        copy.FlatAppearance.BorderColor = Color.FromArgb(70, Th.Blu);
        copy.Click += (_, _) => { try { Clipboard.SetText(_rtb.Text); } catch { } };

        var refresh = new Button
        {
            Text = "🔄 Request sysinfo", BackColor = Th.Card, ForeColor = Th.Cyan, FlatStyle = FlatStyle.Flat,
            Size = new Size(150, 28), Location = new Point(156, 5), Cursor = Cursors.Hand
        };
        refresh.FlatAppearance.BorderColor = Color.FromArgb(70, Th.Cyan);
        refresh.Click += (_, _) =>
        {
            try { _cl.Send(new ServerCommand { Cmd = "sysinfo", CmdId = Guid.NewGuid().ToString("N")[..8] }); } catch { }
        };

        bp.Controls.AddRange(new Control[] { copy, refresh });
        Controls.Add(_rtb);
        Controls.Add(bp);

        RefreshData();
        _timer = new Timer { Interval = 2000 };
        _timer.Tick += (_, _) => RefreshData();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
    }

    void RefreshData()
    {
        var sb = new StringBuilder();
        bool linux = ServerEngine.IsLinuxClient(_cl);
        bool outdated = ServerEngine.ClientNeedsUpdate(_cl.ClientVersion);
        var alias = _store.GetAlias(_cl.MachineName);
        var mac = _store.GetMac(_cl.MachineName);
        bool paw = _store.IsPaw(_cl.MachineName);
        var now = DateTime.UtcNow;
        var seenAgo = now - _cl.LastSeen;
        var reportAge = _cl.LastReport != null ? (TimeSpan?)(now - _cl.LastSeen) : null;
        bool stale = seenAgo.TotalSeconds > 70;

        sb.AppendLine($"  Machine:        {_cl.MachineName}");
        if (!string.IsNullOrEmpty(alias)) sb.AppendLine($"  Alias:          {alias}");
        sb.AppendLine($"  Platform:       {(linux ? "Linux" : "Windows")}");
        sb.AppendLine($"  Client version: {(string.IsNullOrEmpty(_cl.ClientVersion) ? "(unknown)" : "v" + _cl.ClientVersion)}{(outdated ? "  — OUTDATED, server is v" + Proto.AppVersion : "")}");
        sb.AppendLine($"  Authenticated:  {(_cl.Authenticated ? "yes" : "no")}");
        sb.AppendLine($"  Connection:     {(stale ? "STALE" : "live")}  ({FormatAge(seenAgo)} since last seen)");
        if (reportAge.HasValue)
            sb.AppendLine($"  Last report:    {FormatAge(reportAge.Value)}");
        else
            sb.AppendLine($"  Last report:    (none yet — waiting for first report)");
        sb.AppendLine($"  Send mode:      {_cl.SendMode}");
        sb.AppendLine($"  Card expanded:  {(_cl.Expanded ? "yes" : "no")}");
        sb.AppendLine($"  PAW:            {(paw ? "yes" : "no")}");
        if (!string.IsNullOrEmpty(mac)) sb.AppendLine($"  MAC:            {mac}");

        if (_cl.LastReport is { } r)
        {
            sb.AppendLine();
            sb.AppendLine("  --- Latest report metrics ---");
            if (r.TotalLoadPercent.HasValue) sb.AppendLine($"  CPU load:       {r.TotalLoadPercent.Value:0}%");
            if (r.PackageTemperatureC is > 0) sb.AppendLine($"  CPU temp:       {r.PackageTemperatureC:0.0}°C");
            if (r.PackageFrequencyMHz is > 0) sb.AppendLine($"  CPU freq:       {Th.FF(r.PackageFrequencyMHz)}");
            if (r.RamTotalGB > 0) sb.AppendLine($"  RAM:            {r.RamUsedGB:0.0} / {r.RamTotalGB:0.0} GB");
            if (r.GpuLoadPercent.HasValue) sb.AppendLine($"  GPU load:       {r.GpuLoadPercent.Value:0}%");
            sb.AppendLine($"  CPU name:       {r.CpuName}");
            sb.AppendLine($"  OS version:     {r.OsVersion}");
        }

        if (_cl.LastSysInfo is { } si)
        {
            sb.AppendLine();
            sb.AppendLine("  --- Sysinfo (last query) ---");
            sb.AppendLine($"  Uptime:         {si.UptimeHours:0.0} h");
            sb.AppendLine($"  User:           {si.Domain}\\{si.UserName}");
            sb.AppendLine($"  .NET runtime:   {si.DotNetVersion}");
            foreach (var ip in si.IpAddresses) sb.AppendLine($"  IP:             {ip}");
        }

        sb.AppendLine();
        sb.AppendLine("  --- Diagnostics ---");
        sb.AppendLine($"  Server version: v{Proto.AppVersion}");
        sb.AppendLine($"  Server log:     %ProgramData%\\CpuMon\\logs\\cpumon-YYYY-MM-DD.jsonl");
        if (linux)
            sb.AppendLine($"  Client log:     journalctl -u cpumon  (or /var/log/cpumon if redirected)");
        else
            sb.AppendLine($"  Client log:     %ProgramData%\\CpuMon\\logs\\cpumon-YYYY-MM-DD.jsonl");
        sb.AppendLine($"  Snapshot at:    {now.ToLocalTime():yyyy-MM-dd HH:mm:ss} (local)");

        _rtb.Text = sb.ToString();
    }

    static string FormatAge(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h ago";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m ago";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s ago";
        if (ts.TotalSeconds >= 1) return $"{(int)ts.TotalSeconds}s ago";
        return "just now";
    }
}
