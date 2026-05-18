using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class WebHostOptions
{
    public int      Port          { get; init; } = 47202;
    public string   BindAddress   { get; init; } = "0.0.0.0";
    public bool     UseTls        { get; init; } = true;
    /// <summary>If null, falls back to AppPaths.DataFile("webcert.pfx") then AppPaths.DataFile("cpumon.pfx").</summary>
    public string?  CertPath      { get; init; }
    public string?  CertPassword  { get; init; }
    public bool     BehindProxy   { get; init; }
    public string   ServerVersion { get; init; } = "?";
    public DateTime StartedAt     { get; init; } = DateTime.UtcNow;
    public CLog?    Log           { get; init; }
    /// <summary>Called after middleware is installed and /api/healthz is mapped but before app.StartAsync().
    /// Use to register additional API endpoints from per-concern modules (webauthapi, webdashboardapi, …).</summary>
    public Action<WebApplication, WebApiContext>? ConfigureRoutes { get; init; }
}

/// <summary>Read-only view of host options passed to route configurators so they can shape responses correctly
/// (Secure cookie attribute, X-Forwarded-For handling, log routing).</summary>
public sealed class WebApiContext
{
    public bool   UseTls      { get; init; }
    public bool   BehindProxy { get; init; }
    public CLog?  Log         { get; init; }

    public string ClientIp(HttpContext ctx)
    {
        if (BehindProxy)
        {
            var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrEmpty(fwd))
            {
                int comma = fwd.IndexOf(',');
                return (comma > 0 ? fwd[..comma] : fwd).Trim();
            }
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    }
}

public sealed class WebHost : IAsyncDisposable
{
    WebApplication? _app;
    WebHostOptions? _options;

    public bool IsRunning => _app != null;
    public int Port { get; private set; }

    public async Task StartAsync(WebHostOptions options)
    {
        if (_app != null) throw new InvalidOperationException("WebHost already started");
        _options = options;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseKestrel(k =>
        {
            var ip = System.Net.IPAddress.Parse(options.BindAddress);
            k.Listen(ip, options.Port, listen =>
            {
                if (options.UseTls)
                {
                    var cert = LoadCert(options);
                    listen.UseHttps(cert);
                }
            });
        });

        var app = builder.Build();

        // Security headers — apply before any handler writes a response.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"]        = "DENY";
                h["Referrer-Policy"]        = "no-referrer";
                // 'unsafe-inline' on script-src is here only so the /setup page's bootstrap form
                // can run its inline submit handler. Phase 3 will replace this with per-request
                // nonces once the SPA assets land and inline script becomes the exception, not
                // the rule.
                h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
                if (options.UseTls)
                    h["Strict-Transport-Security"] = "max-age=31536000";
                h["Server"] = $"cpumon/{options.ServerVersion}";
                return Task.CompletedTask;
            });
            await next().ConfigureAwait(false);
        });

        // Request log → CLog. Format: METHOD /path → status (12ms) from 10.0.4.12
        app.Use(async (ctx, next) =>
        {
            var sw = Stopwatch.StartNew();
            try { await next().ConfigureAwait(false); }
            finally
            {
                sw.Stop();
                if (options.Log != null)
                {
                    int status = ctx.Response.StatusCode;
                    string method = ctx.Request.Method;
                    string path = ctx.Request.Path.Value ?? "";
                    string ip = RemoteIp(ctx, options.BehindProxy);
                    var color = status >= 500 ? Th.Red
                              : status >= 400 ? Th.Org
                              : status >= 300 ? Th.Yel
                              :                 Th.Dim;
                    options.Log.Add($"{method} {path} → {status} ({sw.ElapsedMilliseconds}ms) {ip}", color);
                }
            }
        });

        app.MapGet("/api/healthz", (HttpContext _) =>
        {
            return Results.Json(new
            {
                ok          = true,
                version     = options.ServerVersion,
                uptimeSec   = (long)(DateTime.UtcNow - options.StartedAt).TotalSeconds
            });
        });

        if (options.ConfigureRoutes != null)
        {
            var apiCtx = new WebApiContext
            {
                UseTls      = options.UseTls,
                BehindProxy = options.BehindProxy,
                Log         = options.Log
            };
            options.ConfigureRoutes(app, apiCtx);
        }

        await app.StartAsync().ConfigureAwait(false);

        // Record the actually-bound port (useful when Port=0 was passed for tests).
        Port = ResolveBoundPort(app) ?? options.Port;
        _app = app;

        options.Log?.Add($"Web UI: listening on {(options.UseTls ? "https" : "http")}://{options.BindAddress}:{Port}", Th.Grn);
    }

    public async Task StopAsync()
    {
        if (_app == null) return;
        try { await _app.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { _options?.Log?.Add($"Web UI: stop error ({ex.GetType().Name})", Th.Org); }
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _options?.Log?.Add("Web UI: stopped", Th.Dim);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    static X509Certificate2 LoadCert(WebHostOptions options)
    {
        var candidates = new[]
        {
            options.CertPath,
            AppPaths.DataFile("webcert.pfx"),
            AppPaths.DataFile("cpumon.pfx")
        };
        foreach (var path in candidates)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (!File.Exists(path)) continue;
            return X509CertificateLoader.LoadPkcs12FromFile(path, options.CertPassword);
        }
        throw new FileNotFoundException("No PFX found for web TLS (checked CertPath, webcert.pfx, cpumon.pfx)");
    }

    static int? ResolveBoundPort(WebApplication app)
    {
        var feature = app.Services.GetService<IServer>()?.Features?.Get<IServerAddressesFeature>();
        var addr = feature?.Addresses?.FirstOrDefault();
        if (addr == null) return null;
        // e.g. "http://[::]:54321" or "https://0.0.0.0:47202"
        int colon = addr.LastIndexOf(':');
        if (colon < 0 || colon == addr.Length - 1) return null;
        return int.TryParse(addr[(colon + 1)..], out var p) ? p : null;
    }

    static string RemoteIp(HttpContext ctx, bool behindProxy)
        => new WebApiContext { BehindProxy = behindProxy }.ClientIp(ctx);
}
