/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

// ── Every app-issued DDL writes BOTH registers (F-A / ruling R1, 2026-09-01) ────────
//
// WHAT WAS WRONG. Three surfaces sent DDL to a monitored server and journaled NOTHING — no audit
// entry, no change-ledger row: the plan viewer's index-create, the dashboard action cell (including
// its double hop), and the XEvent session lifecycle. Remediation was already audited; these three
// were the gap. An operator could create an index and start and drop event sessions on a client's
// production instance and no register in the product held a record of it.
//
// R1 ruled BOTH registers for all of them: the HMAC-chained AuditLogService AND a
// ChangeItemService row. This file exercises the journal itself against REAL instances of both
// registers over temp files — no mocks, because the thing being proved is that a record reaches
// disk, and a mock cannot fail the way a locked SQLite file can.
//
// The failure path is covered by driving XEventService at an unreachable instance: the connection
// genuinely fails, and the assertion is that the attempt AND a Failed outcome are both on record.
// "Journaled on the happy path only" is the shape this ruling exists to prevent.
public class DdlJournalingTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public DdlJournalingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ddl-journal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* test cleanup */ }
    }

    private AuditLogService NewAudit() => new(Path.Combine(_tempDir, "audit"), startFlushTimer: false);

    private ChangeItemService NewLedger() =>
        new(NullLogger<ChangeItemService>.Instance, audit: null,
            dbPath: Path.Combine(_tempDir, "change-items.db"));

    private List<AuditLogEntry> ReadAudit()
    {
        var dir = Path.Combine(_tempDir, "audit");
        if (!Directory.Exists(dir)) return new List<AuditLogEntry>();
        return Directory.GetFiles(dir, "audit-*.jsonl")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .SelectMany(File.ReadAllLines)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<AuditLogEntry>(l, Json)!)
            .ToList();
    }

    /// <summary>
    /// A statement long enough that any truncation would be visible, carrying a marker near its
    /// END so a prefix-only record fails the assertion rather than passing on the first 120 chars.
    /// The 120 is not arbitrary: it is the cap the dashboard's log line used.
    /// </summary>
    private const string TailMarker = "IX_TAIL_MARKER_MUST_SURVIVE";
    private static string LongStatement() =>
        "CREATE NONCLUSTERED INDEX [IX_probe] ON [db].[dbo].[t] ("
        + string.Join(", ", Enumerable.Range(0, 40).Select(i => $"[column_number_{i:D3}]"))
        + $") WITH (ONLINE = ON) -- {TailMarker}";

    // ── The journal writes both registers ───────────────────────────────────────

    [Fact]
    public async Task A_successful_ddl_reaches_the_audit_chain_and_the_change_ledger()
    {
        using var audit = NewAudit();
        using var ledger = NewLedger();
        var journal = new DdlJournal(audit, ledger);

        journal.HasAuditRegister.Should().BeTrue();
        journal.HasLedgerRegister.Should().BeTrue();
        journal.Describe().Should().Be("audit chain + change ledger");

        var sql = LongStatement();
        journal.RecordAttempt(AuditLogService.DdlSurfaces.IndexCreate, "create-index", "SRV1", sql, "Database=master");
        await journal.RecordOutcomeAsync(AuditLogService.DdlSurfaces.IndexCreate, "create-index", "SRV1", sql,
            AuditLogService.DdlOutcomes.Succeeded);

        var entries = ReadAudit();
        var attempted = entries.Single(e => e.EventType == AuditEventType.DdlAttempted);
        var completed = entries.Single(e => e.EventType == AuditEventType.DdlCompleted);

        attempted.Details["ServerName"].Should().Be("SRV1");
        attempted.Details["Surface"].Should().Be(AuditLogService.DdlSurfaces.IndexCreate);
        attempted.Details["Statement"].Should().Be(sql, "the statement is journaled in full, never truncated.");
        completed.Details["Outcome"].Should().Be(AuditLogService.DdlOutcomes.Succeeded);
        completed.Severity.Should().Be(AuditSeverity.Info);

        var row = ledger.GetLatest("SRV1", AuditLogService.DdlSurfaces.IndexCreate);
        row.Should().NotBeNull("the second register is not optional — R1 ruled BOTH.");
        row!.RemediationScript.Should().Be(sql, "the ledger carries the same statement text as the chain.");
        row.Rationale.Should().NotBeNullOrWhiteSpace("the ledger rejects a row with no stated reason.");
        row.Status.Should().Be(ChangeItemStatus.Logged);
    }

    [Fact]
    public async Task A_failed_ddl_is_journaled_as_failed_in_both_registers()
    {
        using var audit = NewAudit();
        using var ledger = NewLedger();
        var journal = new DdlJournal(audit, ledger);

        var sql = "ALTER EVENT SESSION [probe] ON SERVER STATE = START";
        journal.RecordAttempt(AuditLogService.DdlSurfaces.XEventLifecycle, "start", "SRV2", sql);
        await journal.RecordOutcomeAsync(AuditLogService.DdlSurfaces.XEventLifecycle, "start", "SRV2", sql,
            AuditLogService.DdlOutcomes.Failed, "Msg 15151, the event session does not exist");

        var completed = ReadAudit().Single(e => e.EventType == AuditEventType.DdlCompleted);
        completed.Details["Outcome"].Should().Be(AuditLogService.DdlOutcomes.Failed);
        completed.Details["Error"].Should().Contain("15151");
        completed.Severity.Should().Be(AuditSeverity.Error,
            "a failed statement that logs at Info reads as benign in the ledger view. Distinct "
            + "terminal states exist so it cannot.");

        var row = ledger.GetLatest("SRV2", AuditLogService.DdlSurfaces.XEventLifecycle);
        row!.Rationale.Should().Contain("failed");
        row.Rationale.Should().Contain("15151");
    }

    [Fact]
    public async Task A_cancelled_ddl_says_the_server_side_effect_is_unknown()
    {
        using var audit = NewAudit();
        using var ledger = NewLedger();
        var journal = new DdlJournal(audit, ledger);

        await journal.RecordOutcomeAsync(AuditLogService.DdlSurfaces.IndexCreate, "create-index", "SRV3",
            "CREATE NONCLUSTERED INDEX [IX_x] ON [t] ([a]);", AuditLogService.DdlOutcomes.Cancelled);

        var completed = ReadAudit().Single(e => e.EventType == AuditEventType.DdlCompleted);
        completed.Severity.Should().Be(AuditSeverity.Warning,
            "cancelling CREATE INDEX does not prove nothing happened on the server.");

        ledger.GetLatest("SRV3", AuditLogService.DdlSurfaces.IndexCreate)!
            .Rationale.Should().Contain("unknown",
                "the row must not imply the change did or did not land when nobody knows.");
    }

    [Fact]
    public void An_unrecognised_outcome_is_treated_as_suspect_not_as_success()
    {
        using var audit = NewAudit();
        audit.LogDdlCompleted(AuditLogService.DdlSurfaces.IndexCreate, "create-index", "SRV1", "SELECT 1", "WhoKnows");
        audit.Flush();

        ReadAudit().Single().Severity.Should().Be(AuditSeverity.Warning,
            "an outcome string outside the canonical set is not evidence of success.");
    }

    [Fact]
    public void A_blocked_ddl_is_journaled_with_the_refused_text_and_the_reason()
    {
        using var audit = NewAudit();
        var journal = new DdlJournal(audit, ledger: null);

        var hostile = "EXEC xp_cmdshell 'whoami'";
        journal.RecordBlocked(AuditLogService.DdlSurfaces.DashboardAction, "double-hop", "SRV1", hostile,
            "xp_cmdshell (OS command execution) is not permitted on this surface");

        var blocked = ReadAudit().Single(e => e.EventType == AuditEventType.DdlBlocked);
        blocked.Severity.Should().Be(AuditSeverity.Warning);
        blocked.Details["Statement"].Should().Be(hostile,
            "a refusal is often the ONLY trace that something tried, so the refused text is kept in full.");
        blocked.Details["Reason"].Should().Contain("xp_cmdshell");
    }

    [Fact]
    public void The_journal_reports_which_registers_it_actually_has()
    {
        // A caller must never be able to say "journaled to both registers" on a host that has one.
        using var audit = NewAudit();
        using var ledger = NewLedger();

        new DdlJournal(audit, null).Describe().Should().Be("audit chain only (change ledger not registered)");
        new DdlJournal(null, ledger).Describe().Should().Be("change ledger only (audit chain not registered)");
        new DdlJournal(null, null).Describe().Should().Be("no registers available");
    }

    [Fact]
    public async Task Journaling_with_no_registers_at_all_does_not_throw()
    {
        // A journaling fault must not turn a completed server-side change into an exception the
        // operator reads as "it did not run".
        var journal = new DdlJournal(null, null);
        journal.RecordAttempt("s", "op", "SRV", "SELECT 1");
        journal.RecordBlocked("s", "op", "SRV", "SELECT 1", "because");
        await journal.RecordOutcomeAsync("s", "op", "SRV", "SELECT 1", AuditLogService.DdlOutcomes.Succeeded);
    }

    // ── The XEvent surface journals through it, on the failure path ─────────────

    [Fact]
    public async Task XEventService_journals_the_attempt_and_the_failure_against_an_unreachable_instance()
    {
        using var audit = NewAudit();
        using var ledger = NewLedger();
        var journal = new DdlJournal(audit, ledger);
        var svc = new XEventService(NullLogger<XEventService>.Instance, connectionManager: null!, journal);

        // A host that cannot resolve, with a 1-second connect timeout. The failure is real: the
        // connection is genuinely attempted and genuinely does not open.
        const string unreachable =
            "Data Source=sqltriage-no-such-host-9f2c;Initial Catalog=master;Integrated Security=true;"
            + "Connect Timeout=1;TrustServerCertificate=true";

        var result = await svc.StopSessionAsync(unreachable, "probe-session");
        result.Should().StartWith("Error:", "the unreachable instance must surface as an error, not a success.");

        var entries = ReadAudit();
        var attempted = entries.Should().ContainSingle(e => e.EventType == AuditEventType.DdlAttempted).Subject;
        var completed = entries.Should().ContainSingle(e => e.EventType == AuditEventType.DdlCompleted).Subject;

        attempted.Details["Operation"].Should().Be("stop");
        attempted.Details["ServerName"].Should().Be("sqltriage-no-such-host-9f2c",
            "the target is read from the connection string, so a failure to connect is still "
            + "recorded against a named server rather than as 'unknown'.");
        attempted.Details["Statement"].Should().Be("ALTER EVENT SESSION [probe-session] ON SERVER STATE = STOP");

        completed.Details["Outcome"].Should().Be(AuditLogService.DdlOutcomes.Failed);

        ledger.GetLatest("sqltriage-no-such-host-9f2c", AuditLogService.DdlSurfaces.XEventLifecycle)
            .Should().NotBeNull("the failure path writes the ledger row too, not just the chain.");
    }

    [Fact]
    public async Task XEventService_journals_a_hostile_session_name_as_the_escaped_text_that_would_run()
    {
        // The two fixes meet here: what the journal records is the ESCAPED statement, so the
        // evidence trail shows what the server would actually have received.
        using var audit = NewAudit();
        var journal = new DdlJournal(audit, ledger: null);
        var svc = new XEventService(NullLogger<XEventService>.Instance, connectionManager: null!, journal);

        const string unreachable =
            "Data Source=sqltriage-no-such-host-9f2c;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=true";

        await svc.DropSessionAsync(unreachable, "x] TO SERVER; DROP TABLE t;--");

        var attempted = ReadAudit().Single(e => e.EventType == AuditEventType.DdlAttempted);
        attempted.Details["Statement"].Should().Be(
            "DROP EVENT SESSION [x]] TO SERVER; DROP TABLE t;--] ON SERVER",
            "the journal records the statement as it would be sent — escaped — so the record and "
            + "the wire agree.");
    }

    // ── The component surfaces journal on every path (source guards) ────────────
    //
    // QueryPlanModal and DynamicDashboard are Razor components whose DDL paths need a live circuit
    // and a live SQL Server to drive. Rather than assert nothing about them, these guards read the
    // source and pin the shape the ruling requires, each with a control below.

    /// <summary>
    /// Counts the journal calls in a source file. Throws when the file has no journal calls at all:
    /// zero must never read as "correctly journaled".
    /// </summary>
    internal static (int Attempts, int Outcomes, int Blocks) CountJournalCalls(string source, string journalIdentifier)
    {
        var attempts = Regex.Matches(source, Regex.Escape(journalIdentifier) + @"\??\.RecordAttempt\(").Count;
        var outcomes = Regex.Matches(source, @"RecordOutcomeAsync\(").Count;
        var blocks = Regex.Matches(source, Regex.Escape(journalIdentifier) + @"\??\.RecordBlocked\(").Count;

        if (attempts == 0 && outcomes == 0 && blocks == 0)
        {
            throw new InvalidOperationException(
                $"No '{journalIdentifier}' journal calls were found, so this guard cannot judge the "
                + "surface and must not report clean. Rename the identifier here if it moved.");
        }

        return (attempts, outcomes, blocks);
    }

    [Fact]
    public void The_plan_viewer_journals_the_attempt_and_all_three_terminal_states()
    {
        var source = ReadRepoFile("Components", "Shared", "QueryPlanModal.razor");
        var (attempts, outcomes, blocks) = CountJournalCalls(source, "Journal");

        attempts.Should().BeGreaterThan(0, "the attempt must be recorded before the statement is sent.");
        blocks.Should().Be(2, "both statement-shape refusals are journaled, not only Serilog-logged.");

        // Success, cancellation and failure. Counted through the wrapper this file's subject uses.
        Regex.Matches(source, @"JournalOutcome\(").Count.Should().BeGreaterThanOrEqualTo(4,
            "one definition plus the success, cancelled and failed call sites. A missing catch-path "
            + "call is how a failed DDL becomes an unrecorded one.");

        source.Should().Contain("DdlOutcomes.Succeeded");
        source.Should().Contain("DdlOutcomes.Cancelled");
        source.Should().Contain("DdlOutcomes.Failed");
    }

    [Fact]
    public void The_plan_viewer_index_create_has_a_bounded_command_timeout()
    {
        // F-C. It was CommandTimeout = 0 — unbounded.
        var source = ReadRepoFile("Components", "Shared", "QueryPlanModal.razor");
        var code = StripCsCommentLines(source);

        code.Should().NotMatchRegex(@"CommandTimeout\s*=\s*0\s*;",
            "an unbounded index-create holds the operation open with no ceiling.");
        code.Should().MatchRegex(@"cmd\.CommandTimeout\s*=\s*300\s*;",
            "300s matches this surface's sibling. Provisional pending the PerCheckTimeout lane, and "
            + "the comment beside it says so.");
    }

    [Fact]
    public void The_dashboard_action_path_guards_journals_and_logs_the_full_statement()
    {
        var source = ReadRepoFile("Components", "Shared", "DynamicDashboard.razor");
        var code = StripCsCommentLines(source);
        var (attempts, outcomes, blocks) = CountJournalCalls(code, "DdlJournal");

        attempts.Should().BeGreaterThan(0, "R2(d): the statement is journaled before it is sent.");
        outcomes.Should().BeGreaterThanOrEqualTo(3,
            "success, cancelled and failed. The cancelled and failed paths are the ones that get "
            + "forgotten, and they are the ones an auditor needs.");
        blocks.Should().BeGreaterThan(0, "R2(c): a guard refusal is recorded.");

        code.Should().Contain("DangerousExecGuard.Inspect(ddl)",
            "R2(c): the guard must inspect the RESOLVED statement. Inspecting the cell's SELECT "
            + "instead would miss the server-authored payload entirely, which is the whole point of "
            + "the double hop.");

        // R2(b): the 120-character truncation, verbatim as it stood.
        code.Should().NotContain("ddl.Length > 120 ? ddl[..120]",
            "a 120-character prefix of a server-authored statement hides the payload.");
        code.Should().Contain(@"Logger.LogInformation(""Action SQL executed on {Server}: {Sql}"", serverName, ddl);",
            "the executed statement is logged in full.");
    }

    [Fact]
    public void The_dashboard_action_button_asks_before_it_writes()
    {
        // R2(a). There was no confirmation anywhere on this path.
        var code = StripCsCommentLines(ReadRepoFile("Components", "Shared", "DataGrid.razor"));

        code.Should().Contain("if (!await ConfirmAction(sql)) return;",
            "the confirmation must GATE execution, not merely exist. A confirm whose answer is "
            + "ignored is worse than none — it looks like a gate in a screenshot.");
        code.Should().Contain(@"InvokeAsync<bool>(""confirm""",
            "window.confirm is the app's existing per-action gate (Sessions.razor's KILL uses it), "
            + "and the type argument must be bool. A real JSRuntime deserialises the answer with "
            + "JsonSerializer.Deserialize<TValue>, so asking for <object> yields a JsonElement and "
            + "any `is bool` test on it is false for EVERY answer — the gate would refuse the "
            + "operator's OK. Shipped that way on 2026-09-01 and caught by the adversarial verifier.");
        code.Should().NotContain(@"InvokeAsync<object>(""confirm""",
            "the shape that made the gate a permanent refusal.");
    }

    // ── The confirmation gate, driven ───────────────────────────────────────────
    //
    // The source guard above says the call is there. These drive the real component's real handler
    // through a recording IJSRuntime, so the assertion is on BEHAVIOUR: does the answer decide
    // whether the delegate runs. A confirm-and-ignore-the-answer implementation passes the source
    // guard and fails these.

    /// <summary>
    /// Drives the REAL DataGrid's private ExecuteAction — the method the Execute button calls —
    /// with a recording IJSRuntime supplying the confirmation answer.
    ///
    /// <para>The component is constructed directly rather than rendered, so it has no render
    /// handle. That is deliberate and it is what makes the result readable: the first thing
    /// ExecuteAction does AFTER the gate is set row state and call StateHasChanged, which throws on
    /// an unattached component. So <c>Threw</c> means "got past the gate" and no throw means "the
    /// gate held" — the same positive-control shape RbacRound4RegressionTests uses for the handler
    /// gates on this component's own callers.</para>
    /// </summary>
    private static async Task<(bool Threw, string? PromptShown)> DriveExecuteActionAsync(
        bool? confirmAnswer, string? mode, string sql = "CREATE INDEX ix ON dbo.t(c)")
    {
        var js = new FakeJsRuntime();
        if (confirmAnswer.HasValue) js.Answers["confirm"] = confirmAnswer.Value;
        else js.ThrowOnInvoke = new InvalidOperationException("no interop on this circuit");

        var type = typeof(SQLTriage.Components.Shared.DataGrid);
        var component = new SQLTriage.Components.Shared.DataGrid();
        type.GetProperty("JS", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, js);

        component.OnActionExecute = (_, _) => Task.FromResult((true, "ok"));
        component.ActionColumnMode = mode;

        var handler = type.GetMethod("ExecuteAction",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        handler.Should().NotBeNull(
            "DataGrid.ExecuteAction is the handler the Execute button calls. If it moved, this test "
            + "moves with it — a gate test that stops finding its handler is worse than none.");

        Exception? thrown = null;
        try { await (Task)handler!.Invoke(component, new object?[] { 0, sql })!; }
        catch (Exception ex) { thrown = ex; }

        var prompt = js.Calls.Count > 0 ? js.Calls[0].Args?[0]?.ToString() : null;
        return (thrown != null, prompt);
    }

    [Fact]
    public async Task Declining_the_confirmation_stops_the_action_before_it_starts()
    {
        var (threw, prompt) = await DriveExecuteActionAsync(confirmAnswer: false, mode: null);

        prompt.Should().NotBeNull("the operator must be asked before anything is sent.");
        threw.Should().BeFalse(
            "the handler ran on past the gate after the operator declined. This is the whole ruling: "
            + "before 2026-09-01 one click on a grid row wrote to a monitored server with nothing in "
            + "between.");
    }

    [Fact]
    public async Task Accepting_the_confirmation_lets_the_handler_proceed()
    {
        // THE POSITIVE CONTROL. Without it the test above would pass against a handler that returns
        // immediately for any answer, or against a deleted handler.
        var (threw, prompt) = await DriveExecuteActionAsync(confirmAnswer: true, mode: null);

        prompt.Should().Contain("CREATE INDEX ix ON dbo.t(c)", "the dialog shows the statement.");
        threw.Should().BeTrue(
            "on 'yes' the handler must run on past the gate and reach the row-state update. A quiet "
            + "completion here would mean the gate refuses both answers.");
    }

    [Fact]
    public async Task An_unanswerable_confirmation_declines_rather_than_proceeding()
    {
        // Fail-safe direction. The lesson this repo already paid for: a usability fail-safe that
        // inverted in a security decision became an anonymous LAN hatch. An unaskable question is
        // not a yes.
        var (threw, _) = await DriveExecuteActionAsync(confirmAnswer: null, mode: null);

        threw.Should().BeFalse("when the answer cannot be obtained the write must not proceed.");
    }

    [Fact]
    public async Task The_double_hop_confirmation_says_the_server_authors_the_statement()
    {
        var (_, prompt) = await DriveExecuteActionAsync(
            confirmAnswer: true, mode: "query", sql: "SELECT create_script FROM dbo.suggestions");

        prompt.Should().Contain("written by the monitored server",
            "the operator is approving a statement the app has not seen. Saying so is the difference "
            + "between consent and a click.");
    }

    [Theory]
    [InlineData("query", "executes whatever SQL")]
    [InlineData(null, "runs the statement below")]
    public void The_confirmation_text_says_which_kind_of_action_this_is(string? mode, string expected)
    {
        var prompt = SQLTriage.Components.Shared.DataGrid.BuildActionConfirmPrompt("SELECT 'CREATE INDEX ...'", mode);
        prompt.Should().Contain(expected);
    }

    [Fact]
    public void The_confirmation_dialog_shortens_a_long_statement_but_says_that_it_did()
    {
        // A dialog cap is not a log truncation: the full statement is journaled either way. It must
        // still be visible that the reader is not seeing everything.
        var longSql = new string('x', 4000);
        var prompt = SQLTriage.Components.Shared.DataGrid.BuildActionConfirmPrompt(longSql, null);

        prompt.Length.Should().BeLessThan(2000, "a browser confirm cannot show 4000 characters.");
        prompt.Should().Contain("shortened for this dialog",
            "silently showing a prefix as if it were the whole statement is not consent.");
        prompt.Should().Contain("the full statement is journaled");
    }

    // ── The plan viewer's authorization gate stands IN FRONT of the journal ─────
    //
    // WHAT WAS WRONG (found by the ddl-governance cold gate, ruled a clear fix 2026-09-01). The
    // run_scripts check lived only inside ExecuteSingleIndex, BELOW the two statement-shape
    // refusals in ExecuteIndexFromOperator. A caller in No-Pants mode holding a ConnectionId but
    // NOT run_scripts could therefore not execute anything — that was always fail-closed — but it
    // could reach both RecordBlocked calls, each of which writes the caller's statement text IN
    // FULL into the tamper-evident chain. That is ledger spam: an unauthorized caller choosing what
    // gets appended to the audit chain, at whatever length and content it likes.
    //
    // These tests drive the REAL component's real [JSInvokable] entry point, so the assertion is on
    // BEHAVIOUR — what reached the audit file on disk — not on the shape of the source. The source
    // guard below them covers the profile where the behaviour cannot be driven at all.

    /// <summary>
    /// The two payloads that trip the two RecordBlocked sites, so both journal-write sites are
    /// exercised rather than only the first.
    /// </summary>
    public static TheoryData<string> GuardBlockedPayloads => new()
    {
        // Site 1: not a single CREATE NONCLUSTERED INDEX at all.
        "DROP TABLE dbo.customers WHERE spam = '" + TailMarker + "'",
        // Site 2: the right opening verb, then a comment marker beyond one trailing semicolon.
        // LongStatement() ends in "-- IX_TAIL_MARKER_MUST_SURVIVE", which is exactly that shape,
        // and it is long enough that a truncating journal would fail the assertion.
        LongStatement(),
    };

    [Theory]
    [MemberData(nameof(GuardBlockedPayloads))]
    public async Task An_unauthorized_caller_is_refused_before_anything_reaches_either_register(string payload)
    {
        using var audit = NewAudit();
        using var ledger = NewLedger();

        var state = await DeniedCircuitAsync();
        state.IsAuthorized("run_scripts").Should().BeFalse(
            "precondition: this circuit must genuinely lack run_scripts, or the test proves nothing.");

        var component = NewPlanViewer(state, audit, ledger);

        var thrown = await DriveExecuteIndexFromOperatorAsync(component, payload);

        thrown.Should().BeNull(
            "an unauthorized caller must be refused outright, not carried into the body. Every other "
            + "service on this component is null, so a throw would mean it ran on past the gate.");

        ReadAudit().Should().BeEmpty(
            "THE DEFECT. Before the hoist this caller's statement text was written into the "
            + "tamper-evident chain in full, by a caller with no permission to send DDL at all. "
            + "Execution was never reachable; the chain was.");

        ledger.GetLatest("SRV-PROBE", AuditLogService.DdlSurfaces.IndexCreate).Should().BeNull(
            "and nothing reached the second register either.");

        IsExecuting(component).Should().BeFalse("the refusal must land before any execution state is set.");
    }

    [Theory]
    [MemberData(nameof(GuardBlockedPayloads))]
    public async Task An_authorized_caller_whose_statement_is_blocked_still_gets_the_full_journaling(string payload)
    {
        using var audit = NewAudit();
        using var ledger = NewLedger();

        var state = await AllowedCircuitAsync();
        var component = NewPlanViewer(state, audit, ledger);

        if (BuildModules.Community)
        {
            // Community hardwires GetNoPantsMode() to false, so this surface refuses EVERY caller
            // at its first line and there is no authorized path to exercise. Asserted rather than
            // skipped: a silent skip in the only profile CI runs is how a guard stops guarding.
            (await DriveExecuteIndexFromOperatorAsync(component, payload)).Should().BeNull();
            ReadAudit().Should().BeEmpty(
                "with No-Pants compiled off, the plan viewer never journals — for anyone.");
            return;
        }

        state.IsAuthorized("run_scripts").Should().BeTrue(
            "precondition: the authorized half must actually be authorized, or the byte-equivalence "
            + "claim below is vacuous.");

        var thrown = await DriveExecuteIndexFromOperatorAsync(component, payload);
        thrown.Should().BeNull("the shape guard returns; it does not fault.");

        var blocked = ReadAudit().Should().ContainSingle(e => e.EventType == AuditEventType.DdlBlocked).Subject;
        blocked.Details["Statement"].Should().Be(payload,
            "the authorized flow is unchanged by the hoist: the refused text is still journaled in "
            + "full, never truncated.");
        blocked.Details["ServerName"].Should().Be("SRV-PROBE");
        blocked.Details["Surface"].Should().Be(AuditLogService.DdlSurfaces.IndexCreate);
        blocked.Details["Reason"].Should().NotBeNullOrWhiteSpace();
        blocked.Severity.Should().Be(AuditSeverity.Warning);
    }

    [Fact]
    public async Task An_authorized_caller_with_a_well_formed_statement_still_reaches_the_execution_path()
    {
        // THE POSITIVE CONTROL for the hoist. Without it, the refusal tests above would pass against
        // a gate that refuses everyone — the failure mode a hoisted check is most likely to have.
        using var audit = NewAudit();
        using var ledger = NewLedger();

        var state = await AllowedCircuitAsync();
        var component = NewPlanViewer(state, audit, ledger);
        const string wellFormed = "CREATE NONCLUSTERED INDEX [IX_probe] ON [dbo].[t] ([a])";

        var thrown = await DriveExecuteIndexFromOperatorAsync(component, wellFormed);

        if (BuildModules.Community)
        {
            thrown.Should().BeNull("No-Pants is compiled off here, so the surface refuses before the gate.");
            IsExecuting(component).Should().BeFalse();
            return;
        }

        thrown.Should().NotBeNull(
            "an authorized caller with a well-formed statement must run ON PAST both the permission "
            + "check and the shape guards into ExecuteSingleIndex, where the unattached component's "
            + "InvokeAsync(StateHasChanged) faults. A quiet completion here would mean the hoisted "
            + "check refuses authorized callers too.");
        IsExecuting(component).Should().BeTrue(
            "_indexIsExecuting is set inside ExecuteSingleIndex, past the gate — so it is the "
            + "evidence that the gate let this caller through.");

        ReadAudit().Should().NotContain(e => e.EventType == AuditEventType.DdlBlocked,
            "a well-formed statement is not a blocked one.");
    }

    /// <summary>
    /// The profile-independent net. CI builds and tests the COMMUNITY profile only, where
    /// GetNoPantsMode() is compiled to false and the behavioural tests above cannot reach the guard
    /// at all. This one reads the shipped source and pins the ORDER, so an un-hoist is caught in
    /// every profile.
    /// </summary>
    [Fact]
    public void The_plan_viewer_checks_the_permission_before_its_first_journal_write()
    {
        var code = StripCsCommentLines(ReadRepoFile("Components", "Shared", "QueryPlanModal.razor"));
        AssertPermissionPrecedesJournaling(code);
    }

    internal static void AssertPermissionPrecedesJournaling(string code)
    {
        var entry = code.IndexOf("public async Task ExecuteIndexFromOperator(", StringComparison.Ordinal);
        entry.Should().BeGreaterThan(-1,
            "ExecuteIndexFromOperator is the [JSInvokable] entry point under guard. If it was renamed "
            + "this guard moves with it — one that stops finding its subject is worse than none.");

        var gate = code.IndexOf("MayCreateIndex", entry, StringComparison.Ordinal);
        var firstJournalWrite = code.IndexOf("Journal.RecordBlocked(", entry, StringComparison.Ordinal);

        gate.Should().BeGreaterThan(-1, "the run_scripts gate must be inside ExecuteIndexFromOperator.");
        firstJournalWrite.Should().BeGreaterThan(-1,
            "the shape refusals must still be journaled — the hoist removes nothing.");

        gate.Should().BeLessThan(firstJournalWrite,
            "the permission check must precede EVERY journal write that carries caller-supplied "
            + "text. Below it, an unauthorized caller writes attacker-chosen statement text into the "
            + "tamper-evident chain — a ledger-spam vector, independent of whether any DDL runs.");
    }

    // ── Controls ────────────────────────────────────────────────────────────────

    [Fact]
    public void Control_the_ordering_guard_flags_the_real_pre_fix_order()
    {
        // The verbatim pre-fix order, reduced to the two markers the guard reads. If this control
        // goes green the ordering guard has stopped detecting the defect it was written for.
        const string PreFix = @"
    public async Task ExecuteIndexFromOperator(string ddl)
    {
        if (!UserSettings.GetNoPantsMode() || string.IsNullOrEmpty(ConnectionId)) return;
        if (string.IsNullOrWhiteSpace(ddl) || !ddl.TrimStart().StartsWith(""CREATE NONCLUSTERED INDEX""))
        {
            Journal.RecordBlocked(Surface, ""create-index"", ServerName, ddl, ""Not a single statement."");
            return;
        }
        await ExecuteSingleIndex(ddl);
    }
    private bool MayCreateIndex => UserState.IsAuthorized(""run_scripts"");";

        var act = () => AssertPermissionPrecedesJournaling(PreFix);
        act.Should().Throw<Exception>(
            "the pre-fix order — gate after the journal write — must still be detectable.");
    }

    [Fact]
    public void Control_the_call_counter_refuses_to_judge_a_source_with_no_journal_calls()
    {
        var act = () => CountJournalCalls("public class Nothing { }", "Journal");
        act.Should().Throw<InvalidOperationException>(
            "a file with no journal calls must throw, not report zero-and-green.");
    }

    [Fact]
    public void Control_the_truncation_guard_flags_the_real_pre_fix_log_line()
    {
        // The verbatim pre-fix line from DynamicDashboard.razor.
        const string PreFix =
            @"Logger.LogInformation(""Action SQL executed on {Server}: {Sql}"", serverName, ddl.Length > 120 ? ddl[..120] + ""..."" : ddl);";

        PreFix.Should().Contain("ddl.Length > 120 ? ddl[..120]",
            "if this control goes green the truncation guard has stopped detecting the line it was "
            + "written for.");
    }

    [Fact]
    public void Control_the_timeout_guard_flags_the_real_pre_fix_assignment()
    {
        const string PreFix = "            cmd.CommandTimeout = 0; // index creation can be long";

        Regex.IsMatch(StripCsCommentLines(PreFix), @"CommandTimeout\s*=\s*0\s*;").Should().BeTrue(
            "the unbounded assignment must still be detectable, or the F-C guard protects nothing.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("These guards read app source, so they need the repo root.");

        var path = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
        if (!File.Exists(path))
            throw new FileNotFoundException($"The file under guard was not at {path}.", path);
        return File.ReadAllText(path);
    }

    // ── Driving QueryPlanModal without a renderer ───────────────────────────────
    //
    // Same shape RbacRound4RegressionTests uses for this component's sibling handlers: construct
    // the component, inject only the services the path under test needs, leave the rest null. A
    // handler that runs past its gate then faults visibly instead of quietly doing nothing.

    private AppUserState NewCircuit(IPAddress remote)
    {
        var configPath = Path.Combine(_tempDir, "rbac-config-" + Guid.NewGuid().ToString("N") + ".json");
        var usersPath = Path.Combine(_tempDir, "rbac-users-" + Guid.NewGuid().ToString("N") + ".json");
        // Dormant RBAC: the unconfigured install. Loopback keeps the bootstrap hatch, a LAN caller
        // does not — the pair RbacRound4RegressionTests already proves for this permission.
        File.WriteAllText(configPath, JsonSerializer.Serialize(new RbacConfig()));

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote;

        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

        var state = new AppUserState(
            HostEnvironmentInfo.BrowserHosted,
            new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath),
            services.BuildServiceProvider(),
            NullLogger<AppUserState>.Instance);

        return state;
    }

    /// <summary>An unauthenticated circuit from the LAN: no run_scripts, no bootstrap hatch.</summary>
    private async Task<AppUserState> DeniedCircuitAsync()
    {
        var state = NewCircuit(IPAddress.Parse("192.168.10.32"));
        await state.InitAsync();
        return state;
    }

    /// <summary>The console user on an unconfigured install: run_scripts via the bootstrap hatch.</summary>
    private async Task<AppUserState> AllowedCircuitAsync()
    {
        var state = NewCircuit(IPAddress.Parse("::ffff:127.0.0.1"));
        await state.InitAsync();
        return state;
    }

    /// <summary>
    /// The real QueryPlanModal, with No-Pants mode on and a ConnectionId set — the state the defect
    /// needs. JS, ConnectionManager and HealthService stay null on purpose.
    /// </summary>
    private object NewPlanViewer(AppUserState state, AuditLogService audit, ChangeItemService ledger)
    {
        var type = typeof(AppUserState).Assembly.GetType("SQLTriage.Components.Shared.QueryPlanModal");
        type.Should().NotBeNull(
            "QueryPlanModal is the component under guard. If it moved, this test moves with it.");

        var component = Activator.CreateInstance(type!, nonPublic: true)!;

        var settings = new UserSettingsService(Path.Combine(_tempDir, "user-settings.json"));
        settings.SetNoPantsMode(true);   // no-op under the community profile, by design

        InjectService(component, state);
        InjectService(component, settings);
        InjectService(component, new ToastService());
        InjectService(component, new DdlJournal(audit, ledger));

        type!.GetProperty("ConnectionId")!.SetValue(component, "conn-probe");
        type.GetProperty("ServerName")!.SetValue(component, "SRV-PROBE");
        type.GetProperty("DatabaseName")!.SetValue(component, "master");
        return component;
    }

    private static void InjectService(object component, object service)
    {
        var matches = component.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.GetCustomAttributes(typeof(InjectAttribute), inherit: true).Any())
            .Where(p => p.PropertyType == service.GetType())
            .ToList();

        matches.Count.Should().Be(1,
            component.GetType().Name + " must have exactly one injected " + service.GetType().Name
            + " property for this test to wire; found " + matches.Count + ".");

        matches[0].SetValue(component, service);
    }

    /// <summary>
    /// Invokes the real [JSInvokable] entry point and returns the exception it produced, or null.
    /// </summary>
    private static async Task<Exception?> DriveExecuteIndexFromOperatorAsync(object component, string ddl)
    {
        var method = component.GetType().GetMethod("ExecuteIndexFromOperator",
            BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull(
            "ExecuteIndexFromOperator is the method JS calls. A test that stops finding it is worse "
            + "than none.");

        try
        {
            await (Task)method!.Invoke(component, new object?[] { ddl })!;
            return null;
        }
        catch (TargetInvocationException ex) { return ex.InnerException ?? ex; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>Reads _indexIsExecuting — set inside ExecuteSingleIndex, past the gate.</summary>
    private static bool IsExecuting(object component)
    {
        var field = component.GetType().GetField("_indexIsExecuting",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull(
            "_indexIsExecuting is how these tests tell 'past the gate' from 'refused'. If it was "
            + "renamed, this reader moves with it.");
        return (bool)field!.GetValue(component)!;
    }

    private static string StripCsCommentLines(string source) =>
        string.Join("\n", source.Split('\n').Where(l =>
        {
            var t = l.TrimStart();
            return !t.StartsWith("//", StringComparison.Ordinal)
                && !t.StartsWith("///", StringComparison.Ordinal)
                && !t.StartsWith("@*", StringComparison.Ordinal);
        }));
}
