// In the name of God, the Merciful, the Compassionate

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Driven against the shipped dashboard config, not a fixture.
    ///
    /// The defect: "Plan Cache Hit Ratio" selected sys.dm_os_performance_counters' RATIO counter
    /// and printed it with statUnit "%" and format "N1". A ratio counter is a numerator; its
    /// companion "... Base" counter is the denominator. The tile printed the numerator, so a
    /// healthy instance read "3,583.0 %".
    ///
    /// These tests are a lint over config text, which is exactly what they can be — the boundary is
    /// SQL Server's own arithmetic, and this file cannot execute it. The replacement query WAS
    /// executed against a live instance during the fix round; the assertions below only stop the
    /// shipped config from drifting back to a bare ratio counter.
    /// </summary>
    public class DashboardCounterArithmeticTests
    {
        /// <summary>
        /// Resolves the REPO SOURCE config, anchored on SQLTriage.sln.
        ///
        /// The obvious locator — walk up from AppContext.BaseDirectory looking for a "Config"
        /// directory — resolves the BUILD OUTPUT copy instead: the test assembly's own output
        /// carries a lowercase "config\" folder, which Path.Combine matches case-insensitively on
        /// Windows, and it is only as fresh as the last build. A negative control caught this
        /// during the fix round: the source file was reverted to the broken query and the lint
        /// still passed, because it was reading a copy. Anchoring on the solution file makes the
        /// resolution unambiguous, and the assert below makes a wrong resolution loud.
        /// </summary>
        private static string ConfigPath(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln"))) continue;

                var candidate = Path.Combine(dir.FullName, "Config", fileName);
                if (File.Exists(candidate)) return candidate;

                throw new InvalidOperationException(
                    $"Found the repo root at {dir.FullName} but no Config/{fileName} beneath it.");
            }
            throw new InvalidOperationException(
                $"Could not locate the repo root (SQLTriage.sln) above {AppContext.BaseDirectory}.");
        }

        private sealed record Panel(string Id, string Title, string Sql, string StatUnit, string GaugeSuffix, string PanelType);

        private static List<Panel> Panels(string fileName)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath(fileName)));
            var found = new List<Panel>();
            Walk(doc.RootElement, found);
            return found;
        }

        private static void Walk(JsonElement el, List<Panel> found)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("id", out var id) &&
                        el.TryGetProperty("query", out var q) &&
                        q.ValueKind == JsonValueKind.Object &&
                        q.TryGetProperty("sqlServer", out var sql) &&
                        sql.ValueKind == JsonValueKind.String)
                    {
                        found.Add(new Panel(
                            id.GetString() ?? "",
                            el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                            sql.GetString() ?? "",
                            el.TryGetProperty("statUnit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "",
                            el.TryGetProperty("barGaugeUnitSuffix", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "",
                            el.TryGetProperty("panelType", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() ?? "" : ""));
                    }
                    foreach (var p in el.EnumerateObject()) Walk(p.Value, found);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray()) Walk(item, found);
                    break;
            }
        }

        [Theory]
        [InlineData("dashboard-config.json")]
        public void No_panel_selects_a_ratio_counter_without_its_base(string fileName)
        {
            // A perf-counter name ending in "Ratio" is PERF_LARGE_RAW_FRACTION: meaningless alone.
            // Any panel that names one must also name the "Ratio Base" companion in the same query.
            var offenders = Panels(fileName)
                .Where(p => p.Sql.Contains("dm_os_performance_counters", StringComparison.OrdinalIgnoreCase))
                .Where(p => p.Sql.Contains("Ratio'", StringComparison.OrdinalIgnoreCase)
                         || p.Sql.Contains("Ratio\"", StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.Sql.Contains("Ratio Base", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.Id} ({p.Title})")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"{fileName}: these panels select a ratio counter with no base counter, so they print a " +
                $"raw numerator: {string.Join("; ", offenders)}");
        }

        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_plan_cache_hit_panel_divides_by_its_base_counter(string fileName)
        {
            var panel = Panels(fileName).SingleOrDefault(p => p.Id == "livequerystats.plan_cache_hit");
            Assert.NotNull(panel);

            Assert.Contains("Cache Hit Ratio Base", panel!.Sql, StringComparison.Ordinal);
            Assert.Contains("NULLIF", panel.Sql, StringComparison.Ordinal);
            // Still presented as a percentage; that claim is now true of the number beneath it.
            Assert.Equal("%", panel.StatUnit);
        }

        /// <summary>
        /// Every lint in this file used to run twice, against dashboard-config.json and against
        /// dashboard-config.default.json. That second file is DELETED (DECISIONS 2026-08-26 18:23,
        /// ruling 3), and the rows that named it are gone with it.
        ///
        /// <para>WHY IT WENT. It was never "what a fresh install reads": SQLTriage.csproj
        /// Content-Removed it from every build and DashboardConfigService never opened it. A fresh
        /// install reads Config/dashboard-config.json, and an install whose file is missing falls back
        /// to DefaultConfigGenerator's three hardcoded dashboards. Nothing read the seed, so it drifted
        /// to a different dashboard and panel id set, and linting it proved only that a dead file was
        /// tidy. These lints now run once, against the config this product actually ships. What used to
        /// be a two-file drift guard is now a chokepoint instead:
        /// <see cref="DashboardConfigMigratorTests.The_embedded_catalogue_is_byte_identical_to_the_shipped_config_file"/>
        /// pins the copy compiled into SQLTriage.dll — the one DashboardConfigMigrator re-bases from and
        /// DashboardFactoryReset restores from — byte-identical to this file, so every assertion here
        /// reaches both, and a drift between them fails one test rather than escaping through whichever
        /// lint nobody duplicated.</para>
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_per_second_grid_divides_the_cumulative_counter_by_instance_uptime(string fileName)
        {
            // sys.dm_os_performance_counters counter NAMES end in "/sec"; the VALUES are cumulative
            // since instance start. One reading cannot show the current rate, so this panel prints
            // the average since start and its title says so.
            var panel = Panels(fileName).SingleOrDefault(p => p.Id == "queryperf.batch_requests");
            Assert.NotNull(panel);

            Assert.Contains("sqlserver_start_time", panel!.Sql, StringComparison.Ordinal);
            Assert.Contains("NULLIF", panel.Sql, StringComparison.Ordinal);
            Assert.Contains("average per second since instance start", panel.Title, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A DeltaStatCard prints <c>Unit + "/sec"</c> as its headline unit and the raw reading as
        /// "&lt;n&gt; total since start". Three panels (live.poison_waits,
        /// live.serializable_locking, live.cmemthread) select
        /// <c>wait_time_ms - signal_wait_time_ms</c> and shipped with statUnit "", so the headline
        /// resolved to a bare "/sec" and the sub-line printed milliseconds with no unit at all —
        /// measured on '.' as 1,011,419 and 512,334, which read as counts of waits.
        ///
        /// <para>The lint is over the SELECTED COLUMN, not over the three panel ids: the mistake is
        /// available to the next panel somebody wires to a wait-time counter, and naming ids here
        /// would guard the three that are already fixed.</para>
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void A_delta_card_selecting_wait_time_ms_declares_its_unit(string fileName)
        {
            var offenders = Panels(fileName)
                .Where(p => p.PanelType == "DeltaStatCard")
                .Where(p => p.Sql.Contains("wait_time_ms", StringComparison.OrdinalIgnoreCase))
                .Where(p => !string.Equals(p.StatUnit, "ms", StringComparison.Ordinal))
                .Select(p => $"{p.Id} ({p.Title}) statUnit=\"{p.StatUnit}\"")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"{fileName}: these delta cards select a wait time in MILLISECONDS and do not say so, " +
                $"so the headline reads as a bare rate and the total reads as a count: " +
                $"{string.Join("; ", offenders)}");
        }
    }
}
