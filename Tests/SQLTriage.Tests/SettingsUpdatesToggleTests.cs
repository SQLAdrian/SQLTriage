/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// 4a: Settings.razor's Updates:Enabled control used to be a readonly IConfiguration read with
    /// no write path at all — the kill-switch was config-file-only, with no operator toggle. Pins
    /// the round trip of the write it now does, end to end through the exact class that reads the
    /// value back (<see cref="AutoUpdateService"/>).
    ///
    /// <see cref="SQLTriage.Pages.Settings.WriteUpdatesEnabledToAppSettings"/> is an internal test
    /// seam (InternalsVisibleTo SQLTriage.Tests, same convention as
    /// <c>ScheduledTaskEngine.ResolveTargets</c>) — standing up the full Settings Blazor component
    /// (15+ injected services) for a file write would be disproportionate to what changed.
    /// </summary>
    public class SettingsUpdatesToggleTests : IDisposable
    {
        private readonly string _path =
            Path.Combine(Path.GetTempPath(), $"sqltriage-appsettings-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch { /* best-effort cleanup */ }
        }

        private static IConfiguration ConfigFrom(string path) =>
            new ConfigurationBuilder().AddJsonFile(path).Build();

        [Fact]
        public void Toggle_RoundTrips_ThroughAppSettingsJson_AndLeavesOtherKeysUntouched()
        {
            File.WriteAllText(_path, "{\"Updates\":{\"Enabled\":true},\"Other\":{\"Untouched\":42}}");

            SQLTriage.Pages.Settings.WriteUpdatesEnabledToAppSettings(_path, false);

            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            Assert.False(doc.RootElement.GetProperty("Updates").GetProperty("Enabled").GetBoolean());
            // A targeted Section() write, not a rewrite — every other key survives byte-for-byte.
            Assert.Equal(42, doc.RootElement.GetProperty("Other").GetProperty("Untouched").GetInt32());

            SQLTriage.Pages.Settings.WriteUpdatesEnabledToAppSettings(_path, true);
            using var doc2 = JsonDocument.Parse(File.ReadAllText(_path));
            Assert.True(doc2.RootElement.GetProperty("Updates").GetProperty("Enabled").GetBoolean());
        }

        [Fact]
        public void Toggle_CreatesTheUpdatesSection_WhenAbsentFromTheFile()
        {
            // A shipped appsettings.json with no "Updates" section at all (the implicit-default-
            // true path — AutoUpdateService reads GetValue("Updates:Enabled", true)) must still be
            // writable; Section() creates the section rather than requiring it pre-exist.
            File.WriteAllText(_path, "{\"Other\":\"value\"}");

            SQLTriage.Pages.Settings.WriteUpdatesEnabledToAppSettings(_path, false);

            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            Assert.False(doc.RootElement.GetProperty("Updates").GetProperty("Enabled").GetBoolean());
        }

        [Fact]
        public void Toggle_RoundTrips_ThroughTheClassThatActuallyReadsIt()
        {
            // The end-to-end proof: write via the same path Settings uses, then construct
            // AutoUpdateService — the class whose _updatesEnabled field is the real kill-switch —
            // off the written file and confirm it agrees.
            File.WriteAllText(_path, "{\"Updates\":{\"Enabled\":true}}");
            SQLTriage.Pages.Settings.WriteUpdatesEnabledToAppSettings(_path, false);

            var svc = new AutoUpdateService(NullLogger<AutoUpdateService>.Instance, ConfigFrom(_path));

            Assert.False(svc.UpdatesEnabled);
        }
    }
}
