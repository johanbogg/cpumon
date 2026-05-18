using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebDashboardApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           ServerEngine engine,
                           ServerDashboardController controller,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapGet("/api/state", (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            return Results.Json(controller.GetState(), JsonOpts);
        });

        app.MapPost("/api/state/select", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var body = await TryRead<SelectRequest>(ctx);
            if (body == null) return Error(ctx, 400, "validation_failed", "Body required.");
            controller.SetSelection(body.MachineNames ?? Array.Empty<string>());
            return Results.NoContent();
        });

        app.MapPost("/api/state/filter/os", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var body = await TryRead<FilterValueRequest>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Value))
                return Error(ctx, 400, "validation_failed", "Body { value } required.");
            try
            {
                var v = controller.SetOsFilter(body.Value);
                return Results.Json(new { value = v }, JsonOpts);
            }
            catch (ArgumentException ex) { return Error(ctx, 400, "validation_failed", ex.Message); }
        });

        app.MapPost("/api/state/filter/sort", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var body = await TryRead<FilterValueRequest>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Value))
                return Error(ctx, 400, "validation_failed", "Body { value } required.");
            try
            {
                var v = controller.SetSortMode(body.Value);
                return Results.Json(new { value = v }, JsonOpts);
            }
            catch (ArgumentException ex) { return Error(ctx, 400, "validation_failed", ex.Message); }
        });

        app.MapPost("/api/token/regenerate", (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            controller.RegenerateToken();
            var s = controller.GetState();
            apiCtx.Log?.Add("Web UI: token regenerated", Th.Yel);
            return Results.Json(new { token = s.Token, issuedAt = s.TokenIssuedAt }, JsonOpts);
        });

        app.MapPost("/api/pending/{machine}/approve", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!controller.ApprovePending(machine)) return Error(ctx, 404, "not_found", $"Pending client '{machine}' was not found.");
            apiCtx.Log?.Add($"Web UI: approved pending {machine}", Th.Grn);
            return Results.NoContent();
        });

        app.MapPost("/api/pending/{machine}/reject", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!controller.RejectPending(machine)) return Error(ctx, 404, "not_found", $"Pending client '{machine}' was not found.");
            apiCtx.Log?.Add($"Web UI: rejected pending {machine}", Th.Org);
            return Results.NoContent();
        });
    }

    static IResult Error(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return Results.Json(new { error = code, message }, JsonOpts);
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
}

public sealed class SelectRequest
{
    public string[]? MachineNames { get; set; }
}

public sealed class FilterValueRequest
{
    public string Value { get; set; } = "";
}
