// CpuMon.Server/Program.cs
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool noBroadcast = false;
        bool newUi = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--no-broadcast", StringComparison.OrdinalIgnoreCase))
                noBroadcast = true;
            else if (args[i].Equals("--new-ui", StringComparison.OrdinalIgnoreCase))
                newUi = true;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(newUi ? (Form)new ServerForm2(noBroadcast) : new ServerForm(noBroadcast));
    }
}