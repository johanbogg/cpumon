using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

public interface IServerPlatformServices
{
    void SetClipboardText(string text);
    void Confirm(DashboardMessageBoxRequest request);
    void Prompt(DashboardPromptRequest request);
    void PickFile(DashboardFilePickerRequest request);
    void OpenExternal(string target);

    void ShowApprovedClients();
    void ShowAlerts();
    void ShowProcessDialog(string machineName);
    void UpdateProcessDialog(string machineName);
    void ShowSysInfoDialog(string machineName);
    void ShowServicesDialog(string machineName);
    void ShowEventsDialog(string machineName);
    void ShowCpuDetailDialog(string machineName, CpuDetailReport detail);
    void ShowScreenshotDialog(string machineName, ScreenshotData shot);
    void ShowHealthDialog(string machineName);
    void ShowTerminal(string machineName, string shell);
    void ShowFileBrowser(string machineName, string? initialPath);
    void ShowRdp(string machineName);
    void PromptSendUserMessage(string machineName, Action<string> onSubmit);
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

    public void Confirm(DashboardMessageBoxRequest request)
    {
        var icon = request.Kind == DashboardConfirmKind.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.None;
        OnUi(() =>
        {
            if (MessageBox.Show(request.Message, request.Title, MessageBoxButtons.YesNo, icon) == DialogResult.Yes)
            {
                request.OnConfirm();
                _invalidateDashboard();
            }
        });
    }

    public void Prompt(DashboardPromptRequest request)
    {
        OnUi(() =>
        {
            using var dlg = new Form { Text = request.Title, Size = new Size(300, 112), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Th.Bg, ForeColor = Th.Brt };
            var lbl = new Label { Text = request.Label, Location = new Point(12, 12), AutoSize = true, ForeColor = Th.Dim };
            var txt = new TextBox { Location = new Point(12, 34), Width = 260, BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(116, 62), Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(197, 62), Width = 75 };
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            if (dlg.ShowDialog(_owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
            {
                request.OnSubmit(txt.Text);
                _invalidateDashboard();
            }
        });
    }

    public void PickFile(DashboardFilePickerRequest request)
    {
        OnUi(() =>
        {
            using var ofd = new OpenFileDialog { Title = request.Title, Filter = request.Filter };
            if (ofd.ShowDialog(_owner) != DialogResult.OK) return;
            request.OnFileSelected(ofd.FileName);
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

    public void UpdateProcessDialog(string machineName)
    {
        OnUi(() =>
        {
            if (_procDialogs.TryGetValue(machineName, out var existing)
                && !existing.IsDisposed
                && _engine.Clients.TryGetValue(machineName, out var cl)
                && cl.LastProcessList != null)
                existing.UpdateList(cl.LastProcessList);
        });
    }

    public void ShowSysInfoDialog(string machineName)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
            using var d = new SysInfoDialog(cl);
            d.ShowDialog(_owner);
        });
    }

    public void ShowServicesDialog(string machineName)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
            using var d = new ServicesDialog(cl);
            d.ShowDialog(_owner);
        });
    }

    public void ShowEventsDialog(string machineName)
    {
        OnUi(() =>
        {
            if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
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

    public void PromptSendUserMessage(string machineName, Action<string> onSubmit)
    {
        OnUi(() =>
        {
            using var dlg = new Form { Text = "Send Message", Size = new Size(420, 148), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Th.Bg, ForeColor = Th.Brt, MaximizeBox = false, MinimizeBox = false };
            var txt = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 10f), Location = new Point(12, 12), Size = new Size(390, 28), BorderStyle = BorderStyle.FixedSingle };
            txt.PlaceholderText = "Message to show on remote screen...";
            var send = new Button { Text = "Send", DialogResult = DialogResult.OK, Location = new Point(12, 52), Size = new Size(80, 30), BackColor = Color.FromArgb(30, 60, 30), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat }; send.FlatAppearance.BorderColor = Th.Grn;
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(100, 52), Size = new Size(80, 30), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { txt, send, cancel }); dlg.AcceptButton = send;
            if (dlg.ShowDialog(_owner) == DialogResult.OK)
                onSubmit(txt.Text);
        });
    }

    void OnUi(Action action)
    {
        if (_owner.IsDisposed) return;
        if (_owner.InvokeRequired) _owner.BeginInvoke(action);
        else action();
    }
}
