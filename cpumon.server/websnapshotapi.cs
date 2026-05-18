using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebSnapshotApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           SnapshotCache cache,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapGet("/api/clients/{machine}/processes", (HttpContext ctx, string machine) =>
            Serve(ctx, sessions, engine, machine, SnapshotKind.Processes, cl => cl.LastProcessList, cache));

        app.MapGet("/api/clients/{machine}/sysinfo", (HttpContext ctx, string machine) =>
            Serve(ctx, sessions, engine, machine, SnapshotKind.SysInfo, cl => cl.LastSysInfo, cache));

        app.MapGet("/api/clients/{machine}/services", (HttpContext ctx, string machine) =>
            Serve(ctx, sessions, engine, machine, SnapshotKind.Services, cl => cl.LastServiceList, cache));

        app.MapGet("/api/clients/{machine}/events", (HttpContext ctx, string machine) =>
            Serve(ctx, sessions, engine, machine, SnapshotKind.Events, cl => cl.LastEvents, cache));

        app.MapGet("/api/clients/{machine}/cpu-detail", (HttpContext ctx, string machine) =>
            Serve(ctx, sessions, engine, machine, SnapshotKind.CpuDetail, _ => cache.GetCpuDetail(machine), cache));

        app.MapGet("/api/clients/{machine}/health", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            if (!engine.Clients.TryGetValue(machine, out var cl)) return NotFound(ctx, machine);
            return Results.Json(BuildHealth(cl), JsonOpts);
        });
    }

    static IResult Serve(HttpContext ctx, SessionStore sessions, ServerEngine engine, string machine,
                         SnapshotKind kind, Func<RemoteClient, object?> read, SnapshotCache cache)
    {
        if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
        if (!engine.Clients.TryGetValue(machine, out var cl)) return NotFound(ctx, machine);
        bool force = string.Equals(ctx.Request.Query["force"], "true", StringComparison.OrdinalIgnoreCase);
        if (force || cache.IsStale(machine, kind)) cache.TryTriggerFetch(machine, kind);
        var data = read(cl);
        if (data == null) return Results.StatusCode(204);
        var receivedAt = cache.ReceivedAt(machine, kind);
        return Results.Json(new { snapshot = data, receivedAt }, JsonOpts);
    }

    static HealthSummary BuildHealth(RemoteClient cl)
    {
        var r = cl.LastReport;
        return new HealthSummary
        {
            MachineName    = cl.MachineName,
            ClientVersion  = cl.ClientVersion,
            OsVersion      = r?.OsVersion ?? "",
            LastSeen       = cl.LastSeen,
            AgeSeconds     = (DateTime.UtcNow - cl.LastSeen).TotalSeconds,
            HasReport      = r != null,
            Load           = r?.TotalLoadPercent,
            TemperatureC   = r?.PackageTemperatureC,
            FrequencyMHz   = r?.PackageFrequencyMHz,
            RamUsedGB      = r?.RamUsedGB,
            RamTotalGB     = r?.RamTotalGB,
        };
    }

    static IResult NotFound(HttpContext ctx, string machine)
        => Error(ctx, 404, "not_found", $"Machine '{machine}' is not connected.");

    static IResult Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message }, JsonOpts);
    }
}

public sealed class HealthSummary
{
    public string   MachineName   { get; set; } = "";
    public string   ClientVersion { get; set; } = "";
    public string   OsVersion     { get; set; } = "";
    public DateTime LastSeen      { get; set; }
    public double   AgeSeconds    { get; set; }
    public bool     HasReport     { get; set; }
    public float?   Load          { get; set; }
    public float?   TemperatureC  { get; set; }
    public float?   FrequencyMHz  { get; set; }
    public double?  RamUsedGB     { get; set; }
    public double?  RamTotalGB    { get; set; }
}
