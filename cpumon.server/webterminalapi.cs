using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public sealed class WebTerminalStore : IDisposable
{
    static readonly TimeSpan ClosedSessionTtl = TimeSpan.FromSeconds(30);

    readonly ServerEngine _engine;
    readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    volatile bool _disposed;

    public WebTerminalStore(ServerEngine engine)
    {
        _engine = engine;
        _engine.TerminalOutputUpdated += OnOutput;
        _engine.TerminalClosedUpdated += OnClosed;
    }

    public WebTerminalSession Create(string machine, string termId, string shell)
    {
        SweepClosed();
        var session = new Session(machine, termId, shell);
        _sessions[Key(machine, termId)] = session;
        return session.Snapshot(0);
    }

    public bool Exists(string machine, string termId) => _sessions.ContainsKey(Key(machine, termId));

    public WebTerminalSession? Read(string machine, string termId, long since)
    {
        SweepClosed();
        if (!_sessions.TryGetValue(Key(machine, termId), out var session)) return null;
        return session.Snapshot(since);
    }

    public void MarkClosed(string machine, string termId)
    {
        if (_sessions.TryGetValue(Key(machine, termId), out var session))
            session.MarkClosed();
        SweepClosed();
    }

    public bool Remove(string machine, string termId) => _sessions.TryRemove(Key(machine, termId), out _);

    void SweepClosed()
    {
        var cutoff = DateTime.UtcNow - ClosedSessionTtl;
        foreach (var kv in _sessions)
            if (kv.Value.ClosedAt is { } closedAt && closedAt < cutoff)
                _sessions.TryRemove(kv.Key, out _);
    }

    void OnOutput(RemoteClient cl, string termId, string output)
    {
        if (_sessions.TryGetValue(Key(cl.MachineName, termId), out var session))
            session.Append(output);
    }

    void OnClosed(RemoteClient cl, string termId)
    {
        if (_sessions.TryGetValue(Key(cl.MachineName, termId), out var session))
            session.MarkClosed();
    }

    static string Key(string machine, string termId) => machine + "\n" + termId;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.TerminalOutputUpdated -= OnOutput;
        _engine.TerminalClosedUpdated -= OnClosed;
        foreach (var session in _sessions.Values)
        {
            try { _engine.RequestTerminalClose(session.Machine, session.TermId); } catch { }
        }
        _sessions.Clear();
    }

    sealed class Session
    {
        const int MaxChunks = 500;
        const int MaxTextChars = 128 * 1024;
        readonly object _lock = new();
        readonly List<WebTerminalChunk> _chunks = new();
        long _nextSeq;
        int _textChars;
        bool _closed;
        DateTime? _closedAt;
        DateTime _updatedAt = DateTime.UtcNow;

        public string Machine { get; }
        public string TermId { get; }
        public string Shell { get; }
        public DateTime? ClosedAt { get { lock (_lock) return _closedAt; } }

        public Session(string machine, string termId, string shell)
        {
            Machine = machine;
            TermId = termId;
            Shell = shell;
        }

        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                _chunks.Add(new WebTerminalChunk(++_nextSeq, text));
                _textChars += text.Length;
                Trim();
                _updatedAt = DateTime.UtcNow;
            }
        }

        public void MarkClosed()
        {
            lock (_lock)
            {
                if (!_closed)
                {
                    _closed = true;
                    _closedAt = DateTime.UtcNow;
                }
                _updatedAt = DateTime.UtcNow;
            }
        }

        public WebTerminalSession Snapshot(long since)
        {
            lock (_lock)
            {
                var chunks = _chunks.Where(c => c.Seq > since).ToArray();
                return new WebTerminalSession(Machine, TermId, Shell, _nextSeq, _closed, _updatedAt, chunks);
            }
        }

        void Trim()
        {
            while (_chunks.Count > MaxChunks || _textChars > MaxTextChars)
            {
                _textChars -= _chunks[0].Text.Length;
                _chunks.RemoveAt(0);
            }
        }
    }
}

public sealed record WebTerminalSession(string Machine,
                                        string TermId,
                                        string Shell,
                                        long NextSeq,
                                        bool Closed,
                                        DateTime UpdatedAt,
                                        IReadOnlyList<WebTerminalChunk> Chunks);

public sealed record WebTerminalChunk(long Seq, string Text);

public static class WebTerminalApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           WebTerminalStore terminals,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapPost("/api/clients/{machine}/terminal/open", async (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!engine.Clients.TryGetValue(machine, out var cl)) return NotFound(ctx, machine);

            var body = await TryRead<TerminalOpenRequest>(ctx);
            var shell = NormalizeShell(body?.Shell, ServerEngine.IsLinuxClient(cl));
            var termId = "web-" + Guid.NewGuid().ToString("N")[..12];
            terminals.Create(machine, termId, shell);
            if (!engine.RequestTerminalOpen(machine, termId, shell))
            {
                terminals.Remove(machine, termId);
                return NotFound(ctx, machine);
            }
            apiCtx.Log?.Add($"Web terminal: {machine} ({shell})", Th.Cyan);
            return Results.Json(new { termId, shell }, JsonOpts);
        });

        app.MapGet("/api/clients/{machine}/terminal/{termId}/output", (HttpContext ctx, string machine, string termId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            long.TryParse(ctx.Request.Query["since"], out var since);
            var session = terminals.Read(machine, termId, since);
            return session == null ? NotFound(ctx, machine) : Results.Json(session, JsonOpts);
        });

        app.MapPost("/api/clients/{machine}/terminal/{termId}/input", async (HttpContext ctx, string machine, string termId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!terminals.Exists(machine, termId)) return NotFound(ctx, machine);
            var body = await TryRead<TerminalInputRequest>(ctx);
            var input = body?.Input ?? "";
            if (input.Length > 4000) return Error(ctx, 400, "input_too_long", "Web terminal input is limited to 4000 characters per send. Split long pastes into multiple lines.");
            if (!engine.RequestTerminalInput(machine, termId, input)) return NotFound(ctx, machine);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/terminal/{termId}/close", (HttpContext ctx, string machine, string termId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            terminals.MarkClosed(machine, termId);
            engine.RequestTerminalClose(machine, termId);
            return Results.NoContent();
        });
    }

    static string NormalizeShell(string? requested, bool linux)
    {
        if (linux) return "bash";
        if (string.Equals(requested, "powershell", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requested, "pwsh", StringComparison.OrdinalIgnoreCase))
            return requested!;
        return "cmd";
    }

    static async Task<T?> TryRead<T>(HttpContext ctx) where T : class
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, JsonOpts);
        }
        catch { return null; }
    }

    static IResult NotFound(HttpContext ctx, string machine)
        => Error(ctx, 404, "not_found", $"Machine '{machine}' is not connected.");

    static IResult Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message }, JsonOpts);
    }
}

public sealed class TerminalOpenRequest
{
    public string? Shell { get; set; }
}

public sealed class TerminalInputRequest
{
    public string Input { get; set; } = "";
}
