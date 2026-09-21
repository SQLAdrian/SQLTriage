/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 3 (2026-08-28) - the one number an operator checks first and most often.
//
//   pages-r1-03  IsConnected / LastConnected / SuccessfulServers persist to disk and survive a
//                restart, and the tray row and the /servers hero rendered them as CURRENT. The
//                live store on this box proved a 39-day-old "connected" claim for an instance
//                whose SQL service was stopped, on a green card with no timestamp anywhere.
//   pages-r2-07  The Test button had no per-server try/catch: the first failure unwound the loop
//                and persisted an EMPTY successful-server list, discarding servers that had
//                connected moments earlier. Its "Connected to N of M servers" arm was unreachable
//                dead code, and a connection with no server names reported "Successfully
//                connected" having contacted nothing.
//   pages-r2-08  Refresh All replaced a connection's whole successful-server list once PER BATCH
//                (batch size 10), so a connection straddling a boundary lost its first batch's
//                successes; and it attributed each result by server NAME, so two connections
//                sharing a name absorbed each other's results.
//
// The persistence leg below is exercised, not asserted: it writes a store with a 39-day-old
// connected claim, loads it through the real ServerConnectionManager, and reads what the surfaces
// would render.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class ConnectionStatusHonestyTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public ConnectionStatusHonestyTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "sqltriage-pages-connstatus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ServerConnection Measured(bool connected) =>
        new() { ServerNames = @".\X", IsConnected = connected, StatusMeasuredThisSession = true, LastConnected = DateTime.Now };

    private static ServerConnection Stored(bool connected, DateTime? last) =>
        new() { ServerNames = @".\X", IsConnected = connected, StatusMeasuredThisSession = false, LastConnected = last };

    // ── pages-r1-03: a stored status is not a current one ────────────────────

    [Fact]
    public void A_status_nobody_probed_in_this_session_is_neither_connected_nor_offline()
    {
        ConnectionStatusText.Classify(Stored(connected: true, DateTime.Now.AddDays(-39)))
            .Should().Be(ConnectionStatusKind.NotCheckedThisSession);
        ConnectionStatusText.Classify(Measured(connected: true)).Should().Be(ConnectionStatusKind.ConnectedNow);
        ConnectionStatusText.Classify(Measured(connected: false)).Should().Be(ConnectionStatusKind.DisconnectedNow);
    }

    [Fact]
    public void The_row_line_carries_the_age_of_a_stored_reading()
    {
        var now = new DateTime(2026, 8, 28, 9, 0, 0);
        var line = ConnectionStatusText.DescribeRow(Stored(true, now.AddDays(-39)), now);

        _out.WriteLine(line);
        line.Should().Contain("Not checked this session")
            .And.Contain("Last connected 20 Jul 2026 09:00")
            .And.Contain("(39 days ago)");
        line.Should().NotContain("Connected -");
    }

    [Fact]
    public void A_connection_that_never_connected_says_so_rather_than_dating_nothing()
    {
        ConnectionStatusText.DescribeRow(Stored(false, null), DateTime.Now)
            .Should().Contain("no successful connection has ever been recorded");
    }

    [Theory]
    [InlineData(0, 0, 30, "under a minute")]
    [InlineData(0, 5, 0, "5 minutes")]
    [InlineData(1, 0, 0, "1 hour")]
    [InlineData(50, 0, 0, "2 days")]
    public void Age_is_reported_in_the_largest_unit_that_is_not_a_lie(int h, int m, int s, string expected)
        => ConnectionStatusText.DescribeAge(new TimeSpan(0, h, m, s)).Should().Be(expected);

    [Fact]
    public void The_tray_never_counts_an_unchecked_connection_as_connected()
    {
        var estate = new List<ServerConnection>
        {
            Measured(connected: true),
            Stored(connected: true, DateTime.Now.AddDays(-39)),
            Stored(connected: true, DateTime.Now.AddDays(-39)),
        };

        var (text, kind) = ConnectionStatusText.DescribeTray(estate);
        _out.WriteLine(text);

        // The defect, in one line: this used to read "Servers: 3/3 connected".
        text.Should().Be("Servers: 1/1 connected (2 not checked)");
        kind.Should().Be(ConnectionEstateKind.Mixed);
    }

    [Fact]
    public void The_tray_says_nothing_was_checked_when_nothing_was()
    {
        var (text, kind) = ConnectionStatusText.DescribeTray(new List<ServerConnection>
        {
            Stored(true, DateTime.Now.AddDays(-39)), Stored(true, null),
        });

        text.Should().Be("Servers: 2 configured, none checked yet");
        kind.Should().Be(ConnectionEstateKind.NothingChecked);
    }

    [Fact]
    public void A_fully_measured_estate_keeps_the_original_tray_wording()
    {
        ConnectionStatusText.DescribeTray(new List<ServerConnection> { Measured(true), Measured(true) })
            .Should().Be(("Servers: 2/2 connected", ConnectionEstateKind.AllConnected));

        ConnectionStatusText.DescribeTray(new List<ServerConnection> { Measured(true), Measured(false) })
            .Should().Be(("Servers: 1/2 connected  (1 down)", ConnectionEstateKind.Mixed));

        ConnectionStatusText.DescribeTray(new List<ServerConnection> { Measured(false) })
            .Should().Be(("Servers: 0/1 connected", ConnectionEstateKind.NoneConnected));

        ConnectionStatusText.DescribeTray(new List<ServerConnection>())
            .Should().Be(("No servers configured", ConnectionEstateKind.NoneConfigured));
    }

    [Fact]
    public void The_hero_caption_appears_only_when_a_number_needs_qualifying()
    {
        ConnectionStatusText.DescribeHeroCaption(new List<ServerConnection> { Measured(true), Measured(false) })
            .Should().BeNull();

        ConnectionStatusText.DescribeHeroCaption(new List<ServerConnection> { Measured(true), Stored(true, null) })
            .Should().Contain("1 of 2 connections have not been checked in this session");

        ConnectionStatusText.DescribeHeroCaption(new List<ServerConnection> { Stored(true, null) })
            .Should().Contain("None of the 1 configured connections has been checked");
    }

    [Fact]
    public void The_measured_flag_is_never_persisted_so_a_restart_cannot_inherit_it()
    {
        // Exercised through the real store, not asserted from the attribute: a connection saved
        // while measured must load as unmeasured, or the whole fix evaporates at the next start.
        var store = Path.Combine(_dir, "server-connections.json");
        var saved = new List<ServerConnection>
        {
            new()
            {
                Id = "aaaaaaaa-0000-0000-0000-000000000001",
                ServerNames = @".\OLD2017",
                IsConnected = true,
                StatusMeasuredThisSession = true,
                LastConnected = new DateTime(2026, 7, 16, 8, 16, 0),
                SuccessfulServers = new List<string> { @".\OLD2017" },
            }
        };
        File.WriteAllText(store, JsonSerializer.Serialize(saved));
        _out.WriteLine("STORE: " + File.ReadAllText(store));

        File.ReadAllText(store).Should().NotContain("StatusMeasuredThisSession");

        var manager = new ServerConnectionManager(
            NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: store);
        var loaded = manager.GetConnections().Single();

        loaded.IsConnected.Should().BeTrue("the stored value round-trips - it is the record of an old probe");
        loaded.StatusMeasuredThisSession.Should().BeFalse("nothing probed it in THIS process");
        ConnectionStatusText.Classify(loaded).Should().Be(ConnectionStatusKind.NotCheckedThisSession);

        var line = ConnectionStatusText.DescribeRow(loaded, new DateTime(2026, 8, 24, 8, 16, 0));
        _out.WriteLine("ROW: " + line);
        line.Should().Contain("Not checked this session").And.Contain("39 days ago");

        var (tray, _) = ConnectionStatusText.DescribeTray(manager.GetConnections());
        _out.WriteLine("TRAY: " + tray);
        tray.Should().Be("Servers: 1 configured, none checked yet");
    }

    [Fact]
    public void Recording_a_probe_result_marks_the_status_as_measured()
    {
        var store = Path.Combine(_dir, "measured.json");
        File.WriteAllText(store, JsonSerializer.Serialize(new List<ServerConnection>
        {
            new() { Id = "bbbbbbbb-0000-0000-0000-000000000002", ServerNames = @".\NEW2022" }
        }));

        var manager = new ServerConnectionManager(
            NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: store);

        ConnectionStatusText.Classify(manager.GetConnections().Single())
            .Should().Be(ConnectionStatusKind.NotCheckedThisSession);

        manager.UpdateSuccessfulServers("bbbbbbbb-0000-0000-0000-000000000002", new List<string> { @".\NEW2022" });

        ConnectionStatusText.Classify(manager.GetConnections().Single())
            .Should().Be(ConnectionStatusKind.ConnectedNow);
    }

    // ── pages-r2-07: the Test button's own message ───────────────────────────

    private static ConnectionTestReport Report(int planned, string[] ok, (string, string)[] bad, bool cancelled = false)
        => new(planned, ok, bad.Select(b => new ConnectionTestFailure(b.Item1, b.Item2)).ToList(), cancelled);

    [Fact]
    public void A_connection_with_no_server_names_never_reports_success()
    {
        // The refuter's extra case: 0 == 0 took the equal arm and said "Successfully connected"
        // having opened nothing.
        var r = Report(0, Array.Empty<string>(), Array.Empty<(string, string)>());
        ConnectionTestText.Describe(r).Should().Be("This connection has no server names, so nothing was contacted.");
    }

    [Fact]
    public void A_connection_with_no_server_names_is_not_a_measurement_either()
    {
        // lane9-07. The test above pinned the MESSAGE and stopped there. Complete was still true
        // vacuously (0 attempted of 0 planned), and Complete is the gate the page persists on - so
        // the same click that said "nothing was contacted" wrote UpdateSuccessfulServers(id, []),
        // which sets StatusMeasuredThisSession=true, IsConnected=false and LastConnected=now. The
        // row then read "Did not answer - checked this session." for a connection nothing had
        // contacted, and the tray dot went red. Proved against the shipped assembly.
        var r = Report(0, Array.Empty<string>(), Array.Empty<(string, string)>());

        r.Complete.Should().BeFalse(
            "an empty plan measured nothing, and Complete is what decides whether a result is "
            + "written to the connection store");

        // The consequence chain, both ways round. What the page persists when it is NOT complete
        // is nothing at all, so the connection stays unchecked - and that is what it renders.
        var untouched = new ServerConnection { ServerNames = "   " };
        ConnectionStatusText.Classify(untouched).Should().Be(ConnectionStatusKind.NotCheckedThisSession);
        var row = ConnectionStatusText.DescribeRow(untouched, DateTime.Now);
        _out.WriteLine("row: " + row);
        row.Should().NotContain("Did not answer");
        row.Should().Contain("Not checked this session");

        // And the state the old code wrote, so the difference is measured rather than asserted.
        var stamped = new ServerConnection
        {
            ServerNames = "   ", StatusMeasuredThisSession = true,
            IsConnected = false, LastConnected = DateTime.Now,
        };
        ConnectionStatusText.DescribeRow(stamped, DateTime.Now)
            .Should().Be("Did not answer - checked this session.",
                "this is the sentence the vacuous Complete produced, and it is why it had to go");
        ConnectionStatusText.DescribeTray(new[] { stamped }).Kind
            .Should().Be(ConnectionEstateKind.NoneConnected, "the red tray dot went with it");
    }

    [Fact]
    public void The_silent_sweep_does_not_write_a_result_for_a_connection_it_never_contacted()
    {
        // lane9-07's other half, and the one that fires without a click:
        // TestAllConnectionsSilently runs on EVERY /servers load and called
        // UpdateSuccessfulServers unconditionally after its per-server loop, so a connection with
        // no server names was stamped as checked-and-down automatically, on every page load.
        var markup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Markup", "Servers.razor"));

        System.Text.RegularExpressions.Regex.IsMatch(
                markup,
                @"if \(serverList\.Count > 0\)\s*\r?\n\s*ConnectionManager\.UpdateSuccessfulServers\(conn\.Id, successfulServers\);")
            .Should().BeTrue("the silent sweep persists only when it actually had a server to contact");
    }

    [Fact]
    public void A_partial_success_is_reported_as_a_partial_success()
    {
        var r = Report(2, new[] { @".\NEW2022" }, new[] { (@".\OLD2017", "Login timeout expired.") });

        var msg = ConnectionTestText.Describe(r);
        _out.WriteLine(msg);
        msg.Should().Contain("Connected to 1 of 2 servers")
           .And.Contain(@".\OLD2017")
           .And.Contain("Login timeout expired.");
        r.Complete.Should().BeTrue("every planned server was attempted, so the result may be persisted");
    }

    [Fact]
    public void A_total_failure_still_reports_the_first_reason()
        => ConnectionTestText.Describe(Report(2, Array.Empty<string>(),
                new[] { (@".\A", "network error."), (@".\B", "other.") }))
            .Should().Be("Connection failed: network error.");

    [Fact]
    public void A_complete_success_keeps_its_original_wording()
    {
        ConnectionTestText.Describe(Report(1, new[] { "A" }, Array.Empty<(string, string)>()))
            .Should().Be("Successfully connected");
        ConnectionTestText.Describe(Report(3, new[] { "A", "B", "C" }, Array.Empty<(string, string)>()))
            .Should().Be("Connected to all 3 servers");
    }

    [Fact]
    public void A_cancelled_run_is_incomplete_so_nothing_is_persisted_from_it()
    {
        var r = Report(3, new[] { "A" }, Array.Empty<(string, string)>(), cancelled: true);

        r.Complete.Should().BeFalse();
        ConnectionTestText.Describe(r)
            .Should().Contain("Authentication was cancelled after 1 of 3 server(s) connected")
            .And.Contain("The rest were not contacted.");
    }

    [Fact]
    public void The_mixed_mode_hint_follows_the_failure_that_earns_it()
    {
        ConnectionTestText.DescribeAuthHint(Report(1, Array.Empty<string>(),
            new[] { ("A", "Integrated authentication only is supported.") }))
            .Should().Contain("Windows Authentication only");

        ConnectionTestText.DescribeAuthHint(Report(1, Array.Empty<string>(),
            new[] { ("A", "Login timeout expired.") })).Should().BeNull();
    }

    // ── pages-r2-08: the estate-wide refresh ─────────────────────────────────

    [Fact]
    public void A_connection_straddling_a_batch_boundary_keeps_both_batches_successes()
    {
        // 12 servers on one connection, batch size 10: servers 11 and 12 land in a second batch.
        var plan = Enumerable.Range(1, 12).Select(i => ("conn-1", $"srv{i:00}")).ToList();
        var tally = new ConnectionProbeTally(plan);

        foreach (var (id, srv) in plan.Take(10)) tally.Record(id, srv, true);
        tally.DrainCompletedConnections().Should().BeEmpty("the connection is not finished yet, so nothing may be written");

        foreach (var (id, srv) in plan.Skip(10)) tally.Record(id, srv, true);
        tally.DrainCompletedConnections().Should().BeEquivalentTo(new[] { "conn-1" });

        // The defect: the second batch's write replaced the list with just srv11, srv12.
        tally.SuccessesFor("conn-1").Should().HaveCount(12);
        tally.SucceededServers.Should().Be(12);
        tally.FullyConnectedConnections.Should().Be(1);
        tally.DescribeSweep().Should().Be("Successfully connected to all 12 servers");
    }

    [Fact]
    public void Two_connections_sharing_a_server_name_do_not_absorb_each_others_results()
    {
        // The live-proved variant: connection aaaaaaaa, whose only server is .\NEW2022, persisted
        // SuccessfulServers = ['.\NEW2022','.\NEW2022'] - a second connection's result - and then
        // failed its own count comparison and was excluded from the success gate.
        var tally = new ConnectionProbeTally(new[] { ("aaaaaaaa", @".\NEW2022"), ("bbbbbbbb", @".\NEW2022") });

        tally.Record("aaaaaaaa", @".\NEW2022", true);
        tally.Record("bbbbbbbb", @".\NEW2022", false);

        tally.SuccessesFor("aaaaaaaa").Should().BeEquivalentTo(new[] { @".\NEW2022" });
        tally.SuccessesFor("bbbbbbbb").Should().BeEmpty();
        tally.FullyConnectedConnections.Should().Be(1);
        tally.DescribeSweep().Should().Be("Connected to 1 of 2 servers");
    }

    [Fact]
    public void A_result_for_a_connection_that_was_not_planned_is_refused_rather_than_guessed()
    {
        var tally = new ConnectionProbeTally(new[] { ("conn-1", "srv") });
        var act = () => tally.Record("conn-2", "srv", true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*attributed to whichever connection happened to share the server name*");
    }

    [Fact]
    public void A_connection_with_no_servers_is_not_counted_as_fully_connected()
    {
        var tally = new ConnectionProbeTally(new[] { ("has-servers", "srv1") });
        tally.Record("has-servers", "srv1", true);

        tally.PlannedConnections.Should().Be(1);
        tally.FullyConnectedConnections.Should().Be(1);
        new ConnectionProbeTally(Array.Empty<(string, string)>()).DescribeSweep()
            .Should().Be("No connections are configured, so nothing was contacted.");
    }

    [Fact]
    public void A_finished_connection_is_drained_exactly_once()
    {
        var tally = new ConnectionProbeTally(new[] { ("c", "s") });
        tally.Record("c", "s", false);
        tally.DrainCompletedConnections().Should().BeEquivalentTo(new[] { "c" });
        tally.DrainCompletedConnections().Should().BeEmpty("a second write would replay a stale list");
        tally.DescribeSweep().Should().Be("Failed to connect to any servers");
    }

    // ── Markup lints: the shipped .razor, not a claim about it ───────────────

    private static string ReadMarkup(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", file);
        File.Exists(path).Should().BeTrue($"{file} is copied into the test output by the .csproj");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_servers_hero_counts_measured_statuses_only()
    {
        var markup = ReadMarkup("Servers.razor");

        markup.Should().Contain("ConnectionStatusText.Classify(c) == ConnectionStatusKind.ConnectedNow");
        markup.Should().NotContain("Connections.Count(c => c.IsConnected)",
            "that count included connections nothing had probed in this process");
        markup.Should().Contain("ConnectionStatusText.DescribeHeroCaption");
        markup.Should().Contain("ConnectionStatusText.DescribeRow", "every card states when it was measured");
    }

    [Fact]
    public void Delete_disconnected_acts_only_on_a_status_measured_this_session()
    {
        var markup = ReadMarkup("Servers.razor");

        markup.Should().NotContain("Connections.Where(c => !c.IsConnected)",
            "a destructive action may not run off a stored reading");
        markup.Should().Contain("ConnectionStatusKind.DisconnectedNow");
    }

    [Fact]
    public void Refresh_all_attributes_results_by_connection_and_not_by_server_name()
    {
        var markup = ReadMarkup("Servers.razor");

        markup.Should().Contain("new ConnectionProbeTally");
        markup.Should().Contain("DrainCompletedConnections");
        markup.Should().NotContain("batch.First(b => b.serverName == r.serverName)",
            "that lookup is the name-collision mis-attribution");
    }

    [Fact]
    public void The_test_button_measures_each_server_on_its_own()
    {
        var markup = ReadMarkup("Servers.razor");

        markup.Should().Contain("new ConnectionTestReport");
        markup.Should().Contain("ConnectionTestText.Describe");
        markup.Should().NotContain("ConnectionManager.UpdateSuccessfulServers(conn.Id, new List<string>());",
            "that is the wipe: a failure anywhere persisted a total failure over a partial success");
    }
}
