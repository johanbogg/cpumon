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
            return Results.Json(View(alerts.Config), JsonOpts);
        });

        app.MapPut("/api/alerts", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var body = await TryRead<AlertConfigUpdateRequest>(ctx);
            if (body == null) return Error(ctx, 400, "validation_failed", "AlertConfig body required.");
            var port = body.Port ?? 587;
            if (port < 0 || port > 65535)
                return Error(ctx, 400, "validation_failed", "Port must be 0-65535.");
            var cooldown = body.Cooldown ?? 30;
            if (cooldown < 0)
                return Error(ctx, 400, "validation_failed", "Cooldown must be non-negative.");
            if (body.Ram is < 0 or > 100)
                return Error(ctx, 400, "validation_failed", "Ram threshold must be 0-100.");
            if (body.Disk is < 0 or > 100)
                return Error(ctx, 400, "validation_failed", "Disk threshold must be 0-100.");
            var merged = Merge(alerts.Config, body, port, cooldown);
            alerts.SaveConfig(merged);
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

    static AlertConfigView View(AlertConfig cfg) => new()
    {
        Host        = cfg.SmtpHost,
        Port        = cfg.SmtpPort,
        Security    = cfg.Security,
        From        = cfg.FromAddress,
        To          = cfg.ToAddress,
        Username    = cfg.Username,
        PasswordSet = !string.IsNullOrEmpty(cfg.EncryptedPassword),
        Ram         = cfg.AlertRamPct,
        Disk        = cfg.AlertDiskPct,
        Temp        = cfg.AlertTempC,
        Cooldown    = cfg.CooldownMinutes,
    };

    static AlertConfig Merge(AlertConfig existing, AlertConfigUpdateRequest body, int port, int cooldown)
    {
        string? encryptedPassword;
        if (body.ClearPassword == true) encryptedPassword = null;
        else if (!string.IsNullOrEmpty(body.Password)) encryptedPassword = AlertConfigStore.Encrypt(body.Password);
        else encryptedPassword = existing.EncryptedPassword;

        return new AlertConfig
        {
            SmtpHost          = body.Host,
            SmtpPort          = port,
            Security          = body.Security ?? existing.Security,
            FromAddress       = body.From,
            ToAddress         = body.To,
            Username          = body.Username,
            EncryptedPassword = encryptedPassword,
            AlertRamPct       = body.Ram,
            AlertDiskPct      = body.Disk,
            AlertTempC        = body.Temp,
            CooldownMinutes   = cooldown,
        };
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

public sealed class AlertConfigView
{
    public string?       Host        { get; set; }
    public int           Port        { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmailSecurity Security    { get; set; }
    public string?       From        { get; set; }
    public string?       To          { get; set; }
    public string?       Username    { get; set; }
    public bool          PasswordSet { get; set; }
    public int?          Ram         { get; set; }
    public int?          Disk        { get; set; }
    public float?        Temp        { get; set; }
    public int           Cooldown    { get; set; }
}

public sealed class AlertConfigUpdateRequest
{
    public string?        Host          { get; set; }
    public int?           Port          { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmailSecurity? Security      { get; set; }
    public string?        From          { get; set; }
    public string?        To            { get; set; }
    public string?        Username      { get; set; }
    public string?        Password      { get; set; }   // plaintext; encrypted server-side
    public bool?          ClearPassword { get; set; }   // explicit wipe
    public int?           Ram           { get; set; }
    public int?           Disk          { get; set; }
    public float?         Temp          { get; set; }
    public int?           Cooldown      { get; set; }
}
