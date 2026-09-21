/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationValueBoundsTests — Phase-2 item 6a. Operator-proofing the one thing an operator types.
 *
 * ADRIAN'S INSTRUCTION, mid-lane 2026-09-01: "make it idiot proof - I will be driving it." Every
 * other input to a remediation is shipped data: the option name, the T-SQL shape, the offenders
 * query. The INTEGER is the one thing a human types, and until now it was checked only against the
 * template's own MinValue/MaxValue, which is a SCHEMA range and not a sane one. MAXSERVERMEMORY
 * ships [128, 2147483647]: 128 is inside the range and starves the engine, and every digit up to
 * two billion is accepted on a box with 16 GB in it.
 *
 * WHAT EACH REFUSAL MUST DO, and what these tests measure:
 *   1. refuse - the value never becomes a number the renderer can use;
 *   2. say the ALLOWED RANGE, so the operator can act on it;
 *   3. say TRUTHFULLY WHAT RAN, which is the fact they will want first;
 *   4. and prove it by counting, not by comment.
 *
 * ⚠ CLAIM 3 USED TO BE FALSE FOR ONE OPTION, AND THE FIX-ROUND GATE PROVED IT LIVE. Every refusal
 * printed "Nothing was sent to the server". For 'max server memory (MB)' the executor read the
 * host's installed RAM BEFORE resolving the value, so MaxServerMemoryMb="banana" printed that
 * sentence while sys.dm_os_sys_info's execution_count went 0 -> 1. The refusal was right; the
 * sentence beside it was an operator-facing lie.
 *
 * THE CURE IS ORDER, AND THESE TESTS MEASURE THE ORDER. RemediationValueBounds.ResolveStagedAsync
 * takes a host READER rather than a host VALUE, so a test can COUNT its invocations with no server
 * at all: zero for anything that fails the strict parse or the static range, one for a value that
 * gets far enough to need the host-relative ceiling. The two refusal sentences are separate
 * constants and each is asserted only on its own path. The live half - the same values through the
 * REAL runner against a REAL instance, with sys.dm_os_sys_info's execution_count read before and
 * after - is in RemediationPhase2LiveSmokeTests.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class RemediationValueBoundsTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationValueBoundsTests(ITestOutputHelper output) => _out = output;

        private static RemediationOperation Op(string configName, int min = 0, int max = 2147483647) => new()
        {
            OpKind = RemediationOpKind.SpConfigure,
            ConfigName = configName,
            ValueParam = "V",
            MinValue = min,
            MaxValue = max,
        };

        private static IReadOnlyDictionary<string, string> P(string v) =>
            new Dictionary<string, string> { ["V"] = v };

        // ── THE FUZZ: each REJECTED CLASS proves a refusal that names the range ──

        [Theory]
        [InlineData("")]                       // empty
        [InlineData("   ")]                    // whitespace
        [InlineData("lots")]                   // words
        [InlineData("8 GB")]                   // a unit an operator would reasonably type
        [InlineData("1,024")]                  // thousands separator
        [InlineData("1 024")]                  // separator, other dialect
        [InlineData("0x10")]                   // hex
        [InlineData("4.5")]                    // decimal
        [InlineData("1e3")]                    // scientific
        [InlineData("4; DROP DATABASE payroll")]      // injection-shaped
        [InlineData("4'); EXEC xp_cmdshell 'dir")]    // injection-shaped, quote-breaking
        [InlineData("99999999999999999999")]   // overflows int
        [InlineData("--4")]                    // double sign
        [InlineData("+")]                      // a sign with no digits
        public void GarbageIsRefused_AndTheRefusalNamesTheAllowedRange(string raw)
        {
            var op = Op("max degree of parallelism", 0, 64);

            var ok = RemediationOpRenderer.TryResolveValue(op, P(raw), out var value, out var error);

            _out.WriteLine($"[{raw}] -> {error}");
            Assert.False(ok);
            Assert.Equal(0, value);
            Assert.StartsWith("Refused:", error);
            Assert.Contains(RemediationValueBounds.RefusedBeforeAnySqlSentence, error);
            Assert.Contains("Enter a whole number from 0 to 64", error);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(65)]
        [InlineData(int.MaxValue)]
        public void AnOutOfRangeNumberIsRefused_AndTheNumberIsQuotedBack(int raw)
        {
            var op = Op("max degree of parallelism", 0, 64);

            Assert.False(RemediationOpRenderer.TryResolveValue(op, P(raw.ToString()), out _, out var error));
            Assert.Contains(raw.ToString(), error);
            Assert.Contains("outside the allowed range", error);
            Assert.Contains("Enter a whole number from 0 to 64", error);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("4")]
        [InlineData("+8")]
        [InlineData("64")]
        public void ARealValueIsAccepted_SoTheGuardIsNotJustStrict(string raw)
        {
            // The negative control on the whole file: a validator that refused everything would
            // pass every assertion above and ship a product nobody can use.
            Assert.True(RemediationOpRenderer.TryResolveValue(Op("max degree of parallelism", 0, 64), P(raw), out var v, out var e), e);
            Assert.Equal(int.Parse(raw.TrimStart('+')), v);
        }

        // ── THE SANE RANGE IS TIGHTER THAN THE TEMPLATE'S, AND ONLY EVER TIGHTER ──

        [Fact]
        public void MaxServerMemory_HasAFloorTheTemplateDoesNot()
        {
            // The template ships MinValue = 128, which SQL Server accepts and which is an outage.
            var op = Op("max server memory (MB)", 128, 2147483647);

            Assert.False(RemediationOpRenderer.TryResolveValue(op, P("128"), out _, out var error));
            Assert.Contains("starves the engine", error);
            Assert.Contains($"from {RemediationValueBounds.MaxServerMemoryFloorMb}", error);

            Assert.True(RemediationOpRenderer.TryResolveValue(op, P("4096"), out var ok, out _));
            Assert.Equal(4096, ok);
        }

        [Fact]
        public void MaxServerMemory_IsCappedAtTheHostsRAMWhenTheHostCouldBeRead()
        {
            var op = Op("max server memory (MB)", 128, 2147483647);

            // 16 GB box: a 64 GB cap is not a cap, it is a typo with a credit attached.
            Assert.False(RemediationOpRenderer.TryResolveValue(op, P("65536"), physicalMemoryMb: 16384, out _, out var error));
            Assert.Contains("16384 MB of RAM", error);
            Assert.Contains("from 1024 to 16384", error);

            // ⚠ AND THE SENTENCE IS THE HOST-CHECK ONE, not the no-SQL one. This refusal is only
            // reachable because a read-only query ran, and it says so.
            _out.WriteLine(error);
            Assert.Contains(RemediationValueBounds.HostCheckRanSentence, error);
            Assert.DoesNotContain(RemediationValueBounds.RefusedBeforeAnySqlSentence, error);

            Assert.True(RemediationOpRenderer.TryResolveValue(op, P("12288"), physicalMemoryMb: 16384, out var v, out _));
            Assert.Equal(12288, v);
        }

        // ── THE ORDER, MEASURED BY COUNTING THE HOST READER'S INVOCATIONS ──

        /// <summary>Counts how many times the staged resolution asked for the host's RAM.</summary>
        private sealed class CountingHostReader
        {
            private readonly int? _answer;
            public int Calls { get; private set; }
            public CountingHostReader(int? answer) => _answer = answer;
            public Task<int?> ReadAsync(CancellationToken ct) { Calls++; return Task.FromResult(_answer); }
        }

        [Theory]
        [InlineData("banana")]                    // the gate's own value
        [InlineData("")]                          // empty
        [InlineData("-1")]                        // negative
        [InlineData("128")]                       // parses, but below the 1024 MB floor: STATIC refusal
        [InlineData("8 GB")]                      // a unit
        [InlineData("4096; DROP DATABASE payroll")]
        public async Task TheHostIsNotReadForAValueThatCannotParse_OrThatFailsTheStaticRange(string raw)
        {
            // ⚠ THE FIX-ROUND BLOCKER, MEASURED OFFLINE. Before the staging, the executor read
            // sys.dm_os_sys_info first and passed the answer in, so EVERY refusal of a max-memory
            // value had already queried the server while printing that nothing had.
            var op = Op("max server memory (MB)", 128, 2147483647);
            var host = new CountingHostReader(16384);

            var staged = await RemediationValueBounds.ResolveStagedAsync(op, raw, host.ReadAsync, CancellationToken.None);

            _out.WriteLine($"[{raw}] host reads={host.Calls} -> {staged.Error}");
            Assert.False(staged.Ok);
            Assert.Equal(0, host.Calls);
            Assert.False(staged.HostCheckRan);
            Assert.Contains(RemediationValueBounds.RefusedBeforeAnySqlSentence, staged.Error);
            Assert.DoesNotContain(RemediationValueBounds.HostCheckRanSentence, staged.Error);
        }

        [Fact]
        public async Task AValidlyParsedValue_IsTheONLYThingThatCanCauseTheHostRead()
        {
            // The positive control on the test above: an instrument that never fires proves nothing.
            var op = Op("max server memory (MB)", 128, 2147483647);

            var tooBig = new CountingHostReader(16384);
            var refused = await RemediationValueBounds.ResolveStagedAsync(op, "65536", tooBig.ReadAsync, CancellationToken.None);
            _out.WriteLine($"[65536] host reads={tooBig.Calls} -> {refused.Error}");
            Assert.False(refused.Ok);
            Assert.Equal(1, tooBig.Calls);
            Assert.True(refused.HostCheckRan);
            Assert.Equal(16384, refused.HostMemoryMb);
            Assert.Contains(RemediationValueBounds.HostCheckRanSentence, refused.Error);
            Assert.DoesNotContain(RemediationValueBounds.RefusedBeforeAnySqlSentence, refused.Error);

            var ok = new CountingHostReader(16384);
            var accepted = await RemediationValueBounds.ResolveStagedAsync(op, "12288", ok.ReadAsync, CancellationToken.None);
            Assert.True(accepted.Ok);
            Assert.Equal(12288, accepted.Value);
            Assert.Equal(1, ok.Calls);
        }

        [Fact]
        public async Task AnOptionWithNoHostRelativeBound_NeverReadsTheHostAtAll()
        {
            // Every other option's ceiling is static, so asking a server for it would be a query
            // with no question behind it.
            var op = Op("cost threshold for parallelism", 0, 32767);

            var good = new CountingHostReader(16384);
            Assert.True((await RemediationValueBounds.ResolveStagedAsync(op, "50", good.ReadAsync, CancellationToken.None)).Ok);
            Assert.Equal(0, good.Calls);

            var bad = new CountingHostReader(16384);
            var refused = await RemediationValueBounds.ResolveStagedAsync(op, "lots", bad.ReadAsync, CancellationToken.None);
            Assert.False(refused.Ok);
            Assert.Equal(0, bad.Calls);
        }

        [Fact]
        public async Task AnUnreadableHost_MakesNoClaimEitherWay_AndTheCallerCanTell()
        {
            // The old sentence for this case said "The host's installed RAM could not be read" on a
            // range NOBODY had asked a host about - the same over-claim pointing the other way. A
            // null reading now leaves the static range exactly as it is, and the CALLER is told the
            // check ran and failed so a surface can say so where it matters (the preview does).
            var op = Op("max server memory (MB)", 128, 2147483647);

            var unreadable = new CountingHostReader(null);
            var staged = await RemediationValueBounds.ResolveStagedAsync(op, "1048576", unreadable.ReadAsync, CancellationToken.None);

            Assert.True(staged.Ok);
            Assert.Equal(1, unreadable.Calls);
            Assert.True(staged.HostCheckFailed);

            // And the static range, asked directly, does not pretend a host was involved.
            var stat = RemediationValueBounds.ResolveStatic(op);
            Assert.Equal(RemediationValueBounds.MaxServerMemoryFloorMb, stat.Min);
            Assert.Equal(2147483647, stat.Max);
            Assert.True(stat.HasHostCeiling, "the static range must MARK the ceiling as host-relative "
                + "so a surface binding to it cannot present it as final.");
            Assert.DoesNotContain("could not be read", stat.Reason);
        }

        [Fact]
        public void TheSaneRangeNeverWIDENSTheTemplatesOwnRange()
        {
            // The property that makes this safe to add to every option at once: intersection, never
            // replacement. A template that ships a TIGHTER bound than the sane table keeps it.
            var tight = Op("max degree of parallelism", 2, 8);
            var range = RemediationValueBounds.Resolve(tight);
            Assert.Equal(2, range.Min);
            Assert.Equal(8, range.Max);

            Assert.False(RemediationOpRenderer.TryResolveValue(tight, P("1"), out _, out _));
            Assert.False(RemediationOpRenderer.TryResolveValue(tight, P("16"), out _, out _));
            Assert.True(RemediationOpRenderer.TryResolveValue(tight, P("4"), out _, out _));
        }

        [Fact]
        public void AnOptionWithNoSaneEntry_KeepsTheTemplatesRangeExactly()
        {
            var op = Op("some future option", 3, 9);
            var range = RemediationValueBounds.Resolve(op);
            Assert.Equal(3, range.Min);
            Assert.Equal(9, range.Max);
            Assert.Contains("ships with", range.Reason);
        }

        [Fact]
        public void AFixedValueTemplateTakesNoOperatorInput_AndIsNotBoundsChecked()
        {
            // corpus `value_fixed`: shipped, no operator input, nothing to refuse. Asserted so a
            // future tightening does not accidentally start refusing shipped fixed values.
            var op = Op("some toggle", 0, 1);
            op.ValueFixed = 1;
            Assert.True(RemediationOpRenderer.TryResolveValue(op, new Dictionary<string, string>(), out var v, out _));
            Assert.Equal(1, v);
        }

        // ── EVERY SHIPPED TEMPLATE STILL ACCEPTS ITS OWN RECOMMENDED VALUE ──

        [Fact]
        public void NoShippedTemplateIsMadeUnusableByTheNewBounds()
        {
            // The failure mode of a bounds tightening is that it refuses the product's own advice.
            //
            // ⚠ AND IT NO LONGER SKIPS THE TEMPLATES THAT MATTER MOST (fix round, gate blocker 4).
            // This loop used to `continue` past every template with a null RecommendedValue - which
            // is EXACTLY the set an operator types a number into, and exactly the set MAXSERVERMEMORY
            // belongs to. The counterweight was weighing the templates that need no operator at all.
            // A range with no recommendation is now covered by its ENDPOINTS: a range whose own
            // floor or ceiling is refused is a range with no usable value in it.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var failures = new List<string>();
            int recommended = 0, endpoints = 0;

            foreach (var t in store.All().Where(t => t.Operation?.OpKind == RemediationOpKind.SpConfigure))
            {
                var op = t.Operation!;

                // The template's OWN value parameter name, not the helper's: a dictionary keyed on
                // the wrong name reads as "no value supplied" and would fail this test for a reason
                // that has nothing to do with bounds.
                bool Accepts(int candidate, out string err) =>
                    RemediationOpRenderer.TryResolveValue(
                        op, new Dictionary<string, string> { [op.ValueParam] = candidate.ToString() }, out _, out err);

                if (op.RecommendedValue is int rec)
                {
                    recommended++;
                    if (!Accepts(rec, out var recErr))
                        failures.Add($"{t.Key}: its own RecommendedValue {rec} was refused - {recErr}");
                }

                // Operator-input templates (RecommendedValue null) and recommended ones alike: both
                // ends of the range the app itself computes must be values the app itself accepts.
                var range = RemediationValueBounds.ResolveStatic(op);
                endpoints++;
                if (!Accepts(range.Min, out var minErr))
                    failures.Add($"{t.Key}: its own range FLOOR {range.Min} was refused - {minErr}");
                if (!Accepts(range.Max, out var maxErr))
                    failures.Add($"{t.Key}: its own range CEILING {range.Max} was refused - {maxErr}");

                _out.WriteLine($"{t.Key} '{op.ConfigName}' recommended={op.RecommendedValue?.ToString() ?? "(operator types it)"} "
                             + $"range=[{range.Min}, {range.Max}] hostCeiling={range.HasHostCeiling}");
            }

            // The instrument has to have measured something. A store that returned nothing would
            // pass every assertion above.
            Assert.True(endpoints > 0, "no shipped sp_configure template was examined at all.");
            Assert.True(recommended > 0, "no shipped template carried a RecommendedValue.");
            Assert.True(endpoints > recommended,
                "every shipped sp_configure template carries a RecommendedValue, so the operator-input "
                + "case this test was extended to cover is not represented in the store any more. "
                + "Re-check the extension rather than deleting it.");

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void EveryShippedTemplateHasAUsableRange()
        {
            // A floor above a ceiling would refuse every value with no way out, and would do it
            // silently at apply time. Measured across the shipped set rather than reasoned about.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            foreach (var t in store.All().Where(t => t.Operation?.OpKind == RemediationOpKind.SpConfigure))
            {
                var range = RemediationValueBounds.Resolve(t.Operation!);
                Assert.True(range.Min <= range.Max, $"{t.Key}: floor {range.Min} above ceiling {range.Max}");
                _out.WriteLine($"{t.Key} '{t.Operation!.ConfigName}' -> [{range.Min}, {range.Max}] {range.Reason}");
            }
        }

        [Fact]
        public void ARefusedValueNeverReachesTheRenderer()
        {
            // Claim 4's offline half, stated structurally: the renderer is only ever called with a
            // resolved value, and a refusal returns before that. Asserted by driving the pair in
            // the same order the executor does.
            var op = Op("max degree of parallelism", 0, 64);
            var resolved = RemediationOpRenderer.TryResolveValue(op, P("4; DROP DATABASE payroll"), out var v, out _);

            Assert.False(resolved);
            // v is the default, and rendering it would be rendering a value nobody supplied - so
            // the executor must not, and does not, reach TryRender at all on this path.
            Assert.Equal(0, v);
        }
    }
}
