/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Direct unit coverage of <see cref="CheckClassification"/> — the shared classification
    /// source promoted out of GovernanceService (2026-07-16). GovernanceServiceTests covers the
    /// same rules through the full scoring pipeline; these pin the raw predicates in isolation.
    /// </summary>
    public class CheckClassificationTests
    {
        private static CheckResult Plain(bool passed = true) => new() { CheckId = "C", Category = "Security", Passed = passed };

        [Fact]
        public void IsInfo_True_ForSeverityInfo()
        {
            var r = Plain();
            r.Severity = "info"; // case-insensitive
            Assert.True(CheckClassification.IsInfo(r));
        }

        [Fact]
        public void IsInfo_True_ForVerdictInfo_EvenWithNonInfoSeverity()
        {
            // The 2026-07-16 gap this file closes: a verdict-contract check (e.g.
            // SQLT-CUSTOM-MAXDOP) can declare Severity=High/Medium/etc. and still return an
            // INFO verdict from its SQL body.
            var r = Plain();
            r.Severity = "High";
            r.Verdict = "info"; // case-insensitive
            Assert.True(CheckClassification.IsInfo(r));
        }

        [Fact]
        public void IsInfo_False_WhenNeitherSeverityNorVerdictIsInfo()
        {
            var r = Plain();
            r.Severity = "High";
            r.Verdict = "PASS";
            Assert.False(CheckClassification.IsInfo(r));
        }

        [Fact]
        public void IsSkip_True_ForSkipMessage()
        {
            var r = Plain();
            r.Message = "SKIP: not applicable to this edition";
            Assert.True(CheckClassification.IsSkip(r));
        }

        [Fact]
        public void IsSkip_True_ForErrorMessage()
        {
            var r = Plain();
            r.ErrorMessage = "permission denied";
            Assert.True(CheckClassification.IsSkip(r));
        }

        [Fact]
        public void IsSkip_False_ForOrdinaryResult()
        {
            var r = Plain();
            r.Message = "Check passed (value=0)";
            Assert.False(CheckClassification.IsSkip(r));
        }

        // ── Leading whitespace (2026-08-27, reports lane fix round) ──────────────────────────
        //
        // The reports lane consolidated a 4th copy of this ladder onto IsSkip. The copy it deleted
        // (ScheduledReportService's private IsSkipped) did Message.TrimStart() first and this one did
        // not, so the swap turned "  SKIP - x" from a skip into r.Passed — a green PASS on the rate
        // of an unattended, unreviewed client PDF. The trim belongs in the shared rule: fixing it
        // here fixes all four copies, and re-forking one of them is how the drift started.

        [Fact]
        public void IsMessagePrefixSkip_IgnoresLeadingWhitespace()
        {
            var r = Plain();
            r.Message = "  SKIP - not applicable to this edition";
            Assert.True(CheckClassification.IsMessagePrefixSkip(r));
            Assert.True(CheckClassification.IsSkip(r));
            Assert.False(CheckClassification.IsScorable(r), "a skip is out of the score either way");
        }

        [Fact]
        public void IsMessagePrefixSkip_IsTheMessageSignalOnly_NotTheErrorMessageOne()
        {
            // DailySummaryBuilder.IsError depends on exactly this split: the host-probe skip carries
            // a diagnostic ErrorMessage BESIDE its "SKIP —" prefix, so an arm that keys on the wider
            // IsSkip swallows genuine execution errors instead of counting them.
            var r = Plain();
            r.Message = "Error: could not run";
            r.ErrorMessage = "Login failed for user x.";
            Assert.False(CheckClassification.IsMessagePrefixSkip(r));
            Assert.True(CheckClassification.IsSkip(r));
        }

        [Fact]
        public void IsMessagePrefixSkip_False_ForAMessageThatMerelyContainsSkip()
        {
            var r = Plain();
            r.Message = "Backup jobs configured to SKIP verification";
            Assert.False(CheckClassification.IsMessagePrefixSkip(r));
        }

        [Fact]
        public void IsSkip_True_ForVerdictSkip_EvenWhenPassedTrue()
        {
            // Gate B1 (2026-07-16): a verdict-contract check can return Verdict=="SKIP" from its
            // SQL body (e.g. SQLT-CORE-00580 "Implement Appropriate AG Synchronization Mode" with
            // Message "No AGs configured."), rides Passed=true for the scoring tier same as PASS,
            // and previously had no Message-prefix or ErrorMessage signal — escaping into scored
            // Passed and a green PASS badge. Not scorable regardless of declared Severity.
            var r = Plain(passed: true);
            r.Severity = "High";
            r.Verdict = "skip"; // case-insensitive
            r.Message = "No AGs configured.";
            Assert.True(CheckClassification.IsSkip(r));
            Assert.False(CheckClassification.IsScorable(r));
        }

        [Fact]
        public void IsScorable_False_ForSkipAndInfo_True_Otherwise()
        {
            var skip = Plain();
            skip.Message = "SKIP: n/a";
            var info = Plain();
            info.Severity = "INFO";
            var verdictInfo = Plain();
            verdictInfo.Verdict = "INFO";
            var ordinary = Plain();

            Assert.False(CheckClassification.IsScorable(skip));
            Assert.False(CheckClassification.IsScorable(info));
            Assert.False(CheckClassification.IsScorable(verdictInfo));
            Assert.True(CheckClassification.IsScorable(ordinary));
        }

        [Fact]
        public void CountsAsPass_True_ForRawPass_And_ForAcceptedFail()
        {
            var pass = Plain(passed: true);
            var acceptedFail = Plain(passed: false);
            acceptedFail.IsAccepted = true;
            var openFail = Plain(passed: false);

            Assert.True(CheckClassification.CountsAsPass(pass));
            Assert.True(CheckClassification.CountsAsPass(acceptedFail));
            Assert.False(CheckClassification.CountsAsPass(openFail));
        }
    }
}
