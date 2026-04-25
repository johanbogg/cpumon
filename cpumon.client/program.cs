// CpuMon.Client/Program.cs
using System;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows.Forms;

internal static class Program
{
    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static readonly bool Admin = IsAdmin();

    [STAThread]
    static void Main(string[] args)
    {
        AppState.Admin = Admin;

        bool daemon = false, serviceMode = false, agentMode = false, install = false, uninstall = false;
        string? forceIp = null, token = null, pipeSecret = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--daemon", StringComparison.OrdinalIgnoreCase) || a.Equals("-d", StringComparison.OrdinalIgnoreCase))
                daemon = true;
            else if (a.Equals("--service", StringComparison.OrdinalIgnoreCase))
                serviceMode = true;
            else if (a.Equals("--agent", StringComparison.OrdinalIgnoreCase))
                agentMode = true;
            else if (a.Equals("--install", StringComparison.OrdinalIgnoreCase))
                install = true;
            else if (a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
                uninstall = true;
            else if ((a.Equals("--server-ip", StringComparison.OrdinalIgnoreCase) || a.Equals("-ip", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                forceIp = args[++i];
            else if ((a.Equals("--token", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                token = args[++i];
            else if (a.Equals("--pipe-secret", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                pipeSecret = args[++i];
        }

        // Non-GUI paths — no WinForms pump needed
        if (install)   { ServiceManager.Install(forceIp, token); return; }
        if (uninstall) { ServiceManager.Uninstall(); return; }

        // SCM starts the service and blocks here until the service stops
        if (serviceMode)
        {
            ServiceBase.Run(new CpuMonService(forceIp, token));
            return;
        }

        // WinForms paths
        ApplicationConfiguration.Initialize();

        // Agent runs in the interactive user session, no UAC needed
        if (agentMode)
        {
            Application.Run(new AgentContext(pipeSecret));
            return;
        }

        // Normal modes — request admin elevation if not already elevated
        if (!Admin)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                    Arguments = string.Join(' ', args),
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return;
            }
            catch { }
        }

        if (daemon)
            Application.Run(new DaemonContext(forceIp, token));
        else
            Application.Run(new ClientForm(forceIp, token));
    }
}
