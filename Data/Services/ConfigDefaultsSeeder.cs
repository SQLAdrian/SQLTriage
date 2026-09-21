/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SQLTriage.Data.Services
{
    /// <summary>What the seeder did to one shipped default file.</summary>
    public enum SeedAction
    {
        /// <summary>The file was not in the target folder, so the shipped default was copied there.
        /// (The target is <c>config\</c>, <c>docs\</c> or <c>BPScripts\</c> depending on the pass.)</summary>
        Seeded,

        /// <summary>A file of that name was already in the target folder. Left exactly as it was.</summary>
        Kept,

        /// <summary>
        /// A file of that name was already there, its bytes matched a revision this product is known to
        /// have shipped, and it was therefore an untouched stock file of an OLDER revision. Replaced
        /// with the current default. Only the BPScripts pass can produce this — it is the one pass given
        /// a <see cref="ShippedBPScriptRevisions"/> oracle, because it is the one pass whose shipped
        /// files are improved over time.
        ///
        /// <para><b>This is the only action that overwrites, and it is reachable only for a file whose
        /// exact bytes are in the known-shipped set.</b> An unknown hash, an unknown filename and an
        /// unreadable file all classify as <see cref="Kept"/>. The failure mode is a missed improvement,
        /// never a destroyed edit.</para>
        /// </summary>
        Refreshed,

        /// <summary>The copy was attempted and failed. The app will run without that file.</summary>
        Failed,

        /// <summary>
        /// <b>THE PAYLOAD DID NOT CARRY IT.</b> This build PROMISED the file — it has a
        /// <c>config.default/</c> TargetPath in <c>SQLTriage.csproj</c> and no
        /// <c>PayloadDefaultAbsentReason</c> — and it is not in the shipped defaults folder, so there was
        /// nothing to copy. Added 2026-09-11 (lane seeder-stub-freeze).
        ///
        /// <para><b>It is a SEPARATE action from <see cref="Failed"/> on purpose, and the reason is the
        /// remedy.</b> Failed means the copy was attempted and the folder would not take it — a
        /// permissions problem, which is what <see cref="SeedResult.Describe"/> tells the operator for
        /// that class. This one is an INCOMPLETE PAYLOAD: nothing was attempted and no permission will
        /// fix it. Reporting it as Failed would have printed "This is a FOLDER PERMISSION problem, not a
        /// corrupt install" over a corrupt install, which is the exact defect class — a warning that
        /// misdescribes what happened is worse than no warning.</para>
        ///
        /// <para>Before this existed the seeder could not notice: it enumerates what IS in the defaults
        /// folder, so <b>it cannot fail to copy a file it never looked for</b>. See
        /// <see cref="ShippedConfigDefaults"/> for where the expected set comes from.</para>
        /// </summary>
        MissingFromPayload
    }

    /// <summary>One shipped default and what happened to it.</summary>
    public sealed record SeedEntry(string RelativePath, SeedAction Action, string? Reason = null);

    /// <summary>The outcome of one seeding pass. Everything here is measured, not assumed.</summary>
    public sealed class SeedResult
    {
        /// <summary>The folder the shipped defaults were read from, or null when there was none.</summary>
        public string? DefaultsFolder { get; init; }

        /// <summary>The folder defaults are seeded INTO — the runtime folder for this pass
        /// (<c>config\</c>, <c>docs\</c> or <c>BPScripts\</c>).</summary>
        public string? TargetFolder { get; init; }

        /// <summary>
        /// What the operator actually LOSES when a file in this pass could not be created, in their
        /// words, set by the call that knows which pass this was. It is a property rather than a
        /// sentence in <see cref="Describe"/> because the two passes have two different consequences
        /// and a generic line would be true of neither: a missing <c>config\appsettings.json</c> stops
        /// a headless start dead, and a missing compliance document is a dangling pointer in four
        /// audit banners. Empty when nothing failed.
        /// </summary>
        public IReadOnlyList<string> FailureConsequence { get; init; } = Array.Empty<string>();

        /// <summary>
        /// What the operator loses when a file this build PROMISED was never in the payload — computed
        /// from <see cref="MissingFromPayload"/> by the same per-pass consequence function that fills
        /// <see cref="FailureConsequence"/>, and kept SEPARATE from it because the two have different
        /// causes and different remedies. Empty when the payload was complete.
        /// </summary>
        public IReadOnlyList<string> IncompleteConsequence { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Shipped defaults this build promised AND declared absent in the same breath, by a
        /// <c>PayloadDefaultAbsentReason</c> on the csproj item. <b>Accounted for, so not an attention
        /// entry</b> — the whole point of the declaration is that somebody decided it on purpose. Listed
        /// in <see cref="Describe"/> whenever the transcript prints, so an audit can see the exceptions
        /// without reading the build.
        /// </summary>
        public IReadOnlyList<string> DeclaredAbsentDefaults { get; init; } = Array.Empty<string>();

        public IReadOnlyList<SeedEntry> Entries { get; init; } = Array.Empty<SeedEntry>();

        /// <summary>True when this build ships no defaults folder for this pass at all (a dev tree, a
        /// payload that predates the split, or - for the BPScripts pass - a community build, which
        /// removes the scripts entirely). Not an error: there is simply nothing to seed.</summary>
        public bool NoDefaultsShipped => DefaultsFolder is null;

        public IReadOnlyList<SeedEntry> Seeded => Entries.Where(e => e.Action == SeedAction.Seeded).ToList();
        public IReadOnlyList<SeedEntry> Kept   => Entries.Where(e => e.Action == SeedAction.Kept).ToList();
        public IReadOnlyList<SeedEntry> Failed => Entries.Where(e => e.Action == SeedAction.Failed).ToList();

        /// <summary>Stock files replaced with a newer shipped revision. Separate from
        /// <see cref="Seeded"/> because "we created something that was missing" and "we replaced
        /// something that was there" are two different facts and the operator is owed both.</summary>
        public IReadOnlyList<SeedEntry> Refreshed => Entries.Where(e => e.Action == SeedAction.Refreshed).ToList();

        /// <summary>Shipped defaults this build promised and the payload did not carry. See
        /// <see cref="SeedAction.MissingFromPayload"/> for why these are not <see cref="Failed"/>.</summary>
        public IReadOnlyList<SeedEntry> MissingFromPayload =>
            Entries.Where(e => e.Action == SeedAction.MissingFromPayload).ToList();

        /// <summary>
        /// True when the operator must be told. An unwritable config folder is the dangerous case:
        /// every reader in the app resolves its own path and falls back to built-in defaults when the
        /// file is absent, so a silent failure here reads to the operator as "the app is configured"
        /// when nothing of theirs is being read at all.
        ///
        /// <para><b>AN INCOMPLETE PAYLOAD IS THE SAME DANGER BY A DIFFERENT ROUTE</b> (added 2026-09-11,
        /// lane seeder-stub-freeze) and must set this too. It is arguably the worse of the two: a
        /// permissions failure at least leaves the shipped default readable beside the app, while a file
        /// that never shipped cannot be recovered from the install at all. It was silent until this lane
        /// because nothing knew what the payload was supposed to contain.</para>
        /// </summary>
        public bool NeedsAttention => Failed.Count > 0 || MissingFromPayload.Count > 0;

        /// <summary>
        /// A human-readable account of what happened, for stderr. Split deliberately: what was
        /// COPIED, what was LEFT ALONE, and what FAILED are three different facts and never merged.
        /// </summary>
        public IReadOnlyList<string> Describe()
        {
            var lines = new List<string>();
            if (NoDefaultsShipped)
            {
                // NOT "config.default" any more. This line is shared by all three passes, so naming one
                // of them made it FALSE for the other two - a community build has no BPScripts.default\
                // at all and would have been told the config defaults were missing. Pre-existing since
                // the docs pass joined on 2026-09-10; corrected 2026-09-11. The folder cannot be named
                // here because DefaultsFolder is null in exactly this case.
                lines.Add("SQLTriage: this build ships no defaults folder for that pass - nothing to seed.");
                // ⚠ NOT AN UNCONDITIONAL EARLY RETURN ANY MORE (2026-09-11, lane seeder-stub-freeze).
                // "nothing to seed" is the whole truth only when nothing was PROMISED. A build that
                // promised seven shipped defaults and carries no defaults folder is a broken payload, and
                // saying "nothing to seed" to that operator would be the friendliest possible way to hide
                // it. The entries say which files, so the line below names the shape rather than repeating
                // them.
                if (MissingFromPayload.Count > 0)
                {
                    lines.Add($"  THE WHOLE SHIPPED-DEFAULTS FOLDER IS ABSENT, and this build promised"
                              + $" {MissingFromPayload.Count} file(s) in it.");
                    lines.AddRange(DescribeIncompletePayload());
                }
                lines.AddRange(DescribeDeclaredAbsent());
                return lines;
            }

            lines.Add($"SQLTriage first-run configuration seeding: {DefaultsFolder} -> {TargetFolder}");
            foreach (var e in Seeded)    lines.Add($"  created (was absent) : {e.RelativePath}");
            foreach (var e in Refreshed) lines.Add($"  refreshed (was stock): {e.RelativePath} - {e.Reason}");
            foreach (var e in Kept)      lines.Add($"  left untouched       : {e.RelativePath}");
            foreach (var e in Failed)    lines.Add($"  COULD NOT CREATE     : {e.RelativePath} - {e.Reason}");
            foreach (var e in MissingFromPayload)
                                         lines.Add($"  NOT IN THE PAYLOAD   : {e.RelativePath} - {e.Reason}");

            if (Failed.Count > 0)
            {
                lines.Add("");
                lines.Add("  WARNING. The files listed as COULD NOT CREATE are not present in");
                lines.Add($"  {TargetFolder} and could not be placed there.");
                lines.Add("  This is a FOLDER PERMISSION problem, not a corrupt install: the account");
                lines.Add("  SQLTriage runs as cannot write to that folder.");
                foreach (var line in FailureConsequence) lines.Add("  " + line);
                lines.Add("  Grant that account write access to the folder and start again, or copy the");
                lines.Add($"  files across by hand from {DefaultsFolder}.");
                // NOT unconditional any more. Once the BPScripts pass can refresh a stock file, "nothing
                // has been changed" is false whenever Refreshed is non-empty, and a warning that
                // misdescribes what just happened to the operator's folder is worse than no warning.
                lines.Add(Refreshed.Count == 0
                    ? "  Nothing already in that folder has been changed."
                    : $"  Nothing you authored has been changed. {Refreshed.Count} unmodified stock file(s)"
                      + " were refreshed, listed above.");
            }

            lines.AddRange(DescribeIncompletePayload());
            lines.AddRange(DescribeDeclaredAbsent());
            return lines;
        }

        /// <summary>
        /// The operator-facing account of an INCOMPLETE PAYLOAD. Deliberately does not reuse one word of
        /// the <see cref="Failed"/> block: that one says "This is a FOLDER PERMISSION problem, not a
        /// corrupt install", and printing it here would name the wrong cause and send the operator to
        /// fix an ACL that is perfectly fine. A corrupt install is exactly what this is.
        /// </summary>
        private IReadOnlyList<string> DescribeIncompletePayload()
        {
            if (MissingFromPayload.Count == 0) return Array.Empty<string>();

            var lines = new List<string>
            {
                "",
                "  WARNING. THIS INSTALL IS INCOMPLETE. The files listed as NOT IN THE PAYLOAD are",
                "  files this build of SQLTriage is supposed to ship, and they are not in",
                $"  {DefaultsFolder ?? "the shipped defaults folder"}, so there was nothing to copy.",
                "  THIS IS NOT A PERMISSION PROBLEM and no change to the folder's access will fix it:",
                "  the file was never delivered. Do not hand-create it - re-extract the release archive",
                "  over this folder, or reinstall, and report the build number if it happens again.",
            };
            foreach (var line in IncompleteConsequence) lines.Add("  " + line);
            lines.Add("  Nothing you authored has been changed or deleted by this.");
            return lines;
        }

        /// <summary>
        /// The declared exceptions, so the accounting is complete in the transcript and not only in this
        /// object. Informational: each of these was promised and declared absent in the same breath.
        /// </summary>
        private IReadOnlyList<string> DescribeDeclaredAbsent()
        {
            if (DeclaredAbsentDefaults.Count == 0) return Array.Empty<string>();

            var lines = new List<string> { "" };
            foreach (var name in DeclaredAbsentDefaults)
                lines.Add($"  declared absent by the build, not a fault: {name}");
            return lines;
        }
    }

    /// <summary>
    /// Seeds the runtime <c>config\</c> folder from the shipped <c>config.default\</c> folder, once,
    /// per file, at process start.
    ///
    /// <para>WHY THIS EXISTS. Before 2026-09-10 the payload shipped the operator-editable config files
    /// straight into <c>config\</c>. Every safe route into an install (the Inno installer's
    /// <c>onlyifdoesntexist</c> flags, and the deploy script's refusal to copy <c>config</c>) then had to
    /// carry the "do not overwrite" rule itself, and any route that did NOT - most obviously an operator
    /// hand-extracting the zip over the install directory, which is what the customer update path had
    /// always been - silently replaced their tuning with ours. The ruling of 2026-09-10 (DECISIONS,
    /// Adrian) was to make the artefact itself harmless: the shipped copy is now a DEFAULT that lives in
    /// a folder no upgrade route writes over, and the real file is created here, on first run, only when
    /// it is not already there.</para>
    ///
    /// <para>WHY IT IS ONE EXPLICIT STEP AND NOT A CHOKEPOINT. There is no single config-reading service
    /// to hook: roughly forty call sites independently resolve
    /// <c>Path.Combine(AppContext.BaseDirectory, "Config", "&lt;file&gt;.json")</c> for themselves
    /// (AdminAuthService, AlertDefinitionService, DashboardConfigService, DiagnosticScriptRunner,
    /// AlertingService, the two CLI arg parsers, LicenseService, ProductionReadinessGate, ...). Routing
    /// them through one accessor is a different, much larger change. So this runs FIRST, from
    /// <c>Program.Main</c>, ahead of every entry path - WPF, <c>--service</c>/<c>--server</c>,
    /// <c>--audit</c>, <c>--import</c>, and the <c>--help</c>/<c>--version</c> short-circuit - and the
    /// forty readers are left exactly as they are, resolving a folder that is now populated.</para>
    ///
    /// <para>WHAT IT SEEDS. Three pairs, by the same rule: <c>config.default\</c> into
    /// <c>config\</c>; (from 2026-09-10 round two) <c>docs.default\</c> into <c>docs\</c>; and (from
    /// 2026-09-11) <c>BPScripts.default\</c> into <c>BPScripts\</c>.
    /// The compliance pack joined the second pair because its four documents are filled in by the
    /// operator - <c>sign-off-log.md</c> is an append-only evidence record auditors read - so
    /// refreshing them on every update destroyed a signed record. The pristine templates ship in
    /// <c>docs.default\</c>, which nobody authors in, and are copied across only when the real
    /// file is absent.</para>
    ///
    /// <para>THE THIRD PAIR IS THE SAME MISTAKE A THIRD TIME, and it was the last folder of its class.
    /// <c>Pages\BestPractice.razor:87</c> gives the operator a "Save Script" button and
    /// <c>Data\BPScriptService.cs:67-71</c> writes it straight into <c>&lt;install&gt;\BPScripts</c>,
    /// yet the payload shipped <c>BPScripts\</c> and every update route copied over it.
    /// ⚠ <b>TWO ROUTES PUT OPERATOR WORK IN THAT FOLDER, and an earlier draft of this comment named the
    /// wrong one.</b> The Save Script button EDITS the content of a script that is already there — it
    /// passes <c>_editingScript.FileName</c> (<c>Pages\BestPractice.razor:216</c>) and
    /// <c>_editingScript</c> is assigned only at <c>:209</c> from an entry of <c>_config.Scripts</c>, so
    /// the page offers no filename box and no new-script action and the button <b>cannot name a file</b>.
    /// Operator FILENAMES arrive by the other route: a file dropped into the folder by hand, picked up
    /// by <c>Sync Scripts from Folder</c> (<c>:41</c> → <c>Data\BPScriptService.cs:73</c>, which
    /// enumerates <c>*.sql</c>). The conclusion is unchanged and is what matters here — the folder holds
    /// files whose names we do not choose, so no allow-list of filenames could ever have protected it —
    /// but the route is the drop-plus-sync one, not the button. A per-route
    /// exclusion could not fix it, because the applier every shipped install runs is a wholesale
    /// <c>robocopy %SRC% "&lt;appDir&gt;" /E /IS /IT</c> with no <c>/XF</c> and no <c>/XD</c>
    /// (<c>Data\AutoUpdateService.cs:740</c>). So the PAYLOAD changed instead: the stock scripts ship
    /// as <c>BPScripts.default\</c> and nothing delivers <c>BPScripts\</c> at all. Ruled by Adrian
    /// 2026-09-10 (DECISIONS, widget): SEED ONCE, keep the button.</para>
    ///
    /// <para>⚠ <b>AND THEN RULED AGAIN ON 2026-09-11, because seed-once alone had a consequence nobody
    /// had measured.</b> The cold gate checked the population: an existing install already holds all
    /// eleven stock scripts, so create-if-absent Keeps every one of them and freezes <b>every install</b>
    /// at its installed revision — not only the ones where somebody edited something. Adrian ruled to fix
    /// it properly before merge. The scripts pass now REFRESHES a file whose bytes it can prove are a
    /// revision this product shipped (<see cref="ShippedBPScriptRevisions"/>) and keeps everything else.
    /// His original trade is untouched: an edited script is still never replaced.</para>
    ///
    /// <para>WHAT IT WILL NOT DO. It never deletes. It never overwrites a file it cannot prove is our
    /// own unmodified stock — for <c>config\</c> and <c>docs\</c> that means it never overwrites at all,
    /// because those two passes are given no oracle to prove it with. It never touches anything that
    /// is not a name shipped in <c>config.default\</c>, so an operator's own files in <c>config\</c> -
    /// <c>.seat-register-key</c>, <c>.sqlite-cipher-key</c>, <c>portal-settings.json</c>,
    /// <c>server-connections.json</c> - are outside its reach by construction: they have no shipped
    /// default and are therefore never enumerated.</para>
    /// </summary>
    public static class ConfigDefaultsSeeder
    {
        /// <summary>The shipped-defaults folder name, as it appears in the publish payload.</summary>
        public const string DefaultsFolderName = "config.default";

        /// <summary>
        /// The shipped compliance pack, added 2026-09-10 (round two of lane customer-update-path).
        /// The four documents under <c>docs\compliance\</c> are FILLED IN BY THE OPERATOR —
        /// <c>sign-off-log.md</c> describes itself as an append-only evidence record auditors read,
        /// and two of the others ship with <c>{{placeholder}}</c> values the buyer replaces — so
        /// they are operator state by exactly the test the config split uses, and the first attempt at
        /// this lane was wrong to refresh them on every update. The pristine templates now ship here
        /// and the real ones are created by the same seed-once rule.
        /// </summary>
        public const string DocsDefaultsFolderName = "docs.default";

        /// <summary>The runtime docs folder. Casing follows the pointers the app shows the operator
        /// (<c>docs/compliance/incident-response-runbook.md</c>, from four audit banners).</summary>
        public const string DocsFolderName = "docs";

        /// <summary>
        /// The runtime config folder name. Windows is case-insensitive, and the app's readers try
        /// <c>Config</c> then <c>config</c>; an existing folder of either casing is reused rather than
        /// a second one created beside it.
        /// </summary>
        public const string ConfigFolderName = "Config";

        /// <summary>
        /// The shipped Best Practice scripts, added 2026-09-11 (lane bpscripts-operator-edits-are-lost).
        /// The operator's work lands in <c>BPScripts\</c> by TWO routes and only the second can name a
        /// file: <c>Pages\BestPractice.razor:87</c>'s Save Script button EDITS the content of a script
        /// already listed (it passes <c>_editingScript.FileName</c>, and there is no filename box on that
        /// page), while <c>Sync Scripts from Folder</c> at <c>:41</c> adopts whatever <c>*.sql</c> the
        /// operator dropped in by hand. The names are therefore theirs, which is why no allow-list of
        /// filenames can protect the folder. Shipping the stock scripts here instead of into
        /// <c>BPScripts\</c> is what makes the folder unreachable by every update route at once.
        /// </summary>
        public const string BPScriptsDefaultsFolderName = "BPScripts.default";

        /// <summary>
        /// The runtime Best Practice scripts folder. Casing follows the path the app resolves for itself:
        /// <c>Data\BPScriptService.cs:27</c>, <c>Data\Services\ProductionReadinessGate.cs:95</c> and
        /// <c>Pages\ScheduledTasks.razor:147</c> all resolve <c>BPScripts</c> under the base directory,
        /// and the last of those reads <c>01. MaintenanceSolution.sql</c> out of it to deploy Ola
        /// Hallengren's maintenance solution - so a pass that failed to seed is not cosmetic.
        /// </summary>
        public const string BPScriptsFolderName = "BPScripts";

        /// <summary>The last config pass's result, so a host can log it once its logger exists.</summary>
        public static SeedResult? LastResult { get; private set; }

        /// <summary>The last docs pass's result. Separate because it is a separate measurement.</summary>
        public static SeedResult? LastDocsResult { get; private set; }

        /// <summary>The last Best Practice scripts pass's result. Separate, for the same reason.</summary>
        public static SeedResult? LastBPScriptsResult { get; private set; }

        /// <summary>Seed the config folder from the executable's own directory. The normal entry point.</summary>
        public static SeedResult Run()
        {
            var result = Seed(AppContext.BaseDirectory);
            LastResult = result;
            return result;
        }

        /// <summary>Seed the docs folder from the executable's own directory. Program.Main calls both.</summary>
        public static SeedResult RunDocs()
        {
            var result = SeedDocs(AppContext.BaseDirectory);
            LastDocsResult = result;
            return result;
        }

        /// <summary>Seed the Best Practice scripts folder from the executable's own directory.
        /// Program.Main calls all three.</summary>
        public static SeedResult RunBPScripts()
        {
            var result = SeedBPScripts(AppContext.BaseDirectory);
            LastBPScriptsResult = result;
            return result;
        }

        /// <summary>
        /// Seed <paramref name="baseDirectory"/>'s config folder from its shipped defaults. Pure with
        /// respect to process state - takes the root, touches only what is under it - so the tests
        /// exercise the production method against a temp tree rather than a copy of its logic.
        /// </summary>
        public static SeedResult Seed(string baseDirectory) =>
            Seed(baseDirectory, ShippedConfigDefaults.PromisedFileNames, ShippedConfigDefaults.DeclaredAbsentFileNames);

        /// <summary>
        /// The same pass with the PROMISED set supplied explicitly. <b>This overload exists so that a
        /// test on a toy payload says in its own words what that payload promises</b>, instead of
        /// inheriting the real build's seven-file promise and being told about six files it was never
        /// pretending to carry. Production always goes through
        /// <see cref="Seed(string)"/>, which supplies <see cref="ShippedConfigDefaults"/>.
        ///
        /// <para><b>A caller that passes an EMPTY promise set gets the old, silent behaviour</b> - which
        /// is correct for a tree that promises nothing and is the reason this is internal rather than
        /// public. It must never become the production path: the pin that a real build's promise reaches
        /// the production call is
        /// <c>ShippedConfigDefaultsManifestTests.The_production_seed_entry_point_carries_the_builds_own_promise</c>.</para>
        /// </summary>
        internal static SeedResult Seed(string baseDirectory,
                                        IReadOnlyCollection<string> promisedFileNames,
                                        IReadOnlyCollection<string>? declaredAbsentFileNames = null) =>
            SeedPair(baseDirectory, DefaultsFolderName, ConfigFolderName, ConfigConsequence,
                     promisedFileNames: promisedFileNames,
                     declaredAbsentFileNames: declaredAbsentFileNames);

        /// <summary>
        /// Seed <paramref name="baseDirectory"/>'s <c>docs\</c> folder from its shipped
        /// <c>docs.default\</c>. Same rule, same guarantees, different folder: an operator's filled-in
        /// compliance pack is never replaced, and a document they deleted comes back.
        /// </summary>
        public static SeedResult SeedDocs(string baseDirectory) =>
            SeedPair(baseDirectory, DocsDefaultsFolderName, DocsFolderName, DocsConsequence);

        /// <summary>
        /// Seed <paramref name="baseDirectory"/>'s <c>BPScripts\</c> folder from its shipped
        /// <c>BPScripts.default\</c>. A script the operator wrote or edited is never replaced, and a
        /// stock script they deleted comes back.
        ///
        /// <para><b>THIS IS THE ONE PASS THAT ALSO REFRESHES, and the reason is a defect seed-once alone
        /// introduced.</b> Measured 2026-09-11 on the live install: <c>C:\SQLTriage-Service\BPScripts\</c>
        /// already holds all eleven stock scripts, so a pure create-if-absent rule finds every one of
        /// them present and Keeps it — freezing <b>every existing install</b> at its installed revision
        /// forever, not just the installs where somebody edited something. That is not the trade Adrian
        /// ruled for; it is a side effect that would have shipped silently. Improvements demonstrably do
        /// reach installs today — that same folder's <c>Install-All-Scripts.sql</c> is dated 26 Aug
        /// against 10 Jul for the other ten, because the wholesale robocopy carried the First Responder
        /// Kit 8.34 refresh into it. Adrian ruled on 2026-09-11 to fix it properly before merging.</para>
        ///
        /// <para>So this pass is handed <see cref="IsUntouchedStockScript"/>, an oracle over
        /// <see cref="ShippedBPScriptRevisions"/>, and each existing file is classified rather than
        /// assumed: bytes we are known to have shipped are refreshed to the current default; anything
        /// else is the operator's and is kept. <b>Read that type's remarks before changing this</b> — the
        /// obvious "compare against the current default" rule is wrong on exactly the population this
        /// exists for, and the hashes are of the CRLF-delivered bytes rather than the git blob.</para>
        ///
        /// <para><b>THE TRADE ADRIAN ACTUALLY RULED FOR IS UNCHANGED.</b> An operator who has edited a
        /// stock script still never receives an improved version of it: their bytes are not in the
        /// known-shipped set, so it is Kept. The current revision stays readable beside it in
        /// <c>BPScripts.default\</c> and they merge it themselves.</para>
        /// </summary>
        public static SeedResult SeedBPScripts(string baseDirectory) =>
            SeedPair(baseDirectory, BPScriptsDefaultsFolderName, BPScriptsFolderName, BPScriptsConsequence,
                     IsUntouchedStockScript);

        /// <summary>
        /// THE ORACLE THAT SEPARATES "the operator edited this" FROM "we shipped a newer one", and the
        /// reason the BPScripts pass is the only one that refreshes. It is deliberately narrow:
        ///
        /// <list type="bullet">
        /// <item>a relative path with a directory separator is NOT classifiable — the manifest keys on
        /// bare filenames, <c>BPScripts.default\</c> carries no subfolders today, and a nested file
        /// appearing later must fall to Kept rather than be matched by its leaf name;</item>
        /// <item>everything else defers to <see cref="ShippedBPScriptRevisions.IsKnownShipped"/>, which
        /// answers false for an unknown hash, an unknown filename and a null hash alike.</item>
        /// </list>
        ///
        /// <para>Every false answer means KEEP. There is no input to this method that can destroy an
        /// operator's file, which is the property the whole design exists to hold.</para>
        /// </summary>
        private static bool IsUntouchedStockScript(string relativePath, string? hash)
        {
            if (relativePath.Contains(Path.DirectorySeparatorChar) ||
                relativePath.Contains(Path.AltDirectorySeparatorChar))
                return false;

            return ShippedBPScriptRevisions.IsKnownShipped(relativePath, hash);
        }

        /// <summary>
        /// PROVED 2026-09-10 by the cold gate, on an install with an unseeded config folder:
        /// <c>--server</c> does not fall back, it dies. Data\Services\WindowsServiceHost.cs loads
        /// <c>config/appsettings.json</c> with <c>optional: false</c>, so the process throws
        /// FileNotFoundException before it binds a port. The message this replaces said "SQLTriage will
        /// run on built-in defaults for each of them", which pointed a headless operator away from the
        /// one thing that was wrong — the folder's permissions.
        /// </summary>
        private static IReadOnlyList<string> ConfigConsequence(IReadOnlyList<SeedEntry> failed)
        {
            var appsettings = failed.Any(e =>
                string.Equals(Path.GetFileName(e.RelativePath), "appsettings.json", StringComparison.OrdinalIgnoreCase));

            if (appsettings)
            {
                return new[]
                {
                    "A HEADLESS START WILL NOT SURVIVE THIS. --server and --service load",
                    "config/appsettings.json as a REQUIRED file, so the process exits with",
                    "FileNotFoundException before it binds a port. The desktop app still starts.",
                };
            }

            var lines = new List<string>
            {
                "Each reader of one of those files falls back to a value built into the",
                "binary. That is not the shipped configuration and it is not yours.",
            };

            // ⚠ THE ONE EXCEPTION, and it is new on 2026-09-11 (lane seeder-stub-freeze). The sentence
            // above was true of every file in this set until DashboardConfigService was changed to fall
            // back to the dashboard config EMBEDDED IN THE ASSEMBLY - the same 27-dashboard artefact the
            // payload ships - instead of DefaultConfigGenerator's three-dashboard stub. For that one file
            // the fallback IS the shipped configuration, and saying otherwise would send an operator
            // hunting a layout problem they do not have. It is still not THEIRS, which is the half that
            // matters if they had tuned it.
            if (failed.Any(e => string.Equals(Path.GetFileName(e.RelativePath), "dashboard-config.json",
                                              StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add("dashboard-config.json is the exception: the dashboards fall back to the");
                lines.Add("config embedded in this build, which IS the shipped layout - but any");
                lines.Add("dashboard tuning of your own that was in that file is not being read.");
            }

            return lines;
        }

        /// <summary>Pages\AuditLogViewer.razor sends the operator to
        /// <c>docs/compliance/incident-response-runbook.md</c> from four banners, and the report body
        /// of a chain verification names it too. A document that could not be placed is a pointer to
        /// nothing at the moment it matters most.</summary>
        private static IReadOnlyList<string> DocsConsequence(IReadOnlyList<SeedEntry> failed) => new[]
        {
            "The app points operators at docs/compliance/incident-response-runbook.md",
            "from four audit-chain banners. Those pointers now name a file that is not",
            "there. Nothing else in the application is affected.",
        };

        /// <summary>Pages\ScheduledTasks.razor:147 reads
        /// <c>BPScripts\01. MaintenanceSolution.sql</c> off disk to deploy Ola Hallengren's maintenance
        /// solution to a server, and Pages\BestPractice.razor lists the folder as the operator's script
        /// library. A script that could not be placed is a Deploy button that reports "Deployment script
        /// not found" - and an empty Best Practice page on a fresh install.</summary>
        private static IReadOnlyList<string> BPScriptsConsequence(IReadOnlyList<SeedEntry> failed) => new[]
        {
            "The Best Practice page lists the scripts in BPScripts\\, and Scheduled Tasks",
            "deploys BPScripts\\01. MaintenanceSolution.sql to a server. A script that is not",
            "there cannot be run or deployed. Nothing you authored has been touched.",
        };

        /// <summary>
        /// THE ONE PLACE THAT DECIDES, for all three pairs. Create-if-absent, per FILE, recursive, and
        /// never a delete.
        ///
        /// <para><b>ONE PASS ALSO REFRESHES, and it is opt-in by construction.</b> A pass that supplies
        /// <paramref name="isKnownShipped"/> may replace an existing file, but ONLY when that oracle
        /// proves the bytes on disk are a revision this product itself shipped. <c>config\</c> and
        /// <c>docs\</c> pass null and are therefore create-if-absent exactly as before — no hashing, no
        /// overwrite, not one changed line of behaviour. <c>BPScripts\</c> passes
        /// <see cref="IsUntouchedStockScript"/>, because it is the only pair whose shipped files are
        /// improved over time and whose installs would otherwise be frozen at their installed revision
        /// forever. <b>Adding a fourth pair defaults to seed-once; refreshing is a decision somebody has
        /// to make on purpose.</b></para>
        ///
        /// <para><b>THE INVARIANT THIS EXISTS TO KEEP: no folder the application writes to at runtime
        /// may sit on the update overwrite path.</b> Every folder the operator can author in ships as a
        /// <c>&lt;name&gt;.default\</c> sibling and is seeded here, so the payload carries nothing that
        /// any update route could copy over their work. Adding a fourth pair is the right way to add a
        /// fourth such folder; adding an exclusion to a copy list is not, because the applier every
        /// shipped install runs is a wholesale robocopy that honours no list
        /// (<c>Data\AutoUpdateService.cs:740</c>).</para>
        ///
        /// <para>The standing pin is
        /// <c>Tests\SQLTriage.Tests\RuntimeWriteFolderCensusTests.cs</c>,
        /// <c>No_folder_the_app_writes_into_is_on_the_update_overwrite_path</c> - it enumerates the
        /// writers FROM THE SOURCE and goes red when a folder production code writes into appears in the
        /// deploy script's <c>$copyItems</c> without a recorded reason. That test name is real and was
        /// checked to exist when this comment was written. The cautionary tale is
        /// <c>AutoUpdateService.cs</c>, which until 2026-09-10 cited "ProtectedConfigFilesMatchTheUpdater"
        /// - a test that existed nowhere in the repo. It was CORRECTED that day and now cites
        /// <c>ProtectedConfigParityTests.cs:201 (Runtime_and_updater_protected_lists_are_identical)</c>,
        /// which resolves, and its own comment records the dead name. The PAST tense here is deliberate:
        /// the present-tense version of this sentence was itself stale from 2026-09-10 and was corrected
        /// 2026-09-12 after the seeder-stub-freeze builder measured it. The lesson is unchanged - the
        /// comment is not the guard, the test is, so name it by its REAL name and check it resolves.</para>
        /// </summary>
        private static SeedResult SeedPair(
            string baseDirectory,
            string defaultsFolderName,
            string targetFolderName,
            Func<IReadOnlyList<SeedEntry>, IReadOnlyList<string>> consequence,
            Func<string, string?, bool>? isKnownShipped = null,
            IReadOnlyCollection<string>? promisedFileNames = null,
            IReadOnlyCollection<string>? declaredAbsentFileNames = null)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return new SeedResult();

            var promised = promisedFileNames ?? Array.Empty<string>();
            var declaredAbsent = (declaredAbsentFileNames ?? Array.Empty<string>())
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            var defaults = Path.Combine(baseDirectory, defaultsFolderName);
            if (!Directory.Exists(defaults))
            {
                // ⚠ NO LONGER AN UNCONDITIONALLY EMPTY RESULT (2026-09-11, lane seeder-stub-freeze). When
                // nothing was promised this is still exactly what it was - a dev tree or a community build
                // with nothing to seed. When this build DID promise files, a missing defaults folder is
                // the loudest version of an incomplete payload and every promised file is unaccounted for.
                var wholeFolderMissing = promised
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Select(n => new SeedEntry(n, SeedAction.MissingFromPayload,
                                               defaultsFolderName + "\\ is not in this install at all"))
                    .ToList();

                return new SeedResult
                {
                    Entries = wholeFolderMissing,
                    DeclaredAbsentDefaults = declaredAbsent,
                    IncompleteConsequence = wholeFolderMissing.Count > 0
                        ? consequence(wholeFolderMissing)
                        : Array.Empty<string>()
                };
            }

            var target = ResolveTargetFolder(baseDirectory, targetFolderName);
            var entries = new List<SeedEntry>();

            // Recursive: config.default carries no subfolders today, but a schema or checks folder
            // moving in later must be seeded, not silently skipped.
            string[] sources;
            try
            {
                sources = Directory.GetFiles(defaults, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                var listFailure = new[] { new SeedEntry(defaultsFolderName, SeedAction.Failed, "could not be listed: " + ex.Message) };
                return new SeedResult
                {
                    DefaultsFolder = defaults,
                    TargetFolder = target,
                    Entries = listFailure,
                    DeclaredAbsentDefaults = declaredAbsent,
                    FailureConsequence = consequence(listFailure)
                };
                // ⚠ DELIBERATELY NO MissingFromPayload ENTRIES HERE. The folder could not be listed, so we
                // do not know what is in it. "Not in the payload" would be a claim about the world made
                // from a failure to measure it; the Failed entry above says what actually happened.
            }

            Array.Sort(sources, StringComparer.OrdinalIgnoreCase);

            // ── THE COMPLETENESS CENSUS ─────────────────────────────────────────────────────────
            // The half of the job Directory.GetFiles structurally cannot do. Everything below this point
            // acts on files that ARE here; this acts on the ones that should be and are not.
            // <paramref name="promisedFileNames"/> is derived from the BUILD (ShippedConfigDefaults,
            // generated by XPath over SQLTriage.csproj's XML tree), never from a list anybody keeps by
            // hand - a parity test between two hand-kept lists validates AGREEMENT, not COMPLETENESS, and
            // on 2026-09-10 two such lists were held identical by a good test while both were missing
            // power-pricing.json.
            var presentNames = new HashSet<string>(
                sources.Select(s => Path.GetFileName(s)!), StringComparer.OrdinalIgnoreCase);

            foreach (var name in promised.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (presentNames.Contains(name)) continue;
                entries.Add(new SeedEntry(name, SeedAction.MissingFromPayload,
                    "this build promised it in " + defaultsFolderName + "\\ and the payload does not carry it"));
            }

            foreach (var src in sources)
            {
                var relative = Path.GetRelativePath(defaults, src);
                var dst = Path.Combine(target, relative);

                if (File.Exists(dst))
                {
                    // SEED-ONCE unless this pass was given an oracle that can prove the file on disk is
                    // an untouched stock file. Without one - config\ and docs\ - the behaviour is
                    // exactly what it was: Kept, no hashing, no overwrite, no exception.
                    if (isKnownShipped is null)
                    {
                        entries.Add(new SeedEntry(relative, SeedAction.Kept));
                        continue;
                    }

                    var existingHash = ShippedBPScriptRevisions.TryHashFile(dst);

                    if (!isKnownShipped(relative, existingHash))
                    {
                        // Unknown bytes, unknown filename, or a file we could not read. All three are
                        // the operator's until proven otherwise. This is the fail-safe direction and it
                        // is the whole safety argument for the refresh.
                        entries.Add(new SeedEntry(relative, SeedAction.Kept,
                            existingHash is null ? "could not be read - kept" : "modified by the operator"));
                        continue;
                    }

                    var currentHash = ShippedBPScriptRevisions.TryHashFile(src);

                    if (currentHash is null || string.Equals(currentHash, existingHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Already the current revision, or we cannot read our own default. Either way
                        // there is nothing to gain from a write.
                        entries.Add(new SeedEntry(relative, SeedAction.Kept, "stock, already current"));
                        continue;
                    }

                    try
                    {
                        // The ONLY overwrite in this type, and it is reached only for a file whose exact
                        // bytes we shipped ourselves in an earlier revision.
                        File.Copy(src, dst, overwrite: true);
                        entries.Add(new SeedEntry(relative, SeedAction.Refreshed,
                            "stock " + existingHash![..12] + " -> " + currentHash[..12]));
                    }
                    catch (Exception ex)
                    {
                        // A refresh that fails leaves the operator with a working older stock file, so
                        // this is not the same severity as a seed that fails. Recorded as Kept with the
                        // reason rather than Failed, because the consequence text for Failed says the
                        // file is NOT THERE, and it is.
                        entries.Add(new SeedEntry(relative, SeedAction.Kept,
                            "refresh failed, older stock revision kept: " + ex.GetType().Name));
                    }

                    continue;
                }

                try
                {
                    var dstDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                    // overwrite:false is the guarantee, not the File.Exists above: if a second process
                    // wins the race between the check and the copy this throws rather than clobbering
                    // the file it just wrote, and the catch below records it as Kept.
                    File.Copy(src, dst, overwrite: false);
                    entries.Add(new SeedEntry(relative, SeedAction.Seeded));
                }
                catch (IOException) when (File.Exists(dst))
                {
                    entries.Add(new SeedEntry(relative, SeedAction.Kept, "appeared while seeding"));
                }
                catch (Exception ex)
                {
                    entries.Add(new SeedEntry(relative, SeedAction.Failed, ex.GetType().Name + ": " + ex.Message));
                }
            }

            var failed = entries.Where(e => e.Action == SeedAction.Failed).ToList();
            var missing = entries.Where(e => e.Action == SeedAction.MissingFromPayload).ToList();
            return new SeedResult
            {
                DefaultsFolder = defaults,
                TargetFolder = target,
                Entries = entries,
                DeclaredAbsentDefaults = declaredAbsent,
                FailureConsequence = failed.Count > 0 ? consequence(failed) : Array.Empty<string>(),
                // TWO CALLS, TWO LISTS, on purpose. A permissions failure and an undelivered file have
                // different remedies, so they get different consequence text even when the same function
                // computes both - and a test that asserts the permissions block does not make the headless
                // claim stays honest about the file it is actually testing.
                IncompleteConsequence = missing.Count > 0 ? consequence(missing) : Array.Empty<string>()
            };
        }

        /// <summary>
        /// The folder to seed into. An install that already has a <c>Config</c> or <c>config</c> folder
        /// keeps it, whatever its casing; a fresh extract gets <c>Config</c>.
        /// </summary>
        public static string ResolveConfigFolder(string baseDirectory) =>
            ResolveTargetFolder(baseDirectory, ConfigFolderName);

        /// <summary>
        /// The folder to seed into, for either pass. An install that already has a folder of that name
        /// keeps it whatever its casing; a fresh extract gets the canonical spelling.
        /// </summary>
        public static string ResolveTargetFolder(string baseDirectory, string folderName)
        {
            var canonical = Path.Combine(baseDirectory, folderName);
            if (Directory.Exists(canonical)) return canonical;

            try
            {
                foreach (var dir in Directory.GetDirectories(baseDirectory))
                {
                    if (string.Equals(Path.GetFileName(dir), folderName, StringComparison.OrdinalIgnoreCase))
                        return dir;
                }
            }
            catch { /* an unlistable base directory falls through to the canonical name */ }

            return canonical;
        }
    }
}
