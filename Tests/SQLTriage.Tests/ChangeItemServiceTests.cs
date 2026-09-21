/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// #88 — Change-item ledger (the LOG branch beside ACCEPT). Covers the status lifecycle
    /// (Logged -> HandedToVendor -> ConfirmedFixed | StillFailing), the required rationale,
    /// latest-per-(server,check) indexing, the overdue-follow-up sweep, and persistence.
    /// Uses the test-seam dbPath ctor + a temp encrypted SQLite file per test class — mirrors
    /// <see cref="AcceptedFindingsServiceTests"/>.
    /// </summary>
    public class ChangeItemServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public ChangeItemServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "change-item-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        private ChangeItemService NewService(string? file = null) =>
            new(NullLogger<ChangeItemService>.Instance,
                audit: null,
                dbPath: Path.Combine(_tempDir, file ?? "change-items.db"));

        private static CheckResult Failing(string checkId, string server = "SRV1") =>
            new() { CheckId = checkId, InstanceName = server, CheckName = checkId, Passed = false };

        private static CheckResult Passing(string checkId, string server = "SRV1") =>
            new() { CheckId = checkId, InstanceName = server, CheckName = checkId, Passed = true };

        // ── Log ───────────────────────────────────────────────────────────

        [Fact]
        public async Task LogChange_then_GetLatest_returns_the_item()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "Missing index", "Vendor to add index", decidedBy: "tester");

            Assert.True(item.Id > 0);
            var latest = svc.GetLatest("SRV1", "CHK-A");
            Assert.NotNull(latest);
            Assert.Equal(ChangeItemStatus.Logged, latest!.Status);
            Assert.Equal("Vendor to add index", latest.Rationale);
        }

        [Fact]
        public async Task LogChange_with_blank_rationale_throws()
        {
            using var svc = NewService();
            await Assert.ThrowsAsync<ArgumentException>(
                () => svc.LogChange("SRV1", "CHK-A", "Missing index", "   ", decidedBy: "tester"));
        }

        [Fact]
        public async Task LogChange_captures_the_remediation_script()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "Missing index", "reason",
                remediationScript: "CREATE INDEX IX_Foo ON dbo.Orders(CustomerId);", decidedBy: "tester");

            Assert.Equal("CREATE INDEX IX_Foo ON dbo.Orders(CustomerId);",
                svc.GetById(item.Id)!.RemediationScript);
        }

        // ── Lifecycle transitions ─────────────────────────────────────────

        [Fact]
        public async Task HandToVendor_sets_status_vendor_and_due()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");
            var due = DateTime.UtcNow.AddDays(7);

            await svc.HandToVendor(item.Id, "Contoso DBA", due, by: "tester");

            var latest = svc.GetLatest("SRV1", "CHK-A")!;
            Assert.Equal(ChangeItemStatus.HandedToVendor, latest.Status);
            Assert.Equal("Contoso DBA", latest.HandedTo);
            Assert.NotNull(latest.HandedAt);
            Assert.NotNull(latest.DueAt);
        }

        [Fact]
        public async Task ConfirmFixed_from_handed_reaches_terminal()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");
            await svc.HandToVendor(item.Id, "vendor", null, by: "tester");

            await svc.ConfirmFixed(item.Id, note: "verified", by: "tester");

            Assert.Equal(ChangeItemStatus.ConfirmedFixed, svc.GetById(item.Id)!.Status);
        }

        [Fact]
        public async Task ConfirmFixed_from_logged_throws()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");

            // Nothing was handed off — confirming a fix is not a valid transition from Logged.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.ConfirmFixed(item.Id, by: "tester"));
        }

        [Fact]
        public async Task MarkStillFailing_then_rehand_is_allowed()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");
            await svc.HandToVendor(item.Id, "vendor", null, by: "tester");
            await svc.MarkStillFailing(item.Id, by: "tester");
            Assert.Equal(ChangeItemStatus.StillFailing, svc.GetById(item.Id)!.Status);

            // Re-handing a still-failing item is legitimate.
            await svc.HandToVendor(item.Id, "vendor", DateTime.UtcNow.AddDays(3), by: "tester");
            Assert.Equal(ChangeItemStatus.HandedToVendor, svc.GetById(item.Id)!.Status);
        }

        // ── Follow-up sweep ───────────────────────────────────────────────

        [Fact]
        public async Task FollowUp_flags_overdue_handed_item_whose_check_still_fails()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");
            await svc.HandToVendor(item.Id, "vendor", DateTime.UtcNow.AddDays(-1), by: "tester"); // past due

            var flagged = await svc.EvaluateFollowUpsAsync("SRV1", new[] { Failing("CHK-A") });

            Assert.Equal(1, flagged);
            Assert.Equal(ChangeItemStatus.StillFailing, svc.GetById(item.Id)!.Status);
        }

        [Fact]
        public async Task FollowUp_ignores_item_not_yet_due()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");
            await svc.HandToVendor(item.Id, "vendor", DateTime.UtcNow.AddDays(7), by: "tester"); // future due

            var flagged = await svc.EvaluateFollowUpsAsync("SRV1", new[] { Failing("CHK-A") });

            Assert.Equal(0, flagged);
            Assert.Equal(ChangeItemStatus.HandedToVendor, svc.GetById(item.Id)!.Status);
        }

        [Fact]
        public async Task FollowUp_does_not_flag_when_check_now_passes()
        {
            // Honest default: a passing check is confirmed by a human, never auto-flagged nor auto-closed.
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");
            await svc.HandToVendor(item.Id, "vendor", DateTime.UtcNow.AddDays(-1), by: "tester");

            var flagged = await svc.EvaluateFollowUpsAsync("SRV1", new[] { Passing("CHK-A") });

            Assert.Equal(0, flagged);
            Assert.Equal(ChangeItemStatus.HandedToVendor, svc.GetById(item.Id)!.Status);
        }

        [Fact]
        public async Task FollowUp_does_not_touch_other_servers()
        {
            using var svc = NewService();
            var item = await svc.LogChange("SRV1", "CHK-A", "n", "reason", decidedBy: "tester");
            await svc.HandToVendor(item.Id, "vendor", DateTime.UtcNow.AddDays(-1), by: "tester");

            var flagged = await svc.EvaluateFollowUpsAsync("SRV2", new[] { Failing("CHK-A", "SRV2") });

            Assert.Equal(0, flagged);
            Assert.Equal(ChangeItemStatus.HandedToVendor, svc.GetById(item.Id)!.Status);
        }

        // ── Latest-per-key + persistence ──────────────────────────────────

        [Fact]
        public async Task GetLatest_returns_most_recent_item_for_key()
        {
            using var svc = NewService();
            await svc.LogChange("SRV1", "CHK-A", "n", "first", decidedBy: "tester");
            var second = await svc.LogChange("SRV1", "CHK-A", "n", "second", decidedBy: "tester");

            Assert.Equal(second.Id, svc.GetLatest("SRV1", "CHK-A")!.Id);
        }

        [Fact]
        public async Task Items_survive_service_restart()
        {
            const string file = "persist.db";
            long id;
            using (var svc1 = NewService(file))
            {
                var item = await svc1.LogChange("SRV1", "CHK-A", "n", "durable", decidedBy: "tester");
                await svc1.HandToVendor(item.Id, "vendor", DateTime.UtcNow.AddDays(5), by: "tester");
                id = item.Id;
            }

            using var svc2 = NewService(file); // re-opens the same encrypted db
            var reloaded = svc2.GetById(id);
            Assert.NotNull(reloaded);
            Assert.Equal(ChangeItemStatus.HandedToVendor, reloaded!.Status);
            Assert.Equal("durable", reloaded.Rationale);
            Assert.Equal("vendor", reloaded.HandedTo);
        }

        [Fact]
        public async Task GetAll_orders_newest_first()
        {
            using var svc = NewService();
            await svc.LogChange("SRV1", "CHK-A", "n", "a", decidedBy: "tester");
            await svc.LogChange("SRV1", "CHK-B", "n", "b", decidedBy: "tester");

            var all = svc.GetAll();
            Assert.Equal(2, all.Count);
            Assert.True(all[0].DecidedAt >= all[1].DecidedAt);
        }
    }
}
