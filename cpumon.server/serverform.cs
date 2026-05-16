using System;
using System.Globalization;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

sealed class ServerForm : BorderlessForm
{
    readonly ServerEngine _engine;
    readonly Panel _ct;
    readonly Timer _tm;
    readonly ToolTip _toolTip = new();
    readonly NotifyIcon _tray;
    bool _exitRequested;
    bool _trayBalloonShown;
    int _sy, _contentH;
    readonly List<(Rectangle R, string M, string A)> _btns = new();
    string _currentToolTip = "";
    readonly Dictionary<string, ProcDialog> _procDialogs = new();
    readonly HashSet<string> _selectedMachines = new();
    string _osFilter = "all";
    string _sortMode = "name";

    public ServerForm(bool noBroadcast)
    {
        _engine = new ServerEngine(noBroadcast);

        Text = $"CPU Monitor — Server  v{Proto.AppVersion}";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(50, 50);
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(420, 400);
        BackColor = Th.Bg; ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true; ShowInTaskbar = true;

        var tp = MkTitle("⬡ CPU Monitor Server", Th.Grn);

        _ct = new DPanel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        _ct.Paint += PaintContent;
        _ct.MouseWheel += (_, e) => { _sy = Math.Clamp(_sy - e.Delta / 4, 0, Math.Max(0, _contentH - _ct.Height + 20)); _ct.Invalidate(); };
        _ct.MouseClick += OnClick;
        _ct.MouseMove += OnMouseMove;

        Controls.Add(_ct);
        Controls.Add(tp);

        _tm = new Timer { Interval = 500 };
        _tm.Tick += (_, _) => _ct.Invalidate();

        _engine.ProcessListReceived += OnEngineProcessList;
        _engine.SysInfoReceived += OnEngineSysInfo;
        _engine.ServicesReceived += OnEngineServices;
        _engine.EventsReceived += OnEngineEvents;
        _engine.CpuDetailReceived += OnEngineCpuDetail;
        _engine.ScreenshotReceived += OnEngineScreenshot;
        _engine.UpdateAvailable += OnEngineUpdateAvailable;
        _engine.ReleaseStaged += OnEngineReleaseStaged;

        var appIcon = Th.MakeHexIcon(Th.Grn);
        Icon = appIcon;
        _tray = new NotifyIcon
        {
            Icon = appIcon,
            Text = "CPU Monitor Server",
            Visible = false
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => { _exitRequested = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        FormClosing += (_, e) =>
        {
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
        };

        Load += (_, _) => { _engine.Start(); _tm.Start(); };

        FormClosed += (_, _) =>
        {
            _tm.Stop(); _tm.Dispose();
            _toolTip.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _engine.ProcessListReceived -= OnEngineProcessList;
            _engine.SysInfoReceived -= OnEngineSysInfo;
            _engine.ServicesReceived -= OnEngineServices;
            _engine.EventsReceived -= OnEngineEvents;
            _engine.CpuDetailReceived -= OnEngineCpuDetail;
            _engine.ScreenshotReceived -= OnEngineScreenshot;
            _engine.UpdateAvailable -= OnEngineUpdateAvailable;
            _engine.ReleaseStaged -= OnEngineReleaseStaged;
            _engine.Dispose();
        };

        Action? onTh = null;
        onTh = () => { if (!IsDisposed) BeginInvoke(() => { BackColor = Th.Bg; _ct.BackColor = Th.Bg; _ct.Invalidate(); }); };
        Th.ThemeChanged += onTh;
        FormClosed += (_, _) => Th.ThemeChanged -= onTh;
    }

    void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _tray.Visible = true;
        if (!_trayBalloonShown)
        {
            _trayBalloonShown = true;
            _tray.ShowBalloonTip(3000, "CPU Monitor Server", "Still running in the tray. Double-click to restore.", ToolTipIcon.Info);
        }
    }

    void RestoreFromTray()
    {
        _tray.Visible = false;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        BringToFront();
        Activate();
    }

    void OnEngineProcessList(RemoteClient cl)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (_procDialogs.TryGetValue(cl.MachineName, out var existing) && !existing.IsDisposed && cl.LastProcessList != null)
                existing.UpdateList(cl.LastProcessList);
        });
    }

    void OnEngineSysInfo(RemoteClient cl)
    {
        if (IsDisposed) return;
        BeginInvoke(() => { using var d = new SysInfoDialog(cl); d.ShowDialog(this); });
    }

    void OnEngineServices(RemoteClient cl)
    {
        if (IsDisposed) return;
        BeginInvoke(() => { using var d = new ServicesDialog(cl); d.ShowDialog(this); });
    }

    void OnEngineEvents(RemoteClient cl)
    {
        if (IsDisposed) return;
        BeginInvoke(() => { using var d = new EventViewerDialog(cl); d.ShowDialog(this); });
    }

    void OnEngineCpuDetail(RemoteClient cl, CpuDetailReport detail)
    {
        if (IsDisposed) return;
        BeginInvoke(() => new CpuDetailDialog(cl.MachineName, detail).Show(this));
    }

    void OnEngineScreenshot(RemoteClient cl, ScreenshotData shot)
    {
        if (IsDisposed) return;
        BeginInvoke(() => new ScreenshotPreviewDialog(cl.MachineName, shot).Show(this));
    }

    void OnEngineUpdateAvailable()
    {
        if (IsDisposed) return;
        BeginInvoke(() => _ct.Invalidate());
    }

    void OnEngineReleaseStaged()
    {
        if (IsDisposed) return;
        BeginInvoke(() => _ct.Invalidate());
    }

    void OpenProcessDialog(string machine)
    {
        if (!_engine.Clients.TryGetValue(machine, out var cl)) return;
        if (_procDialogs.TryGetValue(cl.MachineName, out var existing) && !existing.IsDisposed)
        {
            existing.BringToFront();
            _engine.RequestProcessList(machine);
            return;
        }

        var d = new ProcDialog(cl);
        if (cl.LastProcessList != null) d.UpdateList(cl.LastProcessList);
        _procDialogs[cl.MachineName] = d;
        d.FormClosed += (_, _) => _procDialogs.Remove(cl.MachineName);
        d.Show(this);
        _engine.RequestProcessList(machine);
    }

    void OnClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        foreach (var (r, m, a) in _btns)
        {
            if (!r.Contains(e.Location)) continue;

            if (a == "newtoken") { _engine.RegenerateToken(); _ct.Invalidate(); break; }
            if (a == "copytoken") { Clipboard.SetText(_engine.Token); _engine.Log.Add("Token copied", Th.Grn); break; }
            if (a == "showapproved") { BeginInvoke(() => { using var d = new ApprovedClientsDialog(_engine.Store, _engine.Clients, _engine.Log); d.ShowDialog(this); }); break; }
            if (a == "theme") { Th.Toggle(); break; }
            if (a == "alerts") { BeginInvoke(() => { using var d = new AlertConfigDialog(_engine.Alerts); if (d.ShowDialog(this) == DialogResult.OK) _ct.Invalidate(); }); break; }
            if (a == "os_filter") { CycleOsFilter(); break; }
            if (a == "sort_mode") { CycleSortMode(); break; }
            if (a == "openrelease")
            {
                // Prefer the locally staged folder when the release has been downloaded;
                // fall back to the GitHub release page otherwise.
                var staged = _engine.StagedReleaseDir;
                string? target = staged != null && System.IO.Directory.Exists(staged) ? staged : _engine.AvailableUpdate?.ReleaseUrl;
                if (target != null) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { LogSink.Warn("Server.UI", $"Failed to open update target {target}", ex); } }
                break;
            }
            if (a == "openreleasenotes")
            {
                var url = _engine.AvailableUpdate?.ReleaseUrl;
                if (url != null) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { LogSink.Warn("Server.UI", $"Failed to open release URL {url}", ex); } }
                break;
            }
            if (a == "approve_pending") { _engine.ApprovePending(m); _ct.Invalidate(); break; }
            if (a == "reject_pending") { _engine.RejectPending(m); _ct.Invalidate(); break; }
            if (a == "forget_offline")
            {
                if (MessageBox.Show($"Forget {m}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                { _engine.Store.Forget(m); _ct.Invalidate(); }
                break;
            }
            if (a == "set_mac_offline")
            {
                using var dlg = new Form { Text = "Set MAC", Size = new Size(300, 112), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Th.Bg, ForeColor = Th.Brt };
                var lbl = new Label { Text = $"MAC for {m} (e.g. AA:BB:CC:DD:EE:FF):", Location = new Point(12, 12), AutoSize = true, ForeColor = Th.Dim };
                var txt = new TextBox { Location = new Point(12, 34), Width = 260, BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(116, 62), Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(197, 62), Width = 75 };
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
                { _engine.SetMacForOffline(m, txt.Text.Trim()); _ct.Invalidate(); }
                break;
            }
            if (a == "wake_offline") { _engine.WakeOffline(m); break; }

            if (a == "select") { if (!_selectedMachines.Remove(m)) _selectedMachines.Add(m); _ct.Invalidate(); break; }
            if (a == "clear_selection") { _selectedMachines.Clear(); _ct.Invalidate(); break; }
            if (a == "select_all") { foreach (var visible in VisibleClients()) _selectedMachines.Add(visible.MachineName); _ct.Invalidate(); break; }
            if (a == "select_outdated") { foreach (var visible in VisibleClients()) if (ServerEngine.ClientNeedsUpdate(visible.ClientVersion)) _selectedMachines.Add(visible.MachineName); _ct.Invalidate(); break; }
            if (a == "push_update_selected") { PushUpdateToSelected(); break; }

            if (!_engine.Clients.TryGetValue(m, out var cl)) continue;

            if (a.StartsWith("files_path:", StringComparison.Ordinal))
            {
                OpenFileBrowserAt(cl, a["files_path:".Length..]);
                break;
            }

            switch (a)
            {
                case "toggle": cl.Expanded = !cl.Expanded; _ct.Invalidate(); break;
                case "restart":
                    if (MessageBox.Show($"Restart {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _engine.RequestRestart(m);
                    break;
                case "processes": OpenProcessDialog(m); break;
                case "shutdown":
                    if (MessageBox.Show($"SHUT DOWN {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _engine.RequestShutdown(m);
                    break;
                case "sysinfo": _engine.RequestSysInfo(m); break;
                case "cpu_detail": _engine.RequestCpuDetail(m); break;
                case "health": BeginInvoke(() => new HealthDialog(cl, _engine.Store).Show(this)); break;
                case "screenshot": _engine.RequestScreenshot(m); break;
                case "forget":
                    if (MessageBox.Show($"Forget {m}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    { _engine.ForgetClient(m); _ct.Invalidate(); }
                    break;
                case "services": _engine.RequestServices(m); break;
                case "events": _engine.RequestEvents(m); break;
                case "msg":
                    BeginInvoke(() =>
                    {
                        using var dlg = new Form { Text = "Send Message", Size = new Size(420, 148), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Th.Bg, ForeColor = Th.Brt, MaximizeBox = false, MinimizeBox = false };
                        var txt = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 10f), Location = new Point(12, 12), Size = new Size(390, 28), BorderStyle = BorderStyle.FixedSingle };
                        txt.PlaceholderText = "Message to show on remote screen...";
                        var send = new Button { Text = "Send", DialogResult = DialogResult.OK, Location = new Point(12, 52), Size = new Size(80, 30), BackColor = Color.FromArgb(30, 60, 30), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat }; send.FlatAppearance.BorderColor = Th.Grn;
                        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(100, 52), Size = new Size(80, 30), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
                        dlg.Controls.AddRange(new Control[] { txt, send, cancel }); dlg.AcceptButton = send;
                        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
                            _engine.SendUserMessage(m, txt.Text);
                    });
                    break;
                case "cmd": BeginInvoke(() => new TerminalDialog(cl, "cmd").Show(this)); _engine.Log.Add($"CMD→{m}", Th.Cyan); break;
                case "powershell": BeginInvoke(() => new TerminalDialog(cl, "powershell").Show(this)); _engine.Log.Add($"PS→{m}", Th.Cyan); break;
                case "bash": BeginInvoke(() => new TerminalDialog(cl, "bash").Show(this)); _engine.Log.Add($"Bash→{m}", Th.Cyan); break;
                case "files": BeginInvoke(() => new FileBrowserDialog(cl).Show(this)); _engine.Log.Add($"Files→{m}", Th.Yel); break;
                case "rdp":
                    string rdpId = Guid.NewGuid().ToString("N")[..12];
                    var rdpViewer = new RdpViewerDialog(m, rdpId,
                        cmd => { if (_engine.Clients.TryGetValue(m, out var rc)) try { rc.Send(cmd); } catch { } },
                        () => { if (_engine.Clients.TryGetValue(m, out var rc)) rc.RdpDialogs.TryRemove(rdpId, out _); });
                    cl.RdpDialogs[rdpId] = rdpViewer;
                    cl.Send(new ServerCommand { Cmd = "rdp_open", RdpId = rdpId, RdpFps = Proto.RdpFpsDefault, RdpQuality = Proto.RdpJpegQuality });
                    BeginInvoke(() => rdpViewer.Show(this));
                    _engine.Log.Add($"RDP→{m}", Th.Cyan);
                    break;
                case "paw": _engine.TogglePaw(m); _ct.Invalidate(); break;
                case "update":
                    BeginInvoke(() =>
                    {
                        bool linux = ServerEngine.IsLinuxClient(cl);
                        using var ofd = new OpenFileDialog
                        {
                            Title = linux ? "Select Linux cpumon.py or cpumon-linux release zip" : "Select new client exe to push",
                            Filter = linux ? "Linux update|*.py;*.zip|Python script|*.py|Release zip|*.zip" : "Executable|*.exe"
                        };
                        if (ofd.ShowDialog(this) != DialogResult.OK) return;
                        if (linux)
                        {
                            if (!LinuxUpdatePayload.TryRead(ofd.FileName, out var fileName, out var bytes, out var error))
                            {
                                _engine.Log.Add($"Linux update failed: {error}", Th.Red);
                                return;
                            }
                            _engine.Log.Add($"Pushing Linux update → {m}…", Th.Org);
                            _engine.PushUpdatePayload(cl, fileName, bytes);
                            return;
                        }
                        _engine.Log.Add($"Pushing update → {m}…", Th.Org);
                        _engine.PushUpdate(cl, ofd.FileName);
                    });
                    break;
            }
            break;
        }
    }

    void OpenFileBrowserAt(RemoteClient cl, string path)
    {
        string initialPath = NormalizeInitialFilePath(path);
        BeginInvoke(() => new FileBrowserDialog(cl, initialPath).Show(this));
        _engine.Log.Add($"Files->{cl.MachineName} {initialPath}", Th.Yel);
    }

    static string NormalizeInitialFilePath(string path)
    {
        path = path.Trim();
        if (path.Length == 2 && path[1] == ':') return path + "\\";
        return path;
    }

    void CycleOsFilter()
    {
        _osFilter = _osFilter switch { "all" => "windows", "windows" => "linux", _ => "all" };
        _sy = 0;
        _ct.Invalidate();
    }

    void CycleSortMode()
    {
        _sortMode = _sortMode == "name" ? "os" : "name";
        _ct.Invalidate();
    }

    List<RemoteClient> VisibleClients()
    {
        IEnumerable<RemoteClient> clients = _engine.Clients.Values;
        clients = _osFilter switch
        {
            "windows" => clients.Where(cl => cl.LastReport != null && !ServerEngine.IsLinuxClient(cl)),
            "linux" => clients.Where(cl => cl.LastReport != null && ServerEngine.IsLinuxClient(cl)),
            _ => clients
        };
        clients = _sortMode == "os"
            ? clients.OrderBy(OsSortKey).ThenBy(cl => cl.MachineName, StringComparer.OrdinalIgnoreCase)
            : clients.OrderBy(cl => cl.MachineName, StringComparer.OrdinalIgnoreCase);
        return clients.ToList();
    }

    static string OsSortKey(RemoteClient cl) => ServerEngine.IsLinuxClient(cl) ? "2-linux" : "1-windows";

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        string tip = "";
        foreach (var (r, _, a) in _btns)
        {
            if (!r.Contains(e.Location)) continue;
            tip = TooltipForAction(a);
            break;
        }

        if (tip == _currentToolTip) return;
        _currentToolTip = tip;
        _toolTip.SetToolTip(_ct, tip);
    }

    static string TooltipForAction(string action) => action switch
    {
        "openrelease" => "Open the staged folder in Explorer (or the GitHub release page if not yet staged)",
        "openreleasenotes" => "Open the GitHub release page in your browser",
        "cpu_detail" => "Show detailed CPU sensors",
        "os_filter" => "Filter visible clients by operating system",
        "sort_mode" => "Sort visible clients by name or operating system",
        var a when a.StartsWith("files_path:", StringComparison.Ordinal) => "Open this drive in the file browser",
        _ => ""
    };

    void DrawSelectionBar(Graphics g, int x, int y, int w, bool anyOutdated)
    {
        int h = 38;
        using (var bg = new SolidBrush(Color.FromArgb(35, Th.Grn))) g.FillRectangle(bg, x, y, w, h);
        using (var pen = new Pen(Color.FromArgb(55, Th.Grn), 1f)) g.DrawRectangle(pen, x, y, w - 1, h - 1);
        using var f = new Font("Segoe UI", 8f);
        int n = _selectedMachines.Count;
        using var b = new SolidBrush(n > 0 ? Th.Grn : Th.Dim);
        g.DrawString(n > 0 ? $"{n} client{(n == 1 ? "" : "s")} selected" : "No selection", f, b, x + 10, y + 12);
        int bx = x + w - 14;
        if (n > 0)
        {
            bx -= 96; DrawBtn(g, bx, y + 6, 94, 26, "Push Update", Th.Org, "", "push_update_selected");
            bx -= 84; DrawBtn(g, bx, y + 6, 82, 26, "Select All", Th.Grn, "", "select_all");
            bx -= 66; DrawBtn(g, bx, y + 6, 64, 26, "Clear", Th.Dim, "", "clear_selection");
        }
        else
        {
            bx -= 84; DrawBtn(g, bx, y + 6, 82, 26, "Select All", Th.Grn, "", "select_all");
        }
        if (anyOutdated)
        {
            bx -= 110; DrawBtn(g, bx, y + 6, 108, 26, "Select Outdated", Th.Org, "", "select_outdated");
        }
    }

    void PushUpdateToSelected()
    {
        var winClients = _engine.Clients.Where(kv => _selectedMachines.Contains(kv.Key) && !ServerEngine.IsLinuxClient(kv.Value)).Select(kv => kv.Value).ToList();
        var linuxClients = _engine.Clients.Where(kv => _selectedMachines.Contains(kv.Key) && ServerEngine.IsLinuxClient(kv.Value)).Select(kv => kv.Value).ToList();
        if (winClients.Count == 0 && linuxClients.Count == 0) return;
        BeginInvoke(() =>
        {
            string filter = winClients.Count > 0 && linuxClients.Count > 0
                ? "Client files|*.exe;*.py;*.zip|Executables|*.exe|Linux update|*.py;*.zip"
                : winClients.Count > 0 ? "Executable|*.exe" : "Linux update|*.py;*.zip|Python script|*.py|Release zip|*.zip";
            using var ofd = new OpenFileDialog { Title = $"Select file to push to {_selectedMachines.Count} client(s)", Filter = filter };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            string path = ofd.FileName;
            bool isLinuxPayload = path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var targets = isLinuxPayload ? linuxClients : winClients;
            if (targets.Count == 0) { _engine.Log.Add("No matching clients for selected file type", Th.Org); return; }
            if (isLinuxPayload)
            {
                if (!LinuxUpdatePayload.TryRead(path, out var fileName, out var bytes, out var error))
                {
                    _engine.Log.Add($"Linux update failed: {error}", Th.Red);
                    return;
                }
                _engine.Log.Add($"Pushing Linux update → {targets.Count} client(s)…", Th.Org);
                _engine.PushUpdateMultiPayload(targets, fileName, bytes);
                return;
            }
            _engine.Log.Add($"Pushing update → {targets.Count} client(s)…", Th.Org);
            _engine.PushUpdateMulti(targets, path);
        });
    }

    // ── Painting ──

    void PaintContent(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _btns.Clear();

        // Purge stale clients
        foreach (var k in _engine.Clients.Where(kv => (DateTime.UtcNow - kv.Value.LastSeen).TotalSeconds > 120).Select(kv => kv.Key).ToList())
        { if (_engine.Clients.TryRemove(k, out var c)) c.Dispose(); _selectedMachines.Remove(k); }

        _sy = Math.Clamp(_sy, 0, Math.Max(0, _contentH - _ct.Height + 20));
        int x = 10, y = 6 - _sy, w = _ct.Width - 20;

        DrawStatusBar(g, x, y, w);
        y += 76;
        var visibleClients = VisibleClients();
        bool anyOutdated = visibleClients.Any(cl => ServerEngine.ClientNeedsUpdate(cl.ClientVersion));
        if (_selectedMachines.Count > 0 || anyOutdated) { DrawSelectionBar(g, x, y, w, anyOutdated); y += 42; }

        var pendingClients = _engine.PendingApprovals.Values.OrderBy(p => p.MachineName).ToList();
        if (_engine.Clients.IsEmpty && pendingClients.Count == 0)
        {
            using var f = new Font("Segoe UI", 10f);
            using var b = new SolidBrush(Th.Dim);
            using var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString($"No clients.\nShare token: {_engine.Token}", f, b, new RectangleF(x, y + 20, w, 80), sf);
            y += 80;
        }
        if (pendingClients.Count > 0)
        {
            using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Yel))
                g.DrawString("AWAITING APPROVAL", hf, hb, x + 4, y + 4);
            y += 18;
            foreach (var pending in pendingClients)
            { DrawPendingApproval(g, x, y, w, pending); y += 44; }
        }

        if (!_engine.Clients.IsEmpty)
        {
            foreach (var cl in visibleClients)
            {
                if (cl.LastReport == null)
                {
                    DrawConnectedWithoutReport(g, x, y, w, cl);
                    y += 44;
                    continue;
                }
                bool stale = (DateTime.UtcNow - cl.LastSeen).TotalSeconds > 70;
                int ch = cl.Expanded ? DrawExpanded(g, x, y, w, cl, stale) : DrawCollapsed(g, x, y, w, cl, stale);
                y += ch + 6;
            }
            if (visibleClients.Count == 0)
            {
                using var f = new Font("Segoe UI", 9f);
                using var b = new SolidBrush(Th.Dim);
                g.DrawString("No clients match the current OS filter.", f, b, x + 6, y + 10);
                y += 40;
            }
        }

        var offlineClients = _osFilter == "all"
            ? _engine.Store.All().Where(a => !a.Revoked && !_engine.Clients.ContainsKey(a.Name)).OrderBy(a => a.Name).ToList()
            : new List<ApprovedClient>();
        if (offlineClients.Count > 0)
        {
            using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Dim))
                g.DrawString("OFFLINE", hf, hb, x + 4, y + 4);
            y += 18;
            foreach (var ac in offlineClients)
            { DrawOffline(g, x, y, w, ac); y += 52; }
        }

        _contentH = y + _sy - 6;
        int logY = Math.Max(y + 8, _ct.Height - 110);
        DrawLog(g, 10, logY, w, _ct.Height - logY - 4);
    }

    void DrawStatusBar(Graphics g, int x, int y, int w)
    {
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 70, 8); g.FillPath(bg, p); }
        Color accentClr = _engine.BroadcastDisabled ? Th.Org : Th.Grn;
        using (var ac = new SolidBrush(Color.FromArgb(180, accentClr)))
            g.FillRectangle(ac, x + 1, y + 8, 4, 54);

        using (var d = new SolidBrush(accentClr)) g.FillEllipse(d, x + 14, y + 11, 9, 9);
        using (var sf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold))
        using (var sb = new SolidBrush(Th.Brt))
            g.DrawString(_engine.BroadcastDisabled ? "DIRECT ONLY" : "BROADCASTING", sf, sb, x + 30, y + 9);
        using (var df = new Font("Segoe UI", 7.5f))
        using (var db = new SolidBrush(Th.Dim))
            g.DrawString($"TCP :{Proto.DataPort}" + (_engine.BroadcastDisabled ? "" : $" | UDP :{Proto.DiscPort}"), df, db, x + 155, y + 11);

        bool tokExpired = (DateTime.UtcNow - _engine.TokenIssuedAt).TotalMinutes >= 10;
        int minsLeft = Math.Max(0, 10 - (int)(DateTime.UtcNow - _engine.TokenIssuedAt).TotalMinutes);
        using (var tf = new Font("Consolas", 8.5f, FontStyle.Bold))
        using (var tb = new SolidBrush(tokExpired ? Th.Red : Th.Yel))
            g.DrawString($"Token: {_engine.Token}", tf, tb, x + 14, y + 32);
        using (var xf = new Font("Segoe UI", 6.5f))
        using (var xb = new SolidBrush(tokExpired ? Th.Red : Th.Dim))
            g.DrawString(tokExpired ? "⚠ EXPIRED — click New" : $"expires in {minsLeft}m", xf, xb, x + 14, y + 50);

        int bx = x + 260;
        DrawBtn(g, bx, y + 23, 70, 24, "📋 Copy", Th.Blu, "", "copytoken"); bx += 78;
        DrawBtn(g, bx, y + 23, 70, 24, "🔄 New", Th.Org, "", "newtoken"); bx += 78;
        DrawBtn(g, bx, y + 23, 84, 24, "👥 Clients", Th.Cyan, "", "showapproved"); bx += 92;
        DrawBtn(g, bx, y + 23, 68, 24, Th.IsDark ? "☀ Light" : "🌙 Dark", Th.Dim, "", "theme"); bx += 76;
        DrawBtn(g, bx, y + 23, 80, 24, "🔔 Alerts", _engine.Alerts.ThresholdsConfigured ? Th.Org : Th.Dim, "", "alerts"); bx += 88;
        var update = _engine.AvailableUpdate;
        if (update != null)
        {
            bool staged = _engine.StagedReleaseDir != null;
            string label = staged ? $"📁 v{update.Version} ready" : $"↑ Update v{update.Version}";
            DrawBtn(g, bx, y + 23, 120, 24, label, Th.Cyan, "", "openrelease"); bx += 124;
            if (staged)
                DrawBtn(g, bx, y + 23, 56, 24, "Notes", Th.Dim, "", "openreleasenotes");
        }

        string osLabel = _osFilter switch { "windows" => "OS: Win", "linux" => "OS: Linux", _ => "OS: All" };
        DrawBtn(g, x + w - 180, y + 48, 78, 18, osLabel, _osFilter == "all" ? Th.Dim : Th.Cyan, "", "os_filter");
        DrawBtn(g, x + w - 96, y + 48, 84, 18, _sortMode == "os" ? "Sort: OS" : "Sort: Name", Th.Dim, "", "sort_mode");

        using (var cf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold))
        using (var ccb = new SolidBrush(_engine.ConnectionCount > 0 ? Th.Grn : Th.Dim))
        {
            string ct = $"{_engine.ConnectionCount} conn · {_engine.Clients.Count} auth";
            var sz = g.MeasureString(ct, cf);
            g.DrawString(ct, cf, ccb, x + w - sz.Width - 12, y + 9);
        }
    }

    void DrawConnectedWithoutReport(Graphics g, int x, int y, int w, RemoteClient cl)
    {
        int h = 38;
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(45, Th.Yel), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(160, Th.Yel)))
            g.FillRectangle(ac, x + 1, y + 6, 4, h - 12);
        using (var dot = new SolidBrush(Th.Yel)) g.FillEllipse(dot, x + 12, y + 14, 8, 8);

        var alias = _engine.Store.GetAlias(cl.MachineName);
        bool hasAlias = !string.IsNullOrEmpty(alias);
        string displayName = hasAlias ? alias! : cl.MachineName;
        using var nf = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using (var nb = new SolidBrush(Th.Brt))
            g.DrawString(displayName, nf, nb, x + 26, y + 7);
        if (hasAlias)
        {
            using var hnf = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Color.FromArgb(85, 85, 100));
            var nsz = g.MeasureString(displayName, nf);
            g.DrawString(cl.MachineName, hnf, hnb, x + 30 + (int)nsz.Width, y + 11);
        }

        using var sf = new Font("Segoe UI", 7.5f);
        using var sb = new SolidBrush(Th.Yel);
        string ver = string.IsNullOrEmpty(cl.ClientVersion) ? "" : $" · v{cl.ClientVersion}";
        g.DrawString($"Connected · waiting for first report{ver}", sf, sb, x + 26, y + 23);
    }

    int DrawCollapsed(Graphics g, int x, int y, int w, RemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        int h = 42;
        Color brd = stale ? Th.Org : Th.Grn;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(50, brd), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(200, brd)))
            g.FillRectangle(ac, x + 1, y + 6, 4, h - 12);

        bool sel = _selectedMachines.Contains(r.MachineName);
        var cbR = new Rectangle(x + w - 20, y + 15, 12, 12);
        _btns.Add((cbR, r.MachineName, "select"));
        _btns.Add((new Rectangle(x, y, w, h), r.MachineName, "toggle"));
        using (var cbBg = new SolidBrush(sel ? Th.Grn : Color.FromArgb(30, Th.Brd))) g.FillRectangle(cbBg, cbR);
        using (var cbPen = new Pen(sel ? Th.Grn : Th.Dim, 1f)) g.DrawRectangle(cbPen, cbR);
        if (sel) { using var ck = new Pen(Color.Black, 1.5f); g.DrawLine(ck, cbR.X + 2, cbR.Y + 6, cbR.X + 4, cbR.Y + 9); g.DrawLine(ck, cbR.X + 4, cbR.Y + 9, cbR.X + 9, cbR.Y + 3); }

        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 12, y + 17, 8, 8);
        var alias = _engine.Store.GetAlias(r.MachineName);
        bool hasAlias = !string.IsNullOrEmpty(alias);
        string displayName = hasAlias ? alias! : r.MachineName;
        using var nf = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using var nb = new SolidBrush(Th.Brt);
        g.DrawString(displayName, nf, nb, x + 28, hasAlias ? y + 8 : y + 13);
        if (hasAlias)
        {
            using var hnf = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Color.FromArgb(85, 85, 100));
            g.DrawString(r.MachineName, hnf, hnb, x + 28, y + 25);
        }
        var nsz = g.MeasureString(displayName, nf);
        bool outdated = ServerEngine.ClientNeedsUpdate(cl.ClientVersion);
        int mx = x + 32 + (int)nsz.Width + 14;
        if (outdated)
        {
            string badge = $"⚠ v{cl.ClientVersion}";
            using var bf = new Font("Segoe UI", 7f);
            using var bb = new SolidBrush(Th.Org);
            g.DrawString(badge, bf, bb, mx - 4, hasAlias ? y + 9 : y + 14);
            var bsz = g.MeasureString(badge, bf);
            mx += (int)bsz.Width + 4;
        }
        using var mf = new Font("Segoe UI", 8f);
        int chipLeft = HeaderChipX(x, w, 60) - 8;
        string osText = "OS: " + ShortOsLabel(r);
        var osSz = g.MeasureString(osText, mf);
        if (mx + osSz.Width + 10 < chipLeft)
        {
            using var osb = new SolidBrush(ServerEngine.IsLinuxClient(cl) ? Th.Cyan : Th.Dim);
            g.DrawString(osText, mf, osb, mx, y + 14);
            mx += (int)osSz.Width + 16;
        }

        if (r.TotalLoadPercent.HasValue)
        { using var lb = new SolidBrush(Th.LdC(r.TotalLoadPercent.Value)); g.DrawString($"{r.TotalLoadPercent.Value:0}%", mf, lb, mx, y + 14); mx += 48; }
        if (r.PackageTemperatureC is > 0)
        { using var tb = new SolidBrush(Th.TpC(r.PackageTemperatureC.Value)); g.DrawString($"{r.PackageTemperatureC.Value:0}°C", mf, tb, mx, y + 14); mx += 52; }
        if (r.PackageFrequencyMHz is > 0)
        { using var fb = new SolidBrush(Th.Blu); g.DrawString(Th.FF(r.PackageFrequencyMHz), mf, fb, mx, y + 14); mx += 60; }
        if (r.RamTotalGB > 0)
        { int pct = (int)(r.RamUsedGB / r.RamTotalGB * 100); using var rb = new SolidBrush(pct > 90 ? Th.Red : pct > 70 ? Th.Org : Th.Grn); g.DrawString($"RAM {pct}%", mf, rb, mx, y + 14); mx += 56; }
        if (r.Drives.Count > 0)
        { var d0 = r.Drives[0]; int dpct = d0.TotalGB > 0 ? (int)((d0.TotalGB - d0.FreeGB) / d0.TotalGB * 100) : 0; using var db = new SolidBrush(dpct > 90 ? Th.Red : dpct > 75 ? Th.Org : Th.Dim); g.DrawString($"{d0.Name} {d0.FreeGB:0.0}G", mf, db, mx, y + 14); mx += 72; }
        if (r.NetUpKBps + r.NetDownKBps > 0.5)
        { using var netb = new SolidBrush(Th.Dim); g.DrawString($"↑{FmtNet(r.NetUpKBps)} ↓{FmtNet(r.NetDownKBps)}", mf, netb, mx, y + 14); }

        // LIVE / MON / IDLE chip
        Color chipC = cl.SendMode == "full" ? Th.Grn : cl.SendMode == "monitor" ? Th.Org : Th.Dim;
        string chipTxt = cl.SendMode == "full" ? "● LIVE" : cl.SendMode == "monitor" ? "◉ MON" : "○ IDLE";
        using (var chipF = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        {
            var csz = g.MeasureString(chipTxt, chipF);
            int cx = HeaderChipX(x, w, (int)csz.Width), cy = y + 14;
            using (var chipBg = new SolidBrush(Color.FromArgb(35, chipC)))
            using (var chipPath = Th.RR(cx - 4, cy - 2, (int)csz.Width + 8, (int)csz.Height + 4, 4))
            { g.FillPath(chipBg, chipPath); using var chipPen = new Pen(Color.FromArgb(60, chipC), 1f); g.DrawPath(chipPen, chipPath); }
            using var chipBr = new SolidBrush(chipC);
            g.DrawString(chipTxt, chipF, chipBr, cx, cy);
        }

        using var ef = new Font("Segoe UI", 10f);
        using var eb = new SolidBrush(Th.Dim);
        g.DrawString("▾", ef, eb, x + w - 38, y + 12);

        return h;
    }

    int DrawExpanded(Graphics g, int x, int y, int w, RemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        bool linux = ServerEngine.IsLinuxClient(cl);
        const int BtnH = 26, BtnGap = 8;
        int hdrH = 100, h = hdrH + 6 + BtnH + BtnGap + BtnH + 6;
        Color brd = stale ? Th.Org : Th.Grn;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 8); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(50, brd), 1f)) { using var p = Th.RR(x, y, w, h, 8); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(200, brd)))
            g.FillRectangle(ac, x + 1, y + 8, 4, h - 16);

        bool selExp = _selectedMachines.Contains(r.MachineName);
        var cbRExp = new Rectangle(x + w - 20, y + 15, 12, 12);
        _btns.Add((cbRExp, r.MachineName, "select"));
        _btns.Add((new Rectangle(x, y, w, 32), r.MachineName, "toggle"));
        using (var cbBg2 = new SolidBrush(selExp ? Th.Grn : Color.FromArgb(30, Th.Brd))) g.FillRectangle(cbBg2, cbRExp);
        using (var cbPen2 = new Pen(selExp ? Th.Grn : Th.Dim, 1f)) g.DrawRectangle(cbPen2, cbRExp);
        if (selExp) { using var ck2 = new Pen(Color.Black, 1.5f); g.DrawLine(ck2, cbRExp.X + 2, cbRExp.Y + 6, cbRExp.X + 4, cbRExp.Y + 9); g.DrawLine(ck2, cbRExp.X + 4, cbRExp.Y + 9, cbRExp.X + 9, cbRExp.Y + 3); }

        using (var ef = new Font("Segoe UI", 10f)) using (var eb = new SolidBrush(Th.Dim))
            g.DrawString("▴", ef, eb, x + w - 38, y + 12);
        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 12, y + 17, 8, 8);
        var alias2 = _engine.Store.GetAlias(r.MachineName);
        bool hasAlias2 = !string.IsNullOrEmpty(alias2);
        string displayName2 = hasAlias2 ? alias2! : r.MachineName;
        using var expNf = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        using (var nb2 = new SolidBrush(Th.Brt)) g.DrawString(displayName2, expNf, nb2, x + 28, y + 7);
        var expNsz = g.MeasureString(displayName2, expNf);
        int expX = x + 30 + (int)expNsz.Width;
        if (hasAlias2)
        {
            using var hnf = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Color.FromArgb(85, 85, 100));
            g.DrawString(r.MachineName, hnf, hnb, expX, y + 11);
            expX += (int)g.MeasureString(r.MachineName, hnf).Width + 4;
        }
        if (!string.IsNullOrEmpty(cl.ClientVersion))
        {
            bool outdated = ServerEngine.ClientNeedsUpdate(cl.ClientVersion);
            using var vf = new Font("Segoe UI", 7f);
            using var vb = new SolidBrush(outdated ? Th.Org : Th.Dim);
            g.DrawString($"v{cl.ClientVersion}" + (outdated ? " ⚠" : ""), vf, vb, expX, y + 11);
        }
        using (var cf = new Font("Segoe UI", 7.5f)) using (var cb = new SolidBrush(Th.Dim))
            g.DrawString(r.CpuName, cf, cb, x + 28, y + 27);

        // LIVE / MON / IDLE chip
        Color chipC = cl.SendMode == "full" ? Th.Grn : cl.SendMode == "monitor" ? Th.Org : Th.Dim;
        string chipTxt = cl.SendMode == "full" ? "● LIVE" : cl.SendMode == "monitor" ? "◉ MON" : "○ IDLE";
        using (var chipF = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        {
            var csz = g.MeasureString(chipTxt, chipF);
            int cx = HeaderChipX(x, w, (int)csz.Width), cy = y + 14;
            using (var chipBg = new SolidBrush(Color.FromArgb(35, chipC)))
            using (var chipPath = Th.RR(cx - 4, cy - 2, (int)csz.Width + 8, (int)csz.Height + 4, 4))
            { g.FillPath(chipBg, chipPath); using var chipPen = new Pen(Color.FromArgb(60, chipC), 1f); g.DrawPath(chipPen, chipPath); }
            using var chipBr = new SolidBrush(chipC);
            g.DrawString(chipTxt, chipF, chipBr, cx, cy);
        }

        // Separator: header / metrics
        using (var sep = new Pen(Color.FromArgb(35, Th.Brd), 1f))
            g.DrawLine(sep, x + 12, y + 43, x + w - 12, y + 43);

        // Metrics row 1 — CPU
        int my = y + 59, mx = x + 14;
        int cpuMetricsX = mx;
        DrawMetric(g, mx, my, "LOAD", Th.F(r.TotalLoadPercent, "0", "%"), Th.LdC(r.TotalLoadPercent ?? 0)); mx += 112;
        DrawMetric(g, mx, my, "FREQ", Th.FF(r.PackageFrequencyMHz), Th.Blu); mx += 112;
        DrawMetric(g, mx, my, "TEMP", Th.F(r.PackageTemperatureC, "0.0", "°C"), Th.TpC(r.PackageTemperatureC ?? 0)); mx += 112;
        if (!linux) _btns.Add((new Rectangle(cpuMetricsX - 4, my - 13, Math.Min(336, w - 28), 32), r.MachineName, "cpu_detail"));
        if (r.PackagePowerW is > 0) { DrawMetric(g, mx, my, "PWR", Th.F(r.PackagePowerW, "0.0", "W"), Th.Org); mx += 112; }
        if (r.GpuLoadPercent.HasValue) DrawMetric(g, mx, my, "GPU", Th.F(r.GpuLoadPercent, "0", "%"), Th.LdC(r.GpuLoadPercent ?? 0));

        // Metrics row 2 — storage & net
        int my2 = y + 87, mx2 = x + 14;
        if (r.RamTotalGB > 0) { int pct = (int)(r.RamUsedGB / r.RamTotalGB * 100); DrawMetric(g, mx2, my2, $"RAM {pct}%", $"{FmtGb(r.RamUsedGB, "0.0")} GB / {FmtGb(r.RamTotalGB, "0.0")} GB", pct > 90 ? Th.Red : pct > 70 ? Th.Org : Th.Grn); mx2 += 172; }
        foreach (var drv in r.Drives.Take(3)) { int pct = drv.TotalGB > 0 ? (int)((drv.TotalGB - drv.FreeGB) / drv.TotalGB * 100) : 0; int driveX = mx2; DrawMetric(g, mx2, my2, drv.Name, $"{drv.FreeGB:0.0} G free", pct > 90 ? Th.Red : pct > 75 ? Th.Org : Th.Dim); _btns.Add((new Rectangle(driveX - 4, my2 - 13, 100, 32), r.MachineName, "files_path:" + drv.Name)); mx2 += 104; }
        if (r.GpuVramTotalMB is > 0 && r.GpuVramUsedMB.HasValue) { string vram = r.GpuVramTotalMB > 1024 ? $"{FmtGb(r.GpuVramUsedMB.Value / 1024.0, "0.1")}/{FmtGb(r.GpuVramTotalMB.Value / 1024.0, "0.0")}G" : $"{r.GpuVramUsedMB.Value:0}/{r.GpuVramTotalMB.Value:0}M"; DrawMetric(g, mx2, my2, "VRAM", vram, Th.Blu); mx2 += 112; }
        if (r.NetUpKBps + r.NetDownKBps > 0.5) DrawMetric(g, mx2, my2, "NET ↑↓", $"{FmtNet(r.NetUpKBps)}/{FmtNet(r.NetDownKBps)}", Th.Dim);

        // Separator: metrics / buttons
        using (var sep2 = new Pen(Color.FromArgb(35, Th.Brd), 1f))
            g.DrawLine(sep2, x + 12, y + hdrH, x + w - 12, y + hdrH);

        // Row 1 - session launchers
        int by = y + hdrH + 6, bx = x + 14;
        int rowLimit = x + w - 14;
        bool TryDrawTopBtn(int width, int step, string text, Color color, string action)
        {
            if (bx + width > rowLimit) return false;
            DrawBtn(g, bx, by, width, BtnH, text, color, r.MachineName, action);
            bx += step;
            return true;
        }

        if (linux)
        {
            TryDrawTopBtn(78, 86, "Bash", Th.Cyan, "bash");
        }
        else
        {
            TryDrawTopBtn(72, 80, "CMD", Th.Cyan, "cmd");
            TryDrawTopBtn(104, 112, "PowerShell", Th.Blu, "powershell");
        }
        TryDrawTopBtn(74, 82, "Files", Th.Yel, "files");
        TryDrawTopBtn(84, 92, "Services", Th.Grn, "services");
        if (!linux)
        {
            TryDrawTopBtn(68, 76, "RDP", Th.Cyan, "rdp");
        }
        if (ServerEngine.ClientNeedsUpdate(cl.ClientVersion))
            TryDrawTopBtn(80, 88, "Update", Th.Org, "update");
        int row1NextX = bx;

        // Row 2 - info tools (left) + danger zone (right-aligned)
        int by2 = by + BtnH + BtnGap, bx2 = x + 14;
        bool isPaw = _engine.Store.IsPaw(r.MachineName);
        int rightStart = x + w - 14 - 74 - 68 - 82;
        if (!linux) rightStart -= 14 + 78;
        bool TryDrawLeftBtn(int width, int step, string text, Color color, string action)
        {
            if (bx2 + width > rightStart - 8) return false;
            DrawBtn(g, bx2, by2, width, BtnH, text, color, r.MachineName, action);
            bx2 += step;
            return true;
        }

        TryDrawLeftBtn(80, 88, "Procs", Th.Blu, "processes");
        TryDrawLeftBtn(60, 68, "Info", Th.Cyan, "sysinfo");
        TryDrawLeftBtn(70, 78, "Health", Th.Grn, "health");
        if (!linux)
        {
            TryDrawLeftBtn(92, 100, "Screenshot", Th.Cyan, "screenshot");
            TryDrawLeftBtn(74, 82, "Events", Th.Yel, "events");
        }
        if (TryDrawLeftBtn(68, 76, "Msg", Th.Dim, "msg")) { }
        else if (row1NextX + 68 <= x + w - 14)
            DrawBtn(g, row1NextX, by, 68, BtnH, "Msg", Th.Dim, r.MachineName, "msg");

        int dx = x + w - 14;
        dx -= 74; DrawDangerBtn(g, dx, by2, 72, BtnH, "Forget", Th.Dim, r.MachineName, "forget");
        dx -= 68; DrawDangerBtn(g, dx, by2, 66, BtnH, "Off", Th.Red, r.MachineName, "shutdown");
        dx -= 82; DrawDangerBtn(g, dx, by2, 80, BtnH, "Restart", Th.Org, r.MachineName, "restart");
        if (!linux)
        {
            dx -= 14;
            using (var vsp = new Pen(Color.FromArgb(45, Th.Brd), 1f))
                g.DrawLine(vsp, dx + 6, by2 + 4, dx + 6, by2 + 22);
            dx -= 78; DrawBtn(g, dx, by2, 76, BtnH, isPaw ? "PAW yes" : "PAW", isPaw ? Th.Mag : Th.Dim, r.MachineName, "paw");
        }

        return h;
    }

    void DrawBtn(Graphics g, int x, int y, int w, int h, string text, Color c, string machine, string action)
    {
        var rect = new Rectangle(x, y, w, h);
        _btns.Add((rect, machine, action));
        using var bg = new SolidBrush(Color.FromArgb(28, c));
        using var p = Th.RR(x, y, w, h, 6);
        g.FillPath(bg, p);
        using var pen = new Pen(Color.FromArgb(80, c), 1f);
        g.DrawPath(pen, p);
        using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var b = new SolidBrush(c);
        var sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    void DrawDangerBtn(Graphics g, int x, int y, int w, int h, string text, Color c, string machine, string action)
    {
        var rect = new Rectangle(x, y, w, h);
        _btns.Add((rect, machine, action));
        using var bg = new SolidBrush(Color.FromArgb(16, c));
        using var p = Th.RR(x, y, w, h, 6);
        g.FillPath(bg, p);
        using var pen = new Pen(Color.FromArgb(55, c), 1f);
        g.DrawPath(pen, p);
        using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var b = new SolidBrush(Color.FromArgb(180, c));
        var sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    void DrawOffline(Graphics g, int x, int y, int w, ApprovedClient ac)
    {
        int h = 48;
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(Th.IsDark ? 35 : 90, Th.Brd), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac2 = new SolidBrush(Color.FromArgb(80, Th.Dim)))
            g.FillRectangle(ac2, x + 1, y + 6, 4, h - 12);
        using (var dot = new SolidBrush(Th.Dim)) g.FillEllipse(dot, x + 12, y + 12, 7, 7);
        string offDisplay = string.IsNullOrEmpty(ac.Alias) ? ac.Name : ac.Alias;
        using (var nf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Dim))
            g.DrawString(offDisplay, nf, nb, x + 24, y + 6);
        if (!string.IsNullOrEmpty(ac.Alias))
        {
            using var hnf2 = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Th.Dim);
            g.DrawString(ac.Name, hnf2, hnb, x + 26, y + 25);
        }
        var ago = DateTime.UtcNow - ac.Seen;
        string agoStr = ago.TotalDays >= 1 ? $"{(int)ago.TotalDays}d ago" : ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h ago" : ago.TotalMinutes >= 1 ? $"{(int)ago.TotalMinutes}m ago" : "just now";
        using (var sf2 = new Font("Segoe UI", 7.5f)) using (var sb2 = new SolidBrush(Th.Dim))
            g.DrawString($"Offline · {agoStr}", sf2, sb2, x + 150, y + 11);
        if (!string.IsNullOrEmpty(ac.Ip)) { using var if2 = new Font("Segoe UI", 7f); using var ib = new SolidBrush(Th.Dim); g.DrawString(ac.Ip, if2, ib, x + 290, y + 11); }
        if (!string.IsNullOrEmpty(ac.Mac)) DrawBtn(g, x + w - 162, y + 5, 72, 24, "⚡ Wake", Th.Yel, ac.Name, "wake_offline");
        else DrawBtn(g, x + w - 162, y + 5, 72, 24, "Set MAC", Th.Dim, ac.Name, "set_mac_offline");
        DrawBtn(g, x + w - 82, y + 5, 72, 24, "🗑 Forget", Th.Dim, ac.Name, "forget_offline");
    }

    void DrawPendingApproval(Graphics g, int x, int y, int w, PendingClientApproval pending)
    {
        int h = 38;
        using (var bg = new SolidBrush(Color.FromArgb(32, 30, 22))) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(70, Th.Yel), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(170, Th.Yel)))
            g.FillRectangle(ac, x + 1, y + 6, 4, h - 12);

        using (var dot = new SolidBrush(Th.Yel)) g.FillEllipse(dot, x + 12, y + 15, 8, 8);
        using (var nf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Brt))
            g.DrawString(pending.MachineName, nf, nb, x + 26, y + 8);
        using (var sf = new Font("Segoe UI", 7.5f)) using (var sb = new SolidBrush(Th.Dim))
        {
            string age = $"{Math.Max(0, (int)(DateTime.UtcNow - pending.RequestedAt).TotalSeconds)}s ago";
            string version = string.IsNullOrEmpty(pending.ClientVersion) ? "" : $" · v{pending.ClientVersion}";
            g.DrawString($"Awaiting approval · {pending.Ip}{version} · {age}", sf, sb, x + 170, y + 11);
        }
        DrawBtn(g, x + w - 178, y + 7, 82, 24, "Approve", Th.Grn, pending.MachineName, "approve_pending");
        DrawDangerBtn(g, x + w - 88, y + 7, 78, 24, "Reject", Th.Red, pending.MachineName, "reject_pending");
    }

    static int HeaderChipX(int x, int w, int textWidth) => x + w - textWidth - 54;

    static string FmtNet(double kbps) => kbps >= 1024 ? $"{kbps / 1024.0:0.0}M" : $"{kbps:0}K";
    static string FmtGb(double value, string format) => value.ToString(format);
    static string ShortOsLabel(MachineReport r)
    {
        string os = r.OsVersion ?? "";
        if (os.Contains("linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        if (os.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            string clean = os.Replace("Microsoft", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Windows NT", "Windows", StringComparison.OrdinalIgnoreCase)
                .Replace("  ", " ")
                .Trim();
            return clean.Length > 24 ? clean[..24].TrimEnd() : clean;
        }
        return string.IsNullOrWhiteSpace(os) ? "Unknown" : (os.Length > 24 ? os[..24].TrimEnd() : os);
    }
    static void DrawMetric(Graphics g, int x, int y, string l, string v, Color c)
    {
        using var lf = new Font("Segoe UI", 6f); using var lb = new SolidBrush(Color.FromArgb(110, Th.Brt));
        g.DrawString(l, lf, lb, x, y - 11);
        using var vf = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold); using var vb = new SolidBrush(c);
        g.DrawString(v, vf, vb, x, y);
    }

    void DrawLog(Graphics g, int x, int y, int w, int h)
    {
        if (h < 24) return;
        using (var bg = new SolidBrush(Th.Card))
        { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(Th.IsDark ? 55 : 95, Th.Brd), 1f))
        { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Dim))
            g.DrawString("LOG", hf, hb, x + 8, y + 3);

        int lh = 13, ml = Math.Max(1, (h - 18) / lh);
        var entries = _engine.Log.Recent(ml);
        using var ef = new Font("Consolas", 7f);
        int ey = y + 18;
        foreach (var (t, m, c) in entries)
        {
            if (ey + lh > y + h) break;
            using var tb = new SolidBrush(Th.Dim);
            g.DrawString(t.ToString("HH:mm:ss"), ef, tb, x + 6, ey);
            using var mb = new SolidBrush(c);
            g.DrawString(m, ef, mb, x + 68, ey);
            ey += lh;
        }
    }
}
