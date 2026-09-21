/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationBatchPreviewUiTests — the STRUCTURAL guard on the batch surface
 * (plan item 1.1, Phase 1 build lane; re-pointed for the armed apply in Phase 3, 2026-09-01).
 *
 * ── WHAT CHANGED IN PHASE 3, AND WHY THE OLD RULE IS WORTH KEEPING VISIBLE ───────────────────
 *
 * Adrian's Phase-1 ruling was "preview batch first; the MUTATING batch only after the Phase-2
 * rollback leveling", so this file's original job was to prove BatchRemediationDriver.ApplyBatchAsync
 * was reachable from NO user interface at all. Phase 2 landed that leveling and proved the cure live
 * (the coercing-option blind spot from spike S2 §3.1, cured and mutation-proved), so Phase 3 wires
 * the apply.
 *
 * A ban therefore becomes a CONFINEMENT, and the confinement is the stronger claim of the two:
 *
 *   the apply is reachable from exactly one page, and inside that page from exactly one place —
 *   the armed approval dialog, which carries the tickbox.
 *
 * ── THE INSTRUMENTS ───────────────────────────────────────────────────────────────────────────
 *
 *   1. ExactlyOneUserInterfaceFileNamesTheMutatingBatch (+ the rollback twin) — the WIDE one.
 *      Every .razor/.cs under Pages/, Components/ and Shared/ is stripped of comments and searched
 *      for the literal call. Exactly one file may name it. A second batch-apply surface cannot grow
 *      without someone deciding to change this test.
 *
 *   2. TheApplyReachIsConfinedToTheArmedDialogs — the NARROW one. Instrument 1 only sees the
 *      mutating batch by NAME. It would not see an @onclick in this section that reached
 *      RemediationRunner.ApplyAsync — the single-fix apply, which is right there in the same page's
 *      @code block and applies to a real production server. This one PARSES the rbatch section out
 *      of the file (depth-matched <div>), REMOVES the two armed dialogs, enumerates every remaining
 *      event-handler attribute, and follows each expression into the page's own method bodies.
 *
 *   3. NoApplyShapedControlSitsOutsideTheArmedDialogs — the same remainder, read as an OPERATOR
 *      reads it. A button labelled "Apply" that is wired to nothing would still be a lie about what
 *      that part of the surface does.
 *
 *   4. TheArmedDialogsExist_AndTheyAreWhereTheApplyLives — the COUNTERWEIGHT, and without it the
 *      two above are worthless. A subtraction that removed nothing would leave them scanning the
 *      whole section, and a subtraction that removed everything would leave them scanning nothing;
 *      both read as green. This one asserts the regions exist, carry the tickbox, contain the apply
 *      reach, and that what survives the subtraction is still the real surface.
 *
 * Every scan is itself tested against planted controls (TheSectionGuards_… and
 * TheSubtraction_…) — a guard nobody has watched fail is a guard nobody has tested.
 *
 * ⚠ WHAT THIS CLASS DOES AND DOES NOT PROVE. It reads SOURCE TEXT. It proves where the call sites
 * and the apply-shaped controls are in the section's SOURCE. It does not render the page, so it
 * cannot see whether the armed dialog is actually gated on the arming state at runtime — a dialog
 * whose @if condition was deleted would still pass every test here. That is
 * Gated/RemediationBatchApplyRenderTests.cs, which renders BOTH states through the real Blazor
 * renderer: dialog shut (zero apply-shaped controls in the html) and dialog open (exactly the armed
 * control). The preview half of the render is Gated/RemediationBatchRenderTests.cs. The behavioural
 * half — that the page's own selection method refuses an item the app cannot price a target for,
 * and that a server switch drops the batch state — lives in
 * Gated/RemediationBatchPreviewPageTests.cs and Gated/RemediationServerChangeResetTests.cs. None of
 * those can live here: SQLTriage.Pages.Remediation is Content-Removed from a community build (see
 * Gated/README.md).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    public class RemediationBatchPreviewUiTests
    {
        /// <summary>The mutating batch entry point. No UI source may name it in CODE.</summary>
        private const string ApplyBatchCall = "ApplyBatchAsync";

        /// <summary>
        /// The single-fix apply on this same page. Not banned from the FILE — the rows above the
        /// batch are built on it — but banned from anything the batch section can reach.
        /// </summary>
        private const string SingleApplyCall = "ApplyAsync";

        /// <summary>The batch rollback entry point. Same confinement rule as the apply.</summary>
        private const string RollBackBatchCall = "RollBackBatchAsync";

        /// <summary>The class on the batch surface's outermost element, and the parser's anchor.</summary>
        private const string SectionClass = "rbatch-section";

        /// <summary>
        /// The two ARMED regions — the ONE approval dialog and the reversal confirmation. Phase 3
        /// confines every apply reach on this surface to these, and the guards below prove it by
        /// subtracting them and re-scanning what is left.
        ///
        /// <para>⚠ These names must not be a PREFIX of any other class in the section. The section
        /// parser anchors on <c>\bclass\b</c> containing the name delimited by word boundaries, and
        /// a hyphen is a word boundary — so an anchor of "rbatch-arm" would have matched the inner
        /// <c>rbatch-arm-h</c> heading and removed a heading instead of a dialog. That is why the
        /// containers are "rbatch-armed" and the helper classes are "rbatch-arm-*".</para>
        /// </summary>
        private static readonly string[] ArmedRegionClasses = { "rbatch-armed", "rbatch-rollback-armed" };

        // The scan below must run over CODE, not prose: this lane's page comments explain at length
        // WHY ApplyBatchAsync is not wired, and a guard that counted its own explanation as a
        // violation would have to be weakened to something like "the name followed by a bracket" —
        // which stops seeing a method group.
        //
        // The stripper is RawPassedScan's, not a second copy. It is a real lexer (it tracks string
        // and char literals, so a "//" inside a URL is not mistaken for a comment) and it already
        // backs the raw-Passed guard and the em-dash guard. Its own named limitation, stated in that
        // file: string state resets at each newline, so a verbatim literal's continuation lines can
        // be mis-lexed. That direction of error reports a spurious site — a failing guard, not a
        // silent one.
        private static string StripComments(string source, bool isRazor) =>
            string.Join("\n", RawPassedScan.StripComments(
                source.Replace("\r\n", "\n").Split('\n'), isRazor));

        private static string PagePath() =>
            Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor");

        private static string PageText() => StripComments(File.ReadAllText(PagePath()), isRazor: true);

        /// <summary>Every file a user interface is built from: pages, components, layouts.</summary>
        private static List<string> UiSourceFiles()
        {
            var root = RawPassedScan.RepoRoot().FullName;
            var files = new List<string>();
            foreach (var rel in new[] { "Pages", "Components", "Shared" })
            {
                var dir = Path.Combine(root, rel);
                if (!Directory.Exists(dir)) continue;
                files.AddRange(Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));
            }
            return files;
        }

        // ── The ruling, instrument 1: the wide name scan ─────────────────────────

        [Fact]
        public void TheScanHasSomethingToScan()
        {
            // A guard that silently scanned zero files would pass forever. Fail loudly instead.
            var files = UiSourceFiles();
            Assert.True(files.Count > 50, $"Expected the UI tree to hold many files; found {files.Count}.");
            Assert.Contains(files, f => f.EndsWith("Remediation.razor", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ExactlyOneUserInterfaceFileNamesTheMutatingBatch()
        {
            // ⚠ THIS TEST CHANGED MEANING IN PHASE 3, AND THE OLD MEANING IS WORTH STATING. Until
            // Phase 3 it read "no UI names ApplyBatchAsync at all", because Adrian's Phase-1 ruling
            // was preview-first: the mutating batch waited on the Phase-2 rollback leveling. That
            // leveling landed and was proved live, so the apply ships — and the guard becomes a
            // CONFINEMENT rather than a ban. One page may name it. Every other page, component and
            // shared file may not, so a second surface cannot grow its own batch apply without this
            // going red and someone deciding on purpose.
            var offenders = UiSourceFiles()
                .Where(f => StripComments(File.ReadAllText(f), f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)).Contains(ApplyBatchCall, StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f)!)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.True(offenders.Count == 1 && offenders[0].Equals("Remediation.razor", StringComparison.OrdinalIgnoreCase),
                "BatchRemediationDriver.ApplyBatchAsync may be named by exactly one UI file, "
                + "Pages/Remediation.razor, and only from inside its armed approval flow. Named in: "
                + (offenders.Count == 0 ? "(nothing — the apply is gone, which is also a change)" : string.Join(", ", offenders)));
        }

        [Fact]
        public void TheBatchRollback_IsAlsoReachableFromThatOnePageOnly()
        {
            // Same rule for the undo. A rollback surface on a second page would be a second place an
            // operator could send inverse statements to a production server.
            var offenders = UiSourceFiles()
                .Where(f => StripComments(File.ReadAllText(f), f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)).Contains(RollBackBatchCall, StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f)!)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.True(offenders.Count == 1 && offenders[0].Equals("Remediation.razor", StringComparison.OrdinalIgnoreCase),
                "BatchRemediationDriver.RollBackBatchAsync may be named by exactly one UI file. Named in: "
                + (offenders.Count == 0 ? "(nothing)" : string.Join(", ", offenders)));
        }

        [Fact]
        public void StripsProseButNotCode()
        {
            // The planted controls. Without these, a broken stripper (or a typo in the constant)
            // turns the guard above into a test that asserts nothing and passes green forever.

            // 1. A real call survives stripping and IS seen — in both file dialects.
            const string call = "        await BatchDriver.ApplyBatchAsync(server, items, true, who);";
            Assert.Contains(ApplyBatchCall, StripComments(call, isRazor: false), StringComparison.Ordinal);

            // 2. A method group — no bracket at all — is still seen. This is the shape a
            //    "name followed by a bracket" guard would miss.
            const string methodGroup = "        Handler = BatchDriver.ApplyBatchAsync;";
            Assert.Contains(ApplyBatchCall, StripComments(methodGroup, isRazor: false), StringComparison.Ordinal);

            // 3. Prose is NOT seen — a razor comment, a C# line comment, and a C# block comment.
            Assert.DoesNotContain(ApplyBatchCall,
                StripComments("@* ApplyBatchAsync is deliberately not wired here. *@", isRazor: true), StringComparison.Ordinal);
            Assert.DoesNotContain(ApplyBatchCall,
                StripComments("    // never call ApplyBatchAsync from a page", isRazor: false), StringComparison.Ordinal);
            Assert.DoesNotContain(ApplyBatchCall,
                StripComments("/*\n * ApplyBatchAsync stays unwired until Phase 3.\n */", isRazor: false), StringComparison.Ordinal);

            // 4. And the stripper does not eat the code AROUND a comment.
            const string mixed = "@* note *@\nawait BatchDriver.ApplyBatchAsync(x);";
            Assert.Contains(ApplyBatchCall, StripComments(mixed, isRazor: true), StringComparison.Ordinal);
        }

        [Fact]
        public void ThePageCallsTheMutatingBatch_ExactlyOnce()
        {
            // One call site, not two. The whole confinement argument — one approval, one arming
            // gesture, one place to audit — rests on there being a single method that sends a batch
            // to a server, and a second call site would be a second path with its own arming story.
            var code = PageText();
            var calls = Regex.Matches(code, Regex.Escape(ApplyBatchCall)).Count;
            Assert.True(calls == 1,
                $"Pages/Remediation.razor must call {ApplyBatchCall} exactly once; found {calls}.");

            var rollbacks = Regex.Matches(code, Regex.Escape(RollBackBatchCall)).Count;
            Assert.True(rollbacks == 1,
                $"Pages/Remediation.razor must call {RollBackBatchCall} exactly once; found {rollbacks}.");
        }

        [Fact]
        public void TheStripperStillSeparatesProseFromCodeOnTheRealPage()
        {
            // Until Phase 3 this test asserted the page named the mutating batch ONLY in prose. It
            // now names it in both, so the property worth pinning is the stripper's: the page's long
            // explanation of the confinement must not be counted as call sites. Raw mentions exceed
            // stripped ones, which is only true if the stripper is doing its job.
            var raw = File.ReadAllText(PagePath());
            int rawCount = Regex.Matches(raw, Regex.Escape(ApplyBatchCall)).Count;
            int codeCount = Regex.Matches(StripComments(raw, isRazor: true), Regex.Escape(ApplyBatchCall)).Count;

            Assert.True(rawCount > codeCount,
                $"The page mentions {ApplyBatchCall} {rawCount} times raw and {codeCount} times in code. "
                + "Equal counts mean the stripper stopped removing comments, and every scan in this "
                + "file then reads prose as code.");
        }

        [Fact]
        public void TheBatchSection_UsesThePreviewPath()
        {
            // The other half of the ruling: preview-only means preview IS wired, not that nothing is.
            Assert.Contains("PreviewBatchDetailedAsync", PageText(), StringComparison.Ordinal);
        }

        // ── The ruling, instrument 2: nothing in the SECTION reaches an apply ─────

        [Fact]
        public void TheSectionParser_FindsARealSectionWithRealHandlersInIt()
        {
            // The arming assertion for the two guards below. A parser that returned "" — a renamed
            // class, a restructured element — would make them assert over nothing and pass forever,
            // which is the failure mode of every scan-shaped guard in this repo.
            var section = RazorSectionScan.Section(PageText(), SectionClass);

            Assert.False(string.IsNullOrWhiteSpace(section), $"No <div class=\"{SectionClass}\"> was parsed out of Pages/Remediation.razor.");
            Assert.Contains("rbatch-table", section, StringComparison.Ordinal);
            Assert.Contains("Would reserve", section, StringComparison.Ordinal);

            // It is a SECTION, not the file: what sits after it must not be inside it.
            Assert.DoesNotContain("Agent alert + operator pack", section, StringComparison.Ordinal);
            Assert.True(section.Length < PageText().Length / 2,
                "the extracted section is more than half the page; the depth match is not closing.");

            // And it really does carry handlers, so "no offending handler" means something.
            var handlers = RazorSectionScan.EventHandlers(section);
            Assert.True(handlers.Count >= 3,
                $"Expected the batch section to carry several event handlers; found {handlers.Count}. "
                + "A handler-attribute parser that finds none reports every section clean.");
        }

        [Fact]
        public void TheArmedDialogsExist_AndTheyAreWhereTheApplyLives()
        {
            // ⚠ THE COUNTERWEIGHT, AND IT COMES FIRST DELIBERATELY. The two guards below subtract
            // the armed regions and scan the remainder. If the regions did not exist, the
            // subtraction would remove nothing, the remainder would be the whole section, and those
            // guards would still pass the day someone deleted the approval dialog and wired the
            // apply to a bare button — because a bare button carries no armed class either. This
            // test is what makes the subtraction mean something: the regions are real, and the
            // apply reach is INSIDE them.
            var page = PageText();
            var section = RazorSectionScan.Section(page, SectionClass);
            var code = RazorSectionScan.CodeBlock(page);

            foreach (var cssClass in ArmedRegionClasses)
            {
                var armed = RazorSectionScan.Section(section, cssClass);
                Assert.False(string.IsNullOrWhiteSpace(armed),
                    $"No <div class=\"{cssClass}\"> inside the batch section. The apply is supposed to "
                    + "live in an armed dialog; without one the confinement guards assert nothing.");

                // It carries the arming gesture — a tickbox — not just a button.
                Assert.Contains("type=\"checkbox\"", armed, StringComparison.Ordinal);
            }

            // And the apply really is reached from inside them, so the subtraction removes the
            // thing it claims to remove rather than an empty box.
            var applyDialog = RazorSectionScan.Section(section, "rbatch-armed");
            Assert.NotEmpty(RazorSectionScan.HandlersReaching(applyDialog, code, new[] { ApplyBatchCall }));

            var rollbackDialog = RazorSectionScan.Section(section, "rbatch-rollback-armed");
            Assert.NotEmpty(RazorSectionScan.HandlersReaching(rollbackDialog, code, new[] { RollBackBatchCall }));

            // The subtraction leaves a real surface behind, not nothing: the candidate table and
            // both preview totals must survive it. A Without() that ate the section would make the
            // two guards below trivially green.
            var remainder = RazorSectionScan.Without(section, ArmedRegionClasses);
            Assert.Contains("rbatch-table", remainder, StringComparison.Ordinal);
            Assert.Contains("Would reserve", remainder, StringComparison.Ordinal);
            Assert.True(remainder.Length < section.Length,
                "Without() removed nothing, so the two confinement guards below are scanning the "
                + "armed dialogs as well and would pass whatever is in them.");
        }

        [Fact]
        public void TheApplyReachIsConfinedToTheArmedDialogs()
        {
            // ⚠ THE POINT OF THIS TEST. The wide scan above sees ApplyBatchAsync by name, in whole
            // files. It cannot see an @onclick in this section bound to a page method that calls
            // RemediationRunner.ApplyAsync — the SINGLE-fix apply, which lives in this same @code
            // block and changes a production server. So this follows every handler expression in
            // the section into the page's own method bodies and fails on either name.
            //
            // In Phase 3 the section legitimately contains an apply, so the scan runs over the
            // section MINUS the armed dialogs. What that buys is structural rather than
            // conventional: a control that drifted out of a dialog loses its armed ancestor, falls
            // back into the scanned region, and turns this red. Nothing depends on a reviewer
            // noticing.
            var page = PageText();
            var section = RazorSectionScan.Section(page, SectionClass);
            var code = RazorSectionScan.CodeBlock(page);
            var unarmed = RazorSectionScan.Without(section, ArmedRegionClasses);

            var offenders = RazorSectionScan.HandlersReaching(
                unarmed, code, new[] { ApplyBatchCall, SingleApplyCall, RollBackBatchCall });

            Assert.True(offenders.Count == 0,
                "Outside the armed approval dialogs, the batch section must contain no event handler "
                + "that reaches an apply or a rollback. Offending handlers: "
                + string.Join(" | ", offenders));
        }

        [Fact]
        public void NoApplyShapedControlSitsOutsideTheArmedDialogs()
        {
            // The operator's reading of the same rule. A control LABELLED like an apply is a claim
            // about what that part of the surface does, whether or not it is wired to anything. The
            // list of fixes and the reading are not places anything applies from, so nothing there
            // may read as though it is.
            var section = RazorSectionScan.Section(PageText(), SectionClass);
            var unarmed = RazorSectionScan.Without(section, ArmedRegionClasses);
            var offenders = RazorSectionScan.ApplyShapedControls(unarmed);

            Assert.True(offenders.Count == 0,
                "Outside the armed dialogs the batch section must carry no apply-shaped control. "
                + "Offending controls: " + string.Join(" | ", offenders));
        }

        [Fact]
        public void TheSectionGuards_CatchThePlantedShapesTheyExistToCatch()
        {
            // Planted controls for instruments 2 and 3, in the file rather than in a commit message.
            // Every assertion below is over a synthetic section, so nothing here touches the page.
            const string code = @"
    private bool _open;
    private async Task PreviewBatchAsync() { await BatchDriver.PreviewBatchDetailedAsync(s, i); }
    private async Task DoTheBatchAsync() { await BatchDriver.ApplyBatchAsync(s, i, true, who); }
    private async Task OneStepRemovedAsync() { await DoTheBatchAsync(); }
    private async Task SingleFixAsync(FixRow row) { row.Result = await Runner.ApplyAsync(row.Template.Key, s); }
";

            // 1. A handler calling the mutating batch DIRECTLY is caught.
            var direct = Planted(@"<button @onclick=""DoTheBatchAsync"">Preview</button>");
            Assert.Single(RazorSectionScan.HandlersReaching(direct, code, new[] { ApplyBatchCall, SingleApplyCall }));

            // 2. And so is one that reaches it THROUGH another page method. A guard that only read
            //    the attribute value would be defeated by a one-line wrapper.
            var indirect = Planted(@"<button @onclick=""OneStepRemovedAsync"">Preview</button>");
            Assert.Single(RazorSectionScan.HandlersReaching(indirect, code, new[] { ApplyBatchCall, SingleApplyCall }));

            // 3. The SINGLE-fix apply is caught too — the shape the name-based scan cannot see.
            var single = Planted(@"<button @onclick=""() => SingleFixAsync(row)"">Preview</button>");
            Assert.Single(RazorSectionScan.HandlersReaching(single, code, new[] { ApplyBatchCall, SingleApplyCall }));

            // 4. A lambda naming the call inline, with no page method involved at all.
            var lambda = Planted(@"<button @onclick=""() => BatchDriver.ApplyBatchAsync(s, i, true, w)"">Go</button>");
            Assert.Single(RazorSectionScan.HandlersReaching(lambda, code, new[] { ApplyBatchCall, SingleApplyCall }));

            // 5. The negative control: the section as it really is — a preview handler — is clean.
            //    Without this, a scanner that returned every handler would pass 1-4 and prove nothing.
            var clean = Planted(@"<button @onclick=""PreviewBatchAsync"">Preview 3 selected</button>");
            Assert.Empty(RazorSectionScan.HandlersReaching(clean, code, new[] { ApplyBatchCall, SingleApplyCall }));

            // 6. Instrument 3 on a button that is wired to NOTHING and still lies about the surface.
            Assert.Single(RazorSectionScan.ApplyShapedControls(
                Planted(@"<button class=""btn btn-primary"">Apply all 3 fixes</button>")));
            Assert.Single(RazorSectionScan.ApplyShapedControls(
                Planted(@"<input type=""submit"" value=""Run the batch"" />")));

            // 7. Its negative control: the real labels must not trip it, or the guard would have to
            //    be switched off the first time someone edited the copy.
            Assert.Empty(RazorSectionScan.ApplyShapedControls(
                Planted(@"<button @onclick=""PreviewBatchAsync""><span> Preview 3 selected</span></button>")));
        }

        [Fact]
        public void TheSubtraction_RemovesTheArmedDialogAndNothingElse()
        {
            // The Phase-3 half of the same discipline: the guards above are only as good as
            // Without(), so Without() is exercised against planted markup here rather than trusted.
            const string code = @"
    private async Task DoTheBatchAsync() { await BatchDriver.ApplyBatchAsync(s, i, true, who); }
";

            // 1. An apply INSIDE an armed dialog survives the section scan — that is the point.
            var armed = Planted(
                @"<button @onclick=""PreviewBatchAsync"">Preview</button>
                  <div class=""rbatch-armed"">
                    <input type=""checkbox"" />
                    <button @onclick=""DoTheBatchAsync"">Approve and apply 3 fixes</button>
                  </div>");
            var armedSection = RazorSectionScan.Section(armed, SectionClass);
            var armedRemainder = RazorSectionScan.Without(armedSection, ArmedRegionClasses);
            Assert.Empty(RazorSectionScan.HandlersReaching(armedRemainder, code, new[] { ApplyBatchCall }));
            Assert.Empty(RazorSectionScan.ApplyShapedControls(armedRemainder));
            // …and it really was there before the subtraction, so the emptiness above is a removal
            // and not an absence.
            Assert.Single(RazorSectionScan.HandlersReaching(armedSection, code, new[] { ApplyBatchCall }));

            // 2. THE SHAPE THE GUARD EXISTS FOR: the same apply, one div OUTSIDE the dialog. This is
            //    what a careless edit produces, and it must survive the subtraction and be caught.
            var loose = Planted(
                @"<div class=""rbatch-armed""><input type=""checkbox"" /></div>
                  <button @onclick=""DoTheBatchAsync"">Approve and apply 3 fixes</button>");
            var looseRemainder = RazorSectionScan.Without(
                RazorSectionScan.Section(loose, SectionClass), ArmedRegionClasses);
            Assert.Single(RazorSectionScan.HandlersReaching(looseRemainder, code, new[] { ApplyBatchCall }));
            Assert.Single(RazorSectionScan.ApplyShapedControls(looseRemainder));

            // 3. TWO armed regions are both removed. A subtraction that stopped at the first would
            //    leave the reversal dialog in the scanned region and report it as an offender —
            //    a false red, which is survivable, and the fix is still to prove it does not happen.
            var two = Planted(
                @"<div class=""rbatch-armed""><button @onclick=""DoTheBatchAsync"">Apply</button></div>
                  <div class=""rbatch-rollback-armed""><button @onclick=""DoTheBatchAsync"">Put back</button></div>");
            var twoRemainder = RazorSectionScan.Without(
                RazorSectionScan.Section(two, SectionClass), ArmedRegionClasses);
            Assert.Empty(RazorSectionScan.HandlersReaching(twoRemainder, code, new[] { ApplyBatchCall }));

            // 4. A class that matches nothing removes nothing, silently. Pinned so the behaviour is
            //    known rather than discovered: it is exactly why every caller of Without() is paired
            //    with a counterweight test asserting the removal was real.
            var untouched = RazorSectionScan.Without("<div class=\"a\">x</div>", "no-such-class");
            Assert.Equal("<div class=\"a\">x</div>", untouched);

            // 5. The helper classes must NOT be matched by the container anchors. "rbatch-arm-h"
            //    contains "rbatch-arm" between word boundaries — the trap this naming avoids.
            var helpers = "<div class=\"rbatch-arm-h\">heading</div><div class=\"rbatch-arm-table\">t</div>";
            Assert.Equal(helpers, RazorSectionScan.Without(helpers, ArmedRegionClasses));
        }

        /// <summary>Wraps planted markup in a section element the parser will find.</summary>
        private static string Planted(string inner) =>
            $"<div class=\"other\">before</div>\n<div class=\"{SectionClass}\">\n  <div>\n  {inner}\n  </div>\n</div>\n<div>after</div>";

        // ── The credit split: two numbers, never one ─────────────────────────────

        [Fact]
        public void TheBatchTotals_AreQuotedTwice_AndNamedApart()
        {
            // Plan item 1.2 exists because summing what a batch RESERVES and calling it the price
            // over-reports by every already-compliant item — proved live in spike S1 §3.4, where a
            // 3-credit batch billed 2. Both readings must appear, under names an operator can tell
            // apart, and both must be read from the driver rather than recomputed on the page.
            var page = PageText();

            Assert.Contains("PricedTotal", page, StringComparison.Ordinal);
            Assert.Contains("ChangingPrice", page, StringComparison.Ordinal);
            Assert.Contains("Would reserve", page, StringComparison.Ordinal);
            Assert.Contains("Projected commit", page, StringComparison.Ordinal);
        }

        [Fact]
        public void TheNoChangeFlag_KeepsUnknownAsAThirdAnswer()
        {
            // BatchRemediationPreviewItem.IsNoChange is bool?: null means the preview did not expose
            // a comparable current-vs-target pair. Rendering null as "no change" would quietly price
            // an unknown at zero, which is the same over-promise inverted. The page must branch on
            // all three, and say "(unread)" for the third.
            var page = PageText();

            Assert.Contains("IsNoChange == true", page, StringComparison.Ordinal);
            Assert.Contains("IsNoChange == false", page, StringComparison.Ordinal);
            Assert.Contains("(unread)", page, StringComparison.Ordinal);
        }

        [Fact]
        public void TheBatchSection_StatesThatNothingAppliesFromTheList()
        {
            // The operator-facing half. The list of fixes is not a place anything applies from, and
            // the copy says so plainly rather than leaving the absence of a button to be read as a
            // missing feature. It also names the step that DOES apply, so nobody hunts for it.
            var page = PageText();
            Assert.Contains("Nothing is applied from this list", page, StringComparison.Ordinal);
            Assert.Contains("approval screen", page, StringComparison.Ordinal);
        }

        [Fact]
        public void TheReversibilityAnswer_HasThreeStatesAndComesFromTheEngine()
        {
            // Reversibility BEFORE approval is the ruling. Three answers, because bool? has three:
            // rendering "unknown" as "cannot be undone" would refuse a reversible fix, and rendering
            // it as "can be undone" would promise an undo nobody checked.
            var page = PageText();
            Assert.Contains("Can be undone", page, StringComparison.Ordinal);
            Assert.Contains("Cannot be undone", page, StringComparison.Ordinal);
            Assert.Contains("Undo unknown", page, StringComparison.Ordinal);

            // And the answer is READ from the engine's preview, never decided here. A page that
            // computed it would compute it from the template alone, and the honest answer depends
            // on what the server said when the preview ran.
            Assert.Contains("preview.CanRollBack", page, StringComparison.Ordinal);
            Assert.Contains("ReversibilityNote", page, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSensitiveExclusion_ReadsTheDriversOwnPredicate()
        {
            // Belt AND braces, and the braces must be the SAME rule. The driver refuses a Sensitive
            // item at gate B2; this page keeps it un-tickable. If the page restated "what counts as
            // sensitive" instead of asking the driver, the tickbox and the gate could come to
            // disagree — and the visible one would be the tickbox.
            var page = PageText();
            Assert.Contains("BatchRemediationDriver.IsBatchable", page, StringComparison.Ordinal);
            Assert.DoesNotContain("RemediationRiskClass.Sensitive", page, StringComparison.Ordinal);
        }

        [Fact]
        public void TheApprovalIsArmed_AndTheArmingIsNotSetByCode()
        {
            // The arming gesture is the same shape the two ConfirmLargeOperation ticks already use
            // on this page. What matters is the DIRECTION of the default: false, and only ever set
            // true by a bound checkbox. A field the code can set to true is not an arming gesture.
            var page = PageText();
            var code = RazorSectionScan.CodeBlock(page);

            Assert.Contains("@bind=\"_batchArmAcknowledged\"", page, StringComparison.Ordinal);
            Assert.Contains("@bind=\"_batchRollbackAcknowledged\"", page, StringComparison.Ordinal);

            foreach (var field in new[] { "_batchArmAcknowledged", "_batchRollbackAcknowledged" })
            {
                Assert.DoesNotContain($"{field} = true", code, StringComparison.Ordinal);
                Assert.Contains($"{field} = false", code, StringComparison.Ordinal);
            }
        }

        // ── The selection is not re-implemented in the page ──────────────────────

        [Fact]
        public void ThePage_DelegatesSelectionToTheTestedSelector()
        {
            // The predicates deciding what an operator may change on a production server live in
            // BatchCandidateSelector, which has its own tests. If they migrate back into the
            // @code block they become unexercisable again.
            Assert.Contains("BatchCandidateSelector.Select", PageText(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A small, deliberately shallow reader for ONE section of a .razor file: the element carrying a
    /// given class, the event-handler attributes inside it, the controls inside it, and one-level-at-
    /// a-time resolution of a handler expression into the page's own <c>@code</c> method bodies.
    ///
    /// <para>⚠ WHAT IT IS NOT. It is not a Razor parser and it is not a call-graph. It matches tags
    /// and attributes with regular expressions over comment-stripped source, and it follows named
    /// page methods to a bounded depth. Every limitation below errs toward reporting MORE, never
    /// fewer, candidate offenders — a guard that over-reports fails loudly and gets looked at; one
    /// that under-reports is the thing this file exists to avoid.</para>
    /// </summary>
    internal static class RazorSectionScan
    {
        /// <summary>How far a handler expression is followed into page methods. Three hops is well
        /// past anything an event handler on this page does, and it terminates on cycles.</summary>
        private const int MaxHops = 3;

        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The markup of the &lt;div&gt; whose opening tag carries <paramref name="cssClass"/>,
        /// found by matching &lt;div&gt; against &lt;/div&gt; by DEPTH rather than by taking the
        /// next closing tag. Returns "" when there is no such element.
        /// </summary>
        public static string Section(string source, string cssClass)
        {
            var anchor = Regex.Match(source, "class\\s*=\\s*[\"'][^\"']*\\b" + Regex.Escape(cssClass) + "\\b[^\"']*[\"']",
                                     RegexOptions.IgnoreCase, Budget);
            if (!anchor.Success) return string.Empty;

            // Walk back to the '<' that opens the tag carrying that class.
            int open = source.LastIndexOf('<', anchor.Index);
            if (open < 0) return string.Empty;

            int depth = 0;
            var tags = Regex.Matches(source.Substring(open), @"</?div\b", RegexOptions.IgnoreCase, Budget);
            foreach (Match tag in tags)
            {
                depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
                if (depth != 0) continue;

                int end = source.Substring(open).IndexOf('>', tag.Index);
                if (end < 0) break;
                return source.Substring(open, end + 1);
            }
            return string.Empty;   // unbalanced: report nothing rather than the rest of the file
        }

        /// <summary>
        /// <paramref name="markup"/> with every element carrying any of <paramref name="cssClasses"/>
        /// REMOVED, element and contents.
        ///
        /// <para>⚠ THIS IS THE SUBTRACTION THE PHASE-3 GUARDS REST ON, so its failure modes matter.
        /// It removes REPEATEDLY until no such element is left, so two armed dialogs are both
        /// removed rather than the first only. A class that matches NOTHING removes nothing and is
        /// silent here — which is why every caller is paired with a test asserting the subtraction
        /// actually removed something, and that what it removed is what it claims to be. A helper
        /// that quietly removed nothing would turn "the apply is confined to the armed dialogs"
        /// into "there is no apply", and both read as green.</para>
        ///
        /// <para>The removal boundary is <see cref="Section"/>'s depth match, so it inherits that
        /// method's one behaviour on unbalanced markup: an unclosed div yields "" and removes
        /// nothing, leaving the apply IN the scanned region and the guard RED. That is the safe
        /// direction.</para>
        /// </summary>
        public static string Without(string markup, params string[] cssClasses)
        {
            var remaining = markup;
            foreach (var cssClass in cssClasses)
            {
                // Bounded rather than while(true): a Section that returned a non-empty string it
                // could not find again would otherwise spin.
                for (int guard = 0; guard < 32; guard++)
                {
                    var element = Section(remaining, cssClass);
                    if (string.IsNullOrEmpty(element)) break;
                    int at = remaining.IndexOf(element, StringComparison.Ordinal);
                    if (at < 0) break;
                    remaining = remaining.Remove(at, element.Length);
                }
            }
            return remaining;
        }

        /// <summary>The page's <c>@code { ... }</c> block, brace-matched. "" when absent.</summary>
        public static string CodeBlock(string source)
        {
            var start = Regex.Match(source, @"@code\s*\{", RegexOptions.IgnoreCase, Budget);
            if (!start.Success) return string.Empty;

            int i = start.Index + start.Length - 1;   // at the '{'
            int depth = 0;
            for (int p = i; p < source.Length; p++)
            {
                if (source[p] == '{') depth++;
                else if (source[p] == '}' && --depth == 0) return source.Substring(i, p - i + 1);
            }
            return source.Substring(i);
        }

        /// <summary>
        /// Every Blazor event-handler attribute in the markup, as (attribute, expression).
        /// <c>@onclick</c>, <c>@onchange</c>, <c>@onsubmit</c> and the rest all match one shape.
        /// </summary>
        public static List<(string Attribute, string Expression)> EventHandlers(string markup)
        {
            var found = new List<(string, string)>();
            foreach (Match m in Regex.Matches(
                         markup, "@(?<attr>on[a-zA-Z]+)\\s*=\\s*(?<q>[\"'])(?<expr>.*?)\\k<q>",
                         RegexOptions.Singleline, Budget))
            {
                found.Add(("@" + m.Groups["attr"].Value, m.Groups["expr"].Value));
            }
            return found;
        }

        /// <summary>
        /// The handlers in <paramref name="section"/> whose expression — or a page method it names,
        /// followed up to <see cref="MaxHops"/> hops — mentions any of <paramref name="bannedCalls"/>.
        /// </summary>
        public static List<string> HandlersReaching(string section, string codeBlock, IReadOnlyList<string> bannedCalls)
        {
            var offenders = new List<string>();
            foreach (var (attribute, expression) in EventHandlers(section))
            {
                var hit = FirstBannedCall(expression, codeBlock, bannedCalls, MaxHops,
                                          new HashSet<string>(StringComparer.Ordinal));
                if (hit is not null)
                    offenders.Add($"{attribute}=\"{expression}\" reaches {hit}");
            }
            return offenders;
        }

        private static string? FirstBannedCall(
            string expression, string codeBlock, IReadOnlyList<string> banned, int hops, HashSet<string> visited)
        {
            foreach (var call in banned)
                if (expression.Contains(call, StringComparison.Ordinal)) return call;

            if (hops <= 0 || string.IsNullOrEmpty(codeBlock)) return null;

            foreach (Match id in Regex.Matches(expression, @"\b[A-Za-z_]\w*\b", RegexOptions.None, Budget))
            {
                var name = id.Value;
                if (!visited.Add(name)) continue;
                if (!TryGetMethodBody(codeBlock, name, out var body)) continue;

                var hit = FirstBannedCall(body, codeBlock, banned, hops - 1, visited);
                if (hit is not null) return hit;
            }
            return null;
        }

        /// <summary>
        /// The body of a method DECLARED in the code block, block-bodied or expression-bodied.
        /// Over-matching is the safe direction here: a false body makes the guard look harder, and
        /// a name that declares no method simply yields nothing.
        /// </summary>
        public static bool TryGetMethodBody(string codeBlock, string name, out string body)
        {
            body = string.Empty;
            var declaration = Regex.Match(
                codeBlock,
                @"(?:private|protected|public|internal|static|async|override|virtual)\s[^\r\n;=]*?\b"
                + Regex.Escape(name) + @"\s*\([^)]*\)\s*(?<tail>=>|\{)",
                RegexOptions.None, Budget);
            if (!declaration.Success) return false;

            int at = declaration.Groups["tail"].Index;
            if (declaration.Groups["tail"].Value == "{")
            {
                int depth = 0;
                for (int p = at; p < codeBlock.Length; p++)
                {
                    if (codeBlock[p] == '{') depth++;
                    else if (codeBlock[p] == '}' && --depth == 0)
                    {
                        body = codeBlock.Substring(at, p - at + 1);
                        return true;
                    }
                }
                body = codeBlock.Substring(at);
                return true;
            }

            int semi = codeBlock.IndexOf(';', at);
            body = semi < 0 ? codeBlock.Substring(at) : codeBlock.Substring(at, semi - at + 1);
            return true;
        }

        /// <summary>
        /// Words that make a control read as "this changes the server". Matched whole, so
        /// "Previewing" and "Approver" do not trip a rule about "Preview" and "Approve".
        /// </summary>
        private static readonly Regex ApplyShapedLabel = new(
            @"\b(apply|applies|applying|execute|run|runs|approve|commit|remediate|fix)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

        /// <summary>
        /// Buttons and submit/button inputs in the markup whose visible label reads like an apply.
        /// Razor expressions inside the label are stripped before matching, because their SOURCE
        /// (method names, field names) is not what an operator reads.
        /// </summary>
        public static List<string> ApplyShapedControls(string markup)
        {
            var offenders = new List<string>();

            foreach (Match b in Regex.Matches(markup, @"<button\b[^>]*>(?<inner>.*?)</button>",
                                              RegexOptions.Singleline | RegexOptions.IgnoreCase, Budget))
            {
                var label = VisibleLabel(b.Groups["inner"].Value);
                if (ApplyShapedLabel.IsMatch(label)) offenders.Add($"<button> labelled \"{label.Trim()}\"");
            }

            foreach (Match i in Regex.Matches(markup, @"<input\b[^>]*>", RegexOptions.IgnoreCase, Budget))
            {
                var type = Regex.Match(i.Value, "type\\s*=\\s*[\"'](?<t>[^\"']*)[\"']", RegexOptions.IgnoreCase, Budget);
                if (!type.Success) continue;
                if (!type.Groups["t"].Value.Equals("submit", StringComparison.OrdinalIgnoreCase)
                    && !type.Groups["t"].Value.Equals("button", StringComparison.OrdinalIgnoreCase)) continue;

                var value = Regex.Match(i.Value, "value\\s*=\\s*[\"'](?<v>[^\"']*)[\"']", RegexOptions.IgnoreCase, Budget);
                var label = value.Success ? value.Groups["v"].Value : string.Empty;
                if (ApplyShapedLabel.IsMatch(label)) offenders.Add($"<input type=submit> labelled \"{label}\"");
            }

            return offenders;
        }

        /// <summary>
        /// What a person reads inside a control: tags removed, razor expressions removed, C# string
        /// literals inside those expressions KEPT — <c>@(_open ? "Hide" : "Show")</c> renders one of
        /// those two words, so dropping them would blind the label check to any conditional caption.
        /// </summary>
        private static string VisibleLabel(string inner)
        {
            var literals = string.Join(" ", Regex.Matches(inner, "\"(?<s>[^\"]*)\"", RegexOptions.None, Budget)
                                                .Select(m => m.Groups["s"].Value));
            var withoutExpressions = Regex.Replace(inner, @"@\(?[A-Za-z_][\w.]*(\([^)]*\))?\)?", " ", RegexOptions.None, Budget);
            var withoutTags = Regex.Replace(withoutExpressions, "<[^>]*>", " ", RegexOptions.None, Budget);
            return Regex.Replace(withoutTags + " " + literals, @"\s+", " ", RegexOptions.None, Budget);
        }
    }
}
