using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public sealed class WebRdpSessionStore : IDisposable
{
    readonly ServerEngine _engine;
    readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    volatile bool _disposed;

    public WebRdpSessionStore(ServerEngine engine)
    {
        _engine = engine;
        _engine.RdpFrameUpdated += OnFrame;
    }

    public Session Create(string machine, string rdpId)
    {
        var session = new Session(machine, rdpId);
        if (_sessions.TryRemove(Key(machine, rdpId), out var old)) old.Dispose();
        _sessions[Key(machine, rdpId)] = session;
        return session;
    }

    public bool Remove(string machine, string rdpId)
    {
        if (!_sessions.TryRemove(Key(machine, rdpId), out var session)) return false;
        session.Dispose();
        try { _engine.RequestRdpClose(machine, rdpId); } catch { }
        return true;
    }

    public void OnFrame(RemoteClient cl, string rdpId, RdpFrameData frame)
    {
        if (_sessions.TryGetValue(Key(cl.MachineName, rdpId), out var session))
            session.TryWrite(frame);
    }

    static string Key(string machine, string rdpId) => machine + "\n" + rdpId;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.RdpFrameUpdated -= OnFrame;
        foreach (var s in _sessions.Values)
            try { _engine.RequestRdpClose(s.Machine, s.RdpId); } catch { }
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }

    public sealed class Session : IDisposable
    {
        readonly Channel<RdpFrameData> _frames = Channel.CreateBounded<RdpFrameData>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        public string Machine { get; }
        public string RdpId { get; }
        public ChannelReader<RdpFrameData> Reader => _frames.Reader;

        public Session(string machine, string rdpId) { Machine = machine; RdpId = rdpId; }
        public bool TryWrite(RdpFrameData frame) => _frames.Writer.TryWrite(frame);
        public void Dispose() => _frames.Writer.TryComplete();
    }
}

public static class WebRdpApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           WebRdpSessionStore rdps,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.Map("/ws/rdp/{machine}", async (HttpContext ctx, string machine) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "websocket_required", message = "WebSocket upgrade required." }).ConfigureAwait(false);
                return;
            }
            if (!IsOriginAllowed(ctx))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "origin_blocked", message = "Cross-origin WebSocket upgrade rejected." }).ConfigureAwait(false);
                return;
            }
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail))
            {
                await fail!.ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }
            if (!engine.Clients.TryGetValue(machine, out var cl))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "not_found", message = $"Machine '{machine}' is not connected." }).ConfigureAwait(false);
                return;
            }
            if (ServerEngine.IsLinuxClient(cl))
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new { error = "unsupported", message = "RDP is only supported by Windows clients." }).ConfigureAwait(false);
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await RunSession(ws, engine, rdps, machine, ctx.RequestAborted).ConfigureAwait(false);
        });
    }

    static async Task RunSession(WebSocket ws, ServerEngine engine, WebRdpSessionStore rdps, string machine, CancellationToken ct)
    {
        var rdpId = "web-" + Guid.NewGuid().ToString("N")[..12];
        var session = rdps.Create(machine, rdpId);
        if (!engine.RequestRdpOpen(machine, rdpId, Proto.RdpFpsDefault, Proto.RdpJpegQuality))
        {
            rdps.Remove(machine, rdpId);
            await SendJson(ws, new { type = "closed", reason = "open_failed" }, ct).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await SendJson(ws, new { type = "open", rdpId }, linked.Token).ConfigureAwait(false);
            var send = SendFrames(ws, session, linked.Token);
            var recv = ReceiveCommands(ws, engine, machine, rdpId, linked.Token);
            await Task.WhenAny(send, recv).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            rdps.Remove(machine, rdpId);
        }
    }

    static async Task SendFrames(WebSocket ws, WebRdpSessionStore.Session session, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && await session.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (session.Reader.TryRead(out var frame))
                await SendJson(ws, new { type = "frame", frame }, ct).ConfigureAwait(false);
    }

    static async Task ReceiveCommands(WebSocket ws, ServerEngine engine, string machine, string rdpId, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) continue;
            var msg = JsonSerializer.Deserialize<WebRdpMessage>(Encoding.UTF8.GetString(buf, 0, result.Count), JsonOpts);
            if (msg == null) continue;
            switch ((msg.Type ?? "").ToLowerInvariant())
            {
                case "input" when msg.Input != null:
                    msg.Input.SentAtUnixMs = msg.Input.SentAtUnixMs == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : msg.Input.SentAtUnixMs;
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_input", RdpId = rdpId, RdpInput = msg.Input });
                    break;
                case "set_fps":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_fps", RdpId = rdpId, RdpFps = Math.Clamp(msg.Fps, Proto.RdpFpsMin, Proto.RdpFpsMax) });
                    break;
                case "set_quality":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_quality", RdpId = rdpId, RdpQuality = Math.Clamp(msg.Quality, 10, 95) });
                    break;
                case "set_bandwidth":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_bandwidth", RdpId = rdpId, RdpBandwidthKBps = Math.Max(0, msg.BandwidthKbps) });
                    break;
                case "set_monitor":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_monitor", RdpId = rdpId, RdpMonitorIndex = Math.Max(0, msg.Monitor) });
                    break;
                case "refresh":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_refresh", RdpId = rdpId });
                    break;
                case "close":
                    return;
            }
        }
    }

    static bool IsOriginAllowed(HttpContext ctx)
    {
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        var host = ctx.Request.Host;
        if (!string.Equals(o.Host, host.Host, StringComparison.OrdinalIgnoreCase)) return false;
        int oPort = o.IsDefaultPort ? (o.Scheme == "https" ? 443 : 80) : o.Port;
        int hPort = host.Port ?? (ctx.Request.Scheme == "https" ? 443 : 80);
        return oPort == hPort;
    }

    static async Task SendJson(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }
}

public sealed class WebRdpMessage
{
    public string? Type { get; set; }
    public int Fps { get; set; }
    public int Quality { get; set; }
    public int BandwidthKbps { get; set; }
    public int Monitor { get; set; }
    public RdpInputEvent? Input { get; set; }
}
