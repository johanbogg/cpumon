using System;
using System.Collections.Concurrent;

public enum SnapshotKind { Processes, SysInfo, Services, Events, CpuDetail, Screenshot }

public sealed class SnapshotCache : IDisposable
{
    readonly ServerEngine _engine;
    readonly ConcurrentDictionary<(string, SnapshotKind), DateTime> _received = new();
    readonly ConcurrentDictionary<(string, SnapshotKind), DateTime> _triggered = new();
    readonly ConcurrentDictionary<string, CpuDetailReport> _cpuDetail = new();
    readonly ConcurrentDictionary<string, ScreenshotData> _screenshots = new();

    public static readonly TimeSpan TtlProcesses = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TtlSysInfo   = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TtlServices  = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TtlEvents    = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TtlCpuDetail = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TtlScreenshot = TimeSpan.FromSeconds(30);
    // SPA polls ~1Hz while waiting for data; without a throttle each empty/stale
    // poll would re-issue the agent request and saturate slow/offline clients.
    public static readonly TimeSpan TriggerThrottle = TimeSpan.FromSeconds(1);

    public SnapshotCache(ServerEngine engine)
    {
        _engine = engine;
        _engine.ProcessListUpdated += OnProcessList;
        _engine.SysInfoUpdated     += OnSysInfo;
        _engine.ServicesUpdated    += OnServices;
        _engine.EventsUpdated      += OnEvents;
        _engine.CpuDetailUpdated   += OnCpuDetail;
        _engine.ScreenshotUpdated  += OnScreenshot;
    }

    public DateTime? ReceivedAt(string machine, SnapshotKind kind)
        => _received.TryGetValue((machine, kind), out var t) ? t : null;

    public DateTime? TriggeredAt(string machine, SnapshotKind kind)
        => _triggered.TryGetValue((machine, kind), out var t) ? t : null;

    public CpuDetailReport? GetCpuDetail(string machine)
        => _cpuDetail.TryGetValue(machine, out var d) ? d : null;

    public ScreenshotData? GetScreenshot(string machine)
        => _screenshots.TryGetValue(machine, out var s) ? s : null;

    public static TimeSpan TtlFor(SnapshotKind kind) => kind switch
    {
        SnapshotKind.Processes => TtlProcesses,
        SnapshotKind.SysInfo   => TtlSysInfo,
        SnapshotKind.Services  => TtlServices,
        SnapshotKind.Events    => TtlEvents,
        SnapshotKind.CpuDetail => TtlCpuDetail,
        SnapshotKind.Screenshot => TtlScreenshot,
        _ => TimeSpan.FromSeconds(5),
    };

    public bool IsStale(string machine, SnapshotKind kind)
    {
        var received = ReceivedAt(machine, kind);
        if (received == null) return true;
        return (DateTime.UtcNow - received.Value) >= TtlFor(kind);
    }

    public bool TryTriggerFetch(string machine, SnapshotKind kind)
    {
        if (!_engine.Clients.TryGetValue(machine, out _)) return false;
        var last = TriggeredAt(machine, kind);
        if (last != null && (DateTime.UtcNow - last.Value) < TriggerThrottle) return false;
        _triggered[(machine, kind)] = DateTime.UtcNow;
        switch (kind)
        {
            case SnapshotKind.Processes: _engine.RequestProcessList(machine, notifyUi: false); break;
            case SnapshotKind.SysInfo:   _engine.RequestSysInfo(machine, notifyUi: false); break;
            case SnapshotKind.Services:  _engine.RequestServices(machine, notifyUi: false); break;
            case SnapshotKind.Events:    _engine.RequestEvents(machine, notifyUi: false); break;
            case SnapshotKind.CpuDetail: _engine.RequestCpuDetail(machine, notifyUi: false); break;
            case SnapshotKind.Screenshot: _engine.RequestScreenshot(machine, notifyUi: false); break;
        }
        return true;
    }

    void OnProcessList(RemoteClient cl) => _received[(cl.MachineName, SnapshotKind.Processes)] = DateTime.UtcNow;
    void OnSysInfo(RemoteClient cl)     => _received[(cl.MachineName, SnapshotKind.SysInfo)]   = DateTime.UtcNow;
    void OnServices(RemoteClient cl)    => _received[(cl.MachineName, SnapshotKind.Services)]  = DateTime.UtcNow;
    void OnEvents(RemoteClient cl)      => _received[(cl.MachineName, SnapshotKind.Events)]    = DateTime.UtcNow;
    void OnCpuDetail(RemoteClient cl, CpuDetailReport detail)
    {
        _cpuDetail[cl.MachineName] = detail;
        _received[(cl.MachineName, SnapshotKind.CpuDetail)] = DateTime.UtcNow;
    }

    void OnScreenshot(RemoteClient cl, ScreenshotData shot)
    {
        _screenshots[cl.MachineName] = shot;
        _received[(cl.MachineName, SnapshotKind.Screenshot)] = DateTime.UtcNow;
    }

    public void Dispose()
    {
        _engine.ProcessListUpdated -= OnProcessList;
        _engine.SysInfoUpdated     -= OnSysInfo;
        _engine.ServicesUpdated    -= OnServices;
        _engine.EventsUpdated      -= OnEvents;
        _engine.CpuDetailUpdated   -= OnCpuDetail;
        _engine.ScreenshotUpdated  -= OnScreenshot;
    }

    public void MarkReceivedAt(string machine, SnapshotKind kind, DateTime at)
        => _received[(machine, kind)] = at;
}
