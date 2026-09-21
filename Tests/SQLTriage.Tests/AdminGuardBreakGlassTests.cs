/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:AdminGuardBreakGlassTests — adminguard-breakglass: the recovery password gates the loopback break-glass
    /// <summary>
    /// adminguard-breakglass (Adrian's ruling, 2026-09-01). The orphaned AdminGuard password is
    /// WIRED as a deliberate, store-independent break-glass recovery credential: when it is set,
    /// the loopback break-glass term of <see cref="AppUserState.IsAuthorizedWithBreakGlass"/> binds
    /// behind it in ANY posture (Interpretation 2). No password → open (the cold-start carve-out,
    /// byte-identical B1).
    ///
    /// <para>These prove the mechanism by EXERCISE, mirroring
    /// <see cref="RbacLoopbackAdmissionScopeTests"/> (the circuit + posture idioms) and
    /// <see cref="AdminAuthGateTests"/> (the AdminAuthService fixtures):
    /// <list type="number">
    /// <item>an authenticated admin never sees the gate — no double-auth;</item>
    /// <item>no-password + never-configured (Off) → break-glass OPEN, byte-identical B1;</item>
    /// <item>password-SET + never-configured (Off) → GATED, must unlock — the deliberate B1 amendment;</item>
    /// <item>password-SET + lapsed store → gated, recover via unlock;</item>
    /// <item>unlock opens the recovery FORM but the caller stays viewer — no self-elevation;</item>
    /// <item>the onboarding opt-out persists the declined flag, and the loud warning is wired;</item>
    /// <item>the filesystem escape — blanking AdminAuth:Hash/Salt — reopens recovery.</item>
    /// </list></para>
    /// </summary>
    public class AdminGuardBreakGlassTests : IDisposable
    {
        private readonly List<string> _tempDirs = new();

        public void Dispose()
        {
            foreach (var d in _tempDirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
                catch (IOException) { /* best effort */ }
            }
        }

        // ── Fixtures ──────────────────────────────────────────────────────────

        private string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "adminguard-bg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private static IConfiguration Config(string? hash = null, string? salt = null) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdminAuth:Hash"] = hash ?? "",
                    ["AdminAuth:Salt"] = salt ?? "",
                })
                .Build();

        /// <summary>An AdminAuthService with a recovery password set (and LOCKED until unlocked).</summary>
        private AdminAuthService PasswordAuth(string password)
        {
            var (hash, salt) = AdminAuthService.HashPassword(password);
            return new AdminAuthService(Config(hash, salt), new InstallProvenanceService(NewDir()));
        }

        /// <summary>An AdminAuthService with NO recovery password (the cold-start carve-out).</summary>
        private AdminAuthService NoPasswordAuth() =>
            new(Config(), new InstallProvenanceService(NewDir()));

        private RbacService Rbac(string dir)
            => new(NullLogger<RbacService>.Instance,
                   Path.Combine(dir, "rbac-config.json"),
                   Path.Combine(dir, "rbac-users.json"));

        /// <summary>Never configured: neither RBAC file exists → posture Off (not a problem).</summary>
        private RbacService OffRbac() => Rbac(NewDir());

        /// <summary>Previously enforced, user store since damaged → posture Lapsed (a problem).</summary>
        private RbacService LapsedRbac()
        {
            var dir = NewDir();
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            File.WriteAllText(Path.Combine(dir, "rbac-config.json"), JsonSerializer.Serialize(config));
            File.WriteAllText(Path.Combine(dir, "rbac-users.json"), "[{\"email\": \"real.admin@contoso.com\", \"ro");   // truncated
            return Rbac(dir);
        }

        /// <summary>Enforced and healthy: RBAC on, one enabled Windows admin.</summary>
        private RbacService EnforcedRbacWithAdmin()
        {
            var dir = NewDir();
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            File.WriteAllText(Path.Combine(dir, "rbac-config.json"), JsonSerializer.Serialize(config));
            File.WriteAllText(Path.Combine(dir, "rbac-users.json"), JsonSerializer.Serialize(new[]
            {
                new RbacUser
                {
                    Email = @"MSI\admin.adrian", DisplayName = "Configured Admin",
                    Provider = AuthProviders.Windows, Role = AppRoles.Admin, Enabled = true,
                },
            }));
            return Rbac(dir);
        }

        /// <summary>
        /// A browser circuit with an AdminAuthService registered in its provider, optionally
        /// carrying a planted role. An anonymous circuit plants no role (resolves to viewer).
        /// </summary>
        private static AppUserState Circuit(RbacService rbac, AdminAuthService? adminAuth, bool loopback, string? role = null)
        {
            var services = new ServiceCollection();
            if (adminAuth != null) services.AddSingleton(adminAuth);

            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted,
                rbac,
                services.BuildServiceProvider(),
                NullLogger<AppUserState>.Instance);

            if (role != null) state.SetRole(role);
            state.SetLoopbackForTests(loopback);
            return state;
        }

        // ── 1. Authenticated admin: no double-auth, ever ──────────────────────

        [Fact]
        public void AuthenticatedAdmin_OnLoopback_NeverSeesTheGate_EvenWithARecoveryPasswordSet()
        {
            var auth = PasswordAuth("recovery-pass");   // set and LOCKED
            var state = Circuit(EnforcedRbacWithAdmin(), auth, loopback: true, role: AppRoles.Admin);

            Assert.False(auth.IsUnlocked, "precondition: the recovery password is locked");
            Assert.True(state.IsAuthorizedWithBreakGlass("settings"),
                "an admin is authorised BY ROLE, so the break-glass recovery password must never gate them.");
        }

        [Fact]
        public void AuthenticatedAdmin_FromTheNetwork_IsUnaffectedByTheRecoveryPassword()
        {
            var auth = PasswordAuth("recovery-pass");
            var state = Circuit(EnforcedRbacWithAdmin(), auth, loopback: false, role: AppRoles.Admin);

            Assert.True(state.IsAuthorizedWithBreakGlass("settings"), "role authorisation is unchanged remotely too.");
        }

        // ── 2. No password + never configured (Off) → OPEN, byte-identical B1 ─

        [Fact]
        public void NoPassword_NeverConfigured_OnLoopback_BreakGlassStaysOpen_ByteIdenticalB1()
        {
            var auth = NoPasswordAuth();
            var state = Circuit(OffRbac(), auth, loopback: true);   // anonymous viewer

            Assert.True(state.IsAuthorizedWithBreakGlass("settings"),
                "no recovery password → the cold-start carve-out keeps break-glass OPEN.");
            Assert.Equal(AppUserState.BreakGlassPosture.OpenNoPassword, state.BreakGlassState);

            // And the plain bootstrap hatch is untouched — the NO-password never-configured case is
            // byte-identical B1.
            Assert.True(state.IsAuthorized("settings"),
                "the unconfigured-install bootstrap hatch still grants settings on loopback.");
        }

        // ── 3. Password set + never configured (Off) → GATED (the B1 amendment) ─

        [Fact]
        public void PasswordSet_NeverConfigured_OnLoopback_IsGated_TheDeliberateB1Amendment()
        {
            var auth = PasswordAuth("recovery-pass");
            var state = Circuit(OffRbac(), auth, loopback: true);

            // THE AMENDMENT: with a recovery password set, the never-configured loopback surface no
            // longer break-glasses unconditionally. Contrast NoPassword_NeverConfigured... which is
            // OPEN on the same posture.
            Assert.False(state.IsAuthorizedWithBreakGlass("settings"),
                "a password-SET install must be GATED on the never-configured loopback surface — Interpretation 2.");
            Assert.Equal(AppUserState.BreakGlassPosture.LockedNeedsPassword, state.BreakGlassState);

            // Unlocking with the recovery password opens it.
            Assert.True(auth.Unlock("recovery-pass"));
            Assert.True(state.IsAuthorizedWithBreakGlass("settings"), "after unlock the recovery surface is reachable.");
            Assert.Equal(AppUserState.BreakGlassPosture.Unlocked, state.BreakGlassState);
        }

        // ── 4. Password set + lapsed store → gated, recover via unlock ────────

        [Fact]
        public void PasswordSet_LapsedStore_OnLoopback_IsGated_ThenRecoversViaUnlock()
        {
            var rbac = LapsedRbac();
            Assert.True(rbac.DescribeEnforcementPosture().IsProblem, "precondition: the store lapsed");

            var auth = PasswordAuth("recovery-pass");
            var state = Circuit(rbac, auth, loopback: true);

            Assert.False(state.IsAuthorized("settings"), "security-8: a lapsed loopback caller is viewer, not admin.");
            Assert.False(state.IsAuthorizedWithBreakGlass("settings"),
                "the recovery surface is gated behind the recovery password while it is set and locked.");
            Assert.Equal(AppUserState.BreakGlassPosture.LockedNeedsPassword, state.BreakGlassState);

            Assert.True(auth.Unlock("recovery-pass"));
            Assert.True(state.IsAuthorizedWithBreakGlass("settings"), "unlocking recovers access to the repair surface.");
        }

        // ── 5. Unlock opens the FORM, not an admin session — no self-elevation ─

        [Fact]
        public void UnlockingRecovery_DoesNotElevateTheCaller()
        {
            var rbac = LapsedRbac();
            var auth = PasswordAuth("recovery-pass");
            var state = Circuit(rbac, auth, loopback: true);

            Assert.True(auth.Unlock("recovery-pass"));

            Assert.True(state.IsAuthorizedWithBreakGlass("settings"), "the recovery FORM is reachable…");
            // …but the caller is NOT elevated: still viewer, the plain gate still denies, and they
            // cannot be recorded as a change approver.
            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.False(state.IsAuthorized("settings"));
            Assert.False(state.IsAuthorized("run_scripts"));
            Assert.Null(state.ApprovingPrincipal);
        }

        // ── The scope holds: a remote caller gets AccessDenied, not the overlay ─

        [Fact]
        public void RemoteCaller_IsNotEligibleForTheUnlockOverlay()
        {
            var auth = PasswordAuth("recovery-pass");
            var remote = Circuit(LapsedRbac(), auth, loopback: false);

            Assert.False(remote.IsAuthorizedWithBreakGlass("settings"));
            Assert.Equal(AppUserState.BreakGlassPosture.NotEligible, remote.BreakGlassState);
        }

        // ── 7. Filesystem escape: blanking AdminAuth:Hash/Salt reopens recovery ─

        [Fact]
        public void FilesystemEscape_BlankingTheAppsettingsKeys_ReopensBreakGlass()
        {
            var rbac = LapsedRbac();

            // A recovery password is set and the operator is locked out of the recovery surface.
            var withPassword = PasswordAuth("forgotten-pass");
            Assert.True(withPassword.HasPassword);
            var lockedOut = Circuit(rbac, withPassword, loopback: true);
            Assert.False(lockedOut.IsAuthorizedWithBreakGlass("settings"));

            // The ultimate escape: blank AdminAuth:Hash/Salt in appsettings → HasPassword false →
            // the cold-start carve-out reopens the loopback recovery surface.
            var blanked = new AdminAuthService(Config(hash: "", salt: ""), new InstallProvenanceService(NewDir()));
            Assert.False(blanked.HasPassword);
            var reopened = Circuit(rbac, blanked, loopback: true);
            Assert.True(reopened.IsAuthorizedWithBreakGlass("settings"),
                "blanking the recovery credential must reopen the recovery surface — nothing is unbrickable.");
            Assert.Equal(AppUserState.BreakGlassPosture.OpenNoPassword, reopened.BreakGlassState);
        }

        // ── Cold-start: an unresolvable AdminAuthService is treated as no-password ─

        [Fact]
        public void ColdStart_WhenAdminAuthUnresolvable_BreakGlassStaysOpen()
        {
            // Empty provider: GetService<AdminAuthService>() returns null. The carve-out treats that
            // as "no password" so an install that has not wired the service can still recover.
            var state = Circuit(OffRbac(), adminAuth: null, loopback: true);

            Assert.True(state.IsAuthorizedWithBreakGlass("settings"));
            Assert.Equal(AppUserState.BreakGlassPosture.OpenNoPassword, state.BreakGlassState);
        }

        // ── 6. Onboarding opt-out: the declined flag persists ─────────────────

        [Fact]
        public void OnboardingOptOut_PersistsTheDeclinedFlag()
        {
            var path = Path.Combine(NewDir(), "user-settings.json");

            var svc = new UserSettingsService(path);
            Assert.False(svc.GetRecoveryPasswordPromptDeclined(), "default is not-declined");

            svc.SetRecoveryPasswordPromptDeclined(true);
            Assert.True(svc.GetRecoveryPasswordPromptDeclined());

            // A fresh instance reads it back off disk — the decline is durable, so the prompt does
            // not return on every visit.
            var reloaded = new UserSettingsService(path);
            Assert.True(reloaded.GetRecoveryPasswordPromptDeclined(),
                "the declined flag must survive a restart so onboarding does not re-prompt.");
        }

        [Fact]
        public void OnboardingMarkup_WiresTheOptOutAndTheLoudWarning()
        {
            var onboarding = File.ReadAllText(
                Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Onboarding.razor"));

            Assert.Contains("ShouldPromptRecovery", onboarding);
            Assert.Contains("DeclineRecoveryPassword", onboarding);
            Assert.Contains("SetRecoveryPasswordPromptDeclined(true)", onboarding);   // decline PERSISTS
            Assert.Contains("onboarding-recovery-warning", onboarding);               // the loud warning element
            Assert.Contains("role=\"alert\"", onboarding);                            // announced, not just visual
        }

        [Fact]
        public void SettingsMarkup_ExposesTheRecoveryPasswordControl()
        {
            var settings = File.ReadAllText(
                Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Settings.razor"));

            Assert.Contains("set-recovery-password", settings);          // the Security-tab card
            Assert.Contains("SetRecoveryPassword", settings);            // the set control
            Assert.Contains("BreakGlassPosture.LockedNeedsPassword", settings);   // the unlock-overlay branch
            Assert.Contains("<AdminGuard OnUnlocked", settings);         // reuses the registered gate to unlock
        }
    }
}
