/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Honesty-hunt 2026-08-25, Platform lane, Cluster B + Cluster E copy fixes. The About page must
/// state only what ships. These pin the removals so a well-meaning tidy-up cannot re-introduce a
/// security control the build never applies, a retention unit the code never keeps, or a collector
/// the app never runs. The explanatory <c>@* … *@</c> Razor comments left beside each removal are
/// stripped at compile time and never reach a client, so the assertions target the client-visible
/// claim strings, not the bare feature names.
/// </summary>
public class AboutPageHonestyTests
{
    private static string ReadMarkup(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
        File.Exists(path).Should().BeTrue(
            $"{fileName} is copied to the test output by SQLTriage.Tests.csproj; "
          + "if this fails every assertion below would vacuously pass");
        return File.ReadAllText(path);
    }

    // ── platform-r1-01: ConfuserEx2 obfuscation is claimed but never applied ──

    [Fact]
    public void The_about_page_does_not_claim_ConfuserEx2_obfuscation()
    {
        var markup = ReadMarkup("About.razor");

        markup.Should().NotContain("Assembly obfuscation (ConfuserEx2",
            "no build path applies ConfuserEx2, so the Security Hardening card must not list it");
        markup.Should().NotContain("Assembly protection via ConfuserEx2",
            "the Deployment section must not claim ConfuserEx2 assembly protection either");
        markup.Should().NotContain("<td>ConfuserEx2 Obfuscation</td>",
            "the Enterprise Security Architecture table must not carry a ConfuserEx2 row");

        // Control: the honest claims around the removals must survive, or the section was gutted
        // rather than corrected.
        markup.Should().Contain("AES-256-GCM credential encryption (DPAPI machine-key backed)",
            "the real credential-encryption claim stays");
    }

    [Fact]
    public void The_security_doc_does_not_claim_the_pipeline_runs_ConfuserEx2()
    {
        var doc = ReadMarkup("security.md");

        doc.Should().NotContain("runs ConfuserEx2 over the main assembly",
            "docs/security.md repeated the same false claim; the shipped assembly is not obfuscated");
        doc.Should().NotContain("production release pipeline runs ConfuserEx2",
            "no release pipeline performs ConfuserEx2");
    }

    // ── platform-r2-02: ProcessGuard is registered but never resolved ────────

    [Fact]
    public void The_about_page_does_not_list_process_integrity_validation()
    {
        var markup = ReadMarkup("About.razor");

        // ProcessGuard is a DI singleton nothing resolves, so its constructor never runs and its
        // checks never execute. Listing it as a shipped Security Hardening feature is a dead claim.
        markup.Should().NotContain("<li>Process integrity validation</li>",
            "ProcessGuard is never resolved, so 'Process integrity validation' does not ship");
    }

    // ── platform-r1-05: retention is by file count, not by day ───────────────

    [Fact]
    public void The_about_page_states_log_retention_as_a_file_count_not_days()
    {
        var markup = ReadMarkup("About.razor");

        // retainedFileCountLimit:30 with rollOnFileSizeLimit:true keeps the last 30 FILES, not 30
        // days — a busy day rolls to _001, _002 … each counting against the same 30-file cap.
        markup.Should().NotContain("30-day rolling",
            "retention is by file count; '30-day rolling' overstates the age a log survives to");
        markup.Should().Contain("last 30 log files",
            "the honest unit is the last 30 log files");
    }

    // ── platform-r1-08: no per-minute SQL Agent collection ships ─────────────

    [Fact]
    public void The_about_page_faq_does_not_claim_per_minute_sql_agent_collection()
    {
        var markup = ReadMarkup("About.razor");

        // The same page states three times that there is no agent and no collector database. The
        // FAQ's "every minute via SQL Agent jobs" was SQLWATCH-collector residue and contradicted it.
        markup.Should().NotContain("every minute via SQL Agent jobs",
            "SQLTriage ships no per-minute SQL Agent collection job");
        markup.Should().Contain("does not run a background collector",
            "the FAQ answer must state the real behaviour: live DMV reads, no scheduled collector");
    }
}
