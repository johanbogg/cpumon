using System;
using System.Text;

public static class Versioning
{
    public static bool TryNormalize(string? value, out Version version, out string text)
    {
        version = new Version(0, 0, 0);
        text = "";
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

        var numeric = new StringBuilder();
        foreach (char ch in s)
        {
            if (char.IsDigit(ch) || ch == '.') numeric.Append(ch);
            else break;
        }

        var parts = numeric.ToString().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        int[] nums = { 0, 0, 0 };
        for (int i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!int.TryParse(parts[i], out nums[i])) return false;
        }

        version = new Version(nums[0], nums[1], nums[2]);
        text = $"{nums[0]}.{nums[1]}.{nums[2]}";
        return true;
    }

    public static bool IsOlder(string? candidate, string? current)
    {
        if (!TryNormalize(candidate, out var candidateVersion, out _)) return false;
        if (!TryNormalize(current, out var currentVersion, out _)) return false;
        return candidateVersion < currentVersion;
    }

    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryNormalize(candidate, out var candidateVersion, out _)) return false;
        if (!TryNormalize(current, out var currentVersion, out _)) return false;
        return candidateVersion > currentVersion;
    }
}
