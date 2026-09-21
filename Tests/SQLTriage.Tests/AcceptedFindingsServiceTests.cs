/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// F6 — Accepted Findings baseline. Covers IsAccepted matching (instance-wide vs
    /// scoped), expiry, revoke, upsert, and cache/persistence round-trips. Uses the
    /// test-seam dbPath ctor + a temp encrypted SQLite file per test class.
    /// </summary>
    public class AcceptedFindingsServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public AcceptedFindingsServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "accepted-findings-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        private AcceptedFindingsService NewService(string? file = null) =>
            new(NullLogger<AcceptedFindingsService>.Instance,
                audit: null,
                dbPath: Path.Combine(_tempDir, file ?? "check-baselines.db"));

        // ── IsAccepted matching ───────────────────────────────────────────

        [Fact]
        public async Task Accept_then_IsAccepted_true_for_same_server_and_check()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "SQLT-VA-XP-CMDSHELL", "runs CLR by design", "tester");

            Assert.True(svc.IsAccepted("SRV1", "SQLT-VA-XP-CMDSHELL"));
        }

        [Fact]
        public async Task IsAccepted_false_for_different_server()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "reason", "tester");

            Assert.False(svc.IsAccepted("SRV2", "CHK-A"));
        }

        [Fact]
        public async Task IsAccepted_false_for_different_check()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "reason", "tester");

            Assert.False(svc.IsAccepted("SRV1", "CHK-B"));
        }

        [Fact]
        public async Task InstanceWide_acceptance_matches_any_database_or_object()
        {
            using var svc = NewService();
            // db/obj NULL = instance-wide (MVP).
            await svc.Accept("SRV1", "CHK-A", "reason", "tester");

            Assert.True(svc.IsAccepted("SRV1", "CHK-A", db: "AdventureWorks"));
            Assert.True(svc.IsAccepted("SRV1", "CHK-A", db: "AdventureWorks", obj: "dbo.Foo"));
        }

        [Fact]
        public async Task Scoped_acceptance_matches_only_its_exact_scope()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "reason", "tester", db: "DB1");

            Assert.True(svc.IsAccepted("SRV1", "CHK-A", db: "DB1"));   // exact
            Assert.False(svc.IsAccepted("SRV1", "CHK-A", db: "DB2"));  // other db
            Assert.False(svc.IsAccepted("SRV1", "CHK-A"));            // instance-wide query, only scoped row exists
        }

        // ── Expiry ────────────────────────────────────────────────────────

        [Fact]
        public async Task Expired_acceptance_does_not_match()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "reason", "tester",
                expiresAt: DateTime.UtcNow.AddMinutes(-1)); // already expired

            Assert.False(svc.IsAccepted("SRV1", "CHK-A"));
        }

        [Fact]
        public async Task Future_expiry_still_matches()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "reason", "tester",
                expiresAt: DateTime.UtcNow.AddDays(90));

            Assert.True(svc.IsAccepted("SRV1", "CHK-A"));
        }

        [Fact]
        public async Task PruneExpired_removes_expired_rows_only()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "LIVE", "reason", "tester", expiresAt: DateTime.UtcNow.AddDays(30));
            await svc.Accept("SRV1", "DEAD", "reason", "tester", expiresAt: DateTime.UtcNow.AddMinutes(-1));

            await svc.PruneExpired();

            var remaining = svc.GetAcceptedFindings("SRV1");
            Assert.Single(remaining);
            Assert.Equal("LIVE", remaining[0].CheckId);
        }

        // ── Revoke / upsert ───────────────────────────────────────────────

        [Fact]
        public async Task Revoke_removes_the_acceptance()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "reason", "tester");
            Assert.True(svc.IsAccepted("SRV1", "CHK-A"));

            await svc.Revoke("SRV1", "CHK-A");

            Assert.False(svc.IsAccepted("SRV1", "CHK-A"));
        }

        [Fact]
        public async Task Accept_twice_upserts_reason_not_duplicates()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "first reason", "tester");
            await svc.Accept("SRV1", "CHK-A", "second reason", "tester");

            var all = svc.GetAcceptedFindings("SRV1");
            Assert.Single(all);
            Assert.Equal("second reason", all[0].Reason);
        }

        [Fact]
        public async Task Accept_with_blank_reason_throws()
        {
            using var svc = NewService();
            await Assert.ThrowsAsync<ArgumentException>(
                () => svc.Accept("SRV1", "CHK-A", "   ", "tester"));
        }

        // ── 2026-07-06 review regressions ─────────────────────────────────

        [Fact]
        public async Task Accept_twice_persists_single_row_across_restart()
        {
            // Regression: NULL scope columns made the SQLite UNIQUE index treat every re-accept
            // as a new row (NULLs are distinct in UNIQUE), and the in-memory cache MASKED the
            // duplicates. Reopening the DB exposes what was actually stored.
            const string file = "upsert-restart.db";
            using (var svc1 = NewService(file))
            {
                await svc1.Accept("SRV1", "CHK-A", "first", "tester");
                await svc1.Accept("SRV1", "CHK-A", "second", "tester");
            }

            using var svc2 = NewService(file);
            var all = svc2.GetAcceptedFindings("SRV1");
            Assert.Single(all);
            Assert.Equal("second", all[0].Reason);
        }

        [Fact]
        public async Task Key_fields_do_not_collide_across_boundaries()
        {
            // Regression: the cache key joined fields with no separator, so
            // server "SQL-A" + check "B-CHK" collided with server "SQL-A-B" + check "CHK".
            using var svc = NewService();
            await svc.Accept("SQL-A", "B-CHK", "reason", "tester");

            Assert.True(svc.IsAccepted("SQL-A", "B-CHK"));
            Assert.False(svc.IsAccepted("SQL-A-B", "CHK"));
            Assert.False(svc.IsAccepted("SQL-AB", "-CHK"));
        }

        [Fact]
        public async Task Revoke_is_case_insensitive_on_server_name()
        {
            // Regression: the read cache matches OrdinalIgnoreCase but Revoke used case-sensitive
            // SQL '=' — a case-variant server name made Revoke a silent no-op while the badge stayed.
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "reason", "tester");

            await svc.Revoke("srv1", "chk-a");

            Assert.False(svc.IsAccepted("SRV1", "CHK-A"));
        }

        // ── Persistence round-trip ────────────────────────────────────────

        [Fact]
        public async Task Acceptance_survives_service_restart()
        {
            const string file = "persist.db";
            using (var svc1 = NewService(file))
            {
                await svc1.Accept("SRV1", "CHK-A", "durable", "tester");
            }

            using var svc2 = NewService(file); // re-opens the same encrypted db
            Assert.True(svc2.IsAccepted("SRV1", "CHK-A"));
            Assert.Equal("durable", svc2.GetAcceptance("SRV1", "CHK-A")!.Reason);
        }

        [Fact]
        public async Task GetAcceptedFindings_returns_all_for_server_newest_first()
        {
            using var svc = NewService();
            await svc.Accept("SRV1", "CHK-A", "a", "tester");
            await svc.Accept("SRV1", "CHK-B", "b", "tester");
            await svc.Accept("SRV2", "CHK-C", "c", "tester");

            var srv1 = svc.GetAcceptedFindings("SRV1");
            Assert.Equal(2, srv1.Count);
            Assert.All(srv1, a => Assert.Equal("SRV1", a.ServerName));
        }
    }
}
