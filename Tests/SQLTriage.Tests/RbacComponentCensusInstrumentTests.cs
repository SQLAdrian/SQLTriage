/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// THE NEGATIVE CONTROL for the 2026-08-06 component-census upgrade.
    ///
    /// <para>An instrument that has never been shown to fail is not known to work. Every claim in
    /// this file is about the SCANNER, driven over synthetic component text, so it holds whatever
    /// the shipped components happen to contain today. Each test states the old instrument's
    /// answer and the new one's for the same input; the pair is the evidence, not either half.</para>
    ///
    /// <para>The shapes below are not invented. Each is a defect this lane actually shipped:
    /// <c>UserSettings.SetAnonymiseServerNames(false)</c> in MainLayout (a bare <c>Set</c>, live
    /// from a LAN denial page, census green at 42 passed); <c>ToastService.Enabled = …</c> in
    /// NavMenu (a property write no call-shaped scan could match); and
    /// <c>ConnectionManager.CurrentServer.Database = dbName</c> in DynamicDashboard (a write
    /// THROUGH a returned object, which even the property-write scan missed until this round).</para>
    ///
    /// <para><b>WHAT THIS INSTRUMENT CANNOT SEE, 2026-08-06.</b> This file used to say the scanner
    /// "cannot be defeated by choosing a word, only by not injecting the service at all". The first
    /// half is true; the second is an over-claim, and an instrument that overstates its reach is
    /// worse than one that is merely narrow, because the reach is what the census's green is read
    /// as meaning. Three shapes, each with a test below rather than a sentence:</para>
    /// <list type="bullet">
    /// <item><b>A local copy of the reference.</b> <c>var svc = UserSettings; svc.Nuke();</c> —
    ///   the alias map is built from <c>@inject</c> / <c>[Inject]</c>, so <c>svc</c> resolves to
    ///   nothing. One line defeats the scanner without declining to inject anything.</item>
    /// <item><b>A two-hop chained write.</b> <c>x.A.B.C = v</c>. The scanner learned ONE hop this
    ///   round; a second is a different question (what the intermediate object is) and is not
    ///   answered by a deeper regex.</item>
    /// <item><b><c>@bind</c> to an injected service property.</b> It is a WRITE on every input
    ///   event, and it lands on the register as a bare member — the same key a read produces — so
    ///   the register can be true and the reviewer's reason wrong.</item>
    /// </list>
    /// <para>The METHOD GROUP was a fourth, and is the one this round closed:
    /// <c>@onclick="Svc.Member"</c> needs no parentheses, so every earlier pattern was structurally
    /// unable to match it. <see cref="ShellSurfaceRegistry.EdgesIn"/> records it now, and the
    /// shipped tree had one — <c>WelcomeTourOverlay</c>'s
    /// <c>@onclick="Tour.ToggleAutoAdvance"</c>.</para>
    /// </summary>
    public class RbacComponentCensusInstrumentTests
    {
        private const string Header =
            "@inject SQLTriage.Data.UserSettingsService UserSettings\n" +
            "@inject SQLTriage.Data.ToastService Toast\n" +
            "@inject ServerConnectionManager ConnectionManager\n" +
            "<button @onclick=\"Go\">Go</button>\n" +
            "@code {\n";

        private static string Component(string body) => Header + "    " + body + "\n}\n";

        // ── The three shapes, each proved invisible to the old instrument ────────────────────────

        [Fact]
        public void A_bare_Set_call_was_invisible_to_the_verb_lexicon_and_is_an_edge_now()
        {
            var text = Component("private void Go() { UserSettings.SetAnonymiseServerNames(false); }");

            // 2026-08-10: the first assertion here was "the verb lexicon cannot see this", and round
            // 8 of the page census added "Set" so that a bare Set on a ROUTABLE page - which
            // RbacComponentGateCensusTests skips by design - stops falling between the two censuses.
            // The reasoning is written out at RbacShellSurfaceCensusTests.TheCensusSeesTheGatesOwnProbe.
            // What this test exists to hold is the DIVISION OF LABOUR, and that survives unchanged:
            // ActingCallsIn drops every lexicon hit on an injected alias, so for a component the
            // edge scanner still decides membership and the call lands under the declared TYPE, once.
            var acting = RbacComponentGateCensusTests.ActingCallsIn(text);

            Assert.Contains("UserSettingsService.SetAnonymiseServerNames", acting);
            Assert.DoesNotContain(acting, c => c.StartsWith("UserSettings.", StringComparison.Ordinal));
        }

        [Fact]
        public void A_property_write_was_invisible_to_the_verb_lexicon_and_is_an_edge_now()
        {
            var text = Component("private void Go() { Toast.Enabled = false; }");

            Assert.DoesNotContain(
                RbacPageGateCensusTests.MutatingCalls(text),
                c => c.Contains("Enabled", StringComparison.Ordinal));

            Assert.Contains("ToastService.Enabled=", RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        [Fact]
        public void A_write_through_a_returned_object_is_an_edge_now()
        {
            // The Query-Store selector's exact shape. The receiver of the assignment is
            // CurrentServer, not the injected alias, so the single-hop write scan saw `x.A`
            // followed by `.` rather than `=` and recorded nothing.
            var text = Component("private void Go() { ConnectionManager.CurrentServer.Database = \"master\"; }");

            Assert.DoesNotContain(
                RbacPageGateCensusTests.MutatingCalls(text),
                c => c.Contains("Database", StringComparison.Ordinal));

            Assert.Contains("ServerConnectionManager.CurrentServer.Database=",
                RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        [Theory]
        [InlineData("UserSettings.ToggleDarkMode();", "UserSettingsService.ToggleDarkMode")]
        [InlineData("UserSettings.UpdateEverything();", "UserSettingsService.UpdateEverything")]
        [InlineData("UserSettings.Nuke();", "UserSettingsService.Nuke")]
        [InlineData("UserSettings.QuietlyDoTheThing();", "UserSettingsService.QuietlyDoTheThing")]
        public void The_scanner_asks_nothing_about_the_members_name(string statement, string expectedEdge)
        {
            // Toggle and Update are absent from the 29-verb lexicon; the last two are absent from
            // any lexicon anybody will ever write. That is the argument for the inversion: no
            // choice of WORD defeats the new instrument.
            //
            // ⚠ This test used to end "…only by not injecting the service at all", which was an
            // OVER-CLAIM and is corrected below. What the scanner cannot be defeated by is the
            // member's NAME. It can be defeated by the SHAPE of the reference, and the section
            // "What the instrument still cannot see" says exactly which shapes, each with a test.
            var text = Component("private void Go() { " + statement + " }");

            Assert.Contains(expectedEdge, RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        // ── The method group: the gate's probe, and the shape that answered it ───────────────────

        [Fact]
        public void A_method_group_handed_to_an_event_was_invisible_and_is_an_edge_now()
        {
            // THE GATE'S PROBE, 2026-08-06. Every pattern the scanner had needed a '(', an '=' or
            // a '+=' immediately after the member; a method group has none of them, so a service
            // method wired straight to a DOM event was invisible to the whole instrument while the
            // click still called it. The shipped tree had one:
            // WelcomeTourOverlay's @onclick="Tour.ToggleAutoAdvance".
            var text =
                "@inject SQLTriage.Data.UserSettingsService UserSettings\n"
                + "<button @onclick=\"UserSettings.QuietlyDoTheThing\">Go</button>\n"
                + "@code {\n}\n";

            Assert.Empty(RbacPageGateCensusTests.MutatingCalls(text));

            Assert.Contains("UserSettingsService.QuietlyDoTheThing",
                RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        [Fact]
        public void A_delegate_stored_in_a_variable_is_an_edge_now()
        {
            // The same shape one step further from the call: the reference is taken here and
            // invoked through a name the scanner has no reason to look at.
            var text = Component("private void Go() { Action f = UserSettings.Nuke; f(); }");

            Assert.Contains("UserSettingsService.Nuke",
                RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        // ── What the instrument still cannot see ────────────────────────────────────────────────
        //
        // Written as PASSING tests that assert the blindness, not as prose. A limitation described
        // in a comment is a limitation nobody re-measures; one asserted here fails the day somebody
        // closes it, and the fix is to move the test rather than to remember.

        [Fact]
        public void A_LOCAL_COPY_of_an_injected_service_defeats_the_scanner_entirely()
        {
            // ⚠ THE CORRECTION to the over-claim above. Defeating this scanner does NOT require
            // declining to inject the service: one local assignment does it. The alias map is built
            // from @inject and [Inject] declarations, so `svc` resolves to nothing and every call
            // through it is invisible — and the verb lexicon cannot help either unless the member
            // happens to be named after one of its 29 verbs.
            var text = Component("private void Go() { var svc = UserSettings; svc.Nuke(); }");

            Assert.Empty(RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        [Fact]
        public void A_TWO_HOP_chained_write_is_invisible()
        {
            // The scanner learned `x.A.B = v` this round because of DynamicDashboard's
            // ConnectionManager.CurrentServer.Database=. It learned ONE hop. A second hop is a
            // different question — what the intermediate object IS — and guessing at it with a
            // deeper regex would be the same match-known mistake in a new costume.
            var text = Component(
                "private void Go() { ConnectionManager.CurrentServer.Options.Database = \"master\"; }");

            Assert.DoesNotContain("ServerConnectionManager.CurrentServer.Options.Database=",
                RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        [Fact]
        public void A_bind_to_an_injected_service_property_is_recorded_as_a_READ_not_a_write()
        {
            // @bind is a two-way binding: it WRITES the property on every input event. The scanner
            // records the reference (the value-position shape added this round catches the quoted
            // attribute), but it records it as a plain member — the same key a read produces —
            // because nothing in the text says "=". A reviewer writing the register entry from
            // this key alone would call an install-wide write a read, which is precisely the false
            // justification this census exists to make impossible.
            //
            // Not fixed here: @bind:get/@bind:set, @bind-Value and the plain @bind all bind
            // differently, and the shape that reads them correctly is a Razor question rather than
            // a regex one. Recorded so the next reviewer of a bare member edge on a SETTABLE
            // property looks at the markup.
            var text =
                "@inject SQLTriage.Data.ToastService Toast\n"
                + "<input @bind=\"Toast.Enabled\" />\n"
                + "@code {\n}\n";

            var edges = RbacComponentGateCensusTests.ActingCallsIn(text);

            Assert.Contains("ToastService.Enabled", edges);
            Assert.DoesNotContain("ToastService.Enabled=", edges);
        }

        // ── What the upgrade must NOT have lost ─────────────────────────────────────────────────

        [Fact]
        public void A_local_SqlCommand_write_is_still_caught_by_the_retained_lexicon()
        {
            // The edge scanner cannot see this: `cmd` resolves to no injected type. Dropping the
            // lexicon would have LOST the call that gated DynamicDashboard and QueryPlanModal in
            // the first place. An upgrade that removes a detection is not an upgrade.
            var text = Component(
                "private async Task Go() { using var cmd = conn.CreateCommand(); await cmd.ExecuteNonQueryAsync(); }");

            Assert.Contains("cmd.ExecuteNonQueryAsync", RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        [Fact]
        public void One_call_arrives_once_even_though_two_instruments_can_see_it()
        {
            // ConnectionManager.AddConnection matches the lexicon's Add verb AND resolves to an
            // injected type. Registered twice — once as the alias, once as the declared type — it
            // would be one call with two entries that can disagree.
            var text = Component("private void Go() { ConnectionManager.AddConnection(c); }");
            var calls = RbacComponentGateCensusTests.ActingCallsIn(text);

            Assert.Contains("ServerConnectionManager.AddConnection", calls);
            Assert.DoesNotContain("ConnectionManager.AddConnection", calls);
            Assert.Single(calls.Where(c => c.EndsWith(".AddConnection", StringComparison.Ordinal)));
        }

        [Fact]
        public void A_comparison_is_not_a_write()
        {
            var text = Component("private bool Go() => Toast.Enabled == true;");

            Assert.DoesNotContain("ToastService.Enabled=", RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        [Fact]
        public void A_call_named_only_in_a_comment_is_not_an_edge()
        {
            var text = Component("// UserSettings.SetAnonymiseServerNames(false);\n    private void Go() { }");

            Assert.DoesNotContain("UserSettingsService.SetAnonymiseServerNames",
                RbacComponentGateCensusTests.ActingCallsIn(text));
        }

        // ── And the register really is consulted ────────────────────────────────────────────────

        [Fact]
        public void An_unregistered_edge_would_fail_the_census()
        {
            // Drives the census's own predicate rather than restating it: this is the exact key
            // shape EveryComponentEdgeIsOnTheRecord builds, for a call nobody has reviewed.
            var text = Component("private void Go() { UserSettings.QuietlyDoTheThing(); }");
            var edge = Assert.Single(RbacComponentGateCensusTests.ActingCallsIn(text));
            var key = "Components/Shared/Invented.razor → " + edge;

            Assert.False(RbacComponentGateCensusTests.IsRegistered(key),
                "A call nobody has reviewed must not be on the register.");

            // Positive control: a key that IS on the register answers the other way, so the check
            // above is not passing merely because IsRegistered always says false.
            Assert.True(RbacComponentGateCensusTests.IsRegistered(
                "Components/Shared/DynamicDashboard.razor → ServerConnectionManager.SetCurrentServer"));
        }
    }
}
