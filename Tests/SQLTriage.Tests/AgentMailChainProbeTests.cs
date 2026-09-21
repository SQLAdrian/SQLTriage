/* In the name of God, the Merciful, the Compassionate */

/*
 * AgentMailChainProbeTests - the operator-picker-mailchain lane's verdict rule, pinned offline.
 *
 * WHY THESE ARE THE TESTS THAT MATTER. The lane's risk is not "does the SQL run" - every statement
 * was exercised on .\old2017 and .\new2022 before a line of C# was written, and a wrong column name
 * shows up the first time a DBA opens the page. The risk is that the JUDGMENT is wrong in a way that
 * reads as confidence: a green badge over a server whose mail has never worked. So the reads are
 * proved live and the verdict is proved here, over rows CAPTURED FROM THOSE SAME TWO RIGS.
 *
 * WHAT IS REAL IN THIS FILE AND WHAT IS NOT:
 *   REAL       - Evaluate and DefaultOperator. The shipped functions, not a copy.
 *   REAL       - the two rig fixtures. Every number in New2022Rows() and Old2017Rows() was read off
 *                the rig on 2026-09-09 (probe-shape-out.txt in this lane's evidence folder), not
 *                invented: 20,797 failed items, zero sent ever, last failure 2026-07-27 18:28:30.303,
 *                event-log error 2026-07-27 18:33:22.403, operator DBA on 41 enabled alerts.
 *   SYNTHETIC  - the WORKS and stale-success cases, and they are LABELLED synthetic: neither rig has
 *                ever delivered a single mail item, so a live WORKS is unobtainable on this bench.
 *                That is the lane's honest residual and it is stated here rather than implied away.
 *   NOT HERE   - the readers. Nothing in this file opens a connection or fakes one; the columns,
 *                the denial behaviour and the collation are proved live, not in CI.
 */

using System;
using System.Linq;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public sealed class AgentMailChainProbeTests
    {
        // The clock every case is judged against, standing in for the SERVER's SYSDATETIME().
        private static readonly DateTime Now = new(2026, 9, 9, 1, 45, 0);

        private const string DbaAddress = "sqlalerts@sqldba.org";

        // ── 1. The two rigs, as they really are ──────────────────────────────────────────

        /// <summary>
        /// .\new2022, 2026-09-09: profile wired, Agent pointed at it, and NOT ONE mail item ever
        /// delivered against 20,797 failures. This is the case a configuration checker gets wrong -
        /// every link is present, so "is Database Mail set up" answers yes - and it is why WORKS is
        /// reserved for observed delivery.
        /// </summary>
        [Fact]
        public void New2022_ProfileWiredButNothingEverDelivered_IsBroken_AndNamesTheFailureDate()
        {
            var verdict = AgentMailChainProbe.Evaluate(New2022Rows(), Now);

            Assert.Equal(MailChainState.Broken, verdict.State);
            Assert.Equal(5, verdict.Link);
            Assert.Contains(DbaAddress, verdict.Headline, StringComparison.Ordinal);

            // THE REASON, after the 2026-09-09 F1 ruling: the rig is BOTH stopped and 44 days into
            // 20,797 failures, and both are true. The stopped queue is what gets printed, because it is
            // what stops the NEXT notification; see AStoppedQueue_OutranksTheFailureReason below.
            Assert.True(verdict.NamesTheStoppedQueue);

            // The evidence still carries the date and the server's own words, not a paraphrase.
            Assert.Contains("2026-07-27", verdict.Evidence!, StringComparison.Ordinal);
            Assert.Contains("no item has EVER been delivered", verdict.Evidence!, StringComparison.Ordinal);
            Assert.Contains("mail server failure", verdict.Evidence!, StringComparison.Ordinal);
            Assert.Contains("20797", verdict.Evidence!, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE RULE THAT MAKES THE CASE ABOVE WORK, isolated. The failure is 44 days old and there is
        /// no sent item to compare it against. A recency window on FAILURES - the obvious symmetry with
        /// the 30-day WORKS window - would print UNKNOWN over this rig. Never-sent counts.
        /// </summary>
        [Fact]
        public void AFailureOlderThanTheWorksWindow_WithNothingEverSent_IsStillBroken()
        {
            var rows = New2022Rows();
            rows.MailQueueReceiveEnabled = true;    // isolate this rule from the queue rule (F1)
            Assert.True((Now - rows.LastFailed!.Value).TotalDays > AgentMailChainProbe.WorksWindowDays,
                "this fixture is meant to hold a failure OUTSIDE the 30-day window; the rig's is 44 days old");

            Assert.Equal(MailChainState.Broken, AgentMailChainProbe.Evaluate(rows, Now).State);
        }

        /// <summary>
        /// .\old2017, 2026-09-09: Database Mail XPs is on, and there is no profile at all. Link 2 is
        /// the first thing that fails and the verdict says so by number, because "not configured" with
        /// no link named sends a DBA to read four things instead of one.
        /// </summary>
        [Fact]
        public void Old2017_NoProfileAtAll_IsNotConfigured_NamingLinkTwo()
        {
            var verdict = AgentMailChainProbe.Evaluate(Old2017Rows(), Now);

            Assert.Equal(MailChainState.NotConfigured, verdict.State);
            Assert.Equal(2, verdict.Link);
            Assert.Contains("Link 2", verdict.Headline, StringComparison.Ordinal);
            Assert.Contains("sysmail_profile returned no rows", verdict.Evidence!, StringComparison.Ordinal);
        }

        // ── 1b. THE STOPPED QUEUE (ruled 2026-09-09 after gate finding F1) ───────────────
        //
        // The tip never read MailQueueReceiveEnabled in Evaluate at all. These cases are the gate's
        // own probe inputs (evidence/operator-picker-mailchain-2026-09-09/gate/probe/r1r2-verdicts.txt),
        // where the shipped rule returned Works and Unknown over a queue that was switched off.

        /// <summary>
        /// The exact input that produced the wrong verdict: links 1-4 wired, the queue stopped, and a
        /// delivery two days old. A sent item is evidence about the PAST; is_receive_enabled = 0 is a
        /// fact about the NEXT notification, so it wins.
        /// </summary>
        [Fact]
        public void AStoppedQueue_BeatsADeliveryInsideTheWindow()
        {
            var verdict = AgentMailChainProbe.Evaluate(QueueStoppedWithDelivery(), Now);

            Assert.Equal(MailChainState.Broken, verdict.State);
            Assert.True(verdict.NamesTheStoppedQueue);
            Assert.Contains("queue is stopped", verdict.Headline, StringComparison.Ordinal);
            Assert.Contains(AgentMailChainProbe.QueueStoppedEvidence, verdict.Evidence!, StringComparison.Ordinal);
        }

        /// <summary>The same rows with nothing ever sent. The tip returned UNKNOWN here - "links 1-4 are
        /// configured" - which reads as "nothing to worry about" over a queue that is switched off.</summary>
        [Fact]
        public void AStoppedQueue_WithNothingEverSent_IsBroken_NotUnknown()
        {
            var rows = QueueStoppedWithDelivery();
            rows.LastSent = null;
            rows.SentCount = 0;

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.Broken, verdict.State);
            Assert.Contains(AgentMailChainProbe.QueueStoppedEvidence, verdict.Evidence!, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE OTHER HALF, and the reason the field is bool? and not bool. msdb.sys.service_queues
        /// returns ZERO ROWS with no error to a non-sysadmin (proved on both rigs), so NULL means
        /// "nobody could see it" - and a BROKEN verdict off that would be the fabrication this whole
        /// file exists to prevent.
        /// </summary>
        [Fact]
        public void AnUnknownQueue_DecidesNothing_AndTheDeliveryStillEarnsWorks()
        {
            var rows = QueueStoppedWithDelivery();
            rows.MailQueueReceiveEnabled = null;

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.Works, verdict.State);
            Assert.False(verdict.NamesTheStoppedQueue);
        }

        [Fact]
        public void AReceivingQueue_DecidesNothingEither()
        {
            var rows = QueueStoppedWithDelivery();
            rows.MailQueueReceiveEnabled = true;

            Assert.Equal(MailChainState.Works, AgentMailChainProbe.Evaluate(rows, Now).State);
        }

        /// <summary>
        /// THE ORDER, stated. .\new2022 is BOTH stopped and 44 days into 20,797 failures; both reasons
        /// are true. The queue reason is the one PRINTED, because it is the one that stops the next
        /// notification, and the failure evidence is APPENDED rather than dropped - so the badge still
        /// carries the date and the server's own words.
        /// </summary>
        [Fact]
        public void AStoppedQueue_OutranksTheFailureReason_AndKeepsItsEvidence()
        {
            var verdict = AgentMailChainProbe.Evaluate(New2022Rows(), Now);

            Assert.True(verdict.NamesTheStoppedQueue);
            Assert.StartsWith(AgentMailChainProbe.QueueStoppedEvidence, verdict.Evidence!, StringComparison.Ordinal);
            Assert.Contains("2026-07-27", verdict.Evidence!, StringComparison.Ordinal);
            Assert.Contains("20797", verdict.Evidence!, StringComparison.Ordinal);
        }

        /// <summary>A stopped queue is not a verdict until links 1-4 hold: .\old2017 with its queue
        /// stopped is still NOT CONFIGURED at link 2, because there is no profile to send through.</summary>
        [Fact]
        public void AStoppedQueue_DoesNotOutrankAFailingLink()
        {
            var rows = Old2017Rows();
            rows.MailQueueReceiveEnabled = false;

            Assert.Equal(MailChainState.NotConfigured, AgentMailChainProbe.Evaluate(rows, Now).State);
        }

        /// <summary>...and it DOES outrank an unreadable link 5: a history nobody could read cannot
        /// change the fact that nothing queued now will leave the instance.</summary>
        [Fact]
        public void AStoppedQueue_OutranksAnUnreadableLinkFive()
        {
            var rows = QueueStoppedWithDelivery();
            rows.Gaps.Add(new MailChainGap(5, "Database Mail delivery history", "not sysadmin"));

            Assert.Equal(MailChainState.Broken, AgentMailChainProbe.Evaluate(rows, Now).State);
        }

        // ── 2. The delivery branches ─────────────────────────────────────────────────────

        /// <summary>SYNTHETIC (neither rig has ever delivered a mail item): a send five days ago.</summary>
        [Fact]
        public void ASentItemInsideTheWindow_IsTheOnlyThingThatEarnsWorks()
        {
            var rows = New2022Rows();
            rows.LastFailed = null;
            rows.LastEventLogError = null;
            rows.LastEventLogMessage = null;
            rows.FailedCount = 0;
            rows.MailQueueReceiveEnabled = true;    // isolate this rule from the queue rule (F1)
            rows.SentCount = 3;
            rows.LastSent = Now.AddDays(-5);

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.Works, verdict.State);
            Assert.Contains("2026-09-04", verdict.Headline, StringComparison.Ordinal);
        }

        /// <summary>SYNTHETIC. A success older than the window is not evidence that mail works TODAY.
        /// It is the absence of evidence either way, which is what UNKNOWN means here.</summary>
        [Fact]
        public void ASentItemOlderThanTheWindow_WithNothingSince_IsUnknown_NotWorks()
        {
            var rows = New2022Rows();
            rows.LastFailed = null;
            rows.LastEventLogError = null;
            rows.LastEventLogMessage = null;
            rows.FailedCount = 0;
            rows.MailQueueReceiveEnabled = true;    // isolate this rule from the queue rule (F1)
            rows.SentCount = 1;
            rows.LastSent = Now.AddDays(-31);

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.Unknown, verdict.State);
            Assert.Contains("more than 30 days ago", verdict.Headline, StringComparison.Ordinal);
        }

        /// <summary>SYNTHETIC. A failure AFTER a good delivery is still BROKEN: the chain worked and
        /// then stopped, which is the case an operator most needs told.</summary>
        [Fact]
        public void AFailureAfterAGoodDelivery_IsBroken_EvenInsideTheWindow()
        {
            var rows = New2022Rows();
            rows.MailQueueReceiveEnabled = true;    // isolate this rule from the queue rule (F1)
            rows.SentCount = 4;
            rows.LastSent = Now.AddDays(-6);
            rows.LastFailed = Now.AddDays(-2);
            rows.LastEventLogError = null;
            rows.LastEventLogMessage = null;

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.Broken, verdict.State);
            Assert.Contains("the last delivery was", verdict.Evidence!, StringComparison.Ordinal);
        }

        /// <summary>SYNTHETIC. A failure BEFORE the last good delivery is history, not a verdict.</summary>
        [Fact]
        public void AFailureBeforeTheLastGoodDelivery_DoesNotOverrideWorks()
        {
            var rows = New2022Rows();
            rows.MailQueueReceiveEnabled = true;    // isolate this rule from the queue rule (F1)
            rows.SentCount = 4;
            rows.LastSent = Now.AddDays(-2);
            rows.LastFailed = Now.AddDays(-6);
            rows.LastEventLogError = Now.AddDays(-7);

            Assert.Equal(MailChainState.Works, AgentMailChainProbe.Evaluate(rows, Now).State);
        }

        /// <summary>Links 1-4 hold, the history was READ, and it is empty. Nothing has been tried, so
        /// nothing is known - and the verdict says which button answers the question.</summary>
        [Fact]
        public void EverythingConfiguredAndNothingEverAttempted_IsUnknown()
        {
            var rows = New2022Rows();
            rows.LastSent = null;
            rows.LastFailed = null;
            rows.LastEventLogError = null;
            rows.LastEventLogMessage = null;
            rows.SentCount = 0;
            rows.FailedCount = 0;
            rows.MailQueueReceiveEnabled = true;    // isolate this rule from the queue rule (F1)
            rows.MatchedCount = 0;

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.Unknown, verdict.State);
            Assert.Contains("no notification has ever been sent", verdict.Headline, StringComparison.Ordinal);
            Assert.Contains("Send a test notification", verdict.Evidence!, StringComparison.Ordinal);
        }

        // ── 3. COULD NOT READ: the branch that stops a blind spot reading as a finding ────

        /// <summary>
        /// THE PERMISSION TRAP. A non-sysadmin connection sees only the items IT sent, so an empty
        /// history means nothing. This fixture even carries a sent item inside the window - the shape
        /// that would otherwise earn WORKS - to prove the verdict is decided by WHO ASKED and not by
        /// what came back.
        /// </summary>
        [Fact]
        public void ANonSysadminConnection_IsCouldNotRead_EvenWithRowsInHand()
        {
            var rows = New2022Rows();
            // A non-sysadmin reads msdb.sys.service_queues as ZERO ROWS with no error (proved on
            // both rigs), so the queue is UNKNOWN to it - never the false that would decide the verdict.
            rows.MailQueueReceiveEnabled = null;
            rows.IsSysadmin = false;
            rows.LastSent = Now.AddDays(-1);
            rows.SentCount = 9;
            rows.LastFailed = null;
            rows.LastEventLogError = null;
            rows.Gaps.Add(new MailChainGap(5,
                "Database Mail delivery history (msdb.dbo.sysmail_allitems)",
                "This connection is not a member of the sysadmin server role."));

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.CouldNotRead, verdict.State);
            Assert.Equal(5, verdict.Link);
            Assert.Contains("sysmail_allitems", verdict.Headline, StringComparison.Ordinal);

            // …and it still says the configuration half was fine, which is the actionable part.
            Assert.Contains("Links 1-4 are configured", verdict.Headline, StringComparison.Ordinal);
        }

        /// <summary>
        /// A denied read behind links 1-4 outranks everything after it. Msg 229 on sysmail_profile is
        /// what a non-sysadmin actually gets (proved on both rigs); reading that as "no profile
        /// exists" would print NOT CONFIGURED over a correctly configured server.
        /// </summary>
        [Fact]
        public void ADeniedConfigurationRead_IsCouldNotRead_NamingTheRead_NotNotConfigured()
        {
            var rows = New2022Rows();
            rows.Profiles.Clear();          // exactly what a denied read leaves behind
            rows.Gaps.Add(new MailChainGap(2, "Database Mail profiles (msdb.dbo.sysmail_profile)",
                "The SELECT permission was denied on the object 'sysmail_profile', database 'msdb', schema 'dbo'."));

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.CouldNotRead, verdict.State);
            Assert.Equal(2, verdict.Link);
            Assert.Contains("sysmail_profile", verdict.Headline, StringComparison.Ordinal);
            Assert.Contains("SELECT permission was denied", verdict.Evidence!, StringComparison.Ordinal);
        }

        /// <summary>A connection that never opened is link 0, and it is named before anything else.</summary>
        [Fact]
        public void AnUnreachableInstance_IsCouldNotRead_AtLinkZero()
        {
            var rows = new MailChainRows { ServerName = "SQL07", OperatorName = "DBA" };
            rows.Gaps.Add(new MailChainGap(0, "connect to SQL07", "A network-related or instance-specific error"));

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.CouldNotRead, verdict.State);
            Assert.Equal(0, verdict.Link);
        }

        // ── 4. The remaining links, each named by number ──────────────────────────────────

        [Fact]
        public void DatabaseMailOff_IsNotConfigured_NamingLinkOne()
        {
            var rows = New2022Rows();
            rows.DatabaseMailXpsValueInUse = 0;

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.NotConfigured, verdict.State);
            Assert.Equal(1, verdict.Link);
        }

        [Fact]
        public void AgentNotUsingDatabaseMail_IsNotConfigured_NamingLinkThree()
        {
            var rows = New2022Rows();
            rows.AgentUseDatabaseMail = 0;

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.NotConfigured, verdict.State);
            Assert.Equal(3, verdict.Link);
        }

        /// <summary>Agent naming a profile that is not on the instance is its own failure: the registry
        /// value survives a profile being renamed or dropped, and nothing else notices.</summary>
        [Fact]
        public void AgentPointingAtAMissingProfile_IsNotConfigured_NamingLinkThree()
        {
            var rows = New2022Rows();
            rows.AgentMailProfile = "Profile That Was Renamed";

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.NotConfigured, verdict.State);
            Assert.Equal(3, verdict.Link);
            Assert.Contains("does not exist", verdict.Headline, StringComparison.Ordinal);
            Assert.Contains("DBA Mail Profile", verdict.Evidence!, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(false, true, "is disabled")]
        [InlineData(true, false, "has no email address")]
        public void AnUndeliverableOperator_IsNotConfigured_NamingLinkFour(
            bool enabled, bool hasAddress, string expected)
        {
            var rows = New2022Rows();
            rows.Operator!.Enabled = enabled;
            rows.Operator.EmailAddress = hasAddress ? DbaAddress : "";

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.NotConfigured, verdict.State);
            Assert.Equal(4, verdict.Link);
            Assert.Contains(expected, verdict.Headline, StringComparison.Ordinal);
        }

        [Fact]
        public void AnOperatorThatIsNotOnTheInstance_IsNotConfigured_NamingLinkFour()
        {
            var rows = New2022Rows();
            rows.Operator = null;

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.NotConfigured, verdict.State);
            Assert.Equal(4, verdict.Link);
        }

        /// <summary>
        /// A profile row with no account is not a usable profile. The read LEFT JOINs on purpose, so a
        /// profile with nothing attached comes back as a row, and counting rows would pass link 2 over
        /// a chain that cannot send anything.
        /// </summary>
        [Fact]
        public void AProfileWithNoAccount_IsNotConfigured_NamingLinkTwo()
        {
            var rows = New2022Rows();
            rows.Profiles.Clear();
            rows.Profiles.Add(new MailProfileRow(1, "DBA Mail Profile", "", "", 0, ""));

            var verdict = AgentMailChainProbe.Evaluate(rows, Now);

            Assert.Equal(MailChainState.NotConfigured, verdict.State);
            Assert.Equal(2, verdict.Link);
            Assert.Contains("none is joined to an account", verdict.Evidence!, StringComparison.Ordinal);
        }

        // ── 5. The recency window is measured on the SERVER's clock ───────────────────────

        /// <summary>
        /// The app host and the SQL Server are different machines, and on a client site they are often
        /// in different time zones. Judging a server-local sent_date against the app's clock would make
        /// the verdict depend on where the console is sitting.
        /// </summary>
        [Fact]
        public void TheWindowIsMeasuredAgainstTheServerClock_NotTheCallers()
        {
            var rows = New2022Rows();
            rows.LastFailed = null;
            rows.LastEventLogError = null;
            rows.LastEventLogMessage = null;
            rows.FailedCount = 0;
            rows.SentCount = 1;
            rows.LastSent = new DateTime(2026, 9, 8, 12, 0, 0);
            rows.MailQueueReceiveEnabled = true;    // isolate this rule from the queue rule (F1)
            rows.ServerNow = new DateTime(2026, 9, 9, 12, 0, 0);

            // A caller clock two months ahead of the server's would age the delivery out of the window.
            var verdict = AgentMailChainProbe.Evaluate(rows, new DateTime(2026, 11, 9, 12, 0, 0));

            Assert.Equal(MailChainState.Works, verdict.State);
        }

        // ── 6. LinksOneToFourHold, the send button's gate ─────────────────────────────────

        [Fact]
        public void TheSendGateIsOpenOnlyWhereThereIsAProfileToSendThrough()
        {
            Assert.True(AgentMailChainProbe.Evaluate(New2022Rows(), Now).LinksOneToFourHold);
            Assert.False(AgentMailChainProbe.Evaluate(Old2017Rows(), Now).LinksOneToFourHold);

            // A link-5 blind spot leaves 1-4 proven, so a test send still tells you something.
            var blindAtFive = New2022Rows();
            blindAtFive.MailQueueReceiveEnabled = null;   // as a non-sysadmin really sees it
            blindAtFive.IsSysadmin = false;
            blindAtFive.Gaps.Add(new MailChainGap(5, "Database Mail delivery history", "not sysadmin"));
            Assert.True(AgentMailChainProbe.Evaluate(blindAtFive, Now).LinksOneToFourHold);

            // A blind spot BELOW link 5 does not: the send would fail for an unknown reason.
            var blindAtTwo = New2022Rows();
            blindAtTwo.Profiles.Clear();
            blindAtTwo.Gaps.Add(new MailChainGap(2, "Database Mail profiles", "denied"));
            Assert.False(AgentMailChainProbe.Evaluate(blindAtTwo, Now).LinksOneToFourHold);
        }

        // ── 7. DefaultOperator: Adrian's ruling 2, branch by branch ───────────────────────

        /// <summary>Branch 1 on the real rigs: DBA carries 99 of .\old2017's enabled-alert
        /// notifications and SQLDBA carries 7, so DBA is what the estate already points at.</summary>
        [Fact]
        public void Default_IsTheOperatorTheEnabledAlertsAlreadyNotify()
        {
            var inv = Inventory(
                Operator("SQLDBA", enabled: true, address: "alerts@sqldba.org", notifications: 7),
                Operator("DBA", enabled: true, address: DbaAddress, notifications: 99));

            Assert.Equal("DBA", AgentMailChainProbe.DefaultOperator(inv));
        }

        /// <summary>Branch 2: nobody is notified by an alert, and exactly one operator could receive
        /// mail if one were sent.</summary>
        [Fact]
        public void Default_FallsBackToTheOnlyEnabledOperatorWithAnAddress()
        {
            var inv = Inventory(
                Operator("DBA", enabled: true, address: DbaAddress, notifications: 0),
                Operator("OnCall", enabled: false, address: "oncall@sqldba.org", notifications: 0),
                Operator("Pager", enabled: true, address: "", notifications: 0));

            Assert.Equal("DBA", AgentMailChainProbe.DefaultOperator(inv));
        }

        /// <summary>Branch 3, the one Adrian asked for by name: no defensible default, so no default.
        /// The instance then cannot be applied until a human picks.</summary>
        [Fact]
        public void Default_IsNullWhenTwoOperatorsAreEquallyPlausible()
        {
            var inv = Inventory(
                Operator("DBA", enabled: true, address: DbaAddress, notifications: 0),
                Operator("SQLDBA", enabled: true, address: "alerts@sqldba.org", notifications: 0));

            Assert.Null(AgentMailChainProbe.DefaultOperator(inv));
        }

        /// <summary>A TIE on branch 1 has no winner under the rule as written, so it falls through
        /// rather than picking by id. Two operators on 41 alerts each is exactly where a guess is
        /// worse than a question.</summary>
        [Fact]
        public void Default_DoesNotBreakATieOnNotificationCount()
        {
            var inv = Inventory(
                Operator("DBA", enabled: true, address: DbaAddress, notifications: 41),
                Operator("SQLDBA", enabled: true, address: "alerts@sqldba.org", notifications: 41));

            Assert.Null(AgentMailChainProbe.DefaultOperator(inv));
        }

        /// <summary>
        /// THE RULE IS LITERAL, and this pins the consequence rather than hiding it: the most-notified
        /// operator wins even when it is disabled, because "who does alerting already target" is a
        /// question about the estate. The mail-chain verdict then says NOT CONFIGURED at link 4, which
        /// is the true answer; silently defaulting to somebody else would hide a real misconfiguration.
        /// </summary>
        [Fact]
        public void Default_PicksTheMostNotifiedOperatorEvenWhenItIsDisabled()
        {
            var inv = Inventory(
                Operator("DBA", enabled: false, address: DbaAddress, notifications: 99),
                Operator("SQLDBA", enabled: true, address: "alerts@sqldba.org", notifications: 7));

            Assert.Equal("DBA", AgentMailChainProbe.DefaultOperator(inv));
        }

        [Fact]
        public void Default_IsNullForAnInstanceWithNoOperatorsAtAll()
        {
            Assert.Null(AgentMailChainProbe.DefaultOperator(Inventory()));
            Assert.Null(AgentMailChainProbe.DefaultOperator(null));
        }

        // ── 7b. THE INVENTORY STATE (gate finding F2) ────────────────────────────────────
        //
        // A read nobody could make and a server with no operators both arrived as a non-null inventory
        // with an empty list, and the page offered "operator to create" over both. The state is now
        // explicit, so the two cannot be confused by a null check.

        [Fact]
        public void AnInventoryWithAGap_IsUnreadable_AndNamesTheReadThatFailed()
        {
            var inv = Inventory();
            inv.Gaps.Add(new MailChainGap(4, "Agent operators (msdb.dbo.sysoperators)",
                "The SELECT permission was denied on the object 'sysoperators', database 'msdb', schema 'dbo'."));

            Assert.Equal(OperatorInventoryState.Unreadable, inv.State);
            Assert.Contains("sysoperators", inv.UnreadableReason!, StringComparison.Ordinal);
            Assert.Contains("SELECT permission was denied", inv.UnreadableReason!, StringComparison.Ordinal);

            // ...and it is no basis for a default, even though the list came back empty.
            Assert.Null(AgentMailChainProbe.DefaultOperator(inv));
        }

        [Fact]
        public void AConnectionThatNeverOpened_IsUnreadable_NotEmpty()
        {
            var inv = Inventory();
            inv.Gaps.Add(new MailChainGap(0, "connect to SQL07", "A network-related or instance-specific error"));

            Assert.Equal(OperatorInventoryState.Unreadable, inv.State);
        }

        [Fact]
        public void AReadThatSucceededAndFoundNothing_IsEmpty_NotUnreadable()
        {
            Assert.Equal(OperatorInventoryState.Empty, Inventory().State);
            Assert.Null(Inventory().UnreadableReason);
        }

        [Fact]
        public void AReadThatReturnedOperators_IsLoaded()
        {
            var inv = Inventory(Operator("DBA", enabled: true, address: DbaAddress, notifications: 41));

            Assert.Equal(OperatorInventoryState.Loaded, inv.State);
            Assert.Equal("DBA", AgentMailChainProbe.DefaultOperator(inv));
        }

        /// <summary>A PARTIAL inventory - operators listed, an ancillary read denied - yields no default
        /// either. The notification counts this rule ranks on are exactly what a denied read leaves
        /// empty, so the "winner" would be an artefact of the denial.</summary>
        [Fact]
        public void APartialInventory_OffersNoDefault_EvenWithOperatorsInHand()
        {
            var inv = Inventory(
                Operator("DBA", enabled: true, address: DbaAddress, notifications: 41),
                Operator("SQLDBA", enabled: true, address: "alerts@sqldba.org", notifications: 7));
            inv.Gaps.Add(new MailChainGap(4, "alert notifications (msdb.dbo.sysnotifications)", "denied"));

            Assert.Equal(OperatorInventoryState.Unreadable, inv.State);
            Assert.Null(AgentMailChainProbe.DefaultOperator(inv));
        }

        // ── 8. Every shipped statement is a read, and none of them is the blocked one ─────

        /// <summary>
        /// Belt and braces beside SqlSafetyValidatorClassifyTests: the two procedure names this lane
        /// deliberately does NOT use appear nowhere in the shipped SQL. sp_configure is blocked
        /// outright by the validator; sp_get_sqlagent_properties is denied to a non-sysadmin (proved on
        /// both rigs) where xp_instance_regread is not, so the picker would have gone blind on exactly
        /// the connections that need it most.
        /// </summary>
        [Fact]
        public void NoShippedStatementUsesTheTwoProceduresThisLaneRejected()
        {
            foreach (var sql in AgentMailChainProbe.ShippedReadStatements)
            {
                Assert.DoesNotContain("sp_configure", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sp_get_sqlagent_properties", sql, StringComparison.OrdinalIgnoreCase);
            }

            // …and the list is not empty, which would make the loop above vacuous.
            Assert.True(AgentMailChainProbe.ShippedReadStatements.Count >= 13);
        }

        /// <summary>
        /// The one place a caller-supplied value meets a delivery query. A bare LIKE over a non-binary
        /// collation over-matches U+FFFD, so a recipients column holding replacement characters would
        /// count as a delivery to this operator - a green badge manufactured out of mojibake.
        /// </summary>
        [Fact]
        public void TheDeliveryMatchIsABinaryCharindex_NeverALike()
        {
            foreach (var sql in new[]
                     {
                         AgentMailChainProbe.DeliveryAggregateSql,
                         AgentMailChainProbe.DeliveryItemsSql,
                     })
            {
                Assert.Contains("CHARINDEX", sql, StringComparison.Ordinal);
                Assert.Contains("Latin1_General_BIN2", sql, StringComparison.Ordinal);
                Assert.DoesNotContain(" LIKE ", sql, StringComparison.OrdinalIgnoreCase);

                // The address arrives as a parameter. A spliced one would be an injection primitive
                // on a value the operator picks off a dropdown.
                Assert.Contains("@address", sql, StringComparison.Ordinal);
            }
        }

        // ── 9. G-B1: the recipient match is anchored to a WHOLE list entry ────────────────

        /*
         * WHY THIS SECTION EXISTS. The 2026-09-09 cold gate proved live on the NEW2022 rig that the
         * shipped delivery predicate was a SUBSTRING test - CHARINDEX(@address, recipients). That rig's
         * two operator addresses are alerts@sqldba.org (operator SQLDBA) and sqlalerts@sqldba.org
         * (operator DBA), and the first is a substring of the second, so the shorter address was
         * credited with the longer one's rows: the page showed 50,523 failed items where the truth for
         * that address is 29,726.
         *
         * The numbers that decide the verdict all come from that one predicate, sent_count above all.
         * So the same mechanism could credit the shorter address with a SENT item belonging to the
         * longer one and turn "no item has EVER been delivered" into a green badge - the exact axis
         * this lane exists to protect. The gate could not demonstrate that flip live, because neither
         * rig has ever delivered a single mail item. It is demonstrated here instead, over captured
         * rows, in AShortAddressDoesNotInheritTheLongerAddressesDelivery.
         *
         * Matches() below is the EXECUTABLE SPECIFICATION of the shipped predicate; SqlPredicateFor()
         * renders that same rule as the SQL text the probe must contain. They are bound together, so
         * moving either anchor turns TheRecipientMatchIsAnchoredToAWholeListEntry red. That the SQL
         * really behaves this way against a real server is PROVED separately and live: the anchored
         * predicate returns exactly the exact-address truth on the rig - 0 sent / 29,726 failed /
         * 29,743 matched for alerts@sqldba.org, and an unchanged 0 / 20,797 / 20,821 for
         * sqlalerts@sqldba.org. See
         * evidence/operator-picker-mailchain-2026-09-09/fix2/live/anchored-proof.txt.
         */

        // Operator SQLDBA on the NEW2022 rig. DbaAddress above is operator DBA, and this one is a
        // substring of it - that is the whole trap, and it is real, not hypothetical.
        private const string SqldbaAddress = "alerts@sqldba.org";

        // Database Mail's OWN list separator: msdb.dbo.sysmail_verify_addressparams_sp rejects a comma
        // outside double quotes and says in its own error text that users should use a semicolon.
        private const string Sep = ";";

        // The one character normalised away on both sides, because "a@x; b@y" is a legal list.
        private const string Stripped = " ";

        /// <summary>
        /// The executable specification of the shipped delivery predicate. Ordinal comparison is the C#
        /// analogue of the Latin1_General_BIN2 the SQL forces: byte-exact, and therefore case-sensitive.
        /// </summary>
        private static bool Matches(string address, string? recipientList) =>
            (Sep + (recipientList ?? "").Replace(Stripped, "", StringComparison.Ordinal) + Sep)
                .Contains(Sep + address.Replace(Stripped, "", StringComparison.Ordinal) + Sep,
                          StringComparison.Ordinal);

        /// <summary>The rule the gate refuted, kept so the flip it causes can be shown rather than described.</summary>
        private static bool SubstringMatch(string address, string? recipientList) =>
            (recipientList ?? "").Contains(address, StringComparison.Ordinal);

        /// <summary><see cref="Matches"/> rendered as the SQL the probe must contain, for one column.</summary>
        private static string SqlPredicateFor(string column) =>
            $"CHARINDEX(REPLACE(N'{Sep}' + @address + N'{Sep}', N'{Stripped}', N'') COLLATE Latin1_General_BIN2, "
            + $"REPLACE(N'{Sep}' + ISNULL({column}, N'') + N'{Sep}', N'{Stripped}', N'') COLLATE Latin1_General_BIN2) > 0";

        /// <summary>
        /// The shipped SQL matches every recipient column as a WHOLE list entry, in BOTH statements, and
        /// the substring form the gate refuted appears nowhere.
        /// </summary>
        [Fact]
        public void TheRecipientMatchIsAnchoredToAWholeListEntry()
        {
            var columns = new[] { "i.recipients", "i.copy_recipients", "i.blind_copy_recipients" };

            foreach (var (name, sql) in new[]
                     {
                         ("DeliveryAggregateSql", AgentMailChainProbe.DeliveryAggregateSql),
                         ("DeliveryItemsSql", AgentMailChainProbe.DeliveryItemsSql),
                     })
            {
                foreach (var column in columns)
                {
                    var expected = SqlPredicateFor(column);
                    Assert.True(Occurrences(sql, expected) == 1,
                        $"{name} must match {column} as a whole delimited entry, exactly once. Expected to "
                        + $"find:{Environment.NewLine}{expected}{Environment.NewLine}in:{Environment.NewLine}{sql}");
                }

                // The refuted form: a CHARINDEX whose needle is the bare address.
                Assert.DoesNotContain("CHARINDEX(@address", sql, StringComparison.Ordinal);

                // Three columns, three predicates - not a fourth that slipped through unanchored.
                Assert.Equal(3, Occurrences(sql, "CHARINDEX("));
            }
        }

        /// <summary>
        /// The real-world pair off the NEW2022 rig, and the list shapes Database Mail permits around it.
        /// These eleven cases were also run against the live server in anchored-proof.txt, where every
        /// one of them agreed with this table.
        /// </summary>
        [Theory]
        [InlineData("alerts@sqldba.org", "alerts@sqldba.org", true)]
        [InlineData("alerts@sqldba.org", "sqlalerts@sqldba.org", false)]
        [InlineData("alerts@sqldba.org", "alerts@sqldba.org;x@y.com", true)]
        [InlineData("alerts@sqldba.org", "x@y.com;alerts@sqldba.org", true)]
        [InlineData("alerts@sqldba.org", "x@y.com; alerts@sqldba.org ; z@w.com", true)]
        [InlineData("alerts@sqldba.org", "x@y.com;sqlalerts@sqldba.org", false)]
        [InlineData("alerts@sqldba.org", "x@y.com; sqlalerts@sqldba.org", false)]
        [InlineData("alerts@sqldba.org", "alerts@sqldba.org.nz", false)]
        [InlineData("alerts@sqldba.org", "sqlalerts@sqldba.org;alerts@sqldba.org", true)]
        [InlineData("alerts@sqldba.org", "", false)]
        [InlineData("alerts@sqldba.org", null, false)]
        public void AnItemAddressedOnlyToTheLongerAddressIsNotCountedForTheShorterOne(
            string address, string? recipients, bool expected)
        {
            // The trap this table exists for is real on the bench, not hypothetical.
            Assert.Contains(SqldbaAddress, DbaAddress, StringComparison.Ordinal);

            Assert.Equal(expected, Matches(address, recipients));
        }

        /// <summary>
        /// THE FLIP the gate could not reach live, over captured rows: a SENT item belonging to the
        /// LONGER address must not make the shorter one read WORKS. Both rules are run over the SAME
        /// rows, so the defect is reproduced rather than described.
        /// </summary>
        [Fact]
        public void AShortAddressDoesNotInheritTheLongerAddressesDelivery()
        {
            var items = new[]
            {
                new CapturedItem("failed", Now.AddDays(-3), SqldbaAddress),
                new CapturedItem("failed", Now.AddDays(-4), SqldbaAddress),
                new CapturedItem("sent", Now.AddDays(-1), DbaAddress),   // the LONGER address, delivered
            };

            // The shipped rule after this fix: that delivery belongs to the other operator, so nothing
            // has ever reached this address and its own failures stand.
            var anchored = AgentMailChainProbe.Evaluate(AggregateOver(items, SqldbaAddress, Matches), Now);
            Assert.Equal(MailChainState.Broken, anchored.State);
            Assert.Contains(SqldbaAddress, anchored.Headline, StringComparison.Ordinal);

            // The refuted rule over the SAME rows: a green badge, naming a delivery that never happened
            // to this address. This is why G-B1 was blocking and not cosmetic.
            var substring = AgentMailChainProbe.Evaluate(AggregateOver(items, SqldbaAddress, SubstringMatch), Now);
            Assert.Equal(MailChainState.Works, substring.State);

            // And the fix is SCOPED: the longer address reads the same under both rules, which is what
            // the gate measured on the rig - 20,797 either way for sqlalerts@sqldba.org.
            Assert.Equal(
                AgentMailChainProbe.Evaluate(AggregateOver(items, DbaAddress, Matches), Now).State,
                AgentMailChainProbe.Evaluate(AggregateOver(items, DbaAddress, SubstringMatch), Now).State);
        }

        /// <summary>
        /// The case ruling, made deliberately on 2026-09-09 rather than inherited by accident. BIN2 is
        /// byte-exact, so the match is case-SENSITIVE and stays so: the binary collation is what defeats
        /// the U+FFFD over-match, on the rigs no recipients value differs from its own lowercase form and
        /// both operator addresses are lowercase, and the error direction is an under-match, which reads
        /// BROKEN - the safe direction under the never-sent = BROKEN ruling.
        /// </summary>
        [Fact]
        public void TheRecipientMatchIsCaseSensitive_AndTheCollationIsNotRelaxedToChangeThat()
        {
            Assert.True(Matches(SqldbaAddress, "alerts@sqldba.org"));
            Assert.False(Matches(SqldbaAddress, "ALERTS@SQLDBA.ORG"));
            Assert.False(Matches(SqldbaAddress, "Alerts@sqldba.org"));

            foreach (var sql in new[]
                     {
                         AgentMailChainProbe.DeliveryAggregateSql,
                         AgentMailChainProbe.DeliveryItemsSql,
                     })
            {
                // Six: needle and haystack, for each of the three recipient columns.
                Assert.Equal(6, Occurrences(sql, "COLLATE Latin1_General_BIN2"));
                Assert.DoesNotContain("LOWER(", sql, StringComparison.Ordinal);
                Assert.DoesNotContain("UPPER(", sql, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The stated residual: a display-name entry does not match, because the address is not a whole
        /// list entry. Pinned so it stays deliberate. Neither rig holds a single recipients value
        /// containing an angle bracket, Database Mail's own address validator never mentions the form,
        /// and the miss reads BROKEN rather than green - so the match is not widened past the evidence.
        /// </summary>
        [Fact]
        public void ADisplayNameEntryDoesNotMatch_AndThatIsTheDeliberateResidual()
        {
            Assert.False(Matches(SqldbaAddress, "Alerts <alerts@sqldba.org>"));
            Assert.False(Matches(SqldbaAddress, "x@y.com;Alerts <alerts@sqldba.org>"));

            // The bare form, which is what sp_notify_operator actually sends to, still matches.
            Assert.True(Matches(SqldbaAddress, "x@y.com;alerts@sqldba.org"));
        }

        // ── Fixtures ──────────────────────────────────────────────────────────────────────

        /// <summary>One captured msdb.dbo.sysmail_allitems row, reduced to what the aggregate reads.</summary>
        private sealed record CapturedItem(string SentStatus, DateTime Stamp, string Recipients);

        /// <summary>
        /// The server-side aggregate of DeliveryAggregateSql, done in C# over captured rows so a
        /// matching rule can be swapped and the VERDICT compared. Everything outside link 5 is the
        /// NEW2022 fixture, with the queue RUNNING so the stopped-queue branch does not outrank it.
        /// </summary>
        private static MailChainRows AggregateOver(
            CapturedItem[] items, string address, Func<string, string?, bool> match)
        {
            var rows = New2022Rows();
            rows.OperatorName = "SQLDBA";
            rows.Operator = Operator("SQLDBA", enabled: true, address: address, notifications: 7, id: 5);
            rows.MailQueueReceiveEnabled = true;
            rows.LastEventLogError = null;
            rows.LastEventLogMessage = null;
            rows.LastSent = null;
            rows.LastFailed = null;
            rows.SentCount = 0;
            rows.FailedCount = 0;
            rows.MatchedCount = 0;

            foreach (var item in items)
            {
                if (!match(address, item.Recipients)) continue;

                rows.MatchedCount++;
                if (item.SentStatus == "sent")
                {
                    rows.SentCount++;
                    if (rows.LastSent is null || item.Stamp > rows.LastSent) rows.LastSent = item.Stamp;
                }
                else if (item.SentStatus == "failed")
                {
                    rows.FailedCount++;
                    if (rows.LastFailed is null || item.Stamp > rows.LastFailed) rows.LastFailed = item.Stamp;
                }
            }

            return rows;
        }

        private static int Occurrences(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// .\new2022 as read on 2026-09-09. Every value here came off the rig; see
        /// evidence/operator-picker-mailchain-2026-09-09/builder/probe-shape-out.txt.
        /// </summary>
        private static MailChainRows New2022Rows()
        {
            var rows = new MailChainRows
            {
                ServerName = @"MSI\NEW2022",
                OperatorName = "DBA",
                ServerNow = Now,
                DatabaseMailXpsValue = 1,
                DatabaseMailXpsValueInUse = 1,
                MailQueueReceiveEnabled = false,           // STOPPED on the rig, and evidence only
                PrincipalProfileGrants = 1,
                ProfileGrantedToPublic = true,
                AgentMailProfile = "DBA Mail Profile",
                AgentUseDatabaseMail = 1,
                IsSysadmin = true,
                LastSent = null,                            // zero sent items, ever
                LastFailed = new DateTime(2026, 7, 27, 18, 28, 30, 303),
                SentCount = 0,
                FailedCount = 20797,
                MatchedCount = 20821,
                LastEventLogError = new DateTime(2026, 7, 27, 18, 33, 22, 403),
                LastEventLogMessage =
                    "The mail could not be sent to the recipients because of the mail server failure. "
                    + "(Sending Mail using Account 1 (2026-07-27T18:33:22).",
                Operator = Operator("DBA", enabled: true, address: DbaAddress, notifications: 41,
                                    id: 4, lastEmailDate: 20260905, lastEmailTime: 44504),
            };

            rows.Profiles.Add(new MailProfileRow(
                1, "DBA Mail Profile", "DBA_Email_Account", "smtp.office365.com", 587, DbaAddress));

            return rows;
        }

        /// <summary>.\old2017 as read on 2026-09-09: Database Mail XPs on, queue receiving, and no
        /// profile, account or server row anywhere.</summary>
        private static MailChainRows Old2017Rows() => new()
        {
            ServerName = @"MSI\OLD2017",
            OperatorName = "DBA",
            ServerNow = Now,
            DatabaseMailXpsValue = 1,
            DatabaseMailXpsValueInUse = 1,
            MailQueueReceiveEnabled = true,
            AgentMailProfile = null,
            AgentUseDatabaseMail = 0,
            IsSysadmin = true,
            Operator = Operator("DBA", enabled: true, address: DbaAddress, notifications: 99, id: 3),
        };

        /// <summary>
        /// The gate's own R1 input: .\new2022's wiring, its stopped queue, and a SYNTHETIC delivery two
        /// days before <see cref="Now"/> - no rig on this bench has ever delivered a mail item, which is
        /// stated rather than implied away.
        /// </summary>
        private static MailChainRows QueueStoppedWithDelivery()
        {
            var rows = New2022Rows();
            rows.LastFailed = null;
            rows.LastEventLogError = null;
            rows.LastEventLogMessage = null;
            rows.FailedCount = 0;
            rows.SentCount = 3;
            rows.LastSent = Now.AddDays(-2);
            return rows;
        }

        private static AgentOperatorRow Operator(
            string name, bool enabled, string address, int notifications,
            int id = 1, int lastEmailDate = 0, int lastEmailTime = 0) => new()
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            EmailAddress = address,
            EnabledAlertNotifications = notifications,
            LastEmailDate = lastEmailDate,
            LastEmailTime = lastEmailTime,
        };

        private static OperatorInventory Inventory(params AgentOperatorRow[] operators)
        {
            var inv = new OperatorInventory { ServerName = "SQL01" };
            foreach (var op in operators) inv.Operators.Add(op);
            return inv;
        }
    }
}
