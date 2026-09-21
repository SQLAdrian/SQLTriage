/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// D3 big build — unit tests for the C# delta math in
    /// <see cref="CodeHotspotsCacheService"/>. Each test uses a unique temp DB
    /// file (test-seam constructor) so they don't pollute each other or the
    /// production cache. Live DMV capture is NOT exercised here — that path is
    /// verified via DevBridge /hotspots/capture (see project memory).
    ///
    /// Locked decision (Adrian 2026-05-26): first-seen rows are SKIPPED until
    /// the next cycle. A key in S2 but not S1 is treated as plan-compiled
    /// between snapshots and waits one cycle before contributing to the delta.
    /// </summary>
    public class CodeHotspotsCacheServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly CodeHotspotsCacheService _svc;

        public CodeHotspotsCacheServiceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"hotspots-cache-tests-{Guid.NewGuid():N}.db");
            _svc = new CodeHotspotsCacheService(
                NullLogger<CodeHotspotsCacheService>.Instance,
                _dbPath);
        }

        public void Dispose()
        {
            _svc.Dispose();
            // SQLite WAL files cling for a moment after dispose; ignore if locked
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        // ── Rule 1: HasDelta is false with <2 snapshots, true with ≥2 ─────
        [Fact]
        public void HasDelta_IsFalse_WithSingleSnapshot_TrueWithTwo()
        {
            const string server = "SRV1";
            _svc.HasDelta(server).Should().BeFalse("no snapshots yet");

            _svc.InsertTestRow(server, capturedAt: 1000, sqlHandleHex: "AA", startOffset: 0,
                dbId: 5, dbName: "appdb", objectId: 100, schemaQualified: "dbo.Proc1", snippet: "SELECT 1",
                execCount: 10, workerUs: 1000, reads: 5, writes: 0);
            _svc.HasDelta(server).Should().BeFalse("only one snapshot in retention window");

            _svc.InsertTestRow(server, capturedAt: 2000, sqlHandleHex: "AA", startOffset: 0,
                dbId: 5, dbName: "appdb", objectId: 100, schemaQualified: "dbo.Proc1", snippet: "SELECT 1",
                execCount: 20, workerUs: 2500, reads: 12, writes: 0);
            _svc.HasDelta(server).Should().BeTrue("two distinct capture times now exist");
        }

        // ── Rule 2: a key present in BOTH snapshots produces a delta ──────
        [Fact]
        public void ComputeDeltaDatabases_KeyInBothSnapshots_EmitsDelta()
        {
            const string server = "SRV2";
            _svc.InsertTestRow(server, 1000, "BB", 0, 5, "appdb", 100, "dbo.Proc1", "SELECT 2",
                execCount: 10, workerUs: 1_000_000, reads: 50, writes: 5);  // 1000 ms CPU
            _svc.InsertTestRow(server, 2000, "BB", 0, 5, "appdb", 100, "dbo.Proc1", "SELECT 2",
                execCount: 30, workerUs: 4_000_000, reads: 200, writes: 25); // 4000 ms CPU

            var rows = _svc.ComputeDeltaDatabases(server);
            rows.Should().HaveCount(1);
            var r = rows[0];
            r.Key.Should().Be("appdb");
            r.DeltaExecs.Should().Be(20);
            r.DeltaTotalCpuMs.Should().Be(3000);              // (4_000_000 - 1_000_000) / 1000
            r.DeltaAvgCpuMs.Should().Be(150);                 // 3000 / 20
            r.DeltaReads.Should().Be(150);
            r.DeltaWrites.Should().Be(20);
        }

        // ── Rule 3: skip-first-seen (key only in S2 → dropped) ────────────
        [Fact]
        public void ComputeDeltaDatabases_FirstSeenKey_IsSkipped()
        {
            const string server = "SRV3";
            _svc.InsertTestRow(server, 1000, "CC", 0, 5, "appdb", 100, "dbo.Existed", "x",
                execCount: 10, workerUs: 500_000, reads: 10, writes: 0); // baseline
            _svc.InsertTestRow(server, 2000, "CC", 0, 5, "appdb", 100, "dbo.Existed", "x",
                execCount: 15, workerUs: 750_000, reads: 15, writes: 0); // S2 same key — should appear
            _svc.InsertTestRow(server, 2000, "NEW", 0, 5, "appdb", 200, "dbo.JustCompiled", "y",
                execCount: 100, workerUs: 9_000_000, reads: 500, writes: 0); // S2 only — should NOT appear

            var rows = _svc.ComputeDeltaDatabases(server);
            // Both statements collapse to the same dbName=appdb at the DB grain;
            // the JustCompiled one is skipped so the delta reflects ONLY the Existed change.
            rows.Should().HaveCount(1);
            var r = rows[0];
            r.DeltaExecs.Should().Be(5, "only the key present in both snapshots contributes");
            r.DeltaReads.Should().Be(5);
        }

        // ── Rule 4: counter-reset (negative delta) is dropped, not negated ─
        [Fact]
        public void ComputeDeltaDatabases_NegativeDelta_IsDropped()
        {
            const string server = "SRV4";
            _svc.InsertTestRow(server, 1000, "DD", 0, 5, "appdb", 100, "dbo.Recompiled", "z",
                execCount: 1000, workerUs: 10_000_000, reads: 5000, writes: 100);
            // Plan recompile under same handle (rare but real) — counters reset.
            _svc.InsertTestRow(server, 2000, "DD", 0, 5, "appdb", 100, "dbo.Recompiled", "z",
                execCount: 5, workerUs: 5_000, reads: 2, writes: 0);

            var rows = _svc.ComputeDeltaDatabases(server);
            rows.Should().BeEmpty("the only key has a negative delta on every counter and is dropped");
        }

        // ── Rule 5: object grain is filtered to one database AND aggregates ─
        [Fact]
        public void ComputeDeltaObjects_AggregatesByObject_FilteredByDb()
        {
            const string server = "SRV5";
            // Two objects in appdb, one in otherdb — same key in both snapshots
            _svc.InsertTestRow(server, 1000, "EE", 0, 5, "appdb", 100, "sales.ProcA", "a",
                execCount: 10, workerUs: 1_000_000, reads: 10, writes: 0);
            _svc.InsertTestRow(server, 1000, "FF", 0, 5, "appdb", 200, "sales.ProcB", "b",
                execCount: 5, workerUs: 500_000, reads: 5, writes: 0);
            _svc.InsertTestRow(server, 1000, "GG", 0, 6, "otherdb", 300, "x.Y", "c",
                execCount: 99, workerUs: 9_000_000, reads: 99, writes: 0);

            _svc.InsertTestRow(server, 2000, "EE", 0, 5, "appdb", 100, "sales.ProcA", "a",
                execCount: 30, workerUs: 4_000_000, reads: 50, writes: 0);
            _svc.InsertTestRow(server, 2000, "FF", 0, 5, "appdb", 200, "sales.ProcB", "b",
                execCount: 15, workerUs: 1_500_000, reads: 15, writes: 0);
            _svc.InsertTestRow(server, 2000, "GG", 0, 6, "otherdb", 300, "x.Y", "c",
                execCount: 199, workerUs: 19_000_000, reads: 199, writes: 0);

            var rows = _svc.ComputeDeltaObjects(server, "appdb");
            rows.Should().HaveCount(2, "otherdb is filtered out");
            rows.Select(r => r.DisplayName).Should().BeEquivalentTo(new[] { "sales.ProcA", "sales.ProcB" });
            var procA = rows.Single(r => r.DisplayName == "sales.ProcA");
            procA.DeltaExecs.Should().Be(20);
            procA.DeltaTotalCpuMs.Should().Be(3000);
            // Ordered DESC by CPU
            rows[0].DisplayName.Should().Be("sales.ProcA");
        }

        // ── Rule 6: HasDelta on an unknown server is safe and false ────────
        [Fact]
        public void HasDelta_UnknownServer_ReturnsFalse()
        {
            _svc.HasDelta("never-captured-server").Should().BeFalse();
            _svc.ComputeDeltaDatabases("never-captured-server").Should().BeEmpty();
        }
    }
}
