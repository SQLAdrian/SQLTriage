/* In the name of God, the Merciful, the Compassionate */

// ── The audit page's authorization gate, RENDERED, 2026-08-15 ────────────────────────────────
//
// WHY THIS FILE EXISTS. Until this lane Pages/AuditLogViewer.razor gated on the STATIC
// RbacService.HasPermission(UserState.Role, "settings") — the permission matrix alone, which
// cannot see the unconfigured-install bootstrap hatch. It was the only shipped page doing so.
// Every peer settings-gated surface (BuildProfile, EditAuditScripts, RemediationTuner,
// ScheduledTasks, ServiceManagement) goes through AppUserState.IsAuthorized, which is that same
// matrix PLUS this circuit's bootstrap eligibility.
//
// MEASURED at 5608689 on a fresh contained install, browser-hosted, loopback caller: GET
// /audit-log answered HTTP 200 carrying the audit-log-denied marker, while the onboarding wizard
// on the SAME install passed its own gate. So the chain banners and the Verify Chain button were
// unreachable on a fresh browser-hosted install. Fail-closed, so a usability and consistency
// defect rather than a hole. Adrian ruled ALIGN on 2026-08-15: reading audit records is strictly
// weaker than the settings WRITE the hatch already grants that same caller.
//
// WHAT THIS FILE PINS. The ruling is "align", not "open", and those differ in three of the four
// cells below. A test that only proved the fresh-install case would be satisfied by a page with no
// gate at all, which is the exact shape that put Pages/Onboarding.razor on the census in the first
// place. So the matrix is driven on both axes — enforcement on/off, principal admin/viewer — and
// the two denial cells are as load-bearing as the two permit cells.
//
// WHAT IS REAL HERE. The REAL Pages/AuditLogViewer component, the real AuditLogService over real
// planted segments, the real RbacService over real on-disk rbac-config.json / rbac-users.json, and
// the real AppUserState, rendered by the real Blazor HtmlRenderer through the real DI graph. The
// only seams are the two the production code already exposes for exactly this: AppUserState's
// SetRole / SetLoopbackForTests, and HostEnvironmentInfo's BrowserHosted factory.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

public sealed class AuditLogViewerGateTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _dirs = new();

    public AuditLogViewerGateTests(ITestOutputHelper output) => _out = output;

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "_sqlt_auditgate_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>The marker the page's denial branch renders, and nothing else does.</summary>
    private const string DeniedMarker = "audit-log-denied";

    /// <summary>The marker the page's content branch renders, and nothing else does.</summary>
    private const string PageMarker = "audit-log-page";

    /// <summary>
    /// Renders the REAL page through the real DI graph, in a named RBAC posture.
    /// </summary>
    /// <param name="enforced">
    /// True builds an rbac-config.json + rbac-users.json pair that actually makes
    /// <c>IsRbacEnforced()</c> true — enabled, a usable sign-in method, and an admin who can use
    /// it. Anything less is dormant by design (the one-click-lockout guard), and a test that
    /// merely set <c>Enabled = true</c> would be asserting against an UNENFORCED service while
    /// believing it had enforced one.
    /// </param>
    private async Task<string> RenderAsync(bool enforced, string role, bool loopback)
    {
        var dir = NewDir();

        using var audit = new AuditLogService(dir, startFlushTimer: false);
        audit.LogConnectionAttempt("srv1", success: true);
        audit.Flush();

        var configPath = Path.Combine(dir, "rbac-config.json");
        var usersPath = Path.Combine(dir, "rbac-users.json");

        if (enforced)
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));

            var admin = new RbacUser
            {
                Email = "admin@example.com",
                Provider = "local",
                Role = AppRoles.Admin,
                Enabled = true,
                PasswordHash = RbacService.HashPassword("correct horse battery staple")
            };
            File.WriteAllText(usersPath, JsonSerializer.Serialize(new List<RbacUser> { admin }));
        }

        var rbac = new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);

        // The precondition, asserted rather than assumed. Every cell below is about a posture, and
        // a posture that silently failed to take would make the permit cells pass vacuously.
        rbac.IsRbacEnforced().Should().Be(enforced,
            "the fixture must actually be in the posture the case names, or its verdict is about "
            + "some other install");

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
        services.AddSingleton(audit);
        services.AddSingleton(HostEnvironmentInfo.BrowserHosted);
        services.AddSingleton(rbac);
        services.AddScoped<AppUserState>();

        await using var provider = services.BuildServiceProvider();

        var scope = provider.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<AppUserState>();
        user.SetRole(role);
        user.SetLoopbackForTests(loopback);

        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggerFactory);

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SQLTriage.Pages.AuditLogViewer>();
            return output.ToHtmlString();
        });

        _out.WriteLine($"enforced={enforced} role={role} loopback={loopback} "
                       + $"denied={html.Contains(DeniedMarker)} rendered={html.Contains(PageMarker)}");
        return html;
    }

    // ── The permit cells: what this lane changed ─────────────────────────────────────────────

    [Fact]
    public async Task Unconfigured_LoopbackCaller_SeesThePage()
    {
        // THE REGRESSION. This is the cell that was denied at 5608689 and is the whole reason the
        // lane exists: an operator on the console of a fresh browser-hosted install could not
        // reach the chain banners or Verify Chain.
        var html = await RenderAsync(enforced: false, role: AppRoles.Viewer, loopback: true);

        html.Should().NotContain(DeniedMarker,
            "an unconfigured install's loopback caller holds the bootstrap hatch, which already "
            + "grants the settings WRITE — refusing the strictly weaker READ is the defect this "
            + "lane fixed");
        html.Should().Contain(PageMarker);
        html.Should().Contain("Verify Chain",
            "the button whose unreachability was the measured consequence");
    }

    [Fact]
    public async Task Enforced_AdminPrincipal_SeesThePage()
    {
        var html = await RenderAsync(enforced: true, role: AppRoles.Admin, loopback: true);

        html.Should().NotContain(DeniedMarker);
        html.Should().Contain(PageMarker);
    }

    // ── The denial cells: what this lane did NOT change ──────────────────────────────────────

    [Fact]
    public async Task Enforced_NonAdminPrincipal_IsStillRefused()
    {
        // The ruling was ALIGN, not OPEN. Once RBAC is enforced the hatch is gone on every
        // surface, and a viewer must be refused here exactly as at BuildProfile or ServiceManagement.
        var html = await RenderAsync(enforced: true, role: AppRoles.Viewer, loopback: true);

        html.Should().Contain(DeniedMarker,
            "under enforced RBAC the permission matrix is the whole decision, and viewer does not "
            + "hold 'settings'");
        html.Should().NotContain(PageMarker);
    }

    [Fact]
    public async Task Enforced_NonAdminOnLoopback_GetsNoHatchFromBeingOnLoopback()
    {
        // Loopback buys bootstrap eligibility, and bootstrap eligibility is scoped to the
        // UNENFORCED install. If this ever renders the page, the hatch has leaked into a
        // configured install and this page has become more permissive than /query.
        var html = await RenderAsync(enforced: true, role: AppRoles.Operator, loopback: true);

        html.Should().Contain(DeniedMarker);
        html.Should().NotContain(PageMarker);
    }

    [Fact]
    public async Task Unconfigured_RemoteCaller_IsRefused()
    {
        // The other half of the hatch's scope, and the reason the page must go through
        // AppUserState rather than call RbacService.IsAuthorized(role, permission) directly: the
        // two-argument overload assumes bootstrapEligible: true, so a page calling it would hand
        // an unconfigured install's audit log to any caller who could route to the port.
        var html = await RenderAsync(enforced: false, role: AppRoles.Viewer, loopback: false);

        html.Should().Contain(DeniedMarker,
            "'unconfigured' must not mean 'admin for anyone who can reach the listener'");
        html.Should().NotContain(PageMarker);
    }

    // ── The gate's shape, so a future edit cannot regress it silently ────────────────────────

    [Fact]
    public void ThePageGatesThroughAppUserState_NotTheStaticMatrix()
    {
        // RbacServerModeLockoutTests scans Pages/ and Components/ for the static call and is the
        // general guard. This is the specific one, kept beside the behaviour it protects: it names
        // the file, so a reader who lands here from a failing render above sees WHICH construct is
        // required rather than inferring it.
        var markup = File.ReadAllText(FindRepoFile("Pages/AuditLogViewer.razor"));

        markup.Should().Contain("UserState.IsAuthorized(\"settings\")",
            "the page must gate through the circuit-scoped decision that honours the bootstrap hatch");
        markup.Should().Contain("ALIGNED 2026-08-15",
            "the ruling must stay recorded on the gate, or the next reader 'fixes' it back to the "
            + "static call the way the 2026-08-01 sweep's leftover was read as intent");
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relative} from {AppContext.BaseDirectory}.");
    }
}
