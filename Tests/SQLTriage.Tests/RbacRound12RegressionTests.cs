/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
    // BM:RbacRound12RegressionTests — a fail-SAFE predicate must never be an expiry
    /// <summary>
    /// Round 12, item 1: the fail-open that lived INSIDE round 9's own fix.
    ///
    /// <para>Round 9 scoped the remote bootstrap hatch as
    /// <c>loopback || (AllowRemoteBootstrapAdmin &amp;&amp; !IsRbacEnforced())</c>. That closed the
    /// door round 9 was pointed at and opened a quieter one, because
    /// <see cref="RbacService.IsRbacEnforced"/> is a RUNTIME, DISK-DERIVED, FAIL-SAFE predicate: it
    /// deliberately WITHHOLDS enforcement whenever the install looks unusable, so an operator can
    /// never lock themselves out. Composed into an authorization decision its polarity inverts —
    /// "this install looks unusable" becomes "let anonymous strangers in".</para>
    ///
    /// <para><b>Two triggers, both measured, both silent, neither requiring an attacker.</b> With
    /// the flag on and RBAC fully configured: (1) the user store stops parsing — truncated by a
    /// half-written <c>SaveUsers</c>, emptied, or the wrong shape — and the install logs
    /// <c>Loaded 0 RBAC users</c>, reports <c>enforced:false</c>, and answers a LAN stranger 200 on
    /// <c>/servers</c>, 200 on <c>/query</c> and 200 on <c>POST /_blazor/negotiate</c>, which is a
    /// live circuit. (2) The admin's sign-in provider becomes unusable — <c>windows.enabled</c>
    /// toggled off, or an OAuth client secret expiring — and the same three 200s appear. With an
    /// intact store and a working provider: 302 / 302 / 401.</para>
    ///
    /// <para><b>What these tests hold.</b> The fail-safe term is not consulted by any authorization
    /// path, a damaged store fails CLOSED rather than reading as "no users yet", and an install
    /// that falls out of enforcement SAYS SO — which is the actual defect. A security posture that
    /// changes because a file got truncated must not change silently.</para>
    ///
    /// <para><b>Rewritten 2026-08-03, and the rewrite is worth reading.</b> Round 12 replaced the
    /// fail-safe expiry with a stored one; rounds 13 and 14 replaced that in turn; round 15 deleted
    /// the grant all four expiries were guarding. So the tests below no longer set a flag, and the
    /// assertions they used to make CONDITIONALLY — a remote anonymous caller is refused — are now
    /// unconditional. Their subject is unchanged: a predicate that withholds enforcement when the
    /// install looks unusable must never be read as a permission.</para>
    /// </summary>
    public class RbacRound12RegressionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacRound12RegressionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-r12-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        /// <summary>
        /// A fully configured install: RBAC on, one enabled Windows admin, Windows auth on.
        ///
        /// <para>This used to take a <c>bootstrapFlag</c> parameter, because every test below had
        /// to say whether the remote bootstrap grant was on. The grant was deleted on 2026-08-03
        /// and the parameter went with it: there is no configuration in which these tests could
        /// admit a remote anonymous caller, which is a stronger statement than the one they were
        /// written to make.</para>
        /// </summary>
        private static RbacConfig ConfiguredInstall() => new()
        {
            Enabled = true,
            Windows = new WindowsAuthConfig { Enabled = true },
        };

        private static RbacUser TheAdmin() => new()
        {
            Email = @"R12\admin",
            DisplayName = "Configured Admin",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Admin,
            Enabled = true,
        };

        private static string IntactStore() => JsonSerializer.Serialize(new List<RbacUser> { TheAdmin() });

        private RbacService Build(RbacConfig config, string? rawUsersJson)
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (rawUsersJson != null) File.WriteAllText(_usersPath, rawUsersJson);
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        // ── The corrupted user store ─────────────────────────────────────

        /// <summary>
        /// The three shapes a user store fails in, each on a fully configured install with the flag
        /// on. Every one of them produced a live circuit for an anonymous LAN caller under the
        /// round-9 predicate; none may now be eligible.
        /// </summary>
        public static TheoryData<string, string> DamagedStores => new()
        {
            // A half-written SaveUsers. Save is atomic today, but nothing about the FILE guarantees
            // that for a hand edit, an editor that truncates, or a disk that filled mid-write.
            { "truncated", "[{\"email\":\"R12\\\\admin\",\"role\":\"admin\",\"ena" },

            // Zero bytes. No writer in this codebase produces it; something else did.
            { "empty", "" },

            // Valid JSON, wrong shape — an object where a list belongs. This is what a hand edit
            // that "tidied" the file into { "users": [...] } leaves behind.
            { "wrong-shape", "{\"users\":[]}" },
        };

        [Theory]
        [MemberData(nameof(DamagedStores))]
        public void ADamagedUserStoreDoesNotReopenTheRemoteHatch(string shape, string content)
        {
            var rbac = Build(ConfiguredInstall(), content);

            // The precondition that made this a fail-OPEN: the fail-safe reports NOT enforced,
            // exactly as it is designed to, because it cannot see any usable admin.
            Assert.False(rbac.IsRbacEnforced());

            // …and that must no longer buy a stranger anything. This is the assertion round 9's
            // shape failed, for all three shapes. (The IsRemoteBootstrapExpired assertion that
            // stood beside it went with the grant it expired — there is nothing left to expire.)
            Assert.False(rbac.IsBootstrapEligible(loopback: false));

            // Loopback break-glass is untouched — that is the recovery route, and it is the only
            // one that should exist for a damaged install.
            Assert.True(rbac.IsBootstrapEligible(loopback: true));

            // And the install says what happened rather than quietly downgrading itself.
            var lapse = rbac.DescribeEnforcementLapse();
            Assert.NotEmpty(lapse);
            Assert.Contains(lapse, r => r.Contains("did not load", StringComparison.Ordinal));
            Assert.True(rbac.IsUserStoreDamaged, $"the {shape} store should be classified as damaged");
        }

        /// <summary>
        /// The discriminator that makes the test above possible: a MISSING store is a fresh install
        /// and is not damage. It no longer decides anything about the hatch — nothing off disk
        /// does — but it still decides whether the install is reported as LAPSED, so a truncated
        /// store must not read as "a brand-new install with no users yet".
        /// </summary>
        [Fact]
        public void AMissingStoreIsAFreshInstallAndNotDamage()
        {
            var rbac = Build(new RbacConfig(), rawUsersJson: null);

            Assert.False(rbac.IsUserStoreDamaged);
            Assert.Empty(rbac.DescribeEnforcementLapse());

            // The hatch on the freshest install there is: still the box only.
            Assert.True(rbac.IsBootstrapEligible(loopback: true));
            Assert.False(rbac.IsBootstrapEligible(loopback: false));
        }

        /// <summary>
        /// A store holding a well-formed empty list is also not damage — <c>[]</c> is what
        /// <c>SaveUsers</c> writes when the last user is removed.
        /// </summary>
        [Fact]
        public void AnEmptyListIsNotDamage()
        {
            var rbac = Build(new RbacConfig(), "[]");

            Assert.False(rbac.IsUserStoreDamaged);
            Assert.True(rbac.IsBootstrapEligible(loopback: true));
            Assert.False(rbac.IsBootstrapEligible(loopback: false));
        }

        // ── The unusable sign-in provider ────────────────────────────────

        /// <summary>
        /// Trigger 2. The store is intact and RBAC is on; only the PROVIDER became unusable. The
        /// fail-safe correctly stops enforcing — and under round 9's shape that alone readmitted
        /// anonymous strangers from the LAN.
        /// </summary>
        [Theory]
        [InlineData("windows-auth-switched-off")]
        [InlineData("oauth-secret-unusable")]
        public void AnUnusableSignInProviderDoesNotReopenTheRemoteHatch(string how)
        {
            var config = ConfiguredInstall();
            var users = new List<RbacUser> { TheAdmin() };

            if (how == "windows-auth-switched-off")
            {
                // The whole change: one checkbox, on an install that was enforcing a second ago.
                config.Windows.Enabled = false;
            }
            else
            {
                // An OAuth admin whose client secret is gone — the shape a rotated or expired
                // secret leaves behind. DescribeOAuthProblem calls that provider unusable, so no
                // enabled admin can sign in.
                config.Windows.Enabled = false;
                config.Google = new OAuthProviderConfig
                {
                    Enabled = true,
                    ClientId = "1234.apps.googleusercontent.com",
                    ClientSecret = "",
                };
                users = new List<RbacUser>
                {
                    new()
                    {
                        Email = "admin@example.test",
                        Provider = AuthProviders.Google,
                        Role = AppRoles.Admin,
                        Enabled = true,
                    },
                };
            }

            var rbac = Build(config, JsonSerializer.Serialize(users));

            Assert.False(rbac.IsRbacEnforced());               // the fail-safe, working as designed
            Assert.False(rbac.IsBootstrapEligible(false));     // and buying a stranger nothing
            Assert.True(rbac.IsBootstrapEligible(true));       // break-glass intact

            var lapse = rbac.DescribeEnforcementLapse();
            Assert.NotEmpty(lapse);
            Assert.Contains(lapse, r => r.Contains("sign in", StringComparison.Ordinal));
        }

        // ── The shape of the expiry itself ───────────────────────────────

        /// <summary>
        /// Eligibility must not consult the fail-safe at all. Pinned as a matrix with the usability
        /// term varied INDEPENDENTLY of the switch: whether an admin can actually sign in changes
        /// <see cref="RbacService.IsRbacEnforced"/> and must change nothing about eligibility, in
        /// either direction, for either kind of caller.
        ///
        /// <para>The flag column that used to head this table is gone, and what it bought is now
        /// unavailable: every row expects a remote caller to be refused.</para>
        /// </summary>
        [Theory]
        //         rbacOn, providerUsable
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]     // switched on and enforcing
        [InlineData(true, false)]    // switched on, NOT enforcing — the fail-safe withholding
        public void EligibilityDoesNotMoveWithTheFailSafe(bool rbacOn, bool providerUsable)
        {
            var config = new RbacConfig
            {
                Enabled = rbacOn,
                Windows = new WindowsAuthConfig { Enabled = providerUsable },
            };

            var rbac = Build(config, JsonSerializer.Serialize(new List<RbacUser> { TheAdmin() }));

            // The fail-safe moves with the usability term…
            Assert.Equal(rbacOn && providerUsable, rbac.IsRbacEnforced());

            // …and eligibility does not, in either direction.
            Assert.False(rbac.IsBootstrapEligible(loopback: false));
            Assert.True(rbac.IsBootstrapEligible(loopback: true));
        }

        /// <summary>
        /// A config file that does not parse yields defaults, and the hatch is shut for a remote
        /// caller in that state — as it is in every state. This used to assert that the FLAG read
        /// false out of the defaults, which was the fail-closed direction for a security flag. The
        /// flag is gone, so the assertion is about the DECISION rather than the field, and the raw
        /// JSON below still names the dead key on purpose: a leftover key must grant nothing.
        /// </summary>
        [Fact]
        public void AnUnparseableConfigClosesTheHatchRatherThanOpeningIt()
        {
            File.WriteAllText(_configPath, "{\"enabled\": true, \"allowRemoteBootstrapAdmin\": tr");
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);

            Assert.True(rbac.IsConfigStoreDamaged);
            Assert.False(rbac.IsBootstrapEligible(loopback: false));
            Assert.True(rbac.IsBootstrapEligible(loopback: true));
        }

        // ── The lapse must be visible, not only true ─────────────────────

        /// <summary>
        /// The actual defect, stated as a test: an install can fall out of enforcement with no
        /// operator action, and before this round the only place that showed was the
        /// <c>enforced</c> field of <c>/auth/me</c>. These strings are what the Settings banner
        /// renders and what <c>ReportEnforcementPosture</c> writes to the log.
        /// </summary>
        [Fact]
        public void ALapseNamesItsCauseInTermsAnOperatorCanActOn()
        {
            // A store that will not parse.
            var damaged = Build(ConfiguredInstall(), "[{\"email\":");
            var reason = Assert.Single(damaged.DescribeEnforcementLapse());
            Assert.Contains("user store", reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(_usersPath, reason, StringComparison.Ordinal);   // WHICH file

            // A provider the operator switched off under an admin who needs it.
            var offConfig = ConfiguredInstall();
            offConfig.Windows.Enabled = false;
            var noProvider = Build(offConfig, IntactStore());
            var providerReason = Assert.Single(noProvider.DescribeEnforcementLapse());
            Assert.Contains("Windows authentication is switched off", providerReason, StringComparison.Ordinal);

            // RBAC on with nobody to enforce against.
            var noAdmin = Build(ConfiguredInstall(), "[]");
            var adminReason = Assert.Single(noAdmin.DescribeEnforcementLapse());
            Assert.Contains("No enabled Admin", adminReason, StringComparison.Ordinal);
        }

        /// <summary>
        /// The LOG half of the surfacing requirement, measured rather than asserted in prose. A
        /// banner only helps somebody looking at the screen; the installed service has no screen,
        /// and the log is the only place its posture can be read after the fact.
        /// </summary>
        [Fact]
        public void ALapseIsWrittenToTheLogAsAWarningAtStartup()
        {
            var log = new CapturingLogger();
            File.WriteAllText(_configPath, JsonSerializer.Serialize(ConfiguredInstall()));
            File.WriteAllText(_usersPath, "[{\"email\":");

            _ = new RbacService(log, _configPath, _usersPath);

            var warning = Assert.Single(log.Entries.Where(e => e.Level == LogLevel.Warning
                                                               && e.Message.Contains("NOT BEING ENFORCED", StringComparison.Ordinal)));
            Assert.Contains("switched ON", warning.Message, StringComparison.Ordinal);
            Assert.Contains("did not load", warning.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// …and an enforcing install says so once, at Information, so the log distinguishes
        /// "enforcing" from "nothing was logged because the code path never ran".
        /// </summary>
        [Fact]
        public void AnEnforcingInstallSaysSoInTheLogToo()
        {
            var log = new CapturingLogger();
            File.WriteAllText(_configPath, JsonSerializer.Serialize(ConfiguredInstall()));
            File.WriteAllText(_usersPath, IntactStore());

            _ = new RbacService(log, _configPath, _usersPath);

            Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
                                              && e.Message.Contains("ENFORCED", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning
                                                    && e.Message.Contains("NOT BEING ENFORCED", StringComparison.Ordinal));
        }

        /// <summary>Renders the message template the way a sink would, so assertions see the text an operator sees.</summary>
        private sealed class CapturingLogger : ILogger<RbacService>
        {
            internal List<(LogLevel Level, string Message)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }

        /// <summary>
        /// Switched OFF is not a lapse — nothing has fallen away, the operator chose it. A banner
        /// that fires on a deliberate state is a banner nobody reads.
        /// </summary>
        [Fact]
        public void RbacSwitchedOffIsNotALapse()
        {
            var rbac = Build(new RbacConfig { Enabled = false }, "not json at all");
            Assert.Empty(rbac.DescribeEnforcementLapse());
        }

        /// <summary>
        /// Enforcing is not a lapse either — the positive control for the banner.
        /// </summary>
        [Fact]
        public void AnEnforcingInstallReportsNoLapse()
        {
            var rbac = Build(ConfiguredInstall(), IntactStore());

            Assert.True(rbac.IsRbacEnforced());
            Assert.Empty(rbac.DescribeEnforcementLapse());
        }

        /// <summary>
        /// One register: <see cref="RbacService.IsRbacEnforced"/> and
        /// <see cref="RbacService.DescribeEnforcementLapse"/> answer the same question, so the
        /// banner can never say "enforcing" while the gates act as though it were not, or the
        /// reverse.
        /// </summary>
        [Theory]
        [MemberData(nameof(DamagedStores))]
        public void TheBannerAndTheGatesReadOneRegister(string shape, string content)
        {
            _ = shape;
            var rbac = Build(ConfiguredInstall(), content);
            Assert.Equal(rbac.IsRbacEnforced(), rbac.DescribeEnforcementLapse().Count == 0);
        }

        // ── The load outcome that makes all of the above possible ────────

        /// <summary>
        /// <see cref="ConfigFileHelper.Load{T}(string, JsonSerializerOptions?)"/> hands back
        /// <c>new T()</c> for four different reasons and the plain overload cannot say which. Every
        /// assertion above rests on the overload that can.
        /// </summary>
        [Theory]
        [InlineData(null, ConfigLoadOutcome.Missing)]
        [InlineData("", ConfigLoadOutcome.Empty)]
        [InlineData("   \r\n ", ConfigLoadOutcome.Empty)]
        [InlineData("[", ConfigLoadOutcome.Unreadable)]
        [InlineData("{\"users\":[]}", ConfigLoadOutcome.Unreadable)]
        [InlineData("null", ConfigLoadOutcome.Unreadable)]
        [InlineData("[]", ConfigLoadOutcome.Loaded)]
        public void TheLoaderSaysWhyItReturnedDefaults(string? content, ConfigLoadOutcome expected)
        {
            var path = Path.Combine(_tempDir, "probe-" + Guid.NewGuid().ToString("N") + ".json");
            if (content != null) File.WriteAllText(path, content);

            var loaded = ConfigFileHelper.Load<List<RbacUser>>(path, null, out var outcome);

            Assert.Equal(expected, outcome);
            Assert.Empty(loaded);
        }

        /// <summary>
        /// ⚠ INVERTED 2026-08-04. Was <c>SavingAUserRepairsADamagedStoreInPlace</c>, which asserted
        /// that adding a user over an unreadable <c>rbac-users.json</c> reclassified the store as
        /// healthy — protecting the right property (a banner must not outlive its cause) through a
        /// mechanism that was itself the defect. A truncated user store deserialises to an EMPTY
        /// LIST; "repairing in place" meant writing that empty list plus the one new account over a
        /// file that may have held fifty, deleting every account, role and password hash on the
        /// install, and overwriting the damaged file that was the only evidence of it.
        ///
        /// <para><b>Why this store gets no discard-and-re-enter button while the config store does.</b>
        /// A configuration is a dozen fields an operator can re-enter from the screen in front of
        /// them, so offering to replace it is a real repair. A user store is an arbitrary list of
        /// principals and Argon2 hashes that cannot be re-derived from anything; a button offering
        /// to replace it would be a button that deletes accounts. The recovery is therefore the one
        /// the enforcement banner already names — restore the file, from the <c>.rejected-</c> copy
        /// kept beside it — or move it aside, which makes the store MISSING rather than damaged, and
        /// missing is a fresh install that takes writes normally. That last step is asserted here so
        /// it is a measured route out and not a claim.</para>
        /// </summary>
        [Fact]
        public void ADamagedUserStoreIsNotRepairedByWritingOverIt()
        {
            var damaged = "[{\"email\":\"R12\\\\adm";
            var rbac = Build(ConfiguredInstall(), damaged);
            Assert.True(rbac.IsUserStoreDamaged);

            var refused = rbac.AddUser(TheAdmin());

            Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable, refused);
            Assert.True(rbac.IsUserStoreDamaged);
            Assert.Equal(damaged, File.ReadAllText(_usersPath));
            Assert.NotEmpty(rbac.DescribeEnforcementLapse());

            // The route out, measured: take the unreadable file away and the store is a fresh one.
            File.Move(_usersPath, _usersPath + ".set-aside");
            var afterSettingItAside = Build(ConfiguredInstall(), rawUsersJson: null);

            Assert.False(afterSettingItAside.IsUserStoreDamaged);
            Assert.Equal(StoreWriteOutcome.Saved, afterSettingItAside.AddUser(TheAdmin()));
            Assert.True(afterSettingItAside.IsRbacEnforced());
            Assert.Empty(afterSettingItAside.DescribeEnforcementLapse());
        }

        // ── Over the wire, from a real non-loopback origin ───────────────

        /// <summary>
        /// THE DECIDER for item 1, driven the way round 8 established: real Kestrel, real sockets,
        /// a real non-loopback IPv4 of this machine, the shipped front door.
        ///
        /// <para>A unit test of <see cref="RbacService.IsBootstrapEligible"/> alone would not have
        /// caught round 9's defect either — the round-9 predicate was self-consistent, and every
        /// caller asked it. What was wrong was the FACT it consulted, and the only way to see the
        /// consequence is to be the stranger.</para>
        /// </summary>
        [Fact]
        public async Task ADamagedUserStoreDoesNotAdmitAStrangerOverTheWire()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(
                ConfiguredInstall(),
                "[{\"email\":\"R12\\\\admin\",\"role\":\"admin\",\"ena");

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.NonLoopbackBase);

            // The precondition, from the app's own mouth: the fail-safe has stopped enforcing.
            // If this ever reads true the test is proving nothing.
            var me = await client.GetStringAsync("/auth/me");
            Assert.Contains("\"enforced\":false", me, StringComparison.Ordinal);

            // …and the hatch is shut anyway.
            Assert.Contains("\"bootstrapEligible\":false", me, StringComparison.Ordinal);

            await AssertStrangerGetsNothing(client);
        }

        /// <summary>Trigger 2 over the wire: the provider, not the store.</summary>
        [Fact]
        public async Task AnUnusableProviderDoesNotAdmitAStrangerOverTheWire()
        {
            var config = ConfiguredInstall();
            config.Windows.Enabled = false;          // one checkbox

            await using var host = await InteractiveAppAdmissionHost.StartAsync(
                config, JsonSerializer.Serialize(new List<RbacUser> { TheAdmin() }));

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.NonLoopbackBase);

            var me = await client.GetStringAsync("/auth/me");
            Assert.Contains("\"enforced\":false", me, StringComparison.Ordinal);
            Assert.Contains("\"bootstrapEligible\":false", me, StringComparison.Ordinal);

            await AssertStrangerGetsNothing(client);
        }

        /// <summary>
        /// The positive control, moved to the origin that still holds the hatch. Same host, an
        /// install that has NOT been switched on: the caller ON THE BOX is admitted and served the
        /// app. Without this, "denied" is indistinguishable from "the whole fixture stopped
        /// working".
        ///
        /// <para>It used to be driven from <c>NonLoopbackBase</c> with
        /// <c>AllowRemoteBootstrapAdmin</c> on, asserting a 200 for an anonymous LAN stranger as
        /// the CONTROL. That is the behaviour deleted on 2026-08-03, so the control moved rather
        /// than being dropped — a positive control that asserts the defect is not a control.</para>
        /// </summary>
        [Fact]
        public async Task TheHatchStillOpensAnInstallThatWasNeverSwitchedOn()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(new RbacConfig());

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.LoopbackBase);

            var page = await client.GetAsync("/servers");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains(
                InteractiveAppAdmissionHost.InteractiveAppMarker,
                await page.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The operator is not bricked. With the store damaged and the hatch shut to the LAN, a
        /// caller on the box still gets the whole application — that is where recovery happens.
        /// </summary>
        [Fact]
        public async Task LoopbackStillReachesTheAppWithADamagedStore()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(
                ConfiguredInstall(),
                "[{\"email\":\"R12\\\\admin\",\"role\":\"adm");

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.LoopbackBase);

            var page = await client.GetAsync("/settings");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains(
                InteractiveAppAdmissionHost.InteractiveAppMarker,
                await page.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The three responses the round-9 shape returned as 200/200/200. Named as one helper so
        /// both triggers assert the same thing and neither can drift into asserting less.
        /// </summary>
        private static async Task AssertStrangerGetsNothing(HttpClient client)
        {
            foreach (var path in new[] { "/servers", "/query" })
            {
                using var navigation = new HttpRequestMessage(HttpMethod.Get, path);
                navigation.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

                using var page = await client.SendAsync(navigation);
                Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);

                var body = await page.Content.ReadAsStringAsync();
                Assert.DoesNotContain(
                    InteractiveAppAdmissionHost.InteractiveAppMarker, body, StringComparison.Ordinal);
            }

            // The circuit is the one that matters: a refused document with a live /_blazor is an
            // interactive application reachable by a hand-written client.
            using var negotiate = await client.PostAsync("/_blazor/negotiate", new StringContent(""));
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);

            var negotiated = await negotiate.Content.ReadAsStringAsync();
            Assert.DoesNotContain(
                InteractiveAppAdmissionHost.BlazorCircuitMarker, negotiated, StringComparison.Ordinal);
        }
    }
}
