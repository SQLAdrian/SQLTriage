/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.Json;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins the settings-file write contract.
    ///
    /// The defect these cover: <see cref="UserSettingsService.SaveSettings"/> serialised the
    /// UserSettings POCO over the whole of user-settings.json, so any top-level section the POCO
    /// does not model was deleted. The only such section today is "License" (written directly by
    /// UserSettingsLicenseExtensions), which meant changing any setting deactivated the install
    /// and forced a re-activation from the key / 24-word phrase.
    ///
    /// Every test here builds the service over a temp file through
    /// <c>UserSettingsService</c>'s internal path seam, so the real user's settings — and
    /// licence — are never opened at all. This used to reflect the private
    /// <c>_settingsFilePath</c> field AFTER construction, which meant construction still read the
    /// operator's real profile once; the seam removes that last read (2026-08-04).
    /// </summary>
    public class SettingsPersistenceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _settingsPath;

        public SettingsPersistenceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "settings-persist-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _settingsPath = Path.Combine(_tempDir, "user-settings.json");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Builds a service whose settings file is <paramref name="filePath"/>, using the path seam
        /// so the real profile is never bound — not even for the constructor's initial read.
        /// </summary>
        private static UserSettingsService ServiceOver(string filePath)
            => new UserSettingsService(filePath);

        private static JsonElement Root(string filePath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            return doc.RootElement.Clone();
        }

        // ── The licence-wipe regression ──────────────────────────────────────

        [Fact]
        public void SaveSettings_PreservesLicenseSectionWrittenByAnotherWriter()
        {
            // A file in the shape the app produces: modelled settings plus the License section.
            File.WriteAllText(_settingsPath, """
            {
              "ZoomLevel": 125,
              "NoPantsMode": true,
              "License": {
                "ClientName": "TEST_CLIENT_NEVER_PROD",
                "EncryptedKey": "AQIDBA=="
              }
            }
            """);

            var svc = ServiceOver(_settingsPath);
            svc.SetLastSeenVersion("9.9.9-test");   // any Set* — they all call SaveSettings

            var root = Root(_settingsPath);
            Assert.True(root.TryGetProperty("License", out var license),
                "SaveSettings deleted the License section — this is the re-activation regression.");
            Assert.Equal("TEST_CLIENT_NEVER_PROD", license.GetProperty("ClientName").GetString());
            Assert.Equal("AQIDBA==", license.GetProperty("EncryptedKey").GetString());

            // The modelled settings must still round-trip, and the new value must be written.
            Assert.Equal(125, root.GetProperty("ZoomLevel").GetInt32());
            Assert.True(root.GetProperty("NoPantsMode").GetBoolean());
            Assert.Equal("9.9.9-test", root.GetProperty("LastSeenVersion").GetString());
        }

        [Fact]
        public void SaveSettings_PicksUpALicenseWrittenAfterTheServiceLoaded()
        {
            // Activation order in the real app: the service is already running when the licence
            // is written straight to the file, so the in-memory copy has never seen it.
            File.WriteAllText(_settingsPath, """{ "ZoomLevel": 125 }""");
            var svc = ServiceOver(_settingsPath);

            var withLicence = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement;
            Assert.False(withLicence.TryGetProperty("License", out _)); // control: not there yet

            File.WriteAllText(_settingsPath, """
            {
              "ZoomLevel": 125,
              "License": { "ClientName": "LATE_WRITER", "EncryptedKey": "BQYHCA==" }
            }
            """);

            svc.SetLastSeenVersion("9.9.9-test");

            var root = Root(_settingsPath);
            Assert.True(root.TryGetProperty("License", out var license));
            Assert.Equal("LATE_WRITER", license.GetProperty("ClientName").GetString());
        }

        [Fact]
        public void SaveSettings_DoesNotResurrectARemovedLicense()
        {
            // The mirror of the test above: once ClearLicense has taken the section out of the
            // file, a later save must not put a stale in-memory copy back.
            File.WriteAllText(_settingsPath, """
            {
              "ZoomLevel": 125,
              "License": { "ClientName": "TO_BE_CLEARED", "EncryptedKey": "AQIDBA==" }
            }
            """);

            var svc = ServiceOver(_settingsPath);
            svc.SetLastSeenVersion("first-save");                      // loads + rewrites with the licence
            Assert.True(Root(_settingsPath).TryGetProperty("License", out _)); // control

            File.WriteAllText(_settingsPath, """{ "ZoomLevel": 125 }""");  // stands in for ClearLicense
            svc.SetLastSeenVersion("second-save");

            Assert.False(Root(_settingsPath).TryGetProperty("License", out _),
                "A removed License section came back — the save is carrying stale foreign data.");
        }

        [Fact]
        public void ResetToDefaults_KeepsTheLicense()
        {
            File.WriteAllText(_settingsPath, """
            {
              "ZoomLevel": 125,
              "License": { "ClientName": "SURVIVES_RESET", "EncryptedKey": "AQIDBA==" }
            }
            """);

            var svc = ServiceOver(_settingsPath);
            svc.ResetToDefaults();

            var root = Root(_settingsPath);
            Assert.Equal(150, root.GetProperty("ZoomLevel").GetInt32());   // control: settings did reset
            Assert.True(root.TryGetProperty("License", out var license),
                "Resetting preferences must not deactivate the install.");
            Assert.Equal("SURVIVES_RESET", license.GetProperty("ClientName").GetString());
        }

        // ── Clamp-on-read ────────────────────────────────────────────────────

        [Fact]
        public void Getters_ClampValuesThatWereEditedIntoTheFileOutOfRange()
        {
            // A burst multiplier below 1.0 or a concurrency of 0 reaches SemaphoreSlim and throws
            // during construction; the setters clamped but nothing clamped what was already stored.
            File.WriteAllText(_settingsPath, """
            {
              "BurstConcurrencyMultiplier": 0.1,
              "BurstDurationSec": 0,
              "MaxHeavyConcurrent": 0,
              "MaxLightConcurrent": 0,
              "MaxConcurrentPerServer": 0,
              "ChartDataPointCap": 0,
              "AuditMaxConcurrentPerInstance": 0
            }
            """);

            var svc = ServiceOver(_settingsPath);

            Assert.Equal(1.0, svc.GetBurstConcurrencyMultiplier());
            Assert.Equal(10, svc.GetBurstDurationSec());
            Assert.Equal(1, svc.GetMaxHeavyConcurrent());
            Assert.Equal(2, svc.GetMaxLightConcurrent());
            Assert.Equal(1, svc.GetMaxConcurrentPerServer());
            Assert.Equal(500, svc.GetChartDataPointCap());
            Assert.Equal(UserSettingsService.AuditConcurrencyMin, svc.GetAuditMaxConcurrentPerInstance());
        }

        [Fact]
        public void Getters_LeaveInRangeValuesAlone()
        {
            // Control for the test above: without this, every assertion there would also pass if
            // the getters returned a constant.
            File.WriteAllText(_settingsPath, """
            {
              "BurstConcurrencyMultiplier": 2.5,
              "BurstDurationSec": 90,
              "MaxHeavyConcurrent": 6,
              "MaxLightConcurrent": 12,
              "MaxConcurrentPerServer": 4,
              "ChartDataPointCap": 3000,
              "AuditMaxConcurrentPerInstance": 5
            }
            """);

            var svc = ServiceOver(_settingsPath);

            Assert.Equal(2.5, svc.GetBurstConcurrencyMultiplier());
            Assert.Equal(90, svc.GetBurstDurationSec());
            Assert.Equal(6, svc.GetMaxHeavyConcurrent());
            Assert.Equal(12, svc.GetMaxLightConcurrent());
            Assert.Equal(4, svc.GetMaxConcurrentPerServer());
            Assert.Equal(3000, svc.GetChartDataPointCap());
            Assert.Equal(5, svc.GetAuditMaxConcurrentPerInstance());
        }

        // ── ConfigFileHelper: a bad file is quarantined, not silently discarded ──

        [Fact]
        public void Load_QuarantinesAnUnparseableFileAndReturnsDefaults()
        {
            var path = Path.Combine(_tempDir, "broken.json");
            File.WriteAllText(path, """{ "ZoomLevel": "not-an-int" }""");

            var loaded = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path);

            Assert.Equal(150, loaded.ZoomLevel); // defaults, as before
            var rejected = Directory.GetFiles(_tempDir, "broken.json.rejected-*");
            Assert.True(rejected.Length == 1,
                $"expected exactly one quarantine copy, found {rejected.Length}");
            Assert.Contains("not-an-int", File.ReadAllText(rejected[0]));
        }

        [Fact]
        public void Load_ToleratesTrailingCommasAndComments()
        {
            var path = Path.Combine(_tempDir, "lenient.json");
            File.WriteAllText(path, """
            {
              // hand-edited on a client server
              "ZoomLevel": 125,
              "NoPantsMode": true,
            }
            """);

            var loaded = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path);

            Assert.Equal(125, loaded.ZoomLevel);
            Assert.True(loaded.NoPantsMode);
            Assert.Empty(Directory.GetFiles(_tempDir, "lenient.json.rejected-*"));
        }

        [Fact]
        public void Load_OfAGoodFileLeavesNoQuarantineCopy()
        {
            // Control for the quarantine test: proves the *.rejected-* assertion can fail.
            var path = Path.Combine(_tempDir, "good.json");
            File.WriteAllText(path, """{ "ZoomLevel": 125 }""");

            var loaded = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path);

            Assert.Equal(125, loaded.ZoomLevel);
            Assert.Empty(Directory.GetFiles(_tempDir, "good.json.rejected-*"));
        }
    }
}
