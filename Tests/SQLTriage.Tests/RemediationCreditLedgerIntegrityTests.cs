/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Cluster 7 of the remediation-safety lane (honesty hunt, 2026-08-25): remediation-r2-04 (the
    /// ledger's save was not atomic, while its own header said it was), remediation-r1-10 (a damaged
    /// ledger was indistinguishable from an empty one, everywhere the UI could look) and
    /// remediation-r2-05 (redeeming a grant never checked which server it credited).
    ///
    /// <para>All three were proved in-process by the hunt over real objects on temp paths, which is
    /// exactly how they are pinned here.</para>
    /// </summary>
    public sealed class RemediationCreditLedgerIntegrityTests : IDisposable
    {
        private readonly string _dir;

        public RemediationCreditLedgerIntegrityTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "remsafe-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup */ }
        }

        private string LedgerPath => Path.Combine(_dir, "remediation-credit-ledger.json");

        private static IBundleAccessor Bundle(int creditsPerServer) => new FakeBundleAccessor
        {
            Tier = Tier.Full,
            Features = new BundleFeatures(false, false, false, Array.Empty<int>(),
                Remediation: true, RemediationCreditsPerServer: creditsPerServer),
        };

        private PersistedRemediationCreditLedger NewLedger(int creditsPerServer = 10) =>
            new(Bundle(creditsPerServer), NullLogger<PersistedRemediationCreditLedger>.Instance,
                grants: null, pathOverride: LedgerPath);

        /// <summary>
        /// Second opinion for the watcher below: is the ledger's NAME absent from the directory?
        /// <para>File.Exists answers false for two different facts. The name is gone, or the name is
        /// there and the call did not succeed. Only the first is the property under test. This reads
        /// the DIRECTORY ENTRY instead, which separates the two without softening anything: during a
        /// real delete-then-move window the name genuinely is gone and the miss is still recorded.
        /// Proved by re-running the delete-then-move mutation against this watcher.</para>
        /// </summary>
        private bool LedgerNameIsGoneFromTheDirectory()
        {
            try { return !Directory.EnumerateFiles(_dir, "remediation-credit-ledger.json").Any(); }
            catch { return false; } // could not tell, so do not claim a miss nobody observed
        }

        // ── r2-04: the save leaves no moment where the ledger is absent ───────────

        [Fact]
        public void ASaveNeverLeavesTheLedgerFileMissing()
        {
            // The window this closes is small and real: tmp -> DELETE -> move. A crash, a power loss
            // or an AV quarantine landing between the delete and the move leaves no file, absent is
            // classified Missing, Missing is deliberately NOT damage, and the next start reads spend
            // = 0 and restores the whole paid allocation. The window cannot be stepped into on
            // demand, so it is WATCHED: the ledger is saved repeatedly while the kernel reports
            // every filename change in the directory. Not one report of the ledger name being
            // DELETED is the property; a delete-then-move reports one per save.
            var ledger = NewLedger(creditsPerServer: 100000);
            var first = ledger.Reserve("srv1", 1)!;
            ledger.Commit(first); // the file now exists
            Assert.True(File.Exists(LedgerPath));

            // THE INSTRUMENT PERTURBS THE SUBJECT, AND THE TEST IS BUILT AROUND THAT.
            // MEASURED outside the app, same write idiom, same watcher loop, three trials each:
            // with the hot loop, MoveFileEx(REPLACE_EXISTING) failed 138, 144 and 202 times out of
            // 400 and sometimes left the .tmp behind; without it, 0 of 400 three times over and no
            // .tmp. So the watched phase can only certify what a FAILED replace still shows, and a
            // failed replace leaves the PREVIOUS ledger in place, which is exactly the absence
            // property. Anything about the writer's tidiness is measured AFTER the watcher stops.
            //
            // Kernel change notifications were tried instead and cannot decide this: a
            // rename-with-replace reports the replaced name as deleted, identically to a real
            // delete-then-move. MEASURED, not assumed. Sampling is the only instrument that sees
            // the difference, so sampling it is.
            var stop = new ManualResetEventSlim(false);
            var samples = 0;
            var missedAt = -1;

            var watcher = Task.Run(() =>
            {
                while (!stop.IsSet)
                {
                    if (!File.Exists(LedgerPath) && missedAt < 0 && LedgerNameIsGoneFromTheDirectory())
                        missedAt = samples;
                    samples++;
                    Thread.Yield();
                }
            });

            try
            {
                for (var i = 0; i < 400; i++)
                {
                    var r = ledger.Reserve("srv1", 1)!;
                    ledger.Commit(r);
                }
            }
            finally
            {
                stop.Set();
                watcher.Wait(TimeSpan.FromSeconds(10));
            }

            Assert.True(samples > 0, "the watcher never ran, so this proved nothing");
            Assert.True(missedAt < 0,
                $"the ledger file was absent during a save (first seen at sample {missedAt} of {samples}). " +
                "That interval is where a crash hands back the customer's whole allocation.");

            // Unwatched, so nothing is racing the rename: one more save, then the writer's tidiness.
            ledger.Commit(ledger.Reserve("srv1", 1)!);
            Assert.True(File.Exists(LedgerPath), "the ledger is not there after the saves");
            Assert.False(File.Exists(LedgerPath + ".tmp"), "a .tmp was left behind by an unwatched save");
        }

        [Fact]
        public void ASavedLedgerSurvivesAReopen_WithItsSpendIntact()
        {
            // 30, not 10: a DevBridge build floors the per-server allocation at 25, and this suite
            // must not depend on which build it runs under.
            var ledger = NewLedger(creditsPerServer: 30);
            for (var i = 0; i < 7; i++) ledger.Commit(ledger.Reserve("srv1", 1)!);
            Assert.Equal(23, ledger.AvailableFor("srv1"));

            var reopened = NewLedger(creditsPerServer: 30);
            Assert.False(reopened.IsStoreDamaged);
            Assert.Equal(23, reopened.AvailableFor("srv1"));
            Assert.Equal(7, reopened.GetBreakdown("srv1").Committed);
        }

        [Fact]
        public void AnAbsentLedgerStillReadsAsAFreshInstall_WhichIsWhyTheWriteWindowMattered()
        {
            // Pinned deliberately, because it is the mechanism, not a bug: Missing is not damage, and
            // must not be, or a genuinely fresh install could never spend a credit. That is exactly
            // why the writer must never produce Missing out of a healthy ledger.
            var ledger = NewLedger(creditsPerServer: 30);
            for (var i = 0; i < 7; i++) ledger.Commit(ledger.Reserve("srv1", 1)!);
            Assert.Equal(23, ledger.AvailableFor("srv1"));

            File.Delete(LedgerPath);

            var reopened = NewLedger(creditsPerServer: 30);
            Assert.False(reopened.IsStoreDamaged);
            Assert.Equal(30, reopened.AvailableFor("srv1")); // the whole allocation, back
        }

        // ── r1-10: damage is expressible through the seam the UI actually holds ───

        [Fact]
        public void ADamagedLedger_SaysSo_ThroughTheInterfaceTheUiInjects()
        {
            File.WriteAllText(LedgerPath, "{ this is not json");

            // Through the INTERFACE, not the concrete class: /remediation injects
            // IRemediationCreditLedger, and before this fix the interface had no way to ask.
            IRemediationCreditLedger ledger = NewLedger(creditsPerServer: 10);

            Assert.True(ledger.IsStoreDamaged);
            Assert.NotEmpty(ledger.DescribeStoreRecovery());

            // Fail closed, unchanged. The point is that zero now has a companion fact.
            Assert.Equal(0, ledger.AvailableFor("srv1"));
            Assert.Equal(new CreditBreakdown(0, 0, 0, 0), ledger.GetBreakdown("srv1"));
            Assert.Null(ledger.Reserve("srv1", 1));
        }

        [Fact]
        public void ASpentOutServerAndADamagedLedger_AreDistinguishable()
        {
            // Both report Available 0. Before the damage signal reached the seam, that was the whole
            // story the page had, so it printed "Out of change credits ... add more credits below"
            // over a ledger that could not be read, and a grant redeemed on that advice was burnt
            // for nothing.
            var healthy = NewLedger(creditsPerServer: 2);
            healthy.Commit(healthy.Reserve("srv1", healthy.GetBreakdown("srv1").Allocation)!);
            Assert.Equal(0, healthy.AvailableFor("srv1"));
            Assert.False(healthy.IsStoreDamaged);
            Assert.Empty(healthy.DescribeStoreRecovery()); // nothing to recover from

            File.WriteAllText(LedgerPath, "");
            var damaged = NewLedger(creditsPerServer: 2);
            Assert.Equal(0, damaged.AvailableFor("srv1"));
            Assert.True(damaged.IsStoreDamaged);
            Assert.NotEmpty(damaged.DescribeStoreRecovery());
        }

        [Fact]
        public void TheInMemoryLedger_IsNeverDamaged()
        {
            IRemediationCreditLedger ledger = new InMemoryRemediationCreditLedger(5);
            Assert.False(ledger.IsStoreDamaged);
            Assert.Empty(ledger.DescribeStoreRecovery());
        }

        // ── r2-05: a grant is checked against the server it can actually credit ───

        [Fact]
        public void AGrantForTheOperatingServer_IsRedeemedAndSaysItIsSpendableHere()
        {
            var match = RemediationGrantTarget.Classify(".", ".", new[] { ".", "SRV2" });
            Assert.Equal(GrantTargetMatch.OperatingServer, match);

            var sentence = RemediationGrantTarget.DescribeSuccess(match, ".", ".", 50, new DateTime(2027, 1, 1));
            Assert.Contains("available on this server now", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void AGrantForAnotherConfiguredServer_IsRedeemed_AndSaysWhereTheCreditsWent()
        {
            var match = RemediationGrantTarget.Classify("SRV2", ".", new[] { ".", "SRV2" });
            Assert.Equal(GrantTargetMatch.AnotherConfiguredServer, match);

            var sentence = RemediationGrantTarget.DescribeSuccess(match, "SRV2", ".", 50, new DateTime(2027, 1, 1));
            Assert.Contains("not available here", sentence, StringComparison.Ordinal);
            Assert.Contains("SRV2", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void TheMachineNameGrantTheHuntBurned_IsRefusedInsteadOfConsumed()
        {
            // The exact proved shape: the page shows DisplayServerName(".") = the machine name, the
            // ledger keys on ".", and a grant naming the machine name redeemed "successfully" while
            // Available(".") stayed 0 and the one-shot nonce was gone. Classified as unspendable now,
            // and the page refuses BEFORE calling Redeem, so the nonce survives.
            var match = RemediationGrantTarget.Classify("MSI", ".", new[] { "." });
            Assert.Equal(GrantTargetMatch.NoConfiguredServer, match);

            var refusal = RemediationGrantTarget.DescribeRefusal("MSI", new[] { "." });
            Assert.Contains("Nothing was redeemed", refusal, StringComparison.Ordinal);
            Assert.Contains("MSI", refusal, StringComparison.Ordinal);
            Assert.Contains("This install connects to: .", refusal, StringComparison.Ordinal);
        }

        [Fact]
        public void MatchingIsCaseInsensitiveOnTheRawName_TheSameRuleTheLedgerKeysBy()
        {
            // GrantedCreditsFor compares OrdinalIgnoreCase, so this verdict must too, or the check
            // would refuse a grant the ledger would happily have credited.
            Assert.Equal(GrantTargetMatch.OperatingServer,
                RemediationGrantTarget.Classify("srv1", "SRV1", new[] { "SRV1" }));
            Assert.Equal(GrantTargetMatch.AnotherConfiguredServer,
                RemediationGrantTarget.Classify("srv2", "SRV1", new[] { "SRV1", "SRV2" }));
        }

        [Fact]
        public void AGrantWithNoServerName_IsRefused()
        {
            Assert.Equal(GrantTargetMatch.NoConfiguredServer,
                RemediationGrantTarget.Classify(null, "SRV1", new[] { "SRV1" }));
            Assert.Equal(GrantTargetMatch.NoConfiguredServer,
                RemediationGrantTarget.Classify("   ", "SRV1", new[] { "SRV1" }));
        }

        [Fact]
        public void AnInstallWithNoServersConfigured_SaysSoRatherThanListingNothing()
        {
            var refusal = RemediationGrantTarget.DescribeRefusal("SRV9", new List<string>());
            Assert.Contains("no servers configured", refusal, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheGrantedCreditsRule_AgreesWithTheClassification()
        {
            // The verdict predicts spendability, so it is checked against the store that decides it.
            var grants = new RemediationGrantStore(Path.Combine(_dir, "grants.json"));
            var allocation = new SignedAllocation
            {
                Server = "SRV2",
                Credits = 50,
                IssuedUtc = DateTime.UtcNow.AddMinutes(-1),
                ExpiresUtc = DateTime.UtcNow.AddYears(1),
                Nonce = Guid.NewGuid().ToString("N"),
            };
            Assert.Null(grants.Redeem(allocation, DateTime.UtcNow));

            Assert.Equal(50, grants.GrantedCreditsFor("SRV2", DateTime.UtcNow));
            Assert.Equal(0, grants.GrantedCreditsFor(".", DateTime.UtcNow));

            // Same inputs, same answer: not spendable on ".", which is what the page now says.
            Assert.NotEqual(GrantTargetMatch.OperatingServer,
                RemediationGrantTarget.Classify("SRV2", ".", new[] { ".", "SRV2" }));
        }
    }
}
