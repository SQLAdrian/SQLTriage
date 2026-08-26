/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// C1 DPAPI-scope ruling (2026-08-20): move the wrap from LocalMachine to CurrentUser, with a
    /// LocalMachine read-fallback, for any store that is genuinely single-process (written and read
    /// by the same Windows account).
    ///
    /// <para><b>The finding, not just the code.</b> Every production <c>DataProtectionScope.LocalMachine</c>
    /// site was mapped (grep for <c>ProtectedData</c>/<c>DataProtectionScope</c> across non-test
    /// source) and cross-checked against the deployment reality: SQLTriage runs as TWO processes
    /// sharing one install folder — the Windows service as <c>NT SERVICE\SQLTriage</c>, and the
    /// interactive desktop app under the operator's own account — both wired through the same
    /// <c>AddSharedServices</c> composition root (<c>App.xaml.cs</c> and <c>WindowsServiceHost.cs</c>).
    /// All four LocalMachine sites resolve their key-file path off <c>AppContext.BaseDirectory</c> —
    /// the shared install folder, not a per-user profile path — so the SAME physical key file is
    /// opened by whichever account is currently running. DPAPI CurrentUser data is bound to the SID
    /// that wrapped it, so a CurrentUser write on any of these would make a cross-account read fail
    /// outright, not merely need a fallback. <c>AuditLogService</c> even carries a RULED 2026-08-01
    /// note recording the opposite migration already happened there once, for exactly this reason
    /// (a service identity change made a CurrentUser-wrapped key unreadable and orphaned a live audit
    /// chain). <c>Data/Services/Portal/PortalPublishRunner.cs</c> independently confirms the same
    /// lesson at the FILE level: <c>portal-settings.json</c> — which carries the DPAPI-wrapped intake
    /// SAS this ruling specifically named — was itself migrated OFF a per-user
    /// <c>%APPDATA%\SQLTriage\</c> path onto the shared <c>AppContext.BaseDirectory\config\</c> path
    /// (<c>LegacyLocalSettingsFilePath</c> / <c>MigrateLegacySettings</c>).</para>
    ///
    /// <para><b>The one counter-example proves the rule.</b> <c>Data/Services/Portal/Export/DpapiKeyStore.cs</c>
    /// (the Export Pack lane's client key) is <em>already</em> CurrentUser-scoped, by an explicit
    /// earlier ruling (§4 ruling 1) — because that lane genuinely does run under one stable, dedicated
    /// account. It is included below as the contrast: the codebase already knows how to make this call
    /// correctly when the single-process precondition actually holds.</para>
    ///
    /// <para><b>Result: no site qualifies for the C1 migration.</b> Per its own rule ("do NOT break a
    /// genuinely shared store — leave it at LocalMachine, document why, and put the finding in
    /// needsRuling"), this lane's code change is documentation only (a 2026-08-20 review note at each
    /// site) plus this census, which pins today's scope choices so a future change to any of them is a
    /// deliberate act, not a silent drift. See WORKLIST-2026-08-20-app-lane-wave.md needsRuling for the
    /// consolidated write-up.</para>
    /// </summary>
    public class DpapiScopeArchitectureTests
    {
        /// <summary>
        /// One reviewed LocalMachine site. <see cref="ExpectedScope"/> pins today's choice so a future
        /// change is a deliberate diff to this test, not a silent drift; <see cref="SharedPathAnchor"/>
        /// is the structural fact ("resolves under the shared install folder, not a per-user profile")
        /// that makes the scope choice correct.
        /// </summary>
        private sealed record ReviewedSite(string RelativePath, string ExpectedScope, string SharedPathAnchor);

        private static readonly ReviewedSite[] SharedLocalMachineSites =
        {
            new("Data/CredentialProtector.cs", "DataProtectionScope.LocalMachine",
                "AppContext.BaseDirectory"),
            new("Data/AuditLogService.cs", "DataProtectionScope.LocalMachine",
                "AppContext.BaseDirectory"),
            new("Data/SqliteCipherHelper.cs", "DataProtectionScope.LocalMachine",
                "AppContext.BaseDirectory"),
            new("Data/Services/Licensing/SeatRegister.cs", "DataProtectionScope.LocalMachine",
                "AppContext.BaseDirectory"),
        };

        /// <summary>
        /// The one site the codebase already made single-process-correct: CurrentUser, because the
        /// Export Pack lane genuinely runs under one dedicated account. Kept as the contrast.
        /// </summary>
        private const string SingleProcessSite = "Data/Services/Portal/Export/DpapiKeyStore.cs";

        [Fact]
        public void Every_reviewed_LocalMachine_site_still_uses_LocalMachine()
        {
            var root = RepoRoot();

            foreach (var site in SharedLocalMachineSites)
            {
                var source = File.ReadAllText(
                    Path.Combine(root, site.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

                source.Should().Contain(site.ExpectedScope,
                    $"{site.RelativePath} was reviewed 2026-08-20 for the C1 ruling and found "
                    + "genuinely shared across the service and interactive-app accounts; a change away "
                    + "from LocalMachine here needs a fresh ruling, not a silent edit");
            }
        }

        [Fact]
        public void Every_reviewed_LocalMachine_site_keys_off_the_shared_install_folder_not_a_per_user_path()
        {
            var root = RepoRoot();

            foreach (var site in SharedLocalMachineSites)
            {
                var source = File.ReadAllText(
                    Path.Combine(root, site.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

                source.Should().Contain(site.SharedPathAnchor,
                    $"{site.RelativePath}'s LocalMachine choice rests on its key path resolving under "
                    + "the shared install folder (both the service account and the interactive "
                    + "operator account read the same physical file); losing that anchor would "
                    + "invalidate the reasoning even if the scope constant is untouched");

                // The structural counter-fact: none of these may key off a per-user special folder,
                // because a per-user path is exactly what would make CurrentUser scope SAFE here — and
                // none of them do, which is the other half of "genuinely shared".
                source.Should().NotContain("SpecialFolder.ApplicationData",
                    $"{site.RelativePath}: a per-user ApplicationData path would change this site's "
                    + "classification from shared to single-process");
                source.Should().NotContain("SpecialFolder.LocalApplicationData",
                    $"{site.RelativePath}: as above, for the per-user Local variant");
            }
        }

        [Fact]
        public void The_one_genuinely_single_process_site_is_CurrentUser_not_LocalMachine()
        {
            var root = RepoRoot();
            var source = File.ReadAllText(
                Path.Combine(root, SingleProcessSite.Replace('/', Path.DirectorySeparatorChar)));

            source.Should().Contain("DataProtectionScope.CurrentUser",
                $"{SingleProcessSite} is the contrast case: it runs under one dedicated account, so "
                + "CurrentUser is the correct — and already-ruled — scope there");
            source.Should().NotContain("DataProtectionScope.LocalMachine",
                $"{SingleProcessSite} must not silently pick up the machine-wide scope its neighbours "
                + "use for a different reason");
        }

        // ── DPAPI mechanics: the CurrentUser-tries-first / LocalMachine-fallback SHAPE ─────────────
        //
        // No production site in this repo needed the flip (see the class doc above), so there is no
        // production write path to drive here. What these two tests establish instead is that the
        // primitive the ruling depends on behaves the way the ruling assumes it does, using raw
        // ProtectedData over test-owned temp files and test-owned entropy — never CredentialProtector,
        // SqliteCipherHelper or SeatRegister, whose default key paths resolve under this test host's
        // OWN AppContext.BaseDirectory and have previously corrupted shared state when driven directly
        // (recorded in AsideProducerCensusTests: rotated the host's SQLite cipher key and orphaned
        // alert-history.db). This test touches nothing but its own byte arrays.

        private static readonly byte[] TestEntropy = Encoding.UTF8.GetBytes("DpapiScopeArchitectureTests.v1");

        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void A_LocalMachine_wrapped_blob_still_unwraps_under_LocalMachine_scope()
        {
            var plaintext = Encoding.UTF8.GetBytes("still readable after the wrap");
            var wrapped = ProtectedData.Protect(plaintext, TestEntropy, DataProtectionScope.LocalMachine);

            var unwrapped = ProtectedData.Unprotect(wrapped, TestEntropy, DataProtectionScope.LocalMachine);

            unwrapped.Should().BeEquivalentTo(plaintext,
                "this is the read half of every reviewed site above: whatever migration ruling comes "
                + "next, an existing LocalMachine-wrapped store must keep decrypting under LocalMachine");
        }

        /// <summary>
        /// ⚠ MEASURED 2026-08-20, and it corrected the first draft of this test, which asserted the
        /// textbook claim that <c>ProtectedData.Unprotect</c>'s <c>scope</c> argument gates readability
        /// (CurrentUser-wrapped throws under <c>scope: LocalMachine</c> and vice versa). It does not,
        /// AT LEAST not for the SAME calling account: probed standalone on this box (net8.0-windows,
        /// account <c>afsul</c>) — <c>Unprotect(currentUserWrapped, entropy, scope: LocalMachine)</c>
        /// and <c>Unprotect(localMachineWrapped, entropy, scope: CurrentUser)</c> BOTH succeeded and
        /// returned the original plaintext. <c>ProtectedData.Protect</c> also randomizes on every call
        /// regardless of scope (two same-scope calls over identical plaintext/entropy differ too, also
        /// measured), so ciphertext inequality between scopes proves nothing about key separation
        /// either — that line of evidence was tried and discarded.
        /// <para>
        /// <b>What this does and does not establish.</b> It establishes that <c>Unprotect</c>'s scope
        /// argument is not, by itself, a readability gate for the identity that is CALLING it — Windows
        /// resolves the correct master key from the blob's own embedded key material, not from the
        /// flag passed to Unprotect. It does NOT establish, and this single-process/single-account test
        /// harness cannot establish, whether a genuinely DIFFERENT Windows identity (the installed
        /// service running as <c>NT SERVICE\SQLTriage</c> versus an interactive operator's own account)
        /// can read the other's CurrentUser-scoped data — that depends on which identity's per-profile
        /// DPAPI master key store the blob was wrapped under, which requires two distinct accounts to
        /// observe and is exactly the gap <see cref="DpapiScopeArchitectureTests"/>'s class doc names as
        /// the reason every reviewed site stays LocalMachine. Recorded as an explicit limit rather than
        /// silently asserting the untested direction, the same discipline
        /// <see cref="AsideProducerCensusTests"/> already applies to its own isolation gaps.
        /// </para>
        /// </summary>
        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void Unprotects_scope_argument_does_not_gate_same_account_reads_cross_account_is_untested_here()
        {
            var plaintext = Encoding.UTF8.GetBytes("scope-bound");
            var currentUserWrapped =
                ProtectedData.Protect(plaintext, TestEntropy, DataProtectionScope.CurrentUser);
            var localMachineWrapped =
                ProtectedData.Protect(plaintext, TestEntropy, DataProtectionScope.LocalMachine);

            // Same account, mismatched scope argument on Unprotect: MEASURED to succeed both ways.
            var readCuAsLm =
                ProtectedData.Unprotect(currentUserWrapped, TestEntropy, DataProtectionScope.LocalMachine);
            var readLmAsCu =
                ProtectedData.Unprotect(localMachineWrapped, TestEntropy, DataProtectionScope.CurrentUser);

            readCuAsLm.Should().BeEquivalentTo(plaintext,
                "measured 2026-08-20: within one account, Unprotect(scope: LocalMachine) still opens a "
                + "CurrentUser-wrapped blob. This is a fact about the primitive, not a claim that a "
                + "DIFFERENT account could do the same");
            readLmAsCu.Should().BeEquivalentTo(plaintext,
                "measured 2026-08-20, the mirror direction: within one account, Unprotect(scope: "
                + "CurrentUser) still opens a LocalMachine-wrapped blob");
        }

        /// <summary>
        /// The fallback SHAPE the ruling calls for (try the new scope, fall back to the old one) —
        /// <see cref="CredentialProtector"/>'s own <c>DecryptDpapi</c> already has exactly this shape
        /// for its legacy <c>"enc:"</c> format. This proves the shape recovers plaintext for a blob
        /// wrapped under either scope; per the test above it cannot additionally claim the fallback
        /// BRANCH specifically fired (same-account Unprotect does not need it to), only that the
        /// function's OUTPUT is correct either way — which is what a caller observes.
        /// </summary>
        [Fact]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void The_try_CurrentUser_then_LocalMachine_read_shape_recovers_plaintext_for_either_wrap()
        {
            var plaintext = Encoding.UTF8.GetBytes("scope-bound");
            var currentUserWrapped =
                ProtectedData.Protect(plaintext, TestEntropy, DataProtectionScope.CurrentUser);
            var localMachineWrapped =
                ProtectedData.Protect(plaintext, TestEntropy, DataProtectionScope.LocalMachine);

            Func<byte[], byte[]> tryCurrentUserThenLocalMachine = wrapped =>
            {
                try
                {
                    return ProtectedData.Unprotect(wrapped, TestEntropy, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    return ProtectedData.Unprotect(wrapped, TestEntropy, DataProtectionScope.LocalMachine);
                }
            };

            tryCurrentUserThenLocalMachine(currentUserWrapped).Should().BeEquivalentTo(plaintext,
                "a CurrentUser-wrapped blob read by this shape");
            tryCurrentUserThenLocalMachine(localMachineWrapped).Should().BeEquivalentTo(plaintext,
                "a LocalMachine-wrapped blob read by this shape — the compatibility guarantee a future "
                + "migration would depend on, whichever branch actually satisfies it in production");
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                dir = dir.Parent;
            dir.Should().NotBeNull("the census reads app SOURCE, so it needs the repo root");
            return dir!.FullName;
        }
    }
}
