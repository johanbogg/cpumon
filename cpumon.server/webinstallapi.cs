using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebInstallApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxTtl     = TimeSpan.FromDays(7);

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           InstallLinkStore links,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapPost("/api/install-links", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out var session, out var fail)) return fail!;
            var body = await TryRead<IssueRequest>(ctx);

            string serverIp = NormalizeServerIp(body?.ServerIp, ctx);
            if (string.IsNullOrEmpty(serverIp))
                return Error(ctx, 400, "validation_failed", "Server IP could not be determined; pass { serverIp }.");

            TimeSpan ttl = ParseTtl(body?.TtlHours) ?? DefaultTtl;
            if (ttl > MaxTtl) ttl = MaxTtl;

            string thumb = CertificateStore.ServerCert().Thumbprint ?? "";
            if (string.IsNullOrEmpty(thumb))
                return Error(ctx, 503, "no_cert", "Server certificate has no thumbprint yet; install links cannot be issued.");

            var link = links.Issue(session!.Username, serverIp, thumb, ttl);
            engine.Log.Add($"Web UI: install link issued by {session.Username} (expires {link.ExpiresAt:HH:mm:ss} UTC)", Th.Cyan);
            return Results.Json(BuildView(link, BuildUrl(ctx, link.Code)), JsonOpts);
        });

        app.MapGet("/api/install-links", (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            var views = links.All().Select(l => BuildView(l, BuildUrl(ctx, l.Code))).ToList();
            return Results.Json(views, JsonOpts);
        });

        app.MapDelete("/api/install-links/{code}", (HttpContext ctx, string code) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out var session, out var fail)) return fail!;
            if (!links.Revoke(code))
                return Error(ctx, 404, "not_found", $"Install link '{code}' was not found.");
            engine.Log.Add($"Web UI: install link revoked by {session!.Username}", Th.Yel);
            return Results.NoContent();
        });

        // Unauthenticated, one-shot bundle download. The 95-bit unguessable code
        // IS the credential.
        app.MapGet("/install/{code}", (HttpContext ctx, string code) =>
        {
            var link = links.GetUnused(code);
            if (link == null) return Error(ctx, 404, "not_found", "Install link is invalid, expired, or already used.");

            string? stagedDir = engine.StagedReleaseDir;
            if (string.IsNullOrEmpty(stagedDir))
                return Error(ctx, 503, "no_staged_release", "No staged release available on the server.");
            string exePath = Path.Combine(stagedDir, "client", "cpumon.client.exe");
            if (!File.Exists(exePath))
                return Error(ctx, 503, "artifact_missing", "Staged release does not contain cpumon.client.exe.");

            string version = Proto.AppVersion;
            byte[] zipBytes;
            try
            {
                zipBytes = BuildBundle(exePath, link.ServerIp, link.ServerThumbprint, engine.Token, version);
            }
            catch (Exception ex)
            {
                engine.Log.Add($"Install bundle build failed: {ex.Message}", Th.Red);
                return Error(ctx, 500, "bundle_failed", "Could not build install bundle.");
            }

            if (links.Consume(code) == null)
                return Error(ctx, 404, "not_found", "Install link is invalid, expired, or already used.");
            engine.Log.Add($"Install link redeemed from {apiCtx.ClientIp(ctx)}", Th.Cyan);
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"cpumon-install-{version}.zip\"";
            return Results.Bytes(zipBytes, "application/zip");
        });
    }

    static string NormalizeServerIp(string? requested, HttpContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return SafeServerAddress(requested.Trim());
        var host = ctx.Request.Host.Host;
        return string.IsNullOrEmpty(host) ? "" : SafeServerAddress(host);
    }

    static string SafeServerAddress(string value)
        => value.IndexOfAny(new[] { '&', '|', '<', '>', '^', '"', '\r', '\n' }) >= 0 ? "" : value;

    static TimeSpan? ParseTtl(int? hours)
    {
        if (!hours.HasValue || hours.Value <= 0) return null;
        return TimeSpan.FromHours(hours.Value);
    }

    static string BuildUrl(HttpContext ctx, string code)
        => $"{ctx.Request.Scheme}://{ctx.Request.Host}/install/{code}";

    static InstallLinkView BuildView(InstallLink l, string url) => new()
    {
        Code      = l.Code,
        Url       = url,
        CreatedAt = l.CreatedAt,
        ExpiresAt = l.ExpiresAt,
        CreatedBy = l.CreatedBy,
        UsedAt    = l.UsedAt,
        ServerIp  = l.ServerIp,
        Active    = l.UsedAt == null && l.ExpiresAt > DateTime.UtcNow,
    };

    static byte[] BuildBundle(string exePath, string serverIp, string thumb, string token, string version)
    {
        var exeBytes = File.ReadAllBytes(exePath);
        var bat      = BuildBatch(serverIp, thumb, token);
        var readme   = BuildReadme(version, serverIp);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "cpumon.client.exe", CompressionLevel.Fastest, exeBytes);
            WriteEntry(zip, "install.bat",       CompressionLevel.Optimal, Encoding.UTF8.GetBytes(bat));
            WriteEntry(zip, "README.txt",        CompressionLevel.Optimal, Encoding.UTF8.GetBytes(readme));
        }
        return ms.ToArray();
    }

    static void WriteEntry(ZipArchive zip, string name, CompressionLevel level, byte[] data)
    {
        var entry = zip.CreateEntry(name, level);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    static string BuildBatch(string serverIp, string thumb, string token) =>
        "@echo off\r\n" +
        "echo Installing CPU Monitor client...\r\n" +
        $"\"%~dp0cpumon.client.exe\" --install --server-ip {BatchArg(serverIp)} --token {BatchArg(token)} --server-thumb {BatchArg(thumb)}\r\n" +
        "if errorlevel 1 (\r\n" +
        "  echo.\r\n" +
        "  echo Install failed.\r\n" +
        "  pause\r\n" +
        "  exit /b 1\r\n" +
        ")\r\n" +
        "echo.\r\n" +
        "echo Done. The CPU Monitor service is running.\r\n" +
        "pause\r\n";

    static string BatchArg(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    static string BuildReadme(string version, string serverIp) =>
        $"CPU Monitor client install bundle (v{version})\r\n" +
        "\r\n" +
        "1. Right-click install.bat and select 'Run as administrator'.\r\n" +
        "2. The CPU Monitor service installs and starts. The dashboard at\r\n" +
        $"   {serverIp} should show the new client within a few seconds.\r\n" +
        "\r\n" +
        "The server's TLS certificate thumbprint is pre-pinned in the install\r\n" +
        "command, so the first connection rejects any man-in-the-middle attempts.\r\n";

    static IResult Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message }, JsonOpts);
    }

    static Task<T?> TryRead<T>(HttpContext ctx) where T : class => WebJson.TryRead<T>(ctx);
}

public sealed class IssueRequest
{
    public string? ServerIp { get; set; }
    public int?    TtlHours { get; set; }
}

public sealed class InstallLinkView
{
    public string    Code      { get; set; } = "";
    public string    Url       { get; set; } = "";
    public DateTime  CreatedAt { get; set; }
    public DateTime  ExpiresAt { get; set; }
    public string    CreatedBy { get; set; } = "";
    public DateTime? UsedAt    { get; set; }
    public string    ServerIp  { get; set; } = "";
    public bool      Active    { get; set; }
}
