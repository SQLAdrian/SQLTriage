/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Linq;
using System.Windows;

namespace SQLTriage
{
    /// <summary>
    /// Custom entry point that supports WPF (default) and headless server modes.
    /// Run headless (interactive):  SQLTriage.exe --server        (Kestrel only; browse from THIS machine
    ///                              at http://localhost:&lt;ServicePort&gt;. An unauthenticated caller
    ///                              arriving on a non-loopback socket is refused, so a LAN address
    ///                              locks the owner out of a fresh install.)
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
            // FIRST, before any mode is chosen and before the help/version short-circuit below.
            //
            // The payload ships the operator-editable config files in config.default\ rather than in
            // config\, so that no upgrade route - not the installer, not the deploy script, and not an
            // operator hand-extracting the zip over their install, which is what the customer update
            // path has always been - can write over configuration they authored. That makes the real
            // config\ file something the app has to create, once, and this is where it happens.
            //
            // It runs here rather than inside a config service because there is no config service to
            // run inside: about forty call sites resolve Config\<file>.json from AppContext.BaseDirectory
            // independently of each other. Seeding ahead of the dispatch below is what makes the promise
            // hold on ALL of them - WPF, --service/--server, --audit, --import - and on --help/--version,
            // which returns before any of them.
            //
            // It never deletes, and it never overwrites anything the operator authored: see
            // ConfigDefaultsSeeder. ⚠ NOT "never overwrites" flatly, which is what this line used to
            // say. From 2026-09-11 the BPScripts pass MAY replace a file - but only one whose exact
            // bytes are a revision this product itself shipped, proved by hash against
            // ShippedBPScriptRevisions. Unknown bytes are the operator's and are kept. config\ and
            // docs\ are unchanged: they pass no oracle and never overwrite at all.
            // THREE PASSES, ONE RULE. config.default\ -> config\ carries the operator's tuning; from
            // 2026-09-10 round two, docs.default\ -> docs\ carries the compliance pack; and from
            // 2026-09-11, BPScripts.default\ -> BPScripts\ carries the Best Practice scripts. The pack
            // joined the seed-once side because its four documents are FILLED IN by the operator -
            // sign-off-log.md calls itself an append-only evidence record that auditors read - so the
            // first attempt's decision to refresh them on every update destroyed a signed record. The
            // scripts joined for the sharper version of the same reason: the operator does not just
            // fill those files in, they NAME them, and no allow-list of filenames could ever have
            // protected work we cannot enumerate. ⚠ The naming route is NOT the Save Script button,
            // which this comment used to credit. That button (Pages\BestPractice.razor:87, :216) passes
            // _editingScript.FileName and _editingScript is only ever assigned from a script already
            // listed, so it EDITS CONTENT and cannot create a name. Names arrive by hand-dropping a
            // .sql file and pressing Sync Scripts from Folder (:41 -> BPScriptService.cs:73).
            //
            // ⚠ THE SCRIPTS PASS IS NOT PURELY SEED-ONCE from 2026-09-11. Seed-once alone froze every
            // EXISTING install - all eleven stock scripts are already present on one, so every file was
            // Kept - which would have stopped improvements reaching anybody, not just the operators who
            // edited something. That pass now refreshes a file whose bytes are a revision we shipped and
            // keeps everything else. Adrian ruled it before merge; see ShippedBPScriptRevisions.
            //
            // ConfigDefaultsSeedingTests.The_seeder_is_still_invoked_from_Program_Main is the pin on
            // these three lines. Deleting any one of them leaves the whole suite green without it.
            var seeding = Data.Services.ConfigDefaultsSeeder.Run();
            var docSeeding = Data.Services.ConfigDefaultsSeeder.RunDocs();
            var bpSeeding = Data.Services.ConfigDefaultsSeeder.RunBPScripts();
            if (seeding.NeedsAttention || docSeeding.NeedsAttention || bpSeeding.NeedsAttention)
            {
                // A file that could not be created is not a cosmetic failure. Every reader in the app
                // falls back to a built-in default when its file is absent, so staying quiet here would
                // present "we are running on defaults you never chose" as a normal start. Say it, on
                // stderr, on every entry path - and then continue, because refusing to start over a
                // config folder the operator can still fix by hand is the worse failure.
                //
                // ⚠ TWO CLASSES REACH THIS, not one, since 2026-09-11 (lane seeder-stub-freeze).
                // SeedAction.Failed is "the copy was attempted and the folder would not take it" - a
                // permissions problem. SeedAction.MissingFromPayload is "this build PROMISED the file and
                // the payload does not carry it", which no permission will fix and which was SILENT until
                // that lane: the seeder enumerates what IS in config.default\, so it could not fail to
                // copy a file it never looked for. The expected set comes from
                // Data\Services\ShippedConfigDefaults.cs, generated at build time by XPath over this
                // project's csproj read as an XML tree. Describe() keeps the two diagnoses apart, because
                // telling an operator with an incomplete install to check their ACLs is a warning that
                // misdescribes what happened, and that is worse than no warning.
                Cli.CliAuditHost.AttachToParentConsole();
                if (seeding.NeedsAttention)
                    foreach (var line in seeding.Describe()) Console.Error.WriteLine(line);
                if (docSeeding.NeedsAttention)
                    foreach (var line in docSeeding.Describe()) Console.Error.WriteLine(line);
                if (bpSeeding.NeedsAttention)
                    foreach (var line in bpSeeding.Describe()) Console.Error.WriteLine(line);
            }

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
        /// import mode, so advertising one would be a promise the binary does not keep.
        /// <para>Internal rather than private so the test suite grades the SHIPPED string: this
        /// text sent the operator to a LAN address for as long as the app published one its own
        /// admission boundary refuses, which is the lockout this lane exists to remove. A claim
        /// about what the app tells its operator belongs on the string the operator is handed, not
        /// on a copy of it in a test. See LocalBrowserAdminAccessTests, which also lints this file:
        /// quoting the old wording here, even to explain it, turns that cell red.</para></summary>
        internal static readonly string Usage = """
SQLTriage — SQL Server health and audit.

Usage:
  SQLTriage.exe                       Start the desktop application.
  SQLTriage.exe --server              Headless Kestrel; browse http://localhost:<port> on this machine.
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
