// CpuMon.Server/Program.cs
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var settings = ServerStartupSettings.Load();
        bool noBroadcast    = settings.NoBroadcast;
        bool newUi          = settings.NewUi;
        bool web            = settings.WebEnabled;
        int  webPort        = settings.WebPort;
        bool webUseTls      = settings.WebUseTls;
        bool webBehindProxy = settings.WebBehindProxy;
        bool startInTray    = settings.HideToTray;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--no-broadcast", StringComparison.OrdinalIgnoreCase))
                noBroadcast = true;
            else if (a.Equals("--broadcast", StringComparison.OrdinalIgnoreCase))
                noBroadcast = false;
            else if (a.Equals("--new-ui", StringComparison.OrdinalIgnoreCase))
                newUi = true;
            else if (a.Equals("--old-ui", StringComparison.OrdinalIgnoreCase))
                newUi = false;
            else if (a.Equals("--web", StringComparison.OrdinalIgnoreCase))
                web = true;
            else if (a.Equals("--no-web", StringComparison.OrdinalIgnoreCase))
                web = false;
            else if (a.Equals("--web-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                     int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p))
            { webPort = p; i++; }
            else if (a.Equals("--web-no-tls", StringComparison.OrdinalIgnoreCase))
                webUseTls = false;
            else if (a.Equals("--web-tls", StringComparison.OrdinalIgnoreCase))
                webUseTls = true;
            else if (a.Equals("--web-behind-proxy", StringComparison.OrdinalIgnoreCase))
                webBehindProxy = true;
            else if (a.Equals("--web-not-behind-proxy", StringComparison.OrdinalIgnoreCase))
                webBehindProxy = false;
            else if (a.Equals("--systray", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                startInTray = true;
            else if (a.Equals("--no-systray", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--no-tray", StringComparison.OrdinalIgnoreCase))
                startInTray = false;
        }

        WebStartupOptions? webOpts = web ? new WebStartupOptions
        {
            Port        = webPort,
            UseTls      = webUseTls,
            BehindProxy = webBehindProxy,
        } : null;

        ApplicationConfiguration.Initialize();
        Application.Run(newUi
            ? (Form)new ServerForm2(noBroadcast, webOpts, startInTray)
            : new ServerForm(noBroadcast, webOpts, startInTray));
    }
}
