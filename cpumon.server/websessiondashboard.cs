using System;
using System.Collections.Generic;

public static class WebSessionDashboard
{
    public static ServerDashboardState Build(ServerDashboardStateBuilder builder, SessionState session)
    {
        string[] selected;
        HashSet<string> expanded;
        string osFilter;
        string sortMode;
        string showFilter;
        lock (session.UiLock)
        {
            selected = new string[session.SelectedMachineNames.Count];
            session.SelectedMachineNames.CopyTo(selected);
            expanded = new HashSet<string>(session.ExpandedMachineNames, StringComparer.OrdinalIgnoreCase);
            osFilter = session.OsFilter;
            sortMode = session.SortMode;
            showFilter = session.ShowFilter;
        }
        return builder.Build(selected, osFilter, sortMode, expandedMachineNames: expanded, showFilter: showFilter);
    }

    public static void SetSelection(SessionState session, IEnumerable<string> machineNames)
    {
        lock (session.UiLock)
        {
            session.SelectedMachineNames.Clear();
            foreach (var name in machineNames)
                if (!string.IsNullOrWhiteSpace(name))
                    session.SelectedMachineNames.Add(name.Trim());
        }
    }

    public static string SetOsFilter(SessionState session, string value)
    {
        var v = NormalizeOsFilter(value);
        lock (session.UiLock) session.OsFilter = v;
        return v;
    }

    public static string SetSortMode(SessionState session, string value)
    {
        var v = NormalizeSortMode(value);
        lock (session.UiLock) session.SortMode = v;
        return v;
    }

    public static string SetShowFilter(SessionState session, string value)
    {
        var v = NormalizeShowFilter(value);
        lock (session.UiLock) session.ShowFilter = v;
        return v;
    }

    public static bool ToggleExpanded(SessionState session, string machineName)
    {
        lock (session.UiLock)
        {
            if (session.ExpandedMachineNames.Remove(machineName))
                return false;
            session.ExpandedMachineNames.Add(machineName);
            return true;
        }
    }

    public static void RemoveMachine(SessionState session, string machineName)
    {
        lock (session.UiLock)
        {
            session.SelectedMachineNames.Remove(machineName);
            session.ExpandedMachineNames.Remove(machineName);
        }
    }

    static string NormalizeOsFilter(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "all" or "windows" or "linux") return v;
        throw new ArgumentException("Invalid OS filter.");
    }

    static string NormalizeSortMode(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "name" or "os") return v;
        throw new ArgumentException("Invalid sort mode.");
    }

    static string NormalizeShowFilter(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "all" or "outdated" or "selected") return v;
        throw new ArgumentException("Invalid show filter.");
    }
}
