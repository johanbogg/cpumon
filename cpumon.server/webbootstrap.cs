using System;

public static class WebBootstrap
{
    // Returns true if a bootstrap token was issued (and surfaced via platform). False when the
    // operator account already exists, so the caller can skip to the normal login flow.
    public static bool MaybeIssueAndShow(OperatorStore operators,
                                         BootstrapTokenIssuer issuer,
                                         IServerPlatformServices platform,
                                         Func<string, string> buildUrl,
                                         CLog? log = null)
    {
        if (operators.Exists) return false;
        var (token, expiresAt) = issuer.Issue();
        var url = buildUrl(token);
        platform.ShowBootstrapUrl(url, expiresAt);
        log?.Add($"Web UI: bootstrap token issued (expires {expiresAt.ToLocalTime():HH:mm:ss})", Th.Yel);
        return true;
    }
}
