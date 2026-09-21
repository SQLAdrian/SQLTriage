/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

// SQLTriage.Data exports its own LogLevel; the alias picks the logging one, for this file only.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLTriage.Tests
{
    // BM:RbacRound15RegressionTests — the remote bootstrap grant is deleted, not guarded again
    /// <summary>
    /// Round 15. Three things, and the first is a deletion.
    ///
    /// <para><b>1. There is no remote bootstrap grant.</b> Four consecutive rounds wrote an expiry
    /// for <c>allowRemoteBootstrapAdmin</c> — "an unauthenticated client anywhere on the network is
    /// admin until this install has had a working administrator" — and four were defeated by a
    /// state transition nobody had enumerated: no expiry at all; <c>!IsRbacEnforced()</c>, a
    /// fail-SAFE predicate whose polarity inverts when read as an expiry; <c>Config.Enabled</c>, a
    /// mirror of a switch rather than an expiry; and a written-down one-way latch, defeated because
    /// <c>RecordLogin</c> never raised it, so an operator signing in through the shipped path with
    /// <c>requireExplicitAccess=false</c> and <c>defaultRole=admin</c> left a fully bootstrapped
    /// install with a null latch and the door open. A fifth guard would have been a fifth
    /// enumeration of the same open-ended set, so the grant was deleted. These tests assert that
    /// STRUCTURALLY as well as behaviourally: reintroducing either key fails a test rather than
    /// passing one.</para>
    ///
    /// <para><b>2. A circuit may not freeze its bootstrap eligibility.</b>
    /// <c>AppUserState</c> computed it once in <c>InitAsync</c> and never re-read it, and a circuit
    /// is HOURS — one open tab is one circuit. Measured on 2026-08-03: an anonymous circuit opened
    /// while the hatch was open, the operator completed bootstrap, the service correctly answered
    /// ineligible, and that circuit went on reporting eligible with <c>IsAuthorized</c> true for
    /// settings, manage_users, run_scripts and manage_servers — full admin on the term that
    /// SHORT-CIRCUITS every permission. The class had already solved exactly this for the role.</para>
    ///
    /// <para><b>3. The banner must say what the log says.</b> <c>Pages/Settings.razor</c> rendered a
    /// hard-coded headline — "RBAC is switched on but is NOT being enforced" — conditioned only on
    /// <c>lapse.Count &gt; 0</c>. Driven across all seven damaged-config shapes, every one gave
    /// <c>Enabled=false</c> and <c>lapse.Count=1</c>, so the banner asserted a switch position that
    /// had never been read, while <c>ReportEnforcementPosture</c> branched on the same fact and
    /// correctly said DID NOT LOAD. One register: both now render
    /// <see cref="RbacService.EnforcementPosture"/>.</para>
    /// </summary>
    public class RbacRound15RegressionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacRound15RegressionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-r15-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        /// <summary>
        /// Builds the service over RAW JSON rather than a serialized <see cref="RbacConfig"/>, on
        /// purpose: the keys under test no longer exist on the model, so a strongly-typed fixture
        /// could not name them — and naming them is the whole point. This is also what makes this
        /// file compile against the PREVIOUS commit, which is what a red proof needs.
        /// </summary>
        private RbacService BuildRaw(string configJson, string? usersJson = null, ILogger<RbacService>? logger = null)
        {
            File.WriteAllText(_configPath, configJson);
            if (usersJson != null) File.WriteAllText(_usersPath, usersJson);
            return new RbacService(logger ?? NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        private static RbacUser TheAdmin() => new()
        {
            Email = @"R15\admin",
            DisplayName = "Configured Admin",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Admin,
            Enabled = true,
        };

        private static string IntactStore() => JsonSerializer.Serialize(new List<RbacUser> { TheAdmin() });

        // ── 1. The deletion, structurally ────────────────────────────────

        /// <summary>
        /// The flag and its latch are GONE FROM THE MODEL. Asserted by reflection rather than by
        /// grep, so that reintroducing either — under any name that carries the same idea — fails
        /// here instead of passing review.
        ///
        /// <para>Property-name-based, because that is the shape of the thing being forbidden: a
        /// persisted field on the config object that participates in bootstrap eligibility. A
        /// future author who genuinely needs a bootstrap-related setting will have to change this
        /// test, which is exactly the visibility the last four rounds lacked.</para>
        /// </summary>
        [Fact]
        public void RbacConfigCarriesNoBootstrapPropertyAtAll()
        {
            var offenders = typeof(RbacConfig)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToArray();

            Assert.Empty(offenders);
        }

        /// <summary>
        /// …and neither key is written to <c>rbac-config.json</c> any more, so an install that
        /// saves its settings sheds them rather than carrying a dead security switch on disk where
        /// the next reader may believe it.
        /// </summary>
        [Fact]
        public void NeitherKeyIsWrittenToTheConfigFile()
        {
            var json = JsonSerializer.Serialize(new RbacConfig());

            Assert.DoesNotContain("allowRemoteBootstrapAdmin", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bootstrapCompletedUtc", json, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The disjunct itself. <see cref="RbacService.IsBootstrapEligible(bool)"/> takes ONE
        /// argument — the socket fact — so there is nowhere for a second term to be read from.
        /// </summary>
        [Fact]
        public void EligibilityTakesOneArgumentAndConsultsNothingElse()
        {
            var method = typeof(RbacService).GetMethod(
                nameof(RbacService.IsBootstrapEligible),
                BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(method);
            var parameter = Assert.Single(method!.GetParameters());
            Assert.Equal(typeof(bool), parameter.ParameterType);
        }

        // ── 1. The deletion, behaviourally ───────────────────────────────

        /// <summary>
        /// Every shape of the deleted keys an existing install could still be carrying, plus the
        /// switch states each of the four defeated expiries keyed off. NONE of them admits an
        /// anonymous non-loopback caller.
        ///
        /// <para>Each row is the state that beat one of the four rounds. Row 3 is round 13's defeat
        /// (bootstrap complete, RBAC switched off, flag on — no file damage at all). Row 4 is round
        /// 14's (the latch never raised, because the operator signed in through a path that did not
        /// raise it). Row 5 is round 12's (the fail-safe withholding enforcement because the user
        /// store will not parse). Every one of them was a live circuit for a stranger on the day it
        /// was measured.</para>
        /// </summary>
        public static TheoryData<string, string, string?> EveryLeftoverShape => new()
        {
            { "no-keys-at-all", "{}", null },
            { "flag-on-nothing-else", "{\"allowRemoteBootstrapAdmin\": true}", null },
            { "flag-on-rbac-off-bootstrap-complete",
              "{\"enabled\": false, \"allowRemoteBootstrapAdmin\": true, \"windows\": {\"enabled\": true}}", null },
            { "flag-on-latch-null-store-intact",
              "{\"enabled\": false, \"allowRemoteBootstrapAdmin\": true, \"bootstrapCompletedUtc\": null, \"windows\": {\"enabled\": true}}", null },
            { "flag-on-rbac-on-store-truncated",
              "{\"enabled\": true, \"allowRemoteBootstrapAdmin\": true, \"windows\": {\"enabled\": true}}",
              "[{\"email\":\"R15" },
            { "flag-on-rbac-on-enforcing",
              "{\"enabled\": true, \"allowRemoteBootstrapAdmin\": true, \"windows\": {\"enabled\": true}}", null },
            { "flag-on-config-truncated", "{\"enabled\": true, \"allowRemoteBootstrapAdmin\": tr", null },
            { "flag-on-requireExplicitAccess-off-defaultRole-admin",
              "{\"enabled\": false, \"allowRemoteBootstrapAdmin\": true, \"requireExplicitAccess\": false, \"defaultRole\": \"admin\", \"windows\": {\"enabled\": true}}", "[]" },
        };

        [Theory]
        [MemberData(nameof(EveryLeftoverShape))]
        public void NoConfigurationAdmitsAnAnonymousNonLoopbackCaller(string shape, string configJson, string? usersJson)
        {
            var rbac = BuildRaw(configJson, usersJson ?? IntactStore());

            Assert.False(rbac.IsBootstrapEligible(loopback: false), $"shape '{shape}' admitted a remote caller");

            // The loopback hatch is what the deletion had to preserve. Unconditional, in every row.
            Assert.True(rbac.IsBootstrapEligible(loopback: true), $"shape '{shape}' broke break-glass");
        }

        /// <summary>
        /// The consequence, one layer in, because eligibility is not one permission among many:
        /// it SHORT-CIRCUITS <see cref="RbacService.IsAuthorized(string, string, AppUserState.BootstrapEligibilityProof)"/> for every
        /// permission there is. That is why the term had to be right, and why deleting the remote
        /// arm was worth a round.
        /// </summary>
        [Theory]
        [InlineData("settings")]
        [InlineData("manage_users")]
        [InlineData("run_scripts")]
        [InlineData("manage_servers")]
        public void AStrangerHoldsNoPermissionOnTheStateThatBeatRoundFourteen(string permission)
        {
            // Round 14's defeat exactly: bootstrap complete through the shipped sign-in path, so
            // the latch was never raised; RBAC switched off; the flag left on.
            var rbac = BuildRaw(
                "{\"enabled\": false, \"allowRemoteBootstrapAdmin\": true, \"requireExplicitAccess\": false, "
                + "\"defaultRole\": \"admin\", \"windows\": {\"enabled\": true}}",
                "[]");

            var eligible = rbac.IsBootstrapEligible(loopback: false);
            Assert.False(eligible);
            Assert.False(rbac.IsAuthorized(
                AppRoles.Viewer, permission, eligible ? BootstrapProofForTests.Minted : null));

            // The short-circuit itself is unchanged and still total — which is the reason the
            // eligibility term above cannot be allowed to be generous. Since 2026-08-17 that term is
            // a capability token no caller can write; the mint here is by reflection, in the one
            // helper that does it (see BootstrapProofForTests).
            Assert.True(rbac.IsAuthorized(AppRoles.Viewer, permission, BootstrapProofForTests.Minted));
        }

        // ── 2. A circuit asks; it does not remember ──────────────────────

        private static AppUserState NewBrowserState(RbacService rbac)
            => new(HostEnvironmentInfo.BrowserHosted,
                   rbac,
                   new ServiceCollection().BuildServiceProvider(),
                   NullLogger<AppUserState>.Instance);

        /// <summary>
        /// THE MEASURED DEFECT. A circuit resolves, the underlying state then changes, and the
        /// circuit must not go on asserting what it resolved.
        ///
        /// <para>Driven the way it happens in production: the circuit opens first, the operator
        /// completes bootstrap afterwards. The assertion is the invariant rather than a literal —
        /// the circuit AGREES with the service, whatever the service says — because that is the
        /// property that has to hold at every instant, not just at the two this test samples.</para>
        ///
        /// <para>Both facts are asserted after the change, and the second is the one an operator
        /// would have felt: <c>IsAuthorized("settings")</c>, the gate behind every ShellGate,
        /// RbacGuard and toolbar control on the page.</para>
        /// </summary>
        [Fact]
        public async Task ACircuitDoesNotFreezeItsBootstrapEligibility()
        {
            // A fresh headless install as it stood before this round: the flag on, no user store.
            var rbac = BuildRaw("{\"allowRemoteBootstrapAdmin\": true, \"windows\": {\"enabled\": true}}");

            var circuit = NewBrowserState(rbac);
            await circuit.InitAsync();

            // No AuthenticationStateProvider and no HttpContext in this composition, so the circuit
            // resolves as an unauthenticated REMOTE caller — the stranger's circuit.
            Assert.False(circuit.IsLoopback);
            Assert.Equal(rbac.IsBootstrapEligible(loopback: false), circuit.IsBootstrapEligible);

            // The operator now completes bootstrap. Under the old code this was the moment the
            // service and this already-open circuit stopped agreeing, and the circuit won.
            rbac.AddUser(TheAdmin());

            Assert.Equal(rbac.IsBootstrapEligible(loopback: false), circuit.IsBootstrapEligible);
            Assert.False(circuit.IsBootstrapEligible);
            Assert.False(circuit.IsAuthorized("settings"));
            Assert.False(circuit.IsAuthorized("manage_users"));
            Assert.False(circuit.IsAuthorized("run_scripts"));
            Assert.False(circuit.IsAuthorized("manage_servers"));
        }

        /// <summary>
        /// The other half, so the fix is not "eligibility is always false". A LOOPBACK circuit on
        /// the same install keeps the hatch — and keeps it after the same state change, because
        /// break-glass is unconditional.
        /// </summary>
        [Fact]
        public async Task ALoopbackCircuitKeepsTheHatchAcrossTheSameStateChange()
        {
            var rbac = BuildRaw("{\"windows\": {\"enabled\": true}}");

            var circuit = NewBrowserState(rbac);
            await circuit.InitAsync();
            circuit.SetLoopbackForTests(true);

            Assert.True(circuit.IsBootstrapEligible);
            Assert.True(circuit.IsAuthorized("settings"));

            rbac.AddUser(TheAdmin());

            Assert.True(circuit.IsBootstrapEligible);
            Assert.Equal(rbac.IsBootstrapEligible(loopback: true), circuit.IsBootstrapEligible);
        }

        /// <summary>
        /// And the circuit tracks the fact it IS entitled to hold — the socket — rather than a
        /// cached verdict derived from it. Uses the test seam, which is the only way to move that
        /// fact after resolution; in production it cannot move at all, which is precisely why
        /// caching the verdict looked safe and was not.
        /// </summary>
        [Fact]
        public async Task EligibilityFollowsTheLoopbackFactRatherThanASnapshot()
        {
            var rbac = BuildRaw("{}");
            var circuit = NewBrowserState(rbac);
            await circuit.InitAsync();

            circuit.SetLoopbackForTests(true);
            Assert.True(circuit.IsBootstrapEligible);

            circuit.SetLoopbackForTests(false);
            Assert.False(circuit.IsBootstrapEligible);
        }

        // ── 3. One register: the banner says what the log says ───────────

        /// <summary>
        /// The seven damaged-config shapes the adversary drove, each paired with what it is now.
        ///
        /// <para>Six are damage and produce <see cref="RbacService.PostureKind.ConfigDidNotLoad"/>.
        /// The seventh — <c>bootstrapCompletedUtc</c> holding a number — was damage only because
        /// that key was a typed <c>DateTime?</c> on the model; with the key deleted it is an
        /// unmapped member and <c>System.Text.Json</c> skips it, so the file PARSES and the posture
        /// is whatever the file actually says. That is stated rather than hidden: the invariant
        /// under test is not "these seven are damaged", it is "the banner and the log say the same
        /// thing", and the seventh row exercises it on the other branch.</para>
        /// </summary>
        public static TheoryData<string, string, RbacService.PostureKind> SevenDamagedShapes => new()
        {
            { "truncated", "{\"enabled\": true, \"windows\": {\"enab", RbacService.PostureKind.ConfigDidNotLoad },
            { "empty", "", RbacService.PostureKind.ConfigDidNotLoad },
            { "whitespace", "   \n\t ", RbacService.PostureKind.ConfigDidNotLoad },
            { "wrong-type", "{\"enabled\": \"yes\"}", RbacService.PostureKind.ConfigDidNotLoad },
            { "wrong-shape", "[]", RbacService.PostureKind.ConfigDidNotLoad },
            { "literal-null", "null", RbacService.PostureKind.ConfigDidNotLoad },
            { "bad-latch-type", "{\"enabled\": true, \"bootstrapCompletedUtc\": 12345}", RbacService.PostureKind.Lapsed },
        };

        /// <summary>
        /// THE ITEM-3 ASSERTION. For every shape: the banner's headline is the sentence the log
        /// wrote, verbatim, and the log's decision to WARN is the banner's decision to render.
        ///
        /// <para>What this replaces: the banner printed "RBAC is switched on but is NOT being
        /// enforced" for six of these seven, in states where <c>rbac-config.json</c> had never been
        /// read and <c>Enabled=false</c> came from built-in defaults. The log, branching on the
        /// same fact, said DID NOT LOAD. Two registers for one fact.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(SevenDamagedShapes))]
        public void EveryDamagedConfigShapeGivesABannerHeadlineThatMatchesTheLog(
            string shape, string content, RbacService.PostureKind expected)
        {
            var log = new CapturingLogger();
            var rbac = BuildRaw(content, IntactStore(), log);

            var posture = rbac.DescribeEnforcementPosture();
            Assert.Equal(expected, posture.Kind);
            Assert.True(posture.IsProblem, $"shape '{shape}' should be worth telling an operator about");

            // The banner renders Headline; the log wrote a line. They must be the same sentence.
            var warning = Assert.Single(log.Entries.Where(e => e.Level == LogLevel.Warning
                                                               && e.Message.Contains("[RBAC]", StringComparison.Ordinal)));
            Assert.Contains(posture.Headline, warning.Message, StringComparison.Ordinal);
            Assert.Contains(posture.Detail, warning.Message, StringComparison.Ordinal);

            // And the headline must not assert a switch position nobody read.
            if (expected == RbacService.PostureKind.ConfigDidNotLoad)
            {
                Assert.Equal("The access-control configuration DID NOT LOAD.", posture.Headline);
                Assert.DoesNotContain("switched ON", posture.Headline, StringComparison.Ordinal);
                Assert.True(rbac.IsConfigStoreDamaged);
            }
            else
            {
                // The file parsed and really does say enabled:true, so the switch claim is earned.
                Assert.Equal("Access control is switched ON but is NOT BEING ENFORCED.", posture.Headline);
                Assert.False(rbac.IsConfigStoreDamaged);
            }
        }

        /// <summary>
        /// The positive control for the branch above: a config that DID load, says on, and is not
        /// being enforced because the user store is unreadable. The switch claim is true here and
        /// the banner is entitled to make it — otherwise "never says switched on" would be a fix
        /// that removed the message rather than conditioning it.
        /// </summary>
        [Fact]
        public void AGenuineLapseStillSaysTheSwitchIsOn()
        {
            var log = new CapturingLogger();
            var rbac = BuildRaw(
                "{\"enabled\": true, \"windows\": {\"enabled\": true}}",
                "[{\"email\":\"R15",
                log);

            var posture = rbac.DescribeEnforcementPosture();

            Assert.Equal(RbacService.PostureKind.Lapsed, posture.Kind);
            Assert.Contains("switched ON", posture.Headline, StringComparison.Ordinal);
            Assert.NotEmpty(posture.Reasons);

            var warning = Assert.Single(log.Entries.Where(e => e.Level == LogLevel.Warning
                                                               && e.Message.Contains("[RBAC]", StringComparison.Ordinal)));
            Assert.Contains(posture.Headline, warning.Message, StringComparison.Ordinal);
            foreach (var reason in posture.Reasons)
                Assert.Contains(reason, warning.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The other opposite direction: ENFORCING is not a problem, so the banner stays off and
        /// the log stays at Information. A banner that is always on is not a banner.
        ///
        /// <para>⚠ AMENDED 2026-09-07, and the amendment is deliberate. This was a [Theory] over
        /// BOTH healthy states — Enforcing and Off — asserting that neither ever writes a
        /// <c>[RBAC]</c> WARNING. Adrian's fresh-eyes ruling of 2026-09-06 ("accept and be loud")
        /// moves Off: the state is still accepted and still not <c>IsProblem</c>, but an install
        /// that serves every caller from its own console as a full administrator is now announced
        /// at WARNING and bannered on every page. The Off case therefore moved to
        /// <see cref="TheBootstrapAdminPostureIsAnnouncedLoudlyWithoutBecomingAProblem"/>, which
        /// asserts the NEW rule rather than being deleted. Enforcing is unchanged and stays here,
        /// because "silent when enforced" is the half of the old assertion that still holds and is
        /// what stops the loud arm from becoming an always-on banner.</para>
        /// </summary>
        [Fact]
        public void EnforcingIsNotAProblemAndIsLoggedAtInformation()
        {
            var log = new CapturingLogger();
            var rbac = BuildRaw("{\"enabled\": true, \"windows\": {\"enabled\": true}}", IntactStore(), log);

            var posture = rbac.DescribeEnforcementPosture();
            Assert.Equal(RbacService.PostureKind.Enforcing, posture.Kind);
            Assert.False(posture.IsProblem);
            Assert.Empty(posture.Reasons);

            Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning
                                                    && e.Message.Contains("[RBAC]", StringComparison.Ordinal));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
                                              && e.Message.Contains(posture.Headline, StringComparison.Ordinal));
        }

        /// <summary>
        /// The Off half of the same fact, under the 2026-09-06 ruling. Off is STILL not a problem —
        /// <c>IsProblem</c> is unmoved, so the red admission banner and the fail-closed authorization
        /// path both behave exactly as before — and it is nonetheless announced at WARNING, with the
        /// scope sentence attached. Both halves matter: a ruling that made Off a "problem" would have
        /// silently changed what <c>RbacService.IsAuthorized</c> does on a never-configured install.
        /// </summary>
        [Fact]
        public void TheBootstrapAdminPostureIsAnnouncedLoudlyWithoutBecomingAProblem()
        {
            var log = new CapturingLogger();
            var rbac = BuildRaw("{\"enabled\": false}", IntactStore(), log);

            var posture = rbac.DescribeEnforcementPosture();
            Assert.Equal(RbacService.PostureKind.Off, posture.Kind);
            Assert.False(posture.IsProblem);
            Assert.Empty(posture.Reasons);

            // The scope is the service's sentence, so the banner and the log cannot disagree about
            // how far the hatch reaches.
            Assert.Contains("only for connections from this machine", posture.Detail, StringComparison.Ordinal);

            var warning = Assert.Single(log.Entries.Where(e => e.Level == LogLevel.Warning
                                                               && e.Message.Contains("[RBAC]", StringComparison.Ordinal)));
            Assert.Contains(posture.Headline, warning.Message, StringComparison.Ordinal);
            Assert.Contains(posture.Detail, warning.Message, StringComparison.Ordinal);
        }

        // ── 3. …and the markup actually renders it ───────────────────────

        /// <summary>
        /// The C# above proves the posture object is right. It cannot prove the PAGE renders it —
        /// the defect was a literal sitting beside a computed condition, and only the shipped
        /// markup can answer that. Settings.razor is copied into the test output for this reason
        /// (see the test .csproj), the same way AdminGuard, CioDashboard and CheckTrend are.
        ///
        /// ⚠ THIS IS A LINT, NOT A BOUNDARY, and the name says so because the previous name did not.
        /// It rejects FOUR KNOWN LITERALS. It cannot detect a claim nobody has thought of yet: the
        /// 2026-08-03 cold gate injected
        /// <c>&lt;strong&gt;Access control is active on this install and every request is being
        /// checked.&lt;/strong&gt;</c> into this very fixture and BOTH markup tests still passed — the
        /// fifth static instrument in this wave defeated by a fifth vocabulary, after verb lexicons,
        /// prefix anchoring, receiver aliasing and markup position.
        ///
        /// What actually holds the invariant is at RUNTIME and above: DescribeEnforcementPosture is
        /// the single register, the page binds it rather than composing anything, and the
        /// banner-equals-log assertions cover the state matrix. Treat a pass here as "the four known
        /// regressions have not returned", never as "the page states nothing it cannot support".
        /// </summary>
        [Fact]
        public void TheSettingsBannerBindsTheComputedPosture_AndRejectsTheFourKnownLiterals()
        {
            var markup = ReadSettingsMarkup();

            // The banner block binds the computed sentence…
            Assert.Contains("@EnforcementPosture.Headline", markup, StringComparison.Ordinal);
            Assert.Contains("@EnforcementPosture.Detail", markup, StringComparison.Ordinal);
            Assert.Contains("EnforcementPosture.IsProblem", markup, StringComparison.Ordinal);

            // …and the literal it used to hard-code is gone, in both the wording that shipped and
            // the wording the service now uses. A page must not restate a verdict it was handed.
            Assert.DoesNotContain("RBAC is switched on but", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("is NOT being enforced", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NOT BEING ENFORCED", markup, StringComparison.Ordinal);

            // The old condition is gone too: rendering on a reason COUNT is what let the headline
            // and the log disagree, because the count is true in states the headline is false in.
            Assert.DoesNotContain("RbacEnforcementLapse", markup, StringComparison.Ordinal);
        }

        /// <summary>
        /// The deleted switch must not come back as a control either. The Settings page carried a
        /// first-class "Allow remote bootstrap admin" checkbox, which is how an operator could
        /// arrive at the state four rounds tried to guard.
        ///
        /// ⚠ Also a LINT over two literals — a control re-added under any other name or binding
        /// passes this. The load-bearing guard is that <c>RbacService.IsBootstrapEligible</c> takes
        /// one bool and reads nothing off disk (asserted structurally above), so there is no state
        /// for such a control to set.
        /// </summary>
        [Fact]
        public void TheSettingsPageCarriesNoneOfTheTwoKnownRemoteBootstrapControlLiterals()
        {
            var markup = ReadSettingsMarkup();

            Assert.DoesNotContain("_rbacAllowRemoteBootstrap", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Allow remote bootstrap admin", markup, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadSettingsMarkup()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "Settings.razor");
            Assert.True(File.Exists(path),
                $"Settings.razor was not copied to the test output ({path}); the markup assertions below would silently pass.");
            return File.ReadAllText(path);
        }

        // ── Rig ──────────────────────────────────────────────────────────

        /// <summary>Renders the message template the way a sink would, so assertions see the text an operator sees.</summary>
        private sealed class CapturingLogger : ILogger<RbacService>
        {
            internal List<(LogLevel Level, string Message)> Entries { get; } = new();

            IDisposable? ILogger.BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
