using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebAlertsApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           AlertService alerts,
                           SessionStore sessions,
                           WebApiContext apiCtx)
    {
        app.MapGet("/api/alerts", (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;
            return Results.Json(alerts.Config, JsonOpts);
        });

        app.MapPut("/api/alerts", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var cfg = await TryRead<AlertConfig>(ctx);
            if (cfg == null) return Error(ctx, 400, "validation_failed", "AlertConfig body required.");
            if (cfg.SmtpPort < 0 || cfg.SmtpPort > 65535)
                return Error(ctx, 400, "validation_failed", "SmtpPort must be 0-65535.");
            if (cfg.CooldownMinutes < 0)
                return Error(ctx, 400, "validation_failed", "CooldownMinutes must be non-negative.");
            alerts.SaveConfig(cfg);
            apiCtx.Log?.Add("Web UI: alerts config updated", Th.Yel);
            return Results.NoContent();
        });

        app.MapPost("/api/alerts/test", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var cfg = alerts.Config;
            if (!cfg.EmailConfigured)
                return Results.Json(new { ok = false, message = "SMTP host / from / to are required." }, JsonOpts);
            try
            {
                await AlertService.SendAsync(cfg, "test", "This is a cpumon alert test.");
                return Results.Json(new { ok = true, message = "Test email sent." }, JsonOpts);
            }
            catch (Exception ex)
            {
                apiCtx.Log?.Add($"Web UI: alert test failed: {ex.Message}", Th.Red);
                return Results.Json(new { ok = false, message = ex.Message }, JsonOpts);
            }
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
