using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

// Centralized cap-aware JSON body reader for action endpoints.
// File uploads go through their own streaming code path and do not use this.
internal static class WebJson
{
    public const long DefaultMaxBytes = 256 * 1024;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<T?> TryRead<T>(HttpContext ctx, long maxBytes = DefaultMaxBytes) where T : class
    {
        try
        {
            if (ctx.Request.ContentLength is long len && len > maxBytes) return null;
            var body = ctx.Request.Body;
            var ms = new MemoryStream();
            var buf = new byte[8192];
            int n;
            while ((n = await body.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > maxBytes) return null;
                ms.Write(buf, 0, n);
            }
            if (ms.Length == 0) return null;
            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, JsonOpts);
        }
        catch { return null; }
    }
}
