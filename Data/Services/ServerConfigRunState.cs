/* In the name of God, the Merciful, the Compassionate */

/*
 * PER-CIRCUIT run state for /server-configuration's two multi-instance lanes.
 *
 * WHY THIS FILE EXISTS (2026-09-08)
 * ---------------------------------
 * The multi-instance Preview and Apply loops used to live in Pages/ServerConfiguration.razor and
 * kept their results in two plain page FIELDS. Blazor disposes a component on navigation, so
 * leaving the page destroyed results the operator had already waited minutes for, and the
 * done/refused/failed tally only ever reached a Toast that fades. Adrian hit both on the installed
 * service: "when I navigate away the page resets, kind of like a pain".
 *
 * The loops and the two status dictionaries moved here. The page binds to this service, subscribes
 * to Changed on init and unsubscribes on Dispose, so navigating away and back RE-ATTACHES to the
 * same run instead of starting from nothing.
 *
 * LIFETIME, and the limit it carries. Registered AddScoped, so this is per CIRCUIT, not per
 * process and not per page:
 *   - a run in flight KEEPS RUNNING when the page is left, and the returning page shows it live;
 *   - a browser REFRESH is a NEW circuit, so the in-memory status is lost. Every per-instance
 *     change-control file already on disk survives that, which is why the Detail cell now offers
 *     a download rather than a filename;
 *   - AddSingleton was rejected: it would show one operator's run to another, and would need a
 *     DeclaredProcessWideState justification in DiLifetimeCensusTests (which scans singletons only).
 *
 * THE PER-INSTANCE WORK IS A DELEGATE, deliberately. The loop below owns sequencing, the status
 * dictionary, the per-instance try/catch and the change notification; the caller supplies what to
 * DO with one instance. That keeps the SQL, the connection resolution and the licence-refusal
 * mapping where they already were, and it leaves this loop unit-testable with no SQL Server, no
 * connection factory and no rendered component.
 *
 * NOTHING HERE MAY TOUCH THE COMPONENT. No StateHasChanged, no InvokeAsync, no ToastService: the
 * run outlives the page, so a reference to the page would be a reference to a dead object. The
 * page reacts to Changed; the page raises its own toasts.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// One instance's place in a multi-instance change-control run. Promoted out of
    /// Pages/ServerConfiguration.razor (where it was a private nested class) unchanged, so the
    /// state survives the page that started the run.
    /// </summary>
    public sealed class InstanceRunStatus
    {
        public string ServerName = "";

        /// <summary>Pending | Running | Refused | Done | Failed.</summary>
        public string State = ServerConfigRunState.StatePending;

        public string? Message;
        public string? ExportedPath;
        public int RowCount;

        /// <summary>Apply lane only: what happened to this instance's generated undo script. Null on the preview lane, which produces none.</summary>
        public string? RollbackNote;

        /// <summary>True only when an undo script was actually written to disk. Drives the colour, so "not saved" cannot read as routine.</summary>
        public bool RollbackWritten;
    }

    /// <summary>
    /// The done/refused/failed tally for one lane. Rendered as persistent markup, not only as a
    /// toast: before 2026-09-08 these three numbers existed for exactly as long as a toast lasted.
    /// </summary>
    public sealed record InstanceRunSummary(int Total, int Done, int Refused, int Failed);

    /// <summary>
    /// ONE instance's operator picker: who this instance's Apply will name, what operators it has to
    /// choose from, and whether a notification to the chosen one would actually be delivered
    /// (2026-09-09, the operator-picker-mailchain lane).
    ///
    /// <para>It lives on the scoped run state for the same reason the statuses do: the read costs a
    /// round trip to every armed instance, and losing it on navigation would make the operator pay
    /// for it again. Selection is per INSTANCE - Adrian's ask was "the operators might be different"
    /// - which is what replaces the single page-level free-text box.</para>
    /// </summary>
    public sealed class InstanceOperatorState
    {
        public string ServerName = "";

        /// <summary>Null until the read has happened. Ask <see cref="InventoryState"/> what it means -
        /// a non-null inventory can also be one nobody was allowed to read (gate finding F2).</summary>
        public OperatorInventory? Inventory;

        /// <summary>The name this instance's Apply will record. Null = nothing picked yet, and the run
        /// cannot arm on this instance (Adrian's ruling 2, third branch: force a choice).</summary>
        public string? SelectedOperator;

        /// <summary>The five-link verdict for <see cref="SelectedOperator"/>. Re-read whenever the
        /// selection changes, so a badge can never describe a different operator than the one shown.</summary>
        public MailChainReport? MailChain;

        public bool Loading;

        /// <summary>Set only when the whole per-instance read threw, which the probe does not do for a
        /// server-side reason - it returns named gaps instead. A value here is a client-side fault.</summary>
        public string? LoadError;

        /// <summary>The last test send's outcome for this instance, kept so the answer stays on screen
        /// after the toast fades (gate finding F7). Never cleared by a re-read: it is a dated fact.</summary>
        public TestSendOutcome? LastSendResult;

        /// <summary>
        /// WHAT THE READ ESTABLISHED for this instance (gate finding F2). A client-side fault
        /// (<see cref="LoadError"/>) and a server-side refusal (a named gap on the inventory) are the
        /// same thing to a human: nobody knows what operators this instance has, so the page must not
        /// offer to create one and Apply must not run with a name nobody chose.
        /// </summary>
        public OperatorInventoryState InventoryState =>
            LoadError is not null ? OperatorInventoryState.Unreadable
            : Inventory is null ? OperatorInventoryState.NotRead
            : Inventory.State;

        /// <summary>Why the read could not be made, for the badge. Null unless Unreadable.</summary>
        public string? UnreadableReason =>
            LoadError ?? (Inventory?.State == OperatorInventoryState.Unreadable
                ? Inventory.UnreadableReason
                : null);

        /// <summary>
        /// True when this instance cannot be applied as it stands: it has operators and nobody has
        /// chosen one, OR its operators could not be read at all. The second arm is gate finding F2 -
        /// an unreadable instance used to answer FALSE here, so Apply stayed enabled and the run fell
        /// through to the baseline script's own SET @OperatorName = N'DBA'.
        /// </summary>
        public bool NeedsChoice =>
            InventoryState == OperatorInventoryState.Unreadable
            || (InventoryState == OperatorInventoryState.Loaded
                && string.IsNullOrWhiteSpace(SelectedOperator));
    }

    /// <summary>
    /// One test send's outcome, kept per instance. <paramref name="AtUtc"/> is UTC because the run
    /// state outlives the page and a local timestamp rendered later is a lie about a machine that may
    /// have changed zone; the page formats it and says UTC.
    /// </summary>
    public sealed record TestSendOutcome(bool Accepted, string Text, DateTime AtUtc);

    /// <summary>
    /// Scoped (per circuit). Owns the multi-instance Preview and Apply run loops for
    /// /server-configuration, their two status dictionaries, and the change notification the page
    /// re-attaches to. See the file header for the lifetime consequence.
    /// </summary>
    public sealed class ServerConfigRunState
    {
        public const string StatePending = "Pending";
        public const string StateRunning = "Running";
        public const string StateRefused = "Refused";
        public const string StateDone    = "Done";
        public const string StateFailed  = "Failed";

        /// <summary>Preview lane, keyed by instance name. Insertion order is the run order.</summary>
        public Dictionary<string, InstanceRunStatus> PreviewStatuses { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Apply lane, keyed by instance name. Insertion order is the run order.</summary>
        public Dictionary<string, InstanceRunStatus> ApplyStatuses { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised on every state transition. A plain Action event, the same change-notification
        /// shape QuickCheckStateService.StateChanged uses, so a listener can attach and detach
        /// freely; a run continues whether anyone is listening or not.
        /// </summary>
        public event Action? Changed;

        public bool PreviewRunning { get; private set; }

        public bool ApplyRunning { get; private set; }

        public InstanceRunSummary PreviewSummary => Summarise(PreviewStatuses.Values);

        public InstanceRunSummary ApplySummary => Summarise(ApplyStatuses.Values);

        // ── The per-instance operator picker (2026-09-09) ──────────────────────────────────

        /// <summary>Per-instance operator inventory, selection and mail-chain verdict, keyed by instance.</summary>
        public Dictionary<string, InstanceOperatorState> OperatorStates { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The key the SINGLE-instance section's picker uses in <see cref="OperatorStates"/>. Angle
        /// brackets are not legal in a SQL Server instance name, so this can never collide with a real
        /// one - and the single-instance picker then shares every method below rather than needing its
        /// own parallel set.
        /// </summary>
        public const string CurrentServerKey = "<current server>";

        /// <summary>True while the picker is reading instances. The arm cannot complete under it.</summary>
        public bool OperatorsLoading { get; private set; }

        /// <summary>
        /// NOTHING ABOUT A RUN CHANGES MID-RUN. The single free-text box was locked for the whole armed
        /// run (D3, 2026-08-20) so every instance recorded the name that was on screen when the operator
        /// armed it. The picker keeps that property and drops only "the same name for every instance":
        /// selections are frozen per instance at arm time and refused entirely while a lane is running.
        /// </summary>
        public bool OperatorSelectionLocked => PreviewRunning || ApplyRunning;

        public InstanceOperatorState GetOrAddOperatorState(string instanceName)
        {
            if (!OperatorStates.TryGetValue(instanceName, out var state))
            {
                state = new InstanceOperatorState { ServerName = instanceName };
                OperatorStates[instanceName] = state;
            }

            return state;
        }

        /// <summary>
        /// Reads the operator inventory for each instance in turn and, once one is selected, its
        /// mail chain. <see cref="Changed"/> fires as each instance lands, so the rows fill in as the
        /// answers arrive rather than all at the end - on a 49-server estate that is the difference
        /// between a live table and a spinner.
        ///
        /// <para>The reads are DELEGATES for the same reason the run loops are: this class then needs no
        /// SQL Server, no connection factory and no rendered component to be tested. Returns false
        /// without touching anything when a read is already in flight or a lane is running.</para>
        /// </summary>
        public async Task<bool> LoadOperatorsAsync(
            IReadOnlyList<string> instanceNames,
            Func<string, CancellationToken, Task<OperatorInventory>> readInventoryAsync,
            Func<string, string, CancellationToken, Task<MailChainReport>> readMailChainAsync,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(instanceNames);
            ArgumentNullException.ThrowIfNull(readInventoryAsync);
            ArgumentNullException.ThrowIfNull(readMailChainAsync);

            if (OperatorsLoading || OperatorSelectionLocked) return false;

            OperatorsLoading = true;
            foreach (var name in instanceNames) GetOrAddOperatorState(name).Loading = true;
            Changed?.Invoke();

            try
            {
                foreach (var name in instanceNames)
                {
                    var state = GetOrAddOperatorState(name);

                    try
                    {
                        state.LoadError = null;
                        state.Inventory = await readInventoryAsync(name, ct);

                        // Keep a selection the human already made, unless the instance no longer has
                        // that operator. Otherwise apply Adrian's default rule.
                        var stillThere = state.SelectedOperator is not null
                            && state.Inventory.Operators.Any(o =>
                                string.Equals(o.Name, state.SelectedOperator, StringComparison.OrdinalIgnoreCase));

                        if (!stillThere)
                            state.SelectedOperator = AgentMailChainProbe.DefaultOperator(state.Inventory);

                        state.MailChain = string.IsNullOrWhiteSpace(state.SelectedOperator)
                            ? null
                            : await readMailChainAsync(name, state.SelectedOperator!, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        state.LoadError = ex.Message;
                    }
                    finally
                    {
                        state.Loading = false;
                        Changed?.Invoke();
                    }
                }
            }
            finally
            {
                OperatorsLoading = false;
                foreach (var name in instanceNames) GetOrAddOperatorState(name).Loading = false;
                Changed?.Invoke();
            }

            return true;
        }

        /// <summary>
        /// Records a new selection for one instance and re-reads its mail chain. REFUSED while a lane is
        /// running (returns false and changes nothing): a run that has already started must not have its
        /// recorded operator moved under it.
        /// </summary>
        public async Task<bool> SelectOperatorAsync(
            string instanceName,
            string? operatorName,
            Func<string, string, CancellationToken, Task<MailChainReport>> readMailChainAsync,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(readMailChainAsync);

            if (OperatorSelectionLocked) return false;

            var state = GetOrAddOperatorState(instanceName);
            state.SelectedOperator = string.IsNullOrWhiteSpace(operatorName) ? null : operatorName;
            state.MailChain = null;
            Changed?.Invoke();

            if (state.SelectedOperator is null) return true;

            state.Loading = true;
            Changed?.Invoke();
            try
            {
                state.MailChain = await readMailChainAsync(instanceName, state.SelectedOperator, ct);
                state.LoadError = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                state.LoadError = ex.Message;
            }
            finally
            {
                state.Loading = false;
                Changed?.Invoke();
            }

            return true;
        }

        /// <summary>Parks a freshly-read report (a test send's re-read) against one instance.</summary>
        public void SetMailChain(string instanceName, MailChainReport? report)
        {
            GetOrAddOperatorState(instanceName).MailChain = report;
            Changed?.Invoke();
        }

        /// <summary>
        /// THE ARM SNAPSHOT. Copies each instance's selection into a map the run then reads, so a
        /// selection changed after arming cannot reach a run that is already under way - the same
        /// property the frozen instance list has, applied to the names.
        /// </summary>
        public Dictionary<string, string?> FreezeSelectedOperators(IEnumerable<string> instanceNames)
        {
            var frozen = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in instanceNames ?? Enumerable.Empty<string>())
                frozen[name] = OperatorStates.TryGetValue(name, out var s) ? s.SelectedOperator : null;

            return frozen;
        }

        /// <summary>
        /// The instances that CANNOT be applied as they stand: they have operators and none is picked,
        /// or their operators could not be read (gate finding F2). An instance whose read SUCCEEDED and
        /// returned zero operators is not here - the picker offers free text for it, which is what the
        /// baseline script's create path has always taken.
        /// </summary>
        public IReadOnlyList<string> InstancesAwaitingOperatorChoice(IEnumerable<string> instanceNames) =>
            (instanceNames ?? Enumerable.Empty<string>())
                .Where(n => OperatorStates.TryGetValue(n, out var s) && s.NeedsChoice)
                .ToList();

        /// <summary>
        /// The subset of the above that nobody can answer by picking: the read itself failed. The page
        /// needs the two apart because "pick an operator for SQL07" is an instruction a human can follow
        /// and "operators could not be read on SQL07" is not.
        /// </summary>
        public IReadOnlyList<string> InstancesWithUnreadableOperators(IEnumerable<string> instanceNames) =>
            (instanceNames ?? Enumerable.Empty<string>())
                .Where(n => OperatorStates.TryGetValue(n, out var s)
                            && s.InventoryState == OperatorInventoryState.Unreadable)
                .ToList();

        /// <summary>
        /// Parks a test send's outcome against one instance so it stays on screen (gate finding F7): a
        /// 5,000 ms toast was the only feedback a REFUSED send left behind, and a refusal is precisely
        /// the outcome worth reading twice.
        /// </summary>
        public void RecordTestSendResult(string instanceName, bool accepted, string text, DateTime atUtc)
        {
            GetOrAddOperatorState(instanceName).LastSendResult =
                new TestSendOutcome(accepted, text ?? "", atUtc);
            Changed?.Invoke();
        }

        /// <summary>
        /// Runs the preview lane over <paramref name="instanceNames"/> in order, one at a time.
        /// Returns false without touching anything if a preview is already running in this circuit.
        /// </summary>
        public Task<bool> RunPreviewAsync(
            IReadOnlyList<string> instanceNames,
            Func<string, InstanceRunStatus, CancellationToken, Task> perInstanceAsync,
            CancellationToken ct = default) =>
            RunLaneAsync(
                PreviewStatuses,
                () => PreviewRunning,
                v => PreviewRunning = v,
                instanceNames,
                perInstanceAsync,
                ct);

        /// <summary>
        /// Runs the apply lane over <paramref name="instanceNames"/> in order, one at a time.
        /// Returns false without touching anything if an apply is already running in this circuit.
        /// </summary>
        public Task<bool> RunApplyAsync(
            IReadOnlyList<string> instanceNames,
            Func<string, InstanceRunStatus, CancellationToken, Task> perInstanceAsync,
            CancellationToken ct = default) =>
            RunLaneAsync(
                ApplyStatuses,
                () => ApplyRunning,
                v => ApplyRunning = v,
                instanceNames,
                perInstanceAsync,
                ct);

        /// <summary>
        /// The loop, moved from the page with its behaviour intact: sequential foreach + await,
        /// every instance seeded Pending up front, each one taken to Running and then to a terminal
        /// state, and a per-instance try/catch so ONE failure never aborts the run. Only the
        /// notification changed - what was <c>await InvokeAsync(StateHasChanged)</c> is now
        /// <c>Changed?.Invoke()</c>.
        /// </summary>
        private async Task<bool> RunLaneAsync(
            Dictionary<string, InstanceRunStatus> statuses,
            Func<bool> isRunning,
            Action<bool> setRunning,
            IReadOnlyList<string> instanceNames,
            Func<string, InstanceRunStatus, CancellationToken, Task> perInstanceAsync,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(instanceNames);
            ArgumentNullException.ThrowIfNull(perInstanceAsync);

            // Not a lock, a guard: the buttons are already disabled while a lane runs. This stops a
            // second circuit-local caller from clearing a live run's results out from under it.
            if (isRunning()) return false;

            setRunning(true);
            statuses.Clear();
            foreach (var name in instanceNames)
            {
                statuses[name] = new InstanceRunStatus { ServerName = name, State = StatePending };
            }

            Changed?.Invoke();

            try
            {
                foreach (var name in instanceNames)
                {
                    if (!statuses.TryGetValue(name, out var status)) continue;

                    status.State = StateRunning;
                    Changed?.Invoke();

                    try
                    {
                        await perInstanceAsync(name, status, ct);
                    }
                    catch (Exception ex)
                    {
                        status.State = StateFailed;
                        status.Message = ex.Message;
                    }

                    // Fires for EVERY instance, including the ones the caller resolves with a
                    // Refused short-circuit. The page's old loop skipped the notification on that
                    // path (a bare `continue`), so a refused row sat unrendered until the next
                    // instance finished.
                    Changed?.Invoke();
                }
            }
            finally
            {
                setRunning(false);
                Changed?.Invoke();
            }

            return true;
        }

        public static InstanceRunSummary Summarise(IEnumerable<InstanceRunStatus>? statuses)
        {
            int total = 0, done = 0, refused = 0, failed = 0;

            foreach (var s in statuses ?? Enumerable.Empty<InstanceRunStatus>())
            {
                if (s is null) continue;
                total++;
                if (string.Equals(s.State, StateDone, StringComparison.OrdinalIgnoreCase)) done++;
                else if (string.Equals(s.State, StateRefused, StringComparison.OrdinalIgnoreCase)) refused++;
                else if (string.Equals(s.State, StateFailed, StringComparison.OrdinalIgnoreCase)) failed++;
            }

            return new InstanceRunSummary(total, done, refused, failed);
        }

        /// <summary>
        /// The name a consolidated download is offered under. Mirrors
        /// <see cref="ServerConfigScriptService.BuildChangeControlFileName"/>, including its
        /// "-applied" suffix for the apply lane, so the two artifacts read as one family.
        /// </summary>
        public static string BuildConsolidatedFileName(DateTime timestamp, bool apply = false) =>
            $"consolidated_{timestamp:yyyyMMdd-HHmmss}_changecontrol{(apply ? "-applied" : "")}.txt";

        /// <summary>
        /// Concatenates every Done instance's already-exported change-control file, in run order,
        /// under a header naming the instance and the file, and lists the instances that produced
        /// nothing in a trailer with the reason they gave.
        ///
        /// <para>Reads the files the run already wrote and WRITES NOTHING. A file that has gone
        /// missing, or that cannot be read, is named in place rather than thrown - a consolidated
        /// view that dies on one absent file is worse than one that says which file is absent.</para>
        /// </summary>
        public static string BuildConsolidatedText(IEnumerable<InstanceRunStatus>? statuses)
        {
            var list = (statuses ?? Enumerable.Empty<InstanceRunStatus>())
                .Where(s => s is not null)
                .ToList();

            if (list.Count == 0) return "No instances have run yet.";

            var sb = new StringBuilder();

            foreach (var s in list.Where(s => string.Equals(s.State, StateDone, StringComparison.OrdinalIgnoreCase)))
            {
                var path = s.ExportedPath;
                var fileName = string.IsNullOrWhiteSpace(path) ? "(no exported file)" : Path.GetFileName(path);

                sb.Append("===== ").Append(s.ServerName).Append(" - ").Append(fileName).AppendLine(" =====");
                sb.AppendLine();

                if (string.IsNullOrWhiteSpace(path))
                {
                    sb.AppendLine("[this instance recorded no exported file]");
                }
                else if (!File.Exists(path))
                {
                    sb.AppendLine("[the exported file is no longer on disk: " + path + "]");
                }
                else
                {
                    string body;
                    try
                    {
                        body = File.ReadAllText(path);
                    }
                    catch (Exception ex)
                    {
                        body = "[the exported file could not be read: " + ex.Message + "]";
                    }

                    sb.AppendLine(body.TrimEnd());
                }

                sb.AppendLine();
            }

            var notExported = list
                .Where(s => !string.Equals(s.State, StateDone, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (notExported.Count > 0)
            {
                sb.AppendLine("===== Instances that exported nothing =====");
                sb.AppendLine();

                foreach (var s in notExported)
                {
                    sb.Append(s.ServerName).Append(" - ").Append(DescribeNotExported(s.State));
                    if (!string.IsNullOrWhiteSpace(s.Message)) sb.Append(": ").Append(s.Message);
                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// What to call an instance that landed in the trailer. Pending and Running are REACHABLE
        /// here, which is the whole reason this exists: the Consolidated control renders as soon as
        /// the dictionaries are seeded — before the first instance has finished — so an operator can
        /// open the panel mid-run. Printing the bare state under "Instances that exported nothing"
        /// then reads as a verdict on a row that has simply not had its turn yet. Refused and Failed
        /// are verdicts and keep their state word and their Message unchanged.
        /// (Gate finding F3, 2026-09-08.)
        /// </summary>
        private static string DescribeNotExported(string? state) =>
            string.Equals(state, StatePending, StringComparison.OrdinalIgnoreCase) ? "Pending (not started)"
            : string.Equals(state, StateRunning, StringComparison.OrdinalIgnoreCase) ? "Running (still running)"
            : (state ?? "");
    }
}
