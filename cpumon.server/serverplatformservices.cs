using System;
using System.Drawing;
using System.Windows.Forms;

public interface IServerPlatformServices
{
    void SetClipboardText(string text);
    void Confirm(DashboardMessageBoxRequest request);
    void Prompt(DashboardPromptRequest request);
    void PickFile(DashboardFilePickerRequest request);
    void OpenExternal(string target);
}

public sealed class WinFormsServerPlatformServices : IServerPlatformServices
{
    readonly Form _owner;
    readonly Action _invalidateDashboard;

    public WinFormsServerPlatformServices(Form owner, Action invalidateDashboard)
    {
        _owner = owner;
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

    void OnUi(Action action)
    {
        if (_owner.IsDisposed) return;
        if (_owner.InvokeRequired) _owner.BeginInvoke(action);
        else action();
    }
}
