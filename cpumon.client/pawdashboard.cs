using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Security.Cryptography;
using Timer = System.Windows.Forms.Timer;

// ═══════════════════════════════════════════════════
//  PAW Dashboard (shown on PAW client)
// ═══════════════════════════════════════════════════
sealed class PawDashboardForm : Form
{
    readonly ConcurrentDictionary<string, PawRemoteClient> _clients;
    readonly Action<string, ServerCommand> _sendCmd;
    readonly CLog _log;
    readonly DPanel _ct;
    int _sy;
    readonly List<(Rectangle R, string M, string A)> _btns = new();

    // Track expanded state per client
    readonly Dictionary<string, bool> _expanded = new();

    // PAW terminal tracking
    readonly ConcurrentDictionary<string, PawTerminalDialog> _terminals = new();

    // PAW file browser tracking
    readonly ConcurrentDictionary<string, PawFileBrowserProxy> _fileBrowsers = new();

    // PAW process dialog tracking
    readonly Dictionary<string, PawProcDialog> _procDialogs = new();
    readonly Dictionary<string, PawServicesDialog> _serviceDialogs = new();

    // PAW RDP tracking: rdpId → viewer
    readonly ConcurrentDictionary<string, RdpViewerDialog> _rdpViewers = new();

    public PawDashboardForm(ConcurrentDictionary<string, PawRemoteClient> clients, Action<string, ServerCommand> sendCmd, CLog log)
    {
        _clients = clients; _sendCmd = sendCmd; _log = log;

        Text = "🔑 PAW Dashboard"; Size = new Size(700, 550); MinimumSize = new Size(500, 350);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Th.Bg; ForeColor = Th.Brt;
        FormBorderStyle = FormBorderStyle.Sizable; Font = new Font("Segoe UI", 9f);

        var top = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Th.TBg };
        top.Controls.Add(new Label
        {
            Text = "🔑 PAW — Remote Management",
            ForeColor = Th.Mag,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true, Location = new Point(12, 8)
        });

        _ct = new DPanel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        _ct.Paint += PaintContent;
        _ct.MouseWheel += (_, e) => { _sy = Math.Max(0, _sy - e.Delta / 4); _ct.Invalidate(); };
        _ct.MouseClick += OnClick;

        Controls.Add(_ct); Controls.Add(top);
    }

    public void RefreshView() { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(() => _ct.Invalidate()); } catch { } }

    void PaintContent(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _btns.Clear();

        int x = 10, y = 6 - _sy, w = _ct.Width - 20;

        // Header
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 30, 6); g.FillPath(bg, p); }
        using (var hf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var hb = new SolidBrush(Th.Mag))
            g.DrawString($"Monitoring {_clients.Count} client(s) via PAW relay", hf, hb, x + 12, y + 6);
        y += 38;

        var onlineClients = _clients.Where(kv => !kv.Value.IsOffline).OrderBy(k => k.Key).ToList();
        var offlineClients = _clients.Where(kv => kv.Value.IsOffline).OrderBy(k => k.Key).ToList();

        if (!onlineClients.Any() && !offlineClients.Any())
        {
            using var f = new Font("Segoe UI", 10f); using var b = new SolidBrush(Th.Dim);
            using var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("Waiting for client data...", f, b, new RectangleF(x, y + 20, w, 40), sf);
            return;
        }

        foreach (var kv in onlineClients)
        {
            var cl = kv.Value;
            if (cl.LastReport == null) continue;
            bool expanded = _expanded.TryGetValue(cl.MachineName, out var ex) && ex;
            bool stale = (DateTime.UtcNow - cl.LastSeen).TotalSeconds > 10;
            int ch = expanded ? DrawExpanded(g, x, y, w, cl, stale) : DrawCollapsed(g, x, y, w, cl, stale);
            y += ch + 6;
        }

        if (offlineClients.Any())
        {
            using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Dim))
                g.DrawString("OFFLINE", hf, hb, x + 4, y + 4);
            y += 18;
            foreach (var kv in offlineClients)
            {
                using (var bg = new SolidBrush(Color.FromArgb(26, 26, 32))) { using var p = Th.RR(x, y, w, 26, 5); g.FillPath(bg, p); }
                using (var bp = new Pen(Color.FromArgb(35, Th.Dim), 1f)) { using var p = Th.RR(x, y, w, 26, 5); g.DrawPath(bp, p); }
                using (var dot = new SolidBrush(Th.Dim)) g.FillEllipse(dot, x + 10, y + 9, 7, 7);
                using (var nf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Dim))
                    g.DrawString(kv.Key, nf, nb, x + 22, y + 4);
                using (var of = new Font("Segoe UI", 7.5f)) using (var ob = new SolidBrush(Color.FromArgb(100, 70, 70)))
                    g.DrawString("Offline", of, ob, x + w - 60, y + 6);
                y += 32;
            }
        }
    }

    int DrawCollapsed(Graphics g, int x, int y, int w, PawRemoteClient cl, bool stale)
    {
        var r = cl.LastReport!; int h = 36;
        Color brd = stale ? Th.Org : Th.Mag;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(60, brd), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }

        _btns.Add((new Rectangle(x, y, w, h), r.MachineName, "toggle"));

        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 10, y + 13, 8, 8);
        using var nf = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold); using var nb = new SolidBrush(Th.Brt);
        g.DrawString(r.MachineName, nf, nb, x + 24, y + 8);

        var nsz = g.MeasureString(r.MachineName, nf);
        int mx = x + 28 + (int)nsz.Width + 16;
        using var mf = new Font("Segoe UI", 8f);

        if (r.TotalLoadPercent.HasValue) { using var lb = new SolidBrush(Th.LdC(r.TotalLoadPercent.Value)); g.DrawString($"{r.TotalLoadPercent.Value:0}%", mf, lb, mx, y + 10); mx += 48; }
        if (r.PackageTemperatureC is > 0) { using var tb = new SolidBrush(Th.TpC(r.PackageTemperatureC.Value)); g.DrawString($"{r.PackageTemperatureC.Value:0}°C", mf, tb, mx, y + 10); mx += 52; }
        if (r.PackageFrequencyMHz is > 0) { using var fb = new SolidBrush(Th.Blu); g.DrawString(Th.FF(r.PackageFrequencyMHz), mf, fb, mx, y + 10); }

        using var ef = new Font("Segoe UI", 10f); using var eb = new SolidBrush(Th.Dim);
        g.DrawString("▾", ef, eb, x + w - 24, y + 8);

        return h;
    }

    int DrawExpanded(Graphics g, int x, int y, int w, PawRemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        int hdrH = 62, btnH = 56, h = hdrH + btnH + 4;
        Color brd = stale ? Th.Org : Th.Mag;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 8); g.FillPath(bg, p); }
        using (var bp = new Pen(brd, 1.5f)) { using var p = Th.RR(x, y, w, h, 8); g.DrawPath(bp, p); }

        _btns.Add((new Rectangle(x, y, w, 30), r.MachineName, "toggle"));

        using (var ef = new Font("Segoe UI", 10f)) using (var eb = new SolidBrush(Th.Dim))
            g.DrawString("▴", ef, eb, x + w - 24, y + 8);
        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 12, y + 12, 8, 8);
        using (var nf = new Font("Segoe UI Semibold", 11f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Brt))
            g.DrawString(r.MachineName, nf, nb, x + 26, y + 7);
        using (var cf = new Font("Segoe UI", 7.5f)) using (var cb = new SolidBrush(Th.Dim))
            g.DrawString(r.CpuName, cf, cb, x + 26, y + 27);

        int my = y + 46, mx2 = x + 12;
        DrawMetric(g, mx2, my, "LOAD", Th.F(r.TotalLoadPercent, "0", "%"), Th.LdC(r.TotalLoadPercent ?? 0)); mx2 += 110;
        DrawMetric(g, mx2, my, "FREQ", Th.FF(r.PackageFrequencyMHz), Th.Blu); mx2 += 110;
        DrawMetric(g, mx2, my, "TEMP", Th.F(r.PackageTemperatureC, "0.0", "°C"), Th.TpC(r.PackageTemperatureC ?? 0)); mx2 += 110;
        if (r.PackagePowerW is > 0) DrawMetric(g, mx2, my, "PWR", Th.F(r.PackagePowerW, "0.0", "W"), Th.Org);

        // Row 1
        int by = y + hdrH, bx = x + 12;
        DrawBtn(g, bx, by, 72, 22, "⟳ Restart", Th.Org, r.MachineName, "restart"); bx += 80;
        DrawBtn(g, bx, by, 78, 22, "☰ Procs", Th.Blu, r.MachineName, "processes"); bx += 86;
        DrawBtn(g, bx, by, 68, 22, "Info", Th.Cyan, r.MachineName, "sysinfo"); bx += 76;
        DrawBtn(g, bx, by, 76, 22, "Services", Th.Grn, r.MachineName, "services"); bx += 84;
        DrawBtn(g, bx, by, 72, 22, "Off", Th.Red, r.MachineName, "shutdown");

        // Row 2
        int by2 = by + 28; bx = x + 12;
        bool linux = IsLinuxReport(r);
        if (linux)
        {
            DrawBtn(g, bx, by2, 100, 22, "Bash", Th.Cyan, r.MachineName, "bash"); bx += 108;
        }
        else
        {
            DrawBtn(g, bx, by2, 100, 22, "CMD", Th.Cyan, r.MachineName, "cmd"); bx += 108;
            DrawBtn(g, bx, by2, 120, 22, "PowerShell", Th.Blu, r.MachineName, "powershell"); bx += 128;
        }
        DrawBtn(g, bx, by2, 100, 22, "📁 Files", Th.Yel, r.MachineName, "files"); bx += 108;
        DrawBtn(g, bx, by2, 80, 22, "🖥 RDP", Th.Cyan, r.MachineName, "rdp");

        return h;
    }

    void DrawBtn(Graphics g, int x, int y, int w, int h, string text, Color c, string machine, string action)
    {
        var rect = new Rectangle(x, y, w, h); _btns.Add((rect, machine, action));
        using var bg = new SolidBrush(Color.FromArgb(25, c)); using var p = Th.RR(x, y, w, h, 4); g.FillPath(bg, p);
        using var pen = new Pen(Color.FromArgb(70, c), 1f); g.DrawPath(pen, p);
        using var f = new Font("Segoe UI", 7f, FontStyle.Bold); using var b = new SolidBrush(c);
        var sz = g.MeasureString(text, f); g.DrawString(text, f, b, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    static void DrawMetric(Graphics g, int x, int y, string l, string v, Color c)
    {
        using var lf = new Font("Segoe UI", 6.5f); using var lb = new SolidBrush(Th.Dim); g.DrawString(l, lf, lb, x, y - 12);
        using var vf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold); using var vb = new SolidBrush(c); g.DrawString(v, vf, vb, x, y);
    }

    static bool IsLinuxReport(MachineReport report) =>
        report.OsVersion.Contains("linux", StringComparison.OrdinalIgnoreCase) ||
        report.CpuName.Contains("linux", StringComparison.OrdinalIgnoreCase);

    void OnClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        foreach (var (r, m, a) in _btns)
        {
            if (!r.Contains(e.Location)) continue;
            switch (a)
            {
                case "toggle":
                    _expanded[m] = !(_expanded.TryGetValue(m, out var ex) && ex);
                    _ct.Invalidate(); break;
                case "restart":
                    if (MessageBox.Show($"Restart {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _sendCmd(m, new ServerCommand { Cmd = "restart", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "shutdown":
                    if (MessageBox.Show($"SHUT DOWN {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _sendCmd(m, new ServerCommand { Cmd = "shutdown", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "processes":
                    OpenProcessDialog(m); break;
                case "sysinfo":
                    _sendCmd(m, new ServerCommand { Cmd = "sysinfo", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "services":
                    OpenServicesDialog(m); break;
                case "cmd":
                    OpenPawTerminal(m, "cmd"); break;
                case "powershell":
                    OpenPawTerminal(m, "powershell"); break;
                case "bash":
                    OpenPawTerminal(m, "bash"); break;
                case "files":
                    OpenPawFileBrowser(m); break;
                case "rdp":
                    OpenPawRdp(m); break;
            }
            break;
        }
    }

    void OpenPawRdp(string target)
    {
        string rdpId = Guid.NewGuid().ToString("N")[..12];
        var viewer = new RdpViewerDialog(target, rdpId,
            cmd => _sendCmd(target, cmd),
            () => _rdpViewers.TryRemove(rdpId, out _));
        _rdpViewers[rdpId] = viewer;
        _sendCmd(target, new ServerCommand { Cmd = "rdp_open", RdpId = rdpId, RdpFps = Proto.RdpFpsDefault, RdpQuality = Proto.RdpJpegQuality });
        viewer.Show(this);
        _log.Add($"PAW RDP→{target}", Th.Mag);
    }

    public void ReceiveRdpFrame(string source, RdpFrameData frame)
    {
        if (_rdpViewers.TryGetValue(frame.Id, out var viewer))
            viewer.ReceiveFrame(frame);
    }

    void OpenPawTerminal(string target, string shell)
    {
        var termId = Guid.NewGuid().ToString("N")[..12];
        var dlg = new PawTerminalDialog(target, shell, termId, _sendCmd);
        _terminals[$"{target}:{termId}"] = dlg;
        dlg.FormClosed += (_, _) =>
        {
            _terminals.TryRemove($"{target}:{termId}", out _);
            _sendCmd(target, new ServerCommand { Cmd = "terminal_close", TermId = termId });
        };
        _sendCmd(target, new ServerCommand { Cmd = "terminal_open", TermId = termId, Shell = shell });
        dlg.Show(this);
        _log.Add($"PAW term→{target} [{shell}]", Th.Mag);
    }

    void OpenPawFileBrowser(string target)
    {
        var browserId = Guid.NewGuid().ToString("N")[..12];
        var proxy = new PawFileBrowserProxy(target, browserId, _sendCmd);
        _fileBrowsers[browserId] = proxy;
        proxy.Dialog.FormClosed += (_, _) => _fileBrowsers.TryRemove(browserId, out _);
        proxy.Dialog.Show(this);
        _log.Add($"PAW files→{target}", Th.Mag);
    }

    // ── Receive callbacks from ClientForm ──

    public void ReceiveProcessList(string source, List<ProcessInfo> procs)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (_procDialogs.TryGetValue(source, out var existing) && !existing.IsDisposed)
            existing.UpdateList(procs);
    }

    void OpenProcessDialog(string source)
    {
        if (_procDialogs.TryGetValue(source, out var existing) && !existing.IsDisposed)
        {
            existing.BringToFront();
            _sendCmd(source, new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] });
            return;
        }

        var d = new PawProcDialog(source, _sendCmd);
        _procDialogs[source] = d;
        d.FormClosed += (_, _) => _procDialogs.Remove(source);
        d.Show(this);
        _sendCmd(source, new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] });
    }

    public void ReceiveSysInfo(string source, SystemInfoReport si)
    {
        if (!IsHandleCreated || IsDisposed) return;
        using var d = new PawSysInfoDialog(source, si);
        d.ShowDialog(this);
    }

    void OpenServicesDialog(string source)
    {
        if (_serviceDialogs.TryGetValue(source, out var existing) && !existing.IsDisposed)
        {
            existing.BringToFront();
            _sendCmd(source, new ServerCommand { Cmd = "list_services", CmdId = Guid.NewGuid().ToString("N")[..8] });
            return;
        }

        var d = new PawServicesDialog(source, _sendCmd);
        _serviceDialogs[source] = d;
        d.FormClosed += (_, _) => _serviceDialogs.Remove(source);
        d.Show(this);
        _sendCmd(source, new ServerCommand { Cmd = "list_services", CmdId = Guid.NewGuid().ToString("N")[..8] });
    }

    public void ReceiveServiceList(string source, List<ServiceInfo> services)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (_serviceDialogs.TryGetValue(source, out var existing) && !existing.IsDisposed)
            existing.UpdateList(services);
    }

    public void ReceiveCmdResult(string source, bool success, string message, string? cmdId)
    {
        _log.Add($"[PAW {source}] {(success ? "✓" : "✕")} {message}", success ? Th.Grn : Th.Red);
        // Route to file browsers
        foreach (var fb in _fileBrowsers.Values.Where(f => f.Target == source))
            fb.Dialog.ReceiveCmdResult(success, message);
        if (_serviceDialogs.TryGetValue(source, out var svc) && !svc.IsDisposed)
            svc.ReceiveCmdResult(success, message);
    }

    public void ReceiveTermOutput(string source, string termId, string output)
    {
        if (_terminals.TryGetValue($"{source}:{termId}", out var dlg))
            dlg.ReceiveOutput(output);
    }

    public void ReceiveFileListing(string source, FileListing listing, string? cmdId)
    {
        if (cmdId != null && _fileBrowsers.TryGetValue(cmdId, out var proxy))
            proxy.Dialog.ReceiveListing(listing);
    }

    public void ReceiveFileChunk(string source, FileChunkData chunk)
    {
        foreach (var fb in _fileBrowsers.Values.Where(f => f.Target == source))
            fb.Dialog.ReceiveFileChunkPaw(chunk);
    }
}

// ═══════════════════════════════════════════════════
//  PAW Terminal Dialog (client-side, relays via server)
// ═══════════════════════════════════════════════════
sealed class PawTerminalDialog : Form
{
    readonly string _target, _termId;
    readonly Action<string, ServerCommand> _send;
    readonly RichTextBox _output;
    readonly TextBox _input;
    readonly List<string> _history = new();
    int _histIdx = -1;
    readonly StringBuilder _buf = new();
    readonly object _bufLock = new();
    readonly System.Windows.Forms.Timer _flush;

    public PawTerminalDialog(string target, string shell, string termId, Action<string, ServerCommand> send)
    {
        _target = target; _termId = termId; _send = send;
        Text = $"🔑 PAW {shell.ToUpper()} — {target}"; Size = new Size(840, 560); MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(12, 12, 16); ForeColor = Color.FromArgb(204, 204, 204);
        FormBorderStyle = FormBorderStyle.Sizable; KeyPreview = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(22, 22, 28) };
        top.Controls.Add(new Label { Text = $"🔑 {shell.ToUpper()} — {target} (PAW)", ForeColor = Th.Mag, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 6) });

        _output = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 12, 16), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 10f), ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both };

        var inputBar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(28, 28, 34) };
        inputBar.Controls.Add(new Label { Text = "❯", ForeColor = Th.Mag, Font = new Font("Consolas", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 7) });
        _input = new TextBox { BackColor = Color.FromArgb(28, 28, 34), ForeColor = Color.FromArgb(220, 220, 220), Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None, Location = new Point(26, 8), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _input.KeyDown += OnKey; inputBar.Resize += (_, _) => _input.Width = inputBar.Width - 34; _input.Width = inputBar.Width - 34;
        inputBar.Controls.Add(_input);

        Controls.Add(_output); Controls.Add(inputBar); Controls.Add(top);

        _flush = new System.Windows.Forms.Timer { Interval = 50 }; _flush.Tick += (_, _) => FlushOutput(); _flush.Start();
        FormClosed += (_, _) => { _flush.Stop(); _flush.Dispose(); };
        Shown += (_, _) => _input.Focus();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: e.SuppressKeyPress = true; string line = _input.Text; _input.Clear(); if (!string.IsNullOrEmpty(line)) { _history.Add(line); _histIdx = _history.Count; } SendText(line + "\n"); break;
            case Keys.Up: e.SuppressKeyPress = true; if (_history.Count > 0 && _histIdx > 0) { _histIdx--; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } break;
            case Keys.Down: e.SuppressKeyPress = true; if (_histIdx < _history.Count - 1) { _histIdx++; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } else { _histIdx = _history.Count; _input.Clear(); } break;
            case Keys.C when e.Control: e.SuppressKeyPress = true; SendText("\x03"); break;
            case Keys.L when e.Control: e.SuppressKeyPress = true; _output.Clear(); break;
        }
    }

    void SendText(string text) => _send(_target, new ServerCommand { Cmd = "terminal_input", TermId = _termId, Input = text });

    public void ReceiveOutput(string text) { lock (_bufLock) { _buf.Append(text); } }

    void FlushOutput()
    {
        string? text; lock (_bufLock) { if (_buf.Length == 0) return; text = _buf.ToString(); _buf.Clear(); }
        if (_output.TextLength > 200_000) { _output.Select(0, _output.TextLength - 150_000); _output.SelectedText = ""; }
        _output.AppendText(text); _output.ScrollToCaret();
    }
}

// ═══════════════════════════════════════════════════
//  PAW File Browser Proxy (wraps a FileBrowserDialog
//  but sends commands via PAW relay instead of direct)
// ═══════════════════════════════════════════════════
sealed class PawFileBrowserProxy
{
    public string Target { get; }
    public string BrowserId { get; }
    public PawFileBrowserDialogClient Dialog { get; }

    public PawFileBrowserProxy(string target, string browserId, Action<string, ServerCommand> send)
    {
        Target = target; BrowserId = browserId;
        Dialog = new PawFileBrowserDialogClient(target, browserId, send);
    }
}

sealed class PawFileBrowserDialogClient : Form
{
    readonly string _target, _browserId;
    readonly Action<string, ServerCommand> _send;
    readonly ListView _fileList;
    readonly TextBox _pathBox;
    readonly Label _statusLabel;
    readonly ProgressBar _progressBar;
    string _currentPath = "";
    readonly ImageList _icons;
    readonly ConcurrentDictionary<string, FileDownloadState> _downloads = new();

    public PawFileBrowserDialogClient(string target, string browserId, Action<string, ServerCommand> send)
    {
        _target = target; _browserId = browserId; _send = send;
        Text = $"🔑 PAW Files — {target}"; Size = new Size(900, 600); MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterParent; BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        _icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        _icons.Images.Add("folder", MkIco(Th.Yel, true)); _icons.Images.Add("file", MkIco(Th.Blu, false)); _icons.Images.Add("drive", MkIco(Th.Grn, true));

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Th.TBg };
        var backBtn = MkBtn("◀ Up", Th.Blu); backBtn.Location = new Point(4, 4); backBtn.Size = new Size(60, 28); backBtn.Click += (_, _) => NavUp();
        var rootBtn = MkBtn("🖥 Drives", Th.Grn); rootBtn.Location = new Point(68, 4); rootBtn.Size = new Size(80, 28); rootBtn.Click += (_, _) => Nav("");
        _pathBox = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle, Location = new Point(156, 6), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Nav(_pathBox.Text.Trim()); } };
        var goBtn = MkBtn("Go", Th.Grn); goBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right; goBtn.Size = new Size(40, 28); goBtn.Click += (_, _) => Nav(_pathBox.Text.Trim());
        toolbar.Controls.AddRange(new Control[] { backBtn, rootBtn, _pathBox, goBtn });
        toolbar.Resize += (_, _) => { _pathBox.Width = toolbar.Width - 260; goBtn.Location = new Point(toolbar.Width - 80, 4); };

        _fileList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Brt,
            Font = new Font("Segoe UI", 9f), BorderStyle = BorderStyle.None, SmallImageList = _icons, GridLines = true, MultiSelect = true
        };
        _fileList.Columns.Add("Name", 320); _fileList.Columns.Add("Size", 100, HorizontalAlignment.Right); _fileList.Columns.Add("Modified", 160); _fileList.Columns.Add("Type", 80);
        _fileList.DoubleClick += (_, _) => OpenSel();
        _fileList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSel(); } else if (e.KeyCode == Keys.Back) { e.SuppressKeyPress = true; NavUp(); } else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; DelSel(); } else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; Nav(_currentPath); } };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Th.TBg };
        var dlBtn = MkBtn("⬇ Download", Th.Grn); dlBtn.Location = new Point(8, 6); dlBtn.Size = new Size(100, 28); dlBtn.Click += (_, _) => DlSel();
        var delBtn = MkBtn("🗑 Delete", Th.Red); delBtn.Location = new Point(116, 6); delBtn.Size = new Size(90, 28); delBtn.Click += (_, _) => DelSel();
        var ulBtn = MkBtn("⬆ Upload", Th.Yel); ulBtn.Location = new Point(214, 6); ulBtn.Size = new Size(90, 28); ulBtn.Click += (_, _) => UploadFile();
        _statusLabel = new Label { Text = "Loading...", ForeColor = Th.Dim, Font = new Font("Segoe UI", 8f), AutoSize = false, Location = new Point(312, 12), Size = new Size(400, 20), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _progressBar = new ProgressBar { Location = new Point(312, 34), Size = new Size(300, 6), Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        bottom.Controls.AddRange(new Control[] { dlBtn, delBtn, ulBtn, _statusLabel, _progressBar });

        Controls.Add(_fileList); Controls.Add(toolbar); Controls.Add(bottom);
        FormClosed += (_, _) => { _icons.Dispose(); foreach (var d in _downloads.Values) d.Dispose(); };
        Nav("");
    }

    void Nav(string path)
    {
        _currentPath = path;
        if (IsHandleCreated) BeginInvoke(() => { _pathBox.Text = path; _statusLabel.Text = "Loading..."; _fileList.Items.Clear(); });
        _send(_target, new ServerCommand { Cmd = "file_list", Path = path, CmdId = _browserId });
    }

    void NavUp() { if (string.IsNullOrEmpty(_currentPath)) return; Nav(RemoteParent(_currentPath)); }

    void OpenSel()
    {
        if (_fileList.SelectedItems.Count == 0) return;
        var nav = _fileList.SelectedItems[0].Tag as FileNavInfo;
        if (nav?.IsDirectory == true) Nav(nav.Path);
    }

    void DlSel()
    {
        if (_fileList.SelectedItems.Count == 0) return;
        var nav = _fileList.SelectedItems[0].Tag as FileNavInfo;
        if (nav == null || nav.IsDirectory) return;
        using var sfd = new SaveFileDialog { FileName = Path.GetFileName(nav.Path) };
        if (sfd.ShowDialog() != DialogResult.OK) return;
        string tid = Guid.NewGuid().ToString("N")[..12];
        _downloads[tid] = new FileDownloadState(tid, sfd.FileName);
        _statusLabel.Text = "Downloading..."; _progressBar.Value = 0; _progressBar.Visible = true;
        _send(_target, new ServerCommand { Cmd = "file_download", Path = nav.Path, TransferId = tid, CmdId = _browserId });
    }

    void DelSel()
    {
        var items = _fileList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as FileNavInfo).Where(n => n != null && !n.IsUp && !n.IsDrive).ToList();
        if (items.Count == 0) return;
        if (MessageBox.Show($"Delete {items.Count} item(s)?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var nav in items) _send(_target, new ServerCommand { Cmd = "file_delete", Path = nav!.Path, Recursive = true, CmdId = _browserId });
        Task.Delay(500).ContinueWith(_ => { if (IsHandleCreated) BeginInvoke(() => Nav(_currentPath)); });
    }

    void UploadFile() { if (string.IsNullOrEmpty(_currentPath)) { _statusLabel.Text = "Navigate to a folder first"; _statusLabel.ForeColor = Th.Org; return; } using var ofd = new OpenFileDialog { Title = "Upload file to remote" }; if (ofd.ShowDialog() != DialogResult.OK) return; string tid = Guid.NewGuid().ToString("N")[..12]; string dest = _currentPath; string src = ofd.FileName; _statusLabel.Text = "Uploading..."; _progressBar.Value = 0; _progressBar.Visible = true; Task.Run(() => { try { var fi = new FileInfo(src); long total = fi.Length; long offset = 0; var buf = new byte[Proto.FileChunkSize]; using var fs = fi.OpenRead(); while (true) { int n = fs.Read(buf, 0, buf.Length); bool last = n == 0 || offset + n >= total; _send(_target, new ServerCommand { Cmd = "file_upload_chunk", CmdId = tid, DestPath = dest, FileChunk = new FileChunkData { TransferId = tid, FileName = fi.Name, Data = n > 0 ? Convert.ToBase64String(buf, 0, n) : "", Offset = offset, TotalSize = total, IsLast = last } }); if (IsHandleCreated) { int pct = total > 0 ? (int)((offset + n) * 100 / total) : 0; BeginInvoke(() => { _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Uploading: {pct}%"; _statusLabel.ForeColor = Th.Blu; }); } offset += n; if (last) break; } if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Upload: {fi.Name}"; _statusLabel.ForeColor = Th.Grn; }); } catch (Exception ex) { LogSink.Warn("PawFileBrowser.Upload", $"Upload failed for transfer {tid}", ex); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Upload error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); } }); }

    public void ReceiveListing(FileListing listing)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _currentPath = listing.Path; _pathBox.Text = listing.Path; _fileList.Items.Clear();
            if (listing.Error != null) { _statusLabel.Text = $"Error: {listing.Error}"; _statusLabel.ForeColor = Th.Red; return; }
            _statusLabel.ForeColor = Th.Dim;
            if (listing.Drives != null) { foreach (var d in listing.Drives) { var item = new ListViewItem(d.Name, "drive"); item.SubItems.Add(d.Ready ? $"{d.FreeGB:0.0}/{d.TotalGB:0.0} GB" : ""); item.SubItems.Add(d.Label); item.SubItems.Add(d.Format); item.Tag = new FileNavInfo { Path = d.Name, IsDirectory = true, IsDrive = true }; item.ForeColor = d.Ready ? Th.Grn : Th.Dim; _fileList.Items.Add(item); } _statusLabel.Text = $"{listing.Drives.Count} drive(s)"; return; }
            if (!string.IsNullOrEmpty(listing.Path)) { var up = new ListViewItem("..", "folder"); up.SubItems.Add(""); up.SubItems.Add(""); up.SubItems.Add("DIR"); up.Tag = new FileNavInfo { Path = RemoteParent(listing.Path), IsDirectory = true, IsUp = true }; up.ForeColor = Th.Dim; _fileList.Items.Add(up); }
            foreach (var d in listing.Entries.Where(e => e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(d.Name, "folder"); item.SubItems.Add(""); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(d.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add("DIR"); item.Tag = new FileNavInfo { Path = RemoteCombine(listing.Path, d.Name), IsDirectory = true }; item.ForeColor = d.Hidden ? Th.Dim : Th.Yel; _fileList.Items.Add(item); }
            foreach (var f in listing.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(f.Name, "file"); item.SubItems.Add(FmtSz(f.Size)); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(f.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add(Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant()); item.Tag = new FileNavInfo { Path = RemoteCombine(listing.Path, f.Name), IsDirectory = false, Size = f.Size }; item.ForeColor = f.Hidden ? Th.Dim : Th.Brt; _fileList.Items.Add(item); }
            int dc = listing.Entries.Count(e => e.IsDirectory); int fc = listing.Entries.Count(e => !e.IsDirectory);
            _statusLabel.Text = $"{dc} folder(s), {fc} file(s)";
        });
    }

    public void ReceiveFileChunkPaw(FileChunkData chunk)
    {
        if (!_downloads.TryGetValue(chunk.TransferId, out var state)) return;
        if (chunk.Error != null) { state.Dispose(); _downloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = $"Error: {chunk.Error}"; _statusLabel.ForeColor = Th.Red; _progressBar.Visible = false; }); return; }
        try
        {
            if (state.Stream == null) { state.TmpPath = state.LocalPath + ".tmp"; state.Stream = new FileStream(state.TmpPath, FileMode.Create, FileAccess.Write); state.TotalSize = chunk.TotalSize; }
            if (!string.IsNullOrEmpty(chunk.Data)) { var d = Convert.FromBase64String(chunk.Data); state.Stream.Write(d, 0, d.Length); state.Received += d.Length; }
            if (IsHandleCreated) BeginInvoke(() => { int pct = state.TotalSize > 0 ? (int)(state.Received * 100 / state.TotalSize) : 0; _progressBar.Visible = true; _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Downloading: {pct}%"; _statusLabel.ForeColor = Th.Blu; });
            if (chunk.IsLast) { state.Stream.Flush(); state.Stream.Dispose(); state.Stream = null; File.Move(state.TmpPath!, state.LocalPath, overwrite: true); state.Complete = true; _downloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Downloaded: {chunk.FileName}"; _statusLabel.ForeColor = Th.Grn; }); }
        }
        catch (Exception ex) { LogSink.Warn("PawFileBrowser.Download", $"Download chunk handling failed for transfer {chunk.TransferId}", ex); state.Dispose(); _downloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); }
    }

    public void ReceiveCmdResult(bool ok, string msg) { if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = msg; _statusLabel.ForeColor = ok ? Th.Grn : Th.Red; }); }

    static string FmtSz(long b) => b switch { < 1024 => $"{b} B", < 1048576 => $"{b / 1024.0:0.0} KB", < 1073741824 => $"{b / 1048576.0:0.0} MB", _ => $"{b / 1073741824.0:0.00} GB" };
    static bool IsUnixPath(string path) => path.StartsWith("/", StringComparison.Ordinal);
    static string RemoteCombine(string dir, string name) => IsUnixPath(dir) ? (dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name) : Path.Combine(dir, name);
    static string RemoteParent(string path) { if (!IsUnixPath(path)) return Path.GetDirectoryName(path) ?? ""; var trimmed = path.TrimEnd('/'); if (trimmed.Length <= 1) return ""; int slash = trimmed.LastIndexOf('/'); return slash <= 0 ? "/" : trimmed[..slash]; }
    static Bitmap MkIco(Color c, bool f) { var bmp = new Bitmap(16, 16); using var g = Graphics.FromImage(bmp); g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using var br = new SolidBrush(c); if (f) { g.FillRectangle(br, 1, 3, 6, 2); g.FillRectangle(br, 1, 4, 14, 10); } else { g.FillRectangle(br, 3, 1, 10, 14); } return bmp; }
    static Button MkBtn(string t, Color fg) { var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 28), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8f) }; b.FlatAppearance.BorderColor = Color.FromArgb(70, fg); return b; }
}

// ═══════════════════════════════════════════════════
//  PAW Process Dialog (shown on PAW client)
// ═══════════════════════════════════════════════════
sealed class PawProcDialog : Form
{
    readonly string _source;
    readonly Action<string, ServerCommand> _send;
    readonly DataGridView _grid;
    readonly TextBox _search;
    readonly Timer _timer;
    List<ProcessInfo> _all = new();

    public PawProcDialog(string source, Action<string, ServerCommand> send)
    {
        _source = source; _send = send;
        Text = $"🔑 Processes — {source}"; Size = new Size(740, 560); StartPosition = FormStartPosition.CenterScreen;
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

        var bp = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Th.TBg };
        var kill = new Button { Text = "Kill", ForeColor = Th.Red, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 28), Location = new Point(8, 6), Cursor = Cursors.Hand };
        kill.FlatAppearance.BorderColor = Th.Red;
        kill.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0) return;
            var pv = _grid.SelectedRows[0].Cells["PID"].Value;
            if (pv != null && MessageBox.Show($"Kill PID {pv}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                _send(_source, new ServerCommand { Cmd = "kill", CmdId = Guid.NewGuid().ToString("N")[..8], Pid = (int)pv });
        };
        bp.Controls.Add(kill);

        _timer = new Timer { Interval = 2000 };
        _timer.Tick += (_, _) => _send(_source, new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] });
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
}

sealed class PawServicesDialog : Form
{
    readonly string _source;
    readonly Action<string, ServerCommand> _send;
    readonly DataGridView _grid;
    readonly Label _status;
    readonly Timer _timer;

    public PawServicesDialog(string source, Action<string, ServerCommand> send)
    {
        _source = source; _send = send;
        Text = $"Services - {source}"; Size = new Size(780, 520); StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Th.Bg, ForeColor = Th.Brt, GridColor = Th.Brd,
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.Card, ForeColor = Th.Brt, SelectionBackColor = Color.FromArgb(50, 80, 160) },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.TBg, ForeColor = Th.Blu },
            EnableHeadersVisualStyles = false, RowHeadersVisible = false, AllowUserToAddRows = false, ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
        };
        _grid.Columns.Add("DisplayName", "Service"); _grid.Columns.Add("Status", "Status"); _grid.Columns.Add("StartType", "Start"); _grid.Columns.Add("Name", "Name");
        if (_grid.Columns["Name"] is { } nc) nc.Visible = false;
        if (_grid.Columns["Status"] is { } stc) stc.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        if (_grid.Columns["StartType"] is { } stac) stac.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;

        var bp = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Th.TBg };
        var refresh = MkBtn("Refresh", Th.Blu); refresh.Location = new Point(8, 6);
        var start = MkBtn("Start", Th.Grn); start.Location = new Point(116, 6);
        var stop = MkBtn("Stop", Th.Red); stop.Location = new Point(224, 6);
        var restart = MkBtn("Restart", Th.Org); restart.Location = new Point(332, 6);
        _status = new Label { Text = "", ForeColor = Th.Dim, Font = new Font("Segoe UI", 8.5f), AutoSize = false, Location = new Point(440, 12), Size = new Size(300, 18), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        refresh.Click += (_, _) => RequestRefresh();
        start.Click += (_, _) => SendService("service_start");
        stop.Click += (_, _) => SendService("service_stop");
        restart.Click += (_, _) => SendService("service_restart");
        bp.Controls.AddRange(new Control[] { refresh, start, stop, restart, _status });

        _timer = new Timer { Interval = 5000 };
        _timer.Tick += (_, _) => RequestRefresh();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
        Controls.Add(_grid); Controls.Add(bp);
    }

    void RequestRefresh() => _send(_source, new ServerCommand { Cmd = "list_services", CmdId = Guid.NewGuid().ToString("N")[..8] });

    void SendService(string cmd)
    {
        if (_grid.SelectedRows.Count == 0) return;
        var name = _grid.SelectedRows[0].Cells["Name"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        _status.Text = "Working..."; _status.ForeColor = Th.Org;
        _send(_source, new ServerCommand { Cmd = cmd, FileName = name, CmdId = Guid.NewGuid().ToString("N")[..8] });
    }

    public void UpdateList(List<ServiceInfo> services)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateList(services)); return; }
        _grid.Rows.Clear();
        foreach (var s in services.OrderBy(s => s.DisplayName))
        {
            int row = _grid.Rows.Add(s.DisplayName, s.Status, s.StartType, s.Name);
            _grid.Rows[row].DefaultCellStyle.ForeColor = s.Status == "Running" ? Th.Grn : s.Status == "Stopped" ? Th.Dim : Th.Org;
        }
        _status.Text = $"{services.Count} service(s)"; _status.ForeColor = Th.Dim;
    }

    public void ReceiveCmdResult(bool ok, string message)
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() => { _status.Text = message; _status.ForeColor = ok ? Th.Grn : Th.Red; RequestRefresh(); });
    }

    static Button MkBtn(string text, Color fg)
    {
        var b = new Button { Text = text, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(100, 28), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = fg;
        return b;
    }
}

sealed class PawSysInfoDialog : Form
{
    public PawSysInfoDialog(string source, SystemInfoReport si)
    {
        Text = $"🔑 SysInfo — {source}"; Size = new Size(560, 520); StartPosition = FormStartPosition.CenterParent;
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
        cp.Click += (_, _) => Clipboard.SetText(rtb.Text); Controls.Add(rtb); Controls.Add(cp);
    }
}
