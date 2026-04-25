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
        grid.Columns.Add("Name", "Machine"); grid.Columns.Add("IP", "IP");
        grid.Columns.Add("Seen", "Last Seen"); grid.Columns.Add("Status", "Status");

        foreach (var c in store.All())
        {
            bool on = live.ContainsKey(c.Name);
            grid.Rows.Add(c.Name, c.Ip, c.Seen.ToLocalTime().ToString("g"), c.Revoked ? "Revoked" : on ? "Online" : "Offline");
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

        bp.Controls.AddRange(new Control[] { fg, rv });
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

        FormClosed += (_, _) => _timer.Stop();

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

        int? selPid = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Cells["PID"].Value as int? : null;
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
        var start = MkBtn("▶ Start", Th.Grn); start.Location = new Point(8, 6);
        start.Click += (_, _) => { if (grid.SelectedRows.Count == 0) return; var n = grid.SelectedRows[0].Cells["Name"].Value?.ToString(); if (n != null) { cl.Send(new ServerCommand { Cmd = "service_start", FileName = n, CmdId = Guid.NewGuid().ToString("N")[..8] }); Close(); } };
        var stop = MkBtn("■ Stop", Th.Red); stop.Location = new Point(116, 6);
        stop.Click += (_, _) => { if (grid.SelectedRows.Count == 0) return; var n = grid.SelectedRows[0].Cells["Name"].Value?.ToString(); if (n != null) { cl.Send(new ServerCommand { Cmd = "service_stop", FileName = n, CmdId = Guid.NewGuid().ToString("N")[..8] }); Close(); } };
        var restart = MkBtn("⟳ Restart", Th.Org); restart.Location = new Point(224, 6); restart.Size = new Size(100, 28);
        restart.Click += (_, _) => { if (grid.SelectedRows.Count == 0) return; var n = grid.SelectedRows[0].Cells["Name"].Value?.ToString(); if (n != null) { cl.Send(new ServerCommand { Cmd = "service_restart", FileName = n, CmdId = Guid.NewGuid().ToString("N")[..8] }); Close(); } };

        bp.Controls.AddRange(new Control[] { start, stop, restart });
        Controls.Add(grid); Controls.Add(bp);
    }

    static Button MkBtn(string t, Color fg)
    {
        var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(100, 28), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = fg;
        return b;
    }
}
