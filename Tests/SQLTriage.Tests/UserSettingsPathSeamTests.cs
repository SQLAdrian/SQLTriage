/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The settings-path seam a HOST can reach (2026-08-10).
    ///
    /// <para><b>What was closed.</b> <see cref="UserSettingsService"/>'s path override is
    /// <c>internal</c>, and both DI registrations
    /// (<c>ServiceCollectionExtensions.AddSharedServices</c> and
    /// <c>WindowsServiceHost.RegisterAllServices</c>) use the parameterless constructor. A RUNNING
    /// host could therefore only ever store licence activation in the live per-user install, so no
    /// agent could activate a bundle in a throwaway build output and render a licensed page — four
    /// gates in a row recorded that gap. <see cref="UserSettingsService.SettingsDirectoryVariable"/>
    /// gives a host a contained location.</para>
    ///
    /// <para><b>The claim these tests hold.</b> Absent variable = today's behaviour, byte for byte,
    /// guard included. That is asserted two ways: the resolved string is compared ORDINAL against an
    /// independently composed <c>%APPDATA%</c> path, and the parameterless constructor is shown still
    /// REFUSING that path under a test host — which is only observable if it truly resolved there.</para>
    ///
    /// <para>The environment variable is process-global. This assembly runs single-threaded with
    /// collection parallelism off (<c>xunit.runner.json</c>), and every test here restores the
    /// previous value in a finally.</para>
    /// </summary>
    public class UserSettingsPathSeamTests
    {
        /// <summary>The real per-user path, composed here rather than read from the code under test.</summary>
        private static string RealProfilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SQLTriage",
            "user-settings.json");

        private static void WithVariable(string? value, Action body)
        {
            string? previous = Environment.GetEnvironmentVariable(UserSettingsService.SettingsDirectoryVariable);
            Environment.SetEnvironmentVariable(UserSettingsService.SettingsDirectoryVariable, value);
            try { body(); }
            finally { Environment.SetEnvironmentVariable(UserSettingsService.SettingsDirectoryVariable, previous); }
        }

        // ── 1. ABSENT = the production default, character for character ──────────────────────
        [Fact]
        public void An_absent_variable_resolves_the_real_user_profile_path_exactly()
        {
            WithVariable(null, () =>
                UserSettingsService.ResolveSettingsFilePath()
                    .Should().Be(RealProfilePath()));
        }

        // The same claim from the other side, and the one that cannot be satisfied by a string
        // comparison alone: the parameterless constructor still walks into the guard, which only
        // fires on the real profile path. If the seam had shifted the default anywhere, this stops
        // throwing.
        [Fact]
        public void An_absent_variable_still_refuses_the_real_profile_under_a_test_host()
        {
            WithVariable(null, () =>
            {
                var act = () => new UserSettingsService();  // real-profile-lint:allow -- the throw IS the assertion
                act.Should().Throw<InvalidOperationException>()
                    .WithMessage("*" + RealProfilePath() + "*");
            });
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void A_blank_variable_is_the_same_as_an_absent_one(string value)
        {
            WithVariable(value, () =>
                UserSettingsService.ResolveSettingsFilePath()
                    .Should().Be(RealProfilePath()));
        }

        // ── 2. SET = a contained settings file, and the real profile untouched ───────────────
        [Fact]
        public void A_named_directory_becomes_this_hosts_settings_file()
        {
            string contained = Path.Combine(Path.GetTempPath(), "sqlt-seam-" + Guid.NewGuid().ToString("N"));
            string? realBefore = HashOrNull(RealProfilePath());
            try
            {
                WithVariable(contained, () =>
                {
                    UserSettingsService.ResolveSettingsFilePath()
                        .Should().Be(Path.Combine(contained, "user-settings.json"));

                    // The PRODUCTION constructor, which is the one every host uses.
                    var settings = new UserSettingsService();  // real-profile-lint:allow -- the variable is set above
                    settings.SettingsFilePath.Should().Be(Path.Combine(contained, "user-settings.json"));

                    // A write lands there and nowhere else.
                    settings.SetRefreshInterval(42);
                    File.Exists(Path.Combine(contained, "user-settings.json")).Should().BeTrue();
                    new UserSettingsService().GetRefreshInterval().Should().Be(42);  // real-profile-lint:allow
                });

                HashOrNull(RealProfilePath()).Should().Be(realBefore,
                    "a contained host must not touch the operator's own settings file");
            }
            finally
            {
                try { Directory.Delete(contained, recursive: true); } catch { /* best effort */ }
            }
        }

        // ── 3. FAIL-SAFE: the seam cannot be used to sneak a test onto the real profile ──────
        // The guard is applied to the RESOLVED path, not to the default on the way past, so pointing
        // the variable back at the real profile directory is refused exactly as an unset variable is.
        [Fact]
        public void Pointing_the_variable_at_the_real_profile_still_trips_the_guard()
        {
            string realDirectory = Path.GetDirectoryName(RealProfilePath())!;
            WithVariable(realDirectory, () =>
            {
                var act = () => new UserSettingsService();  // real-profile-lint:allow -- the throw IS the assertion
                act.Should().Throw<InvalidOperationException>()
                    .WithMessage("*" + RealProfilePath() + "*");
            });
        }

        // A value that cannot compose into a path is not a reason to fail a host start. It resolves
        // to the default, which is the only fail-safe answer.
        [Fact]
        public void A_malformed_variable_falls_back_to_the_default()
        {
            WithVariable("\0not-a-path", () =>
                UserSettingsService.ResolveSettingsFilePath()
                    .Should().Be(RealProfilePath()));
        }

        // ── 3b. THE MALFORMED CLASS Path.GetFullPath ACCEPTS (2026-08-11) ───────────────────
        // The case above is the ONLY one GetFullPath rejects, so on its own it proved the narrow
        // case while the prose generalised it. MEASURED before this fix: SQLTRIAGE_SETTINGS_DIR
        // = "|||<>:invalid" composed cleanly, and a plain console host resolving this service
        // through AddSharedServices died at the composition root with an unhandled IOException out
        // of Directory.CreateDirectory — "a host that cannot read the variable must still start"
        // was false for every value of this shape. Same for a variable naming an existing FILE.
        //
        // Both are asserted TWICE, because a resolved string is not a started host: the resolution
        // lands on the default, AND the production constructor reaches the guard, which fires only
        // on the real profile path and only under a test host. A constructor that still crashed
        // would throw IOException here instead, and a resolution that had drifted anywhere else
        // would not throw at all.
        [Fact]
        public void A_malformed_but_composable_variable_falls_back_to_the_default()
        {
            WithVariable("|||<>:invalid", () =>
            {
                UserSettingsService.ResolveSettingsFilePath().Should().Be(RealProfilePath());

                var act = () => new UserSettingsService();  // real-profile-lint:allow -- the throw IS the assertion
                act.Should().Throw<InvalidOperationException>()
                    .WithMessage("*" + RealProfilePath() + "*");
            });
        }

        [Fact]
        public void A_variable_naming_an_existing_file_falls_back_to_the_default()
        {
            string file = Path.Combine(Path.GetTempPath(), "sqlt-seam-file-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, "this is a file, not a directory");
            try
            {
                WithVariable(file, () =>
                {
                    UserSettingsService.ResolveSettingsFilePath().Should().Be(RealProfilePath());

                    var act = () => new UserSettingsService();  // real-profile-lint:allow -- the throw IS the assertion
                    act.Should().Throw<InvalidOperationException>()
                        .WithMessage("*" + RealProfilePath() + "*");
                });

                // The fallback is a fallback, not a rewrite: the file the variable named is untouched.
                File.ReadAllText(file).Should().Be("this is a file, not a directory");
            }
            finally
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }

        // The fallback must not be vacuous. Resolution now PREPARES the directory it returns, so the
        // control that matters is that a usable location is still preferred over the default after
        // that preparation — measured on a directory that does not exist yet, which is the shape a
        // fresh host passes.
        [Fact]
        public void A_directory_that_does_not_exist_yet_is_still_this_hosts_settings_file()
        {
            string contained = Path.Combine(Path.GetTempPath(), "sqlt-seam-new-" + Guid.NewGuid().ToString("N"));
            Directory.Exists(contained).Should().BeFalse();
            try
            {
                WithVariable(contained, () =>
                    UserSettingsService.ResolveSettingsFilePath()
                        .Should().Be(Path.Combine(contained, "user-settings.json")));
            }
            finally
            {
                try { Directory.Delete(contained, recursive: true); } catch { /* best effort */ }
            }
        }

        // ── 4. EVERY host composition root, resolved (GATE-02) ──────────────────────────────
        // The lesson from GATE-02 is that a feature registered per host is a feature one host does
        // not have. The seam here lives INSIDE the constructor, so there is no per-root wiring to
        // keep in step — but "resolvable implies correctly rooted" is a claim about behaviour, so
        // both real composition roots are built and resolved rather than reasoned about.
        [Theory]
        [InlineData("shared")]   // App.xaml.cs and ServerModeService both compose AddSharedServices
        [InlineData("service")]  // WindowsServiceHost.RegisterAllServices (--server / --service)
        public void Every_host_composition_root_binds_the_contained_settings_file(string root)
        {
            string contained = Path.Combine(Path.GetTempPath(), "sqlt-seam-di-" + Guid.NewGuid().ToString("N"));
            string? realBefore = HashOrNull(RealProfilePath());
            try
            {
                WithVariable(contained, () =>
                {
                    var services = new ServiceCollection();
                    services.AddLogging();
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>())
                        .Build();
                    services.AddSingleton<IConfiguration>(configuration);

                    if (root == "shared") services.AddSharedServices(configuration);
                    else WindowsServiceHost.RegisterAllServices(services, configuration);

                    using var provider = services.BuildServiceProvider();

                    string expected = Path.Combine(contained, "user-settings.json");
                    provider.GetRequiredService<UserSettingsService>().SettingsFilePath
                        .Should().Be(expected);

                    // The interface registration is a factory over the same singleton on the shared
                    // root; the service root registers only the concrete type, so it is asked for
                    // only where it exists.
                    var byInterface = provider.GetService<IUserSettingsService>();
                    if (byInterface is UserSettingsService concrete)
                        concrete.SettingsFilePath.Should().Be(expected);
                });

                HashOrNull(RealProfilePath()).Should().Be(realBefore);
            }
            finally
            {
                try { Directory.Delete(contained, recursive: true); } catch { /* best effort */ }
            }
        }

        /// <summary>SHA-256 of a file, or null when it is not there. Content never leaves this method.</summary>
        private static string? HashOrNull(string path)
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
