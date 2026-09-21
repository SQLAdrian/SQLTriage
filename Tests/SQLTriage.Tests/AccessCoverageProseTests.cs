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
/// THE RIGHTS CLAIM ON THE THREE OLDER ACCESS-SURFACE SURFACES, pinned to the measurement it is
/// about. The Access Surface page, the SoD matrix page and the SoD PDF each printed "Did not hold
/// sysadmin / CONTROL SERVER-class rights" — and the three-word chip "reduced-privilege sweep" —
/// off <c>Coverage.HadHighPrivilege</c>, a COMPLETENESS flag that one unopenable database drops
/// under a full sysadmin run. The trace surfaces were fixed at 925a00c; these three were not, and
/// this file is their pin.
///
/// <para>WHAT THESE TESTS ARE AND ARE NOT. They render the REAL page components through the REAL
/// Blazor renderer, over a Coverage this test COMPOSED. That is deliberate and it is the limit of
/// the claim: they prove the renderer's WORDING tracks the probe across all three of its states,
/// including <c>NotProbed</c>, which a live instance will not produce on demand. Whether the
/// collector produces those states from a real server is a different claim and belongs to
/// <see cref="AccessProseLiveSmokeTests"/>, which drives the same pages against live SQL with no
/// composed model anywhere.</para>
/// </summary>
public class AccessCoverageProseTests
{
    private readonly ITestOutputHelper _out;
    public AccessCoverageProseTests(ITestOutputHelper output) => _out = output;

    // ── The pure owner, across the whole tri-state ───────────────────────────────────────────

    [Theory]
    [InlineData(HighPrivilegeProbeResult.Held, "high-privilege sweep")]
    [InlineData(HighPrivilegeProbeResult.NotHeld, "reduced-privilege sweep")]
    [InlineData(HighPrivilegeProbeResult.NotProbed, "sweep rights not measured")]
    public void The_privilege_chip_says_what_the_probe_measured_and_nothing_else(
        HighPrivilegeProbeResult probe, string expected)
    {
        // Completeness is dropped in every case, which is what used to decide this chip. It now
        // decides nothing about rights, so all three answers differ while the flag does not.
        var cov = new Coverage { HadHighPrivilege = false, HighPrivilegeProbe = probe };
        AccessCoverageText.PrivilegeLabel(cov).Should().Be(expected);
    }

    [Fact]
    public void A_complete_sweep_and_an_incomplete_one_get_the_same_chip_when_the_probe_agrees()
    {
        var complete = new Coverage { HadHighPrivilege = true, HighPrivilegeProbe = HighPrivilegeProbeResult.Held };
        var wedged = new Coverage { HadHighPrivilege = false, HighPrivilegeProbe = HighPrivilegeProbeResult.Held };
        wedged.SkippedDatabases.Add(new SkippedDatabase
        { Name = "Wedged", Reason = "access denied or unavailable", CouldNotOpen = true });

        AccessCoverageText.PrivilegeLabel(wedged).Should().Be(AccessCoverageText.PrivilegeLabel(complete),
            "the sweep held the same rights in both; only its coverage differed");

        // And the banner still warns, because coverage really was incomplete. The fix separates the
        // two claims; it does not silence the honest one.
        AccessCoverageText.IsPartial(wedged).Should().BeTrue();
        AccessCoverageText.IsPartial(complete).Should().BeFalse();
    }

    // ── The three renderers, over the wedged-sysadmin shape ──────────────────────────────────

    [Fact]
    public async Task The_access_surface_page_prints_no_rights_claim_over_a_wedged_sysadmin_sweep()
    {
        var text = Visible(await Harness.PlantAsync<SQLTriage.Pages.AccessSurface, AccessSurfaceResult>(
            new AccessSurfaceResult { Instance = "lpc:.", Coverage = Wedged() }));
        _out.WriteLine(Strip(text));

        AssertWedgedProse(text);
    }

    [Fact]
    public async Task The_sod_matrix_page_prints_no_rights_claim_over_a_wedged_sysadmin_sweep()
    {
        var text = Visible(await Harness.PlantAsync<SQLTriage.Pages.SodMatrix, SodMatrixResult>(
            new SodMatrixResult { Instance = "lpc:.", Coverage = Wedged() }));
        _out.WriteLine(Strip(text));

        AssertWedgedProse(text);
    }

    [Fact]
    public void The_sod_pdf_renders_over_a_wedged_sysadmin_sweep_and_carries_the_shared_caveats()
    {
        // The repo carries no PDF text extractor, so the words in the document are read back by the
        // gate with pdftotext. What is proved here: the model the renderer is handed carries the
        // honest caveats and not the false one, and the render completes over it.
        var result = new SodMatrixResult { Instance = "lpc:.", Coverage = Wedged() };

        var caveats = AccessCoverageText.Caveats(result.Coverage);
        caveats.Should().NotContain(AccessCoverageText.ReducedPrivilege);
        caveats.Should().ContainSingle().Which.Should().Contain("_sqlt_aprose_wedge");
        AccessCoverageText.PrivilegeLabel(result.Coverage).Should().Be("high-privilege sweep");

        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        SodMatrixPdf.Build(result, Meta()).Should().NotBeEmpty();
    }

    // ── The other direction, on all three ────────────────────────────────────────────────────

    [Fact]
    public async Task A_genuinely_low_privilege_sweep_still_gets_the_reduced_privilege_sentence_everywhere()
    {
        // Without this, "no false rights claim" would be satisfied by never making the claim at all.
        var cov = new Coverage
        {
            RanAsPrincipal = "_sqlt_aprose_lowpriv",
            HadHighPrivilege = false,
            HighPrivilegeProbe = HighPrivilegeProbeResult.NotHeld,
        };
        cov.EnumeratedDatabases.Add("master");

        foreach (var text in new[]
                 {
                     Visible(await Harness.PlantAsync<SQLTriage.Pages.AccessSurface, AccessSurfaceResult>(
                         new AccessSurfaceResult { Instance = "lpc:.", Coverage = cov })),
                     Visible(await Harness.PlantAsync<SQLTriage.Pages.SodMatrix, SodMatrixResult>(
                         new SodMatrixResult { Instance = "lpc:.", Coverage = cov })),
                 })
        {
            _out.WriteLine(Strip(text));
            text.Should().Contain("did not hold sysadmin or CONTROL SERVER rights");
            text.Should().Contain("reduced-privilege sweep");
        }

        AccessCoverageText.Caveats(cov).Should().ContainSingle()
            .Which.Should().Be(AccessCoverageText.ReducedPrivilege);
    }

    [Fact]
    public async Task An_unmeasured_sweep_says_so_rather_than_picking_one_of_the_two_answers()
    {
        // The collector sets NotProbed when the probe itself never completed. A coverage strip built
        // without a probe must not read as either a high-privilege or a reduced-privilege run.
        var cov = new Coverage { HadHighPrivilege = false };
        cov.HighPrivilegeProbe.Should().Be(HighPrivilegeProbeResult.NotProbed, "that is the honest default");

        var text = Visible(await Harness.PlantAsync<SQLTriage.Pages.AccessSurface, AccessSurfaceResult>(
            new AccessSurfaceResult { Instance = "lpc:.", Coverage = cov }));
        _out.WriteLine(Strip(text));

        text.Should().Contain("sweep rights not measured");
        text.Should().NotContain("reduced-privilege sweep");
        text.Should().NotContain("high-privilege sweep");
        text.Should().Contain("could not read what rights it held");
    }

    // ── One owner survives only if its words fit all five surfaces ──────────────────────────
    //
    // ⚠⚠ THE 2026-08-15 NO-SHIP. Moving the sentences to one owner moved the TRACE's wording with
    // them. The SoD matrix is a whole-catalog inventory — the gate's live run listed 23 logins over
    // 35 rows — and its PDF printed "That is a gap in this trace, not a finding that this identity
    // holds nothing in it" four lines under its own scope note calling the document an inventory.
    // On 35 rows "this identity" names nobody, and the document is signed by an auditor. The rights
    // logic was correct; the copy was a trace's. Two nouns are true on all five surfaces — THE SWEEP
    // and THE COVERAGE — and these tests hold the sentences to them.

    public static TheoryData<string> SharedSentences() => new()
    {
        AccessCoverageText.ReducedPrivilege,
        AccessCoverageText.PrivilegeUnknown,
        AccessCoverageText.DatabasesNotOpened(new[] { "_sqlt_aprose_wedge" }),
        AccessCoverageText.DatabasesNotOpened(new[] { "_sqlt_aprose_wedge", "SQLWATCH" }),
    };

    [Theory]
    [MemberData(nameof(SharedSentences))]
    public void No_shared_sentence_speaks_as_if_one_identity_were_being_traced(string sentence)
    {
        foreach (var subject in new[]
                 {
                     "this trace", "the trace", "this identity", "the identity",
                     "leaver", "this person", "this login", "this inventory", "this matrix",
                 })
            sentence.Should().NotContainEquivalentOf(subject,
                $"\"{subject}\" is true on one surface and false on the others, and this sentence "
                + "prints on all five");

        (sentence.Contains("sweep", StringComparison.OrdinalIgnoreCase)
         || sentence.Contains("coverage", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("a shared sentence says what it is about, and the only subjects every "
                             + $"surface has are the sweep and the coverage: \"{sentence}\"");
    }

    [Fact]
    public async Task The_two_inventory_surfaces_never_call_themselves_a_trace()
    {
        // The constants above are where the words live; this is where the gate read them. The Access
        // Surface page walks the privilege graph over EVERY principal and the SoD page is a flat
        // catalog inventory. Neither is a trace and neither has one identity to speak about, in
        // either privilege direction.
        foreach (var cov in new[] { Wedged(), LowPrivilege() })
        foreach (var text in new[]
                 {
                     Visible(await Harness.PlantAsync<SQLTriage.Pages.AccessSurface, AccessSurfaceResult>(
                         new AccessSurfaceResult { Instance = "lpc:.", Coverage = cov })),
                     Visible(await Harness.PlantAsync<SQLTriage.Pages.SodMatrix, SodMatrixResult>(
                         new SodMatrixResult { Instance = "lpc:.", Coverage = cov })),
                 })
        {
            _out.WriteLine(Strip(text));
            text.Should().NotContainEquivalentOf("this trace",
                "THE DEFECT. A whole-catalog surface called itself a trace on a signed artifact");
            text.Should().NotContainEquivalentOf("this identity",
                "and named a subject that does not exist on a surface covering every principal");
        }
    }

    // ── Shared shapes ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE 2026-08-14 NO-SHIP, AS A SHAPE. A sysadmin sweep that lost one database: the probe
    /// measured the rights and found them, one ONLINE database refused the connection, and
    /// completeness correctly dropped.
    /// </summary>
    private static Coverage Wedged()
    {
        var cov = new Coverage
        {
            RanAsPrincipal = "MSI\\afsul",
            HadHighPrivilege = false,
            HighPrivilegeProbe = HighPrivilegeProbeResult.Held,
        };
        cov.EnumeratedDatabases.AddRange(new[] { "master", "msdb" });
        cov.SkippedDatabases.Add(new SkippedDatabase
        {
            Name = "_sqlt_aprose_wedge",
            Reason = "access denied or unavailable",
            CouldNotOpen = true,
        });
        return cov;
    }

    /// <summary>
    /// THE OTHER LIVE SHAPE, as the gate measured it on 2026-08-15: a public-only SQL login read 3
    /// databases and was refused 11. Both caveats print, so a test over this coverage exercises the
    /// rights sentence and the refusal sentence together.
    /// </summary>
    private static Coverage LowPrivilege()
    {
        var cov = new Coverage
        {
            RanAsPrincipal = "_sqlt_aprose_lowpriv",
            HadHighPrivilege = false,
            HighPrivilegeProbe = HighPrivilegeProbeResult.NotHeld,
        };
        cov.EnumeratedDatabases.AddRange(new[] { "master", "tempdb", "SQLDBA" });
        foreach (var name in new[] { "model", "msdb", "_sqlt_aprose_wedge" })
            cov.SkippedDatabases.Add(new SkippedDatabase
            { Name = name, Reason = "access denied or unavailable", CouldNotOpen = true });
        return cov;
    }

    private static void AssertWedgedProse(string text)
    {
        text.Should().NotContain("Did not hold sysadmin",
            "THE DEFECT. A sysadmin sweep may never tell a client it lacked sysadmin");
        text.Should().NotContain("did not hold sysadmin or CONTROL SERVER rights",
            "nor may it make the same claim in the longer wording");
        text.Should().NotContain("reduced-privilege sweep",
            "the chip beside it made the same claim in three words");

        text.Should().Contain("high-privilege sweep",
            "the probe measured that the rights were held, so the chip says so");
        text.Should().Contain("_sqlt_aprose_wedge",
            "the database that refused must be named, or its absence reads as nothing found in it");
        text.Should().Contain("could not be opened",
            "the honest sentence for what actually happened is printed in its place");
        text.Should().Contain("nothing to do with the rights this sweep held",
            "and it says what it does not mean, because a skipped database reads as a rights problem");
    }

    private static AssessmentMeta Meta() => new()
    {
        Title = "SoD permissions matrix",
        Company = "SQLTriage tests",
        Subtitle = "lpc:.",
        Engine = "Access Surface privilege catalog",
        GeneratedUtc = "2026-08-15T00:00Z",
        TimezoneId = "UTC",
        RunId = "unit",
        Watermark = false,
        FooterMeta = "SQLTriage tests",
    };

    private static string Visible(string html) => Harness.VisibleText(html);
    private static string Strip(string text) => Harness.CoverageStrip(text);
}

/// <summary>
/// Renders the REAL Access Surface / SoD page components through the REAL Blazor
/// <see cref="HtmlRenderer"/> and the REAL DI graph. Two ways in, and the difference between them
/// is the difference between the two claims the suite makes:
/// <list type="bullet">
/// <item><see cref="DriveAsync"/> — calls the page's OWN private handler, the same method the
/// Analyze/Collect button binds to, so the model under the markup came from the live service.</item>
/// <item><see cref="PlantAsync"/> — sets the page's result field directly, for the coverage states a
/// live instance will not produce on demand. A test that plants proves the RENDERER, never the
/// collector, and the tests that use it say so.</item>
/// </list>
/// </summary>
internal static class Harness
{
    /// <summary>
    /// The real DI graph the two pages resolve out of. The dev-capability gate is registered ON so
    /// the page renders its body rather than the "not licensed" shell — without that, every
    /// assertion over the markup would pass over an empty page, which is why
    /// <see cref="RenderAsync"/> asserts the coverage strip actually rendered.
    /// </summary>
    private static (ServiceProvider provider, IServiceScope scope) BuildGraph(ServerConnection? conn)
    {
        var settingsDir = Path.Combine(Path.GetTempPath(), "sqlt-aprose-" + Guid.NewGuid().ToString("N"));

        // ⚠⚠ THE STORE SEAM IS NOT OPTIONAL. ServerConnectionManager's default path is
        // AppContext.BaseDirectory\Config\server-connections.json — the BUILD OUTPUT's store, shared
        // by every test in the assembly and by whatever the last run left behind. Without this
        // override the harness (a) appended its connections, including a SQL login and its protected
        // password, into the build output, and (b) swept through whichever pre-existing connection
        // for the same instance name Resolve() happened to match FIRST, so the connection under test
        // was not necessarily the connection used. Measured on 2026-08-15: eleven accumulated entries
        // for one instance, and a SQL-auth run that came back as the Windows caller.
        var manager = new ServerConnectionManager(
            NullLogger<ServerConnectionManager>.Instance,
            seats: null,
            connectionsFilePath: Path.Combine(settingsDir, "server-connections.json"));
        if (conn is not null) manager.AddConnection(conn);
        var gate = new FeatureGate();
        gate.Register(FeatureRegistrar.DevTools, () => true);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
        services.AddSingleton(manager);
        services.AddSingleton<IFeatureGate>(gate);
        services.AddSingleton(new UserSettingsService(Path.Combine(settingsDir, "user-settings.json")));
        services.AddSingleton<IUserSettingsService>(sp => sp.GetRequiredService<UserSettingsService>());
        services.AddSingleton<ToastService>();
        services.AddSingleton<LdapAdGroupExpander>();
        services.AddSingleton<AccessSurfaceService>();
        services.AddSingleton<SodMatrixService>();

        var provider = services.BuildServiceProvider();
        return (provider, provider.CreateScope());
    }

    /// <summary>Runs the page's own handler against a live connection and returns what it rendered.</summary>
    public static Task<(string html, TResult result)> DriveAsync<TPage, TResult>(
        ServerConnection conn, string handler)
        where TPage : ComponentBase
        where TResult : class
        => RenderAsync<TPage, TResult>(conn, page =>
        {
            var run = typeof(TPage).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)
                      ?? throw new MissingMethodException(typeof(TPage).FullName, handler);
            Field(typeof(TPage), "_selectedServer").SetValue(page, conn.ServerNames);
            return (Task)run.Invoke(page, null)!;
        });

    /// <summary>Plants a composed result on the page and returns what it rendered over it.</summary>
    public static async Task<string> PlantAsync<TPage, TResult>(TResult result)
        where TPage : ComponentBase
        where TResult : class
    {
        var (html, _) = await RenderAsync<TPage, TResult>(
            conn: null,
            drive: page =>
            {
                Field(typeof(TPage), "_result").SetValue(page, result);
                return Task.CompletedTask;
            });
        return html;
    }

    private static async Task<(string html, TResult result)> RenderAsync<TPage, TResult>(
        ServerConnection? conn, Func<TPage, Task> drive)
        where TPage : ComponentBase
        where TResult : class
    {
        var (provider, scope) = BuildGraph(conn);
        using (provider)
        {
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggerFactory);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                TPage? page = null;
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["OnCaptured"] = (Action<TPage>)(c => page = c),
                });

                var root = await renderer.RenderComponentAsync<CapturingHost<TPage>>(parameters);
                page.Should().NotBeNull("the page component must have been created to be driven");

                await drive(page!);

                // The handler completes without a re-render because nothing dispatched it; in the
                // app the event dispatcher does this. Re-render explicitly, on the dispatcher, so
                // the markup asserted on is the markup the handler produced.
                typeof(ComponentBase)
                    .GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(page, null);
                await root.QuiescenceTask;

                var result = Field(typeof(TPage), "_result").GetValue(page) as TResult;
                result.Should().NotBeNull(
                    $"{typeof(TPage).Name} produced no result, so the coverage strip under test never "
                    + "rendered; check the connection and the dev-capability gate before reading the "
                    + "assertions below it");

                var html = root.ToHtmlString();
                html.Should().Contain("Coverage",
                    "the coverage strip must be on the page, or every assertion about its wording "
                    + "would pass by never running");

                return (html, result!);
            });
        }
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);

    /// <summary>Hosts the page under test and hands back the instance the renderer created.</summary>
    private sealed class CapturingHost<TPage> : ComponentBase where TPage : IComponent
    {
        [Parameter] public Action<TPage>? OnCaptured { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TPage>(0);
            builder.AddComponentReferenceCapture(1, o => OnCaptured?.Invoke((TPage)o));
            builder.CloseComponent();
        }
    }

    /// <summary>Strips tags and collapses whitespace, so an assertion reads the words a person does.</summary>
    public static string VisibleText(string html)
        => System.Net.WebUtility.HtmlDecode(
            Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();

    /// <summary>The coverage strip alone, for the log — the whole page runs to thousands of words.</summary>
    public static string CoverageStrip(string text)
    {
        var start = text.IndexOf("Coverage", StringComparison.Ordinal);
        if (start < 0) return "(no coverage strip rendered)";
        return text.Substring(start, Math.Min(1000, text.Length - start));
    }
}
