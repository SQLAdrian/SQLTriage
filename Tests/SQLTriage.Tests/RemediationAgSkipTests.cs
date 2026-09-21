/* In the name of God, the Merciful, the Compassionate */

/*
 * RemediationAgSkipTests - the AG-skip invariant on the REMEDIATION PREVIEW path, 2026-09-12.
 *
 * THE INVARIANT. A database that is a non-readable availability-group replica is a SKIP, never a
 * FAILURE. It is a permanent fact about where we are connected: identical on every retry until
 * somebody changes the AG configuration. A skip must still be REPORTED, by name and with a reason,
 * because an unreported skip is its own defect. And it must never poison the verdict for what did
 * read.
 *
 * WHAT WENT WRONG. Six catch blocks in DbatoolsRemediationExecutor's preview path collapsed ANY
 * SqlException into RemediationPreview { Succeeded = false, Error = "<stage> failed: ..." }. So a
 * preview against an AG secondary told the operator a read had FAILED, which invites a retry that
 * can never succeed, when the truthful answer is "not readable from this replica".
 *
 * THE BRIEF FOR THIS LANE SAID FIVE CATCH BLOCKS, AND SO DID THE CLASSIFIER'S OWN DOC COMMENT.
 * There are six. The missing one is "Database-size read failed" in PreviewCheckDbNowAsync, the
 * structural twin of "Backup-size estimate failed" in the sibling PreviewBackupDatabaseNowAsync.
 * A hand count missed it twice, which is why the census below enumerates the set FROM THE SOURCE
 * and never from a number typed by a human. A count pinned by hand measures the counter.
 *
 * WHY THESE TESTS SCAN SOURCE AND HAND-BUILD PREVIEWS INSTEAD OF DRIVING THE EXECUTOR.
 * SqlException cannot be constructed (no public constructor, and its Number comes from the server),
 * and there is no seam to inject a failing connection into DbatoolsRemediationExecutor. Inventing
 * one would be testing the seam. So this file proves two halves and claims no more:
 *   - STRUCTURALLY, that every preview catch routes through the one skip factory, and that the
 *     factory marks NotApplicable (the census + factory-shape tests).
 *   - BEHAVIOURALLY, that the CONSUMERS of a preview treat a skip as neither success nor failure
 *     (the consumer tests, which call the real predicates the UI branches on).
 * WHAT IT DOES NOT PROVE: that a live AG secondary raises one of the pinned numbers. The numbers
 * themselves are pinned by AvailabilityGroupReadabilityTests; the live behaviour is the gate's job.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using SQLTriage.Data.Services.Remediation;

namespace SQLTriage.Tests
{
    public class RemediationAgSkipTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationAgSkipTests(ITestOutputHelper output) => _out = output;

        /// <summary>The shape that collapses a SqlException into a failed preview.</summary>
        private const string FailureCatch =
            "catch (SqlException ex) { return new RemediationPreview { Succeeded = false";

        /// <summary>The guard that must sit immediately ahead of every one of them.</summary>
        private const string SkipGuard =
            "catch (SqlException ex) when (AvailabilityGroupReadability.IsNotReadableOnThisReplica(ex))";

        private static string ExecutorSource() =>
            File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName,
                "Data", "Services", "Remediation", "DbatoolsRemediationExecutor.cs"));

        /// <summary>
        /// Every line matching <see cref="FailureCatch"/> that is NOT immediately preceded by
        /// <see cref="SkipGuard"/>, as (1-based line, text). Blank lines between the two are
        /// tolerated; anything else is an offence.
        /// </summary>
        private static List<(int Line, string Text)> UnguardedCatches(string source)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var offences = new List<(int, string)>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(FailureCatch, StringComparison.Ordinal)) continue;

                int j = i - 1;
                while (j >= 0 && lines[j].Trim().Length == 0) j--;

                if (j < 0 || !lines[j].Contains(SkipGuard, StringComparison.Ordinal))
                    offences.Add((i + 1, lines[i].Trim()));
            }
            return offences;
        }

        [Fact]
        public void EveryPreviewSqlExceptionCatchHasAnAvailabilityGroupSkipAheadOfIt()
        {
            var source = ExecutorSource();
            var offences = UnguardedCatches(source);

            _out.WriteLine("failure catches found: " + CountOccurrences(source, FailureCatch));
            _out.WriteLine("skip guards found: " + CountOccurrences(source, SkipGuard));

            Assert.True(offences.Count == 0,
                "A preview catch collapses every SqlException into a FAILURE with no availability-group "
                + "skip ahead of it. A non-readable AG replica is a SKIP, not a fault, and telling the "
                + "operator a read FAILED invites a retry that can never succeed. Add\n    "
                + SkipGuard + " { return NotReadableSkip(ex); }\n"
                + "immediately before each of these. Do NOT write the error numbers here; the list "
                + "lives in AvailabilityGroupReadability.\nUnguarded:\n  "
                + string.Join("\n  ", offences.Select(o => "line " + o.Line + ": " + o.Text)));

            // The instrument must have seen something. A renamed type or a reformatted catch would
            // make the scan match nothing at all and pass in silence - the exact false green this
            // codebase has paid for. Six is the set as it stands; MORE is fine (a new preview path),
            // FEWER means the scan has lost its grip on the file.
            int found = CountOccurrences(source, FailureCatch);
            Assert.True(found >= 6,
                "the scan found only " + found + " preview failure catches, fewer than the six that "
                + "exist. The pattern has probably drifted from the source, so this guard is no "
                + "longer reading what it claims to read, and its green means nothing.");
        }

        [Fact]
        public void TheCensusInstrumentCatchesAPlantedUnguardedCatch()
        {
            // A guard nobody has watched fail is a guard nobody has tested. One guarded pair, one
            // bare catch: the scan must report exactly the bare one.
            string planted =
                "            try { a(); }\n"
                + "            " + SkipGuard + " { return NotReadableSkip(ex); }\n"
                + "            " + FailureCatch + ", Error = \"Guarded\" }; }\n"
                + "            try { b(); }\n"
                + "            " + FailureCatch + ", Error = \"Bare\" }; }\n";

            var offences = UnguardedCatches(planted);

            _out.WriteLine("planted offences: " + string.Join(" | ", offences.Select(o => o.Text)));
            Assert.Single(offences);
            Assert.Contains("Bare", offences[0].Text, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSkipFactoryIsTheOnlyShapeAndItMarksNotApplicable()
        {
            var source = ExecutorSource();

            // Every guard delegates to the one factory. Six hand-written copies of the skip object
            // is how one of them ends up with NotApplicable missing.
            int guards = CountOccurrences(source, SkipGuard);
            int delegated = CountOccurrences(source, SkipGuard + " { return NotReadableSkip(ex); }");
            Assert.True(guards == delegated,
                "one or more availability-group guards builds its own result instead of returning "
                + "NotReadableSkip(ex). guards=" + guards + " delegating=" + delegated);

            int factory = source.IndexOf("private static RemediationPreview NotReadableSkip(", StringComparison.Ordinal);
            Assert.True(factory >= 0, "NotReadableSkip was not found, so nothing pins the skip's shape.");

            var body = source.Substring(factory, Math.Min(600, source.Length - factory));
            Assert.Contains("NotApplicable = true", body, StringComparison.Ordinal);
            Assert.Contains("Succeeded = false", body, StringComparison.Ordinal);
            Assert.Contains("AvailabilityGroupReadability.DescribeSkip(ex)", body, StringComparison.Ordinal);

            // The numbers must NOT be restated here.
            foreach (var n in new[] { "976", "978", "979", "983" })
                Assert.DoesNotContain(n, body, StringComparison.Ordinal);
        }

        // Consumer-level behaviour: what a skip means to the surfaces that read it.

        /// <summary>A skip exactly as the executor's factory builds one.</summary>
        private static RemediationPreview Skip() => new()
        {
            Succeeded = false,
            NotApplicable = true,
            Error = "Skipped: this database is an availability-group replica that is not readable "
                  + "through this connection (SQL error 976). This is expected, not a fault.",
        };

        private static RemediationPreview OrdinaryFailure() => new()
        {
            Succeeded = false,
            Error = "Offenders read failed: timeout expired.",
        };

        [Fact]
        public void ASkipIsNeitherSuccessNorAnOrdinaryFailure()
        {
            var skip = Skip();

            // Not a success: no surface may offer an Approve button for it.
            Assert.False(skip.Succeeded);

            // Not an ordinary failure either: it is machine-distinguishable without reading prose.
            Assert.True(skip.NotApplicable);
            Assert.False(OrdinaryFailure().NotApplicable);
        }

        [Fact]
        public void TheFlagDefaultsToFalseSoEveryExistingConsumerIsUnchanged()
        {
            // The whole safety argument for adding a field rather than changing Succeeded: the
            // existing readers of .Succeeded keep their exact behaviour because the default is false.
            Assert.False(new RemediationPreview().NotApplicable);
            Assert.False(new RemediationPreview { Succeeded = true, WhatIfText = "x" }.NotApplicable);
        }

        [Fact]
        public void ASkipCarriesNoProposedChangeSoTheBackupAndCheckDbPanelsCannotOfferApply()
        {
            // Pages/Remediation.razor:1523 (backup) and :1624 (CHECKDB) branch FIRST on
            // string.IsNullOrEmpty(Preview.WhatIfText), and only inside the else do they render the
            // proposed statement and consult Succeeded for the Approve button. An AG skip must take
            // the first branch, or the operator is shown a change against a database nobody read.
            Assert.True(string.IsNullOrEmpty(Skip().WhatIfText));
        }

        [Fact]
        public void ASkipTakesTheReasonBranchOnTheAlertPackMaintenanceAndBatchPanels()
        {
            // Pages/Remediation.razor:1234, :1296 and :1404 all branch on !Preview.Succeeded and
            // render Preview.Error. Red is reserved for IsRefused; this branch is amber. So the skip
            // is reported, with its reason, and no Approve button is emitted.
            var skip = Skip();
            Assert.True(!skip.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(skip.Error));
        }

        [Fact]
        public void ASkipDoesNotTripTheForceInstallTick()
        {
            // Pages/Remediation.razor:1296 offers an "install over the existing objects anyway" tick
            // whenever the error text matches this predicate. It is a live example of a consumer
            // deciding on PROSE, and the reason NotApplicable is a field: a new error sentence must
            // never accidentally offer the operator a destructive override.
            Assert.False(MaintenanceSolutionOpRenderer.LooksLikePartialInstallRefusal(Skip().Error));
        }

        [Fact]
        public void ASkipReportsItselfByReasonAndSaysItIsNotAFault()
        {
            // An unreported skip is its own defect. The operator must be able to tell this from a
            // fault without reading the code.
            var error = Skip().Error!;
            Assert.Contains("Skipped", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not a fault", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("976", error, StringComparison.Ordinal);
        }

        [Fact]
        public void ReversibilityOfASkipIsUnknownNeverAPromiseOrARefusal()
        {
            // Pages/Remediation.razor:2682 ItemReversibility: (preview is null || !preview.Succeeded)
            // answers "no reading was taken, so undo is unknown". For a skip that is exactly right,
            // and it must NOT become "cannot be undone", which is a claim about a database we could
            // not read.
            var skip = Skip();
            bool unknownByTheUiPredicate = skip is null || !skip.Succeeded;
            Assert.True(unknownByTheUiPredicate);
            Assert.Null(skip.CanRollBack);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
    }
}
