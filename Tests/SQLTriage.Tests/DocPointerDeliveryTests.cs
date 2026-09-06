/* In the name of God, the Merciful, the Compassionate */

// THE DOC-POINTER CENSUS: every docs/ path this product shows an OPERATOR must be a file we
// actually put on their machine.
//
// WHY. Pages\AuditLogViewer.razor tells the reader to open
// docs/compliance/incident-response-runbook.md from four separate banners, and AuditLogService's
// chain-verification report body names the same file in its "Scope of this report" footer. The
// file was real in the repo and reached a client machine through NOTHING: .md is not in the SDK's
// default Content globs, so it was never in the publish output, and installer\SQLTriage.iss had no
// docs entry of any kind. Measured 2026-08-28 at 84beb1c, before the fix:
//
//     $ grep -ci docs installer/SQLTriage.iss
//     0
//
// Three of those four banners fire only when the audit chain is reported BROKEN, UNVERIFIABLE or
// INDETERMINATE. So the one moment the product sent the operator to the runbook was the one moment
// they most needed it, and the file was not there. Found independently by honesty-hunt
// strings-r1-13.
//
// THE TWO HONEST ENDINGS, AND WHICH ONE EACH POINTER GOT. A pointer to a missing file is closed
// either by shipping the file or by removing the pointer, and which one is right depends on who
// the file was written for:
//
//   * docs/compliance/** is written FOR the operator. The runbook carries {{primary-oncall}}
//     placeholders for the reader to fill in and maps itself to SOC2 CC7.x. It is a deliverable
//     that was not being delivered, so it is now SHIPPED (SQLTriage.csproj Content Include +
//     installer\SQLTriage.iss Source line), and the banners are left alone because they become
//     true.
//   * docs/design/documentation-generator.md is written FOR US. It is the implementation spec for
//     an unbuilt v2 feature. Shipping our build guide into a client install would be a worse
//     answer than the gap, so the POINTER was removed from Pages\Documentation.razor and
//     Pages\InstallationHelper.razor (banner text and the disabled buttons' hover titles).
//
// WHAT THIS FILE IS AND IS NOT. It reads the .iss and the .csproj AS TEXT and XML. It is a lint.
// It does not prove iscc compiled, and it does not prove the compiled installer put the folder on
// a disk; InstallerCompileLiveTests / InstallerScriptResolutionLiveTests are where that class of
// claim is measured. What this file does prove is the three-way agreement that was missing: a
// docs/ path shown to an operator, a csproj rule that copies it, and an .iss line that installs
// it. Do not read a green run here as "the operator can open the runbook" - read it as "nothing
// has silently gone back to promising a file we do not ship."
//
// EXEMPTIONS ARE NAMED ONE BY ONE, WITH A REASON, for the same cause ScriptCensusTests gives: a
// census with a pattern-shaped exemption stops being a census, because the next offender matches
// the pattern and nobody hears about it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests;

public class DocPointerDeliveryTests
{
    private static string RepoRoot() => FrkContractTests.RepoRoot();

    /// <summary>
    /// Source files whose text is rendered to, or printed for, an operator. A docs/ path appearing
    /// in any of these is a promise to that operator. Deliberately a named list rather than a
    /// whole-tree sweep: a docs/ path in an internal C# comment is a note to the next engineer and
    /// is none of this census's business, and a sweep could not tell the two apart.
    /// </summary>
    private static readonly string[] OperatorFacingSources =
    {
        Path.Combine("Pages", "AuditLogViewer.razor"),
        Path.Combine("Pages", "Documentation.razor"),
        Path.Combine("Pages", "InstallationHelper.razor"),
    };

    /// <summary>
    /// The one operator-facing docs/ pointer that is NOT in a .razor file. AuditLogService builds
    /// the chain-verification report body as text and appends the runbook path to its "Scope of
    /// this report" footer; that body is handed to the operator, so the promise counts.
    /// </summary>
    private const string ReportBodySource = "Data/AuditLogService.cs";

    /// <summary>
    /// Matches a docs/ path in prose: docs/ followed by path characters, ending in .md.
    /// </summary>
    private static readonly Regex DocsPathPattern =
        new(@"docs/[A-Za-z0-9._\-/]+\.md", RegexOptions.Compiled);

    /// <summary>A razor comment block, which the renderer strips and the operator never sees.</summary>
    private static readonly Regex RazorCommentBlock =
        new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Blanks a matched block while preserving its newline count, so nothing downstream
    /// silently joins two unrelated lines into one.</summary>
    private static string BlankButKeepNewlines(string block)
        => new string('\n', block.Count(c => c == '\n'));

    private static IReadOnlyList<(string source, string docPath)> OperatorFacingDocPointers()
    {
        var hits = new List<(string, string)>();

        foreach (var rel in OperatorFacingSources.Append(ReportBodySource.Replace('/', Path.DirectorySeparatorChar)))
        {
            var full = Path.Combine(RepoRoot(), rel);
            if (!File.Exists(full)) continue;

            // AuditLogService.cs is mostly internal code: only the strings it APPENDS to the
            // report body are operator-facing, so restrict that one file to those lines. Every
            // .razor line, by contrast, is markup the operator sees - EXCEPT razor comments, which
            // the renderer strips and never emits. Stripping @* *@ blocks first is not a
            // convenience: without it this census reads a note written to the next engineer as a
            // promise made to the client. It found its own author out that way on 2026-08-28 - the
            // comment explaining WHY the design-doc pointer was removed names the path it removed,
            // and three tests went red on it before this block existed.
            var isReportBody = rel.EndsWith("AuditLogService.cs", StringComparison.OrdinalIgnoreCase);

            var text = File.ReadAllText(full);
            if (rel.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                text = RazorCommentBlock.Replace(text, m => BlankButKeepNewlines(m.Value));

            foreach (var line in text.Split('\n'))
            {
                if (isReportBody && !line.Contains("body.AppendLine", StringComparison.Ordinal)) continue;
                if (line.TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;  // doc comment
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;   // code comment

                foreach (Match m in DocsPathPattern.Matches(line))
                    hits.Add((rel.Replace(Path.DirectorySeparatorChar, '/'), m.Value));
            }
        }

        return hits;
    }

    // ── 1. Every docs/ path we SHOW an operator is a file that exists ───────────────────────

    [Fact]
    public void Every_operator_facing_docs_pointer_names_a_file_that_exists_in_the_repo()
    {
        var pointers = OperatorFacingDocPointers();
        Assert.NotEmpty(pointers);   // a census that found nothing is measuring nothing

        var missing = pointers
            .Where(p => !File.Exists(Path.Combine(RepoRoot(), p.docPath.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(missing.Count == 0,
            "An operator-facing string names a docs/ file that is not in the repo at all:\n  "
            + string.Join("\n  ", missing.Select(p => $"{p.source} -> {p.docPath}")));
    }

    // ── 2. ...and is a file we PUT ON THEIR MACHINE ─────────────────────────────────────────

    [Fact]
    public void Every_operator_facing_docs_pointer_is_covered_by_a_csproj_copy_rule()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "SQLTriage.csproj"));

        var uncovered = OperatorFacingDocPointers()
            .Select(p => p.docPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => !CsprojCopiesToPublish(csproj, d))
            .ToList();

        Assert.True(uncovered.Count == 0,
            "These docs/ paths are shown to an operator but no SQLTriage.csproj rule copies them "
            + "into the publish output, so they are absent beside the installed binary:\n  "
            + string.Join("\n  ", uncovered)
            + "\n\nEither add a Content Include with CopyToPublishDirectory, or remove the pointer. "
            + "See this file's header for which ending belongs to which kind of document.");
    }

    [Fact]
    public void Every_operator_facing_docs_pointer_is_installed_by_the_inno_script()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "SQLTriage.iss"));

        // Only real Source: directives count. The .iss carries prose comments that mention docs
        // paths (including this fix's own rationale), and a comment installs nothing.
        var sourceLines = IssLogicalLines(iss)
            .Where(l => l.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var uncovered = OperatorFacingDocPointers()
            .Select(p => p.docPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => !sourceLines.Any(l => IssShips(l, d)))
            .ToList();

        Assert.True(uncovered.Count == 0,
            "These docs/ paths are shown to an operator but installer\\SQLTriage.iss has no Source "
            + "line that installs them, so an Inno-installed client does not have them:\n  "
            + string.Join("\n  ", uncovered)
            + "\n\nThis is the exact shape strings-r1-13 found: grep -ci docs SQLTriage.iss was 0 "
            + "while four audit banners told the operator to read the runbook.");
    }

    // ── 3. The unguarded Source line stays unguarded, and stays justified ───────────────────

    [Fact]
    public void The_compliance_source_line_is_deliberately_unguarded()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "SQLTriage.iss"));

        var line = IssLogicalLines(iss).FirstOrDefault(
            l => l.StartsWith("Source:", StringComparison.OrdinalIgnoreCase)
                 && l.Contains(@"docs\compliance", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(line);

        // The flags live on the continuation half of that line. Reading them proves this test is
        // looking at the whole directive, not just its first physical line.
        Assert.Contains("recursesubdirs", line, StringComparison.OrdinalIgnoreCase);

        // Deploy\ and BPScripts\ are wrapped in #if DirExists because they are legitimately absent
        // in some profile. docs\compliance is present in every profile, so wrapping it would
        // convert "the runbook stopped shipping" from a loud compile abort into a silent
        // regression to exactly the state r1-13 found. Assert the guard has not been "tidied" on.
        var guardedRegion = Regex.Match(
            iss,
            @"#if\s+DirExists\(AddBackslash\(SourceDir\)\s*\+\s*""docs[^""]*""\)",
            RegexOptions.IgnoreCase);

        Assert.False(guardedRegion.Success,
            "installer\\SQLTriage.iss now wraps the docs\\ Source line in #if DirExists. Do not. "
            + "That guard is what lets Deploy\\ and BPScripts\\ compile when a profile legitimately "
            + "drops them; docs\\ is in every profile, so the guard here would only hide the "
            + "regression. If a profile really does need to drop docs\\, delete this test and say "
            + "why in the same commit.");
    }

    [Fact]
    public void Nothing_gates_the_compliance_folder_out_of_any_build_profile()
    {
        // The unguarded decision above rests on a claim read from source: no build profile removes
        // docs\. This test reads the mechanism that could falsify it, the way
        // ScriptCensusTests.Nothing_gates_the_scripts_folder_out_of_any_build_profile does, and
        // goes red the moment such a mechanism appears - naming the .iss line that will break.
        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "buildprofile.targets"));

        var removals = Regex.Matches(targets, @"<Content\s+Remove=""([^""]+)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Where(v => v.StartsWith("docs", StringComparison.OrdinalIgnoreCase)
                        || v.Contains(@"docs\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(removals.Count == 0,
            "buildprofile.targets now removes docs content from a build profile:\n  "
            + string.Join("\n  ", removals)
            + "\n\nThe docs\\compliance Source line in installer\\SQLTriage.iss is UNGUARDED on the "
            + "claim that no profile does this. That profile's installer compile will now abort. "
            + "Either exclude the compliance folder from the removal, or guard the .iss line and "
            + "accept that this product can ship a build whose audit banners point at nothing.");
    }

    // ── 3b. What the folder glob ships is an allow-list, not whatever happens to be there ───

    /// <summary>
    /// The compliance pack ships WHOLESALE - one <c>docs\compliance\*.md</c> rule in the csproj and
    /// one recursing Source line in the .iss - so the delivery decision is made by the FOLDER, and
    /// every file in it is a file on a client machine. That is exactly right for the four documents
    /// below, and it was wrong for a fifth.
    ///
    /// <para>WHAT WENT IN WITH THEM, 2026-08-28. <c>DEPLOY-CHECKLIST.md</c> sat in the same folder
    /// and is written FOR US: it named the code-signing certificate and its expiry, recorded that
    /// the key is HSM-resident and non-exportable and that CI therefore cannot sign, gave the local
    /// signing command with its thumbprint switch, and pointed into <c>.handoff/</c>. Shipping the
    /// folder shipped that to every client. It was also a NEW disclosure and not, as first
    /// believed, already public: the public repository's copy of the same file (origin/main
    /// eacc2a7) carries the generic <c>CODESIGN_CERT_BASE64</c> line instead, and "Certum" appears
    /// nowhere under its <c>docs/</c>. The file moved to <c>.handoff/release-docs/</c>, following
    /// the 2026-05-26 precedent that took the release records out of <c>docs/</c> for the same
    /// reason.</para>
    ///
    /// <para>The list is named one by one on purpose, the way this file's header requires: a folder
    /// glob with no census is how the next internal document walks onto a client machine without
    /// anyone deciding it should.</para>
    /// </summary>
    [Fact]
    public void The_shipped_compliance_pack_is_exactly_the_documents_written_for_the_operator()
    {
        var folder = Path.Combine(RepoRoot(), "docs", "compliance");
        var shipped = Directory.GetFiles(folder, "*.md").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var intended = new[]
        {
            "access-review-procedure.md",       // {{placeholder}} template the buyer fills in
            "incident-response-runbook.md",     // what four audit banners send the operator to read
            "sign-off-log.md",                  // the runbook forwards to it at four steps
            "vendor-dependency-register.md",    // buyer-facing third-party register
        };

        Assert.Equal(intended, shipped);
    }

    /// <summary>
    /// The same guard by CONTENT rather than by filename, so a renamed internal document does not
    /// walk through the list above. Nothing that ships to a client may name our signing
    /// infrastructure or our internal handoff tree.
    /// </summary>
    [Fact]
    public void No_shipped_compliance_document_names_our_release_engineering_internals()
    {
        string[] markers = { "Certum", "SigningThumbprint", "CODESIGN_CERT", "HSM-resident", ".handoff" };

        var offenders = Directory
            .GetFiles(Path.Combine(RepoRoot(), "docs", "compliance"), "*.md")
            .SelectMany(f => markers
                .Where(m => File.ReadAllText(f).Contains(m, StringComparison.OrdinalIgnoreCase))
                .Select(m => Path.GetFileName(f) + " names " + m))
            .ToList();

        Assert.True(offenders.Count == 0,
            "docs\\compliance\\*.md ships into every client install and publishes wholesale to the "
            + "public site. These files carry release-engineering detail written for us:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nMove the document to .handoff\\release-docs\\, as DEPLOY-CHECKLIST.md was on "
            + "2026-08-28, rather than trimming the sentence and leaving the file where the glob "
            + "will pick up the next one.");
    }

    // ── 4. The internal design spec never comes back into operator-facing prose ─────────────

    [Fact]
    public void No_operator_facing_string_points_at_an_internal_design_document()
    {
        var offenders = OperatorFacingDocPointers()
            .Where(p => p.docPath.StartsWith("docs/design/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "An operator-facing string points at docs/design/, our internal implementation specs:\n  "
            + string.Join("\n  ", offenders.Select(p => $"{p.source} -> {p.docPath}"))
            + "\n\nThose files ship in no build and no installer, and they are written for us, not "
            + "for the reader. strings-r1-13 removed two of them from Pages\\Documentation.razor "
            + "and Pages\\InstallationHelper.razor. Say what is true to the operator instead.");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The .iss as LOGICAL lines: Inno continues a directive onto the next physical line with a
    /// trailing backslash, and every multi-line Source: directive in this file uses that form to
    /// put its Flags: on the second half. Splitting on '\n' alone would read a Source line whose
    /// recursesubdirs flag it cannot see, which is how a lint quietly stops measuring the thing it
    /// was written for.
    /// </summary>
    private static IReadOnlyList<string> IssLogicalLines(string iss)
    {
        var joined = new List<string>();
        var current = "";

        foreach (var raw in iss.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();

            if (line.EndsWith("\\", StringComparison.Ordinal))
            {
                current += line[..^1].TrimEnd() + " ";
                continue;
            }

            joined.Add((current + line).Trim());
            current = "";
        }

        if (current.Length > 0) joined.Add(current.Trim());
        return joined;
    }

    /// <summary>
    /// True when the csproj has an Include whose glob covers <paramref name="docPath"/> AND that
    /// item copies to the publish directory. Both halves matter: a Content Include with only
    /// CopyToOutputDirectory reaches a dev bin\ and never a client.
    /// </summary>
    private static bool CsprojCopiesToPublish(string csproj, string docPath)
    {
        var windows = docPath.Replace('/', '\\');
        var dir = windows.Contains('\\') ? windows[..windows.LastIndexOf('\\')] : "";

        foreach (Match m in Regex.Matches(csproj, @"<Content\s+Include=""([^""]+)""([^>]*)>?", RegexOptions.IgnoreCase))
        {
            var include = m.Groups[1].Value;
            var rest = m.Groups[2].Value;

            var covers =
                include.Equals(windows, StringComparison.OrdinalIgnoreCase)
                || (include.EndsWith(@"\*.md", StringComparison.OrdinalIgnoreCase)
                    && include[..^5].Equals(dir, StringComparison.OrdinalIgnoreCase))
                || (include.EndsWith(@"\**", StringComparison.OrdinalIgnoreCase)
                    && windows.StartsWith(include[..^3] + "\\", StringComparison.OrdinalIgnoreCase));

            if (covers && rest.Contains("CopyToPublishDirectory", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when an Inno <c>Source:</c> line installs <paramref name="docPath"/>, whether named
    /// exactly or covered by a recursing wildcard on one of its parent folders.
    /// </summary>
    private static bool IssShips(string sourceLine, string docPath)
    {
        var windows = docPath.Replace('/', '\\');

        var m = Regex.Match(sourceLine, @"Source:\s*""\{#SourceDir\}\\([^""]+)""", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        var pattern = m.Groups[1].Value;

        if (pattern.Equals(windows, StringComparison.OrdinalIgnoreCase)) return true;

        if (pattern.EndsWith(@"\*", StringComparison.OrdinalIgnoreCase))
        {
            var folder = pattern[..^2];
            var recurses = sourceLine.Contains("recursesubdirs", StringComparison.OrdinalIgnoreCase);

            if (windows.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var tail = windows[(folder.Length + 1)..];
                return recurses || !tail.Contains('\\');
            }
        }

        return false;
    }
}
