using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// Tray-launched operator management. Always works against the shared OperatorStore
// instance; if the web host happens to be running, kicks the affected user's open
// browser sessions on delete and on password reset so a stale cookie cannot keep
// acting under the changed identity. When no web host is running the dialog still
// works — it only ever touches operator.json directly.
public sealed class UsersDialog : Form
{
    readonly OperatorStore _store;
    readonly SessionStore? _sessions;
    readonly BootstrapTokenIssuer? _bootstrap;
    readonly CLog? _log;
    readonly ListView _list;
    readonly Button _addBtn;
    readonly Button _resetBtn;
    readonly Button _removeBtn;
    readonly Button _closeBtn;
    readonly Label _hint;

    public UsersDialog(OperatorStore store, SessionStore? sessions = null,
                       BootstrapTokenIssuer? bootstrap = null, CLog? log = null)
    {
        _store     = store ?? throw new ArgumentNullException(nameof(store));
        _sessions  = sessions;
        _bootstrap = bootstrap;
        _log       = log;

        Text = "Web Operators";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 360);
        BackColor = Th.Bg;
        ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);
        Icon = Th.MakeHexIcon(Th.Grn);

        _list = new ListView
        {
            Location  = new Point(18, 18),
            Size      = new Size(400, 294),
            View      = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            GridLines = false,
            BackColor = Th.Card,
            ForeColor = Th.Brt
        };
        _list.Columns.Add("Username", 150);
        _list.Columns.Add("Created", 120);
        _list.Columns.Add("Password changed", 120);
        _list.SelectedIndexChanged += (_, _) => UpdateButtonState();
        Controls.Add(_list);

        _addBtn    = MakeButton("Add user…",      new Point(432, 18),  Th.Grn);
        _resetBtn  = MakeButton("Reset password", new Point(432, 56),  Th.Cyan);
        _removeBtn = MakeButton("Remove",         new Point(432, 94),  Th.Red);
        _closeBtn  = MakeButton("Close",          new Point(432, 284), Th.Dim, subtle: true);

        _addBtn.Click    += (_, _) => OnAdd();
        _resetBtn.Click  += (_, _) => OnReset();
        _removeBtn.Click += (_, _) => OnRemove();
        _closeBtn.Click  += (_, _) => Close();

        Controls.Add(_addBtn);
        Controls.Add(_resetBtn);
        Controls.Add(_removeBtn);
        Controls.Add(_closeBtn);

        _hint = new Label
        {
            Location  = new Point(18, 318),
            Size      = new Size(524, 32),
            ForeColor = Th.Dim,
            Text      = "Removing a user or resetting their password also signs them out of the web UI immediately."
        };
        Controls.Add(_hint);

        AcceptButton = _closeBtn;
        CancelButton = _closeBtn;

        Reload();
    }

    void Reload()
    {
        var selected = SelectedUsername();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _store.List())
        {
            var item = new ListViewItem(new[]
            {
                a.Username,
                a.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                a.PasswordChangedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            })
            {
                Tag = a.Username
            };
            _list.Items.Add(item);
            if (selected != null && string.Equals(a.Username, selected, StringComparison.OrdinalIgnoreCase))
                item.Selected = true;
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;
        UpdateButtonState();
    }

    void UpdateButtonState()
    {
        bool any = _list.SelectedItems.Count > 0;
        _resetBtn.Enabled  = any;
        _removeBtn.Enabled = any && _store.Count > 1;
    }

    string? SelectedUsername() =>
        _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as string;

    void OnAdd()
    {
        bool wasEmpty = !_store.Exists;
        using var dlg = new AddUserDialog(_store);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // If the tray created the very first operator, the pre-issued bootstrap
        // URL must stop working so the identity can't be claimed by anyone else.
        if (wasEmpty)
        {
            _bootstrap?.Clear();
            _log?.Add($"Web UI: operator created via tray ({dlg.Username}); bootstrap link cleared", Th.Grn);
        }
        else
        {
            _log?.Add($"Web UI: operator added via tray ({dlg.Username})", Th.Grn);
        }
        Reload();
    }

    void OnReset()
    {
        var username = SelectedUsername();
        if (username == null) return;
        using var dlg = new ResetPasswordDialog(_store, username);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var kicked = _sessions?.InvalidateByUsername(username) ?? 0;
        _log?.Add($"Web UI: password reset via tray ({username}); {kicked} session(s) signed out", Th.Cyan);
        Reload();
    }

    void OnRemove()
    {
        var username = SelectedUsername();
        if (username == null) return;
        if (_store.Count <= 1)
        {
            MessageBox.Show(this, "Cannot remove the last operator — the web UI would be unreachable.",
                            "Remove operator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var r = MessageBox.Show(this,
            $"Remove operator '{username}'?\n\nAny browser sessions they have open will be signed out immediately.",
            "Remove operator", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        try
        {
            _store.Remove(username);
            var kicked = _sessions?.InvalidateByUsername(username) ?? 0;
            _log?.Add($"Web UI: operator removed via tray ({username}); {kicked} session(s) signed out", Th.Org);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Remove operator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static Button MakeButton(string text, Point location, Color accent, bool subtle = false)
    {
        var b = new Button
        {
            Text      = text,
            Location  = location,
            Size      = new Size(110, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = subtle ? Th.Card : Color.FromArgb(42, accent),
            ForeColor = subtle ? Th.Brt : accent,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(90, accent);
        return b;
    }
}

sealed class AddUserDialog : Form
{
    readonly OperatorStore _store;
    readonly TextBox _username = new() { Width = 240 };
    readonly TextBox _password = new() { Width = 240, UseSystemPasswordChar = true };
    readonly TextBox _confirm  = new() { Width = 240, UseSystemPasswordChar = true };
    readonly Label   _msg      = new() { ForeColor = Th.Red, Size = new Size(330, 32), AutoSize = false };

    public string Username { get; private set; } = "";

    public AddUserDialog(OperatorStore store)
    {
        _store = store;

        Text = "Add operator";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(370, 230);
        BackColor = Th.Bg;
        ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);
        Icon = Th.MakeHexIcon(Th.Grn);

        int y = 18;
        AddRow("Username",            _username, ref y);
        AddRow("Password (12+ chars)", _password, ref y);
        AddRow("Confirm password",    _confirm,  ref y);
        _msg.Location = new Point(18, y + 4);
        Controls.Add(_msg);

        var ok     = MakeButton("Add",    new Point(182, 188), Th.Grn);
        var cancel = MakeButton("Cancel", new Point(280, 188), Th.Dim, subtle: true);
        ok.Click     += (_, _) => Submit();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    void AddRow(string label, TextBox input, ref int y)
    {
        Controls.Add(new Label { Text = label, Location = new Point(18, y + 4), AutoSize = true, ForeColor = Th.Dim });
        input.Location = new Point(112, y);
        input.BackColor = Th.Card;
        input.ForeColor = Th.Brt;
        input.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(input);
        y += 38;
    }

    void Submit()
    {
        var username = _username.Text.Trim();
        var password = _password.Text;
        var confirm  = _confirm.Text;
        if (string.IsNullOrWhiteSpace(username)) { Fail("Username is required."); return; }
        if (password.Length < 12)                { Fail("Password must be at least 12 characters."); return; }
        if (!string.Equals(password, confirm))   { Fail("Passwords do not match."); return; }
        if (_store.Contains(username))           { Fail("That username already exists."); return; }
        try
        {
            _store.Create(username, password);
            Username = username;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    void Fail(string text) { _msg.Text = text; }

    static Button MakeButton(string text, Point location, Color accent, bool subtle = false)
    {
        var b = new Button
        {
            Text      = text,
            Location  = location,
            Size      = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = subtle ? Th.Card : Color.FromArgb(42, accent),
            ForeColor = subtle ? Th.Brt : accent,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(90, accent);
        return b;
    }
}

sealed class ResetPasswordDialog : Form
{
    readonly OperatorStore _store;
    readonly string _username;
    readonly TextBox _password = new() { Width = 240, UseSystemPasswordChar = true };
    readonly TextBox _confirm  = new() { Width = 240, UseSystemPasswordChar = true };
    readonly Label   _msg      = new() { ForeColor = Th.Red, Size = new Size(330, 32), AutoSize = false };

    public ResetPasswordDialog(OperatorStore store, string username)
    {
        _store    = store;
        _username = username;

        Text = $"Reset password — {username}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(370, 188);
        BackColor = Th.Bg;
        ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);
        Icon = Th.MakeHexIcon(Th.Grn);

        int y = 18;
        AddRow("New password (12+)", _password, ref y);
        AddRow("Confirm",            _confirm,  ref y);
        _msg.Location = new Point(18, y + 4);
        Controls.Add(_msg);

        var ok     = MakeButton("Save",   new Point(182, 146), Th.Cyan);
        var cancel = MakeButton("Cancel", new Point(280, 146), Th.Dim, subtle: true);
        ok.Click     += (_, _) => Submit();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    void AddRow(string label, TextBox input, ref int y)
    {
        Controls.Add(new Label { Text = label, Location = new Point(18, y + 4), AutoSize = true, ForeColor = Th.Dim });
        input.Location = new Point(112, y);
        input.BackColor = Th.Card;
        input.ForeColor = Th.Brt;
        input.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(input);
        y += 38;
    }

    void Submit()
    {
        var password = _password.Text;
        var confirm  = _confirm.Text;
        if (password.Length < 12)              { Fail("Password must be at least 12 characters."); return; }
        if (!string.Equals(password, confirm)) { Fail("Passwords do not match."); return; }
        try
        {
            _store.ChangePassword(_username, password);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    void Fail(string text) { _msg.Text = text; }

    static Button MakeButton(string text, Point location, Color accent, bool subtle = false)
    {
        var b = new Button
        {
            Text      = text,
            Location  = location,
            Size      = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = subtle ? Th.Card : Color.FromArgb(42, accent),
            ForeColor = subtle ? Th.Brt : accent,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(90, accent);
        return b;
    }
}
