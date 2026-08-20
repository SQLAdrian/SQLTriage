/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Linq;
using System.Windows;

namespace SQLTriage
{
    /// <summary>
    /// Custom entry point that supports WPF (default) and headless server modes.
    /// Run headless (interactive):  SQLTriage.exe --server        (Kestrel only, browse from any machine)
    /// Run as service:              SQLTriage.exe --service
    /// Install as service:          SQLTriage.exe --service --install [--username DOMAIN\user [--prompt-credentials]]
    ///                              (--password is rejected; --prompt-credentials asks Windows for it when elevated)
    /// Uninstall service:           SQLTriage.exe --service --uninstall
    /// Run a headless audit:        SQLTriage.exe --audit --servers .\SQL01,.\SQL02 [...]  (see Cli/CliAuditHost.cs)
    /// Import results headlessly:   SQLTriage.exe --import run1.json [run2.json|folder\ ...]  (full/dev build only — see Cli/CliImportHost.cs)
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Top-level help/version — ONLY when no mode switch is present, so a mode's own
            // arguments are never intercepted here (`--audit --help` belongs to AuditCliArgs, and
            // a --service argument VALUE that happens to look like "-h" is not a help request).
            var hasMode = ModeSwitches.Any(m => args.Contains(m, StringComparer.OrdinalIgnoreCase));
            if (!hasMode && args.Any(IsHelpOrVersion))
            {
                Cli.CliAuditHost.AttachToParentConsole();
                Console.WriteLine(args.Contains("--version", StringComparer.OrdinalIgnoreCase)
                    ? $"SQLTriage {Cli.CliVersion.Read()}"
                    : Usage);
                Environment.Exit(0);
                return;
            }

            // Headless modes — Kestrel only, no WPF. Both route to the same host;
            // --service additionally honours --install/--uninstall and Windows Service control.
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--server", StringComparer.OrdinalIgnoreCase))
            {
                // Propagate the host's exit code. This call used to discard it, so a refused
                // `sc create` and a Kestrel bind failure both exited 0 — read as success by the
                // Service Management page and by the SCM alike.
                Environment.Exit(Data.Services.WindowsServiceHost.Run(args));
                return;
            }
            else if (args.Contains("--audit", StringComparer.OrdinalIgnoreCase))
            {
                // Non-interactive audit mode — console-only composition root, touches zero
                // WPF types. Community-build, fully free, incl. scheduling (the OS Task
                // Scheduler invokes the exe unattended). See Cli/CliAuditHost.cs.
                Environment.Exit(SQLTriage.Cli.CliAuditHost.Run(args));
                return;
            }
#if !SQLT_NO_DEVTOOLS
            else if (args.Contains("--import", StringComparer.OrdinalIgnoreCase))
            {
                // #84 results-import round-trip — FULL/DEV BUILD ONLY. Cli/CliImportHost.cs and
                // Cli/ImportCliArgs.cs are Compile-Removed from a community build
                // (buildprofile.targets, module: dev-tools), so this whole branch is fenced the
                // same way the pages/routes are — the community/redacted binary only ever
                // EXPORTS a results file (via --audit), it can't re-import one.
                Environment.Exit(SQLTriage.Cli.CliImportHost.Run(args));
                return;
            }
#endif
            else if (UnknownSwitch(args) is { } unknown)
            {
                // Dispatch above is by exact match, so a mistyped mode switch (`--audti
                // --servers X`) matched nothing and fell THROUGH to the GUI branch — an operator
                // scripting a headless run got the desktop app started instead of an error.
                // Anything switch-shaped that no mode claimed is now refused, and never starts
                // the GUI.
                //
                // Attach first — a GUI-subsystem binary has no console streams wired at startup,
                // so without this the message below goes nowhere.
                Cli.CliAuditHost.AttachToParentConsole();
                Console.Error.WriteLine($"ERROR: Unknown argument: {unknown}");
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage);
                Environment.Exit(3);
                return;
            }
            else
            {
                // Normal WPF application
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
        }

        /// <summary>The switches that select a non-GUI mode above. Kept in one place so the
        /// top-level help check and the unknown-argument check cannot drift from the dispatch.</summary>
        private static readonly string[] ModeSwitches = { "--service", "--server", "--audit", "--import" };

        private static bool IsHelpOrVersion(string a) =>
            a.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || a.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || a.Equals("-?", StringComparison.Ordinal)
            || a.Equals("/?", StringComparison.Ordinal)
            || a.Equals("--version", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The first switch-shaped argument ("-" or "/" prefixed) that no mode above claimed, or
        /// null when the command line is a legitimate GUI launch. Bare (non-prefixed) arguments
        /// are ignored — Windows can hand a WPF app a file path, and refusing those would be a
        /// new failure mode rather than a fixed one.
        /// </summary>
        private static string? UnknownSwitch(string[] args)
        {
            foreach (var a in args)
            {
                if (a.Length == 0) continue;
                if (a[0] != '-' && a[0] != '/') continue;

                // --devbridge[=...] is a real GUI-mode switch (App.xaml.cs). It is only ACTED on
                // in a DEBUG, non-community build, but it is accepted here unconditionally so a
                // Release build refuses it silently-as-a-no-op rather than refusing to start.
                if (a.StartsWith("--devbridge", StringComparison.OrdinalIgnoreCase)) continue;

                return a;
            }
            return null;
        }

        /// <summary>Top-level usage. Per-mode options live behind that mode's own --help.
        /// The --import line is fenced the same way its dispatch is: a community build has no
        /// import mode, so advertising one would be a promise the binary does not keep.</summary>
        private static readonly string Usage = """
SQLTriage — SQL Server health and audit.

Usage:
  SQLTriage.exe                       Start the desktop application.
  SQLTriage.exe --server              Headless Kestrel; browse from another machine.
  SQLTriage.exe --service [--install|--uninstall]
                                      Run or manage the Windows Service.
  SQLTriage.exe --audit  --help       Headless audit mode and its options.
"""
#if !SQLT_NO_DEVTOOLS
            + "  SQLTriage.exe --import --help       Headless results import (full/dev build only)."
#endif
            ;
    }
}
