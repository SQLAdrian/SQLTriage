/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

// SQLTriage.Data exports its own LogLevel; the alias picks the logging one, for this file only.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLTriage.Tests
{
    // BM:RbacRound14RegressionTests — a bootstrap hatch is a one-way door
    /// <summary>
    /// Round 14. The remote bootstrap hatch did not LATCH, and the shipped prose said it did.
    ///
    /// <para><b>The defect, driven live with NO file damage of any kind.</b> An install where
    /// bootstrap was COMPLETE — <c>rbac-users.json</c> intact, an enabled Windows admin, the
    /// Windows provider on — with RBAC then switched OFF and <c>allowRemoteBootstrapAdmin</c> left
    /// on. An anonymous stranger from 192.168.10.32 was answered:</para>
    /// <code>
    /// /auth/me -> {"authenticated":false,"enforced":false,"loopback":false,"bootstrapEligible":true}
    /// /servers 200 · /query 200 · /settings 200 · /server-configuration 200
    /// /audit-log 200 · /service-management 200 · POST /_blazor/negotiate 200 (live circuit)
    /// </code>
    /// <para>and <see cref="RbacService.IsAuthorized(string, string, AppUserState.BootstrapEligibilityProof)"/> answered true for
    /// EVERY permission, because <c>bootstrapEligible</c> short-circuits it. Control, same rig,
    /// <c>enabled:true</c>: 302 across the board, negotiate 401.</para>
    ///
    /// <para><b>The cause was inherent to the predicate</b>
    /// (<c>IsUserStoreDamaged || Config.Enabled</c>), not a slip in it. "Expired once RBAC is
    /// enabled" is identically "un-expired once RBAC is switched off": that is a MIRROR of a
    /// switch, not an expiry. Round 12's own predicate answers the same way here, so this is not a
    /// round-13 regression — what round 13 added was the SENTENCE at Pages/Settings.razor telling
    /// the operator the hatch stops granting once bootstrap is complete, which was false.</para>
    ///
    /// <para><b>What round 14 held.</b> A bootstrap hatch is a ONE-WAY DOOR: the fact that this
    /// install has HAD an administrator who could sign in was recorded durably, in the same file
    /// and the same parse as the flag, and nothing this application did lowered it.
    ///
    /// <para><b>And round 15 deleted the flag, so most of this file went with it (2026-08-03).</b>
    /// The latch was defeated in turn — <c>RecordLogin</c> never raised it, so with
    /// <c>requireExplicitAccess=false</c> and <c>defaultRole=admin</c> an operator could sign in
    /// through the shipped path, leaving a fully bootstrapped install with a null latch and the
    /// hatch still open to the LAN. That is the fourth expiry to be defeated by a state transition
    /// nobody had enumerated, and enumerating a fifth time was the wrong move. What was deleted
    /// here: every test of the latch mechanism, of the settings-save carry-over, and of the
    /// one-lock read of flag-plus-expiry, because none of those terms exist. What SURVIVES is
    /// round 14's item 2 — the <see cref="ConfigLoadOutcome"/> discriminator on
    /// <c>rbac-config.json</c>, which is load-bearing for the enforcement report and is the thing
    /// the round-15 banner is now derived from — plus the wire tests, which assert the same
    /// refusals unconditionally instead of with a flag set.</para>

    /// </summary>
    public class RbacRound14RegressionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacRound14RegressionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-r14-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        // ── The install the cold gate drove ──────────────────────────────

        /// <summary>
        /// Bootstrap complete: an enabled Windows admin, and the Windows provider on. The
        /// <c>bootstrapFlag</c> parameter this used to take is gone with the flag itself.
        /// </summary>
        private static RbacConfig BootstrapCompleteInstall(bool rbacOn) => new()
        {
            Enabled = rbacOn,
            Windows = new WindowsAuthConfig { Enabled = true },
        };

        private static RbacUser TheAdmin() => new()
        {
            Email = @"MSI\afsul",
            DisplayName = "Configured Admin",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Admin,
            Enabled = true,
        };

        private static string IntactStore() => JsonSerializer.Serialize(new List<RbacUser> { TheAdmin() });

        private RbacService Build(RbacConfig config, string? rawUsersJson, ILogger<RbacService>? logger = null)
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (rawUsersJson != null) File.WriteAllText(_usersPath, rawUsersJson);
            return new RbacService(logger ?? NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        /// <summary>Reopens the same two files, as a restart would.</summary>
        private RbacService Restart() =>
            new(NullLogger<RbacService>.Instance, _configPath, _usersPath);

        /// <summary>
        /// What Settings.SaveRbacConfig hands to UpdateConfig: a FRESH RbacConfig built from the
        /// form fields, carrying no latch. Every save on that page has this shape, which is why the
        /// service — not the call site — has to be the thing that refuses to lower it.
        /// </summary>
        private static RbacConfig AsSettingsWouldSave(bool rbacOn, bool windowsOn = true) => new()
        {
            Enabled = rbacOn,
            Windows = new WindowsAuthConfig { Enabled = windowsOn },
        };

        // ── Item 1 is GONE, and its absence is the round-15 change ──────
        //
        // Everything that stood here exercised allowRemoteBootstrapAdmin and its latch:
        // SwitchingRbacOffDoesNotReopenTheRemoteHatch, AStrangerOnASwitchedOffInstallHoldsNoPermission,
        // TheLatchIsWrittenToTheConfigFileAndSurvivesARestart, RemovingEveryAdminDoesNotReopenTheHatch,
        // NoSettingsSaveCanLowerTheLatch, AGenuinelyFreshInstallStillGetsTheHatch,
        // TheLatchWaitsUntilTheAdminCanActuallySignIn, TheLatchAlsoFiresOnThePathOnboardingActuallyTakes,
        // ClearingTheLatchOnDiskReopensOnlyAnInstallThatCannotSignAnyoneIn.
        //
        // Their subject no longer exists. The claim they were collectively working towards — that
        // no state of this install admits an anonymous non-loopback caller — is now asserted
        // directly and unconditionally by RbacRound15RegressionTests, including structurally, so
        // that reintroducing either key fails a test rather than passing one.
        //
        // Two of them left a residue worth keeping, and it moved rather than being dropped: the
        // permission short-circuit (bootstrapEligible makes IsAuthorized answer true for EVERY
        // permission, which is why the eligibility term has to be right) and the break-glass
        // guarantee on loopback. Both are asserted in RbacRound15RegressionTests and in
        // RbacBootstrapScopeTests.


        // ── Item 2: the discriminator now covers the file the predicate reads ──

        /// <summary>
        /// Round 13 built <see cref="ConfigLoadOutcome"/> and used it for the USER store, while the
        /// expiry and the lapse report both moved onto <c>Config.Enabled</c> — read from
        /// rbac-config.json, still loaded without it. A damaged config yields defaults,
        /// <c>Enabled=false</c>, indistinguishable from "the operator switched it off", and
        /// <see cref="RbacService.DescribeEnforcementLapse"/> early-returned on exactly that.
        /// Correct reasoning over a fact that could no longer be trusted.
        /// </summary>
        [Theory]
        [InlineData("truncated", "{\"enabled\": true, \"windows\": {\"enab")]
        [InlineData("empty", "")]
        [InlineData("wrong-shape", "[]")]
        [InlineData("literal-null", "null")]
        public void ADamagedConfigIsReportedAsALapseInItsOwnRight(string shape, string content)
        {
            File.WriteAllText(_configPath, content);
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);

            Assert.True(rbac.IsConfigStoreDamaged, $"the {shape} config should be classified as damaged");

            var reason = Assert.Single(rbac.DescribeEnforcementLapse());
            Assert.Contains(_configPath, reason, StringComparison.Ordinal);          // WHICH file
            Assert.Contains("did not load", reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("defaults", reason, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// FAIL-SILENT, not fail-open, and said out loud. When this was written the boundary held
        /// in the damaged state because defaults read <c>AllowRemoteBootstrapAdmin=false</c>; it
        /// now holds because no configuration, damaged or otherwise, has anything to say about a
        /// remote caller. The defect round 14 was pointed at is unchanged: the install could not
        /// SAY it had stopped enforcing, and saying so is the deliverable.
        ///
        /// <para>The raw JSON below still names the dead key deliberately — a config left over
        /// from an older install must grant nothing.</para>
        /// </summary>
        [Fact]
        public void ADamagedConfigStillRefusesTheStranger()
        {
            File.WriteAllText(_configPath, "{\"enabled\": true, \"allowRemoteBootstrapAdmin\": tr");
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);

            Assert.True(rbac.IsConfigStoreDamaged);
            Assert.False(rbac.IsBootstrapEligible(loopback: false));
            Assert.True(rbac.IsBootstrapEligible(loopback: true));
            Assert.NotEmpty(rbac.DescribeEnforcementLapse());
        }

        /// <summary>
        /// The opposite direction, so the new lapse cannot be a banner that is always on. A MISSING
        /// config is a fresh install; a config that PARSED and says off is the operator's choice.
        /// Neither is a lapse.
        /// </summary>
        [Fact]
        public void ANonDamagedConfigIsNeverALapseForThisReason()
        {
            var fresh = Build(new RbacConfig(), rawUsersJson: null);
            File.Delete(_configPath);
            Assert.False(Restart().IsConfigStoreDamaged);
            Assert.Empty(Restart().DescribeEnforcementLapse());
            Assert.False(fresh.IsConfigStoreDamaged);

            var switchedOff = Build(BootstrapCompleteInstall(rbacOn: false), IntactStore());
            Assert.False(switchedOff.IsConfigStoreDamaged);
            Assert.Empty(switchedOff.DescribeEnforcementLapse());
        }

        /// <summary>
        /// The LOG half. The installed service has no screen, so the log is the only place its
        /// posture can be read after the fact — and the line it used to write in this state said
        /// "Access control is switched OFF", attributing to the operator a state they never chose.
        /// </summary>
        [Fact]
        public void ADamagedConfigIsWarnedAboutRatherThanCalledSwitchedOff()
        {
            var log = new CapturingLogger();
            File.WriteAllText(_configPath, "{\"enabled\": true, \"win");
            File.WriteAllText(_usersPath, IntactStore());

            _ = new RbacService(log, _configPath, _usersPath);

            var warning = Assert.Single(log.Entries.Where(e => e.Level == LogLevel.Warning
                                                               && e.Message.Contains("[RBAC]", StringComparison.Ordinal)));
            Assert.Contains("DID NOT LOAD", warning.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("switched OFF", warning.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                log.Entries,
                e => e.Message.Contains("switched OFF", StringComparison.Ordinal));
        }

        /// <summary>
        /// Repairing through the UI clears it without a restart, the same way a user save
        /// reclassifies a damaged user store. A banner that outlives its cause stops being read.
        ///
        /// <para>⚠ AMENDED 2026-08-04 by D4 of the 2026-08-03 cold gate, and the amendment is the
        /// point. This test used to drive the repair with a PLAIN <c>UpdateConfig</c> — which is
        /// also what the Enable-RBAC checkbox did on <c>@bind:after</c>, from a form filled with
        /// built-in defaults. So the property this test was protecting ("the banner clears without a
        /// restart") was being delivered by the mechanism that made one click overwrite the damaged
        /// file and destroy the evidence it had ever been damaged.</para>
        ///
        /// <para>The property survives; the mechanism is now the operator's explicit
        /// <see cref="StoreWriteIntent.ReplaceUnreadableStore"/>, given on a page that has told them
        /// the file did not load and that saving discards it. The second half below is the new half:
        /// without that acknowledgement the same call writes nothing, so the banner correctly
        /// outlives a click that was never a repair. See RbacDamagedStoreWriteTests.</para>
        /// </summary>
        [Fact]
        public void SavingTheConfigRepairsADamagedConfigInPlace()
        {
            File.WriteAllText(_configPath, "{\"enabled\": true, \"win");
            File.WriteAllText(_usersPath, IntactStore());
            var rbac = new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
            Assert.True(rbac.IsConfigStoreDamaged);

            // Not a repair: no operator has said the file may be discarded, so nothing is written
            // and the install goes on saying it cannot read its configuration.
            var unacknowledged = rbac.UpdateConfig(AsSettingsWouldSave(rbacOn: true));
            Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable, unacknowledged);
            Assert.True(rbac.IsConfigStoreDamaged);

            // A repair: the operator has.
            rbac.UpdateConfig(AsSettingsWouldSave(rbacOn: true), StoreWriteIntent.ReplaceUnreadableStore);

            Assert.False(rbac.IsConfigStoreDamaged);
            Assert.Empty(rbac.DescribeEnforcementLapse());
            Assert.True(rbac.IsRbacEnforced());
        }

        // ── Item 3 is GONE, for the same reason as item 1 ───────────────
        //
        // TheFlagAndItsExpiryAreReadUnderOneLock drove four reader threads against a concurrent
        // UpdateConfig and asserted that eligibility was never assembled from two different
        // config objects — 13 torn opens in 14,229 evaluations before the fix. There is nothing
        // left to tear: IsBootstrapEligible reads one bool argument and touches no shared state,
        // so it does not take the lock at all. A concurrency test over a pure function would be a
        // test that cannot fail, which is worse than no test.

        // ── Over the wire, from a real non-loopback origin ───────────────

        /// <summary>
        /// THE DECIDER. The cold gate's exact install, driven through the shipped front door from a
        /// real non-loopback IPv4 of this machine: bootstrap complete, RBAC switched OFF, and no
        /// file damage of any kind. It used to need "flag left on" as a fourth condition to
        /// reproduce; the flag is gone, so the state is simply an install with RBAC switched off.
        ///
        /// <para>Note what is NOT asserted here: <c>enforced</c> is false in this state and stays
        /// false — RBAC really is switched off — so the report tells the truth about enforcement
        /// while the hatch tells the truth about admission. Those are different questions, and
        /// conflating them is what produced the defect.</para>
        /// </summary>
        [Fact]
        public async Task ABootstrapCompleteInstallWithRbacOffAdmitsNoStranger()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(
                BootstrapCompleteInstall(rbacOn: false), IntactStore());

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.NonLoopbackBase);

            var me = await client.GetStringAsync("/auth/me");
            Assert.Contains("\"authenticated\":false", me, StringComparison.Ordinal);
            Assert.Contains("\"loopback\":false", me, StringComparison.Ordinal);
            Assert.Contains("\"enforced\":false", me, StringComparison.Ordinal);      // the precondition
            Assert.Contains("\"bootstrapEligible\":false", me, StringComparison.Ordinal);

            foreach (var path in new[] { "/servers", "/query", "/settings", "/server-configuration" })
            {
                using var navigation = new HttpRequestMessage(HttpMethod.Get, path);
                navigation.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

                using var page = await client.SendAsync(navigation);
                Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
                Assert.DoesNotContain(
                    InteractiveAppAdmissionHost.InteractiveAppMarker,
                    await page.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            using var negotiate = await client.PostAsync("/_blazor/negotiate", new StringContent(""));
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
            Assert.DoesNotContain(
                InteractiveAppAdmissionHost.BlazorCircuitMarker,
                await negotiate.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The positive control on the same rig, moved to the origin that still holds the hatch.
        /// A genuinely unconfigured install IS served the application ON THE BOX, so "denied"
        /// above is a decision and not a fixture that stopped working.
        ///
        /// <para>It used to drive <c>NonLoopbackBase</c> with the flag on and assert a 200 for an
        /// anonymous LAN stranger. That is the behaviour deleted on 2026-08-03; a positive control
        /// that asserts the defect is not a control.</para>
        /// </summary>
        [Fact]
        public async Task TheHatchStillServesAFreshInstallOverTheWire()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(new RbacConfig());

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.LoopbackBase);

            var me = await client.GetStringAsync("/auth/me");
            Assert.Contains("\"bootstrapEligible\":true", me, StringComparison.Ordinal);

            var page = await client.GetAsync("/servers");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains(
                InteractiveAppAdmissionHost.InteractiveAppMarker,
                await page.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The operator is never bricked by any of this. On the box, with the hatch shut to the LAN
        /// and RBAC switched off, the whole application is still served — that is where recovery
        /// happens, and it is why the refusal can afford to be absolute.
        /// </summary>
        [Fact]
        public async Task LoopbackStillReachesTheAppWithTheHatchShutToTheLan()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(
                BootstrapCompleteInstall(rbacOn: false), IntactStore());

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.LoopbackBase);

            var page = await client.GetAsync("/settings");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains(
                InteractiveAppAdmissionHost.InteractiveAppMarker,
                await page.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        // ── Rig ──────────────────────────────────────────────────────────

        private sealed record LogEntry(LogLevel Level, string Message);

        private sealed class CapturingLogger : ILogger<RbacService>
        {
            public List<LogEntry> Entries { get; } = new();

            IDisposable? ILogger.BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (Entries) Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
