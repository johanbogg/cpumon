using System;

// Headless / non-WinForms IServerPlatformServices stub. Only ShowBootstrapUrl does anything
// real (stdout + stderr) — every other method either no-ops or throws, because no web flow
// in Phase 1 actually composes through the platform-services facade (POST = consent on the
// wire, dialog content arrives via REST snapshots). The future Linux daemon (Phase 9) will
// use this directly; in --web mode alongside the WinForms UI, ServerForm's
// WinFormsServerPlatformServices stays authoritative.
public sealed class WebPlatformServices : IServerPlatformServices
{
    public void SetClipboardText(string text) { }
    public bool Confirm(string message, string title, DashboardConfirmKind kind)
        => throw new InvalidOperationException("Confirm is not available in headless web context.");
    public string? Prompt(string title, string label)
        => throw new InvalidOperationException("Prompt is not available in headless web context.");
    public string? PickFile(string title, string filter)
        => throw new InvalidOperationException("PickFile is not available in headless web context.");
    public void OpenExternal(string target) { }
    public void ShowApprovedClients() { }
    public void ShowAlerts() { }
    public void ShowProcessDialog(string machineName) { }
    public void UpdateProcessDialog(RemoteClient cl) { }
    public void ShowSysInfoDialog(RemoteClient cl) { }
    public void ShowServicesDialog(RemoteClient cl) { }
    public void ShowEventsDialog(RemoteClient cl) { }
    public void ShowCpuDetailDialog(string machineName, CpuDetailReport detail) { }
    public void ShowScreenshotDialog(string machineName, ScreenshotData shot) { }
    public void ShowHealthDialog(string machineName) { }
    public void ShowTerminal(string machineName, string shell) { }
    public void ShowFileBrowser(string machineName, string? initialPath) { }
    public void ShowRdp(string machineName) { }
    public string? PromptUserMessage(string machineName)
        => throw new InvalidOperationException("PromptUserMessage is not available in headless web context.");
    public void ShowBootstrapUrl(string url, DateTime expiresAt)
    {
        var localExpiry = expiresAt.ToLocalTime().ToString("HH:mm:ss");
        Console.Out.WriteLine($"* Web UI setup: {url} (valid until {localExpiry})");
        Console.Error.WriteLine($"* Web UI setup: {url} (valid until {localExpiry})");
    }
}
