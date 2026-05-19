using System;
using System.Drawing;
using System.Linq;
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

public static class WebSocketApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(250);

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           ServerDashboardController controller,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.Map("/ws/state", async (HttpContext ctx) =>
        {
            var session = await TryAccept(ctx, sessions).ConfigureAwait(false);
            if (session == null) return;
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await StreamState(ws, engine, sessions, session, ctx.RequestAborted).ConfigureAwait(false);
        });

        app.Map("/ws/log", async (HttpContext ctx) =>
        {
            var session = await TryAccept(ctx, sessions).ConfigureAwait(false);
            if (session == null) return;
            var sinceUtc = DateTime.UtcNow;
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await StreamLog(ws, engine.Log, sessions, session, sinceUtc, ctx.RequestAborted).ConfigureAwait(false);
        });
    }

    static async Task<SessionState?> TryAccept(HttpContext ctx, SessionStore sessions)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "websocket_required", message = "WebSocket upgrade required." }).ConfigureAwait(false);
            return null;
        }
        if (!IsOriginAllowed(ctx))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "origin_blocked", message = "Cross-origin WebSocket upgrade rejected." }).ConfigureAwait(false);
            return null;
        }
        if (WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out var session, out var fail)) return session;
        await fail!.ExecuteAsync(ctx).ConfigureAwait(false);
        return null;
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

    static async Task StreamState(WebSocket ws, ServerEngine engine, SessionStore sessions, SessionState session, CancellationToken ct)
    {
        var builder = new ServerDashboardStateBuilder(engine);
        await SendJson(ws, new { type = "state", state = WebSessionDashboard.Build(builder, session) }, ct).ConfigureAwait(false);
        using var timer = new PeriodicTimer(StateInterval);
        while (ws.State == WebSocketState.Open && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (sessions.Validate(session.Id) == null) break;
            await SendJson(ws, new { type = "state", state = WebSessionDashboard.Build(builder, session) }, ct).ConfigureAwait(false);
        }
    }

    static async Task StreamLog(WebSocket ws, CLog log, SessionStore sessions, SessionState session, DateTime sinceUtc, CancellationToken ct)
    {
        var queue = Channel.CreateUnbounded<LogEntryDto>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void OnEntry(DateTime ts, string message, Color color)
        {
            queue.Writer.TryWrite(new LogEntryDto
            {
                Ts = ts.ToUniversalTime(),
                Message = message,
                Color = ColorHex(color),
            });
        }

        log.EntryAdded += OnEntry;
        try
        {
            foreach (var e in log.Recent(WebLogApi.MaxLimit).Where(e => e.T.ToUniversalTime() >= sinceUtc))
                OnEntry(e.T, e.M, e.C);

            while (ws.State == WebSocketState.Open && await queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                if (sessions.Validate(session.Id) == null) break;
                while (queue.Reader.TryRead(out var entry))
                    await SendJson(ws, new { type = "log", entry }, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            log.EntryAdded -= OnEntry;
        }
    }

    static async Task SendJson(WebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    static string ColorHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
