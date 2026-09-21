/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:BoundaryCanaryTests — the census is blind to these two controls, and it no longer matters
    /// <summary>
    /// The half of round 8's proof that looks at source — and it looks at it to assert the
    /// INSTRUMENT IS BLIND, not that it sees.
    ///
    /// <para><c>Components/Shared/BoundaryCanary.razor</c> ships two real, deliberately ungated
    /// interactive controls in the always-rendered shell, in the exact two shapes the cold gate
    /// used to defeat round 7's markup-position census:</para>
    /// <code>
    /// &lt;input @bind:get="_text" @bind:set="AcceptText" /&gt;
    /// &lt;EditForm Model="_model" OnValidSubmit="AcceptSubmit"&gt;&lt;button type="submit"&gt;…
    /// </code>
    /// <para>Neither is adversarial. They are what an ordinary developer writes next week, and the
    /// scanner cannot see either: there is no <c>@bind=</c> attribute for rule (b) to match, no
    /// lowercase <c>&lt;form&gt;</c> for rule (c), and <c>EditForm</c> is a framework component so
    /// rule (d) — which derives a child's <c>EventCallback</c> parameters from <c>.razor</c> files
    /// in this repo — has no declaration to derive anything from.</para>
    ///
    /// <para><b>These tests pin the blindness in place.</b> If somebody later widens the scanner
    /// until it sees these two, the round-8 claim quietly reverts to the round-7 claim — "the
    /// census catches them" — which is the sentence that was falsified four times running. The
    /// point is that it does NOT catch them and they are unreachable anyway:
    /// <see cref="InteractiveAppAdmissionTests"/> drives every route in the app over real HTTP and
    /// proves an unauthenticated non-loopback caller never receives them.</para>
    ///
    /// <para>Source blind, plus HTTP refused, is the whole proof that the boundary moved. Either
    /// half alone proves nothing.</para>
    /// </summary>
    public class BoundaryCanaryTests
    {
        private const string CanaryFile = "Components/Shared/BoundaryCanary.razor";

        private static string CanaryMarkup()
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    CanaryFile.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), CanaryFile + " is missing. The permanent regression for the two "
                                           + "shapes that beat round 7 has been deleted.");

            // Comment bodies blanked, so an assertion cannot be satisfied by the prose ABOVE the
            // markup. Five rounds of censuses have each had to learn that lesson separately.
            return ShellBoundaryScan.Blank(File.ReadAllText(full));
        }

        [Fact]
        public void BothShapesAreStillReallyThere()
        {
            var markup = CanaryMarkup();

            Assert.Contains(@"@bind:get=""_text""", markup, StringComparison.Ordinal);
            Assert.Contains(@"@bind:set=""AcceptText""", markup, StringComparison.Ordinal);
            Assert.Contains("<EditForm", markup, StringComparison.Ordinal);
            Assert.Contains(@"OnValidSubmit=""AcceptSubmit""", markup, StringComparison.Ordinal);
            Assert.Contains(@"type=""submit""", markup, StringComparison.Ordinal);
        }

        [Fact]
        public void TheyAreInsideNoAuthorizationBoundary()
        {
            // Deliberately ungated. A ShellGate appearing here would make the HTTP proof vacuous —
            // "unreachable" would then be indistinguishable from "not rendered to anyone".
            var markup = CanaryMarkup();

            Assert.DoesNotContain("<" + ShellBoundaryScan.BoundaryTag, markup, StringComparison.Ordinal);
            Assert.Empty(ShellBoundaryScan.BoundaryRegions(markup));
        }

        [Fact]
        public void TheyRenderOnEveryRoute()
        {
            // MainLayout wraps the Router, so anything it renders outside the <Found> branch is on
            // every route including AccessDenied — which is the page round 6's and round 7's
            // exploits were both driven from.
            Assert.True(ShellSurfaceRegistry.IsShell(CanaryFile),
                CanaryFile + " is no longer in the derived shell closure, so it is no longer the "
                + "always-rendered surface this regression is about. Check that MainLayout still "
                + "renders <BoundaryCanary />.");
        }

        [Fact]
        public void TheMarkupCensusIsStillBlindToBothOfThem()
        {
            // THE ASSERTION THIS FILE EXISTS FOR. Round 7's decider reports ZERO interactive
            // elements in a file containing two of them, and the suite is green — exactly the
            // state the cold gate demonstrated on 2026-08-02, preserved on purpose.
            var elements = ShellBoundaryScan.ElementsOf(CanaryFile);

            Assert.True(elements.Count == 0,
                "The markup census has been widened until it can see the boundary canary:\n  "
                + string.Join("\n  ", elements.Select(e => e.Describe))
                + "\nThat is not an improvement — it re-adopts the claim that a source scan is the "
                + "boundary, which was falsified in rounds 4, 5, 6 and 7. If the scanner really should "
                + "see these shapes, change the canary to two shapes it still cannot see and keep this "
                + "test asserting zero.");
        }

        [Fact]
        public void ThePositiveControl_TheCensusStillSeesAnOrdinaryHandler()
        {
            // Without this, "the census sees nothing in the canary" could be true because the
            // census sees nothing anywhere — a broken instrument reads the same as a blind one.
            var seen = ShellBoundaryScan.ElementsIn("probe.razor",
                @"<button @onclick=""AcceptSubmit"">go</button>");

            Assert.Single(seen);
            Assert.Equal("@onclick", seen[0].Attribute);
            Assert.Null(seen[0].Permission);   // ungated, and correctly reported as such
        }

        [Fact]
        public void TheCanaryTouchesNothingOutsideItself()
        {
            // Round 6's edge census: every call and every property write the component makes on an
            // injected service. Zero, because both handlers only write this component's own
            // private fields. Shipping a genuinely live ungated control to make a point would be
            // its own defect, so the inertness is measured rather than promised.
            var edges = ShellSurfaceRegistry.EdgesOf(CanaryFile);

            Assert.True(edges.Count == 0,
                "The boundary canary now reaches a service: " + string.Join(", ", edges)
                + ". It must stay inert — it is deliberately ungated, so anything it can reach is "
                + "reachable by every console user and every signed-in LAN user with no permission "
                + "check at all.");
        }

        [Fact]
        public void TheDomMarkerIsTheShippedConstant()
        {
            // The HTTP test greps responses for this token. If the component and the test could
            // disagree about it, every "the marker is absent" assertion would pass vacuously.
            Assert.Contains(SQLTriage.Components.Shared.BoundaryCanary.DomMarker,
                            CanaryMarkup(), StringComparison.Ordinal);
        }
    }
}
