using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebClientActionsApi
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
        app.MapPost("/api/clients/{machine}/restart", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!engine.RequestRestart(machine)) return NotFound(ctx, machine);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/shutdown", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!engine.RequestShutdown(machine)) return NotFound(ctx, machine);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/forget", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out var session, out var fail)) return fail!;
            if (!engine.Clients.ContainsKey(machine)) return NotFound(ctx, machine);
            engine.ForgetClient(machine);
            WebSessionDashboard.RemoveMachine(session!, machine);
            apiCtx.Log?.Add($"Web UI: forget {machine}", Th.Yel);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/paw", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!engine.TogglePaw(machine)) return NotFound(ctx, machine);
            return Results.Json(new { isPaw = engine.Store.IsPaw(machine) }, JsonOpts);
        });

        app.MapPost("/api/clients/{machine}/message", async (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var body = await TryRead<MessageRequest>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Text))
                return Error(ctx, 400, "validation_failed", "Body { text } required.");
            if (body.Text.Length > 500)
                return Error(ctx, 400, "validation_failed", "Message must be 1-500 characters.");
            if (!engine.SendUserMessage(machine, body.Text)) return NotFound(ctx, machine);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/screenshot", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!engine.RequestScreenshot(machine, notifyUi: false)) return NotFound(ctx, machine);
            return Results.NoContent();
        });

        app.MapPost("/api/clients/{machine}/expand", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out var session, out var fail)) return fail!;
            if (!engine.Clients.ContainsKey(machine)) return NotFound(ctx, machine);
            var expanded = WebSessionDashboard.ToggleExpanded(session!, machine);
            return Results.Json(new { expanded }, JsonOpts);
        });
    }

    static IResult NotFound(HttpContext ctx, string machine)
        => Error(ctx, 404, "not_found", $"Machine '{machine}' is not connected.");

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

public sealed class MessageRequest
{
    public string Text { get; set; } = "";
}
