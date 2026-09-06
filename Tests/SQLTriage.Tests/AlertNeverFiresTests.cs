/* In the name of God, the Merciful, the Compassionate */

// ── strings-r2-06 / r1-02 / r1-04 / r1-05: alerts no value could reach, 2026-08-28 ────────────
//
// WHY THIS FILE EXISTS. AlertEvaluationService.IsThresholdBreached is STRICT:
//
//     return op == "less_than" ? value < threshold.Value : value > threshold.Value;
//
// Never >=. Combine that with a query that can only answer 0 or 1, and a shipped
// warning threshold of 1, and the alert is silent for ever: 1 is not greater than 1. There is no
// number an operator can type that helps, except one below 1. SIXTEEN shipped alerts were in
// exactly that state at 84beb1c, four of them announcing conditions a DBA pages on.
//
// WHAT WAS PROVED LIVE, before the fix, on .\new2022 and re-checked on .\old2017:
//   high_risk_linked_servers            returned 1 - the condition PRESENT - and could not fire.
//   implicit_column_conversions         returned 1, likewise.
//   cluster_failover                    returned NO ROWS AT ALL on a standalone instance, because
//                                       its FROM clause was sys.dm_os_cluster_nodes.
//   public_role_dangerous_permissions   did not merely stay silent, it THREW: "Invalid column name
//                                       'object_id'". sys.database_permissions has major_id.
//   low_compression_success_rates       shipped the literal "SELECT 0 -- Requires manual review".
//
// THE HUNT'S EVIDENCE BOUNDARY, CLOSED HERE. The discover pass could not exercise
// IsThresholdBreached at all - "my attempt to exercise it by reflection failed - Windows PowerShell
// 5.1 cannot load a .NET 10 assembly" - so the whole finding rested on reading the source. The
// method is `internal` and Properties/AssemblyInfo.cs carries
// [assembly: InternalsVisibleTo("SQLTriage.Tests")], so `dotnet test` reaches it directly. Tier 1
// below drives the real production predicate, including the value == threshold case
// AlertBaselineFenceDirectionTests never had an InlineData row for (its four rows were 100/90,
// 85/90, 97/95, 80/95 - no equality in either direction). That row is added there too, at the gap
// it was named at; here it is joined to the catalogue consequence.
//
// A FIFTEEN-ALERT EXTENSION THE HUNT DID NOT FILE. Its census looked for the yes/no shape and
// stopped. Extending it to "every alert whose threshold a single occurrence cannot reach" found
// fifteen more: real COUNT(*) queries, also under warning 1, so they needed TWO occurrences while
// their own descriptions named one - "A user database is not in ONLINE state" (Critical), "A fatal
// error (severity >= 20) has been logged" (Critical), "An Availability Group listener is offline"
// (Critical). Same arithmetic, milder shape, same remedy: warning 0.
//
// WHAT IS REAL HERE. Tier 1 drives AlertEvaluationService.IsThresholdBreached, the production
// decision function. Tiers 2-4 drive AlertEvaluationService.QueryValueShape over the SHIPPED
// Config/alert-definitions.json - the bytes that install, read off disk, not a fixture. Nothing is
// re-implemented in the test.
//
// MUTATION THAT MUST FAIL. (1) Put any one of the 31 alerts back to "warning": 1 in
// Config/alert-definitions.json: Every_shipped_alert_can_be_reached_by_its_own_smallest_occurrence
// goes red and names the id. (2) Loosen IsThresholdBreached to >=: the tier-1 boundary rows go red.
// (3) Delete the is_linked = 1 predicate from high_risk_linked_servers, or the is_ms_shipped = 0
// join from public_role_dangerous_permissions: the two false-positive pins go red. Both were
// MEASURED, not imagined - the shipped predicates matched the local server and Microsoft's own
// spt_* grants on a stock instance.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class AlertNeverFiresTests
{
    // Anchored on the solution file, the repo's own convention. Probing for
    // Config/alert-definitions.json would match the TEST OUTPUT copy instead of what ships.
    private static string ShippedPath() =>
        Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json");

    private static List<AlertDefinition> Shipped()
    {
        var json = File.ReadAllText(ShippedPath());
        var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        file.Should().NotBeNull();
        return file!.Alerts;
    }

    private static AlertDefinition Alert(string id) =>
        Shipped().Single(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    // ── Tier 1: the predicate, at the boundary the hunt could not reach ─────────

    /// <summary>
    /// The equality row. This is the whole mechanism of strings-r2-06 in one assertion: a query
    /// capped at 1, against a threshold of 1, under greater_than, is not a breach.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0, "greater_than", false)]   // the defect: value == threshold does not fire
    [InlineData(1.0, 0.0, "greater_than", true)]    // the fix: warning 0 makes the same reading fire
    [InlineData(0.0, 0.0, "greater_than", false)]   // and a quiet instance stays quiet
    [InlineData(2.0, 1.0, "greater_than", true)]    // which is why a COUNT needed TWO under warning 1
    [InlineData(95.0, 95.0, "less_than", false)]    // equality does not fire in the other direction
    [InlineData(94.9, 95.0, "less_than", true)]
    public void IsThresholdBreached_is_strict_in_both_directions(
        double value, double threshold, string op, bool expected)
    {
        AlertEvaluationService.IsThresholdBreached(value, threshold, op).Should().Be(expected);
    }

    [Fact]
    public void A_null_threshold_is_not_a_breach_at_any_value()
    {
        AlertEvaluationService.IsThresholdBreached(1.0, null, "greater_than").Should().BeFalse();
        AlertEvaluationService.IsThresholdBreached(0.0, null, "less_than").Should().BeFalse();
    }

    // ── Tier 2: the shape reading, which the lint below rests on ────────────────

    // The expected shape is passed by NAME rather than as the enum value: the enum is internal (a
    // public test method may not take an internal parameter type), and comparing ToString() still
    // fails if the reader returns the wrong member.
    [Theory]
    [InlineData("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.servers) THEN 1 ELSE 0 END AS value", "BooleanFlag")]
    [InlineData("SELECT\r\n  CASE WHEN 1 = 1\r\n  THEN 1 ELSE 0 END AS value", "BooleanFlag")]
    // The INVERTED yes/no, which this reader used to answer Unknown for. That answer took
    // failed_login_xevent_session out of all four lints below while it sat in the file at warning 1.
    [InlineData("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = 'TrackFailedLogins') THEN 0 ELSE 1 END AS value", "BooleanFlag")]
    [InlineData("SELECT\r\n  CASE WHEN 1 = 1\r\n  THEN 0 ELSE 1 END AS value", "BooleanFlag")]
    [InlineData("SELECT COUNT(*) AS value FROM sys.databases WHERE state <> 0", "OccurrenceCount")]
    [InlineData("SELECT COUNT( * ) AS value FROM sys.databases", "OccurrenceCount")]
    [InlineData("SELECT 0 -- Requires manual review using compression analysis scripts", "Constant")]
    [InlineData("SELECT 1 AS value", "Constant")]
    [InlineData("SELECT MAX(pages_kb) AS value FROM sys.dm_os_memory_clerks", "Unknown")]
    [InlineData("", "Unknown")]
    public void QueryValueShape_reads_only_shapes_it_positively_recognises(string sql, string expected)
    {
        AlertEvaluationService.QueryValueShape(new AlertDefinition { Query = sql })
            .ToString().Should().Be(expected);
    }

    // ── Tier 3: THE LINT, over the bytes that install ───────────────────────────

    /// <summary>
    /// THE GUARD THAT MAKES THIS CLASS UNABLE TO RECUR. For every shipped alert whose query has a
    /// known reachable range and whose unit says that range is a cardinality, the smallest value
    /// worth speaking about must actually breach a threshold under the alert's own operator,
    /// through the real predicate.
    ///
    /// <para>WHAT IS DELIBERATELY NOT FLAGGED, because running this lint the first time flagged it
    /// and the flag was WRONG: an alert with a real band. <c>vlf_count</c> (500),
    /// <c>blocked_sessions_count</c> (5), <c>tempdb_contention</c> (5) and <c>connection_count</c>
    /// (80 percent) all count things and all correctly stay quiet at one. The defect was never
    /// "1 does not fire"; it was a threshold of EXACTLY 1, which every operator and every one of
    /// those descriptions reads as "one or more" and which strict <c>&gt;</c> makes mean two. That
    /// is what the second test below refuses.</para>
    /// </summary>
    [Fact]
    public void Every_shipped_alert_can_be_reached_by_its_own_smallest_occurrence()
    {
        var unreachable = new List<string>();

        foreach (var alert in Shipped())
        {
            var smallest = AlertEvaluationService.SmallestAlertableValue(alert);
            if (smallest is null) continue;                     // shape or unit says nothing useful

            // A real band is a decision, not a defect: only a threshold of exactly 1 is the trap.
            var warning = alert.Thresholds.Warning;
            if (warning.HasValue && warning.Value > 1) continue;

            var warningFires = AlertEvaluationService.IsThresholdBreached(
                smallest.Value, warning, alert.Operator);
            var criticalFires = AlertEvaluationService.IsThresholdBreached(
                smallest.Value, alert.Thresholds.Critical, alert.Operator);

            if (!warningFires && !criticalFires)
                unreachable.Add(
                    $"{alert.Id} ({AlertEvaluationService.QueryValueShape(alert)}, " +
                    $"operator {alert.Operator}, warning {warning?.ToString() ?? "none"}) " +
                    $"cannot fire at {smallest.Value}");
        }

        unreachable.Should().BeEmpty(
            "an alert whose smallest real occurrence cannot breach its own threshold is a control "
            + "that does nothing, and the operator has no way to tell");
    }

    /// <summary>
    /// THE SHARPER HALF OF THE SAME GUARD, and the one that catches the defect as authored. A
    /// warning threshold of exactly 1 under strict greater_than, over a yes/no or a cardinality,
    /// means "two or more" while reading as "one or more". Thirty-one shipped alerts carried it.
    /// If a future author genuinely means two or more, the honest form is to say so in the
    /// description and change this test with that reason attached.
    /// </summary>
    [Fact]
    public void No_shipped_alert_carries_a_warning_threshold_of_exactly_one()
    {
        Shipped()
            .Where(a => AlertEvaluationService.SmallestAlertableValue(a) is not null)
            .Where(a => string.Equals(a.Operator, "greater_than", StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Thresholds.Warning == 1)
            .Select(a => a.Id)
            .Should().BeEmpty(
                "strict > turns a threshold of 1 into a requirement for 2, which is not what the "
                + "control says and not what any of these descriptions say");
    }

    /// <summary>
    /// A literal cannot measure anything, so no threshold rescues it. Kept separate because the
    /// remedy differs: a constant query needs a measurement or an honest removal, not a threshold
    /// move.
    ///
    /// <para>EXEMPT: an alert routed by <c>queryMode</c>. Its query field is text nobody executes -
    /// the evaluator hands it to a built-in handler that runs its own SQL - so the literal
    /// <c>SELECT 1 AS value</c> in <c>instance_unreachable</c> and <c>machine_unreachable</c> is
    /// inert rather than broken. That dead-query class is the Alerts lane's <c>strings-r2-04b</c>,
    /// already surfaced in the alert editor; it is not this test's subject and pretending otherwise
    /// would have made this lint cry wolf on its first run.</para>
    ///
    /// <para>THE EXEMPTION IS THIS TEST'S ALONE, and 2026-08-28's fix round is why that sentence is
    /// here. The exemption was originally written into <c>SmallestAlertableValue</c> itself, where it
    /// took every queryMode alert out of ALL FOUR lints below - and two Critical connectivity alerts
    /// and the deadlock alert were sitting in that shadow at warning 1, unable to fire. The dead
    /// query really is inert; the alerts were broken for a different reason, in the handler. Reading
    /// the handler's own range is now <c>SpecialHandlerValueShape</c>, so only the CONSTANT-query
    /// question is exempt here, which is the only question the dead text cannot answer.</para>
    /// </summary>
    [Fact]
    public void No_executed_alert_query_returns_a_constant()
    {
        Shipped()
            .Where(a => string.IsNullOrWhiteSpace(a.QueryMode)
                        || string.Equals(a.QueryMode, "standard", StringComparison.OrdinalIgnoreCase))
            .Where(a => AlertEvaluationService.QueryValueShape(a) == AlertEvaluationService.AlertValueShape.Constant)
            .Select(a => a.Id)
            .Should().BeEmpty("strings-r1-02: low_compression_success_rates shipped SELECT 0 as an "
                              + "ENABLED check with a threshold control the operator could tune for ever");
    }

    /// <summary>The census this lane acted on, pinned so a later reader can see the size of it.</summary>
    [Fact]
    public void The_thirty_one_alerts_this_lane_repaired_are_all_still_reachable()
    {
        var shaped = Shipped()
            .Where(a => AlertEvaluationService.SmallestAlertableValue(a) is not null)
            .ToList();

        shaped.Count.Should().BeGreaterThanOrEqualTo(31,
            "16 yes/no plus 15 occurrence-count alerts were repaired; the shape reader must still see them");

        foreach (var a in shaped.Where(a =>
                     string.Equals(a.Operator, "greater_than", StringComparison.OrdinalIgnoreCase)))
        {
            // Either it fires on one occurrence, or it carries a band above one that an author chose.
            var firesAtOne = AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator);
            (firesAtOne || a.Thresholds.Warning > 1).Should().BeTrue(
                a.Id + " neither fires on a single occurrence nor carries a deliberate band above one");
        }
    }

    // ── Tier 4: the four named findings, pinned individually ────────────────────

    /// <summary>
    /// strings-r1-05. The shipped query compared NodeName against a scalar subquery over the same
    /// row the outer WHERE had already pinned, so it was false by construction; and it kept no
    /// prior-owner state, so it could not have detected a failover even if the comparison worked.
    /// The replacement measures what the instance can actually see, and the description says so.
    /// </summary>
    [Fact]
    public void Cluster_failover_stops_claiming_to_detect_something_it_cannot_see()
    {
        var a = Alert("cluster_failover");

        a.Query.Should().NotContain("is_current_owner",
            "the self-comparison against the current owner was the defect");
        a.Query.Should().Contain("sys.dm_os_cluster_nodes");
        a.Query.Should().Contain("status");
        a.Description.Should().Contain("does NOT detect that a failover has happened",
            "an operator reading this must not believe the instance can see a completed failover");
        AlertEvaluationService.QueryValueShape(a)
            .Should().Be(AlertEvaluationService.AlertValueShape.OccurrenceCount);
    }

    /// <summary>
    /// strings-r1-04, and its fix round. The shipped query matched EXPLICIT '%CAST(%' and
    /// '%CONVERT(%' in query text, which is the opposite of an implicit conversion, and capped the
    /// answer at 1 so it could not fire in any case. The replacement reads SQL Server's own
    /// PlanAffectingConvert warning, and is BOUNDED: the unbounded scan measured 2,625 ms of CPU on a
    /// 718-plan cache, against 329 ms for the top 50.
    ///
    /// <para>WHAT THE FIX ROUND CHANGED, 2026-08-28. The first repair counted affected plans and
    /// called the band "a SHARE OF THAT 50" while the unit said count and the threshold was an
    /// absolute 25. Two things were wrong with that. The denominator is not 50: the CROSS APPLY drops
    /// plans whose XML is unavailable, and .\OLD2017 sampled SIX, so "of 50" was not a reachable
    /// denominator there at all. And the calibration was contradicted by the arithmetic beside it -
    /// the description ended "which is why this alert does not fire on the first one" while the same
    /// instance MEASURED 26 of 50 against a warning of 25. It fired, hourly, for ever, on an idle
    /// healthy instance. The number is now a percentage of the plans ACTUALLY sampled, and the band
    /// is set above the worst healthy reading this product has measured.</para>
    /// </summary>
    [Fact]
    public void Implicit_column_conversions_measures_implicit_conversions_and_says_what_it_skipped()
    {
        var a = Alert("implicit_column_conversions");

        a.Query.Should().NotContain("CAST(%", "matching explicit CAST text was the whole defect");
        a.Query.Should().NotContain("CONVERT(%");
        a.Query.Should().Contain("ConvertIssue");
        a.Query.Should().Contain("TOP (50)", "an unbounded plan-cache scan costs seconds of CPU per run");

        // The denominator is what was sampled, not the cap. This is the half .\OLD2017 disproved:
        // six plans sampled there, so a threshold expressed as "of 50" measured nothing.
        a.Unit.Should().Be("percent");
        a.Query.Should().Contain("/ COUNT(*)",
            "the share must be taken over the plans actually sampled, which is at most 50 and often "
            + "far fewer");
        a.Description.Should().Contain("ACTUALLY SAMPLED",
            "a percentage whose denominator is not the stated cap must say so");

        // THE CALIBRATION, MEASURED 2026-08-28 on two idle instances: 46 percent then 42 percent on
        // .\NEW2022, 0 percent on .\OLD2017. Both must stay quiet, or the fix has swapped a silent
        // alert for a permanent one - which the governing ruling calls worse than no alert.
        a.Thresholds.Warning.Should().Be(60);
        a.Thresholds.Critical.Should().Be(80);
        AlertEvaluationService.IsThresholdBreached(46.0, a.Thresholds.Warning, a.Operator)
            .Should().BeFalse("the worst healthy reading measured must not fire");
        AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator)
            .Should().BeFalse();
        AlertEvaluationService.IsThresholdBreached(85.0, a.Thresholds.Warning, a.Operator)
            .Should().BeTrue("85 percent of your most expensive plans is not a healthy baseline");

        // The band rests on two instances, and the description has to admit that rather than
        // presenting it as a general truth.
        a.Description.Should().Contain("two instances only");
    }

    /// <summary>
    /// strings-r1-02. A hardcoded literal became a real ratio. It also changes direction: a
    /// SUCCESS rate is bad when it is LOW, so the operator is less_than, and the old greater_than
    /// would have been silent all over again.
    /// </summary>
    [Fact]
    public void Low_compression_success_rates_measures_a_rate_and_compares_it_the_right_way()
    {
        var a = Alert("low_compression_success_rates");

        a.Query.Should().NotStartWith("SELECT 0");
        a.Query.Should().Contain("page_compression_attempt_count");
        a.Query.Should().Contain("page_compression_success_count");
        a.Operator.Should().Be("less_than", "a success rate is bad when it falls, not when it rises");
        a.Unit.Should().Be("percent");
        a.Thresholds.Warning.Should().Be(60);
        a.Thresholds.Critical.Should().Be(40);
        a.Thresholds.Critical!.Value.Should().BeLessThan(a.Thresholds.Warning!.Value,
            "under less_than the critical band must be the NARROWER one, or every warning is a critical");

        // The materiality floor: no reading at all below 1000 attempts, rather than a percentage
        // computed from three attempts.
        a.Query.Should().Contain(">= 1000");
        a.Description.Should().Contain("1000 attempts");
    }

    /// <summary>
    /// THE TRAP THIS LANE NEARLY WALKED INTO, kept as a test because it was MEASURED and not
    /// imagined. sys.servers holds the instance's OWN row at server_id 0, and every SQL Server
    /// ships it with is_rpc_out_enabled and is_remote_login_enabled both set. Making the alert fire
    /// without excluding it would have reported a high-risk linked server on every server in the
    /// world - swapping a silent alert for a false one, which is not a fix.
    /// </summary>
    [Fact]
    public void High_risk_linked_servers_does_not_count_the_instance_itself()
    {
        var a = Alert("high_risk_linked_servers");

        a.Query.Should().Contain("is_linked = 1");
        a.Query.Should().Contain("COUNT(*)");
        a.Unit.Should().Be("count", "the unit now describes what the number is");
        a.Description.Should().Contain("own row in sys.servers is excluded");
    }

    /// <summary>
    /// The same trap, second instance. A stock SQL Server grants public SELECT on spt_values,
    /// spt_monitor and the spt_fallback tables in every database: 44 rows on .\new2022 and 42 on
    /// .\old2017 before the exclusion, 0 after. And the shipped query read a column that does not
    /// exist, so it threw rather than returning anything at all.
    /// </summary>
    [Fact]
    public void Public_role_permissions_reads_a_real_column_and_ignores_Microsofts_own_grants()
    {
        var a = Alert("public_role_dangerous_permissions");

        a.Query.Should().NotContain("dp.object_id",
            "sys.database_permissions has major_id; object_id made this alert throw on every run");
        a.Query.Should().Contain("dp.major_id");
        a.Query.Should().Contain("is_ms_shipped = 0");
        a.Description.Should().Contain("shipped objects are excluded");
    }

    /// <summary>
    /// on_fail_action is a step's flow-control action, not a notification: a job that retries on
    /// failure satisfied the shipped predicate while telling nobody anything.
    /// </summary>
    [Fact]
    public void Jobs_without_notifications_looks_at_notifications()
    {
        var a = Alert("sql_agent_jobs_without_notifications");

        a.Query.Should().NotContain("on_fail_action");
        a.Query.Should().Contain("notify_level_email");
        a.Query.Should().Contain("notify_email_operator_id");
        a.Description.Should().Contain("event log");
    }

    /// <summary>
    /// The seven alerts that declared unit "count" over a yes/no. A cardinality unit on a value
    /// capped at 1 tells the operator "1 connection" when there may be fifty, so the unit and the
    /// query had to be reconciled - and the query is the half that was wrong.
    /// </summary>
    [Theory]
    // implicit_column_conversions left this list in the 2026-08-28 fix round: its number is now a
    // PERCENTAGE of the plans sampled, not a cardinality, because the denominator is not the stated
    // cap of 50 and .\OLD2017 sampled six. Its unit is pinned in its own test above, so it is
    // recorded here rather than silently dropped.
    [InlineData("unencrypted_tcp_connections")]
    [InlineData("high_risk_linked_servers")]
    [InlineData("public_role_dangerous_permissions")]
    [InlineData("orphaned_sql_agent_jobs")]
    [InlineData("sql_agent_jobs_without_notifications")]
    [InlineData("auto_update_stats_async")]
    public void An_alert_declaring_a_count_returns_a_count(string id)
    {
        var a = Alert(id);
        a.Unit.Should().Be("count");
        AlertEvaluationService.QueryValueShape(a)
            .Should().Be(AlertEvaluationService.AlertValueShape.OccurrenceCount,
                id + " declares a cardinality, so its query may not answer a capped yes/no");
    }

    /// <summary>
    /// The yes/no alerts that legitimately stay yes/no. A service is running or it is not; there is
    /// no cardinality to report, so unit "event" is honest and only the threshold was wrong.
    /// </summary>
    [Theory]
    [InlineData("agent_stopped")]
    [InlineData("fulltext_stopped")]
    [InlineData("dtc_stopped")]
    [InlineData("browser_stopped")]
    [InlineData("ssis_stopped")]
    [InlineData("sql_analysis_service_stopped")]
    [InlineData("sql_reporting_service_stopped")]
    [InlineData("database_mail_not_configured")]
    public void A_yes_no_condition_keeps_its_event_unit_and_fires_at_zero(string id)
    {
        var a = Alert(id);

        AlertEvaluationService.QueryValueShape(a)
            .Should().Be(AlertEvaluationService.AlertValueShape.BooleanFlag);
        a.Unit.Should().Be("event", "there is no count to report for a yes/no");
        AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator)
            .Should().BeTrue(id + " must speak when its condition is true");
        AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator)
            .Should().BeFalse(id + " must stay quiet when its condition is false");
    }

    /// <summary>
    /// The fifteen the hunt did not file. Each description names ONE occurrence; each threshold
    /// demanded two. Six of them are Critical.
    /// </summary>
    [Theory]
    [InlineData("database_unavailable")]
    [InlineData("error_log_fatal")]
    [InlineData("ag_replica_unhealthy")]
    [InlineData("ag_listener_offline")]
    [InlineData("ag_not_ready_automatic_failover")]
    [InlineData("page_verify_disabled")]
    [InlineData("agent_job_failure")]
    [InlineData("config_change")]
    [InlineData("mirror_status_change")]
    [InlineData("data_file_autogrow")]
    [InlineData("log_file_autogrow")]
    [InlineData("tempdb_autogrow")]
    [InlineData("error_log_severity")]
    [InlineData("ag_failover")]
    [InlineData("agent_job_completion")]
    public void A_single_occurrence_now_reaches_the_threshold(string id)
    {
        var a = Alert(id);

        AlertEvaluationService.QueryValueShape(a)
            .Should().Be(AlertEvaluationService.AlertValueShape.OccurrenceCount);
        AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator)
            .Should().BeTrue(
                id + " counts occurrences and its own description names one of them");
    }

    // ── Tier 5: the shadow this lint cast over its own defect class (fix round, 2026-08-28) ──

    /// <summary>
    /// The blind spot, named as a test so it cannot come back. Every mode the evaluator routes to a
    /// built-in handler is listed here with the range that handler can return, read off the handler
    /// itself. A new queryMode with no entry answers Unknown and is silently exempt from the four
    /// lints above - which is exactly how three shipped alerts (two of them Critical) sat inside a
    /// green guard at warning 1, unable to fire.
    /// </summary>
    [Theory]
    [InlineData("connectivity_check", "BooleanFlag")]        // CheckConnectivityAsync: 0 or 1
    [InlineData("host_connectivity_check", "BooleanFlag")]   // the SAME handler
    [InlineData("deadlock_count", "OccurrenceCount")]        // CountDeadlocksAsync: COUNT(*)
    [InlineData("error_log_scan", "OccurrenceCount")]        // ScanErrorLogAsync: COUNT(*)
    [InlineData("io_error_check", "OccurrenceCount")]        // CheckIoErrorsAsync: COUNT(*)
    [InlineData("registry_check", "Unknown")]                // CheckPowerPlanAsync: 0.0 / 2.0 / null
    [InlineData("something_invented_later", "Unknown")]
    public void Every_built_in_handler_declares_the_range_it_can_return(string mode, string expected)
    {
        AlertEvaluationService.SpecialHandlerValueShape(mode).ToString().Should().Be(expected);
    }

    /// <summary>
    /// The consequence, on the bytes that install: a queryMode alert whose handler answers a yes/no
    /// or a cardinality is now visible to <see cref="AlertEvaluationService.SmallestAlertableValue"/>,
    /// so the two lints above cover it. Before this, all seven queryMode alerts answered null.
    /// </summary>
    [Theory]
    [InlineData("instance_unreachable")]
    [InlineData("machine_unreachable")]
    [InlineData("deadlock")]
    [InlineData("error_log_severity")]
    [InlineData("error_log_fatal")]
    public void A_special_path_alert_fires_on_the_first_occurrence_its_handler_can_report(string id)
    {
        var a = Alert(id);

        AlertEvaluationService.SmallestAlertableValue(a).Should().Be(1.0,
            id + " is routed to a handler whose smallest alertable answer is 1");
        AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator)
            .Should().BeTrue(id + " must speak the first time its handler reports the condition");
        AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator)
            .Should().BeFalse(id + " must stay quiet when the condition is absent");
    }

    /// <summary>
    /// The two queryMode alerts that are deliberately NOT at zero, recorded rather than skipped so a
    /// blanket carve-out cannot grow back. <c>logon_failure</c> counts failed logins and 5 in five
    /// minutes is a real band an author chose; <c>windows_power_plan</c>'s handler answers 0.0 or
    /// 2.0, so its warning of 1 is a midpoint, not the strict-comparison trap.
    /// </summary>
    [Fact]
    public void The_two_special_alerts_above_zero_are_bands_and_not_the_trap()
    {
        var logon = Alert("logon_failure");
        logon.Thresholds.Warning.Should().Be(5);
        AlertEvaluationService.SmallestAlertableValue(logon).Should().Be(1.0);

        var power = Alert("windows_power_plan");
        AlertEvaluationService.SpecialHandlerValueShape(power.QueryMode)
            .Should().Be(AlertEvaluationService.AlertValueShape.Unknown);
        AlertEvaluationService.SmallestAlertableValue(power).Should().BeNull(
            "0.0 / 2.0 is neither a flag nor a cardinality, and warning 1 sits in the gap on purpose");
        AlertEvaluationService.IsThresholdBreached(2.0, power.Thresholds.Warning, power.Operator)
            .Should().BeTrue("the unhealthy reading must still fire");
        AlertEvaluationService.IsThresholdBreached(0.0, power.Thresholds.Warning, power.Operator)
            .Should().BeFalse("the healthy reading must not");
    }

    /// <summary>
    /// The inverted yes/no, pinned on the shipped bytes. 1 means the session is ABSENT, which is the
    /// alertable state, so the threshold has to be 0 for the same reason every other yes/no does.
    /// </summary>
    [Fact]
    public void The_inverted_boolean_alert_is_seen_by_the_lint_and_fires_when_the_session_is_missing()
    {
        var a = Alert("failed_login_xevent_session");

        a.Query.Should().Contain("THEN 0 ELSE 1 END", "the inverted form is the point of this test");
        AlertEvaluationService.QueryValueShape(a)
            .Should().Be(AlertEvaluationService.AlertValueShape.BooleanFlag);
        AlertEvaluationService.SmallestAlertableValue(a).Should().Be(1.0);
        AlertEvaluationService.IsThresholdBreached(1.0, a.Thresholds.Warning, a.Operator)
            .Should().BeTrue("1 means the XEvent session is missing");
        AlertEvaluationService.IsThresholdBreached(0.0, a.Thresholds.Warning, a.Operator)
            .Should().BeFalse("0 means it is present");
        a.Description.Should().Contain("ABSENT",
            "an operator reading an inverted boolean must be told which way it points");
    }

    /// <summary>
    /// The census, restated for the shape the fix round found: the count of alerts the shape readers
    /// can say something about must include the special path. Pinned as a floor so the coverage
    /// cannot quietly shrink back to "queryMode is exempt".
    /// </summary>
    [Fact]
    public void The_shape_readers_cover_the_special_path_as_well_as_the_query_field()
    {
        var special = Shipped()
            .Where(a => !string.IsNullOrWhiteSpace(a.QueryMode)
                        && !string.Equals(a.QueryMode, "standard", StringComparison.OrdinalIgnoreCase))
            .ToList();

        special.Should().HaveCountGreaterThanOrEqualTo(7,
            "seven shipped alerts are routed to a built-in handler");

        special.Count(a => AlertEvaluationService.SmallestAlertableValue(a) is not null)
            .Should().BeGreaterThanOrEqualTo(6,
                "only windows_power_plan's 0.0/2.0 handler is legitimately unreadable");
    }
}
