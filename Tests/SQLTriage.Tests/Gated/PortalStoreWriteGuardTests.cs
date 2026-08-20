/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SQLTriage.Data;
using SQLTriage.Data.Services.Portal;
using Xunit;

namespace SQLTriage.Tests.Gated
{
    /// <summary>
    /// The D4 write guard on <c>portal-settings.json</c> — the file holding the
    /// CredentialProtector-wrapped Azure intake SAS. Split out of
    /// <c>ConfigStoreWriteGuardTests</c> and filed here for the reason <c>Gated/README.md</c>
    /// gives: <c>PortalPublishRunner</c> lives under <c>Data\Services\Portal\**</c>, which
    /// <c>buildprofile.targets</c> Compile-Removes from the community build, so a test binding it
    /// cannot compile in the only profile CI builds. Only the gated tests moved; the rest of the
    /// fixture is profile-independent and stayed where a developer will run it.
    ///
    /// <para><b>Why this store refuses rather than warns.</b> The intake SAS exists in exactly one
    /// place on the machine, is derivable from nothing else, and replacing it means issuing a new
    /// one in Azure. A damaged file deserialises to defaults with that field blank and the next
    /// save writes the blank over it — which is the measured /portal-status incident with one
    /// variable changed. There, the stored SAS was never echoed back into its input, so clicking
    /// Save wrote an empty string and cleared a live credential; a handoff chore instructing
    /// someone to click it had to be withdrawn as destructive. Here it is the whole object being
    /// defaults, reachable from four entry points, two of which fire with no operator present.</para>
    ///
    /// <para><b>These drive the REAL settings path</b> under the test host's BaseDirectory, because
    /// the portal seam is deliberately static and computes its own path. Each test therefore snaps
    /// the file's bytes and puts them back. Each also creates the file BEFORE acting:
    /// <c>LoadLocalSettings</c> would otherwise run the legacy migration, which reads
    /// <c>%APPDATA%\SQLTriage</c> and RENAMES what it finds there. <c>SaveLocalSettings</c> — the
    /// method under test — never migrates; it probes. So no write here can reach a real profile.</para>
    /// </summary>
    public sealed class PortalStoreWriteGuardTests
    {
        private static string PortalPath => (string)typeof(PortalPublishRunner)
            .GetProperty("LocalSettingsFilePath", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        /// <summary>Runs <paramref name="body"/> with the real settings file restored afterwards, whatever happens.</summary>
        private static void OverTheSettingsFile(Action<string> body)
        {
            var path = PortalPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var restore = File.Exists(path) ? File.ReadAllBytes(path) : null;
            try { body(path); }
            finally
            {
                if (restore != null) File.WriteAllBytes(path, restore);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        [Theory]
        [InlineData(true)]      // truncated → quarantined by the loader
        [InlineData(false)]     // 0-byte    → damage, deliberately NOT quarantined
        public void DamagedPortalStore_RefusesTheWrite_AndTheStoredSasSurvives(bool truncated)
        {
            OverTheSettingsFile(path =>
            {
                if (truncated)
                    File.WriteAllText(path, "{\"DailyIntakeSasProtected\":\"enc:the-only-copy-of-the-sas\",");
                else
                    File.WriteAllText(path, "");
                var before = File.ReadAllBytes(path);

                // Every entry point, including RememberClientId — which fires as a side effect of
                // publishing, with nobody watching, to remember a string nobody would miss.
                Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable,
                    PortalPublishRunner.SaveLocalSettings(new PortalPublishRunner.PortalLocalSettings()));
                Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable,
                    PortalPublishRunner.SaveIntakeSas("https://acct.blob.core.windows.net/x?sig=NEW"));
                Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable,
                    PortalPublishRunner.RememberClientId("conn-1", "acme"));

                Assert.Equal(before, File.ReadAllBytes(path));
            });
        }

        [Fact]
        public void DamagedPortalStore_RefusalTakesNoSecondRejectedCopy()
        {
            // The probe exists so the WRITE path does not quarantine. Without it every refused save
            // would drop another timestamped copy of the same damaged file, and the guard would
            // litter the config directory in proportion to how often the operator tried — leaving
            // the recovery advice with several files it could be naming.
            OverTheSettingsFile(path =>
            {
                var dir = Path.GetDirectoryName(path)!;
                var preexisting = Directory.GetFiles(dir, "portal-settings.json.rejected-*").ToHashSet();
                try
                {
                    File.WriteAllText(path, "{\"DailyIntakeSasProtected\":\"enc:x\",");

                    for (var i = 0; i < 3; i++)
                        PortalPublishRunner.SaveLocalSettings(new PortalPublishRunner.PortalLocalSettings());

                    Assert.Empty(Directory.GetFiles(dir, "portal-settings.json.rejected-*")
                                          .Where(f => !preexisting.Contains(f)));
                }
                finally
                {
                    foreach (var f in Directory.GetFiles(dir, "portal-settings.json.rejected-*")
                                               .Where(f => !preexisting.Contains(f)))
                        try { File.Delete(f); } catch { }
                }
            });
        }

        [Fact]
        public void HealthyPortalStore_StillSaves()
        {
            // The control. Without it the refusals above would pass just as well on a guard that
            // refused everything, which is a broken product rather than a protected one.
            OverTheSettingsFile(path =>
            {
                ConfigFileHelper.Save(path, new PortalPublishRunner.PortalLocalSettings());

                Assert.Equal(StoreWriteOutcome.Saved,
                    PortalPublishRunner.SaveLocalSettings(new PortalPublishRunner.PortalLocalSettings
                    {
                        DailyPublishClientId = "acme-nz",
                    }));

                Assert.Equal("acme-nz",
                    ConfigFileHelper.Load<PortalPublishRunner.PortalLocalSettings>(path).DailyPublishClientId);
            });
        }

        // ── Load-time re-protect, over the real file (2026-08-05) ──────────────────────────────
        // These live here rather than beside the other intake-SAS tests for the reason the class
        // header gives: they drive the REAL settings path, and two classes doing that in one test
        // assembly would race each other. The repair fires from LoadLocalSettings, so this is the
        // only place its end-to-end behaviour can be observed.

        /// <summary>
        /// The repair is attempted ONCE per process, so a test that wants to observe it must clear
        /// the flag first — otherwise it passes or fails on which test ran before it.
        /// </summary>
        private static void ResetTheOncePerProcessAttempt() => typeof(PortalPublishRunner)
            .GetField("_sasReprotectAttempted", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, 0);

        [Fact]
        public void APlaintextStoredSas_IsEncryptedOnLoad_AndTheFileKeepsTheSameCredential()
        {
            OverTheSettingsFile(path =>
            {
                ResetTheOncePerProcessAttempt();
                const string sas = "https://acct.blob.core.windows.net/daily-acme-corp?sp=cw&sig=PLAINTEXT";
                // The signature token, asserted against the file rather than the whole URL: the
                // serializer escapes the ampersand, so the full string never appears verbatim.
                const string secretPart = "sig=PLAINTEXT";

                // The measured live-service shape: a hand-pasted SAS, stored with no prefix.
                ConfigFileHelper.Save(path, new PortalPublishRunner.PortalLocalSettings
                {
                    DailyPublishClientId = "acme-corp",
                    DailyIntakeSasProtected = sas,
                });
                Assert.Contains(secretPart, File.ReadAllText(path), StringComparison.Ordinal);

                var loaded = PortalPublishRunner.LoadLocalSettings();

                // On disk: wrapped, and the plaintext is gone from the file.
                var onDisk = ConfigFileHelper.Load<PortalPublishRunner.PortalLocalSettings>(path);
                Assert.True(CredentialProtector.IsEncrypted(onDisk.DailyIntakeSasProtected));
                Assert.DoesNotContain(secretPart, File.ReadAllText(path), StringComparison.Ordinal);

                // Still the same credential, and the rest of the store survived the rewrite.
                Assert.Equal(sas, PortalPublishRunner.UnprotectIntakeSas(onDisk));
                Assert.Equal("acme-corp", onDisk.DailyPublishClientId);

                // The object handed to the caller matches the file, not the pre-repair value.
                Assert.Equal(onDisk.DailyIntakeSasProtected, loaded.DailyIntakeSasProtected);

                // A second load has nothing to repair and must not rewrite the file.
                var bytes = File.ReadAllBytes(path);
                PortalPublishRunner.LoadLocalSettings();
                Assert.Equal(bytes, File.ReadAllBytes(path));
            });
        }

        [Fact]
        public void AnUnsetCredential_IsNotConjuredByALoad()
        {
            OverTheSettingsFile(path =>
            {
                ResetTheOncePerProcessAttempt();
                ConfigFileHelper.Save(path, new PortalPublishRunner.PortalLocalSettings { DailyPublishClientId = "acme-corp" });
                var bytes = File.ReadAllBytes(path);

                Assert.Equal("", PortalPublishRunner.LoadLocalSettings().DailyIntakeSasProtected);
                Assert.Equal(bytes, File.ReadAllBytes(path));
            });
        }

        [Fact]
        public void SavingABlankSas_StillClearsTheStoredCredential()
        {
            // PINNED, not fixed: /portal-status treats a blank field as "clear it" and says so
            // ("Intake credential cleared."). It is the reason the standing instruction is never to
            // click Save on that page with the field empty. If a later round makes blank a no-op,
            // this test is where that decision has to be made deliberately rather than by accident.
            OverTheSettingsFile(path =>
            {
                ResetTheOncePerProcessAttempt();
                ConfigFileHelper.Save(path, new PortalPublishRunner.PortalLocalSettings
                {
                    DailyIntakeSasProtected = PortalPublishRunner.ProtectIntakeSas(
                        "https://acct.blob.core.windows.net/daily-acme-corp?sp=cw&sig=LIVE"),
                });

                Assert.Equal(StoreWriteOutcome.Saved, PortalPublishRunner.SaveIntakeSas("   "));

                Assert.Equal("", ConfigFileHelper
                    .Load<PortalPublishRunner.PortalLocalSettings>(path).DailyIntakeSasProtected);
            });
        }

        [Fact]
        public void MissingPortalStore_SavesBecauseThatIsAFreshInstall()
        {
            // Missing is NOT damage. Refusing the first save would make onboarding impossible, and
            // this is the assertion that stops a later round widening the predicate to catch it.
            OverTheSettingsFile(path =>
            {
                if (File.Exists(path)) File.Delete(path);

                Assert.Equal(StoreWriteOutcome.Saved,
                    PortalPublishRunner.SaveLocalSettings(new PortalPublishRunner.PortalLocalSettings
                    {
                        DailyPublishClientId = "first-run",
                    }));

                Assert.True(File.Exists(path));
            });
        }
    }
}
