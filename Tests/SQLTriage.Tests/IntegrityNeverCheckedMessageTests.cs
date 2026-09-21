/* In the name of God, the Merciful, the Compassionate */

// INVARIANT D (lane alert-correctness, 2026-09-19): A READING AN OPERATOR CANNOT INTERPRET IS NOT A
// READING. integrity_check_overdue computes DATEDIFF(HOUR, last_good, GETDATE()), and for a database
// that has never had a clean DBCC CHECKDB last_good is SQL Server's placeholder 1900-01-01, so the
// message said "1,110,757.0 hrs". Ruled by Adrian 2026-09-19, "keep the number, fix the message": the
// value still fires Critical at once, and only the sentence changes. The placeholder is recognised by
// the date it IMPLIES, never by a magic number, because the reading grows by one every hour.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public sealed class IntegrityNeverCheckedMessageTests : IDisposable
    {
        private readonly string _dir;

        public IntegrityNeverCheckedMessageTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "never-checked-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private AlertDefinitionService ShippedDefinitionsCopy()
        {
            var local = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-alert-definitions.json");
            File.Copy(ShippedConfig.Path("alert-definitions.json"), local);
            return new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, local);
        }

        private AlertDefinition Integrity() => ShippedDefinitionsCopy().GetAlert("integrity_check_overdue")
            ?? throw new InvalidOperationException("the shipped definitions no longer carry integrity_check_overdue");

        /// <summary>What the shipped query returns for a never-checked database when the SERVER's local
        /// clock reads <paramref name="serverLocalNow"/>: DATEDIFF(HOUR) counts hour boundaries crossed.</summary>
        private static double SentinelReading(DateTime serverLocalNow) =>
            Math.Floor((serverLocalNow - new DateTime(1900, 1, 1)).TotalHours);

        private static FiringBasis Fixed(double threshold) => FiringBasis.Fixed(threshold);

        [Fact]
        public void The_shipped_definition_is_the_shape_this_rule_is_about()
        {
            var alert = Integrity();
            Assert.Equal("hours", alert.Unit);
            Assert.Equal("greater_than", alert.Operator);
            Assert.Equal(672.0, alert.Thresholds.Critical!.Value);
            Assert.Contains("LastGoodCheckDbTime", alert.Query);
            Assert.Contains("DATEDIFF(HOUR, last_good, GETDATE())", alert.Query);
        }

        [Theory]
        [InlineData(0)]     // server on UTC
        [InlineData(14)]    // server 14 h ahead of us (UTC+14)
        [InlineData(-12)]   // server 12 h behind us (UTC-12)
        public void A_never_checked_reading_says_never_checked_at_any_server_clock_and_keeps_the_number(int serverOffsetHours)
        {
            var alert = Integrity();
            var nowUtc = new DateTime(2026, 9, 18, 13, 45, 0, DateTimeKind.Utc);
            var reading = SentinelReading(nowUtc.AddHours(serverOffsetHours));
            Assert.True(reading > alert.Thresholds.Critical!.Value, "the placeholder reading still breaches Critical");

            var message = AlertEvaluationService.FormatMessage(alert, "SRV1", reading, Fixed(672), "Critical", nowUtc);

            Assert.StartsWith("Never checked (critical):", message);
            Assert.Contains("no clean DBCC CHECKDB on record", message);
            Assert.Contains($"{reading:N0} hours", message);
            Assert.DoesNotContain("hrs (above", message);
        }

        [Fact]
        public void The_placeholder_is_recognised_by_its_implied_date_not_by_a_fixed_number()
        {
            // The two readings the record carries, seven hours apart on the same day. A rule keyed on
            // either number would miss the other.
            var alert = Integrity();
            var morning = new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Utc);
            var afternoon = morning.AddHours(7);
            var early = SentinelReading(morning);
            var late = SentinelReading(afternoon);
            Assert.Equal(7, late - early);

            Assert.True(AlertEvaluationService.IsNeverCheckedReading(alert, early, morning));
            Assert.True(AlertEvaluationService.IsNeverCheckedReading(alert, late, afternoon));
            // A year on, the reading has grown by 8,760 and is still the placeholder.
            Assert.True(AlertEvaluationService.IsNeverCheckedReading(alert, SentinelReading(afternoon.AddYears(1)), afternoon.AddYears(1)));
        }

        [Fact]
        public void The_margin_is_two_days_past_1900_01_01_on_our_clock()
        {
            var alert = Integrity();
            var nowUtc = new DateTime(2026, 9, 18, 13, 45, 0, DateTimeKind.Utc);
            var edge = new DateTime(1900, 1, 1).AddDays(2);

            var justInside = (nowUtc - edge.AddHours(-1)).TotalHours;   // implies 1900-01-02 23:00
            var justOutside = (nowUtc - edge.AddHours(1)).TotalHours;   // implies 1900-01-03 01:00
            Assert.True(AlertEvaluationService.IsNeverCheckedReading(alert, justInside, nowUtc));
            Assert.False(AlertEvaluationService.IsNeverCheckedReading(alert, justOutside, nowUtc));
            Assert.True(AlertEvaluationService.IsNeverCheckedReading(alert, double.MaxValue, nowUtc),
                "a reading that implies a date before year 1 is not a real CHECKDB date either");
        }

        [Fact]
        public void A_checked_database_keeps_exactly_the_old_message()
        {
            var alert = Integrity();
            var nowUtc = new DateTime(2026, 9, 18, 13, 45, 0, DateTimeKind.Utc);
            Assert.False(AlertEvaluationService.IsNeverCheckedReading(alert, 700, nowUtc));
            Assert.Equal("700.0 hrs (above the 672.0 hrs critical threshold)",
                AlertEvaluationService.FormatMessage(alert, "SRV1", 700, Fixed(672), "Critical", nowUtc));
            // A database last checked in 1990 is old, and it is a real date: an age, not a placeholder.
            var from1990 = (nowUtc - new DateTime(1990, 1, 1)).TotalHours;
            Assert.False(AlertEvaluationService.IsNeverCheckedReading(alert, from1990, nowUtc));
        }

        [Fact]
        public void Only_integrity_check_overdue_is_read_this_way()
        {
            var defs = ShippedDefinitionsCopy();
            var backup = defs.GetAlert("backup_full_overdue")!;
            Assert.Equal("hours", backup.Unit);
            var nowUtc = new DateTime(2026, 9, 18, 13, 45, 0, DateTimeKind.Utc);
            var reading = SentinelReading(nowUtc);
            Assert.False(AlertEvaluationService.IsNeverCheckedReading(backup, reading, nowUtc));
            Assert.StartsWith($"{reading:N1} hrs (above", AlertEvaluationService.FormatMessage(backup, "SRV1", reading, Fixed(336), "Critical", nowUtc));
        }

        [Fact]
        public void The_new_message_follows_the_voice_guide()
        {
            var message = AlertEvaluationService.NeverCheckedMessage(1_110_757, "Critical");
            var sentences = message.Split(". ", StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(sentences <= 3, $"three sentences at most: {message}");
            Assert.DoesNotContain('—', message);   // em-dash
            Assert.DoesNotContain('–', message);   // en-dash
            Assert.DoesNotContain('“', message);
            Assert.DoesNotContain('”', message);
            Assert.DoesNotContain('‘', message);
            Assert.DoesNotContain('’', message);
            Assert.Equal(
                "Never checked (critical): at least one user database has no clean DBCC CHECKDB on record. "
                + "SQL Server stores that as the date 1900-01-01, so the raw reading of 1,110,757 hours is not an age. "
                + "Schedule DBCC CHECKDB for every user database.",
                message);
        }

        // ── Through the engine: the value still fires Critical, with the new sentence ─────

        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                try { await request.Work(cancellationToken); return new QueryResult { QueryId = request.QueryId, Success = true }; }
                catch (Exception ex) { return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex }; }
            }
            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private AlertEvaluationService NewEngine(AlertDefinitionService defs)
        {
            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
            var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
            var svc = new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance, defs,
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(), channels, new liveQueriesCacheStore(), new InlineOrchestrator(),
                evalFailureStorePath: Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-eval-failures.json"));
            svc.DryRun = true;
            return svc;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Through_the_engine_a_never_checked_database_fires_Critical_with_the_new_sentence_and_a_checked_one_is_unchanged(bool neverChecked)
        {
            var defs = ShippedDefinitionsCopy();
            var alert = defs.GetAlert("integrity_check_overdue")!;
            alert.AlwaysAlert = true;   // an operational window in the test tree must not suppress the fire
            var server = "never-checked-" + Guid.NewGuid().ToString("N")[..6];
            var svc = NewEngine(defs);
            var reading = neverChecked ? SentinelReading(DateTime.UtcNow) : 700.0;
            svc.StandardQueryOverrideForTests = (_, _) => Task.FromResult<object?>(reading);
            var connection = new ServerConnection { Id = Guid.NewGuid().ToString(), ServerNames = server, UseWindowsAuthentication = true, IsEnabled = true };

            try
            {
                await svc.ThrottledEvaluateAsync(alert, connection, server, new AlertGlobalDefaults(), CancellationToken.None);

                var state = svc.ActiveAlerts.SingleOrDefault(a => a.AlertId == alert.Id && a.ServerName == server);
                Assert.NotNull(state);
                Assert.Equal("Critical", state!.Severity);
                Assert.Equal(reading, state.LastValue);   // the number is kept (ruled)
                if (neverChecked)
                    Assert.StartsWith("Never checked (critical):", state.Message);
                else
                    Assert.Equal("700.0 hrs (above the 672.0 hrs critical threshold)", state.Message);
            }
            finally
            {
                svc.Dispose();
            }
        }
    }
}
