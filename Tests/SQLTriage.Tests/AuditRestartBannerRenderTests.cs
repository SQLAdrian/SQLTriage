/* In the name of God, the Merciful, the Compassionate */

// ── The RESTARTED banner, RENDERED, 2026-08-14 ───────────────────────────────────────────────
//
// ⚠⚠ WHY THIS FILE EXISTS. dev b57c34b made RESTARTED a first-class chain verdict and added two
// surfaces for it on Pages/AuditLogViewer.razor: a startup banner behind AuditLog.ChainRestarted,
// and an amber arm in the on-demand Verify Chain banner's colour and icon switches. AuditChain-
// RestartAndKeyCustodyTests proves the VERDICT thoroughly -- Status, StatusLabel, RestartCount,
// BrokenCount, the detail sentence. Nobody had ever looked at the markup those verdicts produce.
//
// That gap is not academic. Everything this lane's banner has to get right lives in the RAZOR and
// not in the service: which of the four banner blocks fires, whether it is the red role="alert"
// one or the amber role="status" one, and whether the composed headline and closing actually reach
// the page or are dropped by a null-conditional that silently renders nothing. StartupBannerFor
// returns a nullable, and `@(restartClaim?.Headline)` over a null renders the EMPTY STRING -- so a
// banner whose composer stopped answering would render as a bare icon and an unbroken green suite.
//
// WHAT IS REAL HERE. The chain is planted by the PRODUCTION WRITER with no hand-editing of any
// entry -- the same recipe AuditChainRestartAndKeyCustodyTests uses, which reproduces the live
// accident: a service started over a directory whose segment it cannot see seeds an empty tail, so
// the entry it writes declares PreviousHash="", and those lines are then appended after the hidden
// ones. The service is the real AuditLogService over those bytes. The component is the REAL
// Pages/AuditLogViewer, rendered by the real Blazor HtmlRenderer through the real DI graph.
//
// WHAT IS NOT. HtmlRenderer is a STATIC renderer: it cannot dispatch a click, so the on-demand
// Verify Chain banner -- which only appears after RunVerifyChain sets _verifyResult -- is not
// reachable from here. Its RESTARTED arm is covered by the two switch expressions being asserted
// against the bound enum below, which is a weaker instrument and is labelled as one. The startup
// banner needs no interaction and is genuinely rendered.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public sealed class AuditRestartBannerRenderTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _dirs = new();

    public AuditRestartBannerRenderTests(ITestOutputHelper output) => _out = output;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "_sqlt_smalls_banner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* a locked segment is not a test failure */ }
        }
    }

    /// <summary>Names the directory the planting utility writes into. See that method.</summary>
    private const string PlantDirVariable = "SQLT_RESTART_PLANT_DIR";

    private static AuditLogService Open(string dir) => new(dir, startFlushTimer: false);

    private static string[] Segments(string dir)
        => Directory.GetFiles(dir, "audit-*.jsonl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

    private static List<string> Lines(string path)
        => File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

    private static AuditLogEntry Parse(string line)
        => JsonSerializer.Deserialize<AuditLogEntry>(line, Json)!;

    /// <summary>
    /// Plants the live mid-segment restart with the PRODUCTION writer, no hand-editing.
    ///
    /// <para>⚠ THE DISCRIMINATOR IS THE DECLARED LINK, and it is what makes this a RESTART rather
    /// than a tamper: the resuming entry declares an EMPTY previous-hash and its own signature
    /// verifies against that link. A deletion also leaves a following entry whose declared link is
    /// not the one the walk carried in — the difference is that a deletion names a link that is
    /// nowhere in the chain, and that stays BROKEN. Both cases are asserted in
    /// AuditChainRestartAndKeyCustodyTests; this file renders the benign one.</para>
    /// </summary>
    private static string PlantRestartedChain(string dir)
    {
        using (var first = Open(dir))
        {
            first.LogConnectionAttempt("srv1", success: true);
            first.LogConnectionAttempt("srv2", success: true);
            first.Flush();
        }

        var segment = Segments(dir)[^1];
        var beforeRestart = Lines(segment);

        // Not "audit-*.jsonl", so the next service sees an empty chain and seeds an empty tail.
        var hidden = Path.Combine(dir, "hidden-segment.hold");
        File.Move(segment, hidden);

        using (var second = Open(dir))
        {
            second.LogConnectionAttempt("srv3", success: true);
            second.Flush();
        }

        var afterRestart = Lines(Segments(dir)[^1]);
        Parse(afterRestart[0]).PreviousHash.Should().BeEmpty(
            "the resuming entry must declare an EMPTY link, or this fixture is not a restart and "
            + "every assertion below is about some other state");

        File.WriteAllLines(segment, beforeRestart.Concat(afterRestart));
        File.Delete(hidden);
        return segment;
    }

    /// <summary>Renders the REAL page component through the real HtmlRenderer and DI graph.</summary>
    private static async Task<string> RenderAuditPageAsync(AuditLogService audit, string role)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
        services.AddSingleton(audit);
        services.AddSingleton<HostEnvironmentInfo>();
        services.AddSingleton<RbacService>();
        services.AddScoped<AppUserState>();

        await using var provider = services.BuildServiceProvider();

        // The page's own gate is UserState.IsAuthorized("settings") (ALIGNED 2026-08-15; it read
        // the static RbacService.HasPermission until then). Planting the role is the same seam the
        // WPF desktop uses; without it the page renders AccessDenied and the banner assertions
        // would pass by never running — which is exactly why the marker assertion below is not
        // optional. Note this container registers the DEFAULT HostEnvironmentInfo, i.e. the
        // desktop, so the gate short-circuits to allow and these tests are about BANNERS, not
        // authorization. The gate itself is driven on both axes in AuditLogViewerGateTests.
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppUserState>().SetRole(role);

        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggerFactory);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SQLTriage.Pages.AuditLogViewer>();
            return output.ToHtmlString();
        });
    }

    /// <summary>Strips tags and collapses whitespace, so an assertion reads the words a person does.</summary>
    private static string VisibleText(string html)
        => Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ").Trim();

    // ── The render ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_restarted_chain_renders_its_own_banner_and_not_the_tamper_alert()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        PlantRestartedChain(dir);

        using var audit = Open(dir);

        // The precondition, asserted rather than assumed. Every absence assertion below would pass
        // over a chain that was simply INTACT, which is the vacuous-green shape this file is here
        // to avoid in the first place.
        audit.ChainRestarted.Should().BeTrue("the planted chain must actually be in the state under test");
        audit.ChainBroken.Should().BeFalse("a restart is not a break");
        audit.ChainProvenanceIndeterminate.Should().BeFalse();
        audit.ChainUnverifiable.Should().BeFalse();

        var html = await RenderAuditPageAsync(audit, AppRoles.Admin);
        var text = VisibleText(html);
        _out.WriteLine(text);

        // The page rendered at all, rather than the access-denied shell.
        html.Should().Contain("audit-log-page",
            "if the RBAC gate refused, every assertion below would be about an empty page");
        html.Should().NotContain("audit-log-denied");

        // ── The banner is the AMBER one ──
        // role="status"/aria-live="polite", not role="alert"/aria-live="assertive". This is not
        // cosmetic: the assertive alert is what a screen reader interrupts the user for, and until
        // b57c34b a restart raised exactly that, over three restarts the previous build had caused
        // itself on the live service.
        html.Should().Contain("status-message warning",
            "a resumption point is not an integrity failure and must not take the error styling");
        html.Should().Contain("fa-link-slash");
        html.Should().NotContain("status-message error",
            "no red banner may render beside a chain whose only finding is a restart");
        html.Should().NotContain("role=\"alert\"",
            "the assertive alert is the tamper channel");
    }

    /// <summary>
    /// THE WORDS. Both the headline and the closing are COMPOSED by AuditLogService against every
    /// finding on the same launch — no chain-wide sentence lives in that markup — and both reach
    /// the page through a null-conditional. <c>@(restartClaim?.Headline)</c> over a null renders
    /// the empty string, so a composer that stopped answering would produce a banner with an icon,
    /// a record id and no sentence at all, and no existing test would have noticed.
    /// </summary>
    [Fact]
    public async Task The_rendered_restart_banner_carries_the_composed_sentences_and_no_tampering_language()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        PlantRestartedChain(dir);

        using var audit = Open(dir);
        var claim = audit.StartupBannerFor(AuditLogService.StartupChainFinding.Restart);
        claim.Should().NotBeNull("the composer must answer for the finding the chain actually holds");

        var text = VisibleText(await RenderAuditPageAsync(audit, AppRoles.Admin));

        // Composed, not literal: the page must render what the service composed, verbatim.
        text.Should().Contain(claim!.Headline,
            "the headline is composed against every finding on this launch, and a null-conditional "
            + "over a missing claim renders nothing at all");
        text.Should().Contain(claim.ClosingLead);
        text.Should().Contain(claim.ClosingDetail);

        // The restart is NAMED with the record an operator would quote, and never as "unknown"
        // over a chain that knows perfectly well where it resumed.
        audit.ChainRestartFirstRecordId.Should().NotBeNullOrWhiteSpace();
        text.Should().Contain(audit.ChainRestartFirstRecordId!);
        text.Should().Contain("First restart point:");

        // ── NO ACCUSATION. The whole point of the state. ──
        //
        // ⚠ THE FIRST CUT OF THIS ASSERTION WAS WRONG AND THE PRODUCT WAS RIGHT. It forbade the
        // word "tampering" outright, and went red on the banner's own sentence "it is not evidence
        // of tampering" — a DENIAL, which is the most valuable clause on the banner. Forbidding a
        // word is not the property; making an accusation is. So the list below is the accusing
        // PHRASES, and the denial is asserted PRESENT rather than merely tolerated.
        foreach (var accusation in new[]
                 {
                     "Treat as tampering",
                     "integrity check failed",
                     "FAILED verification",
                     "was altered",
                     "may have been altered",
                 })
            text.Should().NotContainEquivalentOf(accusation,
                $"a restart is an accident the product caused itself; \"{accusation}\" is the "
                + "playbook for a different finding");

        text.Should().ContainEquivalentOf("not evidence of tampering",
            "the banner must deny the accusation out loud. An operator who has been handed a red "
            + "chain alert before reads any chain finding as one until told otherwise, which is "
            + "what happened on the live service over three restarts this build caused itself");
        text.Should().ContainEquivalentOf("not a clean result",
            "...and it must not swing the other way either: a restart IS a loss of contiguity, so "
            + "the banner may not read as an all-clear");
    }

    /// <summary>
    /// THE NEGATIVE CONTROL. Without it the two tests above could be passing because the page never
    /// renders a red banner for anything, rather than because a restart does not earn one. Same
    /// page, same renderer, a genuinely BROKEN chain: the red assertive alert must appear.
    /// </summary>
    [Fact]
    public async Task A_genuinely_broken_chain_still_renders_the_red_alert_on_the_same_page()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();

        using (var first = Open(dir))
        {
            for (var i = 0; i < 4; i++) first.LogConnectionAttempt($"srv{i}", success: true);
            first.Flush();
        }

        // Remove an entry from the middle. The following entry then declares a link that is NOWHERE
        // in the chain, which is the discriminator that separates a deletion from a restart.
        var segment = Segments(dir)[^1];
        var lines = Lines(segment);
        File.WriteAllLines(segment, lines.Where((_, i) => i != 1));

        using var audit = Open(dir);
        audit.ChainBroken.Should().BeTrue(
            "the fixture must actually be broken, or this control asserts nothing");

        var html = await RenderAuditPageAsync(audit, AppRoles.Admin);

        html.Should().Contain("status-message error",
            "a chain with a real break earns the red alert the restart banner must not take");
        html.Should().Contain("role=\"alert\"");
        html.Should().Contain("fa-triangle-exclamation");
    }

    /// <summary>
    /// The on-demand Verify Chain banner's RESTARTED arm.
    ///
    /// <para>⚠ THIS IS THE WEAKER INSTRUMENT AND IT IS LABELLED AS ONE. That banner renders only
    /// after RunVerifyChain sets _verifyResult, which needs a CLICK, and HtmlRenderer is a static
    /// renderer that cannot dispatch one. So this asserts the two switch expressions the markup
    /// reads — through the page's own source, since they are private — plus the verdict they switch
    /// on. It does not prove the pixels. The state word itself IS proved end to end: the banner
    /// interpolates <c>_lastVerification.StatusWord</c>, and the folded statement's word is
    /// asserted against a real restarted chain here.</para>
    /// </summary>
    [Fact]
    public void The_verify_chain_banner_maps_a_restart_to_the_amber_arm_and_says_RESTARTED()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        PlantRestartedChain(dir);

        using var audit = Open(dir);
        var statement = audit.DescribeChainForCompliance("banner-render-test");

        statement.Status.Should().Be(AuditLogService.ChainVerificationStatus.Restarted);
        statement.StatusWord.Should().Be("RESTARTED",
            "the banner interpolates this verbatim as \"Chain {StatusWord}.\", so it is the state "
            + "word an operator reads");
        // The accusing phrase, not the word: the detail's own "A restart is not evidence of
        // tampering" is a denial and is the sentence that matters most on it.
        statement.Detail.Should().NotContainEquivalentOf("Treat as tampering");
        statement.Detail.Should().ContainEquivalentOf("not evidence of tampering");

        var markup = File.ReadAllText(Path.Combine(FindRepoRoot(), "Pages", "AuditLogViewer.razor"));
        markup.Should().Contain(
            "AuditLogService.ChainVerificationStatus.Restarted => \"warning\"",
            "amber, beside the evidence gap and NOT with the tamper verdicts");
        markup.Should().Contain(
            "AuditLogService.ChainVerificationStatus.Restarted => \"fa-link-slash\"");
        markup.Should().Contain("Chain {_lastVerification.StatusWord}.",
            "the banner must print the typed verdict's own word rather than composing a second one");
    }

    /// <summary>
    /// PLANTS a restarted chain into a directory the caller names, and leaves it there — the only
    /// thing in this file that does not clean up after itself, deliberately.
    ///
    /// <para>WHY IT IS HERE. The renders above drive the page component directly. The banner also
    /// has to survive the SHIPPED HOST: its own DI graph, its own RBAC resolution, its own
    /// prerender pass. Proving that needs the real application pointed at a real restarted chain,
    /// and the chain has to be planted by the production writer or it is not the state under test.
    /// This is that planting step, gated on its own variable so an ordinary run never touches a
    /// directory outside the temp tree.</para>
    ///
    /// <para>INVOCATION (gate). The application resolves its audit directory as
    /// <c>AppContext.BaseDirectory/audit-logs</c>, so the target is the build output's:</para>
    /// <code>
    ///   $env:SQLT_RESTART_PLANT_DIR = "…\bin\Release\net10.0-windows\win-x64\audit-logs"
    ///   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~Plant_a_restarted_chain"
    /// </code>
    /// <para>⚠ The directory must be EMPTY of segments first. Planting over an existing chain
    /// appends a second, unrelated history and the verdict then describes both.</para>
    /// </summary>
    [LiveFact(PlantDirVariable)]
    public void Plant_a_restarted_chain_for_a_live_render()
    {
        // ⚠ THE FIRST CUT OF THIS WAS A PLAIN [Fact] WITH AN ARMING EARLY-RETURN, and
        // Portal/LiveHarnessArmingCensusTests went red on it within the hour — its structural
        // census walks the whole test tree for exactly that shape, because an unarmed harness that
        // returns quietly is counted as a PASS inside every later "suite green" claim. The
        // attribute reports SKIPPED at discovery instead, and the assertion below is the second
        // half of the guard: if the attribute is ever weakened, this body FAILS rather than
        // planting into an empty path or passing vacuously.
        var target = Environment.GetEnvironmentVariable(PlantDirVariable);
        Assert.False(string.IsNullOrWhiteSpace(target),
            $"{PlantDirVariable} is not set, so this utility has nowhere to plant and nothing to "
            + "assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that attribute "
            + "is no longer doing its job.");

        Directory.CreateDirectory(target!);
        Segments(target!).Should().BeEmpty(
            $"'{target}' already holds audit segments. Planting over an existing chain appends a "
            + "second, unrelated history, and the verdict would then describe both.");

        var segment = PlantRestartedChain(target!);

        using var audit = Open(target!);
        audit.ChainRestarted.Should().BeTrue("the planted chain must be in the state under test");
        audit.ChainBroken.Should().BeFalse();

        _out.WriteLine($"planted {segment}");
        _out.WriteLine($"restart record: {audit.ChainRestartFirstRecordId}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pages", "AuditLogViewer.razor")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repo root above " + AppContext.BaseDirectory
            + ". This assertion reads the page source on purpose (the two switches are private). "
            + "If the suite runs somewhere without sources it must FAIL rather than pass silently.");
    }
}
