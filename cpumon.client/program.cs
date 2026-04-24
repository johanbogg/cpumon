// CpuMon.Client/Program.cs
using System;
using System.Diagnostics;
using System.Security.Principal;
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

        bool daemon = false, serviceMode = false, agentMode = false;
        string? forceIp = null, token = null, pipeSecret = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--daemon", StringComparison.OrdinalIgnoreCase) || a.Equals("-d", StringComparison.OrdinalIgnoreCase))
                daemon = true;
            else if (a.Equals("--service-mode", StringComparison.OrdinalIgnoreCase))
                serviceMode = true;
            else if (a.Equals("--agent", StringComparison.OrdinalIgnoreCase))
                agentMode = true;
            else if ((a.Equals("--server-ip", StringComparison.OrdinalIgnoreCase) || a.Equals("-ip", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                forceIp = args[++i];
            else if ((a.Equals("--token", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                token = args[++i];
            else if (a.Equals("--pipe-secret", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                pipeSecret = args[++i];
        }

        ApplicationConfiguration.Initialize();

        // Agent mode — runs in interactive session, no UAC needed
        if (agentMode)
        {
            Application.Run(new AgentContext(pipeSecret));
            return;
        }

        // Service mode — runs as NSSM service in Session 0
        if (serviceMode)
        {
            Application.Run(new ServiceDaemonContext(forceIp, token));
            return;
        }

        // Normal modes — request admin
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