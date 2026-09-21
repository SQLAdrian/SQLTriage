/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests;

// ── Collector deploy path must stay unreachable ──────────────────────────────────
// Unbundled 2026-07-21 (client safety, not feature cleanup). SQLTriage used to be able
// to install Darling PerformanceMonitor and SQLWATCH onto a target SQL Server. In our
// own field use PerformanceMonitor caused TWO client outages in two months plus a
// near-third: memory above 2GB, sustained high CPU, and a ~600GB self-inflicted database
// blowout. If we ship the ability to deploy it, we own the outage.
//
// The pages, the two installer services and the route constants were DELETED rather than
// gated, so re-introducing a link normally fails to compile. This suite covers what the
// compiler cannot: hardcoded route literals in markup (the old Guide.razor wrote
// "/deploydarlingPM" as a raw string, bypassing RouteConstants entirely), a new @page
// directive resurrecting the route, and the MSBuild rules that stop the installer SQL
// from shipping.
//
// NOTE ON THIS FILE'S OWN HONESTY: a source-scanning test is the easiest kind to write
// vacuously — scan zero files, find zero violations, print green. Every scan below is
// paired with a control that fails if the scanner stops actually looking. See
// Scanner_control_* tests.
public class CollectorDeployUnreachableTests
{
    // Route literals and type names that only ever existed to serve the deploy path.
    // Matched case-insensitively: the old markup used "/deploydarlingPM" while the route
    // constant used "/deploydarlingpm", and Blazor routing is case-insensitive, so a
    // case-sensitive guard would have missed the real historical spelling.
    private static readonly string[] ForbiddenTokens =
    {
        "/deploysqlwatch",
        "/deploydarling",
        "DeploySqlWatch",
        "DeployDarlingPm",
        "PMInstallationService",
        "SqlWatchDeploymentService",
    };

    // Files deleted by the unbundle. Their return would restore a routable page.
    private static readonly string[] DeletedFiles =
    {
        @"Pages\DatabaseDeploy.razor",
        @"Pages\DeployDarlingPM.razor",
        @"Data\Services\PMInstallationService.cs",
        @"Data\Services\SqlWatchDeploymentService.cs",
    };

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SQLTriage.csproj"))) return dir;
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (SQLTriage.csproj) walking up from " +
            AppContext.BaseDirectory + ". This guard cannot run, so it must not pass.");
    }

    // Shipping source only: build outputs, the test project itself and the vendored
    // PerformanceMonitor-main tree are not things a user can click.
    private static List<FileInfo> ShippingSourceFiles()
    {
        var root = RepoRoot();
        return root.EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".cs" or ".razor")
            .Where(f => !Regex.IsMatch(
                f.FullName.Substring(root.FullName.Length),
                @"\\(bin|obj|publish|release|Tests|PerformanceMonitor-main)\\",
                RegexOptions.IgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Removes comment text so the guard flags LIVE references, not the tombstone
    /// comments the unbundle deliberately left behind ("removed 2026-07-21 — do not
    /// re-add"). Those comments name the routes on purpose; without stripping, this
    /// suite could only pass by deleting its own documentation.
    /// Line comments are only stripped when the "//" starts the line (after whitespace),
    /// so a "//" appearing mid-line can never swallow real code to its right.
    /// </summary>
    internal static string StripComments(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);   // Razor
        source = Regex.Replace(source, @"<!--.*?-->", " ", RegexOptions.Singleline);  // HTML
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);   // C# block
        source = Regex.Replace(source, @"(?m)^[ \t]*//.*$", " ");                     // C# line
        return source;
    }

    // ── The guards ──────────────────────────────────────────────────────────────

    [Fact]
    public void Deploy_pages_and_installer_services_are_absent_from_the_repo()
    {
        var root = RepoRoot();
        foreach (var rel in DeletedFiles)
        {
            File.Exists(Path.Combine(root.FullName, rel)).Should().BeFalse(
                $"{rel} was deleted when the collector deploy path was unbundled 2026-07-21. " +
                "Restoring it restores a routable install page. See RouteConstants.cs \"Deployment\".");
        }
    }

    [Fact]
    public void No_page_directive_declares_a_collector_deploy_route()
    {
        var offenders = new List<string>();
        foreach (var file in ShippingSourceFiles().Where(f => f.Extension == ".razor"))
        {
            foreach (Match m in Regex.Matches(
                         StripComments(File.ReadAllText(file.FullName)),
                         @"@page\s+""(?<route>[^""]+)""", RegexOptions.IgnoreCase))
            {
                var route = m.Groups["route"].Value;
                if (route.Contains("deploysqlwatch", StringComparison.OrdinalIgnoreCase) ||
                    route.Contains("deploydarling", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{file.Name}: @page \"{route}\"");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a @page directive makes the deploy page reachable by typing the URL, by a bookmark, " +
            "or from browser history — which no amount of nav-link gating prevents.");
    }

    [Fact]
    public void No_shipping_source_file_references_a_collector_deploy_route_or_installer()
    {
        var offenders = new List<string>();
        foreach (var file in ShippingSourceFiles())
        {
            var stripped = StripComments(File.ReadAllText(file.FullName));
            var lines = stripped.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var token in ForbiddenTokens)
                {
                    if (lines[i].Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{file.Name}:{i + 1} contains '{token}' -> {lines[i].Trim()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "every one of these tokens exists only to reach the SQLWATCH / Darling PerformanceMonitor " +
            "installer. PerformanceMonitor caused two client outages in two months; if we ship the " +
            "ability to deploy it, we own the outage. See RouteConstants.cs \"Deployment\".");
    }

    [Fact]
    public void RouteConstants_declares_no_collector_deploy_route()
    {
        var path = Path.Combine(RepoRoot().FullName, @"Data\RouteConstants.cs");
        File.Exists(path).Should().BeTrue("RouteConstants.cs is the file under test.");

        var stripped = StripComments(File.ReadAllText(path));
        stripped.Should().NotContain("DeploySqlWatch");
        stripped.Should().NotContain("DeployDarlingPm");
    }

    [Fact]
    public void Collector_installer_payload_is_excluded_from_every_copy_to_output_rule()
    {
        var csprojPath = Path.Combine(RepoRoot().FullName, "SQLTriage.csproj");
        var doc = XDocument.Load(csprojPath);

        // Two INDEPENDENT rules copy Deploy\** into the output: a <Content Include> and a
        // <None Include>. Excluding only one is vacuous — the other still ships the SQL.
        var deployCopyRules = doc.Descendants()
            .Where(e => e.Name.LocalName is "Content" or "None")
            .Where(e => (e.Attribute("Include")?.Value ?? string.Empty)
                .StartsWith(@"Deploy\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        deployCopyRules.Should().HaveCountGreaterThanOrEqualTo(2,
            "both the Content and the None rule for Deploy\\** must still be found by this test; " +
            "if the project was restructured, this guard needs rewriting, not deleting.");

        foreach (var rule in deployCopyRules)
        {
            var exclude = rule.Attribute("Exclude")?.Value ?? string.Empty;
            exclude.Should().Contain(@"Deploy\PerformanceMonitor_db\**",
                $"<{rule.Name.LocalName} Include=\"{rule.Attribute("Include")?.Value}\"> would otherwise " +
                "ship 67 ready-to-run PerformanceMonitor installer scripts onto the client's server.");
            exclude.Should().Contain(@"Deploy\SQLWATCH_db\**",
                $"<{rule.Name.LocalName} Include=\"{rule.Attribute("Include")?.Value}\"> would otherwise " +
                "ship the SQLWATCH DACPACs and create scripts onto the client's server.");
        }
    }

    // ── Controls: these fail if the guards above stop actually looking ───────────

    [Fact]
    public void Scanner_control_sees_the_real_shipping_source_tree()
    {
        var files = ShippingSourceFiles();

        // Measured 2026-07-21: 496 shipping .cs/.razor files (348 .cs, 148 .razor).
        // The floor is deliberately well below that so ordinary churn doesn't trip it,
        // while a filter/path regression that empties the scan still does.
        files.Should().HaveCountGreaterThan(300,
            "if the file filter or repo-root walk breaks, every scan above silently passes " +
            "over zero files. That is the failure mode this whole file exists to prevent.");
        files.Should().Contain(f => f.Name == "RouteConstants.cs");
        files.Should().Contain(f => f.Name == "NavMenu.razor");
        files.Should().Contain(f => f.Name == "Guide.razor");
    }

    [Fact]
    public void Scanner_control_can_still_find_a_token_that_is_genuinely_present()
    {
        // Proves the read + strip + match pipeline detects a live reference. If comment
        // stripping ever became over-eager (swallowing real code), this goes red instead
        // of the forbidden-token scan going quietly, falsely green.
        var hits = ShippingSourceFiles()
            .Count(f => StripComments(File.ReadAllText(f.FullName))
                .Contains("RouteConstants.Servers", StringComparison.Ordinal));

        hits.Should().BeGreaterThan(0,
            "RouteConstants.Servers is referenced in live (non-comment) shipping code, so a " +
            "working scanner must find it. Zero hits means the scanner is broken, not that " +
            "the codebase is clean.");
    }

    [Theory]
    // Comment forms the tombstones actually use — these must be stripped.
    [InlineData("@* see /deploysqlwatch *@", false)]
    [InlineData("<!-- see /deploysqlwatch -->", false)]
    [InlineData("/* see /deploysqlwatch */", false)]
    [InlineData("    // see /deploysqlwatch", false)]
    // Live code forms — these must survive stripping and be caught.
    [InlineData("<NavLink href=\"/deploysqlwatch\" />", true)]
    [InlineData("Nav.NavigateTo(\"/deploysqlwatch\");", true)]
    // A "//" mid-line must not swallow the code to its right.
    [InlineData("var url = \"https://x/deploysqlwatch\";", true)]
    public void StripComments_removes_comments_without_eating_live_code(string source, bool shouldSurvive)
    {
        StripComments(source).Contains("/deploysqlwatch", StringComparison.OrdinalIgnoreCase)
            .Should().Be(shouldSurvive);
    }
}
