using System;
using System.Collections.Generic;
using System.Linq;

public sealed record ServerDashboardState(
    string Token,
    DateTime TokenIssuedAt,
    bool BroadcastDisabled,
    int ConnectionCount,
    int AuthenticatedClientCount,
    ReleaseInfo? AvailableUpdate,
    string? StagedReleaseDir,
    IReadOnlyList<ClientCardState> Clients,
    IReadOnlyList<PendingApprovalState> PendingApprovals,
    IReadOnlyList<OfflineClientState> OfflineClients,
    IReadOnlySet<string> SelectedMachineNames,
    string OsFilter,
    string SortMode,
    IReadOnlyList<DashboardLogEntryState> LogEntries,
    bool AlertsConfigured
);

public sealed record ClientCardState(
    string MachineName,
    string DisplayName,
    string Alias,
    bool IsExpanded,
    bool IsStale,
    bool IsWaitingForFirstReport,
    bool IsLinux,
    string OsLabel,
    string ClientVersion,
    bool IsOutdated,
    bool IsPaw,
    MachineReport? Report,
    bool CanRdp,
    bool CanTerminal,
    bool CanServices,
    bool CanScreenshot,
    string SendMode
);

public sealed record PendingApprovalState(
    string MachineName,
    string Ip,
    string Remote,
    DateTime RequestedAt,
    string ClientVersion
);

public sealed record OfflineClientState(
    string MachineName,
    string DisplayName,
    string Alias,
    DateTime Seen,
    string Ip,
    string Mac,
    bool IsPaw
);

public sealed record DashboardLogEntryState(
    DateTime Time,
    string Message,
    int ColorArgb
);

public sealed class ServerDashboardStateBuilder
{
    readonly ServerEngine _engine;

    public ServerDashboardStateBuilder(ServerEngine engine) => _engine = engine;

    public ServerDashboardState Build(IEnumerable<string> selectedMachineNames, string osFilter, string sortMode, int maxLogEntries = 50)
    {
        var selected = selectedMachineNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clients = VisibleClients(osFilter, sortMode)
            .Select(cl => BuildClient(cl, selected))
            .ToList();
        var pendingApprovals = _engine.PendingApprovals.Values
            .OrderBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PendingApprovalState(p.MachineName, p.Ip, p.Remote, p.RequestedAt, p.ClientVersion))
            .ToList();
        var offlineClients = osFilter == "all"
            ? _engine.Store.All()
                .Where(a => !a.Revoked && !_engine.Clients.ContainsKey(a.Name))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new OfflineClientState(
                    a.Name,
                    string.IsNullOrEmpty(a.Alias) ? a.Name : a.Alias,
                    a.Alias,
                    a.Seen,
                    a.Ip,
                    a.Mac,
                    a.Paw))
                .ToList()
            : new List<OfflineClientState>();
        var logEntries = _engine.Log.Recent(maxLogEntries)
            .Select(e => new DashboardLogEntryState(e.T, e.M, e.C.ToArgb()))
            .ToList();

        return new ServerDashboardState(
            _engine.Token,
            _engine.TokenIssuedAt,
            _engine.BroadcastDisabled,
            _engine.ConnectionCount,
            _engine.Clients.Count,
            _engine.AvailableUpdate,
            _engine.StagedReleaseDir,
            clients,
            pendingApprovals,
            offlineClients,
            selected,
            osFilter,
            sortMode,
            logEntries,
            _engine.Alerts.ThresholdsConfigured);
    }

    List<RemoteClient> VisibleClients(string osFilter, string sortMode)
    {
        IEnumerable<RemoteClient> clients = _engine.Clients.Values;
        clients = osFilter switch
        {
            "windows" => clients.Where(cl => cl.LastReport == null || !ServerEngine.IsLinuxClient(cl)),
            "linux" => clients.Where(cl => cl.LastReport == null || ServerEngine.IsLinuxClient(cl)),
            _ => clients
        };
        clients = sortMode == "os"
            ? clients.OrderBy(OsSortKey).ThenBy(cl => cl.MachineName, StringComparer.OrdinalIgnoreCase)
            : clients.OrderBy(cl => cl.MachineName, StringComparer.OrdinalIgnoreCase);
        return clients.ToList();
    }

    ClientCardState BuildClient(RemoteClient cl, IReadOnlySet<string> selected)
    {
        var report = CloneReport(cl.LastReport);
        string alias = _engine.Store.GetAlias(cl.MachineName);
        bool isLinux = ServerEngine.IsLinuxClient(cl);
        bool isOutdated = ServerEngine.ClientNeedsUpdate(cl.ClientVersion);
        string machineName = cl.MachineName;
        return new ClientCardState(
            machineName,
            string.IsNullOrEmpty(alias) ? machineName : alias,
            alias,
            cl.Expanded,
            (DateTime.UtcNow - cl.LastSeen).TotalSeconds > 70,
            cl.LastReport == null,
            isLinux,
            report == null ? "Unknown" : ShortOsLabel(report),
            cl.ClientVersion,
            isOutdated,
            _engine.Store.IsPaw(machineName),
            report,
            !isLinux,
            true,
            true,
            !isLinux,
            cl.SendMode);
    }

    static string OsSortKey(RemoteClient cl) => ServerEngine.IsLinuxClient(cl) ? "2-linux" : "1-windows";

    static MachineReport? CloneReport(MachineReport? r)
    {
        if (r == null) return null;
        return new MachineReport
        {
            MachineName = r.MachineName,
            OsVersion = r.OsVersion,
            CpuName = r.CpuName,
            CoreCount = r.CoreCount,
            PackageTemperatureC = r.PackageTemperatureC,
            PackageFrequencyMHz = r.PackageFrequencyMHz,
            TotalLoadPercent = r.TotalLoadPercent,
            PackagePowerW = r.PackagePowerW,
            Cores = r.Cores.Select(c => new CoreReport
            {
                Index = c.Index,
                FrequencyMHz = c.FrequencyMHz,
                TemperatureC = c.TemperatureC,
                LoadPercent = c.LoadPercent
            }).ToList(),
            RamTotalGB = r.RamTotalGB,
            RamUsedGB = r.RamUsedGB,
            Drives = r.Drives.Select(d => new DriveStat
            {
                Name = d.Name,
                FreeGB = d.FreeGB,
                TotalGB = d.TotalGB
            }).ToList(),
            TimestampUtcMs = r.TimestampUtcMs,
            GpuLoadPercent = r.GpuLoadPercent,
            GpuTemperatureC = r.GpuTemperatureC,
            GpuVramUsedMB = r.GpuVramUsedMB,
            GpuVramTotalMB = r.GpuVramTotalMB,
            NetUpKBps = r.NetUpKBps,
            NetDownKBps = r.NetDownKBps
        };
    }

    static string ShortOsLabel(MachineReport r)
    {
        string os = r.OsVersion ?? "";
        if (os.Contains("linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        if (os.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            string clean = os.Replace("Microsoft", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Windows NT", "Windows", StringComparison.OrdinalIgnoreCase)
                .Replace("  ", " ")
                .Trim();
            return clean.Length > 24 ? clean[..24].TrimEnd() : clean;
        }
        return string.IsNullOrWhiteSpace(os) ? "Unknown" : (os.Length > 24 ? os[..24].TrimEnd() : os);
    }
}
