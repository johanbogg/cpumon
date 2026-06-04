using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

public static class LinuxUpdatePayload
{
    public const long MaxPayloadBytes = 50L * 1024 * 1024;

    public static bool TryRead(string path, out string fileName, out byte[] bytes, out string error)
    {
        fileName = "cpumon.py";
        bytes = Array.Empty<byte>();
        error = "";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "selected file does not exist"; return false; }
            if (info.Length > MaxPayloadBytes) { error = $"selected file exceeds {MaxPayloadBytes / (1024 * 1024)} MB cap"; return false; }

            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.Entries.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Name) &&
                    string.Equals(e.Name, "cpumon.py", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    error = "release zip did not contain cpumon.py";
                    return false;
                }
                if (entry.Length > MaxPayloadBytes) { error = $"cpumon.py inside zip exceeds {MaxPayloadBytes / (1024 * 1024)} MB cap"; return false; }
                using var s = entry.Open();
                using var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
                var buf = new byte[64 * 1024];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > MaxPayloadBytes) { error = "cpumon.py inside zip expanded beyond cap"; return false; }
                    ms.Write(buf, 0, n);
                }
                bytes = ms.ToArray();
            }
            else
            {
                bytes = File.ReadAllBytes(path);
            }

            if (bytes.Length == 0)
            {
                error = "selected Linux update file is empty";
                return false;
            }

            var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (!head.Contains("VERSION", StringComparison.Ordinal) ||
                !head.Contains("cpumon", StringComparison.OrdinalIgnoreCase))
            {
                error = "selected file does not look like cpumon.py";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
