/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:RbacLoopbackAdmissionScopeTests — security-8: a lapsed store must not re-open loopback admin
    /// <summary>
    /// security-8 (fresh-eyes triage, 2026-08-31): a previously-ENFORCED install whose RBAC store is
    /// later DAMAGED silently reverts to full loopback admin, because <see cref="RbacService.IsRbacEnforced"/>
    /// flips false on a lapse and two authorization sites read that coarse boolean and grant the anon
    /// loopback caller the bootstrap hatch — full admin.
    ///
    /// <para><b>The fix (Adrian's ruling, C+D+B combined).</b> When the posture
    /// (<see cref="RbacService.DescribeEnforcementPosture"/>) IsProblem — Lapsed or ConfigDidNotLoad —
    /// the anon loopback caller now resolves to the ENFORCED viewer result at BOTH sites, not admin.
    /// The NEVER-configured case (PostureKind.Off) is untouched: the break-glass hatch that lets an
    /// operator bootstrap a fresh install from the box stays byte-identical (the B1/SEC-3 ruling of
    /// 2026-08-26 is not reopened). The recovery surfaces stay reachable through
    /// <see cref="AppUserState.IsAuthorizedWithBreakGlass"/>, which is independent of this decision,
    /// and re-seeding restores/creates the admin CREDENTIAL without elevating the caller's session —
    /// they stay viewer until they authenticate as the re-seeded admin.</para>
    ///
    /// <para>The two populations are different and the boundary between them is the whole point:
    /// <see cref="RbacBootstrapScopeTests"/> owns "never configured → loopback break-glasses"; this
    /// file owns "was configured, store damaged → loopback does NOT".</para>
    /// </summary>
    public class RbacLoopbackAdmissionScopeTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacLoopbackAdmissionScopeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-lapse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        private RbacService Build() => new(NullLogger<RbacService>.Instance, _configPath, _usersPath);

        private static AppUserState LoopbackCircuit(RbacService rbac)
        {
            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted,
                rbac,
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<AppUserState>.Instance);
            state.SetLoopbackForTests(true);   // an anonymous circuit arriving on the box
            return state;
        }

        private static HttpContext ApiRequest(string remoteIp)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            // The Host header is attacker-controlled and must not move any answer.
            ctx.Request.Host = new HostString("localhost", 5155);
            return ctx;
        }

        private static RbacUser TheWindowsAdmin() => new()
        {
            Email = @"MSI\admin.adrian",
            DisplayName = "Configured Admin",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Admin,
            Enabled = true,
        };

        private static RbacConfig EnforcingConfig()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            return config;
        }

        /// <summary>The permissions behind the gated pages security-8 re-opened.</summary>
        public static TheoryData<string> AdminGatedPermissions => new()
        {
            "settings", "manage_servers", "manage_users", "manage_alerts",
            "run_scripts", "execute_checks",
        };

        // ── (C) The Pages site: a lapse holds the loopback caller at viewer ─────────

        /// <summary>
        /// LAPSED because the user store is damaged. Config says Enabled=true (persisted proof the
        /// operator turned RBAC on), the store is truncated, so IsRbacEnforced() is false and the
        /// posture is Lapsed. The anon loopback caller must get the ENFORCED viewer answer — false
        /// for every admin/operator permission — not the bootstrap hatch.
        /// </summary>
        [Theory]
        [MemberData(nameof(AdminGatedPermissions))]
        public void LapsedByDamagedUserStore_OnLoopback_GetsViewerNotAdmin(string permission)
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(EnforcingConfig()));
            File.WriteAllText(_usersPath, "[{\"email\": \"real.admin@contoso.com\", \"ro");   // truncated

            var rbac = Build();
            Assert.False(rbac.IsRbacEnforced());                                     // precondition: it lapsed
            Assert.Equal(RbacService.PostureKind.Lapsed, rbac.DescribeEnforcementPosture().Kind);

            var state = LoopbackCircuit(rbac);

            Assert.False(state.IsAuthorized(permission),
                $"A DAMAGED store on a previously-enforced install must not re-open '{permission}' "
                + "to an anonymous loopback caller — that is security-8.");
        }

        /// <summary>
        /// LAPSED because the config store itself is damaged (ConfigDidNotLoad) — the other
        /// IsProblem posture. Same requirement at the same site.
        /// </summary>
        [Theory]
        [MemberData(nameof(AdminGatedPermissions))]
        public void ConfigDidNotLoad_OnLoopback_GetsViewerNotAdmin(string permission)
        {
            File.WriteAllText(_configPath, "{\"enabled\": true, \"windows\": {\"enab");        // truncated config
            File.WriteAllText(_usersPath, JsonSerializer.Serialize(new[] { TheWindowsAdmin() }));

            var rbac = Build();
            Assert.False(rbac.IsRbacEnforced());
            Assert.Equal(RbacService.PostureKind.ConfigDidNotLoad, rbac.DescribeEnforcementPosture().Kind);

            var state = LoopbackCircuit(rbac);

            Assert.False(state.IsAuthorized(permission),
                $"A DAMAGED config on a previously-enforced install must not re-open '{permission}'.");
        }

        /// <summary>A lapsed loopback caller still READS — it drops to viewer, not to nothing.</summary>
        [Fact]
        public void LapsedByDamagedUserStore_OnLoopback_StillReadsDashboards()
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(EnforcingConfig()));
            File.WriteAllText(_usersPath, "[{\"email\": \"real.admin@contoso.com\", \"ro");

            var state = LoopbackCircuit(Build());

            Assert.True(state.IsAuthorized("view_dashboard"));
            Assert.True(state.IsAuthorized("view_results"));
        }

        // ── (B1) The never-configured hatch is byte-identical ──────────────────────

        /// <summary>
        /// The whole point of the seam: a NEVER-configured install (PostureKind.Off — no config, no
        /// users) is NOT a problem posture, so the loopback break-glass hatch is untouched. This is
        /// the exact assertion RbacBootstrapScopeTests already makes; it is restated here so a
        /// regression in THIS lane's branch is caught beside the change that could cause it.
        /// </summary>
        [Theory]
        [MemberData(nameof(AdminGatedPermissions))]
        public void NeverConfigured_OnLoopback_StillBreakGlassesToAdmin(string permission)
        {
            var rbac = Build();   // neither file exists — a fresh install
            Assert.False(rbac.DescribeEnforcementPosture().IsProblem);
            Assert.Equal(RbacService.PostureKind.Off, rbac.DescribeEnforcementPosture().Kind);

            var state = LoopbackCircuit(rbac);

            Assert.True(state.IsAuthorized(permission),
                $"A never-configured install must still grant '{permission}' on loopback — the B1 hatch "
                + "is not reopened by the security-8 fix, and this branch must stay byte-identical.");
        }

        // ── (C) The API site: the same lapse withholds the loopback hatch ──────────

        /// <summary>
        /// The non-fail-closed API read hatch (ApiAuthorization step 3) is granted for a
        /// never-configured install on loopback, and WITHHELD once the install has lapsed. Driven
        /// directly against <see cref="ApiAuthorization.Evaluate"/>, the same decision the shipped
        /// endpoint filter makes.
        /// </summary>
        [Fact]
        public void Api_NeverConfigured_OnLoopback_IsAllowed()
        {
            var outcome = ApiAuthorization.Evaluate(
                ApiRequest("127.0.0.1"), Build(), "view_results", failClosedWithoutApiKey: false);

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Allowed, outcome);
        }

        [Fact]
        public void Api_LapsedByDamagedUserStore_OnLoopback_IsNotAllowed()
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(EnforcingConfig()));
            File.WriteAllText(_usersPath, "[{\"email\": \"real.admin@contoso.com\", \"ro");

            var rbac = Build();
            Assert.False(rbac.IsRbacEnforced());
            Assert.True(rbac.DescribeEnforcementPosture().IsProblem);

            var outcome = ApiAuthorization.Evaluate(
                ApiRequest("127.0.0.1"), rbac, "view_results", failClosedWithoutApiKey: false);

            // The hatch is withheld, so an anonymous unauthenticated caller resolves to the enforced
            // result — 401, exactly as a healthy enforcing install would answer.
            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Unauthenticated, outcome);
        }

        [Fact]
        public void Api_ConfigDidNotLoad_OnLoopback_IsNotAllowed()
        {
            File.WriteAllText(_configPath, "{\"enabled\": true, \"windows\": {\"enab");
            File.WriteAllText(_usersPath, JsonSerializer.Serialize(new[] { TheWindowsAdmin() }));

            var rbac = Build();
            Assert.Equal(RbacService.PostureKind.ConfigDidNotLoad, rbac.DescribeEnforcementPosture().Kind);

            var outcome = ApiAuthorization.Evaluate(
                ApiRequest("::1"), rbac, "view_results", failClosedWithoutApiKey: false);

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Unauthenticated, outcome);
        }

        // ── (D) The recovery carve-out: reachable, but it does NOT elevate the caller ──

        /// <summary>
        /// The re-seed surface stays reachable during a lapse. Break-glass
        /// (<see cref="AppUserState.IsAuthorizedWithBreakGlass"/>) is independent of the enforcement
        /// decision the security-8 fix changed, so an operator can still reach Settings/Onboarding on
        /// the box to repair — recovery is not forced to be filesystem-only.
        /// </summary>
        [Fact]
        public void LapsedInstall_RecoverySurfaceStaysReachableOnLoopback()
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(EnforcingConfig()));
            File.WriteAllText(_usersPath, "[{\"email\": \"real.admin@contoso.com\", \"ro");

            var rbac = Build();
            Assert.True(rbac.DescribeEnforcementPosture().IsProblem);

            var onBox = LoopbackCircuit(rbac);
            Assert.False(onBox.IsAuthorized("settings"));                    // the plain gate denies (C)
            Assert.True(onBox.IsAuthorizedWithBreakGlass("settings"));       // …but the recovery surface stays open (D)
        }

        /// <summary>And break-glass stays scoped to the box: a lapsed REMOTE caller gets nothing.</summary>
        [Fact]
        public void LapsedInstall_RecoverySurfaceIsNotReachableFromTheNetwork()
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(EnforcingConfig()));
            File.WriteAllText(_usersPath, "[{\"email\": \"real.admin@contoso.com\", \"ro");

            var remote = new AppUserState(
                HostEnvironmentInfo.BrowserHosted, Build(),
                new ServiceCollection().BuildServiceProvider(), NullLogger<AppUserState>.Instance);
            remote.SetLoopbackForTests(false);

            Assert.False(remote.IsAuthorized("settings"));
            Assert.False(remote.IsAuthorizedWithBreakGlass("settings"));
        }

        /// <summary>
        /// THE LOAD-BEARING CARVE-OUT PROOF. A re-seed performed during a lapse creates the admin
        /// CREDENTIAL, but it must NOT elevate the anonymous session that performed it. Here the user
        /// store is HEALTHY (empty, so the write is permitted) and the lapse is "no enabled Admin";
        /// the caller re-seeds an admin through the sanctioned mutator, and the SAME circuit is still
        /// viewer afterwards. Enforcement is in fact restored by the re-seed, so the anon session
        /// drops to plain viewer — it can become admin only by AUTHENTICATING as the new credential.
        /// </summary>
        [Fact]
        public void ReseedDuringLapse_DoesNotElevateTheCurrentSessionToAdmin()
        {
            // Enabled=true + a usable provider, but NO admin yet → Lapsed, store healthy (missing).
            File.WriteAllText(_configPath, JsonSerializer.Serialize(EnforcingConfig()));

            var rbac = Build();
            Assert.False(rbac.IsRbacEnforced());
            Assert.Equal(RbacService.PostureKind.Lapsed, rbac.DescribeEnforcementPosture().Kind);

            var state = LoopbackCircuit(rbac);

            // Before the re-seed: the escalation is closed (viewer), but the surface is reachable.
            Assert.False(state.IsAuthorized("run_scripts"));
            Assert.False(state.IsAuthorized("settings"));
            Assert.True(state.IsAuthorizedWithBreakGlass("settings"));

            // The re-seed itself — the sanctioned mutator the recovery surface calls. It WRITES the
            // credential; it signs nobody in.
            Assert.Equal(StoreWriteOutcome.Saved, rbac.AddUser(TheWindowsAdmin()));

            // The SAME anonymous circuit is STILL viewer — the write did not elevate it. Enforcement
            // is now restored, so the caller holds the plain enforced viewer result, exactly as any
            // unauthenticated circuit would on a healthy enforcing install.
            Assert.True(rbac.IsRbacEnforced());
            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.False(state.IsAuthorized("run_scripts"));
            Assert.False(state.IsAuthorized("settings"));
            Assert.False(state.IsAuthorized("manage_servers"));
        }

        /// <summary>
        /// The stronger half of the carve-out safety, on the DAMAGED-store lapse: the recovery
        /// surface is reachable, but the store write guard REFUSES a blind clobber of the damaged
        /// user store, so an attacker in the lapse window cannot re-seed themselves an admin by
        /// overwriting it — and the session is never elevated either way. Recovery for this case is
        /// the sanctioned quarantine/restore path, not a blind write.
        /// </summary>
        [Fact]
        public void ReseedOverADamagedStore_IsRefusedAndDoesNotElevate()
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(EnforcingConfig()));
            File.WriteAllText(_usersPath, "[{\"email\": \"real.admin@contoso.com\", \"ro");   // damaged

            var rbac = Build();
            Assert.True(rbac.IsUserStoreDamaged);

            var state = LoopbackCircuit(rbac);
            Assert.True(state.IsAuthorizedWithBreakGlass("settings"));       // reachable…

            // …but the write guard refuses to clobber the damaged store (WouldOverwriteUnreadStore).
            Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable, rbac.AddUser(TheWindowsAdmin()));

            // No elevation happened, and the store is still damaged (still says so to the operator).
            Assert.False(state.IsAuthorized("settings"));
            Assert.False(state.IsAuthorized("run_scripts"));
            Assert.True(rbac.IsUserStoreDamaged);
        }

        // ── architecture-12: the refusal-dedup set is bounded ──────────────────────

        /// <summary>
        /// <c>InteractiveAppAdmission._reported</c> dedups refusal log lines per remote address and
        /// never evicted, so an address-rotating scanner grew it for the process lifetime. The set
        /// is now capped at <see cref="InteractiveAppAdmission.MaxReportedRemotes"/>. Driven through
        /// the extracted decision so the bound can be measured without a full HTTP pipeline: past the
        /// cap, a genuinely new address stops being announced AND stops being stored.
        /// </summary>
        [Fact]
        public void RefusalDedupSet_IsBounded()
        {
            // _reported is a process-wide static, and InteractiveAppAdmissionTests drives real
            // refusals through the middleware in parallel, which adds a small fixed set of foreign
            // addresses. This test is therefore written to tolerate a handful of foreign entries:
            // it asserts the BOUND, which is what architecture-12 is about. Pre-fix, driving
            // cap+5000 distinct addresses left the set holding cap+5000; post-fix it holds ~cap.
            InteractiveAppAdmission.ResetReportedForTests();
            try
            {
                var cap = InteractiveAppAdmission.MaxReportedRemotes;

                var suppressedPastCap = 0;
                for (var i = 0; i < cap + 5000; i++)
                {
                    var announced = InteractiveAppAdmission.ShouldAnnounceRefusal(
                        $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}:{i}");
                    if (!announced) suppressedPastCap++;
                }

                // The leak is closed: the set stays near the cap however many distinct addresses
                // arrive. The slack absorbs the parallel admission test's few foreign entries and
                // any concurrent add that races the count check; it is three orders of magnitude
                // below the pre-fix cap+5000, so it still tells a fixed set from a leaking one.
                Assert.True(InteractiveAppAdmission.ReportedRemoteCount <= cap + 128,
                    $"the refusal-dedup set grew to {InteractiveAppAdmission.ReportedRemoteCount}; it must stay "
                    + $"bounded near the cap of {cap} rather than growing with every distinct address.");

                // And the overwhelming majority of the surplus was suppressed rather than stored —
                // proof the cap engaged, not merely that Count happened to be small.
                Assert.True(suppressedPastCap >= 4000,
                    $"only {suppressedPastCap} of ~5000 surplus refusals were suppressed; the cap is not engaging.");
            }
            finally
            {
                InteractiveAppAdmission.ResetReportedForTests();
            }
        }

        /// <summary>An address already announced is not announced twice — the dedup still dedups.</summary>
        [Fact]
        public void RefusalDedup_StillSuppressesARepeatFromTheSameAddress()
        {
            InteractiveAppAdmission.ResetReportedForTests();
            try
            {
                Assert.True(InteractiveAppAdmission.ShouldAnnounceRefusal("203.0.113.7:52000"));
                Assert.False(InteractiveAppAdmission.ShouldAnnounceRefusal("203.0.113.7:52000"));
            }
            finally
            {
                InteractiveAppAdmission.ResetReportedForTests();
            }
        }
    }
}
