/* In the name of God, the Merciful, the Compassionate */

using System.Linq;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// <see cref="ServerAddress.TryValidate"/> — the judgement half of address handling.
///
/// ServerConnection.ValidateServerName SANITISES: it drops what it does not recognise and hands
/// back a shorter name, which is right for a UI paste. A machine-readable argument needs the
/// opposite: a malformed address must be refused and named, because a silently shortened name is
/// a different server and a silently added list entry gets priced.
/// </summary>
public class ServerAddressValidationTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("localhost")]
    [InlineData(".\\old2017")]
    [InlineData("SQL01\\INST,56510")]
    [InlineData("tcp:localhost,50644")]
    [InlineData("10.1.2.3")]
    [InlineData("sql-prod-01.corp.example.com")]
    [InlineData("SQL_01")]
    public void Real_addresses_validate(string address)
    {
        Assert.True(ServerAddress.TryValidate(address, out var reason), reason);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_is_refused_with_a_reason(string? address)
    {
        Assert.False(ServerAddress.TryValidate(address, out var reason));
        Assert.Equal("the address is empty", reason);
    }

    [Fact]
    public void Over_length_is_refused_and_the_message_carries_both_numbers()
    {
        var address = new string('a', 101);

        Assert.False(ServerAddress.TryValidate(address, out var reason));
        Assert.Contains("101", reason!);
        Assert.Contains("100", reason!);
    }

    [Fact]
    public void Exactly_at_the_limit_is_accepted()
    {
        Assert.True(ServerAddress.TryValidate(new string('a', 100), out _));
    }

    [Theory]
    [InlineData("SQL01<script>", "'<'")]
    [InlineData("SQL 01", "U+0020")]
    [InlineData("SQL01;DROP", "';'")]
    [InlineData("SQL01|SQL02", "'|'")]
    [InlineData("SQL01'", "'''")]
    public void An_illegal_character_is_refused_and_named(string address, string expected)
    {
        Assert.False(ServerAddress.TryValidate(address, out var reason));
        Assert.Contains(expected, reason!);
    }

    [Fact]
    public void A_control_character_is_rendered_visibly_not_swallowed_into_the_message()
    {
        Assert.False(ServerAddress.TryValidate("SQL01\tINST", out var reason));
        Assert.Contains("U+0009", reason!);
    }

    [Fact]
    public void Several_illegal_characters_are_all_named_once_each()
    {
        Assert.False(ServerAddress.TryValidate("SQL01<>>", out var reason));
        Assert.Contains("these characters", reason!);
        Assert.Contains("'<'", reason!);
        Assert.Contains("'>'", reason!);
        // Distinct, not one entry per occurrence.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(reason!, "'>'").Count);
    }

    // ── SHAPE, not just characters (2026-08-06) ─────────────────────────────────────────────────
    //
    // Item 2c was closed for the character set and left open for the shape it named. "host,<not a
    // port>" passed TryValidate as two well-formed addresses, because the splitter had already
    // resolved the comma and the judge never saw it. Every case below is the same defect: a port
    // the user got wrong, arriving at the allocation as a second server.

    [Theory]
    [InlineData(@"SQL01\INST,5651Z")]    // a stray letter in the port
    [InlineData(@"SQL01\INST,566510")]   // six digits: not a port at all
    [InlineData(@"SQL01\INST,0x1F")]     // a port written in hex
    [InlineData(@"SQL01\INST,port")]     // the word, not a number
    public void A_comma_tail_that_is_not_a_port_is_refused_by_the_splitter(string raw)
    {
        Assert.False(ServerAddress.TrySplitList(raw, out var addresses, out var reason));
        Assert.NotNull(reason);

        // Named, so the operator can see which segment was read and how.
        var tail = raw[(raw.IndexOf(',') + 1)..];
        Assert.Contains(tail, reason!, System.StringComparison.Ordinal);

        // And nothing survives as a second server, which is the whole point: the count is what
        // the allocation reads.
        Assert.Empty(addresses.Where(a => a == tail));
    }

    [Theory]
    [InlineData(@"SQL01\INST,5651Z")]
    [InlineData(@"SQL01\INST,566510")]
    [InlineData(@"SQL01\INST,0x1F")]
    [InlineData(@"SQL01\INST,port")]
    public void The_same_four_are_refused_when_judged_as_one_address(string address)
    {
        // The other door. A caller that skips the splitter and validates a raw value must not get
        // a pass on the same shape.
        Assert.False(ServerAddress.TryValidate(address, out var reason));
        Assert.Contains("port", reason!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"SQL01\INST,56510", 1)]
    [InlineData("tcp:localhost,50644", 1)]
    [InlineData("10.1.2.3,1433", 1)]
    [InlineData("SQL01,SQL02", 2)]              // legacy free-text list, both plainly servers
    [InlineData("SQL01,56510,SQL02", 2)]        // one ported server and one plain one
    [InlineData("10.1.2.3,10.1.2.4", 2)]        // numeric hosts: dots, so not port-shaped
    [InlineData("SQL01;SQL02", 2)]              // ';' has never been legal inside a name
    public void What_the_splitter_accepts_it_counts_correctly(string raw, int expected)
    {
        Assert.True(ServerAddress.TrySplitList(raw, out var addresses, out var reason), reason);
        Assert.Equal(expected, addresses.Count);
    }

    [Fact]
    public void An_ambiguous_bare_word_after_a_comma_is_refused_rather_than_resolved()
    {
        // "localhost" IS a legal hostname, and after a comma it is also exactly what a mistyped
        // port looks like. Refusing it is the cost of not guessing, and the message has to say how
        // to write the list unambiguously rather than leave the operator stuck.
        Assert.False(ServerAddress.TrySplitList("SQL01,localhost", out _, out var reason));
        Assert.Contains("newline", reason!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("';'", reason!, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"SQL01\INST,56510")]
    [InlineData("SQL01,SQL02")]
    [InlineData("SQL01,56510,SQL02")]
    [InlineData("SQL01\nSQL02\\INST,56510")]
    [InlineData("10.1.2.3,1433")]
    public void TrySplitListAgreesWithSplitListOnEveryShapeItAccepts(string raw)
    {
        // SplitList keeps its lenient answer for stored desktop free text, so the two methods are
        // two readings of the same input. Where the strict one accepts, they must not differ — a
        // second splitting rule that quietly disagrees with the first is how the port became a
        // server card in the first place.
        Assert.True(ServerAddress.TrySplitList(raw, out var judged, out _));
        Assert.Equal(ServerAddress.SplitList(raw), judged);
    }

    [Fact]
    public void SplitList_keeps_its_historical_answer_for_what_the_judge_refuses()
    {
        // Not an endorsement of the reading — a statement that this method did not change under
        // the desktop's stored values. The CLI is the surface that now refuses it.
        Assert.Equal(new[] { "SQL01", "port" }, ServerAddress.SplitList("SQL01,port"));
    }

    [Fact]
    public void The_reason_never_guesses_what_the_caller_meant()
    {
        Assert.False(ServerAddress.TryValidate("SQL01 with spaces", out var reason));
        foreach (var guess in new[] { "did you mean", "perhaps", "probably", "assuming" })
            Assert.DoesNotContain(guess, reason!, System.StringComparison.OrdinalIgnoreCase);
    }
}
