using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebLogApi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public const int DefaultLimit = 200;
    public const int MaxLimit     = 500;
    public static readonly TimeSpan DefaultSinceWindow = TimeSpan.FromMinutes(10);

    public static void Map(IEndpointRouteBuilder app, ServerEngine engine, SessionStore sessions, WebApiContext apiCtx)
    {
        app.MapGet("/api/log", (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: false, out _, out var fail)) return fail!;

            DateTime sinceUtc = ParseSince(ctx.Request.Query["since"]) ?? DateTime.UtcNow - DefaultSinceWindow;
            int limit = ParseLimit(ctx.Request.Query["limit"]);

            var entries = engine.Log.Recent(MaxLimit)
                .Select(e => (Ts: e.T.ToUniversalTime(), Message: e.M, Color: ColorHex(e.C)))
                .Where(e => e.Ts >= sinceUtc)
                .TakeLast(limit)
                .Select(e => new LogEntryDto { Ts = e.Ts, Message = e.Message, Color = e.Color })
                .ToList();

            return Results.Json(entries, JsonOpts);
        });
    }

    static DateTime? ParseSince(Microsoft.Extensions.Primitives.StringValues raw)
    {
        var s = raw.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return null;
    }

    static int ParseLimit(Microsoft.Extensions.Primitives.StringValues raw)
    {
        if (!int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return DefaultLimit;
        return Math.Clamp(n, 1, MaxLimit);
    }

    static string ColorHex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

public sealed class LogEntryDto
{
    public DateTime Ts      { get; set; }
    public string   Message { get; set; } = "";
    public string   Color   { get; set; } = "";
}
