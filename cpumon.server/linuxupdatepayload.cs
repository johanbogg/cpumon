using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

public static class LinuxUpdatePayload
{
    public static bool TryRead(string path, out string fileName, out byte[] bytes, out string error)
    {
        fileName = "cpumon.py";
        bytes = Array.Empty<byte>();
        error = "";
        try
        {
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
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
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
