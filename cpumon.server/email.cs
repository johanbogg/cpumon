// email.cs  — alert configuration, SMTP delivery, threshold checking

using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

public enum EmailSecurity { None, StartTls, Ssl }

public sealed class AlertConfig
{
    [JsonPropertyName("host")] public string? SmtpHost          { get; set; }
    [JsonPropertyName("port")] public int     SmtpPort          { get; set; } = 587;
    [JsonPropertyName("sec")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
                               public EmailSecurity Security    { get; set; } = EmailSecurity.StartTls;
    [JsonPropertyName("from")] public string? FromAddress       { get; set; }
    [JsonPropertyName("to")]   public string? ToAddress         { get; set; }
    [JsonPropertyName("user")] public string? Username          { get; set; }
    [JsonPropertyName("pass")] public string? EncryptedPassword { get; set; }
    [JsonPropertyName("ram")]  public int?    AlertRamPct       { get; set; }
    [JsonPropertyName("disk")] public int?    AlertDiskPct      { get; set; }
    [JsonPropertyName("temp")] public float?  AlertTempC        { get; set; }
    [JsonPropertyName("cool")] public int     CooldownMinutes   { get; set; } = 30;

    [JsonIgnore]
    public bool EmailConfigured =>
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        !string.IsNullOrWhiteSpace(FromAddress) &&
        !string.IsNullOrWhiteSpace(ToAddress);
}

public static class AlertConfigStore
{
    static readonly string _path = Path.Combine(AppContext.BaseDirectory, "alerts.json");
    static readonly JsonSerializerOptions _jso = new() { WriteIndented = true };

    public static AlertConfig Load()
    {
        try { if (File.Exists(_path)) return JsonSerializer.Deserialize<AlertConfig>(File.ReadAllText(_path), _jso) ?? new(); }
        catch { }
        return new AlertConfig();
    }

    public static void Save(AlertConfig cfg)
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, _jso));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }

    public static string Encrypt(string pw) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(pw), null, DataProtectionScope.LocalMachine));

    public static string Decrypt(string enc)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.LocalMachine)); }
        catch { return ""; }
    }
}

public sealed class AlertService
{
    AlertConfig _cfg;
    readonly ConcurrentDictionary<string, DateTime> _lastAlert = new();
    readonly CLog _log;

    public AlertService(CLog log) { _log = log; _cfg = AlertConfigStore.Load(); }

    public bool ThresholdsConfigured => _cfg.AlertRamPct.HasValue || _cfg.AlertDiskPct.HasValue || _cfg.AlertTempC.HasValue;
    public string IdleMode => ThresholdsConfigured ? "monitor" : "keepalive";
    public AlertConfig Config => _cfg;
    public void Reload() { _cfg = AlertConfigStore.Load(); }

    public void Check(string machine, MachineReport r)
    {
        var cfg = _cfg;
        if (!cfg.EmailConfigured) return;

        if (cfg.AlertRamPct.HasValue && r.RamTotalGB > 0)
        {
            int pct = (int)(r.RamUsedGB / r.RamTotalGB * 100);
            if (pct >= cfg.AlertRamPct.Value)
                TrySend(machine, "ram", $"{machine}: RAM {pct}%",
                    $"RAM usage on {machine} is {pct}% ({r.RamUsedGB:0.1}/{r.RamTotalGB:0.0} GB).");
        }

        if (cfg.AlertDiskPct.HasValue)
            foreach (var drv in r.Drives)
            {
                if (drv.TotalGB <= 0) continue;
                int pct = (int)((drv.TotalGB - drv.FreeGB) / drv.TotalGB * 100);
                if (pct >= cfg.AlertDiskPct.Value)
                    TrySend(machine, $"disk:{drv.Name}", $"{machine}: {drv.Name} disk {pct}%",
                        $"Disk usage on {machine} ({drv.Name}) is {pct}% ({drv.FreeGB:0.1} GB free of {drv.TotalGB:0.0} GB).");
            }

        if (cfg.AlertTempC.HasValue && r.PackageTemperatureC is > 0)
            if (r.PackageTemperatureC.Value >= cfg.AlertTempC.Value)
                TrySend(machine, "temp", $"{machine}: CPU {r.PackageTemperatureC.Value:0}°C",
                    $"CPU temperature on {machine} is {r.PackageTemperatureC.Value:0.0}°C (threshold: {cfg.AlertTempC.Value:0.0}°C).");
    }

    void TrySend(string machine, string kind, string subject, string body)
    {
        var key = $"{machine}:{kind}";
        var now = DateTime.UtcNow;
        var cfg = _cfg;
        if (_lastAlert.TryGetValue(key, out var last) && (now - last).TotalMinutes < cfg.CooldownMinutes)
            return;
        _lastAlert[key] = now;
        _log.Add($"Alert: {subject}", Th.Org);
        Task.Run(async () =>
        {
            try { await SendAsync(cfg, subject, body); _log.Add($"Alert sent: {subject}", Th.Grn); }
            catch (Exception ex) { _log.Add($"Alert send failed: {ex.Message}", Th.Red); }
        });
    }

    public static async Task SendAsync(AlertConfig cfg, string subject, string body)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(cfg.FromAddress!));
        msg.To.Add(MailboxAddress.Parse(cfg.ToAddress!));
        msg.Subject = $"[cpumon] {subject}";
        msg.Body = new TextPart("plain") { Text = body + "\n\n— cpumon" };

        var opts = cfg.Security switch
        {
            EmailSecurity.Ssl      => SecureSocketOptions.SslOnConnect,
            EmailSecurity.StartTls => SecureSocketOptions.StartTls,
            _                      => SecureSocketOptions.None,
        };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(cfg.SmtpHost!, cfg.SmtpPort, opts);
        if (!string.IsNullOrEmpty(cfg.Username) && !string.IsNullOrEmpty(cfg.EncryptedPassword))
            await smtp.AuthenticateAsync(cfg.Username, AlertConfigStore.Decrypt(cfg.EncryptedPassword));
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
    }
}

// ── Alert configuration dialog ────────────────────────────────────────────────

public sealed class AlertConfigDialog : Form
{
    readonly AlertService _svc;
    readonly TextBox _txHost, _txFrom, _txTo, _txUser, _txPass;
    readonly NumericUpDown _numPort, _numRam, _numDisk, _numTemp, _numCool;
    readonly ComboBox _cmbSec;
    readonly CheckBox _chkRam, _chkDisk, _chkTemp;
    readonly Label _lblStatus;
    readonly Button _btnTest;
    bool _pwChanged;

    public AlertConfigDialog(AlertService svc)
    {
        _svc = svc;
        var cfg = svc.Config;

        Text = "Alert Configuration";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg; ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);

        int y = 12;
        const int lx = 16, lw = 130, fx = lx + lw, fw = 330;

        void Section(string t)
        {
            var lbl = new Label { Text = t, Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold), ForeColor = Th.Dim, Location = new Point(lx, y), AutoSize = true };
            Controls.Add(lbl); y += 22;
        }

        Label Lbl(string t) => new Label { Text = t, Location = new Point(lx, y + 3), AutoSize = true, ForeColor = Th.Dim };

        TextBox Txt(string v, int w = 0) => new TextBox { Text = v, Location = new Point(fx, y), Size = new Size(w > 0 ? w : fw, 22), BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle };

        void Row(Control lbl, Control ctl) { Controls.Add(lbl); Controls.Add(ctl); y += 28; }

        Section("EMAIL DELIVERY");

        _txHost = Txt(cfg.SmtpHost ?? "");
        Row(Lbl("SMTP Host"), _txHost);

        _numPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = cfg.SmtpPort, Location = new Point(fx, y), Size = new Size(80, 22), BackColor = Th.Card, ForeColor = Th.Brt };
        _cmbSec = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(fx + 88, y), Size = new Size(108, 22), BackColor = Th.Card, ForeColor = Th.Brt, FlatStyle = FlatStyle.Flat };
        _cmbSec.Items.AddRange(new object[] { "None", "StartTLS", "SSL/TLS" });
        _cmbSec.SelectedIndex = cfg.Security == EmailSecurity.Ssl ? 2 : cfg.Security == EmailSecurity.StartTls ? 1 : 0;
        Controls.Add(Lbl("Port / Security")); Controls.Add(_numPort); Controls.Add(_cmbSec); y += 28;

        _txFrom = Txt(cfg.FromAddress ?? "");
        Row(Lbl("From"), _txFrom);

        _txTo = Txt(cfg.ToAddress ?? "");
        Row(Lbl("To"), _txTo);

        _txUser = Txt(cfg.Username ?? "");
        Row(Lbl("Username"), _txUser);

        _txPass = Txt(!string.IsNullOrEmpty(cfg.EncryptedPassword) ? new string('x', 12) : "");
        _txPass.UseSystemPasswordChar = true;
        _txPass.TextChanged += (_, _) => _pwChanged = true;
        _pwChanged = false;
        Row(Lbl("Password"), _txPass);

        y += 6;
        Section("ALERT THRESHOLDS");

        _chkRam = new CheckBox { Text = "RAM usage ≥", Checked = cfg.AlertRamPct.HasValue, Location = new Point(lx, y + 2), AutoSize = true, ForeColor = Th.Brt };
        _numRam = new NumericUpDown { Minimum = 1, Maximum = 100, Value = cfg.AlertRamPct ?? 90, Location = new Point(fx, y), Size = new Size(60, 22), BackColor = Th.Card, ForeColor = Th.Brt };
        Controls.Add(_chkRam); Controls.Add(_numRam);
        Controls.Add(new Label { Text = "%", Location = new Point(fx + 64, y + 3), AutoSize = true, ForeColor = Th.Dim }); y += 28;

        _chkDisk = new CheckBox { Text = "Disk usage ≥", Checked = cfg.AlertDiskPct.HasValue, Location = new Point(lx, y + 2), AutoSize = true, ForeColor = Th.Brt };
        _numDisk = new NumericUpDown { Minimum = 1, Maximum = 100, Value = cfg.AlertDiskPct ?? 90, Location = new Point(fx, y), Size = new Size(60, 22), BackColor = Th.Card, ForeColor = Th.Brt };
        Controls.Add(_chkDisk); Controls.Add(_numDisk);
        Controls.Add(new Label { Text = "%", Location = new Point(fx + 64, y + 3), AutoSize = true, ForeColor = Th.Dim }); y += 28;

        _chkTemp = new CheckBox { Text = "CPU temp ≥", Checked = cfg.AlertTempC.HasValue, Location = new Point(lx, y + 2), AutoSize = true, ForeColor = Th.Brt };
        _numTemp = new NumericUpDown { Minimum = 30, Maximum = 120, Value = (decimal)(cfg.AlertTempC ?? 80), Location = new Point(fx, y), Size = new Size(60, 22), BackColor = Th.Card, ForeColor = Th.Brt };
        Controls.Add(_chkTemp); Controls.Add(_numTemp);
        Controls.Add(new Label { Text = "°C", Location = new Point(fx + 64, y + 3), AutoSize = true, ForeColor = Th.Dim }); y += 28;

        _numCool = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = cfg.CooldownMinutes, Location = new Point(fx, y), Size = new Size(60, 22), BackColor = Th.Card, ForeColor = Th.Brt };
        Controls.Add(Lbl("Cooldown")); Controls.Add(_numCool);
        Controls.Add(new Label { Text = "min between alerts", Location = new Point(fx + 64, y + 3), AutoSize = true, ForeColor = Th.Dim }); y += 36;

        _lblStatus = new Label { Text = "", Location = new Point(lx, y), Size = new Size(fw + lw, 20), ForeColor = Th.Dim };
        Controls.Add(_lblStatus); y += 28;

        _btnTest = new Button { Text = "Send Test Email", Location = new Point(lx, y), Size = new Size(130, 30), BackColor = Color.FromArgb(14, 32, 52), ForeColor = Th.Blu, FlatStyle = FlatStyle.Flat };
        _btnTest.FlatAppearance.BorderColor = Th.Blu;
        _btnTest.Click += OnTest;

        var btnSave = new Button { Text = "Save", Location = new Point(fx + fw - 164, y), Size = new Size(78, 30), DialogResult = DialogResult.OK, BackColor = Color.FromArgb(14, 42, 14), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat };
        btnSave.FlatAppearance.BorderColor = Th.Grn;
        btnSave.Click += OnSave;

        var btnCancel = new Button { Text = "Cancel", Location = new Point(fx + fw - 80, y), Size = new Size(78, 30), DialogResult = DialogResult.Cancel, BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };

        Controls.Add(_btnTest); Controls.Add(btnSave); Controls.Add(btnCancel);
        AcceptButton = btnSave; CancelButton = btnCancel;

        ClientSize = new Size(lx + lw + fw + lx, y + 50);
    }

    AlertConfig BuildConfig()
    {
        var cfg = new AlertConfig
        {
            SmtpHost        = _txHost.Text.Trim(),
            SmtpPort        = (int)_numPort.Value,
            Security        = _cmbSec.SelectedIndex == 2 ? EmailSecurity.Ssl
                            : _cmbSec.SelectedIndex == 1 ? EmailSecurity.StartTls
                            : EmailSecurity.None,
            FromAddress     = _txFrom.Text.Trim(),
            ToAddress       = _txTo.Text.Trim(),
            Username        = _txUser.Text.Trim(),
            AlertRamPct     = _chkRam.Checked ? (int?)_numRam.Value : null,
            AlertDiskPct    = _chkDisk.Checked ? (int?)_numDisk.Value : null,
            AlertTempC      = _chkTemp.Checked ? (float?)_numTemp.Value : null,
            CooldownMinutes = (int)_numCool.Value,
        };
        if (!_pwChanged)
            cfg.EncryptedPassword = _svc.Config.EncryptedPassword;
        else if (!string.IsNullOrEmpty(_txPass.Text))
            cfg.EncryptedPassword = AlertConfigStore.Encrypt(_txPass.Text);
        return cfg;
    }

    void OnSave(object? sender, EventArgs e) { AlertConfigStore.Save(BuildConfig()); _svc.Reload(); }

    async void OnTest(object? sender, EventArgs e)
    {
        _btnTest.Enabled = false;
        _lblStatus.ForeColor = Th.Dim;
        _lblStatus.Text = "Sending…";
        var cfg = BuildConfig();
        if (!cfg.EmailConfigured)
        {
            _lblStatus.Text = "SMTP host, From, and To are required.";
            _lblStatus.ForeColor = Th.Org;
            _btnTest.Enabled = true;
            return;
        }
        try
        {
            await AlertService.SendAsync(cfg, "Test alert",
                "This is a test alert from cpumon. Email delivery is working correctly.");
            _lblStatus.ForeColor = Th.Grn;
            _lblStatus.Text = "Test email sent successfully.";
        }
        catch (Exception ex)
        {
            _lblStatus.ForeColor = Th.Red;
            _lblStatus.Text = ex.Message;
        }
        _btnTest.Enabled = true;
    }
}
