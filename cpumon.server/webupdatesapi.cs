using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class WebUpdatesApi
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
        app.MapPost("/api/clients/{machine}/update", (HttpContext ctx, string machine) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            if (!engine.Clients.TryGetValue(machine, out var cl)) return NotFound(ctx, machine);

            var stagedDir = engine.StagedReleaseDir;
            if (string.IsNullOrEmpty(stagedDir)) return Error(ctx, 409, "no_staged_release", "No release is staged on the server.");

            bool isLinux = ServerEngine.IsLinuxClient(cl);
            string artifact = ArtifactPath(stagedDir, isLinux);
            if (!File.Exists(artifact))
                return Error(ctx, 409, "artifact_missing", $"Staged release does not contain {Path.GetFileName(artifact)}.");

            if (isLinux)
            {
                engine.Log.Add($"Web UI: pushing Linux update → {cl.MachineName}…", Th.Org);
                engine.PushLinuxUpdate(cl, artifact);
            }
            else
            {
                engine.Log.Add($"Web UI: pushing update → {cl.MachineName}…", Th.Org);
                engine.PushUpdate(cl, artifact);
            }
            return Results.NoContent();
        });

        app.MapPost("/api/updates/push", async (HttpContext ctx) =>
        {
            if (!WebAuthApi.TryAuthenticate(ctx, sessions, requireCsrf: true, out _, out var fail)) return fail!;
            var body = await TryRead<PushUpdateRequest>(ctx);
            var names = body?.MachineNames?
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .ToList() ?? new List<string>();
            if (names.Count == 0) return Error(ctx, 400, "validation_failed", "Body { machineNames } required.");

            var stagedDir = engine.StagedReleaseDir;
            if (string.IsNullOrEmpty(stagedDir)) return Error(ctx, 409, "no_staged_release", "No release is staged on the server.");

            var winClients   = new List<RemoteClient>();
            var linuxClients = new List<RemoteClient>();
            int skipped = 0;
            foreach (var name in names)
            {
                if (!engine.Clients.TryGetValue(name, out var cl)) { skipped++; continue; }
                if (ServerEngine.IsLinuxClient(cl)) linuxClients.Add(cl);
                else                                winClients.Add(cl);
            }
            if (winClients.Count == 0 && linuxClients.Count == 0)
                return Error(ctx, 404, "no_targets", "None of the requested machines are connected.");

            string winPath   = ArtifactPath(stagedDir, isLinux: false);
            string linuxPath = ArtifactPath(stagedDir, isLinux: true);
            bool winReady    = File.Exists(winPath);
            bool linuxReady  = File.Exists(linuxPath);

            int pushedWindows = 0, pushedLinux = 0, missingArtifact = 0;

            if (winClients.Count > 0)
            {
                if (winReady)
                {
                    engine.Log.Add($"Web UI: pushing update → {winClients.Count} Windows client(s)…", Th.Org);
                    engine.PushUpdateMulti(winClients, winPath);
                    pushedWindows = winClients.Count;
                }
                else missingArtifact += winClients.Count;
            }
            if (linuxClients.Count > 0)
            {
                if (linuxReady)
                {
                    engine.Log.Add($"Web UI: pushing Linux update → {linuxClients.Count} client(s)…", Th.Org);
                    engine.PushLinuxUpdateMulti(linuxClients, linuxPath);
                    pushedLinux = linuxClients.Count;
                }
                else missingArtifact += linuxClients.Count;
            }

            if (pushedWindows == 0 && pushedLinux == 0)
                return Error(ctx, 409, "artifact_missing", "Staged release has no artifact for the requested platforms.");

            return Results.Json(new
            {
                windows = pushedWindows,
                linux   = pushedLinux,
                skipped,
                missingArtifact,
            }, JsonOpts);
        });
    }

    static string ArtifactPath(string stagedDir, bool isLinux)
        => isLinux ? Path.Combine(stagedDir, "linux",  "cpumon.py")
                   : Path.Combine(stagedDir, "client", "cpumon.client.exe");

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

public sealed class PushUpdateRequest
{
    public string[]? MachineNames { get; set; }
}
