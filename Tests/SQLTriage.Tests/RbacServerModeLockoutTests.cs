/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The three server-mode RBAC defects Adrian hit live on 2026-08-01, expressed as tests.
    ///
    /// <para>Every test in this class is written so it COMPILES against the pre-fix tree
    /// (791d2cb) and FAILS there. That is deliberate: a regression test whose "before" state
    /// is a compile error proves nothing about behaviour. Real runner output for the failing
    /// run is in the lane's commit message.</para>
    ///
    /// <para><b>═══ DEMOTED 2026-08-17. Read this before adding a round nine. ═══</b></para>
    ///
    /// <para><b>The spelling war this file fought over TWO METHODS is over, and it was not won
    /// here.</b> Rounds 1-7 (2026-08-01 → 2026-08-16) each closed one way a shipped page could SPELL
    /// a call to <c>RbacService</c>'s fail-open authorization surface so that this file's scans could
    /// not see it. Each round was defeated by the next spelling; the round-7 re-gate predicted an
    /// eighth before anyone had written it. That is the
    /// <c>static-scans-cannot-be-a-security-boundary</c> lesson playing out on our own instrument.
    /// The lane that closed it removed those two call TARGETS instead of chasing their
    /// spellings:</para>
    /// <list type="bullet">
    ///   <item><description><c>RbacService.IsAuthorized(string role, string permission)</c> — the
    ///   fail-OPEN two-argument overload, the dangerous one — <b>no longer exists.</b> Not hidden,
    ///   not banned: deleted, with zero callers. Writing it is a compile error, full
    ///   stop.</description></item>
    ///   <item><description><c>RbacService.HasPermission(string role, string permission)</c> — the
    ///   raw matrix — is <b><c>private</c></b>. Nothing outside <c>RbacService.cs</c> can NAME it,
    ///   so there is no spelling left to hide, full stop. <c>internal</c> would have been useless:
    ///   Pages and Components compile into the same assembly.</description></item>
    /// </list>
    /// <para><b>PROVED, not assumed (2026-08-17).</b> All eight of rounds 4-7's evasion shapes — the
    /// <c>@@</c> escape, an <c>@{ }</c> markup block, a raw string literal, a mid-line block comment
    /// in markup, an interpolation hole, a <c>global::</c>-qualified verbatim namespace alias, a
    /// two-line <c>[Inject]</c> split, and a run-away <c>@*</c> span — were re-planted one at a time
    /// in a shipped page. Every one built with <b>0 errors at 930c1a7</b>; every one now fails to
    /// COMPILE, six with <c>CS0122</c> and two with <c>CS7036</c>. The baseline control is half the
    /// proof: a plant that never compiled would demonstrate nothing.</para>
    ///
    /// <para><b>RETIRED, therefore, and deleted rather than disabled:</b>
    /// <c>NoShippedUiCallsTheStaticHasPermissionDirectly</c>,
    /// <c>NoShippedUiCallsTheTwoArgIsAuthorizedDirectly</c>, the <c>KnownOffenders</c> allowlist and
    /// the two tests that policed it. Their subject matter is uncallable, so they could never go red
    /// again — and a test that cannot fail is theatre that costs a reader's attention.</para>
    ///
    /// <para><b>WHAT REMAINS MEANINGFUL, and it is not nothing.</b> The compiler closed those two
    /// methods. It did not close the surface around them, so these stay, each guarding something the
    /// modifiers do not:</para>
    /// <list type="number">
    ///   <item><description><see cref="TheOnlyShippedUiFilesHoldingAnRbacServiceInstanceAreThePinnedThree"/>
    ///   — <c>RbacService</c> still has a public INSTANCE surface beyond authorization: user
    ///   administration, <c>ResolvePrincipal</c>, enforcement state. A page holding the service is a
    ///   hazard for those reasons whether or not it can reach the matrix, so the set of files that
    ///   may hold one stays pinned at three.</description></item>
    ///   <item><description><see cref="NoShippedUiComputesAnAuthorizationDecisionItself"/> — the
    ///   receiver-agnostic ban on a page computing its own verdict. <b>Its scope NARROWED on
    ///   2026-08-17 and the narrowing is recorded rather than quietly applied.</b> Through that
    ///   morning this test was the sole guard against a compilable fail-open verdict: the surviving
    ///   gate took <c>bootstrapEligible</c> as a plain <c>bool</c>, a page passing a literal
    ///   <c>true</c> reproduced the deleted overload exactly, and MEASURED at 8959044 that plant in
    ///   <c>Pages/Settings.razor</c> built with 0 errors while this was the only test in the whole
    ///   Rbac filter to go red (1 failed, 793 passed). The capability-token lane then replaced the
    ///   parameter with <c>AppUserState.BootstrapEligibilityProof</c>, and the same plant is now
    ///   <c>CS1503</c>. What this scan still guards, present tense: a page asking the gate about a
    ///   ROLE it does not hold (the role argument is still a caller-supplied string), a page reaching
    ///   for a token in its own source, and any FUTURE fail-open overload. What it does NOT guard,
    ///   measured the same day: the same call written in a helper OUTSIDE <c>Pages/</c> and
    ///   <c>Components/</c> and invoked from a page by name — this scan reads two folders, and that
    ///   is a hole no amount of pattern work closes. That case is held by the runtime identity check
    ///   and by the assembly-wide censuses in <see cref="RbacChokepointTests"/>. This is a smaller job
    ///   than it had, and it is still a real one.</description></item>
    ///   <item><description><see cref="NoShippedUiImportsRbacServiceStaticallyOrUnderAnAlias"/> — a
    ///   cheap lint on import spellings, kept because it costs nothing and still names an odd
    ///   acquisition.</description></item>
    ///   <item><description>The comment strippers, the region model and the ~29 mutation tests
    ///   beneath them — <b>kept deliberately.</b> They are not decoration: items 1-3 all read source
    ///   text through them, and the mutation tests are what prove the strippers still see what they
    ///   claim to. Retiring the machinery while keeping the scans that depend on it would leave
    ///   those scans quietly blind, which is precisely the defect class this file
    ///   documents.</description></item>
    /// </list>
    /// <para><b>What this file is NOW: a lint that agrees with the compiler about the three closed
    /// surfaces, and stands in for it on the ones the language cannot express.</b> Its limits are
    /// unchanged and still honest — it reads source text, it sees only <c>Pages/</c> and
    /// <c>Components/</c>, it cannot see runtime, and it was out-spelt seven times. What changed is
    /// the scope of what rests on those limits. Nothing rests on them for <c>HasPermission</c> or for
    /// the deleted two-argument overload: those are compile errors now (CS0122 and CS7036, each
    /// measured). The eligibility TERM is a different case and this paragraph got it wrong for one
    /// commit. A literal <c>true</c> is <c>CS1503</c> (measured), but the token is still obtainable in
    /// compiling source — <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> mints it, with no
    /// reflection API and 0 build errors, and this file's scans could not see it because the lane's
    /// verifier put the declaration in <c>Data/Services</c> and had the page call a named helper. So
    /// the term rests on a RUNTIME check, <c>AppUserState.IsTheMintedProof</c>, not on these scans and
    /// not on a modifier. What still rests on the scans is the caller-supplied ROLE argument; what
    /// rests on the assembly-wide censuses in <see cref="RbacChokepointTests"/> is theft of the minted
    /// token.</para>
    ///
    /// <para>The boundary is still that <see cref="AppUserState"/> is the only type that knows
    /// <c>IsBootstrapEligible</c>. Since 2026-08-17 the language enforces the approach to two thirds
    /// of it — the matrix is private and the fail-open overload is gone — and the hatch term is a
    /// token that only <see cref="AppUserState"/> mints and that only <see cref="AppUserState"/> can
    /// vouch for. <see cref="RbacChokepointTests"/> holds the modifier assertions, the runtime pin,
    /// two assembly-wide censuses and two narrow name lints.</para>
    ///
    /// <para><b>If you are here to add round nine:</b> a new SPELLING of a call to the closed methods
    /// is not a defect any more — it will not compile, so do not add a regex for it. Three things
    /// still are: a page computing its own verdict at all (item 2 above, whether it lies about the
    /// role or obtains a token), a new fail-open METHOD or overload, and any route to the minted
    /// token. The answer to the first two is a modifier or a capability argument. The answer to the
    /// third is NOT a folder-scoped text scan — that is exactly what was walked around on 2026-08-17
    /// — it is the reflection census over the whole assembly in
    /// <see cref="RbacChokepointTests"/>.</para>
    /// </summary>
    public class RbacServerModeLockoutTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacServerModeLockoutTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-lockout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private RbacService NewService(RbacConfig? config = null, params RbacUser[] users)
        {
            if (config != null)
                File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (users.Length > 0)
                File.WriteAllText(_usersPath, JsonSerializer.Serialize(users.ToList()));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        private static RbacUser Admin(string email = "admin@example.com") => new()
        {
            Email = email,
            Provider = "google",
            Role = AppRoles.Admin,
            Enabled = true
        };

        // ── Defect 2: the one-click lockout ──────────────────────────────
        //
        // Enable RBAC + add one Admin and IsRbacEnforced() flips true. Every gate then
        // defers to the permission matrix — including Settings, the only page that can
        // undo the change. With no sign-in method configured there is no way to become
        // that Admin, so the install is unrecoverable short of deleting rbac-users.json.
        //
        // FAILS AT 791d2cb: IsRbacEnforced() is `Enabled && any-enabled-admin`, so this
        // returns true and the assert below trips.

        [Fact]
        public void IsRbacEnforced_EnabledWithAdminButNoSignInMethod_StaysDormant()
        {
            var rbac = NewService(new RbacConfig { Enabled = true }, Admin());

            Assert.False(
                rbac.IsRbacEnforced(),
                "RBAC must not enforce while no sign-in method is configured — that is a total lockout.");
        }

        [Fact]
        public void IsRbacEnforced_EnabledWithAWindowsAdminAndWindowsAuth_Enforces()
        {
            // AMENDED 2026-08-01 (round 2). This used to pass an EMAIL-identity admin
            // (Admin() defaults to provider "google") and assert that Windows auth alone made
            // enforcement safe. It does not: Negotiate hands back MSI\afsul, no email-class record
            // matches it, and the box seals. The guard now pairs the admin to the provider, so the
            // admin here is a Windows one — and the case this test used to assert is now covered,
            // with the opposite expectation, by
            // RbacRound2RegressionTests.IsRbacEnforced_WindowsAuthOnButTheOnlyAdminIsAnEmailIdentity_StaysDormant.
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var admin = Admin(Environment.MachineName + @"\admin.adrian");
            admin.Provider = AuthProviders.Windows;
            var rbac = NewService(config, admin);

            Assert.True(rbac.IsRbacEnforced());
        }

        [Fact]
        public void IsRbacEnforced_GoogleFlaggedOnButNoClientId_IsNotAUsableProvider()
        {
            // ConfigureAuthentication requires Enabled AND a ClientId before it registers the
            // handler, so a bare Enabled flag configures nothing and must not count as a way in.
            var config = new RbacConfig { Enabled = true };
            config.Google.Enabled = true;
            config.Google.ClientId = "";
            var rbac = NewService(config, Admin());

            Assert.False(rbac.IsRbacEnforced());
        }

        [Fact]
        public void IsRbacEnforced_LocalPasswordEnabledButAdminHasNoPasswordSet_IsNotAWayIn()
        {
            // The exact shape the Onboarding SetPassword bug produced: an admin in the store
            // with PasswordHash == null. Local-password auth is "on" and still nobody can sign in.
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;
            var admin = Admin();
            admin.Provider = "local";
            admin.PasswordHash = null;
            var rbac = NewService(config, admin);

            Assert.False(rbac.IsRbacEnforced());
        }

        [Fact]
        public void IsRbacEnforced_LocalPasswordEnabledWithAHashedAdmin_Enforces()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;
            var admin = Admin();
            admin.Provider = "local";
            admin.PasswordHash = RbacService.HashPassword("correct horse battery staple");
            var rbac = NewService(config, admin);

            Assert.True(rbac.IsRbacEnforced());
        }

        // ── L2: the save must be refused, not silently ignored ───────────

        [Fact]
        public void DescribeEnforcementBlockers_NoProvider_NamesTheMissingSignInMethod()
        {
            var rbac = NewService(new RbacConfig(), Admin());

            var blockers = rbac.DescribeEnforcementBlockers(new RbacConfig { Enabled = true });

            Assert.NotEmpty(blockers);
            Assert.Contains(blockers, b => b.Contains("sign-in", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void DescribeEnforcementBlockers_NoAdmin_NamesTheMissingAdmin()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var rbac = NewService(config); // no users at all

            var blockers = rbac.DescribeEnforcementBlockers(config);

            Assert.NotEmpty(blockers);
            Assert.Contains(blockers, b => b.Contains("admin", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void DescribeEnforcementBlockers_WindowsAuthOnWithAWindowsAdmin_IsEmpty()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var admin = Admin(Environment.MachineName + @"\admin.adrian");
            admin.Provider = AuthProviders.Windows;
            var rbac = NewService(config, admin);

            Assert.Empty(rbac.DescribeEnforcementBlockers(config));
        }

        [Fact]
        public void DescribeEnforcementBlockers_RemovingTheLastUsableAdmin_IsRefused()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var admin = Admin(Environment.MachineName + @"\admin.adrian");
            admin.Provider = AuthProviders.Windows;
            var rbac = NewService(config, admin);

            // Prospective state: that admin disabled.
            var disabled = new RbacUser
            {
                Id = admin.Id,
                Email = admin.Email,
                Provider = admin.Provider,
                Role = AppRoles.Admin,
                Enabled = false
            };

            var blockers = rbac.DescribeEnforcementBlockers(config, new[] { disabled });
            Assert.NotEmpty(blockers);
        }

        // ── Defect 1: the bootstrap lockout, at the gate ─────────────────
        //
        // The instance IsAuthorized has the escape hatch; the static HasPermission does not.
        // 22 shipped call sites used the static one, so an unconfigured server-mode install
        // denied every surface the hatch was written to grant.
        //
        // FAILS AT 791d2cb: the scan finds 22 offenders.
        //
        // ── RETIRED 2026-08-17: the four tests that used to stand here ───────────────────────────
        //
        // Deleted, not disabled: NoShippedUiCallsTheStaticHasPermissionDirectly,
        // NoShippedUiCallsTheTwoArgIsAuthorizedDirectly, and the two that policed the KnownOffenders
        // allowlist belonging to the first (TheAllowlistedOffenderStillExists_OrTheAllowlistIsStale,
        // TheAllowlistIsEmpty_AnExemptionMustBeArguedNotInherited), together with the now-unused
        // KnownOffenders array itself.
        //
        // WHY, in one sentence: both methods they banned are now unreachable from a shipped page by
        // the LANGUAGE, so a text ban on naming them can no longer fail, and a test that cannot fail
        // is theatre. RbacService.HasPermission is `private` and the fail-open two-argument
        // RbacService.IsAuthorized is deleted outright. A page that writes either call gets a
        // COMPILE error (CS0122 / CS7036), not a red test — measured, not assumed: all eight of
        // rounds 4-7's evasion shapes were planted one at a time in a shipped page on 2026-08-17,
        // every one built 0 errors at 930c1a7, and every one now fails to build. The control is the
        // load-bearing half of that sentence; a plant that never compiled would prove nothing.
        //
        // The allowlist lesson is NOT retired, only its enforcement — an exemption is permanent by
        // default and may never rest on a promise that another lane will land something. It cost a
        // fortnight of silence on a real defect (Pages/AuditLogViewer.razor, 2026-08-01 → 08-15).
        // It is restated in RbacChokepointTests, which carries the one lint this lane left standing.
        //
        // What is NOT retired, and why, is set out in the class header above.

        /// <summary>
        /// Both bans are TEXTUAL — they match the spelling <c>RbacService.HasPermission(</c> and
        /// <c>RbacService.IsAuthorized(</c>. <c>@using static SQLTriage.Data.Services.RbacService</c>
        /// plus a bare <c>HasPermission(</c>, or an alias import, spells the same call in a way
        /// neither regex sees. Nothing in Pages/ or Components/ does that today (MEASURED 2026-08-15:
        /// the only static imports are GovernanceService — three files — and ComplianceScoreService,
        /// and the only alias import in either root is <c>using Color = ApexCharts.Color;</c> at
        /// Pages/VulnerabilityAssessment.razor.cs:20), and this holds it that way.
        ///
        /// <para><b>Corrected 2026-08-15.</b> This doc used to end "so the sentence in
        /// <c>RbacService.cs</c> is a claim about the spelling space and not about one spelling of
        /// it." That was FALSE, and measurably so. This test covers <c>using</c>-level aliasing only.
        /// The aliasing vector that mattered is not an import at all — it is the DI variable name,
        /// which <c>@inject RbacService Rbac</c> supplies for free. <c>Pages/ServerDocs.razor:11</c>
        /// carried exactly that line, dangling and unused, from bec0d4e. MEASURED at 3fe19a3:
        /// planting <c>@if (!Rbac.IsAuthorized(UserState.Role, "settings"))</c> at that page compiled
        /// with 0 errors and ran all 88 census tests green, exit 0. Neither this test nor
        /// <c>NoShippedUiCallsTheTwoArgIsAuthorizedDirectly</c> (retired 2026-08-17 — the overload it
        /// banned no longer exists, so that plant no longer compiles) named it. This test is a lint
        /// on import spellings; the claim about the spelling space belongs to
        /// <see cref="NoShippedUiComputesAnAuthorizationDecisionItself"/>, which reads the call rather
        /// than the receiver, and to
        /// <see cref="TheOnlyShippedUiFilesHoldingAnRbacServiceInstanceAreThePinnedThree"/>, which
        /// holds the surface that can hold one at three files.</para>
        ///
        /// <para><b>Widened 2026-08-16 (round 4).</b> Both regexes spelled the namespace qualifier
        /// <c>[\w.]*</c>, so <c>@using static global::SQLTriage.Data.Services.RbacService</c> and
        /// <c>@using Rb = global::SQLTriage.Data.Services.RbacService;</c> matched neither — the same
        /// blind spot that let a <c>global::</c>-qualified <c>@inject</c> past the holder pin. Both
        /// now use <see cref="TypeQualifier"/>, which reads <c>::</c>. Pinned by
        /// <see cref="MutationEighteen_AGlobalQualifiedImportIsCaughtInBothSpellings"/>.</para>
        /// </summary>
        [Fact]
        public void NoShippedUiImportsRbacServiceStaticallyOrUnderAnAlias()
        {
            var root = RawPassedScan.RepoRoot();

            var offenders = new List<string>();
            foreach (var file in ShippedUiFiles())
            {
                var lines = File.ReadAllText(file.FullName).Split('\n');
                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                for (int i = 0; i < lines.Length; i++)
                    if (RbacStaticImport.IsMatch(lines[i]) || RbacAliasImport.IsMatch(lines[i]))
                        offenders.Add(relative + ":" + (i + 1) + "  " + lines[i].Trim());
            }

            Assert.True(
                offenders.Count == 0,
                "a static or aliased import of RbacService lets a page spell the banned gate calls in "
                + "a form the census cannot match, which would make it blind rather than merely "
                + "silent. Offenders:\n  " + string.Join("\n  ", offenders));
        }

        internal static readonly Regex RbacStaticImport =
            new(@"@?using\s+static\s+" + TypeQualifier + RbacServiceType, RegexOptions.Compiled);

        /// <summary>
        /// An alias import of the service. The trailing semicolon is OPTIONAL because a Razor
        /// <c>@using Rb = SQLTriage.Data.Services.RbacService</c> directive does not carry one — the
        /// version of this pattern that shipped required it, so it could only ever have matched the
        /// C# spelling in a code-behind, and the <c>.razor</c> spelling of the same import was
        /// invisible (found 2026-08-16 while widening the qualifier; latent — no shipped file aliases
        /// the service in either spelling). The <c>\b</c> after the type name is what the semicolon
        /// used to do: it keeps <c>RbacServiceFactory</c> from matching.
        ///
        /// <para><b>2026-08-16 (round 5).</b> The alias NAME and every qualifier segment may be
        /// verbatim, so both take an optional <c>@</c>. Before the fix
        /// <c>using RB = global::SQLTriage.Data.Services.@RbacService;</c> matched neither this
        /// pattern nor <see cref="RbacStaticImport"/> — MEASURED in the tree, planted at
        /// <c>Pages/Portal/ExportPack.razor.cs:7</c> with a two-line <c>[Inject]</c> under it: 0
        /// build errors, census 59/59, exit 0. The holder pin could not see that acquisition either
        /// (the declared type there is the alias <c>RB</c>), so the alias spelling was invisible to
        /// BOTH of the two layers the holder pin's doc says cover each other. With this fix the
        /// import ban names the line.</para>
        /// </summary>
        internal static readonly Regex RbacAliasImport =
            new(@"@?using\s+@?\w+\s*=\s*" + TypeQualifier + RbacServiceType + @"\s*;?", RegexOptions.Compiled);

        // ── The ban with teeth, added 2026-08-15 (round 3) ───────────────────────────────────────
        //
        // Every ban above names a RECEIVER: `RbacService.HasPermission(`, `RbacService.IsAuthorized(`,
        // `@using static … RbacService`. A receiver is a name, and a name is chosen by whoever writes
        // the page. `@inject RbacService Rbac` renames the receiver to `Rbac` in one line, and every
        // scan above goes quiet — MEASURED at 3fe19a3, against the real tree: the probe compiled with
        // 0 errors and the whole census returned exit 0 on 88 passed.
        //
        // The two tests below stop asking who the receiver is.
        //
        //   1. NoShippedUiComputesAnAuthorizationDecisionItself reads the CALL: a shipped UI file may
        //      not call HasPermission at all, and may not call IsAuthorized with two or more
        //      arguments, whatever it is called on or through. The sanctioned gate —
        //      UserState.IsAuthorized("permission") — takes ONE argument, because AppUserState
        //      supplies the role and the bootstrap eligibility itself. Passing a role explicitly IS
        //      the defect: it means the page computed the decision from the matrix rather than
        //      deferring to the authority that knows whether this caller may use the hatch.
        //
        //   2. TheOnlyShippedUiFilesHoldingAnRbacServiceInstanceAreThePinnedThree holds the surface
        //      that can hold the service at all, so growth of that surface is a deliberate act.

        // ── Round 4, 2026-08-16: three holes in the INSTRUMENT, not in the tree ──────────────────
        //
        // Rounds 1-3 each closed a way a page could spell a bypass. Round 4 closes three ways the
        // census could fail to look, all of them LATENT at e5ef5cc — a raw grep proved the shipped
        // tree hid nothing in any of them — and all three PROVED by exercise rather than by reading.
        // Planted together in Pages/ServerDocs.razor and Pages/Servers.razor they compiled with 0
        // errors and left the e5ef5cc census green: 41/41 in this class, 740/740 across the whole
        // Rbac filter, exit 0.
        //
        //   1. `global::` — four regexes spelled the type qualifier `[\w.]*`, and no class of word
        //      characters and dots matches a colon. See TypeQualifier. (A fifth, RbacAliasImport,
        //      also required a trailing `;` that a Razor `@using` alias never has.)
        //
        //   2. A line-initial `/* … */` span in Razor MARKUP is not a comment — Razor emits the
        //      slashes as text and COMPILES every `@` transition between them, PROVED by CS1061 at
        //      the line inside such a span. The stripper blanked it as prose, so a live gate there
        //      was invisible to every scan in this file. See BlankBlockComments / CSharpRegions.
        //
        //   3. The two scans round 3 added — the argument-count ban and the acquisition pin — had no
        //      tail-blindness guard of their own; the one that existed covered only the two older
        //      receiver-named regexes. See EveryScanSeesEveryShippedFileAllTheWayToItsLastLine.
        //
        // And the limit round 3 documented rather than closed — method-group indirection,
        // `_gate = Rbac.IsAuthorized;` — is now itself an offence. It was the other half of the
        // combined bypass measured green above.

        // ── Round 5, 2026-08-16: round 4 fixed three holes and left three siblings of them open ───
        //
        // Every one of these was found by exercising round 4's own claims rather than by reading its
        // code, and every one is a SILENCE — the direction this file says it never fails in. All
        // three were LATENT at 28f7669 (the tree hid nothing in any of them) and all three were
        // PROVED in the tree: planted together they compiled with 0 errors and left the census at
        // 59/59, exit 0, and the whole `~Rbac` filter at 758/758, exit 0.
        //
        //   1. Round 4 ruled "a `/*` in Razor MARKUP is not a comment" and applied it to
        //      BlankBlockComments only. The sibling stripper on the same call path, BlankLineComments,
        //      still blanked any line whose trimmed start was `//`, `///` or `*`, wherever it stood.
        //      Those three prefixes are text in markup and Razor compiles the whole line — PROVED by
        //      CS1061 on all three, planted in the markup of Pages/ServerDocs.razor. A fail-open gate
        //      written on such a line, reaching the service through a two-line [Inject], was invisible
        //      to every scan here. The ruling now reaches both strippers. See BlankLineComments.
        //
        //   2. The acquisition pin was applied PER LINE and claimed to cover "the four DI acquisition
        //      forms … any spelling of the type". The ordinary two-line `[Inject]` spelling, a
        //      verbatim `@RbacService`, and a generic split across lines all defeated it; the verbatim
        //      spelling defeated the two import bans as well, so the layer the pin's own doc names as
        //      its backstop was blind to the same shape. See RbacServiceAcquisition.
        //
        //   3. "This closes the run-away class by construction … not as silence" was false at the
        //      moment it was written: EligibleCSharpCommentSpans hunted `/*` as TEXT, so one inside a
        //      string literal in a @code block opened a span whose `*/` was found in markup 200 lines
        //      below, blanking a live static gate. Inherited from e5ef5cc — but round 4 re-asserted
        //      the sentence beside its new region logic, and the sentence is what a reader trusts.
        //      See EligibleCSharpCommentSpans.
        //
        // Also corrected: the sanctioned-gate figure (89 → 88 measured; the 89th was a `<see cref>`),
        // and the method-group ban's false-positive list, which omitted its largest surface — plain
        // user-visible markup prose naming either method.

        // ── Round 6, 2026-08-16: round 5 fixed ONE opener of two ─────────────────────────────────
        //
        // Round 5's item 3 above rewrote the `/*` opener to read its C# region as C#. `@*` — the
        // other opener on the same call path, in the same method — kept the raw text search round 5
        // had just ruled unsafe: `source.IndexOf("@*", i)` anywhere in the file, closed by an
        // unbounded `source.IndexOf("*@")`. So the silence round 5 closed was still spellable one
        // character differently, and closing it took ONE line in a shipped page.
        //
        //   `private string _gpOpen = "@*";` planted at Pages/ScheduledTasks.razor:25, inside that
        //   file's first @code block, borrowing the file's own real `*@` at line 256. Lines 25-256
        //   blanked. A mid-file `@inject … RbacService RbacHole` at 230 and the two-argument
        //   `RbacHole.IsAuthorized(UserState.Role, "settings")` gate it feeds at 231, both inside
        //   that span: build 0 errors, census 84/84 green, exit 0. Deleting ONLY the string-literal
        //   line turned the census red naming both — the literal is the cause, not a coincidence.
        //   Proved again with a `//` comment in Pages/ComplianceTree.razor:202 hiding a static
        //   two-arg HasPermission at :391. See BlankBlockComments and EligibleRazorCommentSpans.
        //
        // The structural half matters more than the rule. Round 5 fixed one opener because there
        // were two literal walks to remember; both openers are now judged against one shared map
        // (CSharpLiteralMask), so the next rule added there reaches both by construction.
        //
        // And then the same class again, one literal-kind away, found by exercising the round-6 rule
        // rather than reading it: SkipStringLiteral stopped at the first unescaped `"`, so an
        // INTERPOLATION HOLE holding a nested literal — `$"{string.Join(", ", xs)}"`, 52 of them in
        // the shipped tree — came back as two literals with a GAP, and a `@*` parked in that gap was
        // an eligible opener again. PROVED at Pages/ComplianceTree.razor:202, where the borrowed
        // `*@` at 429 is inside the SAME @code block so containment could not save it: build 0
        // errors, the gate at :391 unnamed. Closed in SkipStringLiteral, which now reads its own
        // prefix and walks holes as C#. Pinned by MutationTwentySix.
        //
        // COST, measured rather than argued, off a throwaway harness holding a verbatim copy of the
        // WHOLE d85a401 stack: over all 161 shipped UI files the two produce BYTE-IDENTICAL blanked
        // output, 0 files differing. One C# region moved — Pages/DbaTools.razor's @code block ends
        // 128 characters later, because the round-5 brace walk lost its place on a nested literal
        // inside an interpolation hole at line 478 and closed the region eight lines early, reading
        // that file's whole Dispose() method as markup. The conditions under
        // which the `@*` half of that equality can stop holding are re-measured every run by
        // NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot.
        //
        // Also corrected: three doc sentences that described the `@*` opener as if it had the
        // round-5 discipline — the rule bullet on BlankBlockComments, round 5's "the only multi-line
        // spans that exist" summary beside it, and "a `@* … *@` anywhere" on
        // AuthorityBypassOffences. Sixteenth occurrence of the prose-not-conditioned-on-a-
        // measurement class this file exists to close.

        // ── Round 7, 2026-08-16: three more ways an `@*` is not an opener ────────────────────────
        //
        // The last hardening round on this instrument before it merges. All three defects are
        // SILENCES, all three were found by EXERCISING round 6's rule rather than reading it, and
        // all three were proved in the shipped tree with the instrument as the only variable — plant
        // in, build 0 errors, census GREEN at 20a0384, census RED under this file, plant out.
        //
        //   1. THE `@@` ESCAPE. Razor resolves `@@` into a literal `@` before it looks for a
        //      transition, so `@@*` opens nothing and `@@@*` does. Rounds 5 and 6 taught both
        //      openers to read literals and neither taught this one to read Razor. PROVED BY THE
        //      COMPILER, one `@` apart: `@@*` above `@DateTime.Now.NoSuchMemberXyz` at
        //      Pages/ScheduledTasks.razor:228 failed the build CS1061 at (229,15) — live code —
        //      while the same two lines spelt `@* …` built 0 errors. PROVED IN THE TREE: `@@*` at
        //      :229 with `@inject … RbacHole` at :230 and its fail-open two-arg gate at :231,
        //      borrowing the file's real `*@` at :255 — 88/88 green exit 0 at 20a0384; three tests
        //      name it here. See AtStarIsEscaped, MutationTwentySeven.
        //
        //      That green needed ONE extra planted line — `private string _bal = "*@";` — because
        //      the backstop, NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot, counted the
        //      escaped `@@*` as a delimiter. A backstop reading the tree through a DIFFERENT rule
        //      from the one it backstops is not a backstop. Both call AtStarIsEscaped now.
        //
        //   2. MASK SCOPE IS NOT REGION SCOPE. Round 6 asked CSharpRegions where the literals are.
        //      That list is the BLANKING scope, and rounds 4-5 had ruled — correctly, twice — that
        //      it must NOT include `@{ … }` markup blocks, because blanking too much is silent. But
        //      masking too LITTLE is silent too: an unknown literal leaves a `@*` eligible. One
        //      question, two answers, opposite directions. PROVED IN THE TREE: `@{ var _probe =
        //      "@*"; }` planted in ScheduledTasks.razor markup at :229, gate at :231 — 88/88 green
        //      exit 0 at 20a0384; named at :231, :230 and :229 here. The mask now reads
        //      CSharpLiteralRegions (@code + @{ } + @if/else/@for/@foreach/@while/… bodies) while
        //      every blanking rule keeps reading CSharpRegions. See MutationTwentyEight, whose
        //      fourth case fails if someone widens the blanking scope instead.
        //
        //   3. RAW STRING LITERALS. `"""…"""` came back as an empty literal `""` followed by an
        //      ordinary one that stopped at its line, leaving the fenced content an unmasked GAP —
        //      the round-6 interpolation-hole shape, one literal kind on. Round 6's own doc NAMED
        //      the raw string as a known misread and filed it under the loud direction; it is the
        //      silent one. PROVED IN THE TREE: a three-line raw string at
        //      Pages/ComplianceTree.razor:203-205 borrowing that @code block's own `*@` at :428 hid
        //      a static two-arg gate at :395 — 88/88 green exit 0 at 20a0384; named twice here. See
        //      SkipStringLiteral, MutationTwentyNine.
        //
        // COST, measured not argued, off a throwaway harness holding a verbatim copy of the WHOLE
        // 20a0384 stack beside this one: over all 161 shipped UI files (3,282,291 chars) the blanked
        // output is BYTE-IDENTICAL — 0 files differing, 0 characters differing — and 0 C# regions
        // moved (162 both sides), so round 6's DbaTools region shift has no round-7 counterpart.
        // Every one of the three fixes REMOVES openers, which is why. The tree numbers behind that:
        // 30 `@@` escapes over 12 files and 0 of them `@@*`; 0 raw string literals; 644 unescaped
        // `@*` and 644 `*@`, 0 files unbalanced; 44 `@{ … }` blocks, and 2,304 inline C# regions the
        // widened literal scope now knows about.
        //
        // CURSORY PROBE of the three constructs the round-6 re-gate listed as untested, one live
        // plant each in ScheduledTasks.razor markup, build 0 errors: a two-argument fail-open gate
        // written in an explicit expression `@(…)` (:229), in an `@if` header (:230), on an `@:`
        // line transition (:232) and inside a `<text>` block (:233) is NAMED at all four lines by
        // the argument-count ban, with the acquisition pin naming :228. Nothing to fix; the limit
        // would have been named here if there were one.
        //
        // Also corrected: four doc sentences whose direction claims were false — the `@*` rule
        // bullet on BlankBlockComments, "the rule now encodes what Razor does" on
        // EligibleRazorCommentSpans, "both failure directions … are under-blanking" on
        // EligibleCSharpCommentSpans, and "every failure direction of this map is UNDER-blanking"
        // on CSharpLiteralMask, which named the raw string as a counter-example in its own next
        // sentence. SEVENTEENTH occurrence of the prose-not-conditioned-on-a-measurement class this
        // file exists to close.
        //
        // WHAT THIS CENSUS IS, said plainly because seven rounds have now been spent on it: it is a
        // LINT over source text, not a boundary. Each round closed a spelling that hid a call from
        // it, and each round was defeated by the next spelling, because a text instrument can always
        // be out-spelt. Its named limits stand: it reads two method NAMES, so another service
        // reaching a verdict is invisible to it; it reads calls in shipped Pages/ and Components/,
        // so a delegate assigned in one file and invoked in another is invisible to it; it cannot
        // see runtime. The boundary is that AppUserState is the only type that knows
        // IsBootstrapEligible — and the structural close for the arms race is a separate lane that
        // makes the fail-open overload uncompilable from a page, at which point this file becomes a
        // lint that agrees with the compiler instead of one standing in for it.

        /// <summary>
        /// A shipped UI file must not evaluate an authorization decision itself, no matter what the
        /// receiver is called. Banned: any <c>HasPermission(</c> call, any <c>IsAuthorized(</c>
        /// call taking two or more arguments, and (round 4) either name used as a METHOD GROUP rather
        /// than called. Allowed: <c>UserState.IsAuthorized("permission")</c> and
        /// <c>UserState.IsAuthorizedWithBreakGlass("permission")</c>, which are single-argument and a
        /// different name respectively.
        ///
        /// <para><b>Why by argument count rather than by receiver.</b> The two-argument overload is an
        /// INSTANCE method, so it is always reached through a variable, and the variable's name is
        /// free. The regex bans above are therefore bans on one chosen spelling. Argument count is not
        /// chosen — it is fixed by the overload being called, so it survives any rename, any alias,
        /// any injection, and any chain of properties leading to the service.</para>
        ///
        /// <para><b>Mutation-proved</b> by MutationEleven (the instance-aliased two-arg call, the exact
        /// shape that defeated round 2), MutationTwelve (the sanctioned single-arg gate is NOT an
        /// offence, so this ban does not simply cry at every page), MutationThirteen (a bare
        /// <c>HasPermission(</c> with no receiver at all, the static-import spelling),
        /// MutationTwentyTwo (a gate below a run-away comment opener is still named),
        /// MutationSixteen (a gate hidden in a line-initial <c>/* … */</c> span in Razor markup, which
        /// Razor compiles), MutationSeventeen (the method-group indirection), MutationNineteen (a
        /// name inside a C# comment is still prose) and MutationTwenty (a gate on a line-initial
        /// <c>//</c>, <c>///</c> or <c>*</c> in Razor markup, which Razor also compiles).</para>
        ///
        /// <para><b>What it does not measure.</b> It reads source text, so it is a lint, not a
        /// boundary — the boundary is that <c>AppUserState</c> is the only type that knows
        /// <c>IsBootstrapEligible</c>. It reads calls and method groups of two NAMES; a page that
        /// reached an authorization verdict through some OTHER method name on some other service
        /// would not be named here, and neither would a delegate field assigned in one file and
        /// invoked in another. Seven rounds have each closed one spelling that hid a call from it
        /// and each was defeated by the next; the structural close is a separate lane making the
        /// fail-open overload uncompilable from a page, not another round here.</para>
        ///
        /// <para><b>Razor constructs it does reach, MEASURED 2026-08-16 (round 7)</b> rather than
        /// assumed, one live plant each in <c>Pages/ScheduledTasks.razor</c> markup, build 0 errors:
        /// a two-argument fail-open gate written in an explicit expression <c>@(…)</c>, in an
        /// <c>@if</c> header, on an <c>@:</c> line transition and inside a <c>&lt;text&gt;</c> block
        /// is named at all four lines, with the acquisition pin naming the injected receiver.</para>
        ///
        /// <para><b>Where it is deliberately LOUD, corrected 2026-08-16 (round 5).</b> This
        /// paragraph used to say that a banned call quoted in a comment "would be a false positive,
        /// loudly, never a silence", and that was true only of a TRAILING <c>// …</c> on a code
        /// line. A line-INITIAL <c>//</c>, <c>///</c> or <c>*</c> was blanked wherever it stood,
        /// including in Razor markup where those characters are text and the line is compiled — an
        /// exact silence, and a full live bypass ran through it. That is closed
        /// (<see cref="BlankLineComments"/>), and these are the false positives it leaves, all
        /// loud and none of them present in the tree today:</para>
        /// <list type="bullet">
        ///   <item><description>MARKUP PROSE naming either method — <c>&lt;p&gt;the page calls
        ///   IsAuthorized with one argument&lt;/p&gt;</c> — is a method-group offence. This is the
        ///   largest surface, and the tree carries documentation pages that are one English sentence
        ///   away from it. The fix when it fires is to reword the sentence or wrap it in
        ///   <c>@* … *@</c>, never to add an allowlist.</description></item>
        ///   <item><description>a <c>//</c> comment inside a Razor markup transition block —
        ///   <c>@for</c>, <c>@if</c>/<c>else</c>, <c>@{ … }</c> — which is real C# the region model
        ///   deliberately does not recognise. MEASURED 2026-08-16: 101 such lines over 9 shipped
        ///   files, 0 of them naming either method. See <see cref="BlankLineComments"/>.</description></item>
        ///   <item><description>a CSS comment inside a <c>&lt;style&gt;</c> block quoting either
        ///   name, on one line or several — <c>&lt;style&gt;</c> is markup.</description></item>
        ///   <item><description>an HTML comment <c>&lt;!-- … --&gt;</c> quoting either name; the
        ///   stripper knows Razor and C# comments, not HTML ones.</description></item>
        ///   <item><description><c>nameof(UserState.IsAuthorized)</c>, which is the name without a
        ///   call and therefore reads as a method group.</description></item>
        ///   <item><description>a trailing <c>// …</c> on a code line, as before.</description></item>
        /// </list>
        /// </summary>
        [Fact]
        public void NoShippedUiComputesAnAuthorizationDecisionItself()
        {
            var root = RawPassedScan.RepoRoot();
            var offenders = new List<string>();

            foreach (var file in ShippedUiFiles())
            {
                var raw = File.ReadAllText(file.FullName);
                var rawLines = raw.Split('\n');
                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                foreach (var (line, method, argCount) in AuthorityBypassOffences(raw, KindOf(file)))
                    offenders.Add(relative + ":" + line + "  [" + method + ", " + DescribeShape(argCount)
                                + "]  " + rawLines[line - 1].Trim());
            }

            Assert.True(
                offenders.Count == 0,
                "shipped UI must not compute an authorization decision itself. Call "
                + "UserState.IsAuthorized(\"permission\") — one argument — or wrap the markup in "
                + "<ShellGate> or <RbacGuard>. A call that passes a ROLE has taken the decision away "
                + "from AppUserState, which is the only type that knows whether this caller may use "
                + "the unconfigured-install bootstrap hatch; on a network-reachable listener that "
                + "difference is the difference between admin-for-the-operator and admin-for-anyone-"
                + "who-can-route-to-the-port. HasPermission is banned outright: it is the raw matrix. "
                + "A method group — the name without a call, stored for later — is banned too: it "
                + "carries no argument count, so nothing downstream can tell the sanctioned gate from "
                + "the bypass. Offenders:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The three shipped UI files that may hold an <c>RbacService</c> instance. All three
        /// ADMINISTER RBAC — they read and write the config and the user store — and none of them
        /// gates on it.
        ///
        /// <para><b>This is not an allowlist.</b> Nothing on this list is exempt from anything:
        /// <see cref="NoShippedUiComputesAnAuthorizationDecisionItself"/> scans these three files with
        /// no carve-out, which is the whole point of banning the call rather than the receiver. The
        /// list exists so that a FOURTH file acquiring the service is a deliberate, reviewed act
        /// rather than a silent one — because holding the instance is what makes the receiver-renamed
        /// call spellable in the first place, and two of the five holders at 3fe19a3 held it for
        /// nothing at all: <c>Pages/ServerDocs.razor:11</c> (<c>@inject RbacService Rbac</c>, from
        /// bec0d4e) and <c>Pages/ReportBundles.razor:12</c> (<c>@inject
        /// SQLTriage.Data.Services.RbacService RbacService</c>) each contained exactly one occurrence
        /// of the token — the inject line — and both were removed 2026-08-15.</para>
        ///
        /// <para><b>What it measures.</b> The DI acquisition forms available to a Blazor component —
        /// <c>@inject</c>, <c>[Inject]</c>, <c>GetRequiredService&lt;RbacService&gt;</c>,
        /// <c>GetService&lt;RbacService&gt;</c> and <c>GetService(typeof(RbacService))</c>, aliased,
        /// verbatim or fully qualified, split across lines or not, in <c>.razor</c> and <c>.cs</c>
        /// under Pages/ and Components/. <see cref="RbacServiceAcquisition"/> is the pattern and
        /// carries the list of spellings it still cannot read. MEASURED 2026-08-15: <c>[Inject]</c>
        /// is used in shipped UI (14 sites) but never for RbacService, and neither
        /// <c>GetRequiredService&lt;RbacService&gt;</c> nor <c>GetService&lt;RbacService&gt;</c>
        /// appears at all. It does NOT measure indirect reach — a page holding some other service that
        /// exposes an <c>RbacService</c> property would not be named here. That path is covered by the
        /// call ban above, not by this pin.</para>
        ///
        /// <para><b>Corrected 2026-08-16 (round 5): "the four DI acquisition forms" was applied one
        /// line at a time.</b> The scan read <c>lines[i]</c>, so the ordinary two-line
        /// <c>[Inject]</c> spelling — attribute above, declaration below — acquired the service and
        /// this pin said nothing; so did a verbatim type name and a generic split across lines. PROVED
        /// in the tree 2026-08-16, with the instrument as the only variable: a two-line
        /// <c>[Inject]</c> holding <c>global::SQLTriage.Data.Services.RbacService</c> in
        /// <c>Pages/ServerDocs.razor</c> compiled with 0 errors and left the census at 59/59, exit 0
        /// under the round-4 pin, and is named <c>Pages/ServerDocs.razor:182  [Inject]</c> under this
        /// one. That plant was HALF of a live bypass: the gate it fed sat on a markup line the other
        /// round-5 fix uncovers. The scan now runs over the whole file
        /// (<see cref="AcquisitionLines"/>).</para>
        ///
        /// <para><b>Corrected 2026-08-16 (round 4): "aliased or fully qualified" was one spelling
        /// short.</b> The <c>@inject</c> and <c>GetService&lt;&gt;</c> branches spelled the qualifier
        /// <c>[\w.]*</c>, which cannot match a colon, so <c>@inject
        /// global::SQLTriage.Data.Services.RbacService Rbac</c> acquired the service and this pin said
        /// nothing. LATENT at e5ef5cc — a raw grep for <c>global::</c> under Pages/ and Components/
        /// returned 0 — and PROVED by exercise the same day: planted in <c>Pages/ServerDocs.razor</c>
        /// it built with 0 errors and the whole census stayed green (41/41 in this class, 740/740
        /// across the Rbac filter, exit 0). The <c>@inject</c> branch now reads the whole line and the
        /// <c>GetService&lt;&gt;</c> branch uses <see cref="TypeQualifier"/>; with the fix in place the
        /// same plant is named as <c>Pages/ServerDocs.razor:13</c>. A NAMESPACE-ALIAS spelling
        /// (<c>@using Rb = global::…RbacService</c> then <c>@inject Rb X</c>) still does not match
        /// here — nothing on that inject line says <c>RbacService</c> — and is caught one layer over,
        /// by <see cref="NoShippedUiImportsRbacServiceStaticallyOrUnderAnAlias"/>; that pairing was
        /// exercised too, and the import ban named <c>Pages/ServerDocs.razor:8</c>.</para>
        /// </summary>
        private static readonly string[] RbacServiceHolders =
        {
            "Pages/Login.razor",        // RbacService.Config.Enabled — which sign-in methods to offer
            "Pages/Onboarding.razor",   // mints the first admin: GetUsers/AddUser/SetPassword/UpdateConfig
            "Pages/Settings.razor",     // the RBAC administration surface: users, roles, config, recovery
        };

        [Fact]
        public void TheOnlyShippedUiFilesHoldingAnRbacServiceInstanceAreThePinnedThree()
        {
            var root = RawPassedScan.RepoRoot();
            var found = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (var file in ShippedUiFiles())
            {
                var kind = KindOf(file);
                var raw = File.ReadAllText(file.FullName);
                var code = BlankLineComments(BlankBlockComments(raw, kind), kind);
                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
                var rawLines = raw.Split('\n');

                foreach (var line in AcquisitionLines(code))
                {
                    if (found.ContainsKey(relative)) break;
                    found[relative] = relative + ":" + line + "  " + rawLines[line - 1].Trim();
                }
            }

            var unexpected = found.Keys.Where(f => !RbacServiceHolders.Contains(f, StringComparer.Ordinal)).ToList();
            var stale = RbacServiceHolders.Where(f => !found.ContainsKey(f)).ToList();

            Assert.True(
                unexpected.Count == 0,
                "a shipped UI file that is not one of the three RBAC ADMINISTRATION pages has acquired "
                + "an RbacService instance. Holding the service is what makes the receiver-renamed gate "
                + "call spellable — Pages/ServerDocs.razor held a dangling one for a fortnight and a "
                + "planted gate through it passed the entire census. Gate through UserState, "
                + "<ShellGate> or <RbacGuard>; if this page genuinely administers RBAC, add it here "
                + "with the reason on the line, deliberately. New holders:\n  "
                + string.Join("\n  ", unexpected.Select(f => found[f])));

            Assert.True(
                stale.Count == 0,
                "a pinned RbacService holder no longer acquires it, so the pin is stale and is now "
                + "describing the tree wrongly. Delete the entry — an unused entry on a list nobody "
                + "re-reads is how KnownOffenders happened. Stale:\n  " + string.Join("\n  ", stale));
        }

        /// <summary>
        /// The optional qualifier that may stand in front of a type name: a dotted namespace path,
        /// an alias-qualified path, and <c>global::</c>. Whitespace is legal around each separator in
        /// C#, so it is allowed here too.
        ///
        /// <para><b>Why this exists, 2026-08-16 (round 4).</b> Four regexes in this file spelled the
        /// qualifier <c>[\w.]*</c>, and that character class cannot match a colon.
        /// <c>@inject global::SQLTriage.Data.Services.RbacService Rbac</c> is the same acquisition as
        /// the two spellings the pin already saw, and <c>[\w.]*</c> saw none of it: the class begins
        /// immediately after <c>@inject</c>'s whitespace, stops dead at the first <c>:</c>, and the
        /// alternation fails with no second chance. MEASURED at e5ef5cc — that inject, paired with a
        /// method-group call through it, compiled with 0 errors and left the census green — 41/41 in
        /// this class and 740/740 across the whole <c>~Rbac</c> filter, exit 0.</para>
        ///
        /// <para>This closes the <c>global::</c> spelling for the three positions where the qualifier
        /// is pinned by syntax (a static import, an alias import, a <c>GetService&lt;T&gt;</c> type
        /// argument). The <c>@inject</c> branch does not use it at all — see the regex below.</para>
        ///
        /// <para><b>Verbatim segments, 2026-08-16 (round 5).</b> <c>@</c> before an identifier is a
        /// legal C# spelling of that identifier, so <c>Services.@RbacService</c> and
        /// <c>@SQLTriage.Data.Services.RbacService</c> name the same type as the unprefixed forms.
        /// The round-4 qualifier could not read one — <c>(?:\w+\s*(?:\.|::)\s*)*</c> followed by
        /// <c>\bRbacService\b</c> has no way to consume the <c>@</c> that starts the final segment.
        /// MEASURED in the tree 2026-08-16: <c>using RB =
        /// global::SQLTriage.Data.Services.@RbacService;</c> planted at
        /// <c>Pages/Portal/ExportPack.razor.cs:7</c>, with a two-line <c>[Inject] private RB
        /// RbAlias</c> under it, compiled with 0 errors and left the census at 59/59, exit 0; with
        /// this fix and nothing else changed the import ban names that exact line. Each segment and
        /// the type name itself now allow the <c>@</c> — see <see cref="RbacServiceType"/>.</para>
        /// </summary>
        private const string TypeQualifier = @"(?:@?\w+\s*(?:\.|::)\s*)*";

        /// <summary>
        /// The type name, in either legal spelling: <c>RbacService</c> or the verbatim
        /// <c>@RbacService</c>. The trailing <c>\b</c> is what keeps <c>RbacServiceFactory</c> out.
        /// </summary>
        private const string RbacServiceType = @"@?\bRbacService\b";

        /// <summary>
        /// The DI acquisition forms a Blazor component has, under any variable name and any spelling
        /// of the type. Matched over the WHOLE source rather than line by line, because C# permits a
        /// newline everywhere it permits a space and three of these forms are routinely split.
        ///
        /// <para><b>The <c>@inject</c> branch reads the whole line</b> rather than trying to
        /// enumerate the ways the type may be qualified. An <c>@inject</c> line is a type and a
        /// variable name and nothing else, so a line-wide match is the respelling-proof reading:
        /// <c>global::</c>, whitespace around the dots, a verbatim <c>@</c> and a namespace alias all
        /// land on it without the pattern having to anticipate them. Its cost is a FALSE POSITIVE if
        /// a page ever injects some other service and names the variable <c>RbacService</c>, or
        /// writes the word in a trailing <c>//</c> comment on the inject line. That is the direction
        /// this file chooses everywhere: loud, never silent.</para>
        ///
        /// <para><b>Corrected 2026-08-16 (round 5): the pin was applied PER LINE, and said so
        /// nowhere.</b> Round 4's summary claimed "the four DI acquisition forms … under any variable
        /// name and any spelling of the type", but the <c>[Inject]</c> and <c>GetService&lt;&gt;</c>
        /// branches both required their whole match on one line. Three legal spellings therefore
        /// acquired the service in silence, and two of them were PROVED in the tree on 2026-08-16
        /// (build 0 errors, census 59/59, exit 0):</para>
        /// <list type="number">
        ///   <item><description>the conventional two-line property spelling — <c>[Inject]</c> on its
        ///   own line, <c>private RbacService Rbac { get; set; } = default!;</c> on the next. This is
        ///   how <c>[Inject]</c> is written in most Blazor code, so the pin was blind to the ORDINARY
        ///   form and saw only the compressed one.</description></item>
        ///   <item><description>a verbatim type name (<c>Services.@RbacService</c>), which the
        ///   qualifier could not read — see <see cref="TypeQualifier"/>.</description></item>
        ///   <item><description>a generic argument split across lines:
        ///   <c>GetService&lt;</c> … newline … <c>RbacService&gt;()</c>.</description></item>
        /// </list>
        /// <para>The <c>[Inject]</c> branch now spans from the attribute to the type name across
        /// anything that is not a <c>;</c>, <c>{</c> or <c>}</c> — i.e. to the end of the member
        /// declaration the attribute is attached to, and no further. <c>\s</c> in the generic branch
        /// already matched a newline; the per-line application was what stopped it. A fifth form,
        /// <c>GetService(typeof(RbacService))</c>, is covered too — it was never in the "four" and
        /// was never named as a limit.</para>
        ///
        /// <para><b>What it still does not see, named rather than claimed away.</b> An acquisition
        /// that never spells the type in this file: a <c>Type</c> value computed elsewhere and handed
        /// to the non-generic <c>GetService</c>, a string-concatenated reflective lookup, or a
        /// property on some OTHER injected service that returns an <c>RbacService</c>. The last is
        /// covered by the call ban, which does not care what the receiver is; the first two are not
        /// covered here at all. A namespace-ALIAS import (<c>@using Rb = …RbacService</c> then
        /// <c>@inject Rb X</c>) is caught one layer over by
        /// <see cref="NoShippedUiImportsRbacServiceStaticallyOrUnderAnAlias"/>.</para>
        /// </summary>
        internal static readonly Regex RbacServiceAcquisition = new(
              @"(@inject\b[^\n]*" + RbacServiceType + @")"
            + @"|(\[Inject\][^;{}]*?" + RbacServiceType + @")"
            + @"|(Get(?:Required)?Service\s*<\s*" + TypeQualifier + RbacServiceType + @"\s*>)"
            + @"|(Get(?:Required)?Service\s*\(\s*typeof\s*\(\s*" + TypeQualifier + RbacServiceType + @"\s*\))",
            RegexOptions.Compiled);

        /// <summary>
        /// The 1-based line of every acquisition in <paramref name="code"/> (which must already have
        /// been through the comment blankers). The line reported is where the acquisition STARTS —
        /// the <c>[Inject]</c> attribute, not the declaration under it.
        /// </summary>
        internal static List<int> AcquisitionLines(string code)
        {
            var hits = new List<int>();
            foreach (Match m in RbacServiceAcquisition.Matches(code))
                hits.Add(LineOf(code, m.Index));
            return hits;
        }

        /// <summary>
        /// Method names that decide authorization. <c>IsAuthorizedWithBreakGlass(</c> does not match:
        /// the <c>(</c> must follow the name directly.
        /// </summary>
        internal static readonly Regex AuthorityDecisionCall =
            new(@"(?<!\w)(IsAuthorized|HasPermission)\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// The same two names NOT followed by a <c>(</c> — a method GROUP rather than a call:
        /// <c>_gate = Rbac.IsAuthorized;</c>, <c>Func&lt;string,string,bool&gt; f =
        /// RbacService.HasPermission;</c>, or the name handed to a <c>nameof</c>. A method group has
        /// no argument list, so <see cref="CountTopLevelArguments"/> has nothing to count and the
        /// argument-count ban goes quiet; the call happens later through a delegate the scan cannot
        /// follow. <c>IsAuthorizedWithBreakGlass</c> does not match here either — the <c>\b</c> after
        /// the name needs a boundary, and <c>d</c>→<c>W</c> is not one.
        /// </summary>
        internal static readonly Regex AuthorityDecisionMethodGroup =
            new(@"(?<!\w)(IsAuthorized|HasPermission)\b(?!\s*\()", RegexOptions.Compiled);

        /// <summary>
        /// The <c>ArgCount</c> an offence carries when it is a method group and there is no argument
        /// list to count.
        /// </summary>
        internal const int MethodGroupArgCount = -1;

        /// <summary>
        /// Every authorization call in <paramref name="source"/> that a shipped UI file may not make,
        /// as (1-based line, method name, top-level argument count). Comments are prose and are
        /// blanked first, by the same region-scoped rules every scan in this file uses — a closed
        /// <c>@* … *@</c> whose opener is not itself literal content, and a <c>/* … */</c> span or a
        /// line-initial <c>//</c>, <c>///</c> or <c>*</c> INSIDE a C# region. In Razor markup none of
        /// those four C# prefixes is a comment, so nothing there is blanked; inside a C# region a
        /// <c>@*</c> that sits in a string, a character literal or a <c>//</c> comment is content and
        /// opens nothing. See <see cref="BlankBlockComments"/> and
        /// <see cref="BlankLineComments"/>.
        ///
        /// <para><b>Method groups, added 2026-08-16 (round 4).</b> The argument-count ban reads a call
        /// site, so <c>_gate = Rbac.IsAuthorized;</c> followed by <c>_gate(role, permission)</c>
        /// elsewhere had no call site to read: the assignment has no parentheses and the invocation
        /// has no banned name. That indirection was a documented limit of this scan at e5ef5cc, and
        /// it was half of the combined bypass this round closes. Taking a delegate to either method is
        /// now itself the offence, whatever the receiver. That is a ban on the sanctioned
        /// <c>UserState.IsAuthorized</c> too, deliberately: once the name is a delegate there is no
        /// argument count left to distinguish the one-argument gate from the two-argument bypass, so
        /// the honest rule is that shipped UI calls these names and never stores them. MEASURED
        /// 2026-08-16 over the shipped tree: 58 raw occurrences of the two names stand outside a call,
        /// every one of them prose inside a comment, and 0 survive the comment blanking — so this ban
        /// is silent on the tree it shipped against.</para>
        ///
        /// <para>Still out of reach, and named here rather than claimed away: a decision reached
        /// through some OTHER method name (a wrapper service with its own <c>Check(role, perm)</c>),
        /// or a delegate field assigned in one file and invoked in another. This is a lint over source
        /// text under Pages/ and Components/, not a boundary.</para>
        /// </summary>
        internal static List<(int Line, string Method, int ArgCount)> AuthorityBypassOffences(string source) =>
            AuthorityBypassOffences(source, UiSourceKind.Razor);

        internal static List<(int Line, string Method, int ArgCount)> AuthorityBypassOffences(
            string source, UiSourceKind kind)
        {
            var code = BlankLineComments(BlankBlockComments(source, kind), kind);
            var offences = new List<(int, string, int)>();

            foreach (Match m in AuthorityDecisionCall.Matches(code))
            {
                var method = m.Groups[1].Value;
                var open = code.IndexOf('(', m.Index);
                if (open < 0) continue;

                var argCount = CountTopLevelArguments(code, open);
                if (argCount < 0) continue;                       // never closes: not a call we can read

                if (method != "HasPermission" && argCount < 2) continue;   // the sanctioned single-arg gate

                offences.Add((LineOf(code, m.Index), method, argCount));
            }

            foreach (Match m in AuthorityDecisionMethodGroup.Matches(code))
                offences.Add((LineOf(code, m.Index), m.Groups[1].Value, MethodGroupArgCount));

            return offences.OrderBy(o => o.Item1).ToList();
        }

        /// <summary>How an offence's shape reads in the failure message.</summary>
        private static string DescribeShape(int argCount) =>
            argCount == MethodGroupArgCount
                ? "method group, not a call"
                : argCount + " arg" + (argCount == 1 ? "" : "s");

        /// <summary>
        /// Blanks whole-line C# comments in place, preserving length so offsets and line numbers
        /// survive. A line is prose when it STARTS with <c>///</c>, <c>//</c> or <c>*</c> AND that
        /// first character sits inside a C# region — the whole file for <c>.cs</c>, the brace-matched
        /// body of each <c>@code</c>/<c>@functions</c> block for <c>.razor</c>
        /// (see <see cref="CSharpRegions"/>).
        ///
        /// <para><b>Region-scoped 2026-08-16 (round 5), for the reason round 4 region-scoped
        /// <see cref="BlankBlockComments"/> and did not carry the ruling to this sibling.</b> The
        /// ruling was "encode the language, not a pattern". In Razor MARKUP a line beginning
        /// <c>//</c>, <c>///</c> or <c>*</c> is not a comment: Razor emits those characters as text
        /// and COMPILES every <c>@</c> transition on the line. PROVED by the compiler 2026-08-16 —
        /// <c>// @DateTime.Now.NoSuchMemberAaa</c>, <c>* @DateTime.Now.NoSuchMemberBbb</c> and
        /// <c>/// @DateTime.Now.NoSuchMemberCcc</c> planted in the markup of
        /// <c>Pages/ServerDocs.razor</c> lines 15-17 each failed the build with <c>CS1061</c> at
        /// their own line and column — <c>(15,18)</c>, <c>(16,17)</c>, <c>(17,19)</c>, 3 errors.
        /// Until this change all three were blanked as prose, and a live fail-open gate written on
        /// one of them — <c>// @if (!RbacHole.IsAuthorized(UserState.Role, "settings")) { … }</c>,
        /// reaching the service through a two-line <c>[Inject]</c> — compiled with 0 errors and left
        /// the census at 59/59, exit 0, and the whole <c>~Rbac</c> filter at 758/758. With this
        /// change and nothing else different, the same plant is named
        /// <c>Pages/ServerDocs.razor:15  [IsAuthorized, 2 args]</c>. Note that the slashes are
        /// markup, so ordinary markup hides them from a reader's eye as it hides anything else —
        /// "it would render as visible junk" is not a defence.</para>
        ///
        /// <para><b>Cost, MEASURED over the shipped tree 2026-08-16 before the change was accepted,
        /// and it is not small.</b> 101 lines across 9 of the 154 shipped <c>.razor</c> files stop
        /// being blanked (counted on <see cref="BlankBlockComments"/>' output, which is what this
        /// method actually receives). Every one of them begins <c>//</c> — none begins <c>///</c> or
        /// <c>*</c>, and none is inside a <c>&lt;style&gt;</c> block. They are not markup text: they
        /// are GENUINE C# comments inside Razor markup transition blocks — the bodies of
        /// <c>@for</c>, <c>@if</c>/<c>else</c> and <c>@{ … }</c>, which
        /// <see cref="CSharpRegions"/> deliberately does not recognise as regions, because widening
        /// the regions widens BLANKING and blanking is the silent direction. The heaviest are
        /// <c>Pages/CioDashboard.razor</c> (26), <c>Pages/Portal/PortalStatus.razor</c> (25) and
        /// <c>Pages/Governance.razor</c> (20).</para>
        ///
        /// <para>Not one of the 101 carries <c>IsAuthorized</c>, <c>HasPermission</c> or the token
        /// <c>RbacService</c>, and the whole tree still measures 0 offences — so the census is silent
        /// on the tree it ships against. But a future <c>//</c> comment written in one of those
        /// blocks that happens to NAME a banned method will fail the build. That is a false positive,
        /// which is this file's chosen direction, and the fix when it fires is to reword the comment
        /// or move it into <c>@code</c> — never to add an allowlist, and never to widen the regions,
        /// which would trade a loud wrong answer for a silent one.</para>
        /// </summary>
        internal static string BlankLineComments(string source) =>
            BlankLineComments(source, UiSourceKind.Razor);

        internal static string BlankLineComments(string source, UiSourceKind kind)
        {
            var regions = CSharpRegions(source, kind);
            var lines = source.Split('\n');
            var offset = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var first = offset + (line.Length - trimmed.Length);

                if ((trimmed.StartsWith("///") || trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                    && regions.Any(r => first >= r.Start && first < r.End))
                    lines[i] = new string(' ', line.Length);

                offset += line.Length + 1;                       // the '\n' Split consumed
            }

            return string.Join("\n", lines);
        }

        private static int LineOf(string code, int index)
        {
            var line = 1;
            for (int i = 0; i < index && i < code.Length; i++)
                if (code[i] == '\n') line++;
            return line;
        }

        /// <summary>
        /// Counts top-level arguments of the call whose opening parenthesis is at
        /// <paramref name="open"/>. String and character literals are skipped whole, so a comma or a
        /// parenthesis inside <c>"a, b"</c> counts for nothing. Returns 0 for <c>()</c> and -1 when the
        /// parenthesis never closes.
        /// </summary>
        private static int CountTopLevelArguments(string code, int open)
        {
            int depth = 0, commas = 0;
            var sawContent = false;

            for (int i = open; i < code.Length; i++)
            {
                var c = code[i];

                if (c == '"')
                {
                    i = SkipStringLiteral(code, i);
                    sawContent = true;
                    continue;
                }

                if (c == '\'')
                {
                    var end = SkipCharLiteral(code, i);
                    if (end > i) { i = end; sawContent = true; continue; }
                    // A lone apostrophe in markup or prose. Not a literal; read on.
                }

                if (c is '(' or '[' or '{') { depth++; continue; }
                if (c is ')' or ']' or '}')
                {
                    depth--;
                    if (depth <= 0) return commas > 0 ? commas + 1 : (sawContent ? 1 : 0);
                    continue;
                }

                if (c == ',' && depth == 1) { commas++; continue; }
                if (!char.IsWhiteSpace(c)) sawContent = true;
            }
            return -1;
        }

        /// <summary>
        /// End index of the string literal opening at <paramref name="quote"/>, reading its own
        /// <c>@</c> and <c>$</c> prefix rather than being told about it.
        ///
        /// <para><b>Interpolation holes, added 2026-08-16 (round 6), because they were a live
        /// bypass of the round-6 rule itself.</b> Until this change the walk stopped at the first
        /// unescaped <c>"</c> after the opener, so a hole containing a nested literal —
        /// <c>$"{string.Join(", ", xs)}"</c>, of which the shipped tree holds 52 — was read as two
        /// literals with a GAP between them, and characters inside the real literal came back
        /// unmasked. PROVED in the tree 2026-08-16 with the instrument as the only variable:
        /// <c>private string _gpHole = $"{string.Join(", ", new[]{ "@*" })}";</c> planted in
        /// <c>Pages/ComplianceTree.razor</c>'s <c>@code</c> block at line 202 put its <c>@*</c> in
        /// exactly such a gap, opened a comment that the real <c>*@</c> at line 429 closed, and hid a
        /// static two-argument <c>HasPermission</c> gate at line 391 from both bans — build 0
        /// errors. That is the round-5 defect surviving the round-6 fix one literal-kind away, so it
        /// is closed here rather than named in a paragraph.</para>
        ///
        /// <para>A hole is ordinary C#, so a literal inside one is skipped whole, recursively;
        /// <c>{{</c> and <c>}}</c> are escaped braces and open no hole. A NON-verbatim literal still
        /// stops at its line, hole or not, which is the fail-safe that keeps an unterminated one from
        /// running away.</para>
        ///
        /// <para><b>Raw string literals, added 2026-08-16 (round 7).</b> Round 6's
        /// <see cref="CSharpLiteralMask"/> doc named the raw string as a KNOWN misread and filed it
        /// under "every failure direction of this map is under-blanking". That filing was wrong, and
        /// the direction is what mattered: <c>"""…"""</c> was read as an empty literal <c>""</c>
        /// followed by an ordinary one, so the characters BETWEEN the fences came back unmasked in
        /// a gap — the same shape as the round-6 interpolation-hole defect. A <c>@*</c> parked in
        /// that gap was an eligible opener, and PROVED by the round-7 gate it borrowed a real
        /// <c>*@</c> below it and blanked the code between. That is a SILENCE, not a false positive.
        /// A raw string is now read by its fence: three or more <c>"</c> with no <c>@</c> prefix
        /// open one, and it ends at a run of at least as many. Pinned by MutationTwentyNine.</para>
        ///
        /// <para>MEASURED 2026-08-16 over the 161 shipped UI files: 0 raw string literals, so this
        /// fix moves nothing in the tree today and exists for the next one written. Named limit: an
        /// interpolated raw string (<c>$$"""…"""</c>) has its holes read as literal content rather
        /// than as C#, which masks MORE and is therefore the loud direction; and a raw string with
        /// no closing fence masks to end of file, which removes openers (loud) — though its side
        /// effect in <see cref="MatchingBrace"/>, withdrawing the region entirely, is the SILENT
        /// direction per <see cref="CSharpLiteralRegions"/>. Unreachable in practice: an
        /// unterminated raw string does not compile.</para>
        /// </summary>
        private static int SkipStringLiteral(string code, int quote)
        {
            bool verbatim = false, interpolated = false;
            for (int k = quote - 1; k >= 0 && (code[k] == '@' || code[k] == '$'); k--)
                if (code[k] == '@') verbatim = true;
                else interpolated = true;

            if (!verbatim)
            {
                var fence = 0;
                while (quote + fence < code.Length && code[quote + fence] == '"') fence++;
                if (fence >= 3) return SkipRawStringLiteral(code, quote, fence);
            }

            var depth = 0;                          // interpolation-hole nesting

            for (int i = quote + 1; i < code.Length; i++)
            {
                var c = code[i];

                if (!verbatim && c == '\n') return i;   // unterminated: stop at the line

                if (interpolated && (c == '{' || c == '}'))
                {
                    if (i + 1 < code.Length && code[i + 1] == c) { i++; continue; }   // `{{` / `}}`
                    if (c == '{') depth++;
                    else if (depth > 0) depth--;
                    continue;
                }

                if (depth > 0)                      // inside a hole: ordinary C#
                {
                    if (c == '"') { i = SkipStringLiteral(code, i); continue; }
                    if (c == '\'') { var e = SkipCharLiteral(code, i); if (e > i) i = e; }
                    continue;
                }

                if (verbatim)
                {
                    if (c != '"') continue;
                    if (i + 1 < code.Length && code[i + 1] == '"') { i++; continue; }
                    return i;
                }

                if (c == '\\') { i++; continue; }
                if (c == '"') return i;
            }
            return code.Length - 1;
        }

        /// <summary>
        /// End index of the raw string literal whose opening fence of <paramref name="fence"/>
        /// quotes starts at <paramref name="quote"/>. The literal ends at the last quote of the
        /// first run of <paramref name="fence"/> or more; an unterminated one masks to end of file,
        /// which removes openers rather than adding them.
        ///
        /// <para>A raw string cannot carry an <c>@</c> prefix, which is what keeps the verbatim
        /// spelling <c>@"""x"""</c> — a verbatim string holding <c>"x"</c>, and three quotes wide at
        /// its opener — out of this walk. That discrimination is pinned by MutationTwentyNine's
        /// third case.</para>
        /// </summary>
        private static int SkipRawStringLiteral(string code, int quote, int fence)
        {
            for (int i = quote + fence; i < code.Length; i++)
            {
                if (code[i] != '"') continue;

                var run = 0;
                while (i + run < code.Length && code[i + run] == '"') run++;
                if (run >= fence) return i + run - 1;

                i += run - 1;
            }
            return code.Length - 1;                 // unterminated: mask to EOF, the loud direction
        }

        /// <summary>
        /// End index of a character literal, or <paramref name="quote"/> when the apostrophe does not
        /// open one — markup and prose are full of bare apostrophes, and a char literal is short.
        /// </summary>
        private static int SkipCharLiteral(string code, int quote)
        {
            var limit = Math.Min(code.Length - 1, quote + 4);
            for (int i = quote + 1; i <= limit; i++)
            {
                if (code[i] == '\\') { i++; continue; }
                if (code[i] == '\'') return i;
            }
            return quote;
        }

        /// <summary>Every shipped UI source file the census reads: Pages/ and Components/, .razor and .cs.</summary>
        internal static List<FileInfo> ShippedUiFiles()
        {
            var root = RawPassedScan.RepoRoot();
            var files = new List<FileInfo>();
            foreach (var scanRoot in new[] { "Pages", "Components" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, scanRoot));
                if (!dir.Exists) continue;

                files.AddRange(dir.EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.Extension is ".razor" or ".cs"));
            }
            return files;
        }

        // ── The two receiver-named regexes ───────────────────────────────
        //
        // RETIRED 2026-08-17, with the four [Fact]s they served: the whole-tree wrappers
        // ScanForStaticHasPermission(), ScanForDirectIsAuthorized() and the private ScanFor(Regex)
        // they shared. All three had zero callers once the tests went, and dead code inside the file
        // that just condemned untriggerable test machinery is the same defect wearing a coat.
        //
        // The REGEXES themselves stay, and are not decoration: the ~29 mutation tests below drive
        // them through OffendingLines() to prove the comment strippers and the region model still see
        // what they claim to. Items 1-3 of this class's header all read source text through that same
        // machinery, so retiring it would leave those scans quietly blind. What went is the two
        // whole-tree entry points; what stayed is the instrument they were pointed at.
        //
        // Their history, kept because it is the argument for the modifiers that replaced them:
        //
        // StaticHasPermissionCall — the first ban. Comments are excluded because they are prose, not
        // gates. That exclusion used to be per-line, which reads a Razor @* … *@ block (the house
        // style) as prose on its first line and as CODE on every continuation line: the moment this
        // lane documented on the gate what the old call had been, the scan reported the documentation
        // as the offence. That is the failure mode that produces allowlists, and this file already
        // shows where exemptions end up. Since 2026-08-15 the scanner blanks Razor @* … *@ and C#
        // /* … */ spans before looking, keeping line numbers by replacing commented characters in
        // place rather than removing them.
        //
        // DirectIsAuthorizedCall — the second ban, added 2026-08-15 for the fail-OPEN two-argument
        // overload, which hard-coded bootstrapEligible: true and so returned TRUE for any caller on
        // an unconfigured install, LAN included. The shape was never hypothetical:
        // Pages/Settings.razor gated on that overload from 84b944e (2026-05-16) to a5497e5
        // (2026-08-01), 77 days, and a5497e5 both replaced it AND created this census file in one
        // commit — so the census never overlapped the live call, and had the call survived it would
        // have gone unnamed. That is timing, not a property of the code. Corrected 2026-08-15: the
        // overload was an INSTANCE method, so "RbacService.IsAuthorized(" is not a spelling any
        // compiling call can have; the historical match happened only because the injected variable
        // was itself named RbacService (a5497e5^:25). This regex bans one receiver NAME.
        // AuthorityBypassOffences is the ban that does not care who the receiver is, and it is the
        // one carrying weight today — see item 2 of the class header.

        private static readonly Regex StaticHasPermissionCall =
            new(@"(?<!\w)RbacService\.HasPermission\s*\(", RegexOptions.Compiled);

        private static readonly Regex DirectIsAuthorizedCall =
            new(@"(?<!\w)RbacService\.IsAuthorized\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// The scan itself, over source text rather than the filesystem, so the mutation tests below
        /// can plant a call and read the verdict without writing into the shipped tree. Returns
        /// 1-based line numbers of every banned call that is not inside a comment. The default ban is
        /// the static <c>RbacService.HasPermission(</c>; pass <see cref="DirectIsAuthorizedCall"/> for
        /// the fail-open overload.
        /// </summary>
        internal static List<int> OffendingLines(string source) => OffendingLines(source, StaticHasPermissionCall);

        internal static List<int> OffendingLines(string source, Regex banned) =>
            OffendingLines(source, banned, UiSourceKind.Razor);

        internal static List<int> OffendingLines(string source, Regex banned, UiSourceKind kind)
        {
            // Whole-line C# comments are prose and are blanked by the same region-scoped rule the
            // argument-count ban and the holder pin use — a line-initial `//`, `///` or `*` is prose
            // only inside a C# region, because in Razor markup those characters are text and the
            // line is compiled. Round 5, 2026-08-16: this scan used to apply the prose skip per line
            // with no region test at all. See BlankLineComments.
            var lines = BlankLineComments(BlankBlockComments(source, kind), kind).Split('\n');
            var hits = new List<int>();

            for (int i = 0; i < lines.Length; i++)
                if (banned.IsMatch(lines[i])) hits.Add(i + 1);

            return hits;
        }

        // ── The census has teeth, proved by mutation ─────────────────────
        //
        // Added 2026-08-15. Every assertion above is an ABSENCE — "the scan found nothing
        // unexpected" — and an absence is exactly what a scan that cannot see returns. The tests
        // below plant the offence and require the scan to name it, so a future edit that quietly
        // neuters the regex or over-blanks the comment stripper fails here rather than going green
        // over a live defect. This is the shape the 2026-08-01 allowlist should have had.

        [Fact]
        public void MutationOne_APlantedStaticCallIsCaught()
        {
            const string page = @"@page ""/scratch""
@inject SQLTriage.Data.Services.RbacService RbacService

@if (!RbacService.HasPermission(UserState.Role, ""settings""))
{
    <p>denied</p>
}";
            var hits = OffendingLines(page);

            Assert.True(hits.Count == 1, "the scan must name the planted call; hits: " + string.Join(",", hits));
            Assert.Equal(4, hits[0]);
        }

        [Fact]
        public void MutationTwo_TheShippedAuditPageGateShapeIsCaughtIfItComesBack()
        {
            // The exact line this lane removed from Pages/AuditLogViewer.razor. If someone restores
            // it — the "fix it back" this lane's on-gate comment exists to prevent — the census goes
            // red naming the file, which is what did NOT happen for a fortnight.
            const string restored = @"@inject SQLTriage.Data.Services.AppUserState UserState
@if (!RbacService.HasPermission(UserState.Role, ""settings""))
{
    <div class=""audit-log-denied""></div>
}";
            Assert.Equal(new[] { 2 }, OffendingLines(restored));
        }

        [Fact]
        public void MutationThree_ACallInsideARazorCommentIsProseOnEveryLineOfIt()
        {
            // The false positive that motivated the block-comment fix: the offending token sits on a
            // CONTINUATION line of an @* … *@ block, which the old per-line test read as code.
            const string documented = @"@* ALIGNED 2026-08-15.
   Until this date the gate read RbacService.HasPermission(UserState.Role, ""settings""), the static
   matrix that cannot see the bootstrap hatch. Do not restore it. *@
@if (!UserState.IsAuthorized(""settings""))
{
}";
            Assert.Empty(OffendingLines(documented));
        }

        [Fact]
        public void MutationFour_ACallInsideACSharpBlockCommentIsProse()
        {
            const string code = @"@code {
    /* historical note:
       RbacService.HasPermission(role, ""settings"") was the old gate. */
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Empty(OffendingLines(code));
        }

        [Fact]
        public void MutationFive_BlankingAClosedCommentDoesNotSwallowTheCodeAfterIt()
        {
            // The over-blank failure mode, which fails SILENTLY GREEN and is therefore the dangerous
            // one: if the stripper ran past *@ it would hide every gate below the file's banner
            // comment — and every shipped page in this repo opens with one.
            const string mixed = @"@* banner comment *@
@if (!RbacService.HasPermission(UserState.Role, ""settings""))
{
}";
            Assert.Equal(new[] { 2 }, OffendingLines(mixed));
        }

        [Fact]
        public void MutationSix_AnUnclosedRazorCommentDoesNotHideLaterCallsBeyondItsOwnSpan()
        {
            // Interleaved openers: a `/*` inside a Razor comment must not open a second span. Two
            // independent stripping passes got this wrong and blanked to end-of-file.
            const string interleaved = @"@* a comment mentioning /* an inner opener *@
@if (!RbacService.HasPermission(UserState.Role, ""settings""))
{
}";
            Assert.Equal(new[] { 2 }, OffendingLines(interleaved));
        }

        // ── The three shapes that were NOT pinned, and so shipped ────────
        //
        // Added 2026-08-15 round 2. MutationOne..Six pinned the Razor-comment hazards and left the
        // C-comment ones unpinned; both unpinned shapes already existed in the tree, and both blinded
        // the scan. A hazard named in a `concerns` list and not measured against the tree is a hazard
        // that ships.

        [Fact]
        public void MutationSeven_ASlashStarInMarkupDoesNotBlindTheRestOfTheFile()
        {
            // Pages/Settings.razor:1502, verbatim. The `/*` in the markup text `Config/*.json` is not
            // an opener; nothing below it may go invisible. MEASURED at 7df4b82: it blanked line 1502
            // to end of file, 373 lines.
            const string page = @"<p>SHA-256 hashes of <code>appsettings.json</code> + <code>Config/*.json</code> are snapshotted</p>
@if (!RbacService.HasPermission(UserState.Role, ""settings""))
{
}";
            Assert.Equal(new[] { 2 }, OffendingLines(page));
        }

        [Fact]
        public void MutationEight_ASlashStarInsideAStringLiteralDoesNotOpenAComment()
        {
            // Components/Shared/QueryPlanModal.razor:263, in shape. The literal `"/*"` in the DDL
            // safety check opened a span that a real `catch { /* … */ }` thirty lines below closed,
            // hiding the write path between them. MEASURED at 7df4b82: it blanked 263-293.
            const string code = @"@code {
    private async Task Guard(string ddl)
    {
        if (ddl.Contains(""--"") || ddl.Contains(""/*"")) return;
        if (!RbacService.HasPermission(UserState.Role, ""settings"")) return;
        try { await Run(ddl); }
        catch { /* nothing to do */ }
    }
}";
            Assert.Equal(new[] { 5 }, OffendingLines(code));
        }

        [Fact]
        public void MutationNine_AnUnclosedOpenerBlanksNothingAtAll()
        {
            // The fail-safe direction, stated as a test. Code that compiles cannot hold an unmatched
            // comment opener, so an unmatched one is always a FALSE opener — and blanking to end of
            // file on one is exactly how 404 lines went invisible. Blank nothing; let the false
            // positive be loud.
            const string razorUnclosed = @"@* opened and never closed
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { }";
            Assert.Equal(new[] { 2 }, OffendingLines(razorUnclosed));

            const string cUnclosed = @"@code {
    /* opened and never closed
    private bool May => RbacService.HasPermission(UserState.Role, ""settings"");";
            Assert.Equal(new[] { 3 }, OffendingLines(cUnclosed));
        }

        [Fact]
        public void MutationTen_APlantedDirectIsAuthorizedIsCaughtByItsOwnBan()
        {
            // The fail-OPEN overload. Two bans, not one: the HasPermission regex does not cover this
            // spelling, which is why a page carrying it passed all 78 tests at 7df4b82.
            const string page = @"@code {
    private bool MayConfigure => RbacService.IsAuthorized(UserState.Role, ""settings"");
}";
            Assert.Equal(new[] { 2 }, OffendingLines(page, DirectIsAuthorizedCall));
            Assert.Empty(OffendingLines(page));
        }

        /// <summary>
        /// The shape that defeated round 2, in situ. <c>Pages/ServerDocs.razor:11</c> held
        /// <c>@inject RbacService Rbac</c> — dangling, unused, present since bec0d4e — and a gate
        /// planted through it compiled with 0 errors and passed all 88 census tests at 3fe19a3. Every
        /// ban that existed named a receiver; this one is the same call with the receiver renamed.
        /// </summary>
        [Fact]
        public void MutationEleven_ATwoArgCallThroughAnInjectedAliasIsCaught()
        {
            const string page = @"@page ""/scratch""
@inject RbacService Rbac
@inject AppUserState UserState

@if (!Rbac.IsAuthorized(UserState.Role, ""settings""))
{
    <p>denied</p>
}";
            var offences = AuthorityBypassOffences(page);

            Assert.Equal(new[] { 5 }, offences.Select(o => o.Line).ToArray());
            Assert.Equal("IsAuthorized", offences[0].Method);
            Assert.Equal(2, offences[0].ArgCount);

            // And the reason it needed writing: neither receiver-named ban sees this line.
            Assert.Empty(OffendingLines(page));
            Assert.Empty(OffendingLines(page, DirectIsAuthorizedCall));
        }

        /// <summary>
        /// The other half of a usable ban: the sanctioned gate must NOT be an offence. Dozens of call
        /// sites in Pages/ and Components/ spell it this way — 88 re-measured 2026-08-16: 84
        /// <c>UserState.IsAuthorized(</c> plus 4 <c>UserState.IsAuthorizedWithBreakGlass(</c> — and a
        /// scan that cried at them would be answered with an allowlist, which is how this file's
        /// <c>KnownOffenders</c> happened.
        ///
        /// <para><b>Corrected 2026-08-16 (round 5): the figure said 89, and 89 counted a doc
        /// comment.</b> The 85th <c>IsAuthorized</c> the old number needed is
        /// <c>Components/Shared/ShellGate.razor:76</c>, a <c>&lt;see cref="AppUserState.IsAuthorized
        /// (string)"/&gt;</c> inside a <c>///</c> paragraph — prose, on the TYPE name, not a call.
        /// <c>grep -rhoE "[A-Za-z0-9_.]*\.IsAuthorized\("</c> over Pages/ and Components/ returns 84
        /// <c>UserState.</c> and that one <c>AppUserState.</c>; no other receiver spells it. Nothing
        /// asserts on the number — it is there to say the sanctioned shape is common — but a figure
        /// not conditioned on the measurement it names is this house's own defect class, and it does
        /// not get to stand in the file that exists to close that class.</para>
        /// </summary>
        [Fact]
        public void MutationTwelve_TheSanctionedSingleArgGateIsNotAnOffence()
        {
            const string page = @"@page ""/scratch""
@inject AppUserState UserState

@if (!UserState.IsAuthorized(""settings""))
{
    <p>denied</p>
}

@code {
    private bool MayTune => UserState.IsAuthorizedWithBreakGlass(""settings"");
    private bool MayRun => UserState.IsAuthorized(GetPermission(""run"", ""scripts""));
}";
            // Line 10 is a different method name; line 11 is one argument whose own arguments carry a
            // comma, which is exactly what the paren-and-literal walk exists to get right.
            Assert.Empty(AuthorityBypassOffences(page));
        }

        /// <summary>
        /// A bare <c>HasPermission(</c> with no receiver at all — the spelling a
        /// <c>@using static … RbacService</c> makes available.
        /// <see cref="NoShippedUiImportsRbacServiceStaticallyOrUnderAnAlias"/> bans the import; this
        /// bans the call, so the import ban stops being the only thing standing between a static
        /// import and a silent census.
        /// </summary>
        [Fact]
        public void MutationThirteen_ABareHasPermissionCallIsCaughtWithNoReceiverAtAll()
        {
            const string page = @"@code {
    private bool MayConfigure => HasPermission(UserState.Role, ""settings"");
}";
            var offences = AuthorityBypassOffences(page);

            Assert.Equal(new[] { 2 }, offences.Select(o => o.Line).ToArray());
            Assert.Equal("HasPermission", offences[0].Method);
            Assert.Empty(OffendingLines(page));   // the receiver-named ban does not see it
        }

        /// <summary>
        /// The acquisition scan behind
        /// <see cref="TheOnlyShippedUiFilesHoldingAnRbacServiceInstanceAreThePinnedThree"/> sees the
        /// DI forms written on ONE line, under any variable name and either spelling of the type, and
        /// does not mistake prose for an acquisition. The forms that span lines, and the verbatim
        /// spelling, are pinned by MutationTwentyOne.
        /// </summary>
        [Theory]
        [InlineData("@inject RbacService Rbac", true)]
        [InlineData("@inject SQLTriage.Data.Services.RbacService AnythingAtAll", true)]
        [InlineData("    [Inject] private RbacService Rbac { get; set; } = default!;", true)]
        [InlineData("    [Inject] private SQLTriage.Data.Services.RbacService R { get; set; } = default!;", true)]
        [InlineData("        var rbac = Services.GetRequiredService<RbacService>();", true)]
        [InlineData("        var rbac = sp.GetService<SQLTriage.Data.Services.RbacService>();", true)]
        [InlineData("@inject AppUserState UserState", false)]
        [InlineData("    /// Matched against RbacService.HasPermission.", false)]
        public void MutationFourteen_EveryDiAcquisitionFormIsSeenAndNothingElseIs(string line, bool expected)
        {
            var code = BlankLineComments(BlankBlockComments(line));
            Assert.Equal(expected, RbacServiceAcquisition.IsMatch(code));
        }

        // ── The three holes round 3 left, and the limit it documented ────────────────────────────
        //
        // Added 2026-08-16 (round 4). All three were LATENT at e5ef5cc — a raw grep proved nothing in
        // that tree hid in any of them — and all three were proved by exercise, not by reading.

        /// <summary>
        /// <c>global::</c> is a type qualifier the four acquisition regexes could not read, because
        /// all four spelled the qualifier <c>[\w.]*</c> and no character class containing only word
        /// characters and dots can match a colon. MEASURED at e5ef5cc:
        /// <c>@inject global::SQLTriage.Data.Services.RbacService Rbac</c> planted in a shipped page
        /// compiled with 0 errors and the holder pin did not name it.
        /// </summary>
        [Theory]
        [InlineData("@inject global::SQLTriage.Data.Services.RbacService Rbac", true)]
        [InlineData("@inject global :: SQLTriage . Data . Services . RbacService Rbac", true)]
        [InlineData("    [Inject] private global::SQLTriage.Data.Services.RbacService R { get; set; } = default!;", true)]
        [InlineData("        var rbac = sp.GetRequiredService<global::SQLTriage.Data.Services.RbacService>();", true)]
        [InlineData("        var rbac = sp.GetService<global::SQLTriage.Data.Services.RbacService>();", true)]
        [InlineData("@inject global::SQLTriage.Data.Services.AppUserState UserState", false)]
        public void MutationFifteen_AGlobalQualifiedAcquisitionIsSeen(string line, bool expected)
        {
            var code = BlankLineComments(BlankBlockComments(line));
            Assert.Equal(expected, RbacServiceAcquisition.IsMatch(code));
        }

        /// <summary>
        /// The same qualifier hole in the two import bans.
        /// </summary>
        [Theory]
        [InlineData("@using static global::SQLTriage.Data.Services.RbacService", true, false)]
        [InlineData("@using static SQLTriage.Data.Services.RbacService", true, false)]
        [InlineData("@using Rb = global::SQLTriage.Data.Services.RbacService;", false, true)]
        [InlineData("using Rb = SQLTriage.Data.Services.RbacService;", false, true)]
        [InlineData("@using Rb = SQLTriage.Data.Services.RbacService", false, true)]
        // Round 5: a verbatim `@` on the type name or the alias is the same import. Before the fix
        // these matched NEITHER pattern, so `@using Rb = NS.@RbacService` + `@inject Rb X` was
        // invisible to the import ban AND to the holder pin — the two layers this file's doc says
        // cover each other.
        [InlineData("@using static global::SQLTriage.Data.Services.@RbacService", true, false)]
        [InlineData("@using Rb = global::SQLTriage.Data.Services.@RbacService", false, true)]
        [InlineData("@using Rb = NS.@RbacService", false, true)]
        [InlineData("@using @Rb = SQLTriage.Data.Services.RbacService", false, true)]
        [InlineData("using Color = ApexCharts.Color;", false, false)]
        [InlineData("@using static SQLTriage.Data.Services.GovernanceService", false, false)]
        [InlineData("using Rb = SQLTriage.Data.Services.RbacServiceFactory;", false, false)]
        [InlineData("using Rb = SQLTriage.Data.Services.@RbacServiceFactory;", false, false)]
        public void MutationEighteen_AGlobalQualifiedImportIsCaughtInBothSpellings(
            string line, bool isStaticImport, bool isAliasImport)
        {
            Assert.Equal(isStaticImport, RbacStaticImport.IsMatch(line));
            Assert.Equal(isAliasImport, RbacAliasImport.IsMatch(line));
        }

        /// <summary>
        /// A gate hidden in a line-initial <c>/* … */</c> span in Razor MARKUP. That span is not a
        /// comment: PROVED by the compiler 2026-08-16, planting
        /// <c>@DateTime.Now.NoSuchMemberXyz</c> inside one in <c>Pages/ServerDocs.razor</c> failed the
        /// build with CS1061 at the line INSIDE the span, while the identical three lines inside that
        /// file's <c>@code</c> block built clean. The stripper used to blank both, so the compiled one
        /// was invisible to every scan here.
        ///
        /// <para>The second half of this test is the reason the fix had to be region-scoped rather
        /// than a blanket "stop blanking <c>/* … */</c>": the <c>@code</c>-block comment quoting the
        /// old gate must STAY prose. Crying at documentation is how this file grew an allowlist.</para>
        /// </summary>
        [Fact]
        public void MutationSixteen_AGateInAMarkupBlockCommentIsCaughtAndOneInAtCodeStaysProse()
        {
            const string page = @"@page ""/scratch""
@inject RbacService Rbac
@inject AppUserState UserState

/* layout note
@if (Rbac.IsAuthorized(UserState.Role, ""settings""))
{
    <AdminPanel />
}
*/

@code {
    /* historical note: RbacService.HasPermission(role, ""settings"") was the old gate. */
    private bool May => UserState.IsAuthorized(""settings"");
}";
            var offences = AuthorityBypassOffences(page);

            // Line 6 is markup that Razor compiles. Line 13 is a C# comment inside @code.
            Assert.Equal(new[] { 6 }, offences.Select(o => o.Line).ToArray());
            Assert.Equal("IsAuthorized", offences[0].Method);
            Assert.Equal(2, offences[0].ArgCount);
            Assert.Empty(OffendingLines(page));

            // And the receiver-named ban reaches into a markup span too, now that it is not blanked.
            const string staticInMarkup = @"<p>notes</p>
/* layout note
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { }
*/";
            Assert.Equal(new[] { 3 }, OffendingLines(staticInMarkup));
        }

        /// <summary>
        /// Method-group indirection, the limit round 3 documented rather than closed:
        /// <c>_gate = Rbac.IsAuthorized;</c> has no argument list for the argument-count ban to read,
        /// and the later <c>_gate(role, permission)</c> carries no banned name. Paired with the
        /// <c>global::</c> inject above it, this is the combined bypass measured green at e5ef5cc.
        /// Taking the delegate is now the offence.
        /// </summary>
        [Fact]
        public void MutationSeventeen_AMethodGroupTakenOnTheGateIsCaught()
        {
            const string page = @"@page ""/scratch""
@inject global::SQLTriage.Data.Services.RbacService Rbac
@inject AppUserState UserState

@code {
    private Func<string, string, bool> _gate = null!;
    protected override void OnInitialized() => _gate = Rbac.IsAuthorized;
    private bool May => _gate(UserState.Role, ""settings"");
}";
            var offences = AuthorityBypassOffences(page);

            Assert.Equal(new[] { 7 }, offences.Select(o => o.Line).ToArray());
            Assert.Equal("IsAuthorized", offences[0].Method);
            Assert.Equal(MethodGroupArgCount, offences[0].ArgCount);

            // The whole point of the pairing: nothing else in this file names either half.
            Assert.Empty(OffendingLines(page));
            Assert.Empty(OffendingLines(page, DirectIsAuthorizedCall));
            var blanked = BlankLineComments(BlankBlockComments(page, UiSourceKind.Razor), UiSourceKind.Razor);
            Assert.Contains(2, AcquisitionLines(blanked));   // the global:: inject, line 2
        }

        /// <summary>
        /// The other half of the method-group ban: a name inside a comment is still prose, and the
        /// sanctioned single-argument gate is still not an offence when it is CALLED.
        /// </summary>
        [Fact]
        public void MutationNineteen_ProseNamingTheGateIsNotAMethodGroupOffence()
        {
            const string page = @"@* HATCH SCOPE: plain IsAuthorized, NOT IsAuthorizedWithBreakGlass. IsAuthorized already
   grants the loopback bootstrap hatch, so break-glass would widen nothing here. *@
@inject AppUserState UserState

@if (!UserState.IsAuthorized(""settings""))
{
    <p>denied</p>
}

@code {
    /// Matched against RbacService.HasPermission.
    // Round 4: plain IsAuthorized, never a delegate to it.
    private bool MayTune => UserState.IsAuthorizedWithBreakGlass(""settings"");
}";
            Assert.Empty(AuthorityBypassOffences(page));
        }

        // ── Round 5: the siblings round 4 left open ──────────────────────────────────────────────

        /// <summary>
        /// The other three line-comment prefixes, in Razor MARKUP. Round 4 ruled that a <c>/*</c> in
        /// markup is not a comment and applied the ruling to <see cref="BlankBlockComments"/> only;
        /// <see cref="BlankLineComments"/> kept blanking any line whose trimmed start was <c>//</c>,
        /// <c>///</c> or <c>*</c>. PROVED by the compiler 2026-08-16: all three prefixes planted in
        /// the markup of <c>Pages/ServerDocs.razor</c> with an invalid member after the <c>@</c>
        /// produced <c>CS1061</c> on their own line — <c>ServerDocs.razor(15,18)</c>, <c>(16,17)</c>
        /// and <c>(17,19)</c> — so Razor compiles the line and emits the slashes as text.
        ///
        /// <para>The second half is what keeps the fix from being a blanket "stop blanking line
        /// comments": inside <c>@code</c> the same three prefixes are still prose, because crying at
        /// documentation is how this file grew an allowlist.</para>
        /// </summary>
        [Fact]
        public void MutationTwenty_AGateOnALineInitialSlashInMarkupIsCaughtAndOneInAtCodeStaysProse()
        {
            const string page = @"@page ""/scratch""
@inject RbacService Rbac
@inject AppUserState UserState

// @if (Rbac.IsAuthorized(UserState.Role, ""settings"")) { <AdminPanel /> }
* @if (RbacService.HasPermission(UserState.Role, ""settings"")) { <AdminPanel /> }
/// @if (Rbac.IsAuthorized(UserState.Role, ""reports"")) { <Reports /> }

@code {
    // Round 5: inside a C# region these three prefixes ARE comments.
    /// RbacService.HasPermission(role, ""settings"") was the old gate.
    private bool May => UserState.IsAuthorized(""settings"");
}";
            var offences = AuthorityBypassOffences(page);

            Assert.Equal(new[] { 5, 6, 7 }, offences.Select(o => o.Line).ToArray());
            Assert.Equal(new[] { "IsAuthorized", "HasPermission", "IsAuthorized" },
                offences.Select(o => o.Method).ToArray());
            Assert.All(offences, o => Assert.Equal(2, o.ArgCount));

            // The receiver-named ban reaches the markup line too, and stops at the @code comment.
            Assert.Equal(new[] { 6 }, OffendingLines(page));

            // And in a .cs file every one of those prefixes is a comment again, top to bottom.
            const string codeBehind = @"// RbacService.HasPermission(role, ""settings"") was the old gate.
/// <summary>Matched against RbacService.HasPermission.</summary>
public bool May => UserState.IsAuthorized(""settings"");";
            Assert.Empty(AuthorityBypassOffences(codeBehind, UiSourceKind.CSharp));
            Assert.Empty(OffendingLines(codeBehind, StaticHasPermissionCall, UiSourceKind.CSharp));
        }

        /// <summary>
        /// The acquisition spellings the pin claimed and did not have: the ORDINARY two-line
        /// <c>[Inject]</c>, a verbatim type name, and a generic argument split across lines. The pin
        /// read one line at a time, so none of the three could ever match. Two of them were proved in
        /// the tree the same day, compiling, with the census green.
        /// </summary>
        [Theory]
        [InlineData("    [Inject]\n    private RbacService Rbac { get; set; } = default!;", true)]
        [InlineData("    [Inject]\n    private global::SQLTriage.Data.Services.RbacService R { get; set; } = default!;", true)]
        [InlineData("    [Inject]\n    [Obsolete]\n    private RB Rbac { get; set; } = default!;", false)]
        [InlineData("        var r = sp.GetRequiredService<global::SQLTriage.Data.Services.@RbacService>();", true)]
        [InlineData("        var r = sp.GetRequiredService<@RbacService>();", true)]
        [InlineData("        var r = sp.GetService<\n            RbacService>();", true)]
        [InlineData("        var r = sp.GetService(typeof(SQLTriage.Data.Services.RbacService));", true)]
        [InlineData("@inject SQLTriage.Data.Services.@RbacService Rbac", true)]
        // The [Inject] branch must not run away past the member it is attached to.
        [InlineData("    [Inject]\n    private ExportPackRunner Runner { get; set; } = default!;\n    private RbacService Later;", false)]
        [InlineData("    [Inject]\n    private AppUserState UserState { get; set; } = default!;", false)]
        [InlineData("        var r = sp.GetRequiredService<RbacServiceFactory>();", false)]
        public void MutationTwentyOne_AnAcquisitionSplitAcrossLinesOrSpeltVerbatimIsSeen(
            string source, bool expected)
        {
            var code = BlankLineComments(BlankBlockComments(source, UiSourceKind.CSharp), UiSourceKind.CSharp);
            Assert.Equal(expected, AcquisitionLines(code).Count > 0);
        }

        /// <summary>
        /// A run-away comment span, which the round-4 doc said could no longer exist. A <c>/*</c>
        /// inside a string literal in a <c>@code</c> block was still an eligible opener, and the
        /// unbounded hunt for its <c>*/</c> reached plain markup far below. PROVED in the tree
        /// 2026-08-16 against <c>Pages/ScheduledTasks.razor</c>, with the instrument as the only
        /// variable: build 0 errors and census 59/59 exit 0 under the round-4 stripper, and named
        /// twice at <c>:232</c> under this one.
        /// </summary>
        [Fact]
        public void MutationTwentyTwo_ASlashStarInsideAStringInAtCodeDoesNotBlankTheMarkupBelowIt()
        {
            const string page = @"@page ""/scratch""
@code {
    private string _runawayNote = @""
/* opener at column zero, inside a verbatim string literal
"";
}

@if (!SQLTriage.Data.Services.RbacService.HasPermission(""Viewer"", ""settings"")) { <div></div> }
<p>aspect a*/b</p>";
            Assert.Equal(new[] { 8 }, OffendingLines(page));

            var offences = AuthorityBypassOffences(page);
            Assert.Equal(new[] { 8 }, offences.Select(o => o.Line).ToArray());
            Assert.Equal("HasPermission", offences[0].Method);
            Assert.Equal(2, offences[0].ArgCount);

            // And the fix is not "stop treating /* … */ as a comment": a real one inside @code, and a
            // real one whose */ is on its own line inside the same region, are both still prose.
            const string documented = @"@code {
    /* historical note:
       RbacService.HasPermission(role, ""settings"") was the old gate.
    */
    private bool May => UserState.IsAuthorized(""settings"");
}
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { }";
            Assert.Equal(new[] { 7 }, OffendingLines(documented));
        }

        /// <summary>
        /// The false-positive DIRECTION, pinned rather than described. Round 4 summarised the
        /// method-group ban as "prose naming the gate is still not an offence", which is true of a
        /// C# or Razor COMMENT and false of markup prose — and the list of surfaces this ban is loud
        /// on lived only in a doc paragraph, where nothing measures it. Both halves are asserted
        /// here, so an edit that quietly silences the loud half fails, and one that starts crying at
        /// real documentation fails too.
        ///
        /// <para>The loud cases are the ones a future reader will want to answer with an allowlist.
        /// The answer is to reword the sentence or move it into <c>@* … *@</c>. This file already
        /// shows where exemptions end up.</para>
        /// </summary>
        [Theory]
        // LOUD — markup is compiled, so anything written there that names the gate is an offence.
        [InlineData("<p>Ask an admin: the page calls IsAuthorized with one argument.</p>", 1)]
        [InlineData("<!-- the old gate was RbacService.HasPermission(UserState.Role, \"settings\") -->", 1)]
        [InlineData("<style>\n/* HasPermission(role, perm) was the old gate */\n</style>", 1)]
        [InlineData("@code {\n    private string N => nameof(UserState.IsAuthorized);\n}", 1)]
        // QUIET — a real comment, in either language, is prose.
        [InlineData("@* the old gate was RbacService.HasPermission(UserState.Role, \"settings\") *@", 0)]
        [InlineData("@code {\n    // RbacService.HasPermission(role, perm) was the old gate.\n}", 0)]
        [InlineData("@code {\n    /* RbacService.HasPermission(role, perm) was the old gate. */\n}", 0)]
        public void MutationTwentyThree_TheBanIsLoudOnMarkupProseAndQuietOnRealComments(
            string source, int expected)
        {
            Assert.Equal(expected, AuthorityBypassOffences(source, UiSourceKind.Razor).Count);
        }

        // ── Round 6: the opener round 5 did not touch ────────────────────────────────────────────

        /// <summary>
        /// MutationTwentyTwo's defect, spelt with the OTHER opener. Round 5 rewrote the <c>/*</c>
        /// hunt to read its C# region as C# and left <c>@*</c> a raw <c>IndexOf</c>, so one string
        /// literal in a <c>@code</c> block still opened a span that a real <c>*@</c> far below
        /// closed. PROVED in the tree 2026-08-16 with the instrument as the only variable:
        /// <c>private string _gpOpen = "@*";</c> at <c>Pages/ScheduledTasks.razor:25</c> blanked to
        /// that file's own <c>*@</c> at line 256 and hid a two-argument gate planted at 231, and its
        /// injected receiver at 230 — build 0 errors, census 84/84 green, exit 0 — and deleting only
        /// the literal line turned that census red naming both.
        /// </summary>
        [Fact]
        public void MutationTwentyFour_AnAtStarInsideAStringInAtCodeDoesNotBlankTheCodeBelowIt()
        {
            // The gate, the borrowed `*@` and the fake opener all INSIDE one @code block, so the
            // region-containment rule cannot be what saves this — the literal map is.
            const string inRegion = @"@code {
    private string _gpOpen = ""@*"";

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 4 }, OffendingLines(inRegion));

            var offences = AuthorityBypassOffences(inRegion);
            Assert.Equal(new[] { 4 }, offences.Select(o => o.Line).ToArray());
            Assert.Equal("HasPermission", offences[0].Method);
            Assert.Equal(2, offences[0].ArgCount);

            // And the tree's own geometry: the gate in MARKUP below the block, the borrowed `*@`
            // below that. Here the mask and the containment rule each close it on their own.
            const string acrossTheBlock = @"@page ""/scratch""
@code {
    private string _gpOpen = ""@*"";
}

@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { <div></div> }

@* the old gate was RbacService.HasPermission(UserState.Role, ""settings"") *@";
            Assert.Equal(new[] { 6 }, OffendingLines(acrossTheBlock));
        }

        /// <summary>
        /// The rest of the round-6 rule, in both directions, each case labelled with the half of the
        /// rule it measures. A <c>@*</c> is content — not an opener — wherever a C# region has
        /// already put it inside a literal or a <c>//</c> comment (the MASK), and an opener inside a
        /// C# region that does not close inside that region blanks nothing (CONTAINMENT). The last
        /// two cases are the false-positive direction: a REAL Razor comment is still prose, in
        /// markup and inside <c>@code</c> alike, so the fix is not "stop trusting <c>@*</c>".
        ///
        /// <para>The two mask cases keep the borrowed <c>*@</c> and the hidden gate inside the SAME
        /// <c>@code</c> block on purpose. Written the other way — gate in markup, <c>*@</c> below it
        /// — containment closes them on its own and the case measures nothing about the mask;
        /// VERIFIED 2026-08-16 by neutering the mask, at which point the first two cases fail and
        /// the third still passes.</para>
        ///
        /// <para>A character literal cannot hold this opener — <c>@*</c> is two characters — so
        /// there is no char-literal case to pin here, only the string and <c>//</c> ones.</para>
        /// </summary>
        [Fact]
        public void MutationTwentyFive_AnAtStarThatIsContentOrUnclosedInItsRegionOpensNothing()
        {
            // THE MASK. A `@*` inside a `//` comment in @code, borrowing a `*@` in the same block —
            // this is Pages/ComplianceTree.razor's shape, where containment cannot help.
            const string inLineComment = @"@code {
    // the house banner style opens with @*

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 4 }, OffendingLines(inLineComment));

            // THE MASK, on a VERBATIM string, which spans lines and escapes its quotes differently.
            const string inVerbatimString = @"@code {
    private string _template = @""
a template line that names the @* banner opener
"";

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 6 }, OffendingLines(inVerbatimString));

            // CONTAINMENT. A real opener inside @code whose `*@` is outside the block. An
            // unterminated comment in a code block is a file that does not build, so blank nothing.
            const string closedOutsideItsRegion = @"@code {
    @* opened here and never closed inside this block

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");
}

@* the close this opener would have borrowed *@";
            Assert.Equal(new[] { 4 }, OffendingLines(closedOutsideItsRegion));

            // QUIET: a real Razor comment is prose in markup …
            const string realInMarkup = @"@* the old gate was RbacService.HasPermission(UserState.Role, ""settings""),
   and IsAuthorized(role, permission) was its fail-open twin. *@
@if (!UserState.IsAuthorized(""settings"")) { }";
            Assert.Empty(OffendingLines(realInMarkup));
            Assert.Empty(AuthorityBypassOffences(realInMarkup));

            // … and prose inside a C# region too, which is the direction this fix must not break.
            const string realInAtCode = @"@code {
    @* the old gate was RbacService.HasPermission(role, ""settings"") *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Empty(OffendingLines(realInAtCode));
            Assert.Empty(AuthorityBypassOffences(realInAtCode));
        }

        /// <summary>
        /// The bypass the round-6 rule still had when it was first written, found by exercising it
        /// rather than by reading it. <see cref="SkipStringLiteral"/> stopped at the first unescaped
        /// <c>"</c>, so an INTERPOLATION HOLE containing a nested literal —
        /// <c>$"{string.Join(", ", xs)}"</c>, of which the shipped tree holds 52 — came back as two
        /// literals with a gap between them, and a <c>@*</c> parked in that gap was an eligible
        /// opener again. PROVED in the tree 2026-08-16 with the instrument as the only variable:
        /// planted at <c>Pages/ComplianceTree.razor:202</c>, inside a <c>@code</c> block whose own
        /// real <c>*@</c> at line 429 is INSIDE the same block — so the region-containment rule
        /// could not save it — it hid a static two-argument <c>HasPermission</c> at line 391 from
        /// both bans, with the build at 0 errors. Named at <c>:391</c> by both bans once the skip
        /// understood holes.
        ///
        /// <para>Both loud cases below keep the tree's geometry, because a synthetic that loses it
        /// has no teeth: the hidden gate and the real comment whose <c>*@</c> gets borrowed both sit
        /// INSIDE the same <c>@code</c> block, so the region-containment rule cannot be what saves
        /// them. The first is the <c>string.Join</c> shape; the second is the escaped-quote spelling
        /// the tree actually carries (<c>Pages/DbaTools.razor:478</c>), where a nested
        /// <c>Replace("\"", …)</c> lives inside the hole. The third is the direction the fix must
        /// not break: a real Razor comment quoting an interpolated string is still prose. VERIFIED
        /// discriminating 2026-08-16 — with hole awareness neutered and nothing else changed, the
        /// first two cases fail.</para>
        /// </summary>
        [Fact]
        public void MutationTwentySix_AnAtStarInAnInterpolationHoleOpensNothing()
        {
            const string inHole = @"@code {
    private string _gpHole = $""{string.Join("", "", new[]{ ""@*"" })}"";

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 4 }, OffendingLines(inHole));

            const string escapedQuotes = @"@code {
    private string _csv = $""\""{Row.Replace(""x"", ""@*"")}\"""";

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 4 }, OffendingLines(escapedQuotes));

            // QUIET: a real Razor comment that happens to quote an interpolated string is prose.
            const string prose = @"@code {
    @* the label used to read $""{Role} may {RbacService.HasPermission(role, ""x"")}"" *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Empty(OffendingLines(prose));
        }

        // ── Round 7: the three ways an `@*` is not an opener that rounds 5 and 6 did not ask ──────

        /// <summary>
        /// Razor resolves its own <c>@@</c> ESCAPE before it looks for a transition, and neither
        /// round 5 nor round 6 taught either opener that. In a run of N consecutive <c>@</c> ending
        /// at a matched <c>@*</c>, an EVEN N is entirely consumed by escape pairs and the <c>*</c> is
        /// text; an ODD N leaves the last <c>@</c> free to open a comment.
        ///
        /// <para>PROVED BY THE COMPILER 2026-08-16, one <c>@</c> apart, same two lines, same file,
        /// same position: <c>@@* …</c> above <c>@DateTime.Now.NoSuchMemberXyz</c> planted at
        /// <c>Pages/ScheduledTasks.razor:228</c> failed the build with
        /// <c>error CS1061 … 'DateTime' does not contain a definition for 'NoSuchMemberXyz'</c> at
        /// <c>ScheduledTasks.razor(229,15)</c> — that line was LIVE CODE — while the same plant
        /// spelt <c>@* …</c> built with 0 errors, because that one really is a comment.</para>
        ///
        /// <para>PROVED IN THE TREE the same day, instrument as the only variable: <c>@@*</c> at
        /// <c>ScheduledTasks.razor:229</c> with an <c>@inject … RbacService RbacHole</c> at 230 and
        /// the fail-open <c>RbacHole.IsAuthorized(UserState.Role, "settings")</c> it feeds at 231,
        /// borrowing that file's real <c>*@</c> at 255. Build 0 errors; census 88/88 green exit 0
        /// under 20a0384; under this rule three tests name it — <c>:231</c> by the argument-count
        /// ban, <c>:230</c> by the acquisition pin, and the file by
        /// <see cref="NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot"/>. The 20a0384 run
        /// stayed green with ONE extra planted line, <c>private string _bal = "*@";</c> at line 25,
        /// which balanced the delimiter counts that pin was relying on.</para>
        /// </summary>
        [Fact]
        public void MutationTwentySeven_AnEscapedAtAtStarOpensNothing()
        {
            // ESCAPED, even run: `@@*` is a literal `@` then a `*`, so the gate below is live markup.
            const string escaped = @"@page ""/scratch""
@@* PROBE: Razor escapes this pair, so nothing opens here
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { <div></div> }
@* a real comment *@";
            Assert.Equal(new[] { 3 }, OffendingLines(escaped));

            // ODD run: one more `@`, and the last one is free to transition. This DOES open.
            const string oddRun = @"@page ""/scratch""
@@@* PROBE: three @, so the last one opens a comment
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { <div></div> }
*@";
            Assert.Empty(OffendingLines(oddRun));

            // The plain spelling still opens, which is the direction this fix must not break.
            const string plain = @"@page ""/scratch""
@* PROBE: one @, an ordinary comment
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { <div></div> }
*@";
            Assert.Empty(OffendingLines(plain));
        }

        /// <summary>
        /// The literal map's SCOPE, which round 6 borrowed from the blanking scope and should not
        /// have. Rounds 4 and 5 ruled that a comment in a <c>@{ … }</c> markup block must NOT be
        /// blanked, and that ruling stands — but "where C# comments are blanked" and "where C#
        /// literals live" are opposite questions: blanking too much is silent, masking too little is
        /// silent too, and they are not the same set. Round 7 splits them
        /// (<see cref="CSharpLiteralRegions"/>).
        ///
        /// <para>PROVED IN THE TREE 2026-08-16, instrument as the only variable:
        /// <c>@{ var _probe = "@*"; }</c> planted in <c>Pages/ScheduledTasks.razor</c>'s markup at
        /// line 229, with <c>@inject … RbacService RbacHole</c> at 230 and the fail-open
        /// <c>RbacHole.IsAuthorized(UserState.Role, "settings")</c> at 231, borrowing that file's
        /// real <c>*@</c> at 255. Build 0 errors, census 88/88 green exit 0 under 20a0384 (one
        /// balancing <c>*@</c> planted in a <c>//</c> comment at line 25 kept the delimiter counts
        /// even); under this rule the same plant is named at <c>:231</c>, <c>:230</c> and
        /// <c>:229</c>.</para>
        ///
        /// <para>The last two cases are the ruling that must survive the widening: the BLANKING
        /// scope is unchanged, so a real C# <c>/* … */</c> comment in a <c>@{ … }</c> block is still
        /// a loud false positive rather than a blanked span, and a real Razor <c>@* … *@</c> there
        /// is still prose. An edit that widens <see cref="CSharpRegions"/> instead of
        /// <see cref="CSharpLiteralRegions"/> passes the first three cases and fails the
        /// fourth.</para>
        /// </summary>
        [Fact]
        public void MutationTwentyEight_AnAtStarInsideAnInlineCodeBlockLiteralOpensNothing()
        {
            // A `@{ … }` markup code block: C#, and not a region this census blanks in.
            const string inMarkupBlock = @"@page ""/scratch""
@{ var probe = ""@*""; }
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { <div></div> }
@* a real comment *@";
            Assert.Equal(new[] { 3 }, OffendingLines(inMarkupBlock));

            // An `@if` body: C# statements with markup interleaved.
            const string inIfBlock = @"@page ""/scratch""
@if (DateTime.Now.Ticks > 0)
{
    var probe = ""@*"";
    <div>x</div>
}
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { <div></div> }
@* a real comment *@";
            Assert.Equal(new[] { 7 }, OffendingLines(inIfBlock));

            // An `else` body, which carries no `@` of its own and is reached by continuation.
            const string inElseBlock = @"@page ""/scratch""
@if (DateTime.Now.Ticks > 0)
{
    <div>x</div>
}
else
{
    var probe = ""@*"";
}
@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { <div></div> }
@* a real comment *@";
            Assert.Equal(new[] { 10 }, OffendingLines(inElseBlock));

            // THE RULING ROUNDS 4-5 MADE, still standing: the blanking scope did NOT widen, so a
            // real C# comment in a `@{ … }` block is read as markup and cries loudly.
            const string cSharpCommentInMarkupBlock = @"@page ""/scratch""
@{
    /* RbacService.HasPermission(role, perm) was the old gate. */
}";
            Assert.Single(AuthorityBypassOffences(cSharpCommentInMarkupBlock));

            // And a real Razor comment in one is still prose, in both directions.
            const string razorCommentInMarkupBlock = @"@page ""/scratch""
@{
    @* the old gate was RbacService.HasPermission(role, ""settings"") *@
    var x = 1;
}";
            Assert.Empty(OffendingLines(razorCommentInMarkupBlock));
            Assert.Empty(AuthorityBypassOffences(razorCommentInMarkupBlock));
        }

        /// <summary>
        /// The raw string literal, which round 6 NAMED in <see cref="CSharpLiteralMask"/>'s doc and
        /// filed under the loud direction. It is the silent one: a multi-line
        /// <c>"""…"""</c> came back as an empty literal <c>""</c> followed by an ordinary one that
        /// stopped at its line, so the fenced content was an unmasked GAP — the round-6
        /// interpolation-hole shape, one literal kind further on.
        ///
        /// <para>PROVED IN THE TREE 2026-08-16, instrument as the only variable: a three-line raw
        /// string holding <c>@*</c> planted in <c>Pages/ComplianceTree.razor</c>'s <c>@code</c>
        /// block at lines 203-205, borrowing that block's own real <c>*@</c> at 428 — so
        /// region containment could not save it — hid a static two-argument
        /// <c>SQLTriage.Data.Services.RbacService.HasPermission("Viewer", "settings")</c> planted at
        /// line 395. Build 0 errors, census 88/88 green exit 0 under 20a0384 (one balancing
        /// <c>*@</c> in a <c>//</c> comment kept its delimiter counts even); under this rule
        /// <c>:395</c> is named by the argument-count ban AND the receiver-named static ban, and
        /// <c>:204</c> by <see cref="NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot"/>.</para>
        ///
        /// <para>MEASURED 2026-08-16: 0 raw string literals in the 161 shipped UI files, so this
        /// closes a hole nobody had yet dug. The third case is the boundary that makes the fix safe
        /// to have: <c>@"""x"" @* y"</c> is a VERBATIM literal three quotes wide at its opener, not a
        /// raw one, and reading it as raw runs the mask to end of file — which withdraws the
        /// <c>@code</c> region in <see cref="MatchingBrace"/> and hides the gate below it.</para>
        /// </summary>
        [Fact]
        public void MutationTwentyNine_AnAtStarInARawStringLiteralOpensNothing()
        {
            // A multi-line raw string: the fence is the only thing that ends it.
            const string multiLineRaw = @"@code {
    private string _raw = """"""
        a template line that names the @* banner opener
        """""";

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 6 }, OffendingLines(multiLineRaw));

            // A single-line raw string, where the closing fence is on the same line.
            const string singleLineRaw = @"@code {
    private string _raw = """"""a line naming the @* banner opener"""""";

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 4 }, OffendingLines(singleLineRaw));

            // NOT a raw string: `@""` … `""` is verbatim, however many quotes it opens with.
            const string verbatimNotRaw = @"@code {
    private string _v = @""""""x"""" @* y"";

    private bool _hole => RbacService.HasPermission(UserState.Role, ""settings"");

    @* a real comment inside this same block *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Equal(new[] { 4 }, OffendingLines(verbatimNotRaw));

            // QUIET: a real Razor comment quoting a raw string is prose.
            const string prose = @"@code {
    @* the label used to read """"""{Role} may {RbacService.HasPermission(role, ""x"")}"""""" *@
    private bool May => UserState.IsAuthorized(""settings"");
}";
            Assert.Empty(OffendingLines(prose));
        }

        // ── Presence over the shipped tree, not absence ──────────────────
        //
        // Every ban above asserts an ABSENCE, which is also what a blind scan returns. These two
        // assert a PRESENCE against the real files: plant the call and require the scan to name it.

        /// <summary>
        /// Directly measures the failure mode that shipped: a comment span running to end of file. At
        /// 7df4b82 this named <c>Pages/Settings.razor</c> — 1 file of 161 was blind from line 1502
        /// down.
        ///
        /// <para><b>Widened 2026-08-16 (round 4), and renamed for what it now covers.</b> It used to
        /// guard the two receiver-named regexes only, and both scans written on 2026-08-15 —
        /// <see cref="AuthorityBypassOffences"/> (the argument-count ban with teeth) and
        /// <see cref="RbacServiceAcquisition"/> (the holder pin) — read the SAME comment stripper
        /// through the same blanking and had no tail guard of their own. A stripper regression would
        /// therefore have been caught for the two scans that are named pins on a historical spelling
        /// and NOT for the two that do the work. Every scan the census owns is probed here now, each
        /// on its own planted line, per file, in that file's own language.</para>
        /// </summary>
        [Fact]
        public void EveryScanSeesEveryShippedFileAllTheWayToItsLastLine()
        {
            var root = RawPassedScan.RepoRoot();
            var blind = new List<string>();

            const string injectProbe = @"@inject SQLTriage.Data.Services.RbacService Rbac";
            const string gateProbe = @"@if (!RbacService.HasPermission(UserState.Role, ""settings"")"
                                   + @" || !RbacService.IsAuthorized(UserState.Role, ""settings"")) { }";
            const string groupProbe = @"private Func<string, string, bool> _gate = Rbac.IsAuthorized;";

            foreach (var file in ShippedUiFiles())
            {
                var source = File.ReadAllText(file.FullName).TrimEnd('\r', '\n')
                    + "\n" + injectProbe + "\n" + gateProbe + "\n" + groupProbe;

                var kind = KindOf(file);
                var lines = source.Split('\n');
                var groupLine = lines.Length;
                var gateLine = groupLine - 1;
                var injectLine = groupLine - 2;
                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                if (!OffendingLines(source, StaticHasPermissionCall, kind).Contains(gateLine))
                    blind.Add(relative + " (receiver-named HasPermission)");
                if (!OffendingLines(source, DirectIsAuthorizedCall, kind).Contains(gateLine))
                    blind.Add(relative + " (receiver-named IsAuthorized)");

                var offences = AuthorityBypassOffences(source, kind);
                if (!offences.Any(o => o.Line == gateLine && o.Method == "HasPermission" && o.ArgCount == 2))
                    blind.Add(relative + " (argument-count ban, HasPermission)");
                if (!offences.Any(o => o.Line == gateLine && o.Method == "IsAuthorized" && o.ArgCount == 2))
                    blind.Add(relative + " (argument-count ban, IsAuthorized)");
                if (!offences.Any(o => o.Line == groupLine && o.ArgCount == MethodGroupArgCount))
                    blind.Add(relative + " (method-group ban)");

                var blanked = BlankLineComments(BlankBlockComments(source, kind), kind);
                if (!AcquisitionLines(blanked).Contains(injectLine))
                    blind.Add(relative + " (RbacService acquisition pin)");
            }

            Assert.True(ShippedUiFiles().Count > 100, "implausibly few shipped UI files — the probe is measuring nothing.");
            Assert.True(
                blind.Count == 0,
                "the comment stripper has blinded a census scan over the tail of these files, so a "
                + "gate written there would never be reported:\n  " + string.Join("\n  ", blind));
        }

        /// <summary>
        /// What the <c>@*</c> rule COSTS on the tree it shipped against, re-measured every run rather
        /// than asserted in a doc paragraph. Two claims:
        ///
        /// <para>(1) every shipped file's unescaped <c>@*</c> and <c>*@</c> counts are equal —
        /// MEASURED 2026-08-16: 644 and 644 over 161 files, 0 files unbalanced — so no file today
        /// carries a stray delimiter of either kind; and (2) every unescaped <c>@*</c> occurrence in
        /// the tree is an opener the rule accepts: not literal content, closed, and closed inside its
        /// own C# region when it opened in one.</para>
        ///
        /// <para><b>This pin was itself defeated, 2026-08-16 (round 7).</b> Claim (1) counted
        /// <c>@*</c> as raw text, so an ESCAPED <c>@@*</c> — which opens nothing in Razor, and which
        /// the round-6 rule wrongly read as an opener — counted as a delimiter here. The gate proved
        /// it by planting one <c>@@*</c> and one balancing literal <c>*@</c>: the counts stayed
        /// equal, this pin stayed green, and the round-6 rule blanked live compiled code between
        /// them. A backstop that reads the tree through a DIFFERENT rule from the one it is
        /// backstopping is not a backstop, so both now ask <see cref="AtStarIsEscaped"/>, and the
        /// walk below reads the same widened literal scope (<see cref="CSharpLiteralRegions"/>) the
        /// stripper does. What that costs is honest to state: this pin can no longer catch a defect
        /// IN the escape rule or IN the mask scope — only the mutation tests and a planted probe can
        /// do that, which is what round 7 used.</para>
        ///
        /// <para>The whole cost of round 6 was measured directly as well, once, off a throwaway
        /// harness holding a verbatim copy of the ENTIRE d85a401 stack — stripper, eligible spans,
        /// regions, brace walk and literal skips, nothing shared with the new one. Over all 161
        /// shipped files the two produce BYTE-IDENTICAL blanked output, 0 files differing, which
        /// covers the shared-map refactor of the <c>/*</c> side and the interpolation fix in
        /// <see cref="SkipStringLiteral"/> as well as the <c>@*</c> rule. One C# REGION moved:
        /// <c>Pages/DbaTools.razor</c>'s single <c>@code</c> block ended at character 19883 under
        /// round 5 and ends at 20011 under round 6, of a 20013-character file. The round-5 brace
        /// walk lost its place on the nested literal in
        /// <c>$"\"{row[c]?.ToString()?.Replace("\"", "\"\"")}\""</c> at line 478, closed the region
        /// eight lines early at line 492, and so read this file's whole <c>Dispose()</c> method as
        /// MARKUP. Nothing in those eight lines is a comment, which is why the blanking is
        /// unchanged, and reading C# as markup is the loud direction — but it was wrong. This is a
        /// one-off measurement and not re-run here; what IS re-run is the two claims above, which
        /// are the conditions under which the <c>@*</c> half can stop holding.</para>
        ///
        /// <para>A red here is not automatically a defect. It means a shipped file has started
        /// exercising the round-6 rule, so that file's comment reading changed direction — always
        /// towards blanking less, which is loud. Read the named line, satisfy yourself the stripper
        /// is right about it, and update this pin deliberately.</para>
        /// </summary>
        [Fact]
        public void NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot()
        {
            var root = RawPassedScan.RepoRoot();
            var suspect = new List<string>();
            var openers = 0;

            foreach (var file in ShippedUiFiles())
            {
                var source = File.ReadAllText(file.FullName);
                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                // The `@@` escape is counted the same way here as the rule counts it (round 7): an
                // escaped `@@*` is not a delimiter, so it must not balance a real `*@` either. That
                // was the hole — this pin's own backstop was defeated by planting one of each.
                var opens = Regex.Matches(source, @"@\*").Count(m => !AtStarIsEscaped(source, m.Index));
                var closes = Regex.Matches(source, @"\*@").Count;
                if (opens != closes)
                    suspect.Add(relative + " holds " + opens + " @* and " + closes + " *@");

                var regions = CSharpRegions(source, KindOf(file));
                var masked = CSharpLiteralMask(source, CSharpLiteralRegions(source, KindOf(file)));

                for (int i = source.IndexOf("@*", StringComparison.Ordinal);
                     i >= 0;
                     i = source.IndexOf("@*", i + 2, StringComparison.Ordinal))
                {
                    if (AtStarIsEscaped(source, i)) continue;    // `@@` escape: not a delimiter

                    openers++;
                    var at = relative + ":" + LineOf(source, i);

                    if (masked[i]) { suspect.Add(at + " — @* is literal content, not an opener"); continue; }

                    var close = source.IndexOf("*@", i + 2, StringComparison.Ordinal);
                    if (close < 0) { suspect.Add(at + " — @* is never closed"); continue; }

                    foreach (var region in regions)
                        if (i >= region.Start && i < region.End && close + 2 > region.End)
                            suspect.Add(at + " — @* opens in a @code block and closes outside it");

                    i = close;                                  // the loop's +2 lands past the `*@`
                }
            }

            Assert.True(openers > 100, "implausibly few Razor comments found — the probe is measuring nothing.");
            Assert.True(
                suspect.Count == 0,
                "a shipped file holds a `@*` the round-6 rule reads differently from a raw text "
                + "search. That is the loud direction — the span stops being blanked — but it means "
                + "this pin's measurement is stale and the file wants reading:\n  "
                + string.Join("\n  ", suspect));
        }

        /// <summary>
        /// The C# regions <see cref="CSharpRegions"/> finds in a shipped <c>.razor</c> file all close.
        /// A block whose brace never matches contributes no region, which is fail-loud rather than
        /// fail-silent — but it also means the stripper stops recognising real C# comments in that
        /// file, so the fallback should never be the operating mode. This measures that it is not.
        /// </summary>
        [Fact]
        public void EveryShippedRazorCodeBlockIsBraceMatched()
        {
            var root = RawPassedScan.RepoRoot();
            var unmatched = new List<string>();
            var blocks = 0;

            foreach (var file in ShippedUiFiles().Where(f => KindOf(f) == UiSourceKind.Razor))
            {
                var source = File.ReadAllText(file.FullName);
                var openers = RazorCodeBlock.Matches(source).Count;
                var regions = CSharpRegions(source, UiSourceKind.Razor);
                blocks += openers;

                if (regions.Count != openers)
                    unmatched.Add(Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/')
                        + " (" + openers + " @code/@functions openers, " + regions.Count + " matched)");
            }

            Assert.True(blocks > 100, "implausibly few @code blocks found — the probe is measuring nothing.");
            Assert.True(
                unmatched.Count == 0,
                "a @code/@functions block's brace does not match, so the census no longer treats its "
                + "C# comments as comments. That is loud rather than silent, but it means the brace "
                + "walk in MatchingBrace has met a construct it cannot read — fix the walk, do not "
                + "widen the region:\n  " + string.Join("\n  ", unmatched));
        }

        [Theory]
        [InlineData("Pages/Settings.razor", "<code>Config/*.json</code>", 200)]
        [InlineData("Components/Shared/QueryPlanModal.razor", "body.Contains(\"/*\")", 7)]
        public void TheScanSeesPastTheSlashStarsThatOnceBlindedIt(string relative, string marker, int linesBelow)
        {
            // The two real constructs, pinned in the real files rather than in a synthetic copy, so
            // this stays true about the tree and not just about a string in this test.
            var root = RawPassedScan.RepoRoot();
            var path = Path.Combine(root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), relative + " is gone; re-point this pin or delete it deliberately.");

            var lines = File.ReadAllText(path).Split('\n').ToList();
            var markerLine = lines.FindIndex(l => l.Contains(marker, StringComparison.Ordinal)) + 1;
            Assert.True(
                markerLine > 0,
                "the construct this pin exists for has left " + relative + " (" + marker + "). That is "
                + "not a defect, but this test now measures nothing — re-point it at whatever the tree's "
                + "current mid-line /* looks like, or delete it deliberately.");

            var plantLine = Math.Min(markerLine + linesBelow, lines.Count);
            Assert.True(plantLine > markerLine, "the plant must land BELOW the marker to measure anything.");
            lines.Insert(plantLine - 1, @"@if (!RbacService.HasPermission(UserState.Role, ""settings"")) { }");

            Assert.Contains(plantLine, OffendingLines(string.Join("\n", lines)));
        }

        [Fact]
        public void TheScanActuallyReachesTheShippedTree()
        {
            // A scan pointed at nothing returns "no offenders" and looks identical to a clean tree.
            // Assert it opened real files: every shipped page carries the invocation banner.
            var root = RawPassedScan.RepoRoot();
            var pages = new DirectoryInfo(Path.Combine(root.FullName, "Pages"));

            Assert.True(pages.Exists, "Pages/ not found from RepoRoot() — the scan is looking at nothing.");
            Assert.True(
                pages.EnumerateFiles("*.razor", SearchOption.AllDirectories).Count() > 50,
                "the scan root holds implausibly few pages; RepoRoot() is probably resolving to a build output.");
        }

        /// <summary>
        /// Replaces the interior of Razor <c>@* … *@</c> and C# <c>/* … */</c> spans with spaces,
        /// preserving newlines so every line keeps its original number.
        ///
        /// <para><b>Fail-safe, rewritten 2026-08-15 (round 2).</b> The first version treated ANY
        /// <c>/*</c> as a C# block-comment opener and, when it found no <c>*/</c>, blanked to end of
        /// file. Its own doc comment called that "deliberately naive … which can only ever HIDE a
        /// call", and offered the mutation tests as the answer. It was not measured against the tree,
        /// and the tree already contained two such openers — so the instrument went blind over 404
        /// lines of shipped UI on the day it shipped, which is WORSE than the per-line scan it
        /// replaced:</para>
        /// <list type="number">
        ///   <item><description><c>Pages/Settings.razor:1502</c> — the markup text
        ///   <c>&lt;code&gt;Config/*.json&lt;/code&gt;</c>. No <c>*/</c> follows it anywhere, so the
        ///   span ran to EOF and blanked line 1502 down, 373 lines of the app's largest settings
        ///   surface.</description></item>
        ///   <item><description><c>Components/Shared/QueryPlanModal.razor:263</c> —
        ///   <c>body.Contains("/*")</c> inside the index-DDL safety check. Closed 30 lines later by
        ///   <c>catch { /* summary may not exist for this plan */ }</c>, blanking 263-293 including
        ///   the <c>await ExecuteSingleIndex(ddl)</c> write path.</description></item>
        /// </list>
        /// <para>MEASURED at 7df4b82: planting the identical gate call at QueryPlanModal:276 and :503
        /// named only :503; planting it at Settings:101 and :1802 named only :101. Both fail SILENTLY
        /// GREEN, because every assertion this census makes is an ABSENCE.</para>
        ///
        /// <para><b>The rule now, and why it is structural rather than careful.</b> A span is only
        /// opened by something that cannot plausibly be anything else, and an opener that does not
        /// close blanks NOTHING:</para>
        /// <list type="bullet">
        ///   <item><description><c>@*</c> opens a Razor comment where the <c>@</c> is a transition
        ///   Razor actually reads. It is NOT one when it is the second half of a <c>@@</c> escape
        ///   (round 7: <c>@@*</c> is inert, <c>@@@*</c> opens — <see cref="AtStarIsEscaped"/>), and
        ///   it is not one where those two characters are already literal content: inside a string
        ///   literal, a character literal or a <c>//</c> comment, anywhere
        ///   <see cref="CSharpLiteralRegions"/> knows C# lives — which since round 7 is the
        ///   <c>@code</c> bodies AND the inline <c>@{ … }</c>, <c>@if</c>, <c>@for</c>,
        ///   <c>@foreach</c> bodies, a wider list than the regions this method blanks against. It is
        ///   closed by the next <c>*@</c>, which must exist, and which must be inside the same
        ///   <see cref="CSharpRegions"/> region when the opener was inside one. Otherwise: blank
        ///   nothing. Every one of those conditions REMOVES an opener; none adds
        ///   one.</description></item>
        ///   <item><description><c>/*</c> opens a C# block comment only when it is the first thing on
        ///   its line (a whole-line block comment, which may run for many lines) or when its
        ///   <c>*/</c> is on that same line (the <c>catch { /* … */ }</c> shape). A <c>/*</c> that is
        ///   mid-line AND unclosed on that line — markup like <c>Config/*.json</c>, a
        ///   <c>Contains("/*")</c> literal, a path, a regex — opens nothing.</description></item>
        ///   <item><description>Whichever eligible opener comes first owns the span, in ONE
        ///   left-to-right pass, so a <c>/*</c> inside a Razor comment cannot open a second one
        ///   (MutationSix).</description></item>
        /// </list>
        /// <para><b>The run-away class, corrected 2026-08-16 (round 5).</b> This paragraph used to
        /// read "this closes the run-away class by construction … every remaining error is
        /// under-blanking, which surfaces as a LOUD false positive on a prose line, not as silence."
        /// That was false while it was written. The opener hunt was a text search, so a <c>/*</c>
        /// inside a STRING LITERAL in a <c>@code</c> block was still eligible, and the closing
        /// <c>*/</c> was found with an unbounded search that could land in markup far below. PROVED
        /// in the tree 2026-08-16 against <c>Pages/ScheduledTasks.razor</c> — the very file this doc
        /// cites for its two-<c>@code</c>-block shape — a three-line verbatim string opened at line
        /// 22 of the first block blanked a static two-argument gate at line 232; build 0 errors,
        /// census 59/59, exit 0. With the walk below and nothing else changed, that same plant is
        /// named twice, by the argument-count ban and by the static ban, both at
        /// <c>Pages/ScheduledTasks.razor:232</c>.
        /// The claim that mattered was the one about the FAILURE DIRECTION, and it was wrong
        /// in the silent direction. <see cref="EligibleCSharpCommentSpans"/> now reads each region as
        /// C# — skipping literals and <c>//</c> comments — and requires the <c>*/</c> to fall inside
        /// the region that made the opener eligible. Pinned by MutationTwentyTwo. MEASURED over the
        /// shipped tree: 161 files, 0 offenders, and a call planted on the last line of all 161 files
        /// is named in all 161 — one of which, Settings.razor, was blind before. The last of those is
        /// <see cref="EveryScanSeesEveryShippedFileAllTheWayToItsLastLine"/>, so it is re-measured on
        /// every run rather than resting on this sentence. What round 5 wrote NEXT to that fix — "the
        /// only multi-line spans that exist are a closed <c>@* … *@</c> and a line-initial
        /// <c>/* … */</c> opened and closed within one C# region" — was false about its first half on
        /// the day it was written, for the reason the next paragraph gives.</para>
        ///
        /// <para><b>The same hole on the OTHER opener, closed 2026-08-16 (round 6).</b> Round 5
        /// rewrote the <c>/*</c> hunt and left <c>@*</c> byte-for-byte as it was: a raw
        /// <c>source.IndexOf("@*", i)</c> over the whole file with no region and no literal
        /// awareness, closed by an unbounded <c>source.IndexOf("*@")</c>. So the round-5 silence was
        /// still spellable one character differently, and the bullet above saying an unclosed
        /// <c>@*</c> blanks nothing described the only half of that opener's rule that held. PROVED
        /// in the tree 2026-08-16 with the instrument as the only variable. ONE line —
        /// <c>private string _gpOpen = "@*";</c> — planted inside the first <c>@code</c> block of
        /// <c>Pages/ScheduledTasks.razor</c> at line 25 borrowed that file's own real <c>*@</c> at
        /// line 256 and blanked lines 25-256. Planted among them, a mid-file
        /// <c>@inject SQLTriage.Data.Services.RbacService RbacHole</c> at line 230 and the
        /// fail-open gate it feeds, <c>@if (!RbacHole.IsAuthorized(UserState.Role, "settings"))</c>,
        /// at line 231: build 0 errors, census 84/84 green, exit 0, under the round-5 stripper.
        /// Deleting ONLY the string-literal line and changing nothing else turned that same census
        /// red — <c>:230 [IsAuthorized, 2 args]</c> and the acquisition at <c>:229</c> — which is
        /// what makes the literal the cause rather than a coincidence. Under this stripper, with the
        /// literal line still in place, the plant is named at <c>:231</c> by the argument-count ban,
        /// at <c>:230</c> by the acquisition pin, and at <c>:25</c> by
        /// <see cref="NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot"/>.</para>
        ///
        /// <para>Proved a second time on a different file, a different literal, and a different ban:
        /// <c>// the house banner style opens with @*</c> planted in
        /// <c>Pages/ComplianceTree.razor</c>'s <c>@code</c> block at line 202 borrowed the real
        /// <c>*@</c> at line 429 — a Razor comment that lives INSIDE that code block — and hid a
        /// static <c>SQLTriage.Data.Services.RbacService.HasPermission("Viewer", "settings")</c> at
        /// line 391. Build 0 errors, census 84/84 green, exit 0 under round 5; named at <c>:391</c>
        /// by both the argument-count ban and the receiver-named static ban under this one.</para>
        ///
        /// <para>Both openers are now judged against one shared literal map
        /// (<see cref="CSharpLiteralMask"/>) instead of two private copies of the skips, which is the
        /// structural half of the fix: the reason round 5 reached one opener and not the other is
        /// that there were two walks to remember. Pinned by MutationTwentyFour and
        /// MutationTwentyFive.</para>
        ///
        /// <para><b>Region-scoped 2026-08-16 (round 4): a <c>/*</c> in Razor MARKUP is not a
        /// comment.</b> The round-2 rule above blanked a line-initial <c>/* … */</c> span wherever it
        /// stood in the file. In a <c>.razor</c> file, markup is not C#: <c>/*</c> and <c>*/</c> are
        /// literal characters that Razor emits, and every <c>@</c> transition BETWEEN them is
        /// compiled and run. PROVED 2026-08-16 by compiler, both directions, against the real tree:
        /// planting</para>
        /// <code>
        /// /* PROBE-A markup span
        /// @DateTime.Now.NoSuchMemberXyz
        /// */
        /// </code>
        /// <para>in the markup of <c>Pages/ServerDocs.razor</c> failed the build with
        /// <c>error CS1061 … 'DateTime' does not contain a definition for 'NoSuchMemberXyz'</c> at
        /// <c>ServerDocs.razor(16,15)</c> — the line INSIDE the span — while the same three lines
        /// placed inside that file's <c>@code { … }</c> block built with 0 errors, which is what a
        /// comment does. So the span the stripper was blanking as prose was live compiled code, and a
        /// fail-open gate written there would have been invisible to every scan in this file while
        /// still deciding what the page rendered.</para>
        ///
        /// <para>The rule is now: <c>/*</c> opens a C# block comment only inside a C# REGION — the
        /// whole file for <c>.cs</c>, and the brace-matched body of each <c>@code { … }</c> or
        /// <c>@functions { … }</c> block for <c>.razor</c> (see <see cref="CSharpRegions"/>). The
        /// line-initial / closed-on-its-own-line rule still applies WITHIN a region, because that is
        /// what keeps <c>ddl.Contains("/*")</c> from opening one (MutationEight). Not recognised as a
        /// region: <c>@{ … }</c> markup code blocks, where a real C# comment would therefore raise a
        /// loud false positive rather than a silence — MEASURED 2026-08-16, the 44 <c>@{ … }</c>
        /// blocks in shipped UI contain 0 <c>/* … */</c> spans between them.</para>
        ///
        /// <para>MEASURED 2026-08-16, the whole cost of the change over <c>.razor</c> files: 165 spans
        /// stop being blanked, 135 of them the <c>&lt;!--/* basmalah */--&gt;</c> banner line and the
        /// other 30 CSS comments inside <c>&lt;style&gt;</c> blocks. Not one contains a banned token,
        /// so the census is still silent on the tree — the assertions above are green — and a CSS or
        /// markup comment that quotes a banned call in future will be a loud false positive, which is
        /// the direction this file chooses.</para>
        ///
        /// <para>The default overload reads its argument as RAZOR, which is the fail-loud direction
        /// (fewer blanks, never more) and keeps every synthetic snippet in the mutation tests below
        /// meaning what it says.</para>
        /// </summary>
        internal static string BlankBlockComments(string source) =>
            BlankBlockComments(source, UiSourceKind.Razor);

        internal static string BlankBlockComments(string source, UiSourceKind kind)
        {
            var buffer = source.ToCharArray();
            var regions = CSharpRegions(source, kind);
            var masked = CSharpLiteralMask(source, CSharpLiteralRegions(source, kind));

            var spans = EligibleRazorCommentSpans(source, regions, masked);
            spans.AddRange(EligibleCSharpCommentSpans(source, regions, masked));
            spans.Sort();

            var i = 0;
            foreach (var (start, stop) in spans)
            {
                // ONE left-to-right pass: whichever eligible opener came first owns the span, so an
                // opener of either language sitting INSIDE an earlier span opens nothing.
                if (start < i) continue;

                for (int j = start; j < stop; j++)
                    if (buffer[j] != '\n' && buffer[j] != '\r') buffer[j] = ' ';

                i = stop;
            }

            return new string(buffer);
        }

        /// <summary>
        /// The one literal-aware read of <paramref name="source"/> that both comment openers are
        /// judged against: <c>true</c> at every character sitting inside a C# string literal
        /// (verbatim and interpolated included), a character literal, or a <c>//</c> comment, INSIDE
        /// one of <paramref name="cSharpRegions"/>. Outside a C# region every position is
        /// <c>false</c>, because Razor markup has no literals — a quote there is a quote character.
        ///
        /// <para><b>Added 2026-08-16 (round 6), and shared deliberately.</b> Round 5 taught the
        /// <c>/*</c> opener to read its region as C#, and left <c>@*</c> a raw text search, so the
        /// identical silence stayed spellable one character differently — see
        /// <see cref="EligibleRazorCommentSpans"/>. Two openers judged by two walks is how the first
        /// one got fixed alone; there is now one walk and two callers, so the next rule added here
        /// reaches both by construction rather than by someone remembering.</para>
        ///
        /// <para><b>The failure directions, corrected 2026-08-16 (round 7).</b> Round 6 wrote
        /// "every failure direction of this map is UNDER-blanking, which this file chooses", and
        /// then named the raw string literal as a known misread in the next sentence. Those two
        /// statements contradict each other, and the second one is the true one. This map has TWO
        /// directions, and they are opposite:</para>
        /// <list type="bullet">
        ///   <item><description>OVER-masking — a construct the walk wrongly reads AS a literal —
        ///   removes an opener, so a real comment stops being blanked and a banned token quoted
        ///   inside it becomes a LOUD false positive. A lone apostrophe within four characters of
        ///   another is this direction.</description></item>
        ///   <item><description>UNDER-masking — a literal the walk fails to cover — leaves the
        ///   characters inside it eligible to open a comment, which blanks live code: a SILENCE.
        ///   The round-6 interpolation hole was this direction, and so was the raw string literal
        ///   round 6 filed under the other one. Both are closed in
        ///   <see cref="SkipStringLiteral"/>.</description></item>
        /// </list>
        /// <para>Under-masking is the direction to hunt, and the way it is closed is by widening
        /// WHERE the map looks, not by trusting the walk — see <see cref="CSharpLiteralRegions"/>.
        /// Named limit, measured rather than claimed away: inside the inline regions that walk adds,
        /// Razor MARKUP is interleaved with C#, and an HTML attribute quote there is read as a
        /// string literal. That is the over-masking direction, so it is loud — MEASURED 2026-08-16,
        /// the whole change moves 0 bytes of blanked output over the 161 shipped files.</para>
        /// <para>Named limit, the SILENT direction, found by the 2026-08-16 re-gate exactly the way
        /// this file predicts new spellings are found (planted, compiled, read): this mask covers
        /// string literals, character literals and <c>//</c> comments, but NOT <c>/* … */</c> block
        /// comments — and <see cref="EligibleCSharpCommentSpans"/> declines a mid-line opener whose
        /// close is on a later line, so neither walk sees those characters. A <c>@*</c> inside a
        /// mid-line multi-line block comment therefore remains an eligible opener, the balance
        /// backstop stays green, and a live gate between two such comments went unnamed
        /// (Pages/ReplicationMap.razor.cs probe, build 0 errors, census green, causality proved by
        /// changing only the <c>@*</c> token). MEASURED: 0 such shapes in today's tree. The
        /// structural close for the whole class is the chokepoint lane, not a ninth spelling
        /// rule.</para>
        /// </summary>
        private static bool[] CSharpLiteralMask(string source, List<(int Start, int End)> cSharpRegions)
        {
            var masked = new bool[source.Length];

            foreach (var region in cSharpRegions)
            {
                for (int i = region.Start; i < region.End && i < source.Length; i++)
                {
                    var c = source[i];
                    int end;

                    if (c == '"')
                    {
                        end = SkipStringLiteral(source, i);
                    }
                    else if (c == '\'')
                    {
                        end = SkipCharLiteral(source, i);
                        if (end <= i) continue;                 // a bare apostrophe, not a literal
                    }
                    else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                    {
                        var newline = source.IndexOf('\n', i);
                        end = (newline < 0 ? source.Length : newline) - 1;
                    }
                    else continue;

                    for (int j = i; j <= end && j < source.Length; j++) masked[j] = true;
                    i = end;
                }
            }

            return masked;
        }

        /// <summary>
        /// Every <c>@*</c> in <paramref name="source"/> that is eligible to open a Razor comment,
        /// with the exclusive end of the span it would cover.
        ///
        /// <para><b>Added 2026-08-16 (round 6): this opener was still a raw text search.</b> Round 5
        /// rewrote the <c>/*</c> hunt to read each C# region as C# and requires the <c>*/</c> to
        /// close inside the region that made the opener eligible. It left <c>@*</c> exactly as
        /// written on 2026-08-15 — <c>source.IndexOf("@*", i)</c> anywhere in the file, closed by an
        /// unbounded <c>source.IndexOf("*@")</c> — so the round-5 silence was still spellable one
        /// character differently, and the paragraph on <see cref="BlankBlockComments"/> saying an
        /// unclosed opener blanks nothing was describing the only half of the rule that held.</para>
        ///
        /// <para><b>What "encodes what Razor does" was worth, corrected 2026-08-16 (round 7).</b>
        /// The sentence that stood here said the rule now encodes what Razor does. It encoded two of
        /// the three things Razor does with these characters, and the gate found the third by
        /// planting it: Razor resolves its own <c>@@</c> ESCAPE before it looks for a transition, so
        /// <c>@@*</c> opens nothing, and this rule read it as an opener. PROVED by the compiler —
        /// the lines after a planted <c>@@*</c> compiled as live code (a bogus member on the next
        /// line failed the build with <c>CS1061</c>) while the rule blanked them as a comment. The
        /// backstop that was supposed to catch a stale rule,
        /// <see cref="NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot"/>, counted the same
        /// <c>@@*</c> as a delimiter, so one balancing literal kept it green. Both now ask
        /// <see cref="AtStarIsEscaped"/>. The honest claim is narrower than the one it replaces:
        /// this rule encodes the three ways an <c>@*</c> stops being an opener that we have looked
        /// for, each REMOVING openers, and a fourth would be found the way these three were — by
        /// planting one and reading the compiler, not by reading this paragraph.</para>
        ///
        /// <para>So: <c>@*</c> opens a comment where Razor reads a
        /// transition — markup, and C# region text alike — but two characters that C# has
        /// already put inside a string literal, a character literal or a <c>//</c> comment are not a
        /// transition, they are literal content (<see cref="CSharpLiteralMask"/>, scoped by
        /// <see cref="CSharpLiteralRegions"/>, which round 7 widened past the blanking regions to
        /// the inline <c>@{ … }</c> and <c>@if</c>/<c>@for</c>/<c>@foreach</c> bodies where a gate
        /// was proved hidable). The <c>*@</c> is
        /// then the next one textually, because a Razor comment does not nest and does not parse
        /// literals inside itself; an opener with no <c>*@</c> after it blanks NOTHING, and an opener
        /// inside a C# region whose <c>*@</c> falls outside that region blanks nothing either. That
        /// second case is a file that does not build — PROVED by the compiler 2026-08-16, a bare
        /// <c>@* opened inside this code block and never closed inside it</c> planted at
        /// <c>Pages/ScheduledTasks.razor:25</c> failed with
        /// <c>error RZ1006: The code block is missing a closing "}" character</c> pointing at the
        /// block's own opener, <c>ScheduledTasks.razor(20,7)</c> — so the honest answer to it is a
        /// loud false positive rather than a 200-line silence.</para>
        /// </summary>
        private static List<(int Start, int End)> EligibleRazorCommentSpans(
            string source, List<(int Start, int End)> cSharpRegions, bool[] masked)
        {
            var spans = new List<(int, int)>();

            for (int i = source.IndexOf("@*", StringComparison.Ordinal);
                 i >= 0;
                 i = source.IndexOf("@*", i + 2, StringComparison.Ordinal))
            {
                if (masked[i]) continue;                        // literal content, not a transition
                if (AtStarIsEscaped(source, i)) continue;       // `@@` escape: the `*` is text

                var close = source.IndexOf("*@", i + 2, StringComparison.Ordinal);
                if (close < 0) continue;                        // unclosed: blank nothing

                var regionEnd = -1;
                foreach (var region in cSharpRegions)
                    if (i >= region.Start && i < region.End) { regionEnd = region.End; break; }

                if (regionEnd >= 0 && close + 2 > regionEnd) continue;   // no close in this region

                spans.Add((i, close + 2));
                i = close;                                      // the loop's +2 lands past the `*@`
            }

            return spans;
        }

        /// <summary>
        /// <c>true</c> when the <c>@</c> at <paramref name="i"/> — the first character of a matched
        /// <c>@*</c> — is the SECOND half of a Razor <c>@@</c> escape, so the <c>*</c> behind it is
        /// ordinary text and nothing opens.
        ///
        /// <para><b>Added 2026-08-16 (round 7), and it was a live silence.</b> Rounds 5 and 6 taught
        /// both openers to read literals, and neither taught <c>@*</c> that Razor's own escape comes
        /// first. Razor pairs a run of <c>@</c> left to right: in a run of N ending at the matched
        /// <c>@*</c>, an EVEN N is entirely consumed by escapes and the <c>*</c> is text, an ODD N
        /// leaves the last <c>@</c> free to transition. So <c>@@*</c> is inert and <c>@@@*</c>
        /// opens. PROVED by the compiler 2026-08-16: <c>@@*</c> planted in shipped markup compiled
        /// the lines after it as LIVE CODE — a deliberately bogus member on the line below failed
        /// the build with <c>CS1061</c>, which a comment cannot do — while the round-6 rule read
        /// those same lines as a comment and blanked them.</para>
        ///
        /// <para>Called from both places that judge an opener — <see cref="EligibleRazorCommentSpans"/>
        /// and <see cref="NoShippedFileHoldsAnAtStarThatOpensSomethingItShouldNot"/>, the pin that is
        /// supposed to catch a stale rule. The pin needed it too: its <c>@*</c>/<c>*@</c> balance
        /// check counted an escaped <c>@@*</c> as an opener, so a file holding one balanced out and
        /// the pin stayed green over the defect it exists to find. One backward loop, two callers,
        /// for the reason <see cref="CSharpLiteralMask"/> is shared.</para>
        ///
        /// <para>MEASURED 2026-08-16 over the 161 shipped UI files: 30 <c>@@</c> escapes on 25 lines
        /// across 12 files (SQL <c>@@SERVERNAME</c> prose, CSS <c>@@media</c> and
        /// <c>@@keyframes</c>, parameter placeholders), and 0 of them are <c>@@*</c> — so this rule
        /// removes no span from the tree today, and no mutation test can pin the balance-pin half of
        /// it against the tree either. That half was proved by the planted probe described on
        /// MutationTwentySeven, which is the only evidence class available for it.</para>
        /// </summary>
        private static bool AtStarIsEscaped(string source, int i)
        {
            var run = 1;                                        // the `@` at i counts itself
            for (int k = i - 1; k >= 0 && source[k] == '@'; k--) run++;
            return run % 2 == 0;
        }

        /// <summary>
        /// Every <c>/*</c> in <paramref name="source"/> that is eligible to open a C# block comment,
        /// with the exclusive end of the span it would cover. Eligible means: inside one of
        /// <paramref name="cSharpRegions"/>, not inside a literal or a <c>//</c> comment, AND
        /// line-initial with a <c>*/</c> BELOW IT IN THE SAME REGION, or closed on its own line.
        /// Everything else — the markup, string-literal, path and regex <c>/*</c>s that live in a
        /// Razor tree — is not an opener.
        ///
        /// <para><b>Read as C#, added 2026-08-16 (round 5), because the round-4 version was a text
        /// search.</b> It walked <c>IndexOf("/*")</c> over the whole file and asked only whether the
        /// hit fell inside a region, so a <c>/*</c> INSIDE A STRING LITERAL in a <c>@code</c> block
        /// was an eligible opener, and its <c>*/</c> was hunted with an unbounded
        /// <c>IndexOf</c> that could land far below in plain markup. A three-line verbatim string
        /// opened at line 22 of <c>Pages/ScheduledTasks.razor</c>'s first <c>@code</c> block
        /// therefore blanked live markup 210 lines down, hiding a static two-argument gate at line
        /// 232 from both the argument-count ban and the receiver-named ban. PROVED in the tree
        /// 2026-08-16 with the instrument as the only variable: that plant built with 0 errors and
        /// left the census at 59/59 exit 0 under the round-4 stripper, and is named TWICE — by the
        /// argument-count ban and by the static ban, both at <c>Pages/ScheduledTasks.razor:232</c> —
        /// under this one. That is a SILENCE, and the paragraph on
        /// <see cref="BlankBlockComments"/> claimed the remaining error direction could only ever be
        /// a loud false positive.</para>
        ///
        /// <para>The walk now reads each region as C#, off the same literal map the <c>@*</c> opener
        /// is judged against (<see cref="CSharpLiteralMask"/>, shared in round 6 — round 5 had this
        /// walk carrying its own copy of the skips, which is exactly why the ruling reached one
        /// opener and not the other) — string literals (verbatim included), character literals, and
        /// <c>//</c> to end of line — and requires the closing <c>*/</c> to fall inside the region
        /// that made the opener eligible.</para>
        ///
        /// <para><b>What the sentence that stood here claimed, corrected 2026-08-16 (round 7).</b>
        /// It read "both failure directions of the new rule are under-blanking: a literal or a
        /// region boundary the walk misreads yields FEWER spans … never a silence." That is true of
        /// one direction and false of the other, and it is false in the silent one. The walk asks
        /// <see cref="CSharpLiteralMask"/> whether a position is literal content, so a literal the
        /// MAP MISSES leaves a <c>/*</c> inside it eligible — MORE spans, blanking live C# in the
        /// region below it. Round 6's raw string literal was exactly that miss and its own doc named
        /// it while filing it under the loud direction. Read honestly: over-masking and a lost region
        /// boundary yield fewer spans and are loud; under-masking yields more spans and is silent.
        /// The under-masking cases known today are closed in <see cref="SkipStringLiteral"/> and
        /// pinned by MutationTwentySix and MutationTwentyNine; the way the next one gets closed is
        /// by widening the map, which is why the map is shared and separately scoped.</para>
        /// </summary>
        private static List<(int Start, int End)> EligibleCSharpCommentSpans(
            string source, List<(int Start, int End)> cSharpRegions, bool[] masked)
        {
            var spans = new List<(int, int)>();

            // Razor markup is not C#. `/*` there is literal text, and every `@` transition between it
            // and the `*/` is compiled — PROVED by CS1061, see BlankBlockComments. So the walk never
            // leaves a C# region.
            foreach (var region in cSharpRegions)
            {
                for (int i = region.Start; i < region.End && i < source.Length; i++)
                {
                    if (masked[i]) continue;                    // a literal or a `//`: opens no span
                    if (source[i] != '/' || i + 1 >= source.Length) continue;
                    if (source[i + 1] != '*') continue;

                    var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0 || close + 2 > region.End) continue;   // no close in this region

                    var lineStart = i == 0 ? 0 : source.LastIndexOf('\n', i - 1) + 1;
                    var lineEnd = source.IndexOf('\n', i);
                    if (lineEnd < 0) lineEnd = source.Length;

                    var lineInitial = string.IsNullOrWhiteSpace(source.Substring(lineStart, i - lineStart));
                    if (!lineInitial && close >= lineEnd) continue;

                    spans.Add((i, close + 2));
                    i = close + 1;
                }
            }

            spans.Sort();                                       // merged with the Razor spans in order
            return spans;
        }

        /// <summary>Which language the census should read a shipped UI file as.</summary>
        internal enum UiSourceKind
        {
            /// <summary>A <c>.razor</c> file: markup, with C# only inside <c>@code</c>/<c>@functions</c>.</summary>
            Razor,

            /// <summary>A <c>.cs</c> file, including a <c>.razor.cs</c> code-behind: C# throughout.</summary>
            CSharp
        }

        internal static UiSourceKind KindOf(FileInfo file) =>
            file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? UiSourceKind.CSharp
                : UiSourceKind.Razor;

        /// <summary>
        /// The half-open character ranges of <paramref name="source"/> that are C#. For a
        /// <c>.cs</c> file that is the whole file. For a <c>.razor</c> file it is the brace-matched
        /// body of every <c>@code { … }</c> and <c>@functions { … }</c> block — several files carry
        /// two (MEASURED 2026-08-16: 155 blocks across 142 shipped <c>.razor</c> files, 13 of which
        /// carry two, and
        /// <c>Pages/ScheduledTasks.razor</c> opens one at line 20 with markup below it, so "everything
        /// after the first <c>@code</c>" would be wrong).
        ///
        /// <para>A block whose brace never matches contributes NO region, which is the fail-loud
        /// direction: its comments stop being blanked, so a banned token quoted inside one becomes a
        /// noisy false positive instead of a silence.
        /// <see cref="EveryShippedRazorCodeBlockIsBraceMatched"/> measures that no shipped file is in
        /// that state today, so the fallback is a safety net and not the operating mode.</para>
        /// </summary>
        internal static List<(int Start, int End)> CSharpRegions(string source, UiSourceKind kind)
        {
            if (kind == UiSourceKind.CSharp)
                return new List<(int, int)> { (0, source.Length) };

            var regions = new List<(int, int)>();
            foreach (Match m in RazorCodeBlock.Matches(source))
            {
                var brace = m.Index + m.Length - 1;              // the `{` the pattern ends on
                var close = MatchingBrace(source, brace);
                if (close > brace) regions.Add((brace, close + 1));
            }
            return regions;
        }

        /// <summary>
        /// A <c>@code</c> / <c>@functions</c> block opener. Anchored to the start of a line because
        /// that is where Razor requires the directive, and because an unanchored match would let the
        /// words appear inside prose and open a phantom C# region — which blanks, and blanking is the
        /// silent direction.
        /// </summary>
        private static readonly Regex RazorCodeBlock =
            new(@"^[ \t]*@(?:code|functions)\b\s*\{", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Where <see cref="CSharpLiteralMask"/> looks for literals. This is DELIBERATELY WIDER than
        /// <see cref="CSharpRegions"/>, which is what the blanking rules use, and the two must not be
        /// merged.
        ///
        /// <para><b>Added 2026-08-16 (round 7), because one scope was doing two jobs.</b> Rounds 4
        /// and 5 ruled — twice, and correctly — that widening the BLANKING region to
        /// <c>@{ … }</c> markup code blocks would be wrong: a construct the census wrongly treats as
        /// a comment gets blanked, and blanking is the silent direction. Round 6 then reached for
        /// the same list of regions to say where LITERALS live, and those two questions have
        /// opposite failure directions. The mask only ever REMOVES openers, so a literal outside the
        /// narrow regions is a literal the census does not know about, and a <c>@*</c> inside it is
        /// an opener that blanks live code.</para>
        ///
        /// <para>PROVED by the round-7 gate: a <c>@*</c> inside a string literal in a <c>@{ … }</c>
        /// block opened a comment and hid a gate below it, with the round-6 rule as the only
        /// variable. So the mask now also covers the brace-matched body of every <c>@{</c> and every
        /// inline C# statement block — <c>@if</c>/<c>else</c>, <c>@for</c>, <c>@foreach</c>,
        /// <c>@while</c>, <c>@switch</c>, <c>@lock</c>, <c>@using (…)</c>, <c>@do</c>,
        /// <c>@try</c>/<c>catch</c>/<c>finally</c> — while every blanking rule keeps reading the
        /// narrow list. Pinned by MutationTwentyEight.</para>
        ///
        /// <para>MEASURED 2026-08-16 over the 161 shipped UI files: 44 <c>@{ … }</c> blocks, and the
        /// blanked output is BYTE-IDENTICAL to 20a0384's over every file — this widening costs the
        /// tree nothing today and exists so the next literal written in one of those blocks is
        /// known. Named limits, in OPPOSITE directions: a block whose brace does not match
        /// contributes nothing, so its literals stay unknown and a <c>@*</c> inside one stays an
        /// eligible opener — the SILENT direction, and
        /// <see cref="EveryShippedRazorCodeBlockIsBraceMatched"/> only measures brace-matching for
        /// <c>@code</c>, not for these inline blocks; the markup interleaved inside these bodies
        /// has its attribute quotes read as string literals, which masks more and therefore blanks
        /// less — the loud direction. An earlier headline here claimed both limits were loud; the
        /// 2026-08-16 re-gate falsified it against this paragraph's own parenthesis. House prose
        /// class, occurrence eighteen.</para>
        /// </summary>
        internal static List<(int Start, int End)> CSharpLiteralRegions(string source, UiSourceKind kind)
        {
            if (kind == UiSourceKind.CSharp)
                return new List<(int, int)> { (0, source.Length) };

            var regions = CSharpRegions(source, kind);

            foreach (Match m in RazorInlineCode.Matches(source))
            {
                var open = source[m.Index + 1] == '{'
                    ? m.Index + 1
                    : BraceAfterHeader(source, m.Index + m.Length);

                while (open >= 0)
                {
                    var close = MatchingBrace(source, open);
                    if (close <= open) break;

                    regions.Add((open, close + 1));
                    open = ContinuationBrace(source, close + 1);
                }
            }

            return regions;
        }

        /// <summary>
        /// An inline Razor C# statement block opener. Unanchored, unlike <see cref="RazorCodeBlock"/>,
        /// because these appear mid-markup — and safely so: a match that is not followed by a header
        /// and a <c>{</c> contributes nothing, and every region this finds only REMOVES comment
        /// openers.
        /// </summary>
        private static readonly Regex RazorInlineCode = new(
            @"@(?:\{|(?:if|for|foreach|while|switch|lock|using|do|try)\b)",
            RegexOptions.Compiled);

        /// <summary>
        /// Index of the <c>{</c> that opens a block whose keyword ended at <paramref name="at"/>,
        /// skipping an optional balanced <c>( … )</c> header, or -1 when no brace follows — which is
        /// how <c>@using System;</c> (a directive, not a statement) contributes no region.
        /// </summary>
        private static int BraceAfterHeader(string source, int at)
        {
            var i = SkipWhitespace(source, at);
            if (i < source.Length && source[i] == '(')
            {
                var close = SkipBalancedParens(source, i);
                if (close < 0) return -1;
                i = SkipWhitespace(source, close + 1);
            }
            return i < source.Length && source[i] == '{' ? i : -1;
        }

        /// <summary>
        /// Index of the <c>{</c> opening an <c>else</c>, <c>else if</c>, <c>catch</c> or
        /// <c>finally</c> block continuing the one that closed at <paramref name="at"/>, or -1. Those
        /// clauses carry no <c>@</c> of their own, so without this the second half of every
        /// <c>@if … else …</c> in shipped markup would be a region the literal map never saw.
        /// </summary>
        private static int ContinuationBrace(string source, int at)
        {
            var i = SkipWhitespace(source, at);

            foreach (var word in new[] { "else", "catch", "finally" })
            {
                if (!StartsWithWord(source, i, word)) continue;

                i += word.Length;
                if (word == "else" && StartsWithWord(source, SkipWhitespace(source, i), "if"))
                    i = SkipWhitespace(source, i) + 2;

                return BraceAfterHeader(source, i);
            }

            return -1;
        }

        private static int SkipWhitespace(string source, int at)
        {
            var i = at;
            while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
            return i;
        }

        private static bool StartsWithWord(string source, int at, string word) =>
            at >= 0
            && at + word.Length <= source.Length
            && string.CompareOrdinal(source, at, word, 0, word.Length) == 0
            && (at + word.Length == source.Length
                || !char.IsLetterOrDigit(source[at + word.Length]) && source[at + word.Length] != '_');

        /// <summary>
        /// Index of the <c>)</c> closing the <c>(</c> at <paramref name="open"/>, or -1. Literals are
        /// skipped whole, so a parenthesis inside <c>"(a"</c> counts for nothing.
        /// </summary>
        private static int SkipBalancedParens(string source, int open)
        {
            var depth = 0;

            for (int i = open; i < source.Length; i++)
            {
                var c = source[i];

                if (c == '"') { i = SkipStringLiteral(source, i); continue; }
                if (c == '\'') { var e = SkipCharLiteral(source, i); if (e > i) { i = e; continue; } }

                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return i;
            }

            return -1;
        }

        /// <summary>
        /// Index of the <c>}</c> closing the <c>{</c> at <paramref name="open"/>, or -1 when it does
        /// not close. Braces inside string literals, character literals, <c>//</c> comments and
        /// <c>/* … */</c> comments do not count — an unterminated comment returns -1 rather than
        /// guessing, which is why MutationNine's unclosed <c>/*</c> inside <c>@code</c> blanks
        /// nothing.
        /// </summary>
        private static int MatchingBrace(string source, int open)
        {
            var depth = 0;

            for (int i = open; i < source.Length; i++)
            {
                var c = source[i];

                if (c == '"')
                {
                    i = SkipStringLiteral(source, i);
                    continue;
                }

                if (c == '\'')
                {
                    var end = SkipCharLiteral(source, i);
                    if (end > i) { i = end; continue; }
                }

                if (c == '/' && i + 1 < source.Length)
                {
                    if (source[i + 1] == '/')
                    {
                        var newline = source.IndexOf('\n', i);
                        if (newline < 0) return -1;
                        i = newline;
                        continue;
                    }

                    if (source[i + 1] == '*')
                    {
                        var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (end < 0) return -1;                  // unterminated: refuse to guess
                        i = end + 1;
                        continue;
                    }
                }

                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }

            return -1;
        }
    }
}
