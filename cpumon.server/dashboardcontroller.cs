using System;
using System.Collections.Generic;
using System.Linq;

public enum DashboardConfirmKind { Question, Warning }

public sealed class DashboardMessageBoxRequest
{
    public string Message { get; init; } = "";
    public string Title { get; init; } = "Confirm";
    public DashboardConfirmKind Kind { get; init; } = DashboardConfirmKind.Question;
    public Action OnConfirm { get; init; } = static () => { };
}

public sealed class DashboardPromptRequest
{
    public string Title { get; init; } = "";
    public string Label { get; init; } = "";
    public Action<string> OnSubmit { get; init; } = static _ => { };
}

public sealed class DashboardFilePickerRequest
{
    public string Title { get; init; } = "";
    public string Filter { get; init; } = "";
    public Action<string> OnFileSelected { get; init; } = static _ => { };
}

public sealed class DashboardDialogRequest
{
    public string Kind { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string? Argument { get; init; }
}

public sealed class ServerDashboardController
{
    readonly ServerEngine _engine;
    readonly ServerDashboardStateBuilder _stateBuilder;
    readonly IServerPlatformServices? _platform;
    readonly HashSet<string> _selectedMachineNames = new(StringComparer.OrdinalIgnoreCase);
    string _osFilter = "all";
    string _sortMode = "name";

    public event Action<string>? ClipboardRequested;
    public event Action<DashboardMessageBoxRequest>? MessageBoxRequested;
    public event Action<DashboardPromptRequest>? PromptRequested;
    public event Action<DashboardFilePickerRequest>? FilePickerRequested;
    public event Action<DashboardDialogRequest>? DialogRequested;
    public event Action<string>? OpenExternalRequested;

    public ServerDashboardController(ServerEngine engine, IServerPlatformServices? platform = null)
    {
        _engine = engine;
        _platform = platform;
        _stateBuilder = new ServerDashboardStateBuilder(engine);
    }

    public IReadOnlySet<string> SelectedMachineNames => _selectedMachineNames;
    public string OsFilter => _osFilter;
    public string SortMode => _sortMode;

    public ServerDashboardState GetState() => _stateBuilder.Build(_selectedMachineNames, _osFilter, _sortMode);

    public string CycleOsFilter()
    {
        _osFilter = _osFilter switch { "all" => "windows", "windows" => "linux", _ => "all" };
        return _osFilter;
    }

    public string CycleSortMode()
    {
        _sortMode = _sortMode == "name" ? "os" : "name";
        return _sortMode;
    }

    public bool ToggleSelection(string machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return false;
        return !_selectedMachineNames.Remove(machineName) && _selectedMachineNames.Add(machineName);
    }

    public void DeselectMachine(string machineName) => _selectedMachineNames.Remove(machineName);

    public void ClearSelection() => _selectedMachineNames.Clear();

    public void PurgeStaleClients(int staleSeconds = 120)
    {
        foreach (var machine in _engine.Clients
            .Where(kv => (DateTime.UtcNow - kv.Value.LastSeen).TotalSeconds > staleSeconds)
            .Select(kv => kv.Key)
            .ToList())
        {
            if (_engine.Clients.TryRemove(machine, out var client))
                client.Dispose();
            _selectedMachineNames.Remove(machine);
        }
    }

    public void SelectAllVisible()
    {
        foreach (var client in GetState().Clients)
            _selectedMachineNames.Add(client.MachineName);
    }

    public void SelectOutdatedVisible()
    {
        foreach (var client in GetState().Clients.Where(client => client.IsOutdated))
            _selectedMachineNames.Add(client.MachineName);
    }

    public bool ApprovePending(string machineName) => _engine.ApprovePending(machineName);

    public bool RejectPending(string machineName) => _engine.RejectPending(machineName);

    public void RegenerateToken() => _engine.RegenerateToken();

    public void CopyToken()
    {
        var token = _engine.Token;
        if (string.IsNullOrEmpty(token)) return;
        if (_platform != null) _platform.SetClipboardText(token);
        else ClipboardRequested?.Invoke(token);
        _engine.Log.Add("Token copied", Th.Grn);
    }

    public void ToggleClientExpanded(string machineName)
    {
        if (_engine.Clients.TryGetValue(machineName, out var cl))
            cl.Expanded = !cl.Expanded;
    }

    public void WakeOffline(string machineName) => _engine.WakeOffline(machineName);

    public void SetOfflineMac(string machineName, string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        _engine.SetMacForOffline(machineName, mac.Trim());
    }

    public void RequestSetOfflineMac(string machineName)
    {
        var request = new DashboardPromptRequest
        {
            Title = "Set MAC",
            Label = $"MAC for {machineName} (e.g. AA:BB:CC:DD:EE:FF):",
            OnSubmit = mac => SetOfflineMac(machineName, mac)
        };
        if (_platform != null) _platform.Prompt(request);
        else PromptRequested?.Invoke(request);
    }

    public void ForgetOffline(string machineName)
    {
        Confirm(new DashboardMessageBoxRequest
        {
            Message = $"Forget {machineName}?",
            OnConfirm = () => _engine.Store.Forget(machineName)
        });
    }

    public void RestartClient(string machineName)
    {
        Confirm(new DashboardMessageBoxRequest
        {
            Message = $"Restart {machineName}?",
            Kind = DashboardConfirmKind.Warning,
            OnConfirm = () => _engine.RequestRestart(machineName)
        });
    }

    public void ShutdownClient(string machineName)
    {
        Confirm(new DashboardMessageBoxRequest
        {
            Message = $"SHUT DOWN {machineName}?",
            Kind = DashboardConfirmKind.Warning,
            OnConfirm = () => _engine.RequestShutdown(machineName)
        });
    }

    public void ForgetClient(string machineName)
    {
        Confirm(new DashboardMessageBoxRequest
        {
            Message = $"Forget {machineName}?",
            OnConfirm = () =>
            {
                _engine.ForgetClient(machineName);
                _selectedMachineNames.Remove(machineName);
            }
        });
    }

    public void RequestProcesses(string machineName)
    {
        if (_platform != null) _platform.ShowProcessDialog(machineName);
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "processes", MachineName = machineName });
        _engine.RequestProcessList(machineName);
    }

    public bool RequestSysInfo(string machineName) => _engine.RequestSysInfo(machineName);

    public bool RequestServices(string machineName) => _engine.RequestServices(machineName);

    public bool RequestEvents(string machineName) => _engine.RequestEvents(machineName);

    public bool RequestCpuDetail(string machineName) => _engine.RequestCpuDetail(machineName);

    public bool RequestScreenshot(string machineName) => _engine.RequestScreenshot(machineName);

    public bool TogglePaw(string machineName) => _engine.TogglePaw(machineName);

    public void ShowApprovedClients()
    {
        if (_platform != null) _platform.ShowApprovedClients();
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "approved" });
    }

    public void ShowAlerts()
    {
        if (_platform != null) _platform.ShowAlerts();
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "alerts" });
    }

    public void ShowHealth(string machineName)
    {
        if (_platform != null) _platform.ShowHealthDialog(machineName);
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "health", MachineName = machineName });
    }

    public void OpenTerminal(string machineName, string shell)
    {
        if (_platform != null) _platform.ShowTerminal(machineName, shell);
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "terminal", MachineName = machineName, Argument = shell });
    }

    public void OpenFileBrowser(string machineName, string? initialPath = null)
    {
        if (_platform != null) _platform.ShowFileBrowser(machineName, initialPath);
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "files", MachineName = machineName, Argument = initialPath });
    }

    public void OpenRdp(string machineName)
    {
        if (_platform != null) _platform.ShowRdp(machineName);
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "rdp", MachineName = machineName });
    }

    public void SendUserMessage(string machineName)
    {
        if (_platform != null) _platform.PromptSendUserMessage(machineName, text => SubmitUserMessage(machineName, text));
        else DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "send_message", MachineName = machineName });
    }

    public bool SubmitUserMessage(string machineName, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return _engine.SendUserMessage(machineName, text);
    }

    public void PushUpdate(string machineName)
    {
        if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
        bool linux = ServerEngine.IsLinuxClient(cl);
        PickFile(new DashboardFilePickerRequest
        {
            Title = linux ? "Select Linux cpumon.py or cpumon-linux release zip" : "Select new client exe to push",
            Filter = linux ? "Linux update|*.py;*.zip|Python script|*.py|Release zip|*.zip" : "Executable|*.exe",
            OnFileSelected = path => DoPushUpdate(machineName, linux, path)
        });
    }

    public void PushUpdateToSelected()
    {
        var snapshot = _selectedMachineNames.ToList();
        var winClients = LiveSelectedClients(snapshot, linux: false);
        var linuxClients = LiveSelectedClients(snapshot, linux: true);
        if (winClients.Count == 0 && linuxClients.Count == 0) return;
        string filter = winClients.Count > 0 && linuxClients.Count > 0
            ? "Client files|*.exe;*.py;*.zip|Executables|*.exe|Linux update|*.py;*.zip"
            : winClients.Count > 0 ? "Executable|*.exe" : "Linux update|*.py;*.zip|Python script|*.py|Release zip|*.zip";
        PickFile(new DashboardFilePickerRequest
        {
            Title = $"Select file to push to {snapshot.Count} client(s)",
            Filter = filter,
            OnFileSelected = path => DoPushMulti(snapshot, path)
        });
    }

    public void OpenStagedReleaseOrUrl()
    {
        var staged = _engine.StagedReleaseDir;
        string? target = staged != null && System.IO.Directory.Exists(staged) ? staged : _engine.AvailableUpdate?.ReleaseUrl;
        if (target != null) OpenExternal(target);
    }

    public void OpenReleaseNotes()
    {
        var url = _engine.AvailableUpdate?.ReleaseUrl;
        if (url != null) OpenExternal(url);
    }

    void Confirm(DashboardMessageBoxRequest request)
    {
        if (_platform != null) _platform.Confirm(request);
        else MessageBoxRequested?.Invoke(request);
    }

    void PickFile(DashboardFilePickerRequest request)
    {
        if (_platform != null) _platform.PickFile(request);
        else FilePickerRequested?.Invoke(request);
    }

    void OpenExternal(string target)
    {
        if (_platform != null) _platform.OpenExternal(target);
        else OpenExternalRequested?.Invoke(target);
    }

    void DoPushUpdate(string machineName, bool linux, string path)
    {
        if (!_engine.Clients.TryGetValue(machineName, out var cl))
        {
            _engine.Log.Add($"Update failed: {machineName} is no longer connected", Th.Red);
            return;
        }
        if (linux)
        {
            if (!LinuxUpdatePayload.TryRead(path, out var fileName, out var bytes, out var error))
            {
                _engine.Log.Add($"Linux update failed: {error}", Th.Red);
                return;
            }
            _engine.Log.Add($"Pushing Linux update → {cl.MachineName}…", Th.Org);
            _engine.PushUpdatePayload(cl, fileName, bytes);
            return;
        }
        _engine.Log.Add($"Pushing update → {cl.MachineName}…", Th.Org);
        _engine.PushUpdate(cl, path);
    }

    void DoPushMulti(IReadOnlyList<string> machineNames, string path)
    {
        bool isLinuxPayload = path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var winClients = LiveSelectedClients(machineNames, linux: false);
        var linuxClients = LiveSelectedClients(machineNames, linux: true);
        var targets = isLinuxPayload ? linuxClients : winClients;
        if (targets.Count == 0) { _engine.Log.Add("No matching clients for selected file type", Th.Org); return; }
        if (isLinuxPayload)
        {
            if (!LinuxUpdatePayload.TryRead(path, out var fileName, out var bytes, out var error))
            {
                _engine.Log.Add($"Linux update failed: {error}", Th.Red);
                return;
            }
            _engine.Log.Add($"Pushing Linux update → {targets.Count} client(s)…", Th.Org);
            _engine.PushUpdateMultiPayload(targets, fileName, bytes);
            return;
        }
        _engine.Log.Add($"Pushing update → {targets.Count} client(s)…", Th.Org);
        _engine.PushUpdateMulti(targets, path);
    }

    List<RemoteClient> LiveSelectedClients(IEnumerable<string> machineNames, bool linux)
    {
        var selected = new HashSet<string>(machineNames, StringComparer.OrdinalIgnoreCase);
        return _engine.Clients
            .Where(kv => selected.Contains(kv.Key) && ServerEngine.IsLinuxClient(kv.Value) == linux)
            .Select(kv => kv.Value)
            .ToList();
    }
}
