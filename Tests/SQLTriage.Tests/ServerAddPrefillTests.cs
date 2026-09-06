/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The <c>/servers?add=NAME</c> hand-off: one discovered server, pre-filled into the existing
    /// add-connection dialog, saved by the operator.
    ///
    /// <para>The load-bearing case is <see cref="A_value_carrying_a_newline_is_refused_outright"/>.
    /// The dialog's ServerNames field is newline-separated, so a name containing a newline would
    /// turn one reviewed add into a silent bulk write — the exact thing this route exists to avoid,
    /// and the exact defect being fixed on the Operations Hub in the same wave.</para>
    /// </summary>
    public class ServerAddPrefillTests
    {
        [Fact]
        public void A_discovered_name_survives_the_round_trip()
        {
            var url = "http://localhost:5150/servers?add=" + System.Uri.EscapeDataString("SQLPUB01\\INST2");
            Assert.Equal("SQLPUB01\\INST2", ServerAddPrefill.Parse(url));
        }

        [Fact]
        public void A_value_carrying_a_newline_is_refused_outright()
        {
            var url = "http://localhost:5150/servers?add=" + System.Uri.EscapeDataString("A\nB\nC");
            Assert.Null(ServerAddPrefill.Parse(url));
        }

        [Theory]
        [InlineData("http://localhost:5150/servers")]
        [InlineData("http://localhost:5150/servers?")]
        [InlineData("http://localhost:5150/servers?other=x")]
        [InlineData("http://localhost:5150/servers?add=")]
        [InlineData("http://localhost:5150/servers?add=%20%20")]
        [InlineData(null)]
        public void No_usable_value_means_no_prefill(string? url)
        {
            Assert.Null(ServerAddPrefill.Parse(url));
        }

        [Fact]
        public void Another_parameter_before_it_does_not_hide_it()
        {
            Assert.Equal("SQLSUB03",
                ServerAddPrefill.Parse("http://x/servers?tab=list&add=SQLSUB03"));
        }

        [Fact]
        public void A_fragment_is_not_swallowed_into_the_name()
        {
            Assert.Equal("SQLSUB03", ServerAddPrefill.Parse("http://x/servers?add=SQLSUB03#set-general"));
        }

        [Fact]
        public void An_over_long_value_is_refused_rather_than_truncated()
        {
            var tooLong = new string('a', ServerAddPrefill.MaxLength + 1);
            Assert.Null(ServerAddPrefill.Sanitize(tooLong));
            Assert.NotNull(ServerAddPrefill.Sanitize(new string('a', ServerAddPrefill.MaxLength)));
        }

        // ── refused is not absent ────────────────────────────────────────────────
        //
        // The nullable return collapsed "nothing was asked for" and "what was asked for was
        // refused" into one null. /servers treated both as absent, so a refused value fell
        // through to the ordinary first-add path — where the dialog pre-fills itself from local
        // bulk discovery. An operator arriving from a hand-off met a populated Server Name(s)
        // field, no refusal anywhere on the page, and no way to tell that the names in front of
        // them were not the name they had asked for.

        [Fact]
        public void An_accepted_value_reports_itself_as_accepted()
        {
            var r = ServerAddPrefill.Read("http://x/servers?add=SQLSUB03");

            Assert.Equal(AddPrefillOutcome.Accepted, r.Outcome);
            Assert.Equal("SQLSUB03", r.Name);
            Assert.Equal("", r.Reason);
        }

        [Theory]
        [InlineData("http://x/servers")]
        [InlineData("http://x/servers?other=x")]
        [InlineData("http://x/servers?add=")]
        [InlineData("http://x/servers?add=%20%20")]
        [InlineData(null)]
        public void Nothing_asked_for_is_NotRequested_not_Refused(string? url)
        {
            var r = ServerAddPrefill.Read(url);

            Assert.Equal(AddPrefillOutcome.NotRequested, r.Outcome);
            Assert.Equal("", r.Reason);
        }

        [Fact]
        public void A_newline_value_is_Refused_and_the_reason_names_the_line_break()
        {
            var url = "http://x/servers?add=" + System.Uri.EscapeDataString("A\nB\nC");
            var r = ServerAddPrefill.Read(url);

            Assert.Equal(AddPrefillOutcome.Refused, r.Outcome);
            Assert.Equal("", r.Name);
            Assert.Contains("line break", r.Reason);
        }

        [Fact]
        public void An_over_long_value_is_Refused_and_the_reason_gives_both_numbers()
        {
            var url = "http://x/servers?add=" + new string('a', ServerAddPrefill.MaxLength + 1);
            var r = ServerAddPrefill.Read(url);

            Assert.Equal(AddPrefillOutcome.Refused, r.Outcome);
            Assert.Contains((ServerAddPrefill.MaxLength + 1).ToString(), r.Reason);
            Assert.Contains(ServerAddPrefill.MaxLength.ToString(), r.Reason);
        }

        [Fact]
        public void A_control_character_value_is_Refused_and_says_which_fault_it_was()
        {
            var url = "http://x/servers?add=" + System.Uri.EscapeDataString("SQL" + (char)7 + "PUB");
            var r = ServerAddPrefill.Read(url);

            Assert.Equal(AddPrefillOutcome.Refused, r.Outcome);
            Assert.Contains("control character", r.Reason);
        }

        [Fact]
        public void Every_refusal_carries_a_reason_that_can_be_shown_to_an_operator()
        {
            foreach (var value in new[] { "A\nB", new string('a', ServerAddPrefill.MaxLength + 1), "SQL" + (char)7 + "PUB" })
            {
                var r = ServerAddPrefill.Read("http://x/servers?add=" + System.Uri.EscapeDataString(value));
                Assert.Equal(AddPrefillOutcome.Refused, r.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            }
        }

        [Fact]
        public void Parse_still_answers_null_for_both_of_the_non_accepted_outcomes()
        {
            // The old surface is kept for callers that genuinely cannot act on the difference.
            Assert.Null(ServerAddPrefill.Parse("http://x/servers"));
            Assert.Null(ServerAddPrefill.Parse("http://x/servers?add=" + System.Uri.EscapeDataString("A\nB")));
        }

        /// <summary>
        /// A lint over the page that consumes this. The behaviour itself is not unit-testable
        /// without rendering Servers.razor, so what is pinned here is that the page reads the
        /// three-outcome API, opens the dialog for a refusal, blanks the field rather than
        /// leaving the bulk-discovery pre-fill in it, and suppresses that discovery for BOTH
        /// arrival cases — populated catalogue and empty catalogue take the same branch.
        /// </summary>
        [Fact]
        public void The_servers_page_discloses_a_refusal_instead_of_falling_through()
        {
            var markup = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Pages", "Servers.razor"));

            Assert.Contains("ServerAddPrefill.Read(Navigation.Uri)", markup);
            Assert.Contains("AddPrefillOutcome.Refused", markup);
            Assert.Contains("_addPrefillRefusal = request.Reason;", markup);
            Assert.Contains("The requested name was refused:", markup);

            // The refusal branch opens the dialog EMPTY: no local bulk discovery, and the field
            // is cleared. Both are asserted — either alone would leave a populated box.
            Assert.Contains("await ShowAddDialogCore(discoverLocalInstances: false);", markup);
            Assert.Contains("EditingConnection.ServerNames = string.Empty;", markup);

            // The refusal branch returns, so it can never fall into the first-add path below it.
            var refusalAt = markup.IndexOf("_addPrefillRefusal = request.Reason;", System.StringComparison.Ordinal);
            var firstAddAt = markup.IndexOf("// Auto-open dialog if no servers exist", System.StringComparison.Ordinal);
            Assert.True(refusalAt >= 0 && firstAddAt > refusalAt);
            Assert.Contains("return;", markup.Substring(refusalAt, firstAddAt - refusalAt));
        }

        [Fact]
        public void A_control_character_is_refused()
        {
            Assert.Null(ServerAddPrefill.Sanitize("SQL\u0007PUB"));
        }
    }
}
