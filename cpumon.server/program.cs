// CpuMon.Server/Program.cs
using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool noBroadcast    = false;
        bool newUi          = false;
        bool web            = false;
        int  webPort        = 47202;
        bool webNoTls       = false;
        bool webBehindProxy = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--no-broadcast", StringComparison.OrdinalIgnoreCase))
                noBroadcast = true;
            else if (a.Equals("--new-ui", StringComparison.OrdinalIgnoreCase))
                newUi = true;
            else if (a.Equals("--web", StringComparison.OrdinalIgnoreCase))
                web = true;
            else if (a.Equals("--web-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                     int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p))
            { webPort = p; i++; }
            else if (a.Equals("--web-no-tls", StringComparison.OrdinalIgnoreCase))
                webNoTls = true;
            else if (a.Equals("--web-behind-proxy", StringComparison.OrdinalIgnoreCase))
                webBehindProxy = true;
        }

        WebStartupOptions? webOpts = web ? new WebStartupOptions
        {
            Port        = webPort,
            UseTls      = !webNoTls,
            BehindProxy = webBehindProxy,
        } : null;

        ApplicationConfiguration.Initialize();
        Application.Run(newUi
            ? (Form)new ServerForm2(noBroadcast, webOpts)
            : new ServerForm(noBroadcast, webOpts));
    }
}
