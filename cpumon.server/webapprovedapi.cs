using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebApprovedApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapGet("/api/approved", (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            var entries = engine.Store.All().Select(Project).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Json(entries, JsonOpts);
        });

        app.MapMethods("/api/approved/{machine}", new[] { "PATCH" }, async (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var canonical = Canonical(engine, machine);
            if (canonical == null) return NotFound(ctx, machine);
            var body = await TryRead<ApprovedPatchRequest>(ctx);
            if (body == null) return Error(ctx, 400, "validation_failed", "Body required.");
            if (body.Alias != null) engine.Store.SetAlias(canonical, body.Alias.Trim());
            if (body.IsPaw.HasValue) engine.Store.SetPaw(canonical, body.IsPaw.Value);
            return Results.NoContent();
        });

        app.MapDelete("/api/approved/{machine}", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var canonical = Canonical(engine, machine) ?? machine;
            // ForgetClient = Store.Forget + dispose the live connection if currently online.
            // Safe to call when offline (the TryRemove is a no-op then).
            engine.ForgetClient(canonical);
            sessions.ForgetMachineFromAllSessions(canonical);
            apiCtx.Log?.Add($"Web UI: delete approved {canonical}", Th.Yel);
            return Results.NoContent();
        });
    }

    static string? Canonical(ServerEngine engine, string machine)
        => engine.Store.All().FirstOrDefault(c => string.Equals(c.Name, machine, StringComparison.OrdinalIgnoreCase))?.Name;

    static ApprovedClientEntry Project(ApprovedClient c) => new()
    {
        Name        = c.Name,
        Alias       = c.Alias,
        Ip          = c.Ip,
        Mac         = c.Mac,
        IsPaw       = c.Paw,
        Revoked     = c.Revoked,
        ApprovedAt  = c.At,
        LastSeen    = c.Seen,
    };

    static IResult NotFound(HttpContext ctx, string machine)
        => Error(ctx, 404, "not_found", $"Machine '{machine}' is not known.");

    static IResult Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message }, JsonOpts);
    }

    static Task<T?> TryRead<T>(HttpContext ctx) where T : class => WebJson.TryRead<T>(ctx);
}

public sealed class ApprovedClientEntry
{
    public string   Name       { get; set; } = "";
    public string   Alias      { get; set; } = "";
    public string   Ip         { get; set; } = "";
    public string   Mac        { get; set; } = "";
    public bool     IsPaw      { get; set; }
    public bool     Revoked    { get; set; }
    public DateTime ApprovedAt { get; set; }
    public DateTime LastSeen   { get; set; }
}

public sealed class ApprovedPatchRequest
{
    public string? Alias { get; set; }
    public bool?   IsPaw { get; set; }
}
