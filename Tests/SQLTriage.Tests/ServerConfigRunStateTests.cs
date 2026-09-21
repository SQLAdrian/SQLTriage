/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// mc-preview-usability (2026-09-08). Adrian, on the installed service, watching a three-instance
    /// change-control preview: "when I navigate away the page resets, kind of like a pain".
    ///
    /// <para>The two multi-instance run loops and their status dictionaries used to be plain fields
    /// on <c>Pages/ServerConfiguration.razor</c>. Blazor disposes a component on navigation, so
    /// results the operator had already waited minutes for were destroyed. They now live in
    /// <see cref="ServerConfigRunState"/>, registered AddScoped, and the page subscribes to
    /// <see cref="ServerConfigRunState.Changed"/> on init and unsubscribes on Dispose.</para>
    ///
    /// <para><b>What these tests can and cannot prove.</b> They exercise the real service with a
    /// fake per-instance delegate, so the loop's sequencing, its failure isolation, its notification
    /// and its consolidated-text builder are PROVED here with no SQL Server and no rendered
    /// component. They do NOT prove the page's markup binds to any of it (there is no bUnit in this
    /// tree - see ServerConfigPageMarkupLintTests for the text lint that covers the wiring), and
    /// they cannot prove a browser download, which is an honest UNTESTED residual on the lane.</para>
    /// </summary>
    public sealed class ServerConfigRunStateTests
    {
        private static Func<string, InstanceRunStatus, CancellationToken, Task> Done(int rows = 1) =>
            (name, status, ct) =>
            {
                status.State = ServerConfigRunState.StateDone;
                status.RowCount = rows;
                return Task.CompletedTask;
            };

        // ── (1) The run survives the listener going away ─────────────────────

        /// <summary>
        /// THE DEFECT THIS LANE FIXES, stated as a test. A run in flight continues after the only
        /// listener unsubscribes (which is exactly what the page's Dispose does when the operator
        /// navigates away), and the result is in the dictionary when it lands. Before the move, the
        /// dictionary went with the component.
        /// </summary>
        [Fact]
        public async Task ARunContinuesAfterTheListenerUnsubscribes_AndTheResultIsStillThere()
        {
            var state = new ServerConfigRunState();
            var gate = new TaskCompletionSource();
            var reached = new TaskCompletionSource();

            void Listener() { }

            state.Changed += Listener;

            var run = state.RunPreviewAsync(
                new[] { "SQL01" },
                async (name, status, ct) =>
                {
                    reached.TrySetResult();
                    await gate.Task;
                    status.State = ServerConfigRunState.StateDone;
                    status.RowCount = 35;
                });

            await reached.Task;

            // The operator navigates away: the page unsubscribes in Dispose. Nothing cancels.
            state.Changed -= Listener;
            Assert.True(state.PreviewRunning);

            gate.SetResult();
            Assert.True(await run);

            Assert.False(state.PreviewRunning);
            Assert.Equal(ServerConfigRunState.StateDone, state.PreviewStatuses["SQL01"].State);
            Assert.Equal(35, state.PreviewStatuses["SQL01"].RowCount);
        }

        // ── (2) A page that comes back re-attaches to the same run ───────────

        [Fact]
        public async Task AFreshListenerSubscribingAfterwards_SeesTheStateAndTheOnwardTransitions()
        {
            var state = new ServerConfigRunState();
            var gate = new TaskCompletionSource();
            var reached = new TaskCompletionSource();

            var run = state.RunPreviewAsync(
                new[] { "SQL01", "SQL02" },
                async (name, status, ct) =>
                {
                    if (name == "SQL01")
                    {
                        reached.TrySetResult();
                        await gate.Task;
                    }

                    status.State = ServerConfigRunState.StateDone;
                });

            await reached.Task;

            // A NEW page instance mounts mid-run and subscribes. It must see what already happened
            // (the seeded rows and the in-flight one) and every transition from here on.
            var seenAfterSubscribing = 0;
            void Fresh() => Interlocked.Increment(ref seenAfterSubscribing);
            state.Changed += Fresh;

            Assert.Equal(2, state.PreviewStatuses.Count);
            Assert.Equal(ServerConfigRunState.StateRunning, state.PreviewStatuses["SQL01"].State);
            Assert.Equal(ServerConfigRunState.StatePending, state.PreviewStatuses["SQL02"].State);
            Assert.True(state.PreviewRunning);

            gate.SetResult();
            Assert.True(await run);
            state.Changed -= Fresh;

            Assert.True(seenAfterSubscribing > 0, "the re-attached page saw no transitions at all");
            Assert.Equal(2, state.PreviewSummary.Done);
        }

        // ── (3) One instance failing never aborts the run ────────────────────

        [Fact]
        public async Task AnInstanceThatThrows_IsFailed_AndTheNextInstanceStillRuns()
        {
            var state = new ServerConfigRunState();
            var visited = new List<string>();

            var ran = await state.RunApplyAsync(
                new[] { "SQL01", "SQL02", "SQL03" },
                (name, status, ct) =>
                {
                    visited.Add(name);
                    if (name == "SQL02") throw new InvalidOperationException("login failed for SQL02");
                    status.State = ServerConfigRunState.StateDone;
                    return Task.CompletedTask;
                });

            Assert.True(ran);
            Assert.Equal(new[] { "SQL01", "SQL02", "SQL03" }, visited);
            Assert.Equal(ServerConfigRunState.StateDone, state.ApplyStatuses["SQL01"].State);
            Assert.Equal(ServerConfigRunState.StateFailed, state.ApplyStatuses["SQL02"].State);
            Assert.Equal("login failed for SQL02", state.ApplyStatuses["SQL02"].Message);
            Assert.Equal(ServerConfigRunState.StateDone, state.ApplyStatuses["SQL03"].State);

            var summary = state.ApplySummary;
            Assert.Equal(3, summary.Total);
            Assert.Equal(2, summary.Done);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(0, summary.Refused);
        }

        [Fact]
        public async Task ASecondCallWhileALaneIsRunning_IsRefused_AndDoesNotClearTheLiveRun()
        {
            var state = new ServerConfigRunState();
            var gate = new TaskCompletionSource();
            var reached = new TaskCompletionSource();

            var first = state.RunPreviewAsync(
                new[] { "SQL01" },
                async (name, status, ct) => { reached.TrySetResult(); await gate.Task; status.State = ServerConfigRunState.StateDone; });

            await reached.Task;

            Assert.False(await state.RunPreviewAsync(new[] { "SQL99" }, Done()));
            Assert.True(state.PreviewStatuses.ContainsKey("SQL01"));
            Assert.False(state.PreviewStatuses.ContainsKey("SQL99"));

            gate.SetResult();
            Assert.True(await first);
        }

        // ── (5) Changed fires at least once per instance transition ──────────

        [Fact]
        public async Task ChangedFiresForEveryInstance_IncludingTheRefusedOne()
        {
            var state = new ServerConfigRunState();
            var seenStates = new List<string>();

            state.Changed += () => seenStates.Add(
                string.Join(",", state.PreviewStatuses.Values.Select(s => s.ServerName + "=" + s.State)));

            await state.RunPreviewAsync(
                new[] { "SQL01", "SQL02" },
                (name, status, ct) =>
                {
                    if (name == "SQL01")
                    {
                        // The shape the page's old loop skipped the notification on (a bare
                        // `continue`), so a refused row sat unrendered until the next one finished.
                        status.State = ServerConfigRunState.StateRefused;
                        status.Message = "SQL01 is no longer a configured server.";
                    }
                    else
                    {
                        status.State = ServerConfigRunState.StateDone;
                    }

                    return Task.CompletedTask;
                });

            // seed + (Running + terminal) x 2 + finish
            Assert.Equal(6, seenStates.Count);
            Assert.Contains(seenStates, s => s.Contains("SQL01=Running"));
            Assert.Contains(seenStates, s => s.Contains("SQL01=Refused"));
            Assert.Contains(seenStates, s => s.Contains("SQL02=Running"));
            Assert.Contains(seenStates, s => s.Contains("SQL02=Done"));

            var summary = state.PreviewSummary;
            Assert.Equal(1, summary.Done);
            Assert.Equal(1, summary.Refused);
            Assert.Equal(0, summary.Failed);
        }

        // ── (4) The consolidated text ────────────────────────────────────────

        [Fact]
        public void BuildConsolidatedText_ConcatenatesTheRealFiles_NamesTheMissingOne_AndTrailsTheRefused()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sqlt-consolidated-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var one = Path.Combine(dir, "SQL01_20260908-080205_changecontrol.txt");
                var two = Path.Combine(dir, "SQL02_20260908-080351_changecontrol.txt");
                File.WriteAllText(one, "SQL01 line A\r\nSQL01 line B");
                File.WriteAllText(two, "SQL02 line A");

                var missing = Path.Combine(dir, "SQL03_20260908-080512_changecontrol.txt");

                var text = ServerConfigRunState.BuildConsolidatedText(new[]
                {
                    new InstanceRunStatus { ServerName = "SQL01", State = ServerConfigRunState.StateDone, ExportedPath = one, RowCount = 35 },
                    new InstanceRunStatus { ServerName = "SQL02", State = ServerConfigRunState.StateDone, ExportedPath = two, RowCount = 12 },
                    new InstanceRunStatus { ServerName = "SQL03", State = ServerConfigRunState.StateDone, ExportedPath = missing },
                    new InstanceRunStatus { ServerName = "SQL04", State = ServerConfigRunState.StateRefused, Message = "Not licensed on this install." },
                    // Reachable mid-run, which is the point of the two assertions they carry below:
                    // the Consolidated control renders as soon as the dictionary is seeded, so an
                    // operator can open this panel while instances are still queued. (Gate F3.)
                    new InstanceRunStatus { ServerName = "SQL05", State = ServerConfigRunState.StatePending },
                    new InstanceRunStatus { ServerName = "SQL06", State = ServerConfigRunState.StateRunning },
                });

                // Header per Done instance, naming the instance AND the file it came from.
                Assert.Contains("===== SQL01 - SQL01_20260908-080205_changecontrol.txt =====", text, StringComparison.Ordinal);
                Assert.Contains("===== SQL02 - SQL02_20260908-080351_changecontrol.txt =====", text, StringComparison.Ordinal);

                // Contents, in run order, not just the names.
                Assert.Contains("SQL01 line A", text, StringComparison.Ordinal);
                Assert.Contains("SQL01 line B", text, StringComparison.Ordinal);
                Assert.Contains("SQL02 line A", text, StringComparison.Ordinal);
                Assert.True(text.IndexOf("SQL01 line A", StringComparison.Ordinal) < text.IndexOf("SQL02 line A", StringComparison.Ordinal),
                    "instances must appear in run order");

                // A file that has gone missing is NAMED in place, never thrown.
                Assert.Contains("no longer on disk", text, StringComparison.Ordinal);
                Assert.Contains(missing, text, StringComparison.Ordinal);

                // Refused/Failed instances land in the trailer with their reason.
                Assert.Contains("===== Instances that exported nothing =====", text, StringComparison.Ordinal);
                Assert.Contains("SQL04 - Refused: Not licensed on this install.", text, StringComparison.Ordinal);

                // Gate finding F3. A row that has not had its turn yet is NOT a verdict. The trailer
                // used to print the bare state, so an operator who opened the panel mid-run read
                // "SQL05 - Pending" under a heading that says the instance exported nothing — which
                // reads as an outcome rather than as a queue position.
                Assert.Contains("SQL05 - Pending (not started)", text, StringComparison.Ordinal);
                Assert.Contains("SQL06 - Running (still running)", text, StringComparison.Ordinal);

                // The negatives are the mutation catch: reverting to `Append(s.State)` still
                // satisfies a Contains("SQL05 - Pending"), so the assertion has to name the shape
                // that only the bare state produces — the state word alone at end of line.
                Assert.DoesNotContain("SQL05 - Pending" + Environment.NewLine, text, StringComparison.Ordinal);
                Assert.DoesNotContain("SQL06 - Running" + Environment.NewLine, text, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }

        [Fact]
        public void BuildConsolidatedText_OnNothing_SaysSo_RatherThanReturningAnEmptyDownload()
        {
            Assert.Equal("No instances have run yet.", ServerConfigRunState.BuildConsolidatedText(Array.Empty<InstanceRunStatus>()));
            Assert.Equal("No instances have run yet.", ServerConfigRunState.BuildConsolidatedText(null));
        }

        [Fact]
        public void BuildConsolidatedFileName_MirrorsTheChangeControlNaming_IncludingTheAppliedSuffix()
        {
            var when = new DateTime(2026, 9, 8, 8, 2, 5, DateTimeKind.Local);

            Assert.Equal("consolidated_20260908-080205_changecontrol.txt",
                ServerConfigRunState.BuildConsolidatedFileName(when));
            Assert.Equal("consolidated_20260908-080205_changecontrol-applied.txt",
                ServerConfigRunState.BuildConsolidatedFileName(when, apply: true));

            // Same suffix rule the per-instance export already uses, so the two artifacts read as
            // one family rather than two conventions.
            Assert.EndsWith("_changecontrol-applied.txt",
                ServerConfigScriptService.BuildChangeControlFileName("SQL01", when, apply: true), StringComparison.Ordinal);
        }

        [Fact]
        public void TheTwoLanesAreIndependent()
        {
            var state = new ServerConfigRunState();
            Assert.NotSame(state.PreviewStatuses, state.ApplyStatuses);
            Assert.Empty(state.PreviewStatuses);
            Assert.Empty(state.ApplyStatuses);
            Assert.False(state.PreviewRunning);
            Assert.False(state.ApplyRunning);
        }

        // ══ The per-instance operator picker (operator-picker-mailchain, 2026-09-09) ══════════
        //
        // Same method as everything above: the REAL service, driven by fake read delegates, so the
        // selection rules, the freeze and the lock are proved with no SQL Server and no rendered
        // component. What these do NOT prove is that the page binds to any of it - the render tests
        // in Gated/ServerConfigurationRenderTests own that.

        /// <summary>
        /// Adrian's ask in one assertion: "the operators might be different". Two instances, two
        /// different inventories, two different defaults, and the freeze hands each instance ITS OWN
        /// name to the run. The single frozen name the page used to carry could not express this.
        /// </summary>
        [Fact]
        public async Task EachInstanceGetsItsOwnOperator_AndTheRunLoopReceivesThatName()
        {
            var state = new ServerConfigRunState();

            var ran = await state.LoadOperatorsAsync(
                new[] { "SQL01", "SQL02" },
                (name, ct) => Task.FromResult(name == "SQL01"
                    ? InventoryOf(Op("DBA", notifications: 99), Op("SQLDBA", notifications: 7))
                    : InventoryOf(Op("NightWatch", notifications: 12), Op("SQLDBA", notifications: 3))),
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            Assert.True(ran);
            Assert.Equal("DBA", state.OperatorStates["SQL01"].SelectedOperator);
            Assert.Equal("NightWatch", state.OperatorStates["SQL02"].SelectedOperator);

            // The mail chain read for each instance is the one for the operator that instance picked.
            Assert.Equal("DBA", state.OperatorStates["SQL01"].MailChain!.Rows.OperatorName);
            Assert.Equal("NightWatch", state.OperatorStates["SQL02"].MailChain!.Rows.OperatorName);

            // …and the run loop is handed exactly those names, per instance.
            var frozen = state.FreezeSelectedOperators(new[] { "SQL01", "SQL02" });
            var seen = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            await state.RunApplyAsync(new[] { "SQL01", "SQL02" }, (name, status, ct) =>
            {
                seen[name] = frozen.TryGetValue(name, out var picked) ? picked : null;
                status.State = ServerConfigRunState.StateDone;
                return Task.CompletedTask;
            });

            Assert.Equal("DBA", seen["SQL01"]);
            Assert.Equal("NightWatch", seen["SQL02"]);
        }

        /// <summary>
        /// THE INVARIANT THAT SURVIVED THE REDESIGN. The old page froze one operator name at arm time
        /// so a run could not have its name changed underneath it (D3, 2026-08-20). The freeze is now
        /// per instance and taken at run time; this proves the property is the same one - a selection
        /// changed after the freeze does not reach the run.
        /// </summary>
        [Fact]
        public async Task ASelectionChangedAfterTheFreeze_DoesNotReachTheRun()
        {
            var state = new ServerConfigRunState();
            await state.LoadOperatorsAsync(
                new[] { "SQL01" },
                (name, ct) => Task.FromResult(InventoryOf(Op("DBA", notifications: 99), Op("SQLDBA", notifications: 7))),
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            var frozen = state.FreezeSelectedOperators(new[] { "SQL01" });

            await state.SelectOperatorAsync("SQL01", "SQLDBA", (n, op, ct) => Task.FromResult(ReportFor(n, op)));

            Assert.Equal("SQLDBA", state.OperatorStates["SQL01"].SelectedOperator);   // the picker moved
            Assert.Equal("DBA", frozen["SQL01"]);                                     // the run did not
        }

        /// <summary>
        /// LOCKED WHILE A RUN IS IN FLIGHT. Attempted from INSIDE the run, which is the only moment it
        /// could actually happen, and the refusal is asserted on the STATE and not only on the return
        /// value - a method that returned false and changed the field anyway would pass a weaker test.
        /// </summary>
        [Fact]
        public async Task WhileALaneIsRunning_SelectionAndReloadAreBothRefused()
        {
            var state = new ServerConfigRunState();
            await state.LoadOperatorsAsync(
                new[] { "SQL01" },
                (name, ct) => Task.FromResult(InventoryOf(Op("DBA", notifications: 99))),
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            bool? selectRefused = null;
            bool? loadRefused = null;

            await state.RunApplyAsync(new[] { "SQL01" }, async (name, status, ct) =>
            {
                Assert.True(state.OperatorSelectionLocked);

                selectRefused = !await state.SelectOperatorAsync(
                    "SQL01", "SomebodyElse", (n, op, c) => Task.FromResult(ReportFor(n, op)));

                loadRefused = !await state.LoadOperatorsAsync(
                    new[] { "SQL01" },
                    (n, c) => Task.FromResult(InventoryOf(Op("SomebodyElse", notifications: 1))),
                    (n, op, c) => Task.FromResult(ReportFor(n, op)));

                status.State = ServerConfigRunState.StateDone;
            });

            Assert.True(selectRefused);
            Assert.True(loadRefused);
            Assert.Equal("DBA", state.OperatorStates["SQL01"].SelectedOperator);
            Assert.False(state.OperatorSelectionLocked);
        }

        /// <summary>
        /// The service outlives the page (that is the whole point of the 09-08 lane), so a picker
        /// choice has to survive the page being disposed and re-created - which for this object is a
        /// listener detaching and a new one attaching. Nothing about the selection is held by the
        /// subscription.
        /// </summary>
        [Fact]
        public async Task ASelectionSurvivesTheListenerDetachingAndReattaching()
        {
            var state = new ServerConfigRunState();
            await state.LoadOperatorsAsync(
                new[] { "SQL01" },
                (name, ct) => Task.FromResult(InventoryOf(Op("DBA", notifications: 99), Op("SQLDBA", notifications: 7))),
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            await state.SelectOperatorAsync("SQL01", "SQLDBA", (n, op, ct) => Task.FromResult(ReportFor(n, op)));

            void FirstPage() { }
            state.Changed += FirstPage;
            state.Changed -= FirstPage;          // the page navigated away and was disposed

            var secondPageSawAChange = 0;
            state.Changed += () => secondPageSawAChange++;

            Assert.Equal("SQLDBA", state.OperatorStates["SQL01"].SelectedOperator);
            Assert.Equal("SQLDBA", state.OperatorStates["SQL01"].MailChain!.Rows.OperatorName);

            // …and the re-attached listener is live.
            state.SetMailChain("SQL01", ReportFor("SQL01", "SQLDBA"));
            Assert.Equal(1, secondPageSawAChange);
        }

        /// <summary>A re-read must not silently undo a choice a human already made.</summary>
        [Fact]
        public async Task ReloadingKeepsAChoiceTheOperatorMade_UnlessItIsGone()
        {
            var state = new ServerConfigRunState();
            var inventory = InventoryOf(Op("DBA", notifications: 99), Op("SQLDBA", notifications: 7));

            await state.LoadOperatorsAsync(new[] { "SQL01" },
                (n, ct) => Task.FromResult(inventory),
                (n, op, ct) => Task.FromResult(ReportFor(n, op)));
            await state.SelectOperatorAsync("SQL01", "SQLDBA", (n, op, ct) => Task.FromResult(ReportFor(n, op)));

            await state.LoadOperatorsAsync(new[] { "SQL01" },
                (n, ct) => Task.FromResult(inventory),
                (n, op, ct) => Task.FromResult(ReportFor(n, op)));
            Assert.Equal("SQLDBA", state.OperatorStates["SQL01"].SelectedOperator);

            // The operator was dropped on the server: the stale choice is replaced by the default,
            // never left pointing at a name that is no longer there.
            await state.LoadOperatorsAsync(new[] { "SQL01" },
                (n, ct) => Task.FromResult(InventoryOf(Op("DBA", notifications: 99))),
                (n, op, ct) => Task.FromResult(ReportFor(n, op)));
            Assert.Equal("DBA", state.OperatorStates["SQL01"].SelectedOperator);
        }

        /// <summary>
        /// Ruling 2's third branch, as the page reads it: an instance with operators and no selection
        /// is listed, and an instance with NO operators is not - that one falls to free text and the
        /// baseline script's create path, exactly as it always did.
        /// </summary>
        [Fact]
        public async Task InstancesAwaitingAChoice_ListOnlyThoseThatHaveOperatorsAndNoSelection()
        {
            var state = new ServerConfigRunState();

            await state.LoadOperatorsAsync(
                new[] { "SQL01", "SQL02", "SQL03" },
                (name, ct) => Task.FromResult(name switch
                {
                    // Two equally plausible operators: no defensible default, so a choice is forced.
                    "SQL01" => InventoryOf(Op("DBA", notifications: 0), Op("SQLDBA", notifications: 0)),
                    // One clear winner: defaulted, nothing to ask.
                    "SQL02" => InventoryOf(Op("DBA", notifications: 41), Op("SQLDBA", notifications: 7)),
                    // No operators at all: free text, not a question.
                    _ => InventoryOf(),
                }),
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            var awaiting = state.InstancesAwaitingOperatorChoice(new[] { "SQL01", "SQL02", "SQL03" });

            Assert.Equal(new[] { "SQL01" }, awaiting);
            Assert.Null(state.OperatorStates["SQL01"].SelectedOperator);
            Assert.Equal("DBA", state.OperatorStates["SQL02"].SelectedOperator);
        }

        /// <summary>
        /// The rows fill in as the answers arrive. On a 49-server estate the alternative is a spinner
        /// until the last server answers, and one unreachable server would hold every other row hostage.
        /// </summary>
        [Fact]
        public async Task ChangeFiresPerInstance_AndOneUnreachableInstanceDoesNotStopTheOthers()
        {
            var state = new ServerConfigRunState();
            var notifications = 0;
            state.Changed += () => notifications++;

            await state.LoadOperatorsAsync(
                new[] { "SQL01", "SQL02" },
                (name, ct) => name == "SQL01"
                    ? throw new InvalidOperationException("SQL01 is no longer a configured server.")
                    : Task.FromResult(InventoryOf(Op("DBA", notifications: 41))),
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            Assert.Equal("SQL01 is no longer a configured server.", state.OperatorStates["SQL01"].LoadError);
            Assert.Null(state.OperatorStates["SQL01"].Inventory);
            Assert.Equal("DBA", state.OperatorStates["SQL02"].SelectedOperator);

            // Seed + one per instance + the finally: the table cannot only update at the end.
            Assert.True(notifications >= 4, $"expected a notification per instance; saw {notifications}");
            Assert.False(state.OperatorsLoading);
            Assert.False(state.OperatorStates["SQL01"].Loading);
        }

        /// <summary>Clearing a selection drops the verdict with it. A badge left behind would describe
        /// an operator that is no longer chosen, which is worse than no badge.</summary>
        [Fact]
        public async Task ClearingTheSelectionAlsoClearsTheVerdict()
        {
            var state = new ServerConfigRunState();
            await state.LoadOperatorsAsync(new[] { "SQL01" },
                (n, ct) => Task.FromResult(InventoryOf(Op("DBA", notifications: 99))),
                (n, op, ct) => Task.FromResult(ReportFor(n, op)));
            Assert.NotNull(state.OperatorStates["SQL01"].MailChain);

            Assert.True(await state.SelectOperatorAsync("SQL01", "", (n, op, ct) => Task.FromResult(ReportFor(n, op))));

            Assert.Null(state.OperatorStates["SQL01"].SelectedOperator);
            Assert.Null(state.OperatorStates["SQL01"].MailChain);
        }

        /// <summary>The single-instance section shares this whole machinery through a key that cannot
        /// collide with a real instance name.</summary>
        [Fact]
        public void TheCurrentServerKeyCannotBeARealInstanceName()
        {
            Assert.Contains('<', ServerConfigRunState.CurrentServerKey);
            Assert.Contains('>', ServerConfigRunState.CurrentServerKey);
        }

        // ══ A read nobody could make (gate finding F2, 2026-09-09) ═══════════════════════════

        /// <summary>
        /// THE DEFECT, at the level the page's Apply gate reads it. An instance whose operator read was
        /// REFUSED answered false to NeedsChoice - it requires Operators.Count > 0 - so it was not in
        /// the awaiting list, Apply stayed ENABLED with it armed, and the run fell through to the
        /// baseline script's own SET @OperatorName = N'DBA' on a server nobody could see.
        /// </summary>
        [Fact]
        public async Task AnInstanceWhoseOperatorsCouldNotBeRead_BlocksApply_WhileAnEmptyOneDoesNot()
        {
            var state = new ServerConfigRunState();
            var targets = new[] { "SQL01", "SQL02" };

            await state.LoadOperatorsAsync(
                targets,
                (name, ct) =>
                {
                    var inv = InventoryOf();

                    // SQL01: Msg 229 on msdb.dbo.sysoperators, which is what a least-privilege login
                    // really gets (proved on both rigs 2026-09-09) - a denial, not a zero row.
                    // SQL02: the read SUCCEEDED and the instance genuinely has no operators.
                    if (name == "SQL01")
                        inv.Gaps.Add(new MailChainGap(4, "Agent operators (msdb.dbo.sysoperators)",
                            "The SELECT permission was denied on the object 'sysoperators', database 'msdb'."));

                    return Task.FromResult(inv);
                },
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            Assert.Equal(OperatorInventoryState.Unreadable, state.OperatorStates["SQL01"].InventoryState);
            Assert.Equal(OperatorInventoryState.Empty, state.OperatorStates["SQL02"].InventoryState);

            // The instance nobody could read is refused; the empty one keeps the free-text create path.
            Assert.Equal(new[] { "SQL01" }, state.InstancesAwaitingOperatorChoice(targets));
            Assert.Equal(new[] { "SQL01" }, state.InstancesWithUnreadableOperators(targets));

            // ...and no name was invented for it anywhere.
            Assert.Null(state.OperatorStates["SQL01"].SelectedOperator);
            Assert.Null(state.FreezeSelectedOperators(targets)["SQL01"]);
            Assert.Contains("sysoperators", state.OperatorStates["SQL01"].UnreadableReason!,
                            StringComparison.Ordinal);
        }

        /// <summary>The client-side fault folds into the SAME state: a read that THREW is "nobody knows
        /// this instance's operators" exactly as a denial is, and it blocks Apply the same way.</summary>
        [Fact]
        public async Task AReadThatThrew_IsUnreadableToo_AndBlocksApply()
        {
            var state = new ServerConfigRunState();

            await state.LoadOperatorsAsync(
                new[] { "SQL07" },
                (name, ct) => throw new InvalidOperationException("SQL07 is no longer a configured server."),
                (name, op, ct) => Task.FromResult(ReportFor(name, op)));

            var st = state.OperatorStates["SQL07"];

            Assert.Equal(OperatorInventoryState.Unreadable, st.InventoryState);
            Assert.True(st.NeedsChoice);
            Assert.Contains("no longer a configured server", st.UnreadableReason!, StringComparison.Ordinal);
            Assert.Equal(new[] { "SQL07" }, state.InstancesAwaitingOperatorChoice(new[] { "SQL07" }));
        }

        /// <summary>
        /// GATE FINDING F7. The only feedback a REFUSED send left behind was a 5,000 ms toast, and the
        /// badge does not move on a refusal (the re-read runs only when the send was accepted). The
        /// outcome now lives on the run state, so it survives the toast, a re-render, and the page being
        /// disposed and rebuilt by a navigation.
        /// </summary>
        [Fact]
        public void ATestSendOutcomeSurvivesTheListenerDetachingAndReattaching()
        {
            var state = new ServerConfigRunState();
            var at = new DateTime(2026, 9, 9, 3, 20, 0, DateTimeKind.Utc);

            var firstPageSawAChange = 0;
            void FirstPage() => firstPageSawAChange++;
            state.Changed += FirstPage;

            state.RecordTestSendResult("SQL01", accepted: false,
                "Mail not queued. Database Mail is stopped. Use sysmail_start_sp to start Database Mail.", at);

            state.Changed -= FirstPage;              // the page navigated away and was disposed

            Assert.Equal(1, firstPageSawAChange);

            var outcome = state.OperatorStates["SQL01"].LastSendResult;
            Assert.NotNull(outcome);
            Assert.False(outcome!.Accepted);
            Assert.Equal(at, outcome.AtUtc);
            Assert.Contains("Database Mail is stopped", outcome.Text, StringComparison.Ordinal);

            // ...and a later re-read does not erase it: it is a dated fact about a send that happened.
            state.SetMailChain("SQL01", ReportFor("SQL01", "DBA"));
            Assert.NotNull(state.OperatorStates["SQL01"].LastSendResult);
        }

        private static AgentOperatorRow Op(string name, int notifications) => new()
        {
            Id = name.Length,
            Name = name,
            Enabled = true,
            EmailAddress = name.ToLowerInvariant() + "@sqldba.org",
            EnabledAlertNotifications = notifications,
        };

        private static OperatorInventory InventoryOf(params AgentOperatorRow[] operators)
        {
            var inv = new OperatorInventory { ServerName = "SQL" };
            foreach (var op in operators) inv.Operators.Add(op);
            return inv;
        }

        /// <summary>A report that carries the instance and operator it was asked for, so a test can see
        /// WHICH read the state made rather than only that it made one.</summary>
        private static MailChainReport ReportFor(string instanceName, string operatorName)
        {
            var rows = new MailChainRows { ServerName = instanceName, OperatorName = operatorName };
            return new MailChainReport(rows, AgentMailChainProbe.Evaluate(rows, DateTime.Now), DateTime.Now);
        }
    }
}
