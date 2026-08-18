/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using System.Windows;

namespace SQLTriage
{
    /// <summary>
    /// Custom entry point that supports WPF (default) and headless server modes.
    /// Run headless (interactive):  SQLTriage.exe --server        (Kestrel only, browse from any machine)
    /// Run as service:              SQLTriage.exe --service
    /// Install as service:          SQLTriage.exe --service --install [--username DOMAIN\user --password pass]
    /// Uninstall service:           SQLTriage.exe --service --uninstall
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Headless modes — Kestrel only, no WPF. Both route to the same host;
            // --service additionally honours --install/--uninstall and Windows Service control.
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--server", StringComparer.OrdinalIgnoreCase))
            {
                Data.Services.WindowsServiceHost.Run(args);
            }
            else
            {
                // Normal WPF application
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
        }
    }
}
