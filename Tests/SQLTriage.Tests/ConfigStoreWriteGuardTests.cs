/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The D4 write guard, extended from the two RBAC stores to the rest of the config stores on
    /// 2026-08-04, asserted BEHAVIOURALLY: every test here damages a real file on disk, drives a
    /// real mutator, and then reads the bytes back. Nothing checks wording, and nothing checks that
    /// a log line was written — five static instruments have fallen in this lane, the last to an
    /// ordinary English sentence, and a test that greps source is a lint no matter what it asserts.
    ///
    /// <para><b>The ruling being pinned.</b> Not every store gets the same ceremony. A write is
    /// REFUSED when it would destroy a secret or a security control — something that changes what
    /// the install can do or who it trusts — and merely ANNOUNCED when it would destroy data an
    /// operator authored and can author again. Over-applying the guard is a real failure mode: a
    /// refusal that fires on low-stakes files is a refusal that gets ripped out, and it would take
    /// the credential protection with it. So the warn-only stores are pinned here too, by the
    /// assertion that they STILL SAVE — a later round that "tidies up" by making them uniform will
    /// fail these, which is the point.</para>
    ///
    /// <para>Both damaged shapes are exercised everywhere, because they behave differently one layer
    /// down: malformed content is quarantined to a <c>.rejected-</c> copy, a 0-byte file is not
    /// (there is nothing to preserve), and it was that asymmetry that produced the false recovery
    /// advice the register now exists to prevent.</para>
    /// </summary>
    public class ConfigStoreWriteGuardTests : IDisposable
    {
        private readonly string _dir;

        public ConfigStoreWriteGuardTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "store-write-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Path_(string name) => System.IO.Path.Combine(_dir, name);

        /// <summary>Truncated JSON: valid prefix, no closing brace. Quarantined by the loader.</summary>
        private static void WriteTruncated(string path, string content) => File.WriteAllText(path, content);

        /// <summary>The 0-byte shape. Damage, and deliberately NOT quarantined.</summary>
        private static void WriteEmpty(string path) => File.WriteAllText(path, "");

        // ══════════════════════════════════════════════════════════════════════════════════
        //  TIER 1 — refuse to write. Secrets and security controls.
        // ══════════════════════════════════════════════════════════════════════════════════

        // ── notification-channels.json: every outbound alerting credential on the install ──

        private static NotificationChannelService ChannelsOver(string path)
        {
            var svc = new NotificationChannelService(
                NullLogger<NotificationChannelService>.Instance,
                new AlertTemplateService(NullLogger<AlertTemplateService>.Instance));
            Repoint(svc, "_configFilePath", path);
            Invoke(svc, "LoadConfig");
            return svc;
        }

        [Theory]
        [InlineData(true)]      // truncated  → quarantined
        [InlineData(false)]     // 0-byte     → not quarantined
        public void DamagedChannelStore_RefusesTheWrite_AndTheCredentialsAreStillOnDisk(bool truncated)
        {
            var path = Path_("notification-channels.json");
            const string real = "{\"Smtp\":{\"Enabled\":true,\"Password\":\"the-operators-smtp-secret\"},";
            if (truncated) WriteTruncated(path, real); else WriteEmpty(path);
            var before = File.ReadAllBytes(path);

            var svc = ChannelsOver(path);
            Assert.True(svc.IsStoreDamaged);

            // The whole-config save AND the one-field window save. The second is the dangerous one:
            // "start maintenance mode" is a single click in an incident and it writes the file whole.
            Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable,
                svc.UpdateConfig(new NotificationChannelConfig()));
            Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable,
                svc.UpdateAlertWindows(new AlertWindowConfig()));

            Assert.Equal(before, File.ReadAllBytes(path));
        }

        [Fact]
        public void HealthyChannelStore_StillSaves()
        {
            // The control. Without it the refusal assertions above pass on a guard that refuses
            // everything, which would be a broken product rather than a protected one.
            var path = Path_("notification-channels.json");
            ConfigFileHelper.Save(path, new NotificationChannelConfig());

            var svc = ChannelsOver(path);
            Assert.False(svc.IsStoreDamaged);
            Assert.Equal(StoreWriteOutcome.Saved,
                svc.UpdateConfig(new NotificationChannelConfig { Smtp = new SmtpChannelConfig { Enabled = true } }));

            var reread = ConfigFileHelper.Load<NotificationChannelConfig>(path);
            Assert.True(reread.Smtp.Enabled);
        }

        [Fact]
        public void MissingChannelStore_SavesBecauseThatIsAFreshInstall()
        {
            var path = Path_("notification-channels.json");
            Assert.False(File.Exists(path));

            var svc = ChannelsOver(path);
            Assert.False(svc.IsStoreDamaged);
            Assert.Equal(StoreWriteOutcome.Saved, svc.UpdateConfig(new NotificationChannelConfig()));
            Assert.True(File.Exists(path));
        }

        // ── remediation-grants.json: the replay ledger. Damage RELAXES a control. ──

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedGrantLedger_RefusesToRedeem_SoADamagedFileCannotClearAReplay(bool truncated)
        {
            var path = Path_("remediation-grants.json");

            // A ledger that already knows this nonce. Its bytes are what the replay guard rests on.
            var seeded = new RemediationGrantStore(path);
            Assert.Null(seeded.Redeem(Alloc("SQLBOX", 50, "nonce-A"), DateTime.UtcNow));
            var realLedger = File.ReadAllBytes(path);
            Assert.Contains("nonce-A", System.Text.Encoding.UTF8.GetString(realLedger));

            if (truncated) WriteTruncated(path, "{\"Version\":1,\"Grants\":[{\"Nonce\":\"nonce-A\","); else WriteEmpty(path);
            var before = File.ReadAllBytes(path);

            var store = new RemediationGrantStore(path);
            Assert.True(store.IsDamaged);

            // The replay that a blind read would have waved through: same nonce, second redemption.
            var err = store.Redeem(Alloc("SQLBOX", 50, "nonce-A"), DateTime.UtcNow);
            Assert.NotNull(err);

            // And no credits were granted for it, on the object OR on disk.
            Assert.Equal(0, store.GrantedCreditsFor("SQLBOX", DateTime.UtcNow));
            Assert.Equal(before, File.ReadAllBytes(path));
        }

        [Fact]
        public void HealthyGrantLedger_StillRedeemsAndStillRejectsARealReplay()
        {
            var path = Path_("remediation-grants.json");
            var store = new RemediationGrantStore(path);

            Assert.Null(store.Redeem(Alloc("SQLBOX", 50, "nonce-B"), DateTime.UtcNow));
            Assert.Equal(50, store.GrantedCreditsFor("SQLBOX", DateTime.UtcNow));
            Assert.NotNull(store.Redeem(Alloc("SQLBOX", 50, "nonce-B"), DateTime.UtcNow));
        }

        private static SignedAllocation Alloc(string server, int credits, string nonce) => new()
        {
            Server = server,
            Credits = credits,
            IssuedUtc = DateTime.UtcNow.AddDays(-1),
            ExpiresUtc = DateTime.UtcNow.AddYears(1),
            Nonce = nonce,
        };

        // ── user-settings.json: preferences, and the install's licence ──
        //
        // Built over a temp file through UserSettingsService's internal path seam. This used to
        // construct the service and THEN repoint the private field, which meant construction read
        // the operator's real %APPDATA%\SQLTriage\user-settings.json once. The seam removes that
        // read; nothing here touches a real profile (2026-08-04).

        private static UserSettingsService SettingsOver(string path)
            => new UserSettingsService(path);

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedSettingsStore_RefusesTheWrite_AndTheLicenceSectionSurvives(bool truncated)
        {
            var path = Path_("user-settings.json");

            // The shape that makes this a credential store rather than a preferences file: the
            // License section is written by a DIFFERENT writer and only survives a save because
            // ReloadForeignSections re-reads it — which is the exact thing damage prevents.
            const string real = "{\"RadzenUiTheme\":\"dark\",\"License\":{\"ClientName\":\"Acme\",\"EncryptedKey\":\"AAAA\"}";
            if (truncated) WriteTruncated(path, real); else WriteEmpty(path);
            var before = File.ReadAllBytes(path);

            var svc = SettingsOver(path);
            Assert.True(svc.IsStoreDamaged);

            svc.SetRadzenUiTheme("fluent-dark");    // an ordinary preference click
            svc.SaveSettings();

            Assert.Equal(before, File.ReadAllBytes(path));
        }

        [Fact]
        public void DamagedSettingsStore_WithoutTheGuard_WouldHaveDeletedTheLicence()
        {
            // The failure this prevents, demonstrated on the filesystem rather than argued: take the
            // same damaged file, serialise the in-memory defaults over it the way SaveSettings used
            // to, and read back what an operator would be left holding.
            var path = Path_("user-settings.json");
            File.WriteAllText(path, "{\"RadzenUiTheme\":\"dark\",\"License\":{\"ClientName\":\"Acme\",\"EncryptedKey\":\"AAAA\"}");

            var loaded = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Unreadable, outcome);
            Assert.Empty(loaded.ForeignSections);           // the licence is not in the object
            ConfigFileHelper.Save(path, loaded);            // ...so writing the object drops it

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(doc.RootElement.TryGetProperty("License", out _));
        }

        [Fact]
        public void HealthySettingsStore_StillSaves()
        {
            var path = Path_("user-settings.json");
            File.WriteAllText(path, "{\"RadzenUiTheme\":\"dark\",\"License\":{\"ClientName\":\"Acme\",\"EncryptedKey\":\"AAAA\"}}");

            var svc = SettingsOver(path);
            Assert.False(svc.IsStoreDamaged);

            svc.SetRadzenUiTheme("fluent-dark");
            svc.SaveSettings();

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("fluent-dark", doc.RootElement.GetProperty("RadzenUiTheme").GetString());
            Assert.Equal("Acme", doc.RootElement.GetProperty("License").GetProperty("ClientName").GetString());
        }

        // ── server-connections.json: the SQL logins and their protected passwords ──
        //
        //  Missed entirely by round 1 — the file had ZERO references to ConfigFileHelper and saved
        //  through a raw File.WriteAllText. The gate drove it: a damaged store holding PROD-SQL01
        //  and its password loaded 0 connections, one AddConnection answered Succeeded=True, the
        //  file's hash moved, the original connection was gone, and rejectedCopies was 0.

        private static ServerConnection Conn(string id, string server, string? user = null, string? password = null)
        {
            var c = new ServerConnection
            {
                Id = id,
                ServerNames = server,
                UseWindowsAuthentication = password == null,
                Username = user,
            };
            if (password != null) c.SetPassword(password);
            return c;
        }

        private static ServerConnectionManager ConnectionsOver(string path) =>
            new(NullLogger<ServerConnectionManager>.Instance, null, path);

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedConnectionStore_RefusesTheAdd_AndLeavesTheCredentialsOnDisk(bool truncated)
        {
            var path = Path_("server-connections.json");
            if (truncated)
                WriteTruncated(path, "[{\"Id\":\"prod\",\"ServerNames\":\"PROD-SQL01\",\"Username\":\"svc_triage\",");
            else
                WriteEmpty(path);
            var before = File.ReadAllBytes(path);

            var mgr = ConnectionsOver(path);
            Assert.True(mgr.IsStoreDamaged);
            Assert.Empty(mgr.GetConnections());          // it holds none of the operator's estate

            var result = mgr.AddConnection(Conn("new", "DEV-SQL02", "sa", "hunter2hunter2"));

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            Assert.Equal(before, File.ReadAllBytes(path));    // byte-identical: nothing was written
            Assert.Empty(mgr.GetConnections());               // and nothing phantom in memory
        }

        [Fact]
        public void DamagedConnectionStore_RefusesRemoveAndUpdateAndStatusWrites()
        {
            var path = Path_("server-connections.json");
            WriteTruncated(path, "[{\"Id\":\"prod\",\"ServerNames\":\"PROD-SQL01\",");
            var before = File.ReadAllBytes(path);

            var mgr = ConnectionsOver(path);

            // Every write path, not just the one the gate happened to drive. RemoveConnection of an
            // unknown id is idempotent-Ok by design and writes nothing, so it is not asserted here.
            Assert.False(mgr.UpdateConnection(Conn("prod", "SOMETHING-ELSE")).Succeeded);
            mgr.UpdateSuccessfulServers("prod", new List<string> { "PROD-SQL01" });

            Assert.Equal(before, File.ReadAllBytes(path));
        }

        [Fact]
        public void MissingConnectionStore_IsNotDamage_AndOnboardingStillWorks()
        {
            // The other half of the ruling. A file that was never there is a fresh install; refusing
            // the first save would make the product unusable out of the box, which is how a guard
            // gets removed along with everything it protects.
            var path = Path_("server-connections.json");
            var mgr = ConnectionsOver(path);

            Assert.False(mgr.IsStoreDamaged);
            Assert.True(mgr.AddConnection(Conn("first", "DEV-SQL02", "sa", "hunter2hunter2")).Succeeded);

            var onDisk = ConfigFileHelper.Load<List<ServerConnection>>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Equal("DEV-SQL02", Assert.Single(onDisk).ServerNames);
        }

        [Fact]
        public void HealthyConnectionStore_StillSaves_AndKeepsTheStoredPasswordEncrypted()
        {
            var path = Path_("server-connections.json");
            ConfigFileHelper.Save(path, new List<ServerConnection> { Conn("prod", "PROD-SQL01", "svc", "hunter2hunter2") });

            var mgr = ConnectionsOver(path);
            Assert.False(mgr.IsStoreDamaged);
            Assert.True(mgr.AddConnection(Conn("dev", "DEV-SQL02")).Succeeded);

            var onDisk = ConfigFileHelper.Load<List<ServerConnection>>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Equal(2, onDisk.Count);

            var prod = onDisk.Single(c => c.Id == "prod");
            Assert.Equal("svc", prod.Username);
            Assert.True(CredentialProtector.IsEncrypted(prod.Password!));
            Assert.Equal("hunter2hunter2", prod.GetDecryptedPassword());
        }

        [Fact]
        public void ConnectionWrite_ThatThrows_IsRefused_AndLeavesNoPhantomConnection()
        {
            // The IO half of the same defect: the guard passes, and then the write fails. Forced by
            // making the atomic write's temp path a directory — a real UnauthorizedAccessException.
            var path = Path_("server-connections.json");
            ConfigFileHelper.Save(path, new List<ServerConnection> { Conn("prod", "PROD-SQL01", "svc", "hunter2hunter2") });
            var mgr = ConnectionsOver(path);
            Directory.CreateDirectory(path + ".tmp");

            Assert.False(mgr.AddConnection(Conn("ghost", "GHOST-SQL")).Succeeded);
            Assert.False(mgr.RemoveConnection("prod").Succeeded);

            Assert.Single(mgr.GetConnections());                     // memory rolled back…
            Assert.Equal("prod", mgr.GetConnections()[0].Id);
            Assert.Single(ConfigFileHelper.Load<List<ServerConnection>>(path));   // …and equals disk
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  TIER 1 (read-side) — remediation-credit-ledger.json.
        //
        //  A commercial control that OPENED on corruption: damaged ⇒ empty spend map ⇒ every
        //  server's paid allocation restored in full. Refusing only the write would be worse than
        //  the bug (every apply free AND unrecorded), so this one fails closed on the READ.
        // ══════════════════════════════════════════════════════════════════════════════════

        private static PersistedRemediationCreditLedger LedgerOver(string path)
        {
            var bundle = new SQLTriage.Tests.Licensing.FakeBundleAccessor
            {
                Features = new SQLTriage.Data.Services.Licensing.BundleFeatures(
                    RagEnabled: false, SpBlitzImport: false, FullCorpus: false,
                    PermittedCheckIds: Array.Empty<int>(),
                    Remediation: true, RemediationCreditsPerServer: 10),
            };
            return new PersistedRemediationCreditLedger(
                bundle, NullLogger<PersistedRemediationCreditLedger>.Instance, null, path);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedCreditLedger_WithholdsCredit_AndDoesNotOverwriteTheSpendRecord(bool truncated)
        {
            var path = Path_("remediation-credit-ledger.json");
            if (truncated) WriteTruncated(path, "{\"schemaVersion\":1,\"spent\":{\"PROD-SQL01\":9,"); else WriteEmpty(path);
            var before = File.ReadAllBytes(path);

            var ledger = LedgerOver(path);

            Assert.True(ledger.IsStoreDamaged);
            Assert.Equal(0, ledger.AvailableFor("PROD-SQL01"));     // NOT the full allocation
            Assert.Equal(0, ledger.GetBreakdown("PROD-SQL01").Available);
            Assert.Null(ledger.Reserve("PROD-SQL01", 1));
            Assert.Equal(before, File.ReadAllBytes(path));          // the record is still there to restore
        }

        [Fact]
        public void HealthyCreditLedger_StillSpends()
        {
            // Absolute allocation is deliberately not asserted: a DevBridge build floors it, and
            // this test is about the guard, not the licence maths. What must hold is that spend
            // reduces credit and survives a reload.
            var path = Path_("remediation-credit-ledger.json");
            var ledger = LedgerOver(path);

            Assert.False(ledger.IsStoreDamaged);
            var alloc = ledger.AvailableFor("PROD-SQL01");
            Assert.True(alloc >= 10);

            var res = ledger.Reserve("PROD-SQL01", 4);
            Assert.NotNull(res);
            ledger.Commit(res!);
            Assert.Equal(alloc - 4, ledger.AvailableFor("PROD-SQL01"));

            Assert.Equal(alloc - 4, LedgerOver(path).AvailableFor("PROD-SQL01"));
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  TIER 2 — announce, do not refuse. Operator-authored data, re-authorable.
        //
        //  These assert the write GOES THROUGH. They exist to stop a later round applying the
        //  refusal uniformly: a guard that blocks harmless work on low-stakes files is the guard
        //  an operator disables, and the same code is what protects the intake SAS.
        // ══════════════════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedThresholdStore_StillAcceptsTheWrite(bool truncated)
        {
            // Now alert-thresholds.json, not alert-definitions.json. The old version of this test
            // damaged the file with its OWN array schema, so it never once exercised the file as
            // SHIPPED — see ThresholdWrite_DoesNotTouchTheShippedAlertCatalogue below, which does.
            var path = Path_(AlertingService.ThresholdStoreFileName);
            if (truncated) WriteTruncated(path, "[{\"Name\":\"High CPU\","); else WriteEmpty(path);

            var svc = new AlertingService(NullLogger<AlertingService>.Instance, null, path);
            Assert.True(svc.IsStoreDamaged);

            svc.AddThreshold(new AlertThreshold { Name = "Disk", Metric = "disk", ThresholdValue = 90 });

            var onDisk = ConfigFileHelper.Load<List<AlertThreshold>>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Single(onDisk);
            Assert.Equal("Disk", onDisk[0].Name);
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  The two-schema collision on alert-definitions.json, driven against the file AS SHIPPED.
        //
        //  AlertingService read Config/alert-definitions.json as a JSON ARRAY; AlertDefinitionService
        //  reads the same path (config/ then Config/ — one directory on Windows) as an OBJECT, and
        //  the object is what SQLTriage.csproj ships: 95,970 bytes, 80 alerts. So the announce-tier
        //  write destroyed the other service's catalogue on every install that used the threshold
        //  editor, and the updater PRESERVES that file rather than repairing it.
        //
        //  Neither tier fixes a collision. The path did.
        // ══════════════════════════════════════════════════════════════════════════════════

        /// <summary>The shipped catalogue, copied from the repo so the test moves with the product.</summary>
        private static string ShippedCataloguePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "Config", "alert-definitions.json");
                if (File.Exists(candidate)) return candidate;
            }
            throw new InvalidOperationException("Could not locate the shipped Config/alert-definitions.json.");
        }

        [Fact]
        public void ShippedAlertCatalogue_IsAnObject_AndIsNotThisServicesSchema()
        {
            // The premise, asserted rather than assumed: the file the product ships is an object,
            // and reading it as AlertingService's array schema is damage. If a later round changes
            // the shipped shape to an array, this fails and the collision is back.
            using var doc = JsonDocument.Parse(File.ReadAllText(ShippedCataloguePath()));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement.GetProperty("alerts").GetArrayLength() > 0);

            var probe = ConfigFileHelper.InspectStore<List<AlertThreshold>>(ShippedCataloguePath());
            Assert.Equal(ConfigLoadOutcome.Unreadable, probe);
        }

        [Fact]
        public void ThresholdWrite_DoesNotTouchTheShippedAlertCatalogue()
        {
            var catalogue = System.IO.Path.Combine(_dir, AlertingService.LegacyThresholdStoreFileName);
            File.Copy(ShippedCataloguePath(), catalogue);
            var before = File.ReadAllBytes(catalogue);
            var alertsBefore = JsonDocument.Parse(before).RootElement.GetProperty("alerts").GetArrayLength();
            Assert.True(alertsBefore > 1);

            // A whole AlertingService lifecycle over the shipped layout: start on the collided
            // directory, then drive AddThreshold, the store-write path (its POST /alerts/thresholds
            // route was removed 2026-08-26, ruling 3; AddThreshold stays as this guard's vehicle).
            var svc = new AlertingService(NullLogger<AlertingService>.Instance, null,
                System.IO.Path.Combine(_dir, AlertingService.ThresholdStoreFileName));
            svc.AddThreshold(new AlertThreshold { Name = "Disk", Metric = "disk", ThresholdValue = 90 });

            // Byte-identical. Not "still parses", not "still has 80" — the exact bytes, because the
            // gate measured this defect as a hash change.
            Assert.Equal(before, File.ReadAllBytes(catalogue));

            // And nothing quarantined it either: the probe takes no .rejected- copy, so a stock
            // install no longer sheds a 96 KB copy of a healthy file on every single start.
            Assert.Empty(Directory.GetFiles(_dir, AlertingService.LegacyThresholdStoreFileName + ".rejected-*"));

            // The service that owns the catalogue still reads all of it.
            var defs = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, catalogue);
            Assert.False(defs.IsStoreDamaged);
            Assert.Equal(alertsBefore, defs.GetAllAlerts().Count);
        }

        [Fact]
        public void StockInstall_EvaluatesTheDefaultThresholds_InsteadOfReportingItselfDamaged()
        {
            // The other half of the collision, and it was shipping: with the object catalogue on
            // the old path, LoadThresholds' File.Exists branch always won, the array parse always
            // failed, and the else-branch that seeds GetDefaultThresholds was unreachable. Every
            // stock install evaluated ZERO thresholds and called itself damaged.
            File.Copy(ShippedCataloguePath(),
                System.IO.Path.Combine(_dir, AlertingService.LegacyThresholdStoreFileName));

            var svc = new AlertingService(NullLogger<AlertingService>.Instance, null,
                System.IO.Path.Combine(_dir, AlertingService.ThresholdStoreFileName));

            Assert.False(svc.IsStoreDamaged);
            Assert.NotEmpty(svc.GetThresholds());
        }

        [Fact]
        public void LegacyArrayStore_IsAdoptedOnce_AndTheOldFileIsLeftAlone()
        {
            // The upgrade path for an install that DID use the threshold editor: its
            // alert-definitions.json is an array, its catalogue is already gone, and the thresholds
            // in it are the only ones it has. They move; the file itself is not this service's to
            // delete or rewrite.
            var legacy = System.IO.Path.Combine(_dir, AlertingService.LegacyThresholdStoreFileName);
            ConfigFileHelper.Save(legacy, new List<AlertThreshold>
            {
                new() { Name = "Nightly CPU", Metric = "cpu", ThresholdValue = 85 },
            });
            var legacyBefore = File.ReadAllBytes(legacy);

            var svc = new AlertingService(NullLogger<AlertingService>.Instance, null,
                System.IO.Path.Combine(_dir, AlertingService.ThresholdStoreFileName));

            Assert.Equal("Nightly CPU", Assert.Single(svc.GetThresholds()).Name);
            Assert.Equal(legacyBefore, File.ReadAllBytes(legacy));

            var migrated = ConfigFileHelper.Load<List<AlertThreshold>>(
                System.IO.Path.Combine(_dir, AlertingService.ThresholdStoreFileName), null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Equal("Nightly CPU", Assert.Single(migrated).Name);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedOwnerStore_StillAcceptsTheWrite(bool truncated)
        {
            var path = Path_("finding-owners.json");
            if (truncated) WriteTruncated(path, "{\"Version\":1,\"Assignments\":{\"srv|CHK-1\":"); else WriteEmpty(path);

            var store = new OwnerAssignmentStore(path);
            Assert.True(store.IsStoreDamaged);

            store.Set("srv", "CHK-2", "adrian", DateTime.UtcNow.Date.AddDays(30), "tester");

            var onDisk = ConfigFileHelper.Load<OwnerAssignmentStoreData>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Equal("adrian", onDisk.Assignments["srv|chk-2"].Owner);

            // ...and the honest cost of that ruling, stated as an assertion rather than a comment:
            // the assignments the damaged file held are gone from the live file.
            Assert.Single(onDisk.Assignments);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedScheduledTaskStore_StillAcceptsTheWrite_AndQuarantinesWhatItCan(bool truncated)
        {
            // The gate drove exactly this: one AddTask destroyed an operator's "Nightly assessment"
            // with no quarantine and nothing said. The tier stands — a schedule is re-typable and
            // carries no credential — but the loss is now announced and the copy is now taken.
            var path = Path_("scheduled-tasks.json");
            if (truncated)
                WriteTruncated(path, "{\"version\":\"1.0\",\"tasks\":[{\"name\":\"Nightly assessment\",");
            else
                WriteEmpty(path);

            var svc = new ScheduledTaskDefinitionService(NullLogger<ScheduledTaskDefinitionService>.Instance, path);
            Assert.True(svc.IsStoreDamaged);

            // Taken while the store is still damaged, which is the only state in which this
            // sentence is shown to anyone — a successful save makes the store Loaded again.
            var recovery = svc.DescribeStoreRecovery();

            svc.AddTask(new ScheduledTaskDefinition { Name = "Ad-hoc", Query = "SELECT 1" });

            var onDisk = ConfigFileHelper.Load<ScheduledTasksFile>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Equal("Ad-hoc", Assert.Single(onDisk.Tasks).Name);   // the honest cost, asserted

            // Malformed content IS preserved; a 0-byte file has nothing to preserve, and the
            // recovery sentence must agree with the directory either way.
            var quarantined = Directory.GetFiles(_dir, "scheduled-tasks.json.rejected-*").Any();
            Assert.Equal(truncated, quarantined);
            Assert.Equal(quarantined, recovery.Contains(".rejected-", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedAlertDefinitionStore_StillAcceptsTheWrite_AndQuarantinesWhatItCan(bool truncated)
        {
            var path = Path_("alert-definitions.json");
            if (truncated)
                WriteTruncated(path, "{\"version\":\"1.0\",\"alerts\":[{\"id\":\"cpu-high\",");
            else
                WriteEmpty(path);

            var svc = new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, path);
            Assert.True(svc.IsStoreDamaged);
            Assert.Empty(svc.GetAllAlerts());

            var recovery = svc.DescribeStoreRecovery();   // while damaged — see above

            // UpdateGlobalDefaults is the one mutator that saves unconditionally — UpdateAlert and
            // SetAlertEnabled find nothing to change in an empty catalogue and never reach Save.
            svc.UpdateGlobalDefaults(new AlertGlobalDefaults { CooldownMinutes = 11 });

            var onDisk = ConfigFileHelper.Load<AlertDefinitionsFile>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Equal(11, onDisk.GlobalDefaults.CooldownMinutes);

            var quarantined = Directory.GetFiles(_dir, "alert-definitions.json.rejected-*").Any();
            Assert.Equal(truncated, quarantined);
            Assert.Equal(quarantined, recovery.Contains(".rejected-", StringComparison.Ordinal));
        }

        // ── alert-templates.json (alerts-r1-09): re-authorable notification template text ──
        //
        //  Announce tier, the sibling of alert-definitions.json. Before the guard, Load did
        //  `?? new()` on a literal-null file and then logged "Alert templates loaded" at Information
        //  — a false success over built-in defaults — and the next Save wrote those defaults over
        //  the operator's customised templates with no announcement and no quarantine.

        [Fact]
        public void AlertTemplateStore_ThatDeserialisesToNull_IsDamaged_NotASilentSuccess()
        {
            var path = Path_("alert-templates.json");
            File.WriteAllText(path, "null");   // valid JSON, no object — the exact r1-09 case

            var svc = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance, path);

            Assert.True(svc.IsStoreDamaged);
            Assert.Contains(".rejected-", svc.DescribeStoreRecovery(), StringComparison.Ordinal);
            Assert.True(Directory.GetFiles(_dir, "alert-templates.json.rejected-*").Any(),
                "a file with content that did not load must be quarantined before the next save");
        }

        [Theory]
        [InlineData(true)]      // truncated  → quarantined
        [InlineData(false)]     // 0-byte     → not quarantined
        public void DamagedAlertTemplateStore_StillAcceptsTheWrite_AndQuarantinesWhatItCan(bool truncated)
        {
            var path = Path_("alert-templates.json");
            if (truncated)
                WriteTruncated(path, "{\"Email\":{\"Subject\":\"the operator's own subject\",");
            else
                WriteEmpty(path);

            var svc = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance, path);
            Assert.True(svc.IsStoreDamaged);

            var recovery = svc.DescribeStoreRecovery();   // while damaged — see AlertDefinitionService

            // Announce tier: template text carries no secret and is re-authorable, so the write goes
            // through (the honest cost, asserted rather than commented).
            svc.Update(new AlertTemplateConfig());

            var onDisk = ConfigFileHelper.Load<AlertTemplateConfig>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);

            var quarantined = Directory.GetFiles(_dir, "alert-templates.json.rejected-*").Any();
            Assert.Equal(truncated, quarantined);
            Assert.Equal(quarantined, recovery.Contains(".rejected-", StringComparison.Ordinal));
        }

        [Fact]
        public void HealthyAlertTemplateStore_LoadsAndIsNotDamaged()
        {
            var path = Path_("alert-templates.json");
            ConfigFileHelper.Save(path, new AlertTemplateConfig());

            var svc = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance, path);
            Assert.False(svc.IsStoreDamaged);
        }

        [Fact]
        public void MissingAlertTemplateStore_IsAFreshInstall_NotDamage()
        {
            var path = Path_("alert-templates.json");
            Assert.False(File.Exists(path));

            var svc = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance, path);
            Assert.False(svc.IsStoreDamaged);
            Assert.True(File.Exists(path), "a fresh install writes the defaults on first run");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DamagedBpScriptStore_RebuildsItselfFromTheFolderAtStartup(bool truncated)
        {
            var scripts = Path_("BPScripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(System.IO.Path.Combine(scripts, "check-one.sql"), "SELECT 1;");
            var path = Path_("bp-scripts.json");
            if (truncated) WriteTruncated(path, "{\"Scripts\":[{\"FileName\":\"check-one.sql\","); else WriteEmpty(path);

            // Construction alone does it: load, sync from folder, save. No operator involved.
            var svc = new BPScriptService(NullLogger<BPScriptService>.Instance, scripts, path);

            var onDisk = ConfigFileHelper.Load<BPScriptConfig>(path, null, out var outcome);
            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
            Assert.Single(onDisk.Scripts);
            Assert.Equal("check-one.sql", onDisk.Scripts[0].FileName);
            Assert.Single(svc.GetConfig().Scripts);

            // The script's CONTENT was never in this store and is untouched — which is the reason
            // this one is announced rather than refused.
            Assert.Equal("SELECT 1;", File.ReadAllText(System.IO.Path.Combine(scripts, "check-one.sql")));
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  The register: one sentence, conditioned on what the loader actually did.
        // ══════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void RecoveryAdviceClaimsACopyExactlyWhenACopyExists()
        {
            // Not a wording test: it compares the claim against the directory listing. Malformed
            // content IS quarantined; a 0-byte file is NOT, and four surfaces once told operators
            // to restore from a copy that had never been written.
            var malformed = Path_("malformed.json");
            WriteTruncated(malformed, "{\"a\":1,");
            ConfigFileHelper.Load<OwnerAssignmentStoreData>(malformed, null, out var o1, out var q1);
            Assert.Equal(
                Directory.GetFiles(_dir, "malformed.json.rejected-*").Any(),
                ConfigFileHelper.DescribeStoreRecovery(o1, q1).Contains(".rejected-", StringComparison.Ordinal));

            var empty = Path_("empty.json");
            WriteEmpty(empty);
            ConfigFileHelper.Load<OwnerAssignmentStoreData>(empty, null, out var o2, out var q2);
            Assert.Equal(
                Directory.GetFiles(_dir, "empty.json.rejected-*").Any(),
                ConfigFileHelper.DescribeStoreRecovery(o2, q2).Contains(".rejected-", StringComparison.Ordinal));
        }

        [Fact]
        public void InspectStore_AgreesWithLoad_AndTakesNoCopy()
        {
            // The probe and the loader must classify identically — a probe that disagreed would be
            // a guard firing on files the loader read fine, or missing files it did not.
            foreach (var (name, write) in new (string, Action<string>)[]
                     {
                         ("probe-missing.json",   _ => { }),
                         ("probe-empty.json",     WriteEmpty),
                         ("probe-malformed.json", p => WriteTruncated(p, "{\"a\":1,")),
                         ("probe-good.json",      p => File.WriteAllText(p, "{\"Version\":1}")),
                     })
            {
                var p = Path_(name);
                write(p);

                var probed = ConfigFileHelper.InspectStore<OwnerAssignmentStoreData>(p);
                Assert.Empty(Directory.GetFiles(_dir, name + ".rejected-*"));   // probe took none

                ConfigFileHelper.Load<OwnerAssignmentStoreData>(p, null, out var loaded);
                Assert.Equal(loaded, probed);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  The IO half of the same defect: the write was ALLOWED, and then failed.
        //
        //  The damaged-store refusal was pre-checked and correct, so the mutators returned Saved
        //  and the UI logged "Added" while SaveUsers' own try/catch swallowed a thrown write.
        //  Forced here by making the atomic write's temp path a DIRECTORY, which is a real
        //  UnauthorizedAccessException from a real File.WriteAllText — not a mocked failure.
        // ══════════════════════════════════════════════════════════════════════════════════

        private RbacService RbacOver(out string usersPath)
        {
            var configPath = Path_("rbac-config.json");
            usersPath = Path_("rbac-users.json");
            ConfigFileHelper.Save(configPath, new RbacConfig());
            ConfigFileHelper.Save(usersPath, new List<RbacUser>());
            return new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);
        }

        [Fact]
        public void AddUser_WhenTheWriteThrows_ReportsWriteFailed_AndLeavesNoPhantomAccount()
        {
            var rbac = RbacOver(out var usersPath);
            Directory.CreateDirectory(usersPath + ".tmp");       // the atomic write cannot land

            var outcome = rbac.AddUser(new RbacUser { Email = "ghost@example.com", Role = AppRoles.Admin });

            Assert.Equal(StoreWriteOutcome.WriteFailed, outcome);
            Assert.Empty(ConfigFileHelper.Load<List<RbacUser>>(usersPath));   // nothing on disk
            Assert.Empty(rbac.GetUsers());                                    // and none in memory
        }

        [Fact]
        public void RemoveUser_WhenTheWriteThrows_ReportsWriteFailed_AndTheAccountStillHasAccess()
        {
            var rbac = RbacOver(out var usersPath);
            var user = new RbacUser { Email = "keep@example.com", Role = AppRoles.Admin };
            Assert.Equal(StoreWriteOutcome.Saved, rbac.AddUser(user));

            Directory.CreateDirectory(usersPath + ".tmp");

            Assert.Equal(StoreWriteOutcome.WriteFailed, rbac.RemoveUser(user.Id));

            // The revoke did not happen, and this process is not pretending it did — a removal held
            // only in memory would reverse itself at the next restart.
            Assert.Single(ConfigFileHelper.Load<List<RbacUser>>(usersPath));
            Assert.Single(rbac.GetUsers());
        }

        [Fact]
        public void UpdateUser_WhenTheWriteThrows_ReportsWriteFailed_AndTheOldRecordStands()
        {
            var rbac = RbacOver(out var usersPath);
            var user = new RbacUser { Email = "u@example.com", Role = AppRoles.Viewer };
            Assert.Equal(StoreWriteOutcome.Saved, rbac.AddUser(user));

            Directory.CreateDirectory(usersPath + ".tmp");

            var promoted = new RbacUser { Id = user.Id, Email = user.Email, Role = AppRoles.Admin };
            Assert.Equal(StoreWriteOutcome.WriteFailed, rbac.UpdateUser(promoted));

            Assert.Equal(AppRoles.Viewer, ConfigFileHelper.Load<List<RbacUser>>(usersPath)[0].Role);
            Assert.Equal(AppRoles.Viewer, rbac.GetUsers()[0].Role);
        }

        [Fact]
        public void UpdateConfig_WhenTheWriteThrows_ReportsWriteFailed_AndTheServiceStillReportsTheOldConfig()
        {
            var configPath = Path_("rbac-config.json");
            var usersPath = Path_("rbac-users.json");
            ConfigFileHelper.Save(configPath, new RbacConfig { Enabled = false });
            ConfigFileHelper.Save(usersPath, new List<RbacUser>());
            var rbac = new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);

            Directory.CreateDirectory(configPath + ".tmp");

            Assert.Equal(StoreWriteOutcome.WriteFailed, rbac.UpdateConfig(new RbacConfig { Enabled = true }));

            Assert.False(ConfigFileHelper.Load<RbacConfig>(configPath).Enabled);
            Assert.False(rbac.Config.Enabled);   // the banner and the log must not describe a config that exists nowhere
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  alerts-r2-11 — AlertDefinitionService.Save was void and swallowed a thrown write, so the
        //  page mutated the in-memory model, toasted "Alert definition saved" and closed — while the
        //  write never reached disk and reverted on the next restart. Save now returns WriteFailed and
        //  the mutators roll the in-memory model back, the same IO half the RBAC tests above pin.
        //  Forced by making the atomic write's temp path a DIRECTORY (a real UnauthorizedAccess).
        // ══════════════════════════════════════════════════════════════════════════════════

        private static AlertDefinitionService AlertDefsOver(string path, params AlertDefinition[] alerts)
        {
            var file = new AlertDefinitionsFile
            {
                GlobalDefaults = new AlertGlobalDefaults { CooldownMinutes = 5 },
            };
            file.Alerts.AddRange(alerts);
            ConfigFileHelper.Save(path, file);
            return new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance, path);
        }

        [Fact]
        public void UpdateGlobalDefaults_WhenTheWriteThrows_ReportsWriteFailed_AndTheServiceStillReportsTheOldValue()
        {
            var path = Path_("alert-definitions.json");
            var svc = AlertDefsOver(path, new AlertDefinition { Id = "cpu", Name = "CPU", Enabled = true });
            Assert.False(svc.IsStoreDamaged);

            Directory.CreateDirectory(path + ".tmp");    // the atomic write cannot land

            Assert.Equal(StoreWriteOutcome.WriteFailed,
                svc.UpdateGlobalDefaults(new AlertGlobalDefaults { CooldownMinutes = 99 }));

            // Rolled back in memory, and disk unchanged — the retention setting the page shows must
            // not describe a value that exists nowhere and would revert on restart.
            Assert.Equal(5, svc.GetGlobalDefaults().CooldownMinutes);
            Assert.Equal(5, ConfigFileHelper.Load<AlertDefinitionsFile>(path).GlobalDefaults.CooldownMinutes);
        }

        [Fact]
        public void SetAlertEnabled_WhenTheWriteThrows_ReportsWriteFailed_AndTheToggleDoesNotStick()
        {
            var path = Path_("alert-definitions.json");
            var svc = AlertDefsOver(path, new AlertDefinition { Id = "cpu", Name = "CPU", Enabled = true });

            Directory.CreateDirectory(path + ".tmp");

            Assert.Equal(StoreWriteOutcome.WriteFailed, svc.SetAlertEnabled("cpu", false));

            Assert.True(svc.GetAlert("cpu")!.Enabled);   // memory rolled back…
            Assert.True(ConfigFileHelper.Load<AlertDefinitionsFile>(path).Alerts.Single().Enabled);  // …and disk untouched
        }

        [Fact]
        public void UpdateAlert_WhenTheWriteThrows_ReportsWriteFailed_AndTheOldDefinitionStands()
        {
            var path = Path_("alert-definitions.json");
            var svc = AlertDefsOver(path, new AlertDefinition { Id = "cpu", Name = "CPU", Enabled = true });

            Directory.CreateDirectory(path + ".tmp");

            var edited = new AlertDefinition { Id = "cpu", Name = "CPU RENAMED", Enabled = true };
            Assert.Equal(StoreWriteOutcome.WriteFailed, svc.UpdateAlert(edited));

            Assert.Equal("CPU", svc.GetAlert("cpu")!.Name);   // memory rolled back
            Assert.Equal("CPU", ConfigFileHelper.Load<AlertDefinitionsFile>(path).Alerts.Single().Name);
        }

        [Fact]
        public void HealthyAlertDefinitionStore_SetAlertEnabled_ActuallySaves()
        {
            // The control: without it the WriteFailed assertions could pass on a mutator that refuses
            // everything. A healthy store must still persist the toggle.
            var path = Path_("alert-definitions.json");
            var svc = AlertDefsOver(path, new AlertDefinition { Id = "cpu", Name = "CPU", Enabled = true });

            Assert.Equal(StoreWriteOutcome.Saved, svc.SetAlertEnabled("cpu", false));
            Assert.False(ConfigFileHelper.Load<AlertDefinitionsFile>(path).Alerts.Single().Enabled);
        }

        // ── reflection helpers (the pattern SettingsPersistenceTests established) ──

        private static void Repoint(object target, string field, string path) => Set(target, field, path);

        internal static void Set(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f!.SetValue(target, value);
        }

        internal static object? Invoke(object target, string method)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(m);
            return m!.Invoke(target, null);
        }
    }
}
