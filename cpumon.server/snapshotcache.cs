using System;
using System.Collections.Concurrent;

public enum SnapshotKind { Processes, SysInfo, Services, Events, CpuDetail }

public sealed class SnapshotCache : IDisposable
{
    readonly ServerEngine _engine;
    readonly ConcurrentDictionary<(string, SnapshotKind), DateTime> _received = new();
    readonly ConcurrentDictionary<(string, SnapshotKind), DateTime> _triggered = new();
    readonly ConcurrentDictionary<string, CpuDetailReport> _cpuDetail = new();

    public static readonly TimeSpan TtlProcesses = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TtlSysInfo   = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TtlServices  = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TtlEvents    = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TtlCpuDetail = TimeSpan.FromSeconds(5);

    public SnapshotCache(ServerEngine engine)
    {
        _engine = engine;
        _engine.ProcessListReceived += OnProcessList;
        _engine.SysInfoReceived     += OnSysInfo;
        _engine.ServicesReceived    += OnServices;
        _engine.EventsReceived      += OnEvents;
        _engine.CpuDetailReceived   += OnCpuDetail;
    }

    public DateTime? ReceivedAt(string machine, SnapshotKind kind)
        => _received.TryGetValue((machine, kind), out var t) ? t : null;

    public DateTime? TriggeredAt(string machine, SnapshotKind kind)
        => _triggered.TryGetValue((machine, kind), out var t) ? t : null;

    public CpuDetailReport? GetCpuDetail(string machine)
        => _cpuDetail.TryGetValue(machine, out var d) ? d : null;

    public static TimeSpan TtlFor(SnapshotKind kind) => kind switch
    {
        SnapshotKind.Processes => TtlProcesses,
        SnapshotKind.SysInfo   => TtlSysInfo,
        SnapshotKind.Services  => TtlServices,
        SnapshotKind.Events    => TtlEvents,
        SnapshotKind.CpuDetail => TtlCpuDetail,
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
        _triggered[(machine, kind)] = DateTime.UtcNow;
        switch (kind)
        {
            case SnapshotKind.Processes: _engine.RequestProcessList(machine); break;
            case SnapshotKind.SysInfo:   _engine.RequestSysInfo(machine); break;
            case SnapshotKind.Services:  _engine.RequestServices(machine); break;
            case SnapshotKind.Events:    _engine.RequestEvents(machine); break;
            case SnapshotKind.CpuDetail: _engine.RequestCpuDetail(machine); break;
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

    public void Dispose()
    {
        _engine.ProcessListReceived -= OnProcessList;
        _engine.SysInfoReceived     -= OnSysInfo;
        _engine.ServicesReceived    -= OnServices;
        _engine.EventsReceived      -= OnEvents;
        _engine.CpuDetailReceived   -= OnCpuDetail;
    }

    public void MarkReceivedAt(string machine, SnapshotKind kind, DateTime at)
        => _received[(machine, kind)] = at;
}
