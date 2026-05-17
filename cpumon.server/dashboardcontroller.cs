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
    readonly HashSet<string> _selectedMachineNames = new(StringComparer.OrdinalIgnoreCase);
    string _osFilter = "all";
    string _sortMode = "name";

    public event Action<string>? ClipboardRequested;
    public event Action<DashboardMessageBoxRequest>? MessageBoxRequested;
    public event Action<DashboardPromptRequest>? PromptRequested;
    public event Action<DashboardFilePickerRequest>? FilePickerRequested;
    public event Action<DashboardDialogRequest>? DialogRequested;
    public event Action<string>? OpenExternalRequested;

    public ServerDashboardController(ServerEngine engine)
    {
        _engine = engine;
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
        ClipboardRequested?.Invoke(token);
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
        PromptRequested?.Invoke(new DashboardPromptRequest
        {
            Title = "Set MAC",
            Label = $"MAC for {machineName} (e.g. AA:BB:CC:DD:EE:FF):",
            OnSubmit = mac => SetOfflineMac(machineName, mac)
        });
    }

    public void ForgetOffline(string machineName)
    {
        MessageBoxRequested?.Invoke(new DashboardMessageBoxRequest
        {
            Message = $"Forget {machineName}?",
            OnConfirm = () => _engine.Store.Forget(machineName)
        });
    }

    public void RestartClient(string machineName)
    {
        MessageBoxRequested?.Invoke(new DashboardMessageBoxRequest
        {
            Message = $"Restart {machineName}?",
            Kind = DashboardConfirmKind.Warning,
            OnConfirm = () => _engine.RequestRestart(machineName)
        });
    }

    public void ShutdownClient(string machineName)
    {
        MessageBoxRequested?.Invoke(new DashboardMessageBoxRequest
        {
            Message = $"SHUT DOWN {machineName}?",
            Kind = DashboardConfirmKind.Warning,
            OnConfirm = () => _engine.RequestShutdown(machineName)
        });
    }

    public void ForgetClient(string machineName)
    {
        MessageBoxRequested?.Invoke(new DashboardMessageBoxRequest
        {
            Message = $"Forget {machineName}?",
            OnConfirm = () => _engine.ForgetClient(machineName)
        });
    }

    public void RequestProcesses(string machineName)
    {
        DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "processes", MachineName = machineName });
        _engine.RequestProcessList(machineName);
    }

    public bool RequestSysInfo(string machineName) => _engine.RequestSysInfo(machineName);

    public bool RequestServices(string machineName) => _engine.RequestServices(machineName);

    public bool RequestEvents(string machineName) => _engine.RequestEvents(machineName);

    public bool RequestCpuDetail(string machineName) => _engine.RequestCpuDetail(machineName);

    public bool RequestScreenshot(string machineName) => _engine.RequestScreenshot(machineName);

    public bool TogglePaw(string machineName) => _engine.TogglePaw(machineName);

    public void ShowApprovedClients() => DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "approved" });

    public void ShowAlerts() => DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "alerts" });

    public void ShowHealth(string machineName) => DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "health", MachineName = machineName });

    public void OpenTerminal(string machineName, string shell)
    {
        DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "terminal", MachineName = machineName, Argument = shell });
    }

    public void OpenFileBrowser(string machineName, string? initialPath = null)
    {
        DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "files", MachineName = machineName, Argument = initialPath });
    }

    public void OpenRdp(string machineName) => DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "rdp", MachineName = machineName });

    public void SendUserMessage(string machineName) => DialogRequested?.Invoke(new DashboardDialogRequest { Kind = "send_message", MachineName = machineName });

    public bool SubmitUserMessage(string machineName, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return _engine.SendUserMessage(machineName, text);
    }

    public void PushUpdate(string machineName)
    {
        if (!_engine.Clients.TryGetValue(machineName, out var cl)) return;
        bool linux = ServerEngine.IsLinuxClient(cl);
        FilePickerRequested?.Invoke(new DashboardFilePickerRequest
        {
            Title = linux ? "Select Linux cpumon.py or cpumon-linux release zip" : "Select new client exe to push",
            Filter = linux ? "Linux update|*.py;*.zip|Python script|*.py|Release zip|*.zip" : "Executable|*.exe",
            OnFileSelected = path => DoPushUpdate(cl, linux, path)
        });
    }

    public void PushUpdateToSelected()
    {
        var snapshot = new HashSet<string>(_selectedMachineNames, StringComparer.OrdinalIgnoreCase);
        var winClients = _engine.Clients.Where(kv => snapshot.Contains(kv.Key) && !ServerEngine.IsLinuxClient(kv.Value)).Select(kv => kv.Value).ToList();
        var linuxClients = _engine.Clients.Where(kv => snapshot.Contains(kv.Key) && ServerEngine.IsLinuxClient(kv.Value)).Select(kv => kv.Value).ToList();
        if (winClients.Count == 0 && linuxClients.Count == 0) return;
        string filter = winClients.Count > 0 && linuxClients.Count > 0
            ? "Client files|*.exe;*.py;*.zip|Executables|*.exe|Linux update|*.py;*.zip"
            : winClients.Count > 0 ? "Executable|*.exe" : "Linux update|*.py;*.zip|Python script|*.py|Release zip|*.zip";
        FilePickerRequested?.Invoke(new DashboardFilePickerRequest
        {
            Title = $"Select file to push to {snapshot.Count} client(s)",
            Filter = filter,
            OnFileSelected = path => DoPushMulti(winClients, linuxClients, path)
        });
    }

    public void OpenStagedReleaseOrUrl()
    {
        var staged = _engine.StagedReleaseDir;
        string? target = staged != null && System.IO.Directory.Exists(staged) ? staged : _engine.AvailableUpdate?.ReleaseUrl;
        if (target != null) OpenExternalRequested?.Invoke(target);
    }

    public void OpenReleaseNotes()
    {
        var url = _engine.AvailableUpdate?.ReleaseUrl;
        if (url != null) OpenExternalRequested?.Invoke(url);
    }

    void DoPushUpdate(RemoteClient cl, bool linux, string path)
    {
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

    void DoPushMulti(List<RemoteClient> winClients, List<RemoteClient> linuxClients, string path)
    {
        bool isLinuxPayload = path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
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
}
