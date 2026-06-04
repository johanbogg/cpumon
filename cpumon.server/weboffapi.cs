using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebOfflineApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    static readonly Regex MacPattern = new(@"^[0-9A-Fa-f]{2}([:-]?[0-9A-Fa-f]{2}){5}$", RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapPost("/api/offline/{machine}/wake", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var canonical = Canonical(engine, machine);
            if (canonical == null) return NotFound(ctx, machine);
            if (!engine.WakeOffline(canonical))
                return Error(ctx, 409, "conflict", $"Machine '{canonical}' has no MAC stored or wake failed.");
            return Results.NoContent();
        });

        app.MapPost("/api/offline/{machine}/mac", async (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var canonical = Canonical(engine, machine);
            if (canonical == null) return NotFound(ctx, machine);
            var body = await TryRead<MacRequest>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Mac))
                return Error(ctx, 400, "validation_failed", "Body { mac } required.");
            var mac = body.Mac.Trim();
            if (!MacPattern.IsMatch(mac))
                return Error(ctx, 400, "validation_failed", "MAC must be 12 hex digits, optional ':' or '-' separators.");
            engine.SetMacForOffline(canonical, mac);
            return Results.NoContent();
        });

        app.MapPost("/api/offline/{machine}/forget", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var canonical = Canonical(engine, machine);
            if (canonical == null) return NotFound(ctx, machine);
            engine.Store.Forget(canonical);
            sessions.ForgetMachineFromAllSessions(canonical);
            apiCtx.Log?.Add($"Web UI: forget offline {canonical}", Th.Yel);
            return Results.NoContent();
        });
    }

    static string? Canonical(ServerEngine engine, string machine)
        => engine.Store.All().FirstOrDefault(c => string.Equals(c.Name, machine, StringComparison.OrdinalIgnoreCase))?.Name;

    static IResult NotFound(HttpContext ctx, string machine)
        => Error(ctx, 404, "not_found", $"Machine '{machine}' is not known.");

    static IResult Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message }, JsonOpts);
    }

    static Task<T?> TryRead<T>(HttpContext ctx) where T : class => WebJson.TryRead<T>(ctx);
}

public sealed class MacRequest
{
    public string Mac { get; set; } = "";
}
