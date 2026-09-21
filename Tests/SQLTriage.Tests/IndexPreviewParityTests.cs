/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests;

// ── The previewed index statement must BE the executed one (F-B, ruled 2026-09-01) ──
//
// WHAT WAS WRONG. Two implementations of one substitution.
//   • QueryPlanModal.ApplyIndexOptions substituted the operator's live checkbox state on execute.
//   • queryPlanV2.js substituteDefaultOptions() substituted hardcoded literals for the sidebar
//     preview, the pane copy, the Copy DDL button and the recommendations banner:
//       ONLINE→OFF, SORT_IN_TEMPDB→ON, MAXDOP→0, DATA_COMPRESSION→NONE,
//       RESUMABLE→dropped, OPTIMIZE_FOR_SEQUENTIAL_KEY→dropped.
// The C# defaults are ONLINE ON / SORT_IN_TEMPDB ON / MAXDOP 8. So on a FRESHLY OPENED modal with
// nothing touched, the preview said ONLINE = OFF, MAXDOP = 0 and the statement that ran said
// ONLINE = ON, MAXDOP = 8. Always-on divergence on the default settings, not a corner case — and
// the preview is what the operator reads and pastes into a change ticket.
//
// HOW IT IS FIXED, AND WHAT THAT MEANS FOR TESTING IT. The JS copy is deleted. The preview calls
// back into QueryPlanModal.PreviewIndexDdl → ApplyIndexOptions → IndexOptionSubstitution.Apply —
// the same call execute makes. So byte-for-byte parity is not a property to be checked; it is
// structural, and a test asserting "preview == executed" would be asserting f(x) == f(x).
//
// The two things worth testing are therefore:
//   (A) the single implementation is CORRECT and TOTAL — no option combination leaves a
//       placeholder behind, and each option lands as the value chosen. Exercised below across
//       every combination.
//   (B) there is still only ONE implementation — the JS has not grown a second copy and the
//       call chain has not been short-circuited. Source guards, each with a control measured
//       against the real pre-fix text.
public class IndexPreviewParityTests
{
    /// <summary>
    /// The template ExecutionPlanParser emits, verbatim in its runtime form. Note the SINGLE
    /// braces: the parser's source writes <c>$"... {{ONLINE}} ..."</c>, which is an interpolated
    /// string, so <c>{{</c> is an escape and the emitted text carries one brace. A prior reading of
    /// that source as "the parser emits double braces" is what put a dead double-brace pass into
    /// the substitution.
    /// </summary>
    private const string ParserTemplate =
        "CREATE NONCLUSTERED INDEX [IX_probe]\n"
        + "    ON [db].[dbo].[t] ([a], [b])\n"
        + "    INCLUDE ([c])\n"
        + "    WITH (\n"
        + "        ONLINE = {ONLINE},\n"
        + "        SORT_IN_TEMPDB = {SORT_IN_TEMPDB},\n"
        + "        MAXDOP = {MAXDOP},\n"
        + "        DATA_COMPRESSION = {COMPRESSION}\n"
        + "        {RESUMABLE}\n"
        + "        {SEQUENTIAL_KEY}\n"
        + "    );\n"
        + "    -- Estimated query improvement: 42%\n"
        + "    -- Automatically generated from execution plan recommendation";

    /// <summary>The shape queryPlanV2.js builds, for the same coverage on the other template.</summary>
    private const string JsTemplate =
        "CREATE NONCLUSTERED INDEX [IX_probe]\nON [db].[dbo].[t]\n(\n  [a]\n  ,[b]\n)\n"
        + "INCLUDE([c])\n"
        + "WITH (ONLINE = {ONLINE}, SORT_IN_TEMPDB = {SORT_IN_TEMPDB}, MAXDOP = {MAXDOP}, "
        + "DATA_COMPRESSION = {COMPRESSION}{RESUMABLE}{SEQUENTIAL_KEY})";

    /// <summary>
    /// Every combination of the four booleans, across both templates and a MAXDOP/compression pair.
    /// 2^4 x 2 templates x 2 numeric/compression settings = 64 cases — small enough to enumerate
    /// and the only way to catch "correct for the defaults, wrong for one tick".
    /// </summary>
    public static IEnumerable<object[]> EveryOptionCombination()
    {
        foreach (var template in new[] { ParserTemplate, JsTemplate })
            foreach (var online in new[] { true, false })
                foreach (var sort in new[] { true, false })
                    foreach (var resumable in new[] { true, false })
                        foreach (var seqKey in new[] { true, false })
                            foreach (var (maxDop, compression) in new[] { (8, "NONE"), (1, "PAGE") })
                            {
                                yield return new object[]
                                {
                                    template,
                                    new IndexOptions(online, sort, resumable, maxDop, compression, seqKey)
                                };
                            }
    }

    // ── (A) The single implementation is correct and total ──────────────────────

    [Theory]
    [MemberData(nameof(EveryOptionCombination))]
    public void No_option_combination_leaves_a_placeholder_behind(string template, IndexOptions options)
    {
        var result = IndexOptionSubstitution.Apply(template, options);

        foreach (var name in IndexOptionSubstitution.PlaceholderNames)
        {
            result.Should().NotContain("{" + name + "}",
                $"an unsubstituted {{{name}}} is sent to the server verbatim and fails at parse time.");
            result.Should().NotContain("{{" + name + "}}",
                $"a half-substituted {{{{{name}}}}} is the same failure with a longer fuse.");
        }

        // Nothing brace-shaped may survive at all — a NEW placeholder added to a template without
        // being added to the substitution would otherwise slip through the loop above.
        Regex.IsMatch(result, @"\{[A-Z_]+\}").Should().BeFalse(
            "a brace-delimited upper-case token surviving substitution is an option nobody wired up. "
            + $"Result was:\n{result}");
    }

    [Theory]
    [MemberData(nameof(EveryOptionCombination))]
    public void Each_option_lands_as_the_value_that_was_chosen(string template, IndexOptions options)
    {
        var result = IndexOptionSubstitution.Apply(template, options);

        result.Should().Contain($"ONLINE = {(options.Online ? "ON" : "OFF")}");
        result.Should().Contain($"SORT_IN_TEMPDB = {(options.SortInTempDb ? "ON" : "OFF")}");
        result.Should().Contain($"MAXDOP = {options.MaxDop}");
        result.Should().Contain($"DATA_COMPRESSION = {options.Compression}");

        var seqKeyPresent = result.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON", StringComparison.Ordinal);
        seqKeyPresent.Should().Be(options.OptimizeForSequentialKey);
    }

    [Theory]
    [MemberData(nameof(EveryOptionCombination))]
    public void Resumable_is_emitted_only_when_online_is_also_on(string template, IndexOptions options)
    {
        // SQL Server rejects RESUMABLE = ON with ONLINE = OFF, and the RESUMABLE checkbox is
        // DISABLED rather than cleared when ONLINE comes off — so it can hold a stale true. The
        // substitution, not the UI, is what has to be right about this.
        var result = IndexOptionSubstitution.Apply(template, options);

        var resumablePresent = result.Contains("RESUMABLE = ON", StringComparison.Ordinal);
        resumablePresent.Should().Be(options.Resumable && options.Online,
            "RESUMABLE belongs in the statement exactly when the operator asked for it AND online "
            + "is on. Any other combination is either a rejected statement or a silently dropped option.");
    }

    [Fact]
    public void Trailing_comment_lines_are_stripped_and_the_statement_ends_in_one_semicolon()
    {
        var result = IndexOptionSubstitution.Apply(ParserTemplate, Defaults);

        result.Should().NotContain("-- Estimated query improvement",
            "the parser appends explanatory comment lines; the executed and previewed statement drops them.");
        result.Should().EndWith(";");
        result.Should().NotEndWith(";;");
    }

    [Fact]
    public void The_defaults_produce_the_values_the_old_js_preview_contradicted()
    {
        // This is the divergence itself, pinned as a value assertion. The old JS preview printed
        // ONLINE = OFF and MAXDOP = 0 for exactly this state.
        var result = IndexOptionSubstitution.Apply(JsTemplate, Defaults);

        result.Should().Contain("ONLINE = ON");
        result.Should().Contain("SORT_IN_TEMPDB = ON");
        result.Should().Contain("MAXDOP = 8");
        result.Should().Contain("RESUMABLE = ON");
    }

    /// <summary>QueryPlanModal's initial field values, kept here so the assertion above is legible.</summary>
    private static readonly IndexOptions Defaults =
        new(Online: true, SortInTempDb: true, Resumable: true, MaxDop: 8, Compression: "NONE",
            OptimizeForSequentialKey: false);

    [Fact]
    public void Substitution_survives_a_null_or_empty_template()
    {
        IndexOptionSubstitution.Apply(null, Defaults).Should().Be(";");
        IndexOptionSubstitution.Apply(string.Empty, Defaults).Should().Be(";");
    }

    // ── (B) There is still only ONE implementation ──────────────────────────────

    [Fact]
    public void The_javascript_no_longer_substitutes_option_literals_of_its_own()
    {
        var js = ReadRepoFile("wwwroot", "scripts", "queryPlanV2.js");
        var code = StripJsCommentLines(js);

        code.Should().NotContain("substituteDefaultOptions",
            "the second implementation is deleted, not merely unused. A retained copy is a copy "
            + "somebody wires back up.");

        // The pre-fix idiom: a .replace() of an option token with a literal. Matching the idiom
        // rather than the specific literals, so re-introducing it with DIFFERENT values still fails.
        foreach (var token in IndexOptionSubstitution.PlaceholderNames)
        {
            Regex.IsMatch(code, @"replace\s*\(\s*/\\\{[^/]*" + token).Should().BeFalse(
                $"queryPlanV2.js must not substitute {token} itself. The preview asks .NET for the "
                + "substituted text so there is one set of values, not two that have to be kept in step.");
        }
    }

    [Fact]
    public void The_javascript_asks_dotnet_for_the_substituted_preview()
    {
        var code = StripJsCommentLines(ReadRepoFile("wwwroot", "scripts", "queryPlanV2.js"));

        code.Should().Contain("'PreviewIndexDdl'",
            "the preview text has to come from the C# substitution. Without this call the JS is "
            + "either guessing or showing a raw template.");
        code.Should().Contain("refreshOptionPreviews",
            "a preview that is only correct until the operator ticks a box is the same defect one "
            + "click later.");
    }

    [Fact]
    public void The_dotnet_preview_callback_delegates_to_the_execute_path_substitution()
    {
        var razor = ReadRepoFile("Components", "Shared", "QueryPlanModal.razor");
        var code = StripCsCommentLines(razor);

        code.Should().Contain("public string PreviewIndexDdl",
            "the JS callback target must exist under exactly this name.");
        code.Should().MatchRegex(@"PreviewIndexDdl\([^)]*\)\s*=>\s*ApplyIndexOptions\(",
            "the preview must be produced BY ApplyIndexOptions. A second substitution here — even a "
            + "correct one — is the defect F-B removed.");
        code.Should().MatchRegex(@"ApplyIndexOptions\(string ddl\)\s*=>\s*[\w\.]*IndexOptionSubstitution\.Apply\(",
            "ApplyIndexOptions must delegate to the shared substitution, or the combination coverage "
            + "above measures a copy the app does not run.");
        code.Should().Contain("var ddl     = ApplyIndexOptions(rawDdl);",
            "the execute path must still substitute through the same method the preview uses.");
    }

    [Fact]
    public void The_dotnet_reference_is_registered_unconditionally_so_the_preview_can_resolve()
    {
        // The ref used to be registered only in No-Pants mode, because its only job was execution.
        // The preview needs it for every reader of the plan sidebar. If it goes back behind the
        // No-Pants condition, the preview silently falls back to showing raw templates.
        var code = StripCsCommentLines(ReadRepoFile("Components", "Shared", "QueryPlanModal.razor"));

        var setRefAt = code.IndexOf("queryPlanInteropV2.setDotNetRef", StringComparison.Ordinal);
        setRefAt.Should().BeGreaterThan(0, "the registration call must still exist.");

        // The 400 characters before the call must not gate it on No-Pants mode.
        var window = code.Substring(Math.Max(0, setRefAt - 400), Math.Min(400, setRefAt));
        window.Should().NotContain("if (UserSettings.GetNoPantsMode()",
            "registering the ref only in No-Pants mode leaves every other reader with an unresolved "
            + "preview. Execution is gated inside ExecuteIndexFromOperator, which is where the gate belongs.");
    }

    // ── Controls: the source guards must still be able to see the defect ────────

    [Fact]
    public void Control_the_js_guard_flags_the_real_pre_fix_function()
    {
        // The verbatim pre-fix body. If the guard above cannot flag this, it protects nothing.
        const string PreFixJs = @"
    function substituteDefaultOptions(ddl) {
        return (ddl || '')
            .replace(/\{\{?ONLINE\}\}?/g, 'OFF')
            .replace(/\{\{?SORT_IN_TEMPDB\}\}?/g, 'ON')
            .replace(/\{\{?MAXDOP\}\}?/g, '0')
            .replace(/\{\{?COMPRESSION\}\}?/g, 'NONE')
            .replace(/\{\{?RESUMABLE\}\}?/g, '')
            .replace(/\{\{?SEQUENTIAL_KEY\}\}?/g, '');
    }";

        var code = StripJsCommentLines(PreFixJs);

        code.Should().Contain("substituteDefaultOptions",
            "the name guard must flag the function it was written for.");

        var flagged = IndexOptionSubstitution.PlaceholderNames
            .Count(token => Regex.IsMatch(code, @"replace\s*\(\s*/\\\{[^/]*" + token));
        flagged.Should().Be(IndexOptionSubstitution.PlaceholderNames.Length,
            "the idiom guard must flag ALL SIX of the pre-fix replacements. Flagging fewer means it "
            + "would let a partial re-introduction through.");
    }

    [Fact]
    public void Control_the_delegation_guard_flags_a_second_substitution_in_the_component()
    {
        // A PreviewIndexDdl that substitutes on its own instead of calling ApplyIndexOptions.
        const string DivergentSource = @"
    public string PreviewIndexDdl(string? ddl) => (ddl ?? string.Empty).Replace(""{ONLINE}"", ""OFF"");
";
        var code = StripCsCommentLines(DivergentSource);

        Regex.IsMatch(code, @"PreviewIndexDdl\([^)]*\)\s*=>\s*ApplyIndexOptions\(").Should().BeFalse(
            "if this control goes green, the delegation guard has stopped detecting a second "
            + "substitution and the guard above it is worthless.");
    }

    [Fact]
    public void Control_the_file_reader_refuses_to_judge_a_missing_file()
    {
        var act = () => ReadRepoFile("wwwroot", "scripts", "no-such-file.js");
        act.Should().Throw<Exception>(
            "a missing file must throw, not return empty and let every source guard report clean.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("These guards read app source, so they need the repo root.");

        var path = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
        if (!File.Exists(path))
            throw new FileNotFoundException($"The file under guard was not at {path}.", path);
        return File.ReadAllText(path);
    }

    // Comment lines are excluded because both files now DOCUMENT the pre-fix idiom in prose, and
    // prose about a defect is not the defect.
    private static string StripJsCommentLines(string source) =>
        string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string StripCsCommentLines(string source) =>
        string.Join("\n", source.Split('\n').Where(l =>
        {
            var t = l.TrimStart();
            return !t.StartsWith("//", StringComparison.Ordinal)
                && !t.StartsWith("///", StringComparison.Ordinal)
                && !t.StartsWith("@*", StringComparison.Ordinal);
        }));
}
