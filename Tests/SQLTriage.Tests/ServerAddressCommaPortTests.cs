/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

// ── FIX 1, persona board 2026-07-21 ───────────────────────────────────────────
// "host,port" is standard SQL Server addressing. Six sites reached for .Split(',')
// on it, producing two distinct client-visible failures:
//
//   • ONE server rendered as SEVERAL. The estate held a single connection
//     ".\OLD2017localhost,56510"; /dba drew a card ".\OLD2017localhost" AND a card
//     "56510" — a PORT presented as a server — each scoring Resource 0/100, and
//     /cio then averaged "across 4 assessed servers" to 38/Critical while the one
//     real server read 76/Healthy.
//   • The PORT silently DROPPED by .Split(',')[0] / .First(), so a server addressed
//     by port was contacted on the default port: a wrong-server or failed-connection
//     bug, not a cosmetic one.
//
// These tests pin the primitive AND pin that a comma-bearing name yields exactly ONE
// server through the model that feeds the estate.
public class ServerAddressCommaPortTests
{
    // ── The primitive ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"SQL01\INST,56510")]
    [InlineData(@".\OLD2017localhost,56510")]
    [InlineData("sql-prod-01.contoso.com,1433")]
    [InlineData("10.0.0.4,49172")]
    public void A_single_ported_address_survives_splitting_intact(string address)
    {
        var result = ServerAddress.SplitList(address);

        result.Should().ContainSingle(
            "'host,port' is ONE server — the comma is the port separator, not a list separator");
        result[0].Should().Be(address,
            "the address must arrive byte-identical: dropping the port aims the connection at the default port");
    }

    [Fact]
    public void A_newline_joined_list_splits_while_every_port_stays_attached()
    {
        // The exact format Pages/Servers.razor persists (it joins with '\n').
        var raw = "SQL01\r\n" + @"SQL02\INST,56510" + "\nSQL03.contoso.com,1433";

        ServerAddress.SplitList(raw).Should().Equal(
            new[] { "SQL01", @"SQL02\INST,56510", "SQL03.contoso.com,1433" });
    }

    [Fact]
    public void A_legacy_comma_separated_list_of_hostnames_still_splits()
    {
        // Backwards compatibility: a comma followed by something that is NOT a port is
        // still a list separator, so free-text "A,B" is not welded into one bogus server.
        ServerAddress.SplitList("SQL01,SQL02").Should().Equal(new[] { "SQL01", "SQL02" });

        // ...and the mixed case resolves each comma on its own merits.
        ServerAddress.SplitList("SQL01,56510,SQL02")
            .Should().Equal(new[] { "SQL01,56510", "SQL02" });
    }

    [Theory]
    // Ported DEFAULT instance: no backslash, so the comma is the only thing to strip. Mutation
    // testing caught the original single-case version passing while HostOnly's comma-strip was
    // disabled — the instance strip alone satisfied the @"SQL01\INST,56510" case, so the assertion
    // proved nothing about port handling. Each separator now has a case that isolates it.
    [InlineData("SQL01,56510", "SQL01")]
    [InlineData("SQL01:56510", "SQL01")]
    [InlineData(@"SQL01\INST", "SQL01")]
    [InlineData(@"SQL01\INST,56510", "SQL01")]
    [InlineData("SQL01", "SQL01")]
    public void HostOnly_strips_the_instance_and_the_port_independently(string address, string expectedHost)
    {
        ServerAddress.HostOnly(address).Should().Be(expectedHost,
            "HostOnly answers an OS-level question — a machine has no instance and no port");
    }

    [Fact]
    public void First_keeps_what_HostOnly_strips()
    {
        const string address = @"SQL01\INST,56510";

        ServerAddress.First(address).Should().Be(address,
            "First answers a CONNECT question and must never discard the port");
        ServerAddress.HostOnly(address).Should().NotBe(address,
            "the two helpers must genuinely differ, or one of them is answering the wrong question");
    }

    [Fact]
    public void An_instance_key_round_trips_a_ported_address()
    {
        // Cache keys used to be comma-joined, which made {"A", "B,1433"} and
        // {"A", "B", "1433"} the same string — CapacityCollector could not tell them apart
        // and dropped the ported server's metric.
        var key = ServerAddress.JoinKey(new[] { "A", "B,1433" });

        ServerAddress.SplitKey(key).Should().Equal(new[] { "A", "B,1433" });
        key.Should().NotContain(",1433" + ServerAddress.KeySeparator,
            "sanity: the port must not have become its own key element");
    }

    [Fact]
    public void BuildInstanceKey_produces_a_key_that_matches_a_ported_server()
    {
        var filter = new DashboardFilter { Instances = new[] { "B,1433", "A" } };

        var key = CachingQueryExecutor.BuildInstanceKey(filter);

        ServerAddress.SplitKey(key).Should().BeEquivalentTo(new[] { "A", "B,1433" },
            "the ported instance must come back out of the key as one address");
    }

    // A_ported_instance_inside_a_multi_instance_key_is_still_matched moved 2026-08-04 to
    // Gated/PortalCapacityInstanceKeyTests.cs. It is the only test here that reaches into the
    // Portal surface (Data\Services\Portal\**, Compile-Removed from the community build), and
    // it was breaking the community test build. The other 12 tests are profile-independent.

    // ── The model that feeds the estate ──────────────────────────────────────

    [Fact]
    public void A_comma_bearing_server_name_produces_exactly_ONE_server()
    {
        // The defect verbatim: this stored value produced TWO entries, the second of which
        // was the port "56510", and it then drew its own health card and entered the /cio
        // equal-weight estate mean.
        var conn = new ServerConnection { ServerNames = @".\OLD2017localhost,56510" };

        var servers = conn.GetServerList();

        servers.Should().ContainSingle("a host with a port is one server, not two");
        servers[0].Should().Be(@".\OLD2017localhost,56510");
        conn.GetServerCount().Should().Be(1);
        servers.Should().NotContain("56510", "a port number must never be presented as a server");
    }

    [Fact]
    public void The_name_whitelist_preserves_the_port_rather_than_welding_it_to_the_host()
    {
        // ValidateServerName drops characters outside its whitelist. Before 2026-07-21 the
        // comma was not on it — harmless only because GetServerList split on ',' first.
        // With the address arriving whole, an un-whitelisted comma would silently produce
        // "SQL0156510": a worse failure than the split it replaced.
        var conn = new ServerConnection { ServerNames = "SQL01,56510" };

        conn.GetServerList().Should().Equal(new[] { "SQL01,56510" });
    }

    [Fact]
    public void A_multi_server_connection_counts_hosts_not_ports()
    {
        var conn = new ServerConnection
        {
            ServerNames = "SQL01\n" + @"SQL02\INST,56510" + "\nSQL03,1433"
        };

        conn.GetServerList().Should().Equal(
            new[] { "SQL01", @"SQL02\INST,56510", "SQL03,1433" });
        conn.GetServerCount().Should().Be(3,
            "three configured servers — not five, which is what counting ports gave");
    }

    [Fact]
    public void A_ported_address_reaches_the_connection_string_as_the_data_source()
    {
        // The end of the chain the port has to survive: what SqlClient is actually handed.
        var conn = new ServerConnection { ServerNames = @"SQL01\INST,56510" };
        var address = ServerAddress.First(conn.ServerNames)!;

        var connectionString = conn.GetConnectionString(address, "master");

        connectionString.Should().Contain("56510",
            "the port must reach the connection string, or the client connects to a different server");
    }

    // ── Every site the sweep touched, pinned against the shipped source ──────

    [Theory]
    [InlineData(@"Pages\ServerComparison.razor")]
    [InlineData(@"Pages\Governance.razor")]
    [InlineData(@"Pages\XEvents.razor")]
    [InlineData(@"Pages\AlertingConfig.razor")]
    [InlineData(@"Components\Shared\QueryPlanModal.razor")]
    [InlineData(@"Data\Models\ServerConnection.cs")]
    public void No_touched_site_splits_a_server_name_on_a_comma_again(string relativePath)
    {
        // Comments are stripped first: several of these files now CARRY the offending
        // expression in a comment explaining what it used to be and why it went. Scanning
        // raw text would fail on the documentation of the fix — and, worse, would tempt a
        // future maintainer to delete the explanation to make a test pass.
        var source = StripLineComments(ReadRepoFile(relativePath));

        // The shape that caused the defect. Split(',') on genuinely comma-delimited data
        // (tags, alert recipients) is unaffected — this targets ServerNames only.
        source.Should().NotContain("ServerNames.Split(','",
            $"{relativePath} must route server-name splitting through ServerAddress");
        source.Should().NotContain("ServerNames\r\n                .Split(','",
            $"{relativePath} must route server-name splitting through ServerAddress");
        source.Should().Contain("ServerAddress",
            $"{relativePath} must actually use the shared primitive — otherwise this test "
          + "passes merely because the old call was deleted, proving nothing about the new one");
    }

    /// <summary>Drops `//` line comments and `@* ... *@` Razor comments.</summary>
    private static string StripLineComments(string source)
    {
        var withoutRazorComments = System.Text.RegularExpressions.Regex.Replace(
            source, @"@\*.*?\*@", "", System.Text.RegularExpressions.RegexOptions.Singleline);

        var kept = withoutRazorComments
            .Split('\n')
            .Select(line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? line.Substring(0, idx) : line;
            });

        return string.Join("\n", kept);
    }

    private static string ReadRepoFile(string relativePath)
    {
        // Tests run from bin/<cfg>/<tfm>; the repo root is four levels up from there.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the repo root (the folder holding SQLTriage.sln) must be locatable, "
                             + "or every assertion below would vacuously pass");

        var path = Path.Combine(dir!.FullName, relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} must exist to be asserted against");
        return File.ReadAllText(path);
    }
}
