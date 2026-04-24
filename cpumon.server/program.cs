// CpuMon.Server/Program.cs
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool noBroadcast = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--no-broadcast", StringComparison.OrdinalIgnoreCase))
                noBroadcast = true;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new ServerForm(noBroadcast));
    }
}