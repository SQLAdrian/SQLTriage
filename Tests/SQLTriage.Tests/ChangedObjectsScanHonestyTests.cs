/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

using Outcome = SQLTriage.Data.Services.ChangedObjectsScanOutcome;

// ── The Changed Objects page may only claim what the scan returned ──────────────
//
// WHAT BROKE. ChangedObjectsService.ScanServerAsync never throws — that is deliberate, so one
// unreachable instance cannot sink a multi-server sweep. But it returned a bare Task, so it also
// never REPORTED. The page counted one server per call it made and printed "Scan complete —
// N server(s) inventoried". With every server unreachable, N was still the full count. The number
// was a count of ATTEMPTS wearing the word "inventoried", and an operator reading it would take an
// empty change list as "nothing changed" when the truth was "nothing was read".
//
// THE FIX HAS THREE PARTS AND ALL THREE ARE GUARDED HERE:
//   1. The service returns ChangedObjectsScanOutcome. Reverting the signature to Task goes red at
//      Service_reports_its_outcome_rather_than_returning_a_bare_task.
//   2. The page composes its sentence from that outcome only. ComposeScanStatus is internal and
//      static precisely so the WORDING is assertable without a SQL Server, a renderer or a click.
//      A wording defect that only a human can catch is a wording defect nothing catches.
//   3. One server counts once, however many targets named it. The first cut of this fix counted
//      TARGETS, so two entries for one instance printed a doubled estate AS A COMPLETE SCAN. See
//      the "One server is one server" section below, whose control is the string measured live.
//
// These are unit assertions over composed outcomes: they prove the ARITHMETIC and the SENTENCE.
// The live half (a real unreachable instance producing a NotReached outcome, and the page
// rendering the gap sentence) is recorded in the lane report, not here.
public class ChangedObjectsScanHonestyTests
{
    // The server name is required, never defaulted. Two outcomes sharing a name are two looks at ONE
    // server now, and a helper that quietly names them all "SRV" is how the first cut of this suite
    // asserted "on 2 server(s)" for a single-server estate.
    private static Outcome Reached(string server, int attempted, int inventoried, int changes = 0) =>
        new(server, true, attempted, inventoried, changes);

    // ── The outcome record itself ───────────────────────────────────────────────

    [Fact]
    public void An_unreached_server_carries_no_countable_number()
    {
        var o = Outcome.NotReached("PROD-SQL01");

        o.ServerReached.Should().BeFalse();
        o.DatabasesAttempted.Should().Be(0);
        o.DatabasesInventoried.Should().Be(0);
        o.ChangesRecorded.Should().Be(0);
        o.FullyInventoried.Should().BeFalse();
        o.PartiallyInventoried.Should().BeFalse(
            "there is no partial result to report when nothing was read at all.");
    }

    [Fact]
    public void A_reached_server_with_every_database_captured_is_fully_inventoried()
    {
        var o = Reached("SRV-A", attempted: 4, inventoried: 4);
        o.FullyInventoried.Should().BeTrue();
        o.PartiallyInventoried.Should().BeFalse();
    }

    [Fact]
    public void A_reached_server_missing_some_databases_is_partial_not_full()
    {
        var o = Reached("SRV-A", attempted: 9, inventoried: 3);
        o.FullyInventoried.Should().BeFalse(
            "three of nine is not an inventoried server; the other six are unknown.");
        o.PartiallyInventoried.Should().BeTrue();
    }

    [Fact]
    public void A_reached_server_that_inventoried_nothing_is_neither_full_nor_partial()
    {
        // Enumeration succeeded, every database then failed. The old code counted this as a
        // fully inventoried server.
        var o = Reached("SRV-A", attempted: 5, inventoried: 0);
        o.FullyInventoried.Should().BeFalse();
        o.PartiallyInventoried.Should().BeFalse();
    }

    [Fact]
    public void A_reached_server_with_no_databases_to_list_is_not_inventoried()
    {
        var o = Reached("SRV-A", attempted: 0, inventoried: 0);
        o.FullyInventoried.Should().BeFalse(
            "reaching a server and inventorying a server are different claims.");
    }

    // ── The sentence the operator reads ─────────────────────────────────────────

    [Fact]
    public void Every_server_fully_inventoried_reports_complete_with_the_database_count()
    {
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("SRV-A", 3, 3), Reached("SRV-B", 2, 2) });

        complete.Should().BeTrue();
        message.Should().Be("Scan complete. 5 database(s) inventoried on 2 server(s).");
    }

    [Fact]
    public void Every_server_unreachable_never_claims_a_server_was_inventoried()
    {
        var outcomes = new List<Outcome> { Outcome.NotReached("A"), Outcome.NotReached("B"), Outcome.NotReached("C") };
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(outcomes);

        complete.Should().BeFalse();
        message.Should().NotContain("Scan complete");
        message.Should().Contain("0 of 0 database(s)");
        message.Should().Contain("0 of 3 server(s)");

        // THE REGRESSION ITSELF: the pre-fix page printed "3 server(s) inventoried" here.
        message.Should().NotContain("3 server(s) inventoried",
            "this is the exact false claim the fix exists to remove. Three calls were made and "
            + "nothing was read.");
    }

    [Fact]
    public void A_mixed_result_counts_only_the_servers_that_answered()
    {
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("A", 4, 4), Outcome.NotReached("B"), Reached("C", 6, 2) });

        complete.Should().BeFalse();
        message.Should().Contain("6 of 10 database(s)");
        message.Should().Contain("2 of 3 server(s)");
    }

    [Fact]
    public void A_partial_server_is_not_rounded_up_to_complete()
    {
        var (_, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("SRV-A", 9, 8) });

        complete.Should().BeFalse(
            "eight of nine is a gap. Rounding it to complete is how the old message lied.");
    }

    [Fact]
    public void The_gap_message_states_the_consequence_not_just_the_shortfall()
    {
        var (message, _) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Outcome.NotReached("A") });

        // A number without a consequence still leaves the operator to guess the friendly reading.
        message.Should().Contain("unknown, not unchanged");
        message.Should().Contain("not current for those");

        // And it must NOT overclaim in the other direction: rows an earlier scan recorded for an
        // unreachable server are still shown, so "missing from the list" would be its own lie.
        message.Should().NotContain("missing from the list");
    }

    [Fact]
    public void No_targets_is_never_reported_as_a_complete_scan()
    {
        var (_, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(new List<Outcome>());
        complete.Should().BeFalse("zero servers scanned is not a scan that completed.");
    }

    [Theory]
    // attempted, inventoried per server — every arrangement where something is missing.
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(5, 4)]
    [InlineData(0, 0)]
    public void Any_shortfall_at_all_suppresses_the_word_complete(int attempted, int inventoried)
    {
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("SRV-A", attempted, inventoried) });

        complete.Should().BeFalse();
        message.Should().StartWith("Scan finished with gaps.");
    }

    // ── One server is one server, however many targets named it ─────────────────
    //
    // The same defect class as the header, one level up, and it survived the first cut of this fix:
    // ComposeScanStatus counted TARGETS. Two enabled connection entries naming one instance, or one
    // entry whose ServerNames list repeats it, made the page say a bigger estate than exists — and
    // because every look succeeded, complete=true rendered it as an unqualified success.
    //
    // MEASURED 2026-08-25 against MSI\NEW2022 (7 user databases): two real scans composed to
    // "Scan complete. 14 database(s) inventoried on 2 server(s)." That verbatim string is the
    // control below.

    [Fact]
    public void Two_looks_at_one_server_are_one_server_with_one_database_count()
    {
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("MSI\\NEW2022", 7, 7), Reached("MSI\\NEW2022", 7, 7) });

        complete.Should().BeTrue();
        message.Should().Be("Scan complete. 7 database(s) inventoried on 1 server(s).");

        message.Should().NotContain("14 database(s)",
            "summing two looks at one instance counts every database twice.");
        message.Should().NotContain("2 server(s)",
            "this is the verbatim overclaim measured on 2026-08-25: one instance reported as two.");
    }

    [Fact]
    public void Discrimination_control_two_different_servers_with_the_same_numbers_still_count_twice()
    {
        // The collapse must key on IDENTITY, not on suppressing large numbers. Same arithmetic as
        // the test above, different servers: 14 on 2 is the correct answer here.
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("MSI\\NEW2022", 7, 7), Reached("MSI\\OLD2017", 7, 7) });

        complete.Should().BeTrue();
        message.Should().Be("Scan complete. 14 database(s) inventoried on 2 server(s).");
    }

    [Fact]
    public void A_repeated_name_differing_only_in_case_is_the_same_server()
    {
        // Windows and SQL Server instance names are case-insensitive, so an operator who typed the
        // same instance two ways typed one instance.
        var (message, _) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("MSI\\NEW2022", 7, 7), Reached("msi\\new2022", 7, 7) });

        message.Should().Be("Scan complete. 7 database(s) inventoried on 1 server(s).");
    }

    [Fact]
    public void A_repeated_server_keeps_the_better_look_whole_rather_than_merging_two()
    {
        // Both numbers printed for a server must come from the SAME look at it. Pairing one look's
        // attempted count with another's inventoried count is a ratio nobody measured.
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Reached("A", 9, 3), Reached("A", 9, 7) });

        complete.Should().BeFalse();
        message.Should().Contain("7 of 9 database(s)");
        message.Should().Contain("1 of 1 server(s)");
    }

    [Fact]
    public void A_transient_failure_on_a_repeated_target_does_not_invent_a_missing_server()
    {
        // One entry reached the instance and inventoried all of it; a duplicate entry for the same
        // instance timed out. The estate was fully inventoried, and there is no second server.
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(
            new List<Outcome> { Outcome.NotReached("A"), Reached("A", 7, 7) });

        complete.Should().BeTrue();
        message.Should().Be("Scan complete. 7 database(s) inventoried on 1 server(s).");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Unnamed_targets_never_collapse_into_one(string blank)
    {
        // Two targets with no name cannot be shown to be the same server. Folding them would hide a
        // gap here, and would skip a scan in ScanAsync. Unnamed stays separate, which is the
        // direction that reports MORE unknown, not less.
        var outcomes = new List<Outcome>
        {
            new(blank, false, 0, 0, 0),
            new(blank, false, 0, 0, 0)
        };
        var (message, complete) = SQLTriage.Pages.ChangedObjects.ComposeScanStatus(outcomes);

        complete.Should().BeFalse();
        message.Should().Contain("0 of 2 server(s)");
    }

    // ── Control: the signature the whole fix rests on ───────────────────────────

    [Fact]
    public void Service_reports_its_outcome_rather_than_returning_a_bare_task()
    {
        var method = typeof(ChangedObjectsService).GetMethod(
            nameof(ChangedObjectsService.ScanServerAsync),
            BindingFlags.Public | BindingFlags.Instance);

        method.Should().NotBeNull("ScanServerAsync is the method under guard.");
        method!.ReturnType.Should().Be(typeof(Task<Outcome>),
            "ScanServerAsync swallows every fault by design. A method that never throws and never "
            + "returns tells its caller nothing, and the caller then invents a number. Returning "
            + "Task again re-opens the exact defect fixed on 2026-08-25.");
    }
}
