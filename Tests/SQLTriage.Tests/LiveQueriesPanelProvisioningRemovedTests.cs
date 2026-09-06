/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The liveQueries panel-provisioning surface, deleted 2026-09-05, held deleted.
    ///
    /// <para><b>What was removed.</b> <c>Data/SQLiteTableService.cs</c> (529 lines) declared
    /// <c>liveQueriesTableService</c> and <c>ColumnInfo</c>. The service built one table per
    /// dashboard panel inside the encrypted local store <c>SQLTriage.db</c>, and it built them from
    /// the SHAPE of a result set returned by the monitored SQL Server: the table name came from the
    /// panel id and the column names were the result-set aliases the panel's query chose, each
    /// interpolated verbatim into <c>CREATE TABLE</c>, into <c>ALTER TABLE ADD COLUMN</c> and into
    /// the <c>INSERT</c> column list. Values were parameterised throughout and table names went
    /// through <c>SanitizeTableName</c>; column names went through nothing. It was registered as a
    /// singleton in all three hosts and App.xaml.cs ran it on a background task at desktop startup.
    /// The registrations and that startup block went with it.</para>
    ///
    /// <para><b>Why deleting beat sanitising.</b> Adrian's ruling, 2026-09-05. Nothing read the
    /// tables the service built, it had no UI, and it is a WRITE surface into a local store keyed on
    /// identifiers supplied by the audited server. The product's third brand pillar is read-only
    /// assessment with write surfaces opt-in, so the surface had no business existing whether or not
    /// its identifiers were quoted.</para>
    ///
    /// <para><b>What we proved before deleting, and what it does NOT excuse.</b> Driven live on
    /// 2026-09-05 against the pinned stack, the service never reached its own interpolation: the
    /// statement before it read <c>SELECT name FROM liveQueries_master ...</c>, and
    /// <c>liveQueries_master</c> is a typo for <c>sqlite_master</c> that has been in the tree since
    /// the initial commit (<c>c3e91cd</c>) — a casualty of the project-wide <c>Sqlite</c>
    /// -&gt; <c>liveQueries</c> rename. Every call died with
    /// <c>SQLite Error 1: 'no such table: liveQueries_master'</c>. That was proved twice: by
    /// invoking the private <c>CreateOrUpdateTableAsync</c> against a real encrypted store, and end
    /// to end through the public <c>ValidateAndCreateTableAsync</c> against the live
    /// <c>.\new2022</c> instance (MSI\NEW2022, 16.0.4262.2) with a hostile alias. So the defect
    /// shipped INERT. It was one corrected identifier away from live, and the same probe proved that
    /// one <c>ExecuteNonQuery</c> runs every statement in a CommandText on this stack, on encrypted
    /// connections included, and that each of the three real statement shapes DROPPED a table when
    /// handed a hostile alias. Inert is not safe; it is unexercised.</para>
    ///
    /// <para><b>Precedent.</b> Commit <c>ee55ca0</c> deleted two dead arbitrary-SQL methods from
    /// this same service and left a test that fails if either shape returns. This file replaces
    /// <c>LiveQueriesTableServiceSqlSurfaceTests.cs</c>, which pinned that surface by
    /// <c>typeof(liveQueriesTableService)</c> and cannot compile now the type is gone. The two names
    /// that file guarded are still guarded here, and wider: assembly-wide rather than on one type.</para>
    /// </summary>
    public class LiveQueriesPanelProvisioningRemovedTests
    {
        private const BindingFlags Everything =
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>The product assembly, reached through a type that is not the subject of any
        /// guard here, so a rename of the subject cannot quietly redirect the scan.</summary>
        private static Assembly ProductAssembly => typeof(SqliteCipherHelper).Assembly;

        // ── 1. The type, and the shape it would come back under ──────────────────────────────

        /// <summary>
        /// The service is gone from the product assembly, and so is anything that merely renames it.
        ///
        /// <para>Naming the type alone would be a weak guard: the whole reason this defect survived
        /// four years is a bulk rename. So the method names go with it. A type that declares
        /// <c>EnsureTablesForAllPanelsAsync</c> is the provisioning pass whatever it is called, and
        /// <c>CreateOrUpdateTableAsync</c> is the interpolating builder itself.</para>
        /// </summary>
        [Fact]
        public void NoTypeInTheProductAssemblyProvisionsLocalTablesForPanels()
        {
            var bannedTypeNames = new[] { "liveQueriesTableService", "SqliteTableService", "SQLiteTableService" };

            // Renames are the failure mode this lane exists to punish, so the METHOD names carry
            // the guard. Each was declared on the deleted service; each names a distinct capability.
            var bannedMethodNames = new[]
            {
                "EnsureTablesForAllPanelsAsync",   // the startup pass
                "EnsureTableForPanelAsync",        // one panel's table
                "CreateOrUpdateTableAsync",        // the interpolating DDL builder
                "InsertDataFromSqlServerAsync",    // the interpolating INSERT column list
                "GenerateCreateTableDdlAsync",     // renders the same DDL for display
                "ValidateAndCreateTableAsync",     // the public entry point that reached all of it
                "ExecuteliveQueriesQueryAsync",    // deleted ee55ca0: ran caller SQL on the local store
                "ExecuteSqlServerQueryAsync",      // deleted ee55ca0: ran caller SQL on the factory
            };

            var types = ProductAssembly.GetTypes();

            var offendingTypes = types
                .Where(t => bannedTypeNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                .Select(t => "type " + t.FullName)
                .ToList();

            var offendingMethods = types
                .SelectMany(t => t.GetMethods(Everything).Select(m => (Type: t, Method: m)))
                .Where(x => bannedMethodNames.Contains(x.Method.Name, StringComparer.Ordinal))
                .Select(x => "method " + x.Type.FullName + "." + x.Method.Name)
                .ToList();

            var found = offendingTypes.Concat(offendingMethods).OrderBy(s => s, StringComparer.Ordinal).ToList();

            Assert.True(found.Count == 0,
                "The liveQueries panel-provisioning surface is back in the product assembly. It was "
                + "deleted on 2026-09-05 because it WROTE tables into the encrypted local store using "
                + "table and column identifiers taken from the audited SQL Server's result-set "
                + "metadata, and the product is a read-only assessment whose write surfaces are "
                + "opt-in. Nothing read what it built. It shipped inert only because of a typo "
                + "(liveQueries_master for sqlite_master) that made every call throw before the "
                + "interpolation; correcting that identifier without deleting the surface would have "
                + "armed it. If you genuinely need to persist panel results locally, the column "
                + "names must not be caller-supplied identifiers, and it needs a ruling before it "
                + "needs code:\n  "
                + string.Join("\n  ", found));
        }

        /// <summary>The source file itself is absent. A cheap, unambiguous check that the deletion
        /// was a deletion and not a comment-out.</summary>
        [Fact]
        public void TheDeletedServiceSourceFileIsAbsent()
        {
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName, "Data", "SQLiteTableService.cs");

            Assert.False(File.Exists(path),
                "Data/SQLiteTableService.cs is back. It held liveQueriesTableService and ColumnInfo, "
                + "the panel-provisioning write surface deleted on 2026-09-05 by Adrian's ruling. "
                + "Restoring the file restores a path that builds local DDL out of identifiers the "
                + "monitored server chose.");
        }

        // ── 2. The SHAPE, across the product source ──────────────────────────────────────────

        /// <summary>
        /// A DDL or INSERT statement built with an interpolation hole or a concatenation, on one
        /// line of product source.
        /// </summary>
        private static readonly Regex DdlWithAHole = new(
            @"\b(CREATE\s+TABLE|ALTER\s+TABLE|INSERT\s+INTO)\b[^\r\n]*?(\{\s*[A-Za-z_0-9]|""\s*\+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The one site in product source that builds DDL with a hole, and why it is allowed.
        ///
        /// <para>Keyed on relative path plus the normalised code text, never the line number —
        /// the same convention <see cref="RawPassedScan"/> uses, because an allowlist that churns
        /// on every unrelated edit above a site is an allowlist nobody reads.</para>
        /// </summary>
        private static readonly Dictionary<string, string> AllowedDdlHoles = new(StringComparer.Ordinal)
        {
            [@"Data\Services\AlertHistoryService.cs" + "\t"
              + @"cmd.CommandText = $""ALTER TABLE alert_history ADD COLUMN {col} {def}"";"] =
                "MigrateIncidentColumns. The table name is a literal and both holes come from a "
                + "hard-coded tuple array declared twenty lines above in the same method "
                + "(state, state_updated_utc, state_updated_by, incident_notes, basis_kind). "
                + "No caller, and no monitored server, can reach either.",
        };

        private static string Normalise(string line) =>
            Regex.Replace(line.Trim(), @"\s+", " ");

        /// <summary>Every .cs and .razor file in the product tree. Tests, build output and tooling
        /// are excluded; this guard is about what ships.</summary>
        private static IEnumerable<string> ProductSourceFiles(DirectoryInfo root)
        {
            var skip = new[] { @"\Tests\", @"\bin\", @"\obj\", @"\.git\", @"\node_modules\", @"\.handoff\", @"\tools\" };

            return Directory.EnumerateFiles(root.FullName, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(f => !skip.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// No product source builds CREATE TABLE, ALTER TABLE or INSERT INTO from an interpolation
        /// hole, except the one allowlisted site whose holes are hard-coded literals.
        ///
        /// <para><b>What this measures, exactly.</b> Source TEXT. One line at a time. It flags a
        /// line where one of the three keywords is followed, on that same line, by an interpolation
        /// hole (<c>{ident</c>), a composite-format placeholder (<c>{0</c>, so
        /// <c>string.Format</c> and <c>AppendFormat</c> are covered) or a string concatenation
        /// (<c>" +</c>). Measured on 2026-09-05 after the deletion, the whole product tree contains
        /// exactly one such line, and zero concatenation sites.</para>
        ///
        /// <para><b>What this does NOT measure, and nobody should read it as covering.</b>
        /// (1) It does not know where a hole's VALUE came from — it flags the shape and makes a
        /// human write the reason down, which is the entire mechanism. (2) A statement whose
        /// keyword and hole land on DIFFERENT lines passes: a StringBuilder that appends
        /// <c>"CREATE TABLE "</c> and the name separately is invisible to it. (3) SQL held in .sql
        /// files, resources or the corpus is out of scope. (4) It reads source, not IL, so a
        /// statement assembled at runtime from configuration is out of scope. (5) It says nothing
        /// about SELECT, UPDATE, DELETE or PRAGMA. The positive control below is what keeps the
        /// narrow version honest: it proves the scan catches the exact three shapes that were
        /// deleted, so the guard is at least as strong as the defect it replaces.</para>
        /// </summary>
        [Fact]
        public void NoProductSourceBuildsDdlFromAnIdentifierHoleOutsideTheAllowlist()
        {
            var root = RawPassedScan.RepoRoot();
            var rootPath = root.FullName.TrimEnd('\\') + "\\";

            var scanned = 0;
            var found = new List<string>();

            foreach (var file in ProductSourceFiles(root))
            {
                scanned++;
                var rel = file.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(rootPath.Length)
                    : file;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!DdlWithAHole.IsMatch(lines[i])) continue;

                    var key = rel + "\t" + Normalise(lines[i]);
                    if (AllowedDdlHoles.ContainsKey(key)) continue;

                    found.Add(rel + ":" + (i + 1) + "  " + Normalise(lines[i]));
                }
            }

            // A guard that scans nothing passes silently, which is worse than no guard.
            Assert.True(scanned > 200,
                $"the DDL-hole guard only found {scanned} product source files, so it is not scanning "
                + "the tree it claims to scan. Re-anchor ProductSourceFiles rather than trusting a pass.");

            var stale = AllowedDdlHoles.Keys
                .Where(k => !File.Exists(Path.Combine(rootPath, k.Split('\t')[0])))
                .ToList();
            Assert.True(stale.Count == 0,
                "the DDL-hole allowlist names a file that no longer exists, so its excuse is dead "
                + "text: " + string.Join(", ", stale));

            Assert.True(found.Count == 0,
                "Product source builds a CREATE TABLE / ALTER TABLE / INSERT INTO statement out of "
                + "an interpolation hole or a concatenation. That is the exact shape deleted on "
                + "2026-09-05, where the hole was a result-set column alias chosen by the monitored "
                + "SQL Server and reaching an encrypted local store unquoted; the same shape was "
                + "proved on that date to DROP a table when handed a hostile alias, because one "
                + "ExecuteNonQuery runs every statement in a CommandText on this stack.\n"
                + "This guard flags the SHAPE, not the provenance — if your hole is a hard-coded "
                + "literal, add it to AllowedDdlHoles with the reason, and if it is not, do not add "
                + "it at all. Note the limits stated on this method: it cannot see a statement whose "
                + "keyword and hole sit on different lines.\n  "
                + string.Join("\n  ", found.OrderBy(s => s, StringComparer.Ordinal)));
        }

        /// <summary>
        /// The positive control. ALL FIVE statement lines that were actually deleted — three
        /// shapes across two methods — verbatim from <c>Data/SQLiteTableService.cs</c> at
        /// <c>11c621f</c>, fed to the same regex the guard uses. If the scan cannot see these, the
        /// guard above is theatre and this fails first. The last row is not from the deleted file:
        /// it is the composite-format evasion the 2026-09-05 cold gate demonstrated against the
        /// first version of this regex, pinned here so the widening cannot silently regress.
        /// </summary>
        [Theory]
        // GenerateCreateTableDdlAsync, SQLiteTableService.cs:79 at 11c621f.
        [InlineData("sb.AppendLine($\"CREATE TABLE {tableName} (\");")]
        // GenerateCreateTableDdlAsync, SQLiteTableService.cs:109 at 11c621f.
        [InlineData("sb.AppendLine($\"ALTER TABLE {tableName} ADD COLUMN {col.ColumnName} {liveQueriesType}{nullable};\");")]
        // CreateOrUpdateTableAsync, SQLiteTableService.cs:364 at 11c621f.
        [InlineData("                    CREATE TABLE IF NOT EXISTS {tableName} (")]
        // CreateOrUpdateTableAsync, SQLiteTableService.cs:391 at 11c621f.
        [InlineData("alterStatements.Add($\"ALTER TABLE {tableName} ADD COLUMN {col.ColumnName} {liveQueriesType}{nullable}\");")]
        // InsertDataFromSqlServerAsync, SQLiteTableService.cs:449 at 11c621f.
        [InlineData("var insertSql = $\"INSERT INTO {tableName} ({string.Join(\",\", columnNames)}) VALUES ({string.Join(\",\", paramNames)})\";")]
        // The cold gate's evasion 2: same line, same keyword, positional placeholder.
        [InlineData("var sql = string.Format(\"ALTER TABLE {0} ADD COLUMN {1} TEXT\", tbl, col);")]
        public void TheScanCatchesEveryShapeThatWasDeleted(string deletedLine)
        {
            Assert.True(DdlWithAHole.IsMatch(deletedLine),
                "the DDL-hole regex does NOT match a line that was really in the deleted service, so "
                + "NoProductSourceBuildsDdlFromAnIdentifierHoleOutsideTheAllowlist would pass over a "
                + "re-add of the very defect it exists to stop. Fix the regex, not this control:\n  "
                + deletedLine);
        }
    }
}
