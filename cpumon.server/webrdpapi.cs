using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
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
    static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(15);
    static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(2);

    readonly ServerEngine _engine;
    readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    readonly Timer? _sweeper;
    volatile bool _disposed;

    public TimeSpan IdleSessionTtl { get; init; } = DefaultIdleTtl;

    public WebRdpSessionStore(ServerEngine engine, bool startSweeper = true)
    {
        _engine = engine;
        _engine.RdpFrameUpdated += OnFrame;
        if (startSweeper)
            _sweeper = new Timer(_ => Sweep(), null, DefaultSweepInterval, DefaultSweepInterval);
    }

    public Session Create(string machine, string rdpId)
    {
        var key = Key(machine, rdpId);
        if (_sessions.TryRemove(key, out var old))
        {
            old.Dispose();
            try { _engine.RequestRdpClose(machine, rdpId); } catch { }
        }
        var session = new Session(machine, rdpId);
        _sessions[key] = session;
        return session;
    }

    public bool Remove(string machine, string rdpId)
    {
        if (!_sessions.TryRemove(Key(machine, rdpId), out var session)) return false;
        session.Dispose();
        try { _engine.RequestRdpClose(machine, rdpId); } catch { }
        return true;
    }

    public int Sweep()
    {
        var cutoff = DateTime.UtcNow - IdleSessionTtl;
        int removed = 0;
        foreach (var kv in _sessions)
        {
            if (kv.Value.LastActivityUtc < cutoff && _sessions.TryRemove(kv.Key, out var stale))
            {
                stale.Dispose();
                try { _engine.RequestRdpClose(stale.Machine, stale.RdpId); } catch { }
                removed++;
            }
        }
        return removed;
    }

    public void OnFrame(RemoteClient cl, string rdpId, RdpFrameData frame)
    {
        if (_sessions.TryGetValue(Key(cl.MachineName, rdpId), out var session))
            session.TryWrite(frame);
    }

    static string Key(string machine, string rdpId) => machine + "\u0001" + rdpId;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweeper?.Dispose();
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
        long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public string Machine { get; }
        public string RdpId { get; }
        public ChannelReader<RdpFrameData> Reader => _frames.Reader;
        public DateTime LastActivityUtc => new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        public Session(string machine, string rdpId) { Machine = machine; RdpId = rdpId; }

        public bool TryWrite(RdpFrameData frame)
        {
            Touch();
            return _frames.Writer.TryWrite(frame);
        }

        public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        public void Dispose() => _frames.Writer.TryComplete();
    }
}

public static class WebRdpApi
{
    const int MaxBandwidthKBps = 100_000;
    const int MaxMonitorIndex  = 15;
    const int MaxRecvBytes     = 64 * 1024;

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
            try { await SendJson(ws, new { type = "closed", reason = "open_failed" }, ct).ConfigureAwait(false); } catch { }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "open failed", ct).ConfigureAwait(false); } catch { }
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await SendJson(ws, new { type = "open", rdpId }, linked.Token).ConfigureAwait(false);
            var send = SendFrames(ws, session, linked.Token);
            var recv = ReceiveCommands(ws, engine, session, machine, rdpId, linked.Token);
            await Task.WhenAny(send, recv).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            linked.Cancel();
            rdps.Remove(machine, rdpId);
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "rdp end", CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }
    }

    static async Task SendFrames(WebSocket ws, WebRdpSessionStore.Session session, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && await session.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                while (session.Reader.TryRead(out var frame))
                    await SendJson(ws, new { type = "frame", frame }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    static async Task ReceiveCommands(WebSocket ws, ServerEngine engine, WebRdpSessionStore.Session session, string machine, string rdpId, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            bool isText = true;
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text) { isText = false; }
                    ms.Write(buf, 0, result.Count);
                    if (ms.Length > MaxRecvBytes) return;
                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) { return; }
            catch (WebSocketException) { return; }
            if (!isText) continue;

            WebRdpMessage? msg;
            try { msg = JsonSerializer.Deserialize<WebRdpMessage>(new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length), JsonOpts); }
            catch (JsonException) { continue; }
            if (msg == null) continue;
            session.Touch();

            switch ((msg.Type ?? "").ToLowerInvariant())
            {
                case "input" when msg.Input != null:
                    msg.Input.SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_input", RdpId = rdpId, RdpInput = msg.Input });
                    break;
                case "set_fps":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_fps", RdpId = rdpId, RdpFps = Math.Clamp(msg.Fps, Proto.RdpFpsMin, Proto.RdpFpsMax) });
                    break;
                case "set_quality":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_quality", RdpId = rdpId, RdpQuality = Math.Clamp(msg.Quality, 10, 95) });
                    break;
                case "set_bandwidth":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_bandwidth", RdpId = rdpId, RdpBandwidthKBps = Math.Clamp(msg.BandwidthKbps, 0, MaxBandwidthKBps) });
                    break;
                case "set_monitor":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_set_monitor", RdpId = rdpId, RdpMonitorIndex = Math.Clamp(msg.Monitor, 0, MaxMonitorIndex) });
                    break;
                case "refresh":
                    engine.RequestRdpCommand(machine, new ServerCommand { Cmd = "rdp_refresh", RdpId = rdpId });
                    break;
                case "close":
                    return;
            }
        }
    }

    // Browsers always send Origin on WS upgrades; missing Origin means a non-browser
    // tool (curl, websocat) is connecting. The RDP endpoint grants full remote control,
    // so missing Origin is rejected here even though /ws/state and /ws/log allow it.
    static bool IsOriginAllowed(HttpContext ctx)
    {
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (string.IsNullOrEmpty(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        var host = ctx.Request.Host;
        if (!string.Equals(o.Host, host.Host, StringComparison.OrdinalIgnoreCase)) return false;
        int oPort = o.IsDefaultPort ? (o.Scheme == "https" ? 443 : 80) : o.Port;
        int hPort = host.Port ?? (ctx.Request.Scheme == "https" ? 443 : 80);
        return oPort == hPort;
    }

    static async Task SendJson(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
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
