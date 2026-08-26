/* In the name of God, the Merciful, the Compassionate */

// ── The PROVENANCE-INDETERMINATE and UNVERIFIABLE banners, RENDERED, 2026-08-20 ─────────────────
//
// ⚠⚠ WHY THIS FILE EXISTS. AuditRestartBannerRenderTests (2026-08-14) proved the RESTARTED startup
// banner through the real Blazor renderer. Two siblings on the same page — the
// ChainProvenanceIndeterminate banner (Pages/AuditLogViewer.razor:51-101) and the ChainUnverifiable
// banner (:102-124) — had never been exercised the same way. Both are set deep inside
// AuditLogService's startup verification (ChainProvenanceIndeterminate at ~1286 via a ledger-less
// unresolvable key run, and ~3249 via FlagAnchorIndeterminate on an unreadable out-of-band anchor;
// ChainUnverifiable at ~1298-1300 via a corroborated benign key loss), and both interpolate a
// composer's output through the same nullable-conditional pattern the RESTARTED file documented:
// a composer that stopped answering renders an icon and no sentence, and no prior test would notice.
//
// WHAT IS REAL HERE. Both fixtures below are planted with the PRODUCTION WRITER and production
// mechanics only — no audit-log entry is ever hand-edited. The indeterminate fixture reproduces the
// live 2026-08-01 incident's own shape (AuditLogServiceTests.UnreadableChainAnchor_IsIndeterminate_
// NotTampering carries the same recipe): a genuine chain, then the out-of-band `.chain-anchor`
// sidecar overwritten with a DPAPI blob this identity cannot unwrap — exactly what a service-account
// change produces, and exactly the "orphaned/unreadable key situation" the anchor's own doc comment
// describes. The unverifiable fixture reproduces AuditLogServiceTests.
// KeyReplaced_OldEntriesBecomeUnverifiable_NotBroken: genuine key material loss (the HMAC key file
// replaced, its key archives deleted) with the key-provenance ledger left intact, which is the
// benign-key-loss verdict the ledger corroborates. The component is the REAL Pages/AuditLogViewer,
// rendered by the real Blazor HtmlRenderer through the real DI graph, exactly as in the RESTARTED
// file.
//
// WHAT IS NOT. HtmlRenderer is a STATIC renderer (see the RESTARTED file's own note); the on-demand
// Verify Chain banner needs a click and is out of reach here. Only the startup banners are rendered.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public sealed class AuditProvenanceAndUnverifiableBannerRenderTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _dirs = new();

    public AuditProvenanceAndUnverifiableBannerRenderTests(ITestOutputHelper output) => _out = output;

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "_sqlt_smalls_banner2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
                }
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* a locked segment is not a test failure */ }
        }
    }

    private static AuditLogService Open(string dir) => new(dir, startFlushTimer: false);

    /// <summary>Renders the REAL page component through the real HtmlRenderer and DI graph. Mirrors
    /// AuditRestartBannerRenderTests.RenderAuditPageAsync exactly (see that file for the RBAC-gate
    /// discussion: this container registers the desktop HostEnvironmentInfo, so the gate allows and
    /// these tests are about banners, not authorization).</summary>
    private static async Task<string> RenderAuditPageAsync(AuditLogService audit, string role)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
        services.AddSingleton(audit);
        services.AddSingleton<HostEnvironmentInfo>();
        services.AddSingleton<RbacService>();
        services.AddScoped<AppUserState>();

        await using var provider = services.BuildServiceProvider();
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

    /// <summary>
    /// Strips tags, collapses whitespace, and HTML-decodes entities — a superset of the RESTARTED
    /// file's helper of the same name. Needed here because the anchor-cause detail composed by
    /// AuditLogService carries a literal em dash, which Blazor emits as <c>&amp;#x2014;</c> in the
    /// raw markup; without decoding, a Contains against the composed string would fail even though
    /// the composed sentence reached the page unchanged.
    /// </summary>
    private static string VisibleText(string html)
        => System.Net.WebUtility.HtmlDecode(
            Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ").Trim());

    // ── ChainProvenanceIndeterminate ─────────────────────────────────────────────────────────

    /// <summary>
    /// Plants a genuine chain, then overwrites the out-of-band `.chain-anchor` sidecar with a
    /// well-formed DPAPI blob this process cannot unwrap (wrong entropy) — the exact recipe
    /// AuditLogServiceTests.UnreadableChainAnchor_IsIndeterminate_NotTampering uses, reproducing
    /// the live 2026-08-01 incident where a service-account change orphaned the anchor with
    /// nothing on the chain touched. No audit-log entry is edited.
    /// </summary>
    private static void PlantOrphanedAnchor(string dir)
    {
        using (var svc = Open(dir))
        {
            svc.LogConnectionAttempt("srv1", success: true);
            svc.LogConnectionAttempt("srv2", success: true);
            svc.Flush();
        }

        var anchorPath = Path.Combine(dir, ".chain-anchor");
        File.Exists(anchorPath).Should().BeTrue(
            "the production writer must have established the anchor before it can be orphaned");
        File.SetAttributes(anchorPath, FileAttributes.Normal);
        File.WriteAllBytes(anchorPath, ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes("{\"Sig\":\"x\",\"Ts\":\"x\"}"),
            System.Text.Encoding.UTF8.GetBytes("a-different-entropy"),
            DataProtectionScope.LocalMachine));
    }

    [Fact]
    public async Task The_orphaned_anchor_renders_the_indeterminate_banner_scoped_to_provenance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        PlantOrphanedAnchor(dir);

        using var audit = Open(dir);

        // The precondition, asserted rather than assumed.
        audit.ChainProvenanceIndeterminate.Should().BeTrue(
            "the planted anchor must actually be unreadable, or this fixture is not in the state under test");
        audit.ChainBroken.Should().BeFalse("an unreadable anchor is an evidence failure, not evidence of tampering");
        audit.ChainUnverifiable.Should().BeFalse();
        audit.ChainRestarted.Should().BeFalse();
        // This route sets the detail, not a key id or a record id — see the razor's own comment on
        // why the anchor cause names neither.
        audit.ChainIndeterminateDetail.Should().NotBeNullOrEmpty();
        audit.ChainIndeterminateKeyId.Should().BeNull();
        audit.ChainIndeterminateFirstRecordId.Should().BeNull();

        var html = await RenderAuditPageAsync(audit, AppRoles.Admin);
        var text = VisibleText(html);
        _out.WriteLine(text);

        html.Should().Contain("audit-log-page",
            "if the RBAC gate refused, every assertion below would be about an empty page");
        html.Should().NotContain("audit-log-denied");

        // ── The banner is the INDETERMINATE one, and ONLY that one ──
        // Severity Error (role="alert"/aria-live="assertive"), same as the tamper banner's markup
        // class, but a distinct icon: Critical is not claimed here, only that nothing was determined.
        html.Should().Contain("status-message error", "an unreadable anchor is not a known-benign result");
        html.Should().Contain("fa-circle-exclamation", "the indeterminate banner's own icon");
        html.Should().NotContain("fa-triangle-exclamation", "no ChainBroken banner fired on these bytes");
        html.Should().NotContain("fa-circle-question", "no ChainUnverifiable banner fired on these bytes");
        html.Should().NotContain("fa-link-slash", "no ChainRestarted banner fired on these bytes");

        // THE WORDS. Composed by AuditLogService, reached through the same null-conditional the
        // RESTARTED file's header warns about.
        var claim = audit.StartupBannerFor(AuditLogService.StartupChainFinding.ProvenanceIndeterminate);
        claim.Should().NotBeNull("the composer must answer for the finding the chain actually holds");
        text.Should().Contain(claim!.Headline,
            "\"Audit chain integrity cannot be determined.\" alone, since nothing else fired on this launch");
        text.Should().Contain(claim.ClosingLead);
        text.Should().Contain(claim.ClosingDetail);

        // The anchor-cause detail sentence — the branch this fixture is scoped to, distinct from the
        // key-named branch (which this fixture deliberately does not exercise; see the sibling
        // ledgerless-key-loss coverage in AuditLogServiceTests for that route into the same flag).
        text.Should().Contain(audit.ChainIndeterminateDetailSentence!,
            "the out-of-band anchor cause must reach the page, or a composer that stopped answering "
            + "would render a banner with no explanation of what could not be determined");
        text.Should().NotContain("Some entries name signing key",
            "this fixture has no key-named run; that sentence belongs to the other route into the flag");
        text.Should().NotContain("First affected record:",
            "this fixture has no first record either — the anchor cause names neither");

        text.Should().ContainEquivalentOf("not evidence of tampering",
            "the banner must deny the accusation, exactly like the RESTARTED banner's own denial");
        text.Should().ContainEquivalentOf("not a clean result");
        text.Should().Contain("Audit Chain Provenance Indeterminate",
            "the runbook pointer for THIS finding");
    }

    // ── ChainUnverifiable ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plants a genuine chain under key A, then simulates genuine, unrecoverable key loss exactly
    /// as AuditLogServiceTests.KeyReplaced_OldEntriesBecomeUnverifiable_NotBroken does: both the
    /// main key file AND its archives are gone (the age sidecar is kept), with the key-provenance
    /// ledger left intact so the loss corroborates as benign rather than indeterminate. No
    /// audit-log entry is edited.
    /// </summary>
    private static void PlantGenuineKeyLoss(string dir)
    {
        using (var svc = Open(dir))
        {
            svc.LogApplicationStart();
            svc.LogConnectionAttempt("srv", success: true);
            svc.Flush();
        }

        var keyPath = Path.Combine(dir, "hmac.key");
        File.SetAttributes(keyPath, FileAttributes.Normal);
        File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
        foreach (var archive in Directory.GetFiles(dir, "hmac.key.*"))
        {
            if (archive.EndsWith(".meta")) continue; // keep the age sidecar
            File.SetAttributes(archive, FileAttributes.Normal);
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task The_genuine_key_loss_renders_the_unverifiable_banner_and_not_an_accusation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        PlantGenuineKeyLoss(dir);

        using var audit = Open(dir);

        audit.ChainUnverifiable.Should().BeTrue(
            "the planted key loss must actually corroborate as benign, or this fixture is not in the state under test");
        audit.ChainBroken.Should().BeFalse("key loss is not evidence that anyone altered a record");
        audit.ChainProvenanceIndeterminate.Should().BeFalse(
            "the ledger survived the key loss, so this is the corroborated benign gap, not the evidence-free one");
        audit.ChainRestarted.Should().BeFalse();

        var html = await RenderAuditPageAsync(audit, AppRoles.Admin);
        var text = VisibleText(html);
        _out.WriteLine(text);

        html.Should().Contain("audit-log-page",
            "if the RBAC gate refused, every assertion below would be about an empty page");
        html.Should().NotContain("audit-log-denied");

        // ── The banner is the AMBER one, and ONLY that one ──
        html.Should().Contain("status-message warning",
            "a corroborated key-management gap is not an integrity failure");
        html.Should().Contain("fa-circle-question", "the unverifiable banner's own icon");
        html.Should().NotContain("status-message error");
        html.Should().NotContain("role=\"alert\"", "the assertive alert is the tamper/indeterminate channel");
        html.Should().NotContain("fa-triangle-exclamation");
        html.Should().NotContain("fa-circle-exclamation");
        html.Should().NotContain("fa-link-slash");

        // THE OPENING, which is a literal on this page (not composed — see the razor's own note on
        // why the UnverifiableGap opening stays scoped in markup).
        audit.ChainUnverifiableKeyId.Should().NotBeNullOrWhiteSpace();
        audit.ChainUnverifiableFirstRecordId.Should().NotBeNullOrWhiteSpace();
        text.Should().Contain("unverifiable");
        text.Should().Contain(audit.ChainUnverifiableKeyId!);
        text.Should().Contain(audit.ChainUnverifiableFirstRecordId!);

        // THE CLOSING. Composed, reached through the same null-conditional.
        var claim = audit.StartupBannerFor(AuditLogService.StartupChainFinding.UnverifiableGap);
        claim.Should().NotBeNull("the composer must answer for the finding the chain actually holds");
        text.Should().Contain(claim!.ClosingLead);
        text.Should().Contain(claim.ClosingDetail);

        // The runbook pointer — the render-proven markup bug (2026-08-20): this banner cited
        // "§ Audit Chain Break" while the runbook's dedicated section for THIS finding is
        // "Audit Chain Unverifiable". Only this one banner fires on this fixture, so both
        // assertions are unambiguous: the correct pointer is present, and the wrong one is gone.
        text.Should().Contain("Audit Chain Unverifiable", "the runbook pointer for THIS finding");
        text.Should().NotContain("Audit Chain Break",
            "the pre-fix citation pointed at the wrong runbook section for an unverifiable-key "
            + "finding, which is not a break — this must not regress");

        // ── NO ACCUSATION ──
        foreach (var accusation in new[]
                 {
                     "Treat as tampering",
                     "integrity check failed",
                     "FAILED verification",
                     "was altered",
                     "may have been altered",
                 })
            text.Should().NotContainEquivalentOf(accusation,
                $"a corroborated key loss is not tampering; \"{accusation}\" is the playbook for a "
                + "different finding");
    }
}
