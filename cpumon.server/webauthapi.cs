using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebAuthApi
{
    const string CookieSess = "cpumon_sess";
    const string CookieCsrf = "cpumon_csrf";
    const string HeaderCsrf = "X-CSRF-Token";
    const string SessionItemKey = "cpumon.session";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder app,
                           OperatorStore operators,
                           SessionStore sessions,
                           BootstrapTokenIssuer bootstrap,
                           RateLimiter rateLimiter,
                           WebApiContext apiCtx)
    {
        app.MapPost("/api/auth/bootstrap", async (HttpContext ctx) =>
        {
            var ip = apiCtx.ClientIp(ctx);
            if (rateLimiter.IsBlocked(ip)) return RateLimited(ctx, rateLimiter);
            if (operators.Exists) return Error(ctx, 409, "bootstrap_disabled", "Operator account already exists.");
            var body = await TryRead<BootstrapRequest>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Username) ||
                string.IsNullOrWhiteSpace(body.Password) || string.IsNullOrWhiteSpace(body.BootstrapToken))
                return Error(ctx, 400, "validation_failed", "Missing required fields.");
            if (body.Password.Length < 12)
                return Error(ctx, 400, "validation_failed", "Password must be at least 12 characters.");
            if (!bootstrap.Consume(body.BootstrapToken))
            {
                rateLimiter.RecordFailure(ip);
                return Error(ctx, 401, "bootstrap_invalid", "Bootstrap token is invalid or expired.");
            }
            try { operators.Create(body.Username.Trim(), body.Password); }
            catch (ArgumentException ex) { return Error(ctx, 400, "validation_failed", ex.Message); }
            catch (InvalidOperationException) { return Error(ctx, 409, "bootstrap_disabled", "Operator account already exists."); }
            rateLimiter.Reset(ip);
            apiCtx.Log?.Add($"Web UI: operator created ({body.Username})", Th.Grn);
            IssueSessionCookies(ctx, sessions.Issue(body.Username, ip, ctx.Request.Headers.UserAgent.ToString()), apiCtx.UseTls);
            return Results.NoContent();
        });

        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            var ip = apiCtx.ClientIp(ctx);
            if (rateLimiter.IsBlocked(ip)) return RateLimited(ctx, rateLimiter);
            var body = await TryRead<LoginRequest>(ctx);
            if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
                return Error(ctx, 400, "validation_failed", "Username and password required.");
            if (!operators.Exists)
                return Error(ctx, 401, "auth_required", "No operator configured. Use the bootstrap setup URL.");
            if (!operators.Verify(body.Username, body.Password))
            {
                rateLimiter.RecordFailure(ip);
                apiCtx.Log?.Add($"Web UI: login failed ({body.Username}) from {ip}", Th.Org);
                return Error(ctx, 401, "invalid_credentials", "Username or password is incorrect.");
            }
            rateLimiter.Reset(ip);
            var session = sessions.Issue(body.Username, ip, ctx.Request.Headers.UserAgent.ToString());
            IssueSessionCookies(ctx, session, apiCtx.UseTls);
            apiCtx.Log?.Add($"Web UI: login ok ({body.Username}) from {ip}", Th.Grn);
            return Results.NoContent();
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            if (!TryAuthenticate(ctx, sessions, requireCsrf: true, out var session, out var failure))
                return failure!;
            sessions.Invalidate(session!.Id);
            ClearSessionCookies(ctx, apiCtx.UseTls);
            apiCtx.Log?.Add($"Web UI: logout ({session.Username})", Th.Dim);
            return Results.NoContent();
        });

        app.MapGet("/api/auth/whoami", (HttpContext ctx) =>
        {
            if (!TryAuthenticate(ctx, sessions, requireCsrf: false, out var session, out var failure))
                return failure!;
            return Results.Json(new { username = session!.Username, sessionCreatedAt = session.CreatedAt }, JsonOpts);
        });
    }

    /// <summary>Validates session cookie and CSRF token. Sets ctx.Items[SessionItemKey] on success.
    /// On failure, returns false and populates <paramref name="failure"/> with the IResult to return.</summary>
    public static bool TryAuthenticate(HttpContext ctx, SessionStore sessions, bool requireCsrf, out SessionState? session, out IResult? failure)
    {
        session = null; failure = null;
        var sid = ctx.Request.Cookies[CookieSess];
        if (string.IsNullOrEmpty(sid))
        {
            failure = Error(ctx, 401, "auth_required", "Sign in required.");
            return false;
        }
        var s = sessions.Validate(sid);
        if (s == null)
        {
            failure = Error(ctx, 401, "auth_required", "Session expired or invalid.");
            return false;
        }
        if (requireCsrf)
        {
            var header = ctx.Request.Headers[HeaderCsrf].ToString();
            var cookie = ctx.Request.Cookies[CookieCsrf] ?? "";
            if (string.IsNullOrEmpty(header) ||
                !ConstantTimeEquals(header, s.CsrfToken) ||
                !ConstantTimeEquals(cookie, s.CsrfToken))
            {
                failure = Error(ctx, 403, "csrf_failed", "CSRF token missing or mismatched.");
                return false;
            }
        }
        ctx.Items[SessionItemKey] = s;
        session = s;
        return true;
    }

    static void IssueSessionCookies(HttpContext ctx, SessionState s, bool useTls)
    {
        var common = new CookieOptions
        {
            Secure   = useTls,
            SameSite = SameSiteMode.Lax,
            Path     = "/",
            MaxAge   = TimeSpan.FromDays(30),
        };
        ctx.Response.Cookies.Append(CookieSess, s.Id, new CookieOptions
        {
            HttpOnly = true, Secure = common.Secure, SameSite = common.SameSite, Path = common.Path, MaxAge = common.MaxAge
        });
        ctx.Response.Cookies.Append(CookieCsrf, s.CsrfToken, new CookieOptions
        {
            HttpOnly = false, Secure = common.Secure, SameSite = common.SameSite, Path = common.Path, MaxAge = common.MaxAge
        });
    }

    static void ClearSessionCookies(HttpContext ctx, bool useTls)
    {
        var opts = new CookieOptions { Secure = useTls, SameSite = SameSiteMode.Lax, Path = "/" };
        ctx.Response.Cookies.Delete(CookieSess, opts);
        ctx.Response.Cookies.Delete(CookieCsrf, opts);
    }

    static IResult RateLimited(HttpContext ctx, RateLimiter rl)
    {
        ctx.Response.Headers.RetryAfter = ((int)rl.RetryAfter.TotalSeconds).ToString();
        return Error(ctx, 429, "rate_limited", "Too many failed attempts. Try again later.");
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
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, JsonOpts);
        }
        catch { return null; }
    }

    static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}

public sealed class BootstrapRequest
{
    public string Username       { get; set; } = "";
    public string Password       { get; set; } = "";
    public string BootstrapToken { get; set; } = "";
}

public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
