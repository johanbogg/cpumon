using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

public static class ReleaseStager
{
    const int KeepRecentReleases = 2;
    static readonly HttpClient _http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"cpumon-server/{Proto.AppVersion}");
        return c;
    }

    public static string ReleasesDir => Path.Combine(AppPaths.DataDir, "releases");

    public static string StagedDirFor(string tagName) => Path.Combine(ReleasesDir, tagName);

    public static bool IsStaged(string tagName) =>
        File.Exists(Path.Combine(StagedDirFor(tagName), "stage.ok"));

    public static async Task<string?> StageAsync(ReleaseInfo info, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ReleasesDir);
            string targetDir = StagedDirFor(info.TagName);
            string marker = Path.Combine(targetDir, "stage.ok");
            if (File.Exists(marker))
            {
                LogSink.Debug("ReleaseStager", $"Already staged: {info.TagName}");
                return targetDir;
            }

            string tempDir = targetDir + ".tmp";
            if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch { } }
            Directory.CreateDirectory(tempDir);

            if (string.IsNullOrEmpty(info.ChecksumsUrl))
                throw new InvalidDataException($"Release {info.TagName} has no SHA256SUMS asset; refusing to stage unsigned assets");

            string sumsTxt;
            try
            {
                sumsTxt = await _http.GetStringAsync(info.ChecksumsUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"SHA256SUMS fetch failed for {info.TagName}: {ex.Message}", ex);
            }

            var sums = ParseSums(sumsTxt);
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"SHA256SUMS-{info.Version}.txt"), sumsTxt, ct).ConfigureAwait(false);

            var assets = new List<(string Url, string ZipName, string SubDir)>();
            if (info.ClientAssetUrl != null) assets.Add((info.ClientAssetUrl, $"cpumon-client-{info.Version}.zip", "client"));
            if (info.ServerAssetUrl != null) assets.Add((info.ServerAssetUrl, $"cpumon-server-{info.Version}.zip", "server"));
            if (info.LinuxAssetUrl  != null) assets.Add((info.LinuxAssetUrl,  $"cpumon-linux-{info.Version}.zip",  "linux"));

            if (assets.Count == 0)
            {
                LogSink.Warn("ReleaseStager", $"No staged-eligible assets for {info.TagName}");
                try { Directory.Delete(tempDir, true); } catch { }
                return null;
            }

            foreach (var (url, zipName, subDir) in assets)
            {
                if (!sums.TryGetValue(zipName, out var expected))
                    throw new InvalidDataException($"SHA256SUMS has no entry for {zipName}; refusing to stage {info.TagName}");

                string zipPath = Path.Combine(tempDir, zipName);
                using (var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false))
                using (var fs = File.Create(zipPath))
                    await stream.CopyToAsync(fs, ct).ConfigureAwait(false);

                string actual = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA256 mismatch for {zipName}");

                ZipFile.ExtractToDirectory(zipPath, Path.Combine(tempDir, subDir));
            }

            if (!string.IsNullOrEmpty(info.Notes))
            {
                try { await File.WriteAllTextAsync(Path.Combine(tempDir, "release-notes.md"), info.Notes, ct).ConfigureAwait(false); }
                catch { }
            }

            if (Directory.Exists(targetDir)) { try { Directory.Delete(targetDir, true); } catch { } }
            Directory.Move(tempDir, targetDir);
            await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);

            PruneOldReleases(targetDir);
            return targetDir;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LogSink.Warn("ReleaseStager", $"Stage failed for {info.TagName}: {ex.Message}");
            return null;
        }
    }

    static void PruneOldReleases(string keepDir)
    {
        try
        {
            var keepFull = Path.GetFullPath(keepDir);
            var staged = Directory.GetDirectories(ReleasesDir)
                .Where(d => Path.GetFileName(d).StartsWith('v') || Path.GetFileName(d).StartsWith('V'))
                .Select(d => (Dir: d, Ver: TryParseVer(Path.GetFileName(d))))
                .Where(t => t.Ver != null)
                .OrderByDescending(t => t.Ver!)
                .Select(t => t.Dir)
                .ToList();
            foreach (var old in staged.Skip(KeepRecentReleases))
            {
                if (string.Equals(Path.GetFullPath(old), keepFull, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(old, true); } catch { }
            }
            foreach (var t in Directory.GetDirectories(ReleasesDir, "*.tmp"))
            {
                try { Directory.Delete(t, true); } catch { }
            }
        }
        catch { }
    }

    static Version? TryParseVer(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;

    static Dictionary<string, string> ParseSums(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0) continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0 || sp >= line.Length - 1) continue;
            string hash = line[..sp].Trim().ToLowerInvariant();
            string name = line[(sp + 1)..].TrimStart(' ', '*');
            if (hash.Length == 64 && !string.IsNullOrEmpty(name))
                d[name] = hash;
        }
        return d;
    }

    static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
