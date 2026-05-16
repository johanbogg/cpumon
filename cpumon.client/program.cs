// CpuMon.Client/Program.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    const int AttachParentProcess = -1;
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int dwProcessId);

    static void AttachParentConsole()
    {
        try
        {
            AttachConsole(AttachParentProcess);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        AppState.Admin = Admin;

        bool daemon = false, serviceMode = false, agentMode = false, install = false, uninstall = false, resetAuth = false;
        string? forceIp = null, token = null;

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
            else if (a.Equals("--reset-auth", StringComparison.OrdinalIgnoreCase) || a.Equals("--reset-pairing", StringComparison.OrdinalIgnoreCase))
                resetAuth = true;
            else if ((a.Equals("--server-ip", StringComparison.OrdinalIgnoreCase) || a.Equals("-ip", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                forceIp = args[++i];
            else if ((a.Equals("--token", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                token = args[++i];
        }

        // Non-GUI paths — no WinForms pump needed
        bool cliMode = resetAuth || install || uninstall;
        if (cliMode) AttachParentConsole();

        if (resetAuth)
        {
            bool ok = TokenStore.Clear();
            Console.WriteLine(ok
                ? $"Cleared saved auth pairing: {TokenStore.AuthPath}"
                : $"Failed to clear saved auth pairing: {TokenStore.AuthPath}");
            Environment.ExitCode = ok ? 0 : 1;
            return;
        }
        if (install)   { RunCliAction(() => ServiceManager.Install(forceIp, token)); return; }
        if (uninstall) { RunCliAction(ServiceManager.Uninstall); return; }

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
            try
            {
                LogSink.Info("Agent.Startup", $"Starting interactive agent. user={Environment.UserName} interactive={Environment.UserInteractive} exe={Environment.ProcessPath ?? Application.ExecutablePath}");
                Application.Run(new AgentContext());
                LogSink.Info("Agent.Startup", "Interactive agent exited normally");
            }
            catch (Exception ex)
            {
                LogSink.Error("Agent.Startup", "Interactive agent crashed during startup", ex);
                throw;
            }
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

    static void RunCliAction(Action action)
    {
        try
        {
            action();
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Environment.ExitCode = 1;
        }
    }
}
