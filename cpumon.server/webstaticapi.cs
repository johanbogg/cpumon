using System;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebStaticApi
{
    const string Prefix = "cpumon.server.web.";

    public static void Map(IEndpointRouteBuilder app, SessionStore sessions)
    {
        app.MapGet("/", (HttpContext ctx) =>
        {
            if (!HasSession(ctx, sessions))
                return Results.Redirect("/login");
            return Asset("index.html", "text/html; charset=utf-8");
        });

        app.MapGet("/login", (HttpContext ctx) =>
        {
            if (HasSession(ctx, sessions))
                return Results.Redirect("/");
            return Asset("login.html", "text/html; charset=utf-8");
        });

        app.MapGet("/web/app.css", () => Asset("app.css", "text/css; charset=utf-8"));
        app.MapGet("/web/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
    }

    static IResult Asset(string name, string contentType)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Prefix + name);
        if (stream == null) return Results.NotFound();
        using var reader = new StreamReader(stream);
        return Results.Content(reader.ReadToEnd(), contentType);
    }

    static bool HasSession(HttpContext ctx, SessionStore sessions)
    {
        var sid = ctx.Request.Cookies["cpumon_sess"];
        return !string.IsNullOrEmpty(sid) && sessions.Validate(sid) != null;
    }
}
