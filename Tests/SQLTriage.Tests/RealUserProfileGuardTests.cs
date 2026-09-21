/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The test suite must not read or write the operator's real per-user profile.
    ///
    /// <para><b>The defect.</b> Measured 2026-08-04: fourteen call sites across seven test files
    /// constructed <see cref="UserSettingsService"/> with its parameterless constructor, binding the
    /// developer's real <c>%APPDATA%\SQLTriage\user-settings.json</c>. Two of those files called
    /// <c>ClearLicense()</c> / <c>TryActivate(...)</c>, which WRITE. A fixture licence named
    /// <c>TEST_CLIENT_NEVER_PROD</c> was written into the real file and cleared again; the file was
    /// observed oscillating between two states for a whole session, unnoticed for weeks.</para>
    ///
    /// <para><b>Instrument classes in this file, stated plainly.</b> The first three tests are
    /// BEHAVIOURAL — they construct real objects, drive real writers, and read bytes back off disk.
    /// The last one is a LINT and is labelled as such on its own doc comment. That split is
    /// deliberate: this lane has watched five static instruments be defeated, the last by an
    /// ordinary English sentence, so nothing here rests on a source scan alone.</para>
    /// </summary>
    public sealed class RealUserProfileGuardTests : IDisposable
    {
        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "sqlt-profile-guard-" + Guid.NewGuid().ToString("N"));

        private string TempSettings => Path.Combine(_tempDir, "user-settings.json");

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }

        /// <summary>The real profile path, resolved exactly as the production constructor resolves it.</summary>
        private static string RealProfilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SQLTriage",
            "user-settings.json");

        private static string? FingerprintOf(string path)
        {
            if (!File.Exists(path)) return null;
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
        }

        // ── 1. The chokepoint itself ────────────────────────────────────────────────────────

        /// <summary>
        /// BEHAVIOURAL. The parameterless constructor refuses under a test host — which is what this
        /// process is — so no future test can bind the real profile by writing the obvious thing.
        /// This is the guard that cannot be defeated by rewording, because it is not reading words.
        /// </summary>
        [Fact]
        public void The_parameterless_constructor_refuses_to_bind_the_real_profile_under_a_test_host()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new UserSettingsService());

            // The message must be actionable: it has to name the seam, or the next person just
            // deletes the guard.
            Assert.Contains("user-settings.json", ex.Message);
            Assert.Contains("new UserSettingsService(", ex.Message);
        }

        /// <summary>
        /// BEHAVIOURAL. Same chokepoint on the other service that defaults to the SAME real
        /// directory. Enumerating the category is the point: the previous round of this wave proved
        /// a list of "one missed file" was actually four.
        /// </summary>
        [Fact]
        public void InstallProvenanceService_also_refuses_its_default_real_state_directory()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new InstallProvenanceService());
            Assert.Contains("InstallProvenanceService", ex.Message);
        }

        /// <summary>
        /// BEHAVIOURAL. An explicit path is never second-guessed — the guard fires on the DEFAULT
        /// path only. Without this, the guard above could be "passing" because construction is
        /// broken outright, and every seam call site would be failing for the same reason.
        /// </summary>
        [Fact]
        public void An_explicit_path_is_accepted_and_owned()
        {
            var svc = new UserSettingsService(TempSettings);
            svc.SetZoomLevel(125);

            Assert.True(File.Exists(TempSettings), "the explicit path must be the file that gets written");
            using var doc = JsonDocument.Parse(File.ReadAllText(TempSettings));
            Assert.Equal(125, doc.RootElement.GetProperty("ZoomLevel").GetInt32());
        }

        // ── 2. The silent-return shape: a SECOND copy of the path ───────────────────────────

        /// <summary>
        /// BEHAVIOURAL, and the most important test in this file.
        ///
        /// <para>Giving <see cref="UserSettingsService"/> a seam is only half the fix. The licence
        /// writer — <c>UserSettingsLicenseExtensions</c> — rewrites the SAME file through its own
        /// <c>JsonNode</c> writer, and until 2026-08-04 it took a <see cref="UserSettingsService"/>
        /// and DISCARDED it (the parameter was literally named <c>_</c>), re-deriving
        /// <c>%APPDATA%</c> for itself. So the seam would have moved the service's file and left
        /// every licence write landing on the operator's real profile: a green suite and a moving
        /// file, which is exactly the failure that went unnoticed for weeks.</para>
        ///
        /// <para>This drives the full licence lifecycle over a temp-path service and asserts BOTH
        /// halves: the licence lands in the temp file, and the real profile's bytes are unchanged
        /// across the whole thing. The second assertion is the one that survives a refactor,
        /// because it does not care HOW the path is derived — only where the bytes went.</para>
        /// </summary>
        [Fact]
        public void Licence_writes_follow_the_seam_and_leave_the_real_profile_byte_identical()
        {
            var before = FingerprintOf(RealProfilePath);

            var svc = new UserSettingsService(TempSettings);
            svc.SaveLicense("GUARD_TEST_CLIENT_NEVER_PROD", new byte[] { 1, 2, 3, 4 });

            // Landed in the temp file…
            var (name, key) = svc.GetSavedLicense();
            Assert.Equal("GUARD_TEST_CLIENT_NEVER_PROD", name);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, key);

            using (var doc = JsonDocument.Parse(File.ReadAllText(TempSettings)))
            {
                Assert.True(doc.RootElement.TryGetProperty("License", out var lic),
                    "the licence must be written to the seam's file, not somewhere else");
                Assert.Equal("GUARD_TEST_CLIENT_NEVER_PROD", lic.GetProperty("ClientName").GetString());
            }

            svc.ClearLicense();
            Assert.Null(svc.GetSavedLicense().ClientName);

            // …and the real profile never moved. Fingerprint, not existence: the observed defect
            // wrote the file and wrote it back, so only the bytes tell the truth.
            var after = FingerprintOf(RealProfilePath);
            Assert.True(before == after,
                "A licence write reached the operator's REAL profile at " + RealProfilePath
                + ". Some writer is re-deriving %APPDATA% instead of asking UserSettingsService "
                + "for its SettingsFilePath — that is the 2026-08-04 defect returning. "
                + $"sha256 before={before ?? "<absent>"} after={after ?? "<absent>"}");
        }

        // ── 3. The lint ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠ <b>THIS IS A LINT, NOT A GUARANTEE.</b> It greps test sources for a fixed literal.
        ///
        /// <para>It is here because it names the offending FILE and LINE, which the runtime
        /// chokepoint cannot do — the chokepoint throws from inside a constructor and the stack is
        /// often a helper. It is worth exactly that much and no more.</para>
        ///
        /// <para><b>Do not treat a green result here as proof that no test binds a real profile.</b>
        /// Trivially defeated by <c>var t = typeof(UserSettingsService); Activator.CreateInstance(t)</c>,
        /// by a factory helper, by a using alias, or by whitespace this pattern does not anticipate.
        /// Five static instruments have already fallen in this lane, the last to an ordinary English
        /// sentence. The real boundary is
        /// <c>RealUserProfileGuard.RefuseRealProfileUnderTest</c>, which runs at construction and
        /// does not read source at all — and the byte-level assertion in
        /// <see cref="Licence_writes_follow_the_seam_and_leave_the_real_profile_byte_identical"/>,
        /// which does not care how the path was derived.</para>
        /// </summary>
        /// <summary>
        /// The one escape, and it is deliberately narrow: the marker must sit on the SAME LINE as the
        /// construction, so allowing a line is a visible act in the diff rather than a file-level or
        /// folder-level exemption.
        ///
        /// <para>It exists because 2026-08-10 made the parameterless constructor a thing worth
        /// testing: <see cref="UserSettingsService.SettingsDirectoryVariable"/> lets a host bind a
        /// contained settings file, so a marked line is one of exactly two cases — a test that ASSERTS
        /// the guard throws (which is the guard working), or one that has set the variable first and
        /// asserts the bound path is the contained one. Neither can reach the real profile: the guard
        /// is applied to the RESOLVED path, so a variable pointing back at %APPDATA% throws too, and
        /// the byte-level assertion above is the instrument that does not care how the path was
        /// derived.</para>
        /// </summary>
        internal const string AllowMarker = "real-profile-lint:allow";

        [Fact]
        public void Lint_no_test_source_constructs_UserSettingsService_without_a_path()
        {
            var testsRoot = FindTestsRoot();
            if (testsRoot is null) return; // running from a bin drop without sources; the runtime guard still holds

            var offenders =
                (from file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
                 where !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    // This file quotes the pattern in prose, on purpose.
                    && !file.EndsWith(nameof(RealUserProfileGuardTests) + ".cs", StringComparison.Ordinal)
                 from line in File.ReadAllLines(file).Select((text, i) => (text, no: i + 1))
                 where line.text.Contains("new UserSettingsService()", StringComparison.Ordinal)
                    && !line.text.TrimStart().StartsWith("///", StringComparison.Ordinal)
                    && !line.text.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && !line.text.Contains(AllowMarker, StringComparison.Ordinal)
                 select $"{Path.GetFileName(file)}:{line.no}")
                .ToList();

            Assert.True(offenders.Count == 0,
                "These test sources construct UserSettingsService with no path, which binds the "
                + "operator's REAL %APPDATA%\\SQLTriage\\user-settings.json:\n  "
                + string.Join("\n  ", offenders)
                + "\nPass a temp path instead: new UserSettingsService(Path.Combine(tempDir, \"user-settings.json\")), "
                + "or mark the line " + AllowMarker + " if the parameterless constructor is the thing "
                + "under test.");
        }

        /// <summary>Walks up from the test binary to the Tests\SQLTriage.Tests source folder, if present.</summary>
        private static string? FindTestsRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "Tests", "SQLTriage.Tests");
                if (Directory.Exists(candidate)) return candidate;
                if (File.Exists(Path.Combine(dir.FullName, "SQLTriage.Tests.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
