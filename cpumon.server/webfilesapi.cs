using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

// Web-side file browser broker. Each browser tab opens a session keyed by a
// random sessionId; the server allocates that ID as both the listing/result
// cmdId and the routing key for active transfers. ServerEngine fires
// FileListingUpdated / FileChunkUpdated / FileResultUpdated; this store
// filters and buffers them for HTTP polling.
public sealed class WebFileBrowserStore : IDisposable
{
    public const long MaxTransferBytes = 200L * 1024 * 1024;   // 200 MB cap for first slice
    public const int  UploadChunkBytes  = Proto.FileChunkSize;  // matches agent ReceiveChunk

    readonly ServerEngine _engine;
    readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    readonly string _stagingRoot;
    readonly Timer? _pruner;
    volatile bool _disposed;

    /// <summary>How long a session may sit untouched before the background sweep removes it.</summary>
    public TimeSpan IdleSessionTtl { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How often the background sweep checks for idle sessions.</summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromMinutes(5);

    public WebFileBrowserStore(ServerEngine engine, bool startPruner = true)
    {
        _engine = engine;
        _stagingRoot = AppPaths.DataFile("webdl");
        try { Directory.CreateDirectory(_stagingRoot); }
        catch
        {
            _stagingRoot = Path.Combine(Path.GetTempPath(), "CpuMon", "webdl");
            Directory.CreateDirectory(_stagingRoot);
        }
        SweepOrphanedStagingDirs();
        _engine.FileListingUpdated += OnListing;
        _engine.FileChunkUpdated   += OnChunk;
        _engine.FileResultUpdated  += OnResult;
        if (startPruner)
            _pruner = new Timer(_ => Prune(), null, PruneInterval, PruneInterval);
    }

    // The server runs as a single instance; any subdirs already in _stagingRoot
    // belong to a prior process that exited without disposing its sessions. Reap
    // them so the staging root does not grow without bound across restarts.
    void SweepOrphanedStagingDirs()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_stagingRoot))
                try { Directory.Delete(dir, recursive: true); } catch { }
        }
        catch { }
    }

    public Session Create(string machine)
    {
        var sessionId = "web-" + Guid.NewGuid().ToString("N")[..12];
        var session = new Session(machine, sessionId, _stagingRoot);
        _sessions[sessionId] = session;
        return session;
    }

    public Session? Get(string machine, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        if (!string.Equals(s.Machine, machine, StringComparison.Ordinal)) return null;
        s.Touch();
        return s;
    }

    public bool Remove(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var s)) return false;
        s.Dispose();
        return true;
    }

    public int Count => _sessions.Count;

    /// <summary>Drops every session whose LastTouched is older than IdleSessionTtl. Returns the number removed.</summary>
    public int Prune()
    {
        var cutoff = DateTime.UtcNow - IdleSessionTtl;
        int removed = 0;
        foreach (var kv in _sessions)
            if (kv.Value.LastTouched < cutoff)
            {
                if (_sessions.TryRemove(kv.Key, out var s)) { s.Dispose(); removed++; }
            }
        return removed;
    }

    void OnListing(RemoteClient cl, string? cmdId, FileListing listing)
    {
        if (cmdId == null) return;
        if (_sessions.TryGetValue(cmdId, out var session) &&
            string.Equals(session.Machine, cl.MachineName, StringComparison.Ordinal))
            session.PutListing(listing);
    }

    void OnResult(RemoteClient cl, string cmdId, bool ok, string message)
    {
        if (_sessions.TryGetValue(cmdId, out var session) &&
            string.Equals(session.Machine, cl.MachineName, StringComparison.Ordinal))
            session.PutResult(ok, message);
    }

    // Routing for incoming file_chunk messages. Public so tests can drive the same path
    // the engine's FileChunkUpdated event invokes.
    public void OnChunk(RemoteClient cl, string? cmdId, FileChunkData chunk)
    {
        if (string.IsNullOrEmpty(chunk.TransferId)) return;
        // Preferred path: the agent echoes the originating file_download cmdId on every
        // chunk, so we look the session up directly by sessionId. The fallback walks the
        // machine's sessions for backwards compatibility with older agents that do not
        // stamp cmdId yet.
        if (!string.IsNullOrEmpty(cmdId) && _sessions.TryGetValue(cmdId, out var direct))
        {
            if (string.Equals(direct.Machine, cl.MachineName, StringComparison.Ordinal))
                direct.TryReceiveChunk(chunk);
            return;
        }
        foreach (var session in _sessions.Values)
        {
            if (!string.Equals(session.Machine, cl.MachineName, StringComparison.Ordinal)) continue;
            if (session.TryReceiveChunk(chunk)) return;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pruner?.Dispose();
        _engine.FileListingUpdated -= OnListing;
        _engine.FileChunkUpdated   -= OnChunk;
        _engine.FileResultUpdated  -= OnResult;
        // Each Session.Dispose removes its own staging subdir; we deliberately leave
        // _stagingRoot in place because it can be shared (production: a single store
        // per process; tests: multiple stores constructed against the same %ProgramData%
        // path). Stale subdirs from a crashed previous process get reaped by Prune as
        // sessions age out, or on next startup by Session.Dispose taking ownership.
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }

    public sealed class Session : IDisposable
    {
        readonly object _lock = new();
        readonly string _stagingDir;
        readonly ConcurrentDictionary<string, Transfer> _downloads = new(StringComparer.Ordinal);
        FileListing? _listing;
        bool _ok;
        string _result = "";
        long _listingSeq;
        long _resultSeq;
        // Bumped by every session-level mutation (PutListing, PutResult). Used to
        // memoise the idle-state snapshot so repeated SPA polls return the same
        // object without re-allocating the outer record + transfers array.
        long _revision;
        long _cachedRevision = -1;
        WebFileBrowserState? _cachedSnap;
        DateTime _lastTouched = DateTime.UtcNow;

        public string Machine   { get; }
        public string SessionId { get; }
        public DateTime LastTouched { get { lock (_lock) return _lastTouched; } }

        public Session(string machine, string sessionId, string stagingRoot)
        {
            Machine     = machine;
            SessionId   = sessionId;
            _stagingDir = Path.Combine(stagingRoot, sessionId);
            try { Directory.CreateDirectory(_stagingDir); } catch { }
        }

        public void Touch() { lock (_lock) _lastTouched = DateTime.UtcNow; }

        public void PutListing(FileListing listing)
        {
            lock (_lock)
            {
                _listing = listing;
                _listingSeq++;
                _revision++;
                _lastTouched = DateTime.UtcNow;
            }
        }

        public void PutResult(bool ok, string message)
        {
            lock (_lock)
            {
                _ok = ok;
                _result = message;
                _resultSeq++;
                _revision++;
                _lastTouched = DateTime.UtcNow;
            }
        }

        public WebFileBrowserState Snapshot()
        {
            lock (_lock)
            {
                // With at least one transfer in flight, chunks land outside the
                // session lock and the snapshot can change on every call — always
                // build fresh. The cached fast path is for the much more common
                // idle case (browser sitting on a directory listing, polling).
                if (!_downloads.IsEmpty)
                {
                    return new WebFileBrowserState(
                        SessionId, _listingSeq, _listing, _resultSeq, _result, _ok,
                        _downloads.Values.Select(t => t.Snapshot()).ToArray());
                }
                if (_cachedSnap != null && _cachedRevision == _revision)
                    return _cachedSnap;
                _cachedSnap = new WebFileBrowserState(
                    SessionId, _listingSeq, _listing, _resultSeq, _result, _ok,
                    Array.Empty<WebFileBrowserTransfer>());
                _cachedRevision = _revision;
                return _cachedSnap;
            }
        }

        public Transfer NewDownload(string transferId, string remoteFileName)
        {
            var t = new Transfer(transferId, remoteFileName, Path.Combine(_stagingDir, transferId + ".part"));
            _downloads[transferId] = t;
            Touch();
            return t;
        }

        public Transfer? GetTransfer(string transferId)
        {
            _downloads.TryGetValue(transferId, out var t);
            return t;
        }

        public bool RemoveTransfer(string transferId)
        {
            if (!_downloads.TryRemove(transferId, out var t)) return false;
            t.Dispose();
            return true;
        }

        public bool TryReceiveChunk(FileChunkData chunk)
        {
            if (!_downloads.TryGetValue(chunk.TransferId, out var t)) return false;
            t.ReceiveChunk(chunk);
            Touch();
            return true;
        }

        public void Dispose()
        {
            foreach (var t in _downloads.Values) t.Dispose();
            _downloads.Clear();
            try { Directory.Delete(_stagingDir, recursive: true); } catch { }
        }
    }

    public sealed class Transfer : IDisposable
    {
        readonly object _lock = new();
        FileStream? _stream;
        long _receivedBytes;
        long _totalBytes;
        bool _complete;
        bool _capped;
        string _error = "";
        string _fileName;

        public string TransferId { get; }
        public string PartPath   { get; }
        public bool   Complete   { get { lock (_lock) return _complete; } }
        public bool   Capped     { get { lock (_lock) return _capped; } }
        public bool   HasError   { get { lock (_lock) return _error.Length > 0; } }
        public string FileName   { get { lock (_lock) return _fileName; } }

        public Transfer(string transferId, string remoteFileName, string partPath)
        {
            TransferId = transferId;
            PartPath   = partPath;
            _fileName  = remoteFileName;
            _stream    = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }

        public void ReceiveChunk(FileChunkData chunk)
        {
            lock (_lock)
            {
                if (_complete || _capped || _error.Length > 0 || _stream == null) return;
                if (!string.IsNullOrEmpty(chunk.FileName)) _fileName = chunk.FileName;
                if (chunk.Error != null)
                {
                    _error = chunk.Error;
                    DisposeStream();
                    return;
                }
                if (chunk.TotalSize > 0) _totalBytes = chunk.TotalSize;
                if (_totalBytes > MaxTransferBytes)
                {
                    _capped = true;
                    DisposeStream();
                    return;
                }
                if (!string.IsNullOrEmpty(chunk.Data))
                {
                    byte[] data;
                    try { data = Convert.FromBase64String(chunk.Data); }
                    catch (Exception ex) { _error = ex.Message; DisposeStream(); return; }
                    if (_receivedBytes + data.Length > MaxTransferBytes)
                    {
                        _capped = true;
                        DisposeStream();
                        return;
                    }
                    _stream.Write(data, 0, data.Length);
                    _receivedBytes += data.Length;
                }
                if (chunk.IsLast)
                {
                    _stream.Flush();
                    DisposeStream();
                    _complete = true;
                }
            }
        }

        public WebFileBrowserTransfer Snapshot()
        {
            lock (_lock)
            {
                string state = _capped     ? "capped"
                             : _error != "" ? "error"
                             : _complete   ? "complete"
                             : "active";
                return new WebFileBrowserTransfer(TransferId, _fileName, _receivedBytes, _totalBytes, state, _error);
            }
        }

        public Stream OpenReadAndConsume()
        {
            lock (_lock)
            {
                if (!_complete) throw new InvalidOperationException("transfer not complete");
                return new ConsumingFileStream(PartPath);
            }
        }

        void DisposeStream()
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                DisposeStream();
                try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
            }
        }
    }

    sealed class ConsumingFileStream : FileStream
    {
        readonly string _path;
        public ConsumingFileStream(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read) { _path = path; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }
}

public sealed record WebFileBrowserState(
    string SessionId,
    long ListingSeq,
    FileListing? Listing,
    long ResultSeq,
    string Result,
    bool ResultOk,
    IReadOnlyList<WebFileBrowserTransfer> Transfers);

public sealed record WebFileBrowserTransfer(
    string TransferId,
    string FileName,
    long Received,
    long Total,
    string State,
    string Error);

public static class WebFilesApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           WebFileBrowserStore files,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapPost("/api/clients/{machine}/files/open", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!engine.Clients.TryGetValue(machine, out _)) return NotFound(ctx, machine);
            var session = files.Create(machine);
            apiCtx.Log?.Add($"Web files: {machine} session opened", Th.Cyan);
            return Results.Json(new { sessionId = session.SessionId }, JsonOpts);
        });

        app.MapPost("/api/clients/{machine}/files/{sessionId}/close", (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            files.Remove(sessionId);
            return Results.NoContent();
        });

        app.MapGet("/api/clients/{machine}/files/{sessionId}", (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            var session = files.Get(machine, sessionId);
            if (session == null) return NotFound(ctx, sessionId);
            return Results.Json(session.Snapshot(), JsonOpts);
        });

        app.MapPost("/api/clients/{machine}/files/{sessionId}/list", async (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (files.Get(machine, sessionId) == null) return NotFound(ctx, sessionId);
            var body = await TryRead<PathRequest>(ctx);
            if (!engine.RequestFileList(machine, sessionId, body?.Path)) return NotFound(ctx, machine);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/files/{sessionId}/mkdir", async (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (files.Get(machine, sessionId) == null) return NotFound(ctx, sessionId);
            var body = await TryRead<PathRequest>(ctx);
            if (string.IsNullOrEmpty(body?.Path)) return Error(ctx, 400, "missing_path", "Path is required.");
            if (!engine.RequestFileMkdir(machine, sessionId, body.Path)) return NotFound(ctx, machine);
            apiCtx.Log?.Add($"Web files: mkdir {machine}:{LogPath(body.Path)}", Th.Yel);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/files/{sessionId}/delete", async (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (files.Get(machine, sessionId) == null) return NotFound(ctx, sessionId);
            var body = await TryRead<DeleteRequest>(ctx);
            if (string.IsNullOrEmpty(body?.Path)) return Error(ctx, 400, "missing_path", "Path is required.");
            if (!engine.RequestFileDelete(machine, sessionId, body.Path, body.Recursive)) return NotFound(ctx, machine);
            apiCtx.Log?.Add($"Web files: delete {machine}:{LogPath(body.Path)}", Th.Org);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/files/{sessionId}/rename", async (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (files.Get(machine, sessionId) == null) return NotFound(ctx, sessionId);
            var body = await TryRead<RenameRequest>(ctx);
            if (string.IsNullOrEmpty(body?.Path) || string.IsNullOrEmpty(body.NewName))
                return Error(ctx, 400, "missing_field", "Path and newName are required.");
            if (!engine.RequestFileRename(machine, sessionId, body.Path, body.NewName)) return NotFound(ctx, machine);
            apiCtx.Log?.Add($"Web files: rename {machine}:{LogPath(body.Path)} → {LogPath(body.NewName)}", Th.Yel);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/files/{sessionId}/download", async (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var session = files.Get(machine, sessionId);
            if (session == null) return NotFound(ctx, sessionId);
            var body = await TryRead<PathRequest>(ctx);
            if (string.IsNullOrEmpty(body?.Path)) return Error(ctx, 400, "missing_path", "Path is required.");
            string transferId = "dl-" + Guid.NewGuid().ToString("N")[..12];
            session.NewDownload(transferId, Path.GetFileName(body.Path));
            if (!engine.RequestFileDownload(machine, sessionId, transferId, body.Path))
            {
                session.RemoveTransfer(transferId);
                return NotFound(ctx, machine);
            }
            apiCtx.Log?.Add($"Web files: download {machine}:{LogPath(body.Path)}", Th.Cyan);
            return Results.Json(new { transferId, fileName = Path.GetFileName(body.Path) }, JsonOpts);
        });

        app.MapGet("/api/clients/{machine}/files/{sessionId}/download/{transferId}", (HttpContext ctx, string machine, string sessionId, string transferId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            var session = files.Get(machine, sessionId);
            if (session == null) return NotFound(ctx, sessionId);
            var t = session.GetTransfer(transferId);
            if (t == null) return NotFound(ctx, transferId);
            // Single snapshot under the lock so a chunk landing mid-handler cannot
            // make us read inconsistent state across the Capped/HasError/Complete
            // properties (each one re-takes the transfer lock separately).
            var snap = t.Snapshot();
            switch (snap.State)
            {
                case "capped":
                    session.RemoveTransfer(transferId);
                    return Error(ctx, 409, "too_large", $"Transfer exceeds {WebFileBrowserStore.MaxTransferBytes / (1024 * 1024)} MB cap.");
                case "error":
                    session.RemoveTransfer(transferId);
                    return Error(ctx, 409, "transfer_error", snap.Error);
                case "active":
                    return Results.StatusCode(204);
                case "complete":
                    var stream = t.OpenReadAndConsume();
                    session.RemoveTransfer(transferId);
                    return Results.File(stream, "application/octet-stream", snap.FileName);
                default:
                    return Error(ctx, 500, "unknown_state", snap.State);
            }
        });

        app.MapPost("/api/clients/{machine}/files/{sessionId}/upload", async (HttpContext ctx, string machine, string sessionId) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (files.Get(machine, sessionId) == null) return NotFound(ctx, sessionId);
            string? destPath = ctx.Request.Query["dest"];
            string  fileName = ctx.Request.Query["name"];
            if (string.IsNullOrEmpty(destPath)) return Error(ctx, 400, "missing_dest", "Query param 'dest' is required.");
            if (string.IsNullOrEmpty(fileName)) return Error(ctx, 400, "missing_name", "Query param 'name' is required.");
            if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return Error(ctx, 400, "bad_name", "Invalid filename.");

            long total = ctx.Request.ContentLength ?? -1;
            if (total < 0) return Error(ctx, 400, "missing_length", "Content-Length is required for upload.");
            if (total > WebFileBrowserStore.MaxTransferBytes)
                return Error(ctx, 413, "too_large", $"Upload exceeds {WebFileBrowserStore.MaxTransferBytes / (1024 * 1024)} MB cap.");
            // The read loop below uses `total` to cap each read; Kestrel additionally
            // enforces Content-Length at the transport, so we cannot accidentally consume
            // (or forward to the agent) more bytes than the client declared.

            string transferId = "ul-" + Guid.NewGuid().ToString("N")[..12];
            var buf = new byte[WebFileBrowserStore.UploadChunkBytes];
            long offset = 0;
            var input = ctx.Request.Body;
            bool clientStillConnected = true;
            try
            {
                while (offset < total)
                {
                    int want = (int)Math.Min(buf.Length, total - offset);
                    int got = 0;
                    while (got < want)
                    {
                        int n = await input.ReadAsync(buf.AsMemory(got, want - got), ctx.RequestAborted);
                        if (n == 0) break;
                        got += n;
                    }
                    if (got == 0) break;
                    bool last = offset + got >= total;
                    var chunk = new FileChunkData
                    {
                        TransferId = transferId,
                        FileName   = fileName,
                        Data       = Convert.ToBase64String(buf, 0, got),
                        Offset     = offset,
                        TotalSize  = total,
                        IsLast     = last,
                    };
                    if (!engine.RequestFileUploadChunk(machine, sessionId, destPath, chunk))
                    {
                        clientStillConnected = false;
                        return NotFound(ctx, machine);
                    }
                    offset += got;
                    if (last) break;
                }
                if (offset != total)
                {
                    // Browser cut the request mid-stream. Tell the agent to drop the
                    // partial .tmp so it does not linger until the next ActiveUploads
                    // sweep on disconnect.
                    if (clientStillConnected) engine.RequestFileUploadAbort(machine, sessionId, transferId);
                    return Error(ctx, 400, "short_read", "Upload body was shorter than Content-Length.");
                }
                apiCtx.Log?.Add($"Web files: upload {machine}:{LogPath(destPath)}/{fileName} ({total}B)", Th.Cyan);
                return Results.NoContent();
            }
            catch (OperationCanceledException)
            {
                if (clientStillConnected) engine.RequestFileUploadAbort(machine, sessionId, transferId);
                throw;
            }
        });
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

    // Echoes paths into the operator log have already passed through agent-side
    // IsPathUnder traversal checks at the receiving end; this just keeps the log
    // line tidy when an oversized or accidental path comes through.
    static string LogPath(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    static IResult NotFound(HttpContext ctx, string what)
        => Error(ctx, 404, "not_found", $"'{what}' not found.");

    static IResult Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message }, JsonOpts);
    }
}

public sealed class PathRequest   { public string? Path { get; set; } }
public sealed class DeleteRequest { public string? Path { get; set; } public bool Recursive { get; set; } }
public sealed class RenameRequest { public string? Path { get; set; } public string? NewName { get; set; } }
