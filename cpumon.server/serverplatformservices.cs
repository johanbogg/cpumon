using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

public interface IServerPlatformServices
{
    void SetClipboardText(string text);
    bool Confirm(string message, string title, DashboardConfirmKind kind);
    string? Prompt(string title, string label);
    string? PickFile(string title, string filter);
    void OpenExternal(string target);

    void ShowApprovedClients();
    void ShowAlerts();
    void ShowProcessDialog(string machineName);
    void UpdateProcessDialog(RemoteClient cl);
    void ShowSysInfoDialog(RemoteClient cl);
    void ShowServicesDialog(RemoteClient cl);
    void ShowEventsDialog(RemoteClient cl);
    void ShowCpuDetailDialog(string machineName, CpuDetailReport detail);
    void ShowScreenshotDialog(string machineName, ScreenshotData shot);
    void ShowHealthDialog(string machineName);
    void ShowTerminal(string machineName, string shell);
    void ShowFileBrowser(string machineName, string? initialPath);
    void ShowRdp(string machineName);
    string? PromptUserMessage(string machineName);
    void ShowBootstrapUrl(string url, DateTime expiresAt);
}

public sealed class WinFormsServerPlatformServices : IServerPlatformServices
{
    readonly Form _owner;
    readonly ServerEngine _engine;
    readonly Action _invalidateDashboard;
    readonly Dictionary<string, ProcDialog> _procDialogs = new(StringComparer.OrdinalIgnoreCase);

    public WinFormsServerPlatformServices(Form owner, ServerEngine engine, Action invalidateDashboard)
    {
        _owner = owner;
        _engine = engine;
        _invalidateDashboard = invalidateDashboard;
    }

    public void SetClipboardText(string text)
    {
        OnUi(() =>
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { LogSink.Warn("Server.UI", "Clipboard.SetText failed", ex); }
        });
    }

    public bool Confirm(string message, string title, DashboardConfirmKind kind)
    {
        var icon = kind == DashboardConfirmKind.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.None;
        bool ok = OnUiSync(() => MessageBox.Show(_owner, message, title, MessageBoxButtons.YesNo, icon) == DialogResult.Yes);
        if (ok) _invalidateDashboard();
        return ok;
    }

    public string? Prompt(string title, string label)
    {
        string? text = OnUiSync<string?>(() =>
        {
            using var dlg = new Form { Text = title, Size = new Size(300, 112), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Th.Bg, ForeColor = Th.Brt };
            DwmDark.Hook(dlg);
            var lbl = new Label { Text = label, Location = new Point(12, 12), AutoSize = true, ForeColor = Th.Dim };
            var txt = new TextBox { Location = new Point(12, 34), Width = 260, BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle };
            var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(116, 62), Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(197, 62), Width = 75 };
            dlg.AcceptButton = btnOk; dlg.CancelButton = cancel;
            dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, cancel });
            return dlg.ShowDialog(_owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text) ? txt.Text : null;
        });
        if (text != null) _invalidateDashboard();
        return text;
    }

    public string? PickFile(string title, string filter)
    {
        return OnUiSync<string?>(() =>
        {
            using var ofd = new OpenFileDialog { Title = title, Filter = filter };
            return ofd.ShowDialog(_owner) == DialogResult.OK ? ofd.FileName : null;
        });
    }

    public void OpenExternal(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { LogSink.Warn("Server.UI", $"Failed to open {target}", ex); }
    }

    public void ShowApprovedClients()
    {
        OnUi(() =>
        {
            using var d = new ApprovedClientsDialog(_engine.Store, _engine.Clients, _engine.Log);
            d.ShowDialog(_owner);
        });
    }

    public void ShowAlerts()
    {
        OnUi(() =>
        {
            using var d = new AlertConfigDialog(_engine.Alerts);
            if (d.ShowDialog(_owner) == DialogResult.OK) _invalidateDashboard();
        });
    }

    public void ShowProcessDialog(string machineName)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
            if (_procDialogs.TryGetValue(cl.MachineName, out var existing) && !existing.IsDisposed)
            {
                existing.BringToFront();
                return;
            }
            var d = new ProcDialog(cl);
            if (cl.LastProcessList != null) d.UpdateList(cl.LastProcessList);
            _procDialogs[cl.MachineName] = d;
            d.FormClosed += (_, _) => _procDialogs.Remove(cl.MachineName);
            d.Show(_owner);
        });
    }

    public void UpdateProcessDialog(RemoteClient cl)
    {
        OnUi(() =>
        {
            if (_procDialogs.TryGetValue(cl.MachineName, out var existing)
                && !existing.IsDisposed
                && cl.LastProcessList != null)
                existing.UpdateList(cl.LastProcessList);
        });
    }

    public void ShowSysInfoDialog(RemoteClient cl)
    {
        OnUi(() =>
        {
            using var d = new SysInfoDialog(cl);
            d.ShowDialog(_owner);
        });
    }

    public void ShowServicesDialog(RemoteClient cl)
    {
        OnUi(() =>
        {
            using var d = new ServicesDialog(cl);
            d.ShowDialog(_owner);
        });
    }

    public void ShowEventsDialog(RemoteClient cl)
    {
        OnUi(() =>
        {
            using var d = new EventViewerDialog(cl);
            d.ShowDialog(_owner);
        });
    }

    public void ShowCpuDetailDialog(string machineName, CpuDetailReport detail)
    {
        OnUi(() => new CpuDetailDialog(machineName, detail).Show(_owner));
    }

    public void ShowScreenshotDialog(string machineName, ScreenshotData shot)
    {
        OnUi(() => new ScreenshotPreviewDialog(machineName, shot).Show(_owner));
    }

    public void ShowHealthDialog(string machineName)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
            new HealthDialog(cl, _engine.Store).Show(_owner);
        });
    }

    public void ShowTerminal(string machineName, string shell)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
            new TerminalDialog(cl, shell).Show(_owner);
            var tag = shell == "cmd" ? "CMD" : shell == "powershell" ? "PS" : "Bash";
            _engine.Log.Add($"{tag}→{machineName}", Th.Cyan);
        });
    }

    public void ShowFileBrowser(string machineName, string? initialPath)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
            var dlg = initialPath != null ? new FileBrowserDialog(cl, initialPath) : new FileBrowserDialog(cl);
            dlg.Show(_owner);
            _engine.Log.Add(initialPath != null ? $"Files->{machineName} {initialPath}" : $"Files→{machineName}", Th.Yel);
        });
    }

    public void ShowRdp(string machineName)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
            var rdpId = Guid.NewGuid().ToString("N")[..12];
            var name = machineName;
            var rdpViewer = new RdpViewerDialog(name, rdpId,
                cmd => { if (_engine.Clients.TryGetValue(name, out var rc)) try { rc.Send(cmd); } catch { } },
                () => { if (_engine.Clients.TryGetValue(name, out var rc)) rc.RdpDialogs.TryRemove(rdpId, out _); });
            cl.RdpDialogs[rdpId] = rdpViewer;
            cl.Send(new ServerCommand { Cmd = "rdp_open", RdpId = rdpId, RdpFps = Proto.RdpFpsDefault, RdpQuality = Proto.RdpJpegQuality });
            rdpViewer.Show(_owner);
            _engine.Log.Add($"RDP→{name}", Th.Cyan);
        });
    }

    public void ShowBootstrapUrl(string url, DateTime expiresAt)
    {
        var localExpiry = expiresAt.ToLocalTime().ToString("HH:mm:ss");
        Console.Out.WriteLine($"* Web UI setup: {url} (valid until {localExpiry})");
        Console.Error.WriteLine($"* Web UI setup: {url} (valid until {localExpiry})");
        OnUi(() =>
        {
            using var dlg = new BootstrapUrlDialog(url, expiresAt);
            dlg.ShowDialog(_owner);
        });
    }

    public string? PromptUserMessage(string machineName)
    {
        return OnUiSync<string?>(() =>
        {
            using var dlg = new Form { Text = "Send Message", Size = new Size(420, 148), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Th.Bg, ForeColor = Th.Brt, MaximizeBox = false, MinimizeBox = false };
            DwmDark.Hook(dlg);
            var txt = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 10f), Location = new Point(12, 12), Size = new Size(390, 28), BorderStyle = BorderStyle.FixedSingle };
            txt.PlaceholderText = "Message to show on remote screen...";
            var send = new Button { Text = "Send", DialogResult = DialogResult.OK, Location = new Point(12, 52), Size = new Size(80, 30), BackColor = Color.FromArgb(30, 60, 30), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat }; send.FlatAppearance.BorderColor = Th.Grn;
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(100, 52), Size = new Size(80, 30), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { txt, send, cancel }); dlg.AcceptButton = send;
            return dlg.ShowDialog(_owner) == DialogResult.OK ? txt.Text : null;
        });
    }

    void OnUi(Action action)
    {
        if (_owner.IsDisposed || _owner.Disposing || !_owner.IsHandleCreated) return;
        try
        {
            if (_owner.InvokeRequired)
            {
                _owner.BeginInvoke(() =>
                {
                    if (_owner.IsDisposed || _owner.Disposing) return;
                    action();
                });
            }
            else
            {
                action();
            }
        }
        catch (InvalidOperationException) { }
    }

    T OnUiSync<T>(Func<T> func)
    {
        if (_owner.IsDisposed || _owner.Disposing || !_owner.IsHandleCreated) return default!;
        try
        {
            if (_owner.InvokeRequired)
                return (T)_owner.Invoke(func);
            return func();
        }
        catch (InvalidOperationException) { return default!; }
    }
}

sealed class BootstrapUrlDialog : Form
{
    readonly Timer _timer;
    readonly Label _countdown;
    readonly DateTime _expiresAt;

    public BootstrapUrlDialog(string url, DateTime expiresAt)
    {
        _expiresAt = expiresAt;
        DwmDark.Hook(this);
        Text = "cpumon Web UI — first-run setup";
        Size = new Size(640, 240);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg;
        ForeColor = Th.Brt;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;

        var intro = new Label
        {
            Text = "Open this one-time URL in a browser to create the operator account.\nThe link is single-use and only valid for a few minutes.",
            ForeColor = Th.Dim,
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(16, 16),
            Size = new Size(600, 40),
        };

        var urlBox = new TextBox
        {
            Text = url,
            ReadOnly = true,
            Location = new Point(16, 64),
            Size = new Size(600, 26),
            BackColor = Th.Card,
            ForeColor = Th.Cyan,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f),
        };
        urlBox.GotFocus += (_, _) => urlBox.SelectAll();

        var copy = new Button
        {
            Text = "Copy",
            Location = new Point(16, 104),
            Size = new Size(110, 32),
            BackColor = Color.FromArgb(30, 60, 30),
            ForeColor = Th.Grn,
            FlatStyle = FlatStyle.Flat,
        };
        copy.FlatAppearance.BorderColor = Th.Grn;
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(url); copy.Text = "Copied ✓"; }
            catch (Exception ex) { copy.Text = "Copy failed"; LogSink.Warn("Server.UI", "Clipboard.SetText failed", ex); }
        };

        var dismiss = new Button
        {
            Text = "Dismiss",
            DialogResult = DialogResult.OK,
            Location = new Point(506, 104),
            Size = new Size(110, 32),
            BackColor = Th.Card,
            ForeColor = Th.Dim,
            FlatStyle = FlatStyle.Flat,
        };

        _countdown = new Label
        {
            ForeColor = Th.Yel,
            Font = new Font("Segoe UI", 9f),
            Location = new Point(16, 156),
            Size = new Size(600, 24),
        };
        _timer = new Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateCountdown();
        UpdateCountdown();
        _timer.Start();

        AcceptButton = dismiss;
        Controls.AddRange(new Control[] { intro, urlBox, copy, dismiss, _countdown });
    }

    void UpdateCountdown()
    {
        var remaining = _expiresAt - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _countdown.Text = "Token expired — restart cpumon to regenerate.";
            _countdown.ForeColor = Th.Red;
            _timer.Stop();
            return;
        }
        _countdown.Text = $"Expires in {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }
}
