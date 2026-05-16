using System;
using System.Collections.Generic;
using System.Linq;

public sealed class ServerDashboardController
{
    readonly ServerEngine _engine;
    readonly ServerDashboardStateBuilder _stateBuilder;
    readonly HashSet<string> _selectedMachineNames = new(StringComparer.OrdinalIgnoreCase);
    string _osFilter = "all";
    string _sortMode = "name";

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
}
