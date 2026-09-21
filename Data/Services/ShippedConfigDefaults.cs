/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// <b>THE INVARIANT THIS EXISTS TO HOLD: if the shipped payload is incomplete, somebody is TOLD.
    /// Every file the payload is SUPPOSED to carry in <c>config.default\</c> is accounted for at run
    /// time, and one that is absent produces an attention entry rather than silence.</b>
    ///
    /// <para>WHY IT HAD TO EXIST (measured 2026-09-11, lane seeder-stub-freeze). Nothing in the product
    /// knew what the payload was supposed to contain. <see cref="ConfigDefaultsSeeder"/> enumerates what
    /// IS in <c>config.default\</c> — <c>Directory.GetFiles</c> — so <b>it cannot fail to copy a file it
    /// never looked for</b>. A shipped default missing from the payload produced no entry, no warning and
    /// nothing on stderr; the consuming service then fell back to a built-in default, and for
    /// <c>dashboard-config.json</c> it PERSISTED that fallback into <c>config\</c>, after which every
    /// later seeding pass found the file present and Kept it. A packaging slip became a permanent
    /// three-dashboard product where 27 ship.</para>
    ///
    /// <para><b>THE EXPECTED SET IS DERIVED FROM THE BUILD, READ AS A TREE — NEVER A HAND-KEPT LIST.</b>
    /// The <c>GenerateShippedConfigDefaultsManifest</c> target in <c>SQLTriage.csproj</c> runs an XPath 1.0
    /// query over that csproj AS AN XML TREE and embeds the result in this assembly; this class parses it.
    /// Read that target's remarks for why MSBuild item evaluation would have been vacuous here (a
    /// <c>Content Update</c> on an absent file creates NO ITEM, which is exactly the trigger case), why a
    /// character scan would have been a tripwire rather than a guarantee (RULED 2026-09-11, Adrian), and
    /// why the manifest is a RESOURCE rather than a generated source file (the WPF temporary-target-assembly
    /// pass does not run the target and cannot compile a generated partial). A parity test between two
    /// hand-kept lists validates AGREEMENT, not COMPLETENESS: on 2026-09-10 two such lists were held
    /// identical by a good test and both were missing <c>power-pricing.json</c>.</para>
    ///
    /// <para><b>ABSENT OR EMPTY IS A FAILURE, NEVER A SKIP.</b> <see cref="IsDerivable"/> is false when the
    /// manifest resource is missing, empty, or carries no promise — which the build errors before allowing,
    /// so it means the guard itself is broken rather than that nothing was promised. Callers must treat that
    /// as a failure: a completeness census whose expected set is empty passes everything, which is the
    /// direction that produced the 2026-09-11 IP-boundary defect (a verifier scanned an artefact it had not
    /// built and passed a 174 MB full build as community, exit 0).</para>
    ///
    /// <para>The pins are <c>Tests\SQLTriage.Tests\ShippedConfigDefaultsManifestTests.cs</c>, which
    /// re-derive the promise INDEPENDENTLY from the csproj's XML tree and compare — so a build in which the
    /// target did not re-run shows up as a disagreement rather than as two views of one stale answer.</para>
    /// </summary>
    internal static class ShippedConfigDefaults
    {
        /// <summary>The csproj TargetPath prefix every promised entry shares. The generator's XPath matches
        /// this literal with a FORWARD slash, and <see cref="NameOf"/> strips it.</summary>
        internal const string TargetPathPrefix = ConfigDefaultsSeeder.DefaultsFolderName + "/";

        /// <summary>The embedded manifest's logical name, as the csproj target assigns it. Resolved by
        /// suffix, matching the house convention in <c>DashboardConfigMigrator</c>.</summary>
        internal const string ManifestResourceSuffix = "shipped-config-defaults.manifest.txt";

        private const string PromisedPrefix = "promised:";
        private const string DeclaredAbsentPrefix = "declared-absent:";

        private static readonly Lazy<Manifest> Parsed = new(Read);

        /// <summary>Every <c>config.default/</c> TargetPath this build promised the payload would carry,
        /// exactly as the csproj spells it.</summary>
        internal static IReadOnlyList<string> PromisedTargetPaths => Parsed.Value.Promised;

        /// <summary>Every promised TargetPath the csproj ALSO declares a
        /// <c>PayloadDefaultAbsentReason</c> for — promised, and the build says in its own words why the
        /// payload does not carry it. Accounted for, so not an attention entry.</summary>
        internal static IReadOnlyList<string> DeclaredAbsentTargetPaths => Parsed.Value.DeclaredAbsent;

        /// <summary>The promised file names, which is the grain the seeder compares at.</summary>
        internal static IReadOnlyList<string> PromisedFileNames => Parsed.Value.PromisedNames;

        /// <summary>The declared-absent file names.</summary>
        internal static IReadOnlyList<string> DeclaredAbsentFileNames => Parsed.Value.DeclaredAbsentNames;

        /// <summary>
        /// False when the manifest could not be read or promised nothing. A BROKEN GUARD, not an empty
        /// promise — see the type remarks. <see cref="DerivationDetail"/> says which.
        /// </summary>
        internal static bool IsDerivable => Parsed.Value.Promised.Count > 0;

        /// <summary>How the manifest read went, in words, for a message an operator or a gate can act on.</summary>
        internal static string DerivationDetail => Parsed.Value.Detail;

        /// <summary>The file name in a <c>config.default/name.json</c> TargetPath.</summary>
        internal static string NameOf(string targetPath) =>
            Path.GetFileName(targetPath.Replace('\\', '/'));

        private sealed record Manifest(
            IReadOnlyList<string> Promised,
            IReadOnlyList<string> DeclaredAbsent,
            IReadOnlyList<string> PromisedNames,
            IReadOnlyList<string> DeclaredAbsentNames,
            string Detail);

        private static Manifest Read()
        {
            string? text;
            try
            {
                // The one reflection-over-resources reader already in this assembly, rather than an eighth
                // hand-rolled copy of the same loop. It resolves by suffix because MSBuild's logical-name
                // mangling is an implementation detail, and it strips a BOM.
                text = DashboardConfigMigrator.ReadResourceText(ManifestResourceSuffix);
            }
            catch (Exception ex)
            {
                return Empty("reading the embedded " + ManifestResourceSuffix + " threw " + ex.GetType().Name);
            }

            if (string.IsNullOrWhiteSpace(text))
                return Empty("this assembly carries no embedded " + ManifestResourceSuffix
                             + ", or it is empty. The GenerateShippedConfigDefaultsManifest target in "
                             + "SQLTriage.csproj did not run for this build.");

            var promised = Collect(text, PromisedPrefix);
            var absent = Collect(text, DeclaredAbsentPrefix);

            if (promised.Count == 0)
                return Empty("the embedded " + ManifestResourceSuffix + " carries no '" + PromisedPrefix
                             + "' line. The build is supposed to error before producing that, so the "
                             + "generator's XPath or the manifest format has changed.");

            return new Manifest(
                promised, absent,
                promised.Select(NameOf).ToList(),
                absent.Select(NameOf).ToList(),
                $"read {promised.Count} promised and {absent.Count} declared-absent entr(ies) from the "
                + "embedded manifest");
        }

        private static Manifest Empty(string detail) =>
            new(Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), detail);

        private static IReadOnlyList<string> Collect(string text, string prefix) =>
            text.Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
                .Select(l => l.Substring(prefix.Length).Trim().Replace('\\', '/'))
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
