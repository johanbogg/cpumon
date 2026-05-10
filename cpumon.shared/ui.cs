// "ui.cs"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using System.ServiceProcess;

// ═══════════════════════════════════════════════════
//  Theme & drawing helpers
// ═══════════════════════════════════════════════════
public static class Th
{
    public static bool IsDark = true;
    public static event Action? ThemeChanged;
    public static Color Bg = Color.FromArgb(18, 18, 22);
    public static Color TBg = Color.FromArgb(22, 22, 28);
    public static Color Card = Color.FromArgb(36, 36, 44);
    public static Color Brd = Color.FromArgb(55, 55, 65);
    public static Color Blu = Color.FromArgb(80, 160, 255);
    public static Color Grn = Color.FromArgb(80, 220, 140);
    public static Color Org = Color.FromArgb(255, 180, 60);
    public static Color Red = Color.FromArgb(255, 80, 80);
    public static Color Yel = Color.FromArgb(255, 220, 80);
    public static Color Dim = Color.FromArgb(140, 140, 155);
    public static Color Brt = Color.FromArgb(230, 230, 240);
    public static Color Cyan = Color.FromArgb(80, 220, 240);
    public static Color Mag = Color.FromArgb(200, 120, 255);

    public static void Toggle()
    {
        IsDark = !IsDark;
        if (IsDark) { Bg = Color.FromArgb(18, 18, 22); TBg = Color.FromArgb(22, 22, 28); Card = Color.FromArgb(36, 36, 44); Brd = Color.FromArgb(55, 55, 65); Brt = Color.FromArgb(230, 230, 240); Dim = Color.FromArgb(140, 140, 155); Blu = Color.FromArgb(80, 160, 255); Grn = Color.FromArgb(80, 220, 140); Org = Color.FromArgb(255, 180, 60); Red = Color.FromArgb(255, 80, 80); Yel = Color.FromArgb(255, 220, 80); Cyan = Color.FromArgb(80, 220, 240); Mag = Color.FromArgb(200, 120, 255); }
        else { Bg = Color.FromArgb(232, 234, 240); TBg = Color.FromArgb(219, 223, 232); Card = Color.FromArgb(222, 226, 235); Brd = Color.FromArgb(176, 184, 198); Brt = Color.FromArgb(26, 31, 42); Dim = Color.FromArgb(88, 96, 112); Blu = Color.FromArgb(0, 100, 190); Grn = Color.FromArgb(0, 140, 82); Org = Color.FromArgb(170, 95, 0); Red = Color.FromArgb(185, 40, 40); Yel = Color.FromArgb(140, 112, 0); Cyan = Color.FromArgb(0, 135, 160); Mag = Color.FromArgb(125, 55, 180); }
        ThemeChanged?.Invoke();
    }

    public static Color LdC(float p) => p switch { > 90 => Red, > 70 => Org, > 40 => Blu, _ => Grn };
    public static Color TpC(float c) => c switch { > 90 => Red, > 75 => Org, > 55 => Blu, _ => Grn };

    public static string F(float? v, string f, string s) =>
        v.HasValue ? v.Value.ToString(f, CultureInfo.InvariantCulture) + " " + s : "N/A";

    public static string FF(float? m)
    {
        if (!m.HasValue || m <= 0) return "N/A";
        return m.Value > 1000
            ? (m.Value / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " GHz"
            : m.Value.ToString("0", CultureInfo.InvariantCulture) + " MHz";
    }

    public static GraphicsPath RR(int x, int y, int w, int h, int r)
    {
        var p = new GraphicsPath();
        int d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static (Label close, Label min) MkWB(Form f)
    {
        var close = new Label
        {
            Text = "✕", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Dim,
            Size = new Size(32, 32), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent
        };
        close.MouseEnter += (_, _) => { close.ForeColor = Red; close.BackColor = Color.FromArgb(40, Red); };
        close.MouseLeave += (_, _) => { close.ForeColor = Dim; close.BackColor = Color.Transparent; };

        var min = new Label
        {
            Text = "─", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Dim,
            Size = new Size(32, 32), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent
        };
        min.MouseEnter += (_, _) => { min.ForeColor = Brt; min.BackColor = Color.FromArgb(40, Brt); };
        min.MouseLeave += (_, _) => { min.ForeColor = Dim; min.BackColor = Color.Transparent; };
        min.Click += (_, _) => f.WindowState = FormWindowState.Minimized;

        return (close, min);
    }

    public static void LB(Panel tp, Label c, Label m)
    {
        c.Location = new Point(tp.Width - c.Width - 4, (tp.Height - c.Height) / 2);
        m.Location = new Point(c.Left - m.Width - 2, c.Top);
    }
}

// ═══════════════════════════════════════════════════
//  Remote Desktop Viewer (shown on server/PAW side)
// ═══════════════════════════════════════════════════
public sealed class RdpViewerDialog : Form
{
    readonly Action<ServerCommand> _sendCmd;
    readonly Action? _onClose;
    readonly string _rdpId;
    readonly string _targetName;
    readonly PictureBox _canvas;
    readonly Label _statusLbl;
    readonly TrackBar _fpsSlider, _qualitySlider;
    Bitmap? _framebuffer;
    readonly object _fbLock = new();
    long _lastSeq;
    int _remoteW, _remoteH;
    long _frameCount;
    readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    bool _inputEnabled = true;
    volatile bool _repaintPending;
    readonly System.Windows.Forms.Timer _mouseMoveTimer;
    int _pendingMouseX, _pendingMouseY;
    bool _hasPendingMouseMove;
    int _cursorX = -1, _cursorY = -1;
    bool _closed;
    bool _closeSent;

    public string RdpId => _rdpId;

    public RdpViewerDialog(string targetName, string rdpId, Action<ServerCommand> sendCmd, Action? onClose = null)
    {
        _targetName = targetName;
        _rdpId = rdpId;
        _sendCmd = sendCmd;
        _onClose = onClose;
        _mouseMoveTimer = new System.Windows.Forms.Timer { Interval = Proto.RdpMouseMoveIntervalMs };
        _mouseMoveTimer.Tick += (_, _) => FlushMouseMove();

        Text = $"🖥 Remote Desktop — {targetName}";
        Size = new Size(1040, 640);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        ForeColor = Th.Brt;
        FormBorderStyle = FormBorderStyle.Sizable;
        KeyPreview = true;
        DoubleBuffered = true;

        // Top bar
        var top = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Th.TBg };
        top.Controls.Add(new Label
        {
            Text = $"🖥 {targetName}", ForeColor = Th.Cyan,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = true, Location = new Point(8, 8)
        });

        _statusLbl = new Label
        {
            Text = "Connecting...", ForeColor = Th.Dim,
            Font = new Font("Segoe UI", 7.5f), AutoSize = true,
            Location = new Point(200, 10)
        };
        top.Controls.Add(_statusLbl);

        top.Controls.Add(new Label { Text = "FPS:", ForeColor = Th.Dim, Font = new Font("Segoe UI", 7.5f), AutoSize = true, Location = new Point(400, 10) });
        _fpsSlider = new TrackBar { Minimum = Proto.RdpFpsMin, Maximum = Proto.RdpFpsMax, Value = Proto.RdpFpsDefault, TickFrequency = 5, SmallChange = 1, Size = new Size(100, 20), Location = new Point(430, 4), BackColor = Th.TBg };
        _fpsSlider.ValueChanged += (_, _) => SendIfOpen(new ServerCommand { Cmd = "rdp_set_fps", RdpId = _rdpId, RdpFps = _fpsSlider.Value });
        top.Controls.Add(_fpsSlider);

        top.Controls.Add(new Label { Text = "Q:", ForeColor = Th.Dim, Font = new Font("Segoe UI", 7.5f), AutoSize = true, Location = new Point(540, 10) });
        _qualitySlider = new TrackBar { Minimum = 10, Maximum = 95, Value = Proto.RdpJpegQuality, TickFrequency = 10, SmallChange = 5, Size = new Size(100, 20), Location = new Point(558, 4), BackColor = Th.TBg };
        _qualitySlider.ValueChanged += (_, _) => SendIfOpen(new ServerCommand { Cmd = "rdp_set_quality", RdpId = _rdpId, RdpQuality = _qualitySlider.Value });
        top.Controls.Add(_qualitySlider);

        var inputChk = new CheckBox { Text = "Input", ForeColor = Th.Grn, Checked = true, AutoSize = true, Location = new Point(670, 8), FlatStyle = FlatStyle.Flat, BackColor = Th.TBg };
        inputChk.CheckedChanged += (_, _) => _inputEnabled = inputChk.Checked;
        top.Controls.Add(inputChk);

        var refreshBtn = new Button
        {
            Text = "⟳ Full", ForeColor = Th.Blu, BackColor = Th.Card,
            FlatStyle = FlatStyle.Flat, Size = new Size(60, 24), Location = new Point(740, 5), Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold)
        };
        refreshBtn.FlatAppearance.BorderColor = Color.FromArgb(70, Th.Blu);
        refreshBtn.Click += (_, _) => SendIfOpen(new ServerCommand { Cmd = "rdp_refresh", RdpId = _rdpId });
        top.Controls.Add(refreshBtn);

        top.Controls.Add(new Label { Text = "Mon:", ForeColor = Th.Dim, Font = new Font("Segoe UI", 7.5f), AutoSize = true, Location = new Point(812, 10) });
        var monPicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Th.Card, ForeColor = Th.Brt, FlatStyle = FlatStyle.Flat, Location = new Point(840, 5), Size = new Size(56, 22), Font = new Font("Segoe UI", 8f) };
        monPicker.Items.AddRange(new object[] { "1", "2", "3", "4" });
        monPicker.SelectedIndex = 0;
        monPicker.SelectedIndexChanged += (_, _) => { SendIfOpen(new ServerCommand { Cmd = "rdp_set_monitor", RdpId = _rdpId, RdpMonitorIndex = monPicker.SelectedIndex }); SendIfOpen(new ServerCommand { Cmd = "rdp_refresh", RdpId = _rdpId }); };
        top.Controls.Add(monPicker);

        var bwLabel = new Label { Text = "BW:∞", ForeColor = Th.Dim, Font = new Font("Segoe UI", 7.5f), AutoSize = true, Location = new Point(904, 10) };
        top.Controls.Add(bwLabel);
        var bwSlider = new TrackBar { Minimum = 0, Maximum = 20, Value = 0, TickFrequency = 5, SmallChange = 1, Size = new Size(90, 20), Location = new Point(932, 5), BackColor = Th.TBg };
        bwSlider.ValueChanged += (_, _) =>
        {
            int kbps = bwSlider.Value * 100;
            bwLabel.Text = kbps == 0 ? "BW:∞" : $"BW:{kbps}K";
            SendIfOpen(new ServerCommand { Cmd = "rdp_set_bandwidth", RdpId = _rdpId, RdpBandwidthKBps = kbps });
        };
        top.Controls.Add(bwSlider);

        // Canvas
        _canvas = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
            Cursor = Cursors.Cross
        };
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseDown += OnMouseDown;
        _canvas.MouseUp += OnMouseUp;
        _canvas.MouseWheel += OnMouseWheel;

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        Controls.Add(_canvas);
        Controls.Add(top);

        FormClosing += (_, _) => CloseRemoteSession();
        FormClosed += (_, _) => { CloseRemoteSession(); _mouseMoveTimer.Dispose(); lock (_fbLock) { _framebuffer?.Dispose(); _framebuffer = null; } };
    }

    void SendIfOpen(ServerCommand cmd)
    {
        if (_closed || IsDisposed) return;
        _sendCmd(cmd);
    }

    void CloseRemoteSession()
    {
        if (_closed) return;
        _closed = true;
        _hasPendingMouseMove = false;
        _mouseMoveTimer.Stop();
        _canvas.MouseMove -= OnMouseMove;
        _canvas.MouseDown -= OnMouseDown;
        _canvas.MouseUp -= OnMouseUp;
        _canvas.MouseWheel -= OnMouseWheel;
        KeyDown -= OnKeyDown;
        KeyUp -= OnKeyUp;
        if (!_closeSent)
        {
            _closeSent = true;
            try { _sendCmd(new ServerCommand { Cmd = "rdp_close", RdpId = _rdpId }); } catch { }
        }
        _onClose?.Invoke();
    }

    // Convert viewer coordinates to remote screen coordinates
    (int x, int y) ToRemote(int cx, int cy)
    {
        if (_remoteW == 0 || _remoteH == 0 || _canvas.Image == null) return (cx, cy);

        // Calculate the actual drawn area within the PictureBox (Zoom mode)
        float scaleX = (float)_canvas.Width / _remoteW;
        float scaleY = (float)_canvas.Height / _remoteH;
        float scale = Math.Min(scaleX, scaleY);

        float drawnW = _remoteW * scale;
        float drawnH = _remoteH * scale;
        float offsetX = (_canvas.Width - drawnW) / 2f;
        float offsetY = (_canvas.Height - drawnH) / 2f;

        int rx = (int)((cx - offsetX) / scale);
        int ry = (int)((cy - offsetY) / scale);

        return (Math.Clamp(rx, 0, _remoteW - 1), Math.Clamp(ry, 0, _remoteH - 1));
    }

    void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (_closed || !_inputEnabled || _remoteW == 0) return;
        var (rx, ry) = ToRemote(e.X, e.Y);
        _pendingMouseX = rx;
        _pendingMouseY = ry;
        _hasPendingMouseMove = true;
        if (!_mouseMoveTimer.Enabled) _mouseMoveTimer.Start();
    }

    void FlushMouseMove()
    {
        if (_closed || !_hasPendingMouseMove || !_inputEnabled || _remoteW == 0)
        {
            _mouseMoveTimer.Stop();
            return;
        }
        _hasPendingMouseMove = false;
        SendIfOpen(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_move", X = _pendingMouseX, Y = _pendingMouseY, SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
    }

    void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (_closed || !_inputEnabled || _remoteW == 0) return;
        _canvas.Focus();
        FlushMouseMove();
        var (rx, ry) = ToRemote(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        SendIfOpen(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_down", X = rx, Y = ry, Button = btn } });
    }

    void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (_closed || !_inputEnabled || _remoteW == 0) return;
        FlushMouseMove();
        var (rx, ry) = ToRemote(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        SendIfOpen(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_up", X = rx, Y = ry, Button = btn } });
    }

    void OnMouseWheel(object? s, MouseEventArgs e)
    {
        if (_closed || !_inputEnabled || _remoteW == 0) return;
        var (rx, ry) = ToRemote(e.X, e.Y);
        SendIfOpen(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_wheel", X = rx, Y = ry, Delta = e.Delta } });
    }

    void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (_closed || !_inputEnabled || _remoteW == 0) return;
        e.Handled = true; e.SuppressKeyPress = true;
        bool ext = IsExtended(e.KeyCode);
        SendIfOpen(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "key_down", VirtualKey = (int)e.KeyCode, ScanCode = 0, Extended = ext } });
    }

    void OnKeyUp(object? s, KeyEventArgs e)
    {
        if (_closed || !_inputEnabled || _remoteW == 0) return;
        e.Handled = true; e.SuppressKeyPress = true;
        bool ext = IsExtended(e.KeyCode);
        SendIfOpen(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "key_up", VirtualKey = (int)e.KeyCode, ScanCode = 0, Extended = ext } });
    }

    static bool IsExtended(Keys k) => k is Keys.RMenu or Keys.RControlKey or Keys.Insert or Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.NumLock or Keys.PrintScreen or Keys.Pause;

    public void ReceiveFrame(RdpFrameData frame)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (frame.Seq <= _lastSeq && !frame.IsFull) return;
        _lastSeq = frame.Seq;

        lock (_fbLock)
        {
            if (_framebuffer == null || _framebuffer.Width != frame.ScreenW || _framebuffer.Height != frame.ScreenH)
            {
                _framebuffer?.Dispose();
                _framebuffer = new Bitmap(frame.ScreenW, frame.ScreenH, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(_framebuffer);
                g.Clear(Color.Black);
            }

            _remoteW = frame.ScreenW;
            _remoteH = frame.ScreenH;
            _cursorX = frame.CursorX;
            _cursorY = frame.CursorY;

            using var g2 = Graphics.FromImage(_framebuffer);
            g2.CompositingMode = CompositingMode.SourceCopy;
            g2.InterpolationMode = InterpolationMode.NearestNeighbor;

            foreach (var tile in frame.Tiles)
            {
                try
                {
                    var data = Convert.FromBase64String(tile.Data);
                    using var ms = new MemoryStream(data);
                    using var img = Image.FromStream(ms);
                    g2.DrawImage(img, tile.X, tile.Y, tile.W, tile.H);
                }
                catch (Exception ex)
                {
                    LogSink.Debug("RdpViewer", $"Failed to decode RDP tile for session {frame.Id} seq={frame.Seq}", ex);
                }
            }
        }

        _frameCount++;

        if (!_repaintPending)
        {
            _repaintPending = true;
            BeginInvoke(() =>
            {
                Bitmap? display;
                lock (_fbLock)
                {
                    if (_framebuffer == null) { _repaintPending = false; return; }
                    display = (Bitmap)_framebuffer.Clone();
                }
                using (var cg = Graphics.FromImage(display))
                    DrawRemoteCursor(cg, _cursorX, _cursorY);

                var old = _canvas.Image;
                _canvas.Image = display;
                old?.Dispose();

                if (_fpsSw.ElapsedMilliseconds > 1000)
                {
                    double fps = _frameCount * 1000.0 / _fpsSw.ElapsedMilliseconds;
                    _statusLbl.Text = $"{_remoteW}×{_remoteH} | {fps:0.0} fps | {frame.Tiles.Count} tiles";
                    _statusLbl.ForeColor = Th.Grn;
                    _frameCount = 0;
                    _fpsSw.Restart();
                }

                _repaintPending = false;
            });
        }
    }

    static void DrawRemoteCursor(Graphics g, int x, int y)
    {
        if (x < 0 || y < 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var pts = new[]
        {
            new Point(x, y),
            new Point(x + 14, y + 6),
            new Point(x + 7, y + 9),
            new Point(x + 4, y + 18)
        };
        using var shadow = new Pen(Color.Black, 4f);
        using var pen = new Pen(Color.White, 2f);
        using var fill = new SolidBrush(Color.FromArgb(220, Th.Cyan));
        g.DrawPolygon(shadow, pts);
        g.FillPolygon(fill, pts);
        g.DrawPolygon(pen, pts);
        using var ring = new Pen(Color.FromArgb(220, Th.Red), 2f);
        g.DrawEllipse(ring, x - 6, y - 6, 12, 12);
    }
}

// ═══════════════════════════════════════════════════
//  Terminal dialog (server side) — unchanged
// ═══════════════════════════════════════════════════
public sealed class ScreenshotPreviewDialog : Form
{
    readonly Panel _surface;
    readonly PictureBox _pic;
    readonly Bitmap? _image;
    float _zoom = 1f;

    public ScreenshotPreviewDialog(string machine, ScreenshotData shot)
    {
        Text = $"Screenshot - {machine}";
        Size = new Size(980, 700); MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg; ForeColor = Th.Brt;

        var top = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Th.TBg };
        var fit = Btn("Fit", Th.Blu, 8);
        var actual = Btn("100%", Th.Grn, 68);
        var plus = Btn("+", Th.Cyan, 128);
        var minus = Btn("-", Th.Cyan, 174);
        var info = new Label { Text = shot.Error ?? $"{shot.Width} x {shot.Height}", ForeColor = shot.Error == null ? Th.Dim : Th.Red, AutoSize = true, Location = new Point(226, 11), Font = new Font("Segoe UI", 8.5f) };
        top.Controls.AddRange(new Control[] { fit, actual, plus, minus, info });

        _surface = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(10, 10, 12) };
        _pic = new PictureBox { Location = new Point(0, 0), SizeMode = PictureBoxSizeMode.StretchImage };
        _surface.Controls.Add(_pic);
        Controls.Add(_surface); Controls.Add(top);

        if (shot.Error == null && !string.IsNullOrEmpty(shot.Data))
        {
            try
            {
                var bytes = Convert.FromBase64String(shot.Data);
                using var ms = new MemoryStream(bytes);
                _image = new Bitmap(ms);
                _pic.Image = _image;
                Shown += (_, _) => Fit();
            }
            catch (Exception ex) { info.Text = $"Decode failed: {ex.Message}"; info.ForeColor = Th.Red; }
        }

        fit.Click += (_, _) => Fit();
        actual.Click += (_, _) => SetZoom(1f);
        plus.Click += (_, _) => SetZoom(_zoom * 1.25f);
        minus.Click += (_, _) => SetZoom(_zoom / 1.25f);
        _surface.MouseWheel += (_, e) => { if ((ModifierKeys & Keys.Control) == Keys.Control) SetZoom(e.Delta > 0 ? _zoom * 1.15f : _zoom / 1.15f); };
        FormClosed += (_, _) => _image?.Dispose();
    }

    Button Btn(string text, Color fg, int x)
    {
        var b = new Button { Text = text, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(52, 26), Location = new Point(x, 6), Font = new Font("Segoe UI", 8f), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = fg;
        return b;
    }

    void Fit()
    {
        if (_image == null || _surface.ClientSize.Width <= 0 || _surface.ClientSize.Height <= 0) return;
        float zx = (float)(_surface.ClientSize.Width - 8) / _image.Width;
        float zy = (float)(_surface.ClientSize.Height - 8) / _image.Height;
        SetZoom(Math.Min(1f, Math.Max(0.05f, Math.Min(zx, zy))));
    }

    void SetZoom(float zoom)
    {
        if (_image == null) return;
        _zoom = Math.Clamp(zoom, 0.05f, 8f);
        _pic.Size = new Size(Math.Max(1, (int)(_image.Width * _zoom)), Math.Max(1, (int)(_image.Height * _zoom)));
    }
}

public sealed class TerminalDialog : Form
{
    readonly RemoteClient _client; readonly string _shell; readonly string _termId; readonly RichTextBox _output; readonly TextBox _input;
    readonly List<string> _history = new(); int _histIdx = -1; readonly StringBuilder _buf = new(); readonly object _bufLock = new(); readonly System.Windows.Forms.Timer _flush; bool _dead;
    public TerminalDialog(RemoteClient client, string shell) { _client = client; _shell = shell; _termId = Guid.NewGuid().ToString("N")[..12]; Text = $"{shell.ToUpper()} — {client.MachineName}"; Size = new Size(840, 560); MinimumSize = new Size(480, 300); StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(12, 12, 16); ForeColor = Color.FromArgb(204, 204, 204); FormBorderStyle = FormBorderStyle.Sizable; KeyPreview = true; var top = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(22, 22, 28) }; top.Controls.Add(new Label { Text = $"🖥 {shell.ToUpper()} — {client.MachineName}", ForeColor = Th.Cyan, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 6) }); var saveBtn = new Button { Text = "💾 Save log", ForeColor = Th.Dim, BackColor = Color.FromArgb(22, 22, 28), FlatStyle = FlatStyle.Flat, Size = new Size(80, 22), Location = new Point(top.Width - 86, 4), Anchor = AnchorStyles.Top | AnchorStyles.Right, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 7.5f) }; saveBtn.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 70); saveBtn.Click += (_, _) => { using var sfd = new SaveFileDialog { Title = "Save terminal log", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", FileName = $"terminal_{DateTime.Now:yyyyMMdd_HHmmss}.txt" }; if (sfd.ShowDialog() == DialogResult.OK) try { File.WriteAllText(sfd.FileName, _output.Text); } catch (Exception ex) { MessageBox.Show($"Save failed: {ex.Message}", "Error"); } }; top.Resize += (_, _) => saveBtn.Location = new Point(top.Width - 86, 4); top.Controls.Add(saveBtn); _output = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 12, 16), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 10f), ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both }; var inputBar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(28, 28, 34) }; inputBar.Controls.Add(new Label { Text = "❯", ForeColor = Th.Grn, Font = new Font("Consolas", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 7) }); _input = new TextBox { BackColor = Color.FromArgb(28, 28, 34), ForeColor = Color.FromArgb(220, 220, 220), Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None, Location = new Point(26, 8), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; _input.KeyDown += OnKey; inputBar.Resize += (_, _) => _input.Width = inputBar.Width - 34; _input.Width = inputBar.Width - 34; inputBar.Controls.Add(_input); Controls.Add(_output); Controls.Add(inputBar); Controls.Add(top); _client.Send(new ServerCommand { Cmd = "terminal_open", TermId = _termId, Shell = shell }); _client.TerminalDialogs[_termId] = this; _flush = new System.Windows.Forms.Timer { Interval = 50 }; _flush.Tick += (_, _) => FlushOutput(); _flush.Start(); FormClosed += (_, _) => { _flush.Stop(); _flush.Dispose(); _client.TerminalDialogs.TryRemove(_termId, out _); try { _client.Send(new ServerCommand { Cmd = "terminal_close", TermId = _termId }); } catch { } }; Shown += (_, _) => _input.Focus(); }
    void OnKey(object? sender, KeyEventArgs e) { switch (e.KeyCode) { case Keys.Enter: e.SuppressKeyPress = true; string line = _input.Text; _input.Clear(); if (!string.IsNullOrEmpty(line)) { _history.Add(line); _histIdx = _history.Count; } if (_dead && line.Trim().Equals("reconnect", StringComparison.OrdinalIgnoreCase)) { _dead = false; _client.Send(new ServerCommand { Cmd = "terminal_open", TermId = _termId, Shell = _shell }); } else if (!_dead) { _client.Send(new ServerCommand { Cmd = "terminal_input", TermId = _termId, Input = line + "\n" }); } break; case Keys.Up: e.SuppressKeyPress = true; if (_history.Count > 0 && _histIdx > 0) { _histIdx--; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } break; case Keys.Down: e.SuppressKeyPress = true; if (_histIdx < _history.Count - 1) { _histIdx++; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } else { _histIdx = _history.Count; _input.Clear(); } break; case Keys.C when e.Control: e.SuppressKeyPress = true; _client.Send(new ServerCommand { Cmd = "terminal_input", TermId = _termId, Input = "\x03" }); break; case Keys.L when e.Control: e.SuppressKeyPress = true; _output.Clear(); break; } }
    public void ReceiveOutput(string text) { lock (_bufLock) { _buf.Append(text); } }
    public void ReceiveClosed() { _dead = true; ReceiveOutput("\r\n[Session ended — type 'reconnect' to restart]\r\n"); }
    void FlushOutput() { string? text; lock (_bufLock) { if (_buf.Length == 0) return; text = _buf.ToString(); _buf.Clear(); } if (_output.TextLength > 200_000) { _output.Select(0, _output.TextLength - 150_000); _output.SelectedText = ""; } _output.AppendText(text); _output.ScrollToCaret(); }
}

// ═══════════════════════════════════════════════════
//  File Browser Dialog (server side) — unchanged
// ═══════════════════════════════════════════════════
public sealed class FileBrowserDialog : Form
{
    readonly RemoteClient _client; readonly string _browserId; readonly ListView _fileList; readonly TextBox _pathBox; readonly TextBox _filterBox; readonly Label _statusLabel; readonly ProgressBar _progressBar; string _currentPath = ""; readonly ImageList _icons; readonly List<ListViewItem> _allItems = new(); int _sortCol; bool _sortAsc = true;
    public FileBrowserDialog(RemoteClient client) { _client = client; _browserId = Guid.NewGuid().ToString("N")[..12]; Text = $"📁 Files — {client.MachineName}"; Size = new Size(900, 600); MinimumSize = new Size(600, 400); StartPosition = FormStartPosition.CenterParent; BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable; _icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit }; _icons.Images.Add("folder", MkIco(Th.Yel, true)); _icons.Images.Add("file", MkIco(Th.Blu, false)); _icons.Images.Add("drive", MkIco(Th.Grn, true)); var toolbar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Th.TBg }; var backBtn = MkBtn("◀ Up", Th.Blu); backBtn.Location = new Point(4, 4); backBtn.Size = new Size(60, 28); backBtn.Click += (_, _) => NavUp(); var rootBtn = MkBtn("🖥 Drives", Th.Grn); rootBtn.Location = new Point(68, 4); rootBtn.Size = new Size(80, 28); rootBtn.Click += (_, _) => Nav(""); var refreshBtn = MkBtn("↻ Refresh", Th.Blu); refreshBtn.Location = new Point(152, 4); refreshBtn.Size = new Size(70, 28); refreshBtn.Click += (_, _) => Nav(_currentPath); _pathBox = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle, Location = new Point(228, 6), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; _pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Nav(_pathBox.Text.Trim()); } }; var goBtn = MkBtn("Go", Th.Grn); goBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right; goBtn.Size = new Size(40, 28); goBtn.Click += (_, _) => Nav(_pathBox.Text.Trim()); var filterLabel = new Label { Text = "🔍", ForeColor = Th.Dim, Font = new Font("Segoe UI", 9f), AutoSize = true, Location = new Point(4, 42) }; _filterBox = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 9.5f), BorderStyle = BorderStyle.FixedSingle, Location = new Point(26, 38), PlaceholderText = "Filter files...", Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; _filterBox.TextChanged += (_, _) => ApplyFilter(); toolbar.Controls.AddRange(new Control[] { backBtn, rootBtn, refreshBtn, _pathBox, goBtn, filterLabel, _filterBox }); toolbar.Resize += (_, _) => { _pathBox.Width = toolbar.Width - 332; goBtn.Location = new Point(toolbar.Width - 80, 4); _filterBox.Width = toolbar.Width - 32; }; _fileList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 9f), BorderStyle = BorderStyle.None, SmallImageList = _icons, GridLines = true, MultiSelect = true }; _fileList.Columns.Add("Name", 320); _fileList.Columns.Add("Size", 100, HorizontalAlignment.Right); _fileList.Columns.Add("Modified", 160); _fileList.Columns.Add("Type", 80); _fileList.ColumnClick += (_, e) => { if (_sortCol == e.Column) _sortAsc = !_sortAsc; else { _sortCol = e.Column; _sortAsc = true; } SortAndDisplay(); }; _fileList.DoubleClick += (_, _) => OpenSel(); _fileList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSel(); } else if (e.KeyCode == Keys.Back) { e.SuppressKeyPress = true; NavUp(); } else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; DelSel(); } else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; Nav(_currentPath); } }; var cm = new ContextMenuStrip { BackColor = Th.Card, ForeColor = Th.Brt }; cm.Items.Add("Copy path", null, (_, _) => { var sel = _fileList.SelectedItems.Count > 0 ? _fileList.SelectedItems[0].Tag as FileNavInfo : null; if (sel?.Path != null) Clipboard.SetText(sel.Path); }); _fileList.AllowDrop = true; _fileList.DragEnter += (_, e) => { e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None; }; _fileList.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) foreach (var f in files) if (File.Exists(f)) UploadLocalFile(f); }; _fileList.ContextMenuStrip = cm; var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Th.TBg }; var dlBtn = MkBtn("⬇ Download", Th.Grn); dlBtn.Location = new Point(8, 6); dlBtn.Size = new Size(100, 28); dlBtn.Click += (_, _) => DlSel(); var delBtn = MkBtn("🗑 Delete", Th.Red); delBtn.Location = new Point(116, 6); delBtn.Size = new Size(90, 28); delBtn.Click += (_, _) => DelSel(); var ulBtn = MkBtn("⬆ Upload", Th.Yel); ulBtn.Location = new Point(214, 6); ulBtn.Size = new Size(90, 28); ulBtn.Click += (_, _) => UploadFile(); _statusLabel = new Label { Text = "Loading...", ForeColor = Th.Dim, Font = new Font("Segoe UI", 8f), AutoSize = false, Location = new Point(312, 12), Size = new Size(400, 20), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; _progressBar = new ProgressBar { Location = new Point(312, 34), Size = new Size(300, 6), Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Right }; bottom.Controls.AddRange(new Control[] { dlBtn, delBtn, ulBtn, _statusLabel, _progressBar }); Controls.Add(_fileList); Controls.Add(toolbar); Controls.Add(bottom); _client.FileBrowserDialogs[_browserId] = this; FormClosed += (_, _) => { _client.FileBrowserDialogs.TryRemove(_browserId, out _); _icons.Dispose(); }; Nav(""); }
    void Nav(string path) { _currentPath = path; if (IsHandleCreated) BeginInvoke(() => { _pathBox.Text = path; _statusLabel.Text = "Loading..."; _fileList.Items.Clear(); }); _client.Send(new ServerCommand { Cmd = "file_list", Path = path, CmdId = _browserId }); }
    void NavUp() { if (string.IsNullOrEmpty(_currentPath)) return; Nav(Path.GetDirectoryName(_currentPath) ?? ""); }
    void OpenSel() { if (_fileList.SelectedItems.Count == 0) return; var nav = _fileList.SelectedItems[0].Tag as FileNavInfo; if (nav?.IsDirectory == true) Nav(nav.Path); }
    void DlSel() { if (_fileList.SelectedItems.Count == 0) return; var nav = _fileList.SelectedItems[0].Tag as FileNavInfo; if (nav == null || nav.IsDirectory) return; using var sfd = new SaveFileDialog { FileName = Path.GetFileName(nav.Path) }; if (sfd.ShowDialog() != DialogResult.OK) return; string tid = Guid.NewGuid().ToString("N")[..12]; _client.ActiveDownloads[tid] = new FileDownloadState(tid, sfd.FileName); _statusLabel.Text = "Downloading..."; _progressBar.Value = 0; _progressBar.Visible = true; _client.Send(new ServerCommand { Cmd = "file_download", Path = nav.Path, TransferId = tid, CmdId = _browserId }); }
    void DelSel() { var items = _fileList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as FileNavInfo).Where(n => n != null && !n.IsUp && !n.IsDrive).ToList(); if (items.Count == 0) return; if (MessageBox.Show($"Delete {items.Count} item(s)?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; foreach (var nav in items) _client.Send(new ServerCommand { Cmd = "file_delete", Path = nav!.Path, Recursive = true, CmdId = _browserId }); Task.Delay(500).ContinueWith(_ => { if (IsHandleCreated) BeginInvoke(() => Nav(_currentPath)); }); }
    void UploadFile() { if (string.IsNullOrEmpty(_currentPath)) { _statusLabel.Text = "Navigate to a folder first"; _statusLabel.ForeColor = Th.Org; return; } using var ofd = new OpenFileDialog { Title = "Upload file to remote" }; if (ofd.ShowDialog() != DialogResult.OK) return; UploadLocalFile(ofd.FileName); }
    void UploadLocalFile(string src) { if (string.IsNullOrEmpty(_currentPath)) { if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = "Navigate to a folder first"; _statusLabel.ForeColor = Th.Org; }); return; } string tid = Guid.NewGuid().ToString("N")[..12]; string dest = _currentPath; if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = "Uploading..."; _statusLabel.ForeColor = Th.Blu; _progressBar.Value = 0; _progressBar.Visible = true; }); Task.Run(() => { try { var fi = new FileInfo(src); long total = fi.Length; long offset = 0; var buf = new byte[Proto.FileChunkSize]; using var fs = fi.OpenRead(); while (true) { int n = fs.Read(buf, 0, buf.Length); bool last = n == 0 || offset + n >= total; _client.Send(new ServerCommand { Cmd = "file_upload_chunk", DestPath = dest, FileChunk = new FileChunkData { TransferId = tid, FileName = fi.Name, Data = n > 0 ? Convert.ToBase64String(buf, 0, n) : "", Offset = offset, TotalSize = total, IsLast = last } }); if (IsHandleCreated) { int pct = total > 0 ? (int)((offset + n) * 100 / total) : 0; BeginInvoke(() => { _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Uploading {fi.Name}: {pct}%"; _statusLabel.ForeColor = Th.Blu; }); } offset += n; if (last) break; Thread.Sleep(5); } if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Uploaded: {fi.Name}"; _statusLabel.ForeColor = Th.Grn; }); } catch (Exception ex) { LogSink.Warn("FileBrowser.Upload", $"Upload failed for transfer {tid}", ex); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Upload error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); } }); }
    public void ReceiveListing(FileListing listing) { if (!IsHandleCreated) return; BeginInvoke(() => { _currentPath = listing.Path; _pathBox.Text = listing.Path; _fileList.Items.Clear(); if (listing.Error != null) { _statusLabel.Text = $"Error: {listing.Error}"; _statusLabel.ForeColor = Th.Red; _allItems.Clear(); return; } _statusLabel.ForeColor = Th.Dim; if (listing.Drives != null) { foreach (var d in listing.Drives) { var item = new ListViewItem(d.Name, "drive"); item.SubItems.Add(d.Ready ? $"{d.FreeGB:0.0}/{d.TotalGB:0.0} GB" : ""); item.SubItems.Add(d.Label); item.SubItems.Add(d.Format); item.Tag = new FileNavInfo { Path = d.Name, IsDirectory = true, IsDrive = true }; item.ForeColor = d.Ready ? Th.Grn : Th.Dim; _fileList.Items.Add(item); } _statusLabel.Text = $"{listing.Drives.Count} drive(s)"; _allItems.Clear(); foreach (ListViewItem it in _fileList.Items) _allItems.Add(it); return; } if (!string.IsNullOrEmpty(listing.Path)) { var up = new ListViewItem("..", "folder"); up.SubItems.Add(""); up.SubItems.Add(""); up.SubItems.Add("DIR"); up.Tag = new FileNavInfo { Path = Path.GetDirectoryName(listing.Path) ?? "", IsDirectory = true, IsUp = true }; up.ForeColor = Th.Dim; _fileList.Items.Add(up); } foreach (var d in listing.Entries.Where(e => e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(d.Name, "folder"); item.SubItems.Add(""); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(d.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add("DIR"); item.Tag = new FileNavInfo { Path = Path.Combine(listing.Path, d.Name), IsDirectory = true }; item.ForeColor = d.Hidden ? Th.Dim : Th.Yel; _fileList.Items.Add(item); } foreach (var f in listing.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(f.Name, "file"); item.SubItems.Add(FmtSz(f.Size)); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(f.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add(Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant()); item.Tag = new FileNavInfo { Path = Path.Combine(listing.Path, f.Name), IsDirectory = false, Size = f.Size }; item.ForeColor = f.Hidden ? Th.Dim : Th.Brt; _fileList.Items.Add(item); } int dc = listing.Entries.Count(e => e.IsDirectory); int fc = listing.Entries.Count(e => !e.IsDirectory); _statusLabel.Text = $"{dc} folder(s), {fc} file(s)"; _allItems.Clear(); foreach (ListViewItem it in _fileList.Items) _allItems.Add(it); SortAndDisplay(); }); }
    public void ReceiveFileChunk(FileChunkData chunk) { if (!_client.ActiveDownloads.TryGetValue(chunk.TransferId, out var state)) return; if (chunk.Error != null) { state.Dispose(); _client.ActiveDownloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = $"Error: {chunk.Error}"; _statusLabel.ForeColor = Th.Red; _progressBar.Visible = false; }); return; } try { if (state.Stream == null) { state.TmpPath = state.LocalPath + ".tmp"; state.Stream = new FileStream(state.TmpPath, FileMode.Create, FileAccess.Write); state.TotalSize = chunk.TotalSize; } if (!string.IsNullOrEmpty(chunk.Data)) { var d = Convert.FromBase64String(chunk.Data); state.Stream.Write(d, 0, d.Length); state.Received += d.Length; } if (IsHandleCreated) BeginInvoke(() => { int pct = state.TotalSize > 0 ? (int)(state.Received * 100 / state.TotalSize) : 0; _progressBar.Visible = true; _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Downloading: {pct}%"; _statusLabel.ForeColor = Th.Blu; }); if (chunk.IsLast) { state.Stream.Flush(); state.Stream.Dispose(); state.Stream = null; File.Move(state.TmpPath!, state.LocalPath, overwrite: true); state.Complete = true; _client.ActiveDownloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Downloaded: {chunk.FileName}"; _statusLabel.ForeColor = Th.Grn; }); } } catch (Exception ex) { LogSink.Warn("FileBrowser.Download", $"Download chunk handling failed for transfer {chunk.TransferId}", ex); state.Dispose(); _client.ActiveDownloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); } }
    public void ReceiveCmdResult(bool ok, string msg) { if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = msg; _statusLabel.ForeColor = ok ? Th.Grn : Th.Red; }); }
    void ApplyFilter() { SortAndDisplay(); }
    void SortAndDisplay() { var ft = _filterBox.Text.Trim(); var vis = _allItems.Where(it => { var n = it.Tag as FileNavInfo; return string.IsNullOrEmpty(ft) || n?.IsUp == true || n?.IsDrive == true || it.Text.IndexOf(ft, StringComparison.OrdinalIgnoreCase) >= 0; }).ToList(); Comparison<ListViewItem> cmp = _sortCol switch { 1 => (a, b) => { long sa = (a.Tag as FileNavInfo)?.Size ?? 0, sb2 = (b.Tag as FileNavInfo)?.Size ?? 0; return _sortAsc ? sa.CompareTo(sb2) : sb2.CompareTo(sa); }, 2 => (a, b) => _sortAsc ? string.Compare(a.SubItems[2].Text, b.SubItems[2].Text, StringComparison.Ordinal) : string.Compare(b.SubItems[2].Text, a.SubItems[2].Text, StringComparison.Ordinal), 3 => (a, b) => _sortAsc ? string.Compare(a.SubItems[3].Text, b.SubItems[3].Text, StringComparison.OrdinalIgnoreCase) : string.Compare(b.SubItems[3].Text, a.SubItems[3].Text, StringComparison.OrdinalIgnoreCase), _ => (a, b) => _sortAsc ? string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase) : string.Compare(b.Text, a.Text, StringComparison.OrdinalIgnoreCase) }; var up = vis.Where(it => (it.Tag as FileNavInfo)?.IsUp == true).ToList(); var drives = vis.Where(it => (it.Tag as FileNavInfo)?.IsDrive == true).ToList(); var dirs = vis.Where(it => { var n = it.Tag as FileNavInfo; return n?.IsDirectory == true && n.IsUp == false && n.IsDrive == false; }).ToList(); var files = vis.Where(it => (it.Tag as FileNavInfo)?.IsDirectory == false).ToList(); drives.Sort(cmp); dirs.Sort(cmp); files.Sort(cmp); string[] hdr = { "Name", "Size", "Modified", "Type" }; for (int i = 0; i < _fileList.Columns.Count; i++) _fileList.Columns[i].Text = i == _sortCol ? hdr[i] + (_sortAsc ? " ▲" : " ▼") : hdr[i]; _fileList.BeginUpdate(); _fileList.Items.Clear(); foreach (var it in up.Concat(drives).Concat(dirs).Concat(files)) _fileList.Items.Add(it); _fileList.EndUpdate(); }
    static string FmtSz(long b) => b switch { < 1024 => $"{b} B", < 1048576 => $"{b / 1024.0:0.0} KB", < 1073741824 => $"{b / 1048576.0:0.0} MB", _ => $"{b / 1073741824.0:0.00} GB" };
    static Bitmap MkIco(Color c, bool f) { var bmp = new Bitmap(16, 16); using var g = Graphics.FromImage(bmp); g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using var br = new SolidBrush(c); if (f) { g.FillRectangle(br, 1, 3, 6, 2); g.FillRectangle(br, 1, 4, 14, 10); } else { g.FillRectangle(br, 3, 1, 10, 14); } return bmp; }
    static Button MkBtn(string t, Color fg) { var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 28), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8f) }; b.FlatAppearance.BorderColor = Color.FromArgb(70, fg); return b; }
}

// ═══════════════════════════════════════════════════
//  Controls
// ═══════════════════════════════════════════════════
public sealed class DPanel : Panel { public DPanel() { DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); } }

public class BorderlessForm : Form
{
    protected bool _dragging; Point _dms, _dfs; const int Grip = 8;
    protected BorderlessForm() { FormBorderStyle = FormBorderStyle.None; Padding = new Padding(1); SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Th.Brd); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { base.WndProc(ref m); long v = m.LParam.ToInt64(); var pt = PointToClient(new Point((int)(v & 0xFFFF), (int)((v >> 16) & 0xFFFF))); if (pt.X >= Width - Grip && pt.Y >= Height - Grip) m.Result = (IntPtr)17; else if (pt.Y >= Height - Grip) m.Result = (IntPtr)15; else if (pt.X >= Width - Grip) m.Result = (IntPtr)11; else if (pt.X <= Grip && pt.Y >= Height - Grip) m.Result = (IntPtr)16; else if (pt.X <= Grip) m.Result = (IntPtr)10; return; } base.WndProc(ref m); }
    protected void DD(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _dragging = true; _dms = Cursor.Position; _dfs = Location; } }
    protected void DM(object? s, MouseEventArgs e) { if (_dragging) { var c = Cursor.Position; Location = new Point(_dfs.X + c.X - _dms.X, _dfs.Y + c.Y - _dms.Y); } }
    protected void DU(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _dragging = false; }
    protected Panel MkTitle(string title, Color col) { var tp = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Th.TBg }; var tl = new Label { Text = title, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = col, AutoSize = true, Location = new Point(12, 11) }; var (cb, mb) = Th.MkWB(this); cb.Click += (_, _) => Close(); tp.Controls.AddRange(new Control[] { tl, cb, mb }); tp.Resize += (_, _) => Th.LB(tp, cb, mb); foreach (Control c in new Control[] { tp, tl }) { c.MouseDown += DD; c.MouseMove += DM; c.MouseUp += DU; } Th.LB(tp, cb, mb); Action? onTh = null; onTh = () => { if (!tp.IsDisposed) tp.BackColor = Th.TBg; }; Th.ThemeChanged += onTh; tp.Disposed += (_, _) => Th.ThemeChanged -= onTh; return tp; }
}
