/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using System.Windows;

namespace SqlHealthAssessment
{
    /// <summary>
    /// Custom entry point that supports both WPF (default) and Windows Service (--service) modes.
    /// Install as service: SqlHealthAssessment.exe --service --install [--username DOMAIN\user --password pass]
    /// Uninstall service:  SqlHealthAssessment.exe --service --uninstall
    /// Run as service:     SqlHealthAssessment.exe --service
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
            {
                // Headless Windows Service mode — Kestrel only, no WPF
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
