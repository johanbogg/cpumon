using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

public sealed class ServerStartupSettings
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool HideToTray { get; set; }
    public bool WebEnabled { get; set; }
    public int WebPort { get; set; } = 47202;
    public bool WebUseTls { get; set; } = true;
    public bool WebBehindProxy { get; set; }
    public bool NoBroadcast { get; set; }
    public bool NewUi { get; set; }

    public static string Path => AppPaths.DataFile("server_settings.json");

    public static ServerStartupSettings Load(string? path = null)
    {
        var p = path ?? Path;
        try
        {
            if (File.Exists(p))
                return Normalize(JsonSerializer.Deserialize<ServerStartupSettings>(File.ReadAllText(p), JsonOpts) ?? new());
        }
        catch { }
        return new ServerStartupSettings();
    }

    public void Save(string? path = null)
    {
        var p = path ?? Path;
        AppPaths.EnsureDataDir();
        File.WriteAllText(p, JsonSerializer.Serialize(Normalize(this), JsonOpts));
    }

    static ServerStartupSettings Normalize(ServerStartupSettings s)
    {
        if (s.WebPort < 1 || s.WebPort > 65535) s.WebPort = 47202;
        return s;
    }
}

public sealed class ServerStartupSettingsDialog : Form
{
    readonly CheckBox _hideToTray = new() { Text = "Hide to systray on startup", AutoSize = true };
    readonly CheckBox _web = new() { Text = "Start Web UI", AutoSize = true };
    readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    readonly CheckBox _tls = new() { Text = "Use TLS", AutoSize = true };
    readonly CheckBox _behindProxy = new() { Text = "Behind reverse proxy", AutoSize = true };
    readonly CheckBox _noBroadcast = new() { Text = "Disable UDP discovery broadcast", AutoSize = true };
    readonly CheckBox _newUi = new() { Text = "Use new WinForms UI", AutoSize = true };

    public ServerStartupSettingsDialog()
    {
        var cfg = ServerStartupSettings.Load();

        Text = "Server Startup Options";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(390, 300);
        BackColor = Th.Bg;
        ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);
        Icon = Th.MakeHexIcon(Th.Grn);

        _hideToTray.Checked = cfg.HideToTray;
        _web.Checked = cfg.WebEnabled;
        _port.Value = Math.Max(_port.Minimum, Math.Min(_port.Maximum, cfg.WebPort));
        _tls.Checked = cfg.WebUseTls;
        _behindProxy.Checked = cfg.WebBehindProxy;
        _noBroadcast.Checked = cfg.NoBroadcast;
        _newUi.Checked = cfg.NewUi;

        var y = 18;
        Add(_hideToTray, 18, ref y);
        Add(_web, 18, ref y);

        Controls.Add(new Label { Text = "Web port", Location = new Point(38, y + 4), AutoSize = true, ForeColor = Th.Dim });
        _port.Location = new Point(150, y);
        Controls.Add(_port);
        y += 34;

        Add(_tls, 38, ref y);
        Add(_behindProxy, 38, ref y);
        Add(_noBroadcast, 18, ref y);
        Add(_newUi, 18, ref y);

        var note = new Label
        {
            Text = "Saved options apply next time the server starts. Command-line flags override these defaults for that launch.",
            Location = new Point(18, y + 2),
            Size = new Size(350, 42),
            ForeColor = Th.Dim
        };
        Controls.Add(note);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(202, 255), Size = new Size(78, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(292, 255), Size = new Size(78, 28) };
        StyleButton(ok, Th.Grn);
        StyleButton(cancel, Th.Dim, subtle: true);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    void Add(CheckBox box, int x, ref int y)
    {
        box.Location = new Point(x, y);
        box.ForeColor = Th.Brt;
        Controls.Add(box);
        y += 32;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            new ServerStartupSettings
            {
                HideToTray = _hideToTray.Checked,
                WebEnabled = _web.Checked,
                WebPort = (int)_port.Value,
                WebUseTls = _tls.Checked,
                WebBehindProxy = _behindProxy.Checked,
                NoBroadcast = _noBroadcast.Checked,
                NewUi = _newUi.Checked
            }.Save();
        }
        base.OnFormClosing(e);
    }

    static void StyleButton(Button b, Color c, bool subtle = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = subtle ? Th.Card : Color.FromArgb(42, c);
        b.ForeColor = c;
        b.FlatAppearance.BorderColor = Color.FromArgb(90, c);
    }
}
