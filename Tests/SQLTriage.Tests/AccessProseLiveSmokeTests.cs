/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
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

/// <summary>
/// EXERCISE VEHICLE for the ACCESS SURFACE and SoD MATRIX coverage prose — orchestrator/gate-owned.
/// INERT in a normal <c>dotnet test</c> run: <see cref="LiveFactAttribute"/> reports every test as
/// SKIPPED — not passed — unless the environment names a reachable SQL instance and the fixture
/// database below.
///
/// <para>⚠⚠ WHY IT EXISTS. On 2026-08-14 the cold gate ruled NO-SHIP on the offboarding trace
/// because the page and the PDF told a client the sweep "did not hold sysadmin or CONTROL SERVER
/// rights" whenever <c>Coverage.HadHighPrivilege</c> was false. That flag is a COMPLETENESS flag:
/// the per-database loop drops it on ANY open failure, so one SINGLE_USER database with somebody
/// sitting in it was enough to put a false claim about a client's own DBA into a signed artifact.
/// The trace surfaces were fixed at 925a00c. THE SAME CONDITION WAS STILL LIVE ON THREE OLDER
/// SURFACES — the Access Surface page, the SoD matrix page and the SoD PDF — and this file is the
/// live proof for those three, in both directions.</para>
///
/// <para>WHAT IT RENDERS. The REAL page components, through the REAL Blazor
/// <see cref="Microsoft.AspNetCore.Components.Web.HtmlRenderer"/> and the REAL DI graph, driven
/// through the page's OWN service path — the same private method the Analyze/Collect button
/// invokes. It asserts on the words an operator reads, not on a model a test composed.</para>
///
/// <para>INVOCATION (gate, on a box with the local test instances). Every fixture is the caller's
/// to create and drop; this file plants nothing:</para>
/// <code>
///   $env:APROSE_LIVE_TARGET    = "lpc:."
///   $env:APROSE_LIVE_WEDGE_DB  = "_sqlt_aprose_wedge"
///   $env:APROSE_LIVE_LOWPRIV   = "_sqlt_aprose_lowpriv"
///   $env:APROSE_LIVE_LOWPRIV_PWD = "…"
///   $env:APROSE_LIVE_PDF_OUT   = "C:\temp\aprose\sod.pdf"
///   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~AccessProseLiveSmokeTests"
/// </code>
///
/// <para>⚠⚠ THE FIXTURE CONTRACT IS A PRECONDITION, NOT A SUGGESTION.</para>
/// <list type="bullet">
/// <item>A database named by APROSE_LIVE_WEDGE_DB on the target, ONLINE and set SINGLE_USER, with
/// its one session already occupied by somebody else. ONLINE is what makes the sweep try it; the
/// refusal is what drops the completeness flag; and the refusal says NOTHING about the caller's
/// rights. Build it with <c>CREATE DATABASE …; ALTER DATABASE … SET SINGLE_USER WITH ROLLBACK
/// IMMEDIATE;</c> then hold a session in it (a second sqlcmd running WAITFOR does).
/// ⚠ Return it to MULTI_USER before dropping it, or the drop blocks on the session you left.</item>
/// <item>The caller runs as sysadmin on the target. That is the whole point: the sweep must hold
/// the rights the document used to say it lacked.</item>
/// <item>A SQL login named by APROSE_LIVE_LOWPRIV holding nothing but <c>public</c>, with its
/// password in APROSE_LIVE_LOWPRIV_PWD. That is the OTHER direction: a sweep that genuinely lacks
/// the rights must still say so, or this fix would have replaced a false claim with a silence.</item>
/// </list>
///
/// <para>⚠⚠ PROVENANCE, HEDGED TO WHAT WAS MEASURED. Armed on 2026-08-15 against <c>lpc:.</c> only
/// (the MSI default instance, SQL Server 17.0.1125.2). SQL 2022, 2017 and 2014 are UNTESTED here:
/// <c>.\old2017</c> was stopped for the whole lane and <c>.\new2022</c> carried no fixture.</para>
/// </summary>
public class AccessProseLiveSmokeTests
{
    /// <summary>
    /// A <see cref="FactAttribute"/> that reports <b>SKIPPED</b> — not passed — when the named
    /// environment variables are not set, by computing <see cref="FactAttribute.Skip"/> at
    /// discovery time. Restated here rather than bound from Portal/, which
    /// SQLTriage.Tests.csproj Compile-Removes under the community profile; binding that type from a
    /// file outside Portal/ would make the community test assembly uncompilable.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute(params string[] required)
        {
            var missing = required
                .Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                .ToList();
            if (missing.Count > 0)
                Skip = "live harness not armed; set " + string.Join(", ", missing);
        }
    }

    private const string TargetVar  = "APROSE_LIVE_TARGET";
    private const string WedgeVar   = "APROSE_LIVE_WEDGE_DB";
    private const string LowUserVar = "APROSE_LIVE_LOWPRIV";
    private const string LowPwdVar  = "APROSE_LIVE_LOWPRIV_PWD";
    private const string PdfOutVar  = "APROSE_LIVE_PDF_OUT";

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} must be set; LiveFact should have skipped this test");

    private readonly ITestOutputHelper _out;
    public AccessProseLiveSmokeTests(ITestOutputHelper output) => _out = output;

    // ── The two page renders, wedged sysadmin ────────────────────────────────────────────────

    [LiveFact(TargetVar, WedgeVar)]
    public async Task The_access_surface_page_never_tells_a_sysadmin_sweep_it_lacked_sysadmin()
    {
        var wedge = Require(WedgeVar);
        var (html, result) = await RenderAccessSurfaceAsync(WindowsConnection(Require(TargetVar)));
        var text = Harness.VisibleText(html);

        Precondition(result.Coverage, wedge);
        _out.WriteLine(Harness.CoverageStrip(text));

        text.Should().NotContain("Did not hold sysadmin",
            "THE DEFECT. A sysadmin sweep may never tell a client it lacked sysadmin");
        text.Should().NotContain("reduced-privilege sweep",
            "the chip beside it made the same claim in three words");
        text.Should().Contain("high-privilege sweep",
            "the probe measured that the rights were held, so the chip must say so");
        text.Should().Contain(wedge,
            "the database that refused must be named, or its absence reads as nothing found in it");
        text.Should().Contain("could not be opened",
            "the honest sentence for what actually happened must be printed in its place");
    }

    [LiveFact(TargetVar, WedgeVar)]
    public async Task The_sod_matrix_page_never_tells_a_sysadmin_sweep_it_lacked_sysadmin()
    {
        var wedge = Require(WedgeVar);
        var (html, result) = await RenderSodMatrixAsync(WindowsConnection(Require(TargetVar)));
        var text = Harness.VisibleText(html);

        Precondition(result.Coverage, wedge);
        _out.WriteLine(Harness.CoverageStrip(text));

        text.Should().NotContain("Did not hold sysadmin");
        text.Should().NotContain("reduced-privilege sweep");
        text.Should().Contain("high-privilege sweep");
        text.Should().Contain(wedge);
        text.Should().Contain("could not be opened");
    }

    [LiveFact(TargetVar, WedgeVar, PdfOutVar)]
    public async Task The_sod_pdf_renders_the_shared_caveats_over_a_wedged_sysadmin_sweep()
    {
        // The repo carries no PDF text extractor, so this test proves the render RAN over the live
        // wedged model and writes the artifact; reading the words back out of the document is the
        // gate's exercise, deliberately, the same split the trace lane shipped.
        var wedge = Require(WedgeVar);
        var target = Require(TargetVar);
        var outPath = Require(PdfOutVar);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        // The model comes off the SoD PAGE's own Collect handler, which is what the page's Export
        // button hands to the renderer. Composing one here would be a different artifact.
        var (_, result) = await RenderSodMatrixAsync(WindowsConnection(target));
        Precondition(result.Coverage, wedge);

        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var bytes = SodMatrixPdf.Build(result, new AssessmentMeta
        {
            Title = "SoD permissions matrix",
            Company = "SQLTriage live harness",
            Subtitle = target,
            Engine = "Access Surface privilege catalog",
            GeneratedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mmZ"),
            TimezoneId = TimeZoneInfo.Local.StandardName,
            RunId = "aprose",
            Watermark = true,
            FooterMeta = "SQLTriage live harness",
        });

        // ⚠ WRITTEN BEFORE ANYTHING IS ASSERTED, deliberately. The gate reads the words back out of
        // this document, and a document that is only written when the assertions already passed can
        // never show the gate a defect.
        bytes.Should().NotBeEmpty();
        await File.WriteAllBytesAsync(outPath, bytes);
        _out.WriteLine($"wrote {bytes.Length} bytes to {outPath}");

        var caveats = AccessCoverageText.Caveats(result.Coverage);
        foreach (var c in caveats) _out.WriteLine("caveat: " + c);
        caveats.Should().NotContain(AccessCoverageText.ReducedPrivilege,
            "THE DEFECT, at the model the PDF is handed");
        caveats.Should().ContainSingle().Which.Should().Contain(wedge);
        AccessCoverageText.PrivilegeLabel(result.Coverage).Should().Be("high-privilege sweep");
    }

    // ── The other direction: a sweep that genuinely lacks the rights still says so ───────────

    [LiveFact(TargetVar, LowUserVar, LowPwdVar)]
    public async Task A_genuinely_low_privilege_sweep_still_makes_the_reduced_privilege_claim()
    {
        // Without this, "no false rights claim" would be satisfied by never making the claim at all.
        var conn = SqlAuthConnection(Require(TargetVar), Require(LowUserVar), Require(LowPwdVar));

        var (surfaceHtml, surface) = await RenderAccessSurfaceAsync(conn);
        surface.Coverage.HighPrivilegeProbe.Should().Be(HighPrivilegeProbeResult.NotHeld,
            "the fixture login must hold nothing but public; if this fails, re-read the fixture "
            + "contract before reading the code");

        var surfaceText = Harness.VisibleText(surfaceHtml);
        _out.WriteLine(Harness.CoverageStrip(surfaceText));
        surfaceText.Should().Contain("did not hold sysadmin or CONTROL SERVER rights");
        surfaceText.Should().Contain("reduced-privilege sweep");

        var (sodHtml, sod) = await RenderSodMatrixAsync(conn);
        sod.Coverage.HighPrivilegeProbe.Should().Be(HighPrivilegeProbeResult.NotHeld);

        var sodText = Harness.VisibleText(sodHtml);
        _out.WriteLine(Harness.CoverageStrip(sodText));
        sodText.Should().Contain("did not hold sysadmin or CONTROL SERVER rights");
        sodText.Should().Contain("reduced-privilege sweep");
    }

    // ── Preconditions, asserted rather than assumed ──────────────────────────────────────────

    private void Precondition(Coverage cov, string wedge)
    {
        var skip = cov.SkippedDatabases.FirstOrDefault(d =>
            string.Equals(d.Name, wedge, StringComparison.OrdinalIgnoreCase));

        _out.WriteLine($"ranAs={cov.RanAsPrincipal} probe={cov.HighPrivilegeProbe} "
                       + $"complete={cov.HadHighPrivilege} read={cov.EnumeratedDatabases.Count} "
                       + $"skipped={cov.SkippedDatabases.Count}");

        skip.Should().NotBeNull(
            $"{wedge} must exist on {cov.RanAsPrincipal}'s instance, be ONLINE, be SINGLE_USER and "
            + "have its one session occupied; without that the sweep reads it and every assertion "
            + "below would pass over a state that is not the one under test");
        skip!.CouldNotOpen.Should().BeTrue("the sweep asked for it and was refused");
        cov.HadHighPrivilege.Should().BeFalse("coverage was genuinely incomplete, and that flag still says so");
        cov.HighPrivilegeProbe.Should().Be(HighPrivilegeProbeResult.Held,
            "the harness contract says the caller is sysadmin on this instance");
    }

    // ── Connections ───────────────────────────────────────────────────────────────────────

    private static ServerConnection WindowsConnection(string target) => new()
    {
        ServerNames = target,
        UseWindowsAuthentication = true,
        TrustServerCertificate = true,
        IsEnabled = true,
    };

    private static ServerConnection SqlAuthConnection(string target, string user, string password)
    {
        var conn = new ServerConnection
        {
            ServerNames = target,
            UseWindowsAuthentication = false,
            AuthenticationType = AuthenticationTypes.SqlServer,
            Username = user,
            TrustServerCertificate = true,
            IsEnabled = true,
        };
        conn.SetPassword(password);
        return conn;
    }

    private static Task<(string html, AccessSurfaceResult result)> RenderAccessSurfaceAsync(ServerConnection conn) =>
        Harness.DriveAsync<SQLTriage.Pages.AccessSurface, AccessSurfaceResult>(conn, "Analyze");

    private static Task<(string html, SodMatrixResult result)> RenderSodMatrixAsync(ServerConnection conn) =>
        Harness.DriveAsync<SQLTriage.Pages.SodMatrix, SodMatrixResult>(conn, "Collect");
}
