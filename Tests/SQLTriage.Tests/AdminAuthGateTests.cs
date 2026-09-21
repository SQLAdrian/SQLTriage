/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// The admin write path used to be OPEN BY DEFAULT: AdminAuth ships with an empty Hash/Salt
/// and the old AdminAuthService read "no password" as "everyone is an admin".
///
/// Adrian's ruling (2026-07-19): fail closed on NEW installs only; existing installs must
/// keep working, but must be told loudly that the gate is unset.
///
/// These tests pin all three halves of that: new install is closed, existing install is not
/// locked out, and the unprotected-but-open state is visibly flagged.
/// </summary>
public class AdminAuthGateTests : IDisposable
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

    /// <summary>A state directory with no prior-run artefacts — a genuinely fresh install.</summary>
    private string NewInstallStateDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqltriage-newinstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// A state directory carrying an artefact only a prior run of the app could have written.
    /// This is the durable on-disk evidence the discriminator keys on.
    /// </summary>
    private string ExistingInstallStateDir()
    {
        var dir = NewInstallStateDir();
        File.WriteAllText(Path.Combine(dir, "user-settings.json"), "{\"RefreshIntervalSeconds\":15}");
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

    private static AdminAuthService Auth(string stateDir, string? hash = null, string? salt = null,
                                         string? configPath = null) =>
        new(Config(hash, salt), new InstallProvenanceService(stateDir), configPath);

    /// <summary>
    /// Lays down an appsettings.json in its OWN directory — deliberately NOT the state directory,
    /// because any file in the state directory is prior-run evidence and would flip the verdict.
    /// (The first draft of these tests did exactly that and two of them failed, which is how we
    /// know the probe actually reads the directory.)
    /// </summary>
    private string AppSettingsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqltriage-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, """
        {
          "AdminAuth": { "Hash": "", "Salt": "" },
          "AllowedHosts": "*",
          "AppSettings": { "QueryTimeoutSeconds": 30 }
        }
        """);
        return path;
    }

    // ── 1. New install: the gate is CLOSED ────────────────────────────────

    [Fact]
    public void NewInstall_WithEmptyAdminAuth_IsNotUnlocked_AndRequiresSetup()
    {
        var auth = Auth(NewInstallStateDir());

        auth.HasPassword.Should().BeFalse("the shipped appsettings.json has empty Hash/Salt");
        auth.RequiresSetup.Should().BeTrue("a fresh install must set a password before admin actions");
        auth.IsUnlocked.Should().BeFalse("this is the defect: empty AdminAuth used to mean 'open'");
        auth.IsOpenUnprotected.Should().BeFalse("a new install is closed, not open-and-warning");
    }

    [Fact]
    public void NewInstall_Unlock_CannotBeBypassedByAnyPassword()
    {
        var auth = Auth(NewInstallStateDir());

        auth.Unlock("").Should().BeFalse();
        auth.Unlock("anything").Should().BeFalse();
        auth.IsUnlocked.Should().BeFalse("Unlock() must not open a gate that has no password to check");
    }

    [Fact]
    public void NewInstall_BecomesUsable_OnlyAfterAPasswordIsSet()
    {
        var auth = Auth(NewInstallStateDir(), configPath: AppSettingsFile());

        auth.IsUnlocked.Should().BeFalse();

        auth.SetInitialPassword("correct-horse").Should().BeNull("the password meets the length rule");

        auth.HasPassword.Should().BeTrue();
        auth.RequiresSetup.Should().BeFalse();
        auth.IsOpenUnprotected.Should().BeFalse();
        auth.IsUnlocked.Should().BeTrue("setting the password unlocks the session that set it");

        // …and the gate genuinely closes behind it.
        auth.Lock();
        auth.IsUnlocked.Should().BeFalse();
        auth.Unlock("wrong").Should().BeFalse();
        auth.Unlock("correct-horse").Should().BeTrue();
    }

    [Fact]
    public void SetInitialPassword_RejectsAPasswordShorterThanTheMinimum()
    {
        var auth = Auth(NewInstallStateDir(), configPath: AppSettingsFile());

        var tooShort = new string('a', AdminAuthService.MinimumPasswordLength - 1);
        auth.SetInitialPassword(tooShort).Should().NotBeNull("short passwords are refused");
        auth.HasPassword.Should().BeFalse();
        auth.IsUnlocked.Should().BeFalse("a refused setup must leave the gate closed");
    }

    // ── 2. Existing install: NOT locked out ───────────────────────────────

    [Fact]
    public void ExistingInstall_WithEmptyAdminAuth_IsNotLockedOut()
    {
        var auth = Auth(ExistingInstallStateDir());

        auth.HasPassword.Should().BeFalse();
        auth.RequiresSetup.Should().BeFalse("an upgrade must never lock a live client out");
        auth.IsUnlocked.Should().BeTrue("the pre-upgrade behaviour is preserved");
    }

    [Fact]
    public void ExistingInstall_WithEmptyAdminAuth_SurfacesTheUnprotectedWarning()
    {
        var auth = Auth(ExistingInstallStateDir());

        auth.IsOpenUnprotected.Should().BeTrue(
            "an open gate must be visible; AdminGuard renders its persistent banner off this flag");
    }

    [Fact]
    public void ExistingInstall_CanCloseItsOwnGate()
    {
        var auth = Auth(ExistingInstallStateDir(), configPath: AppSettingsFile());

        auth.IsOpenUnprotected.Should().BeTrue();
        auth.SetInitialPassword("correct-horse").Should().BeNull();
        auth.IsOpenUnprotected.Should().BeFalse("the warning must disappear once the gate is closed");
        auth.Lock();
        auth.IsUnlocked.Should().BeFalse();
    }

    // ── 3. A configured password behaves the same on both provenances ─────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfiguredPassword_GatesRegardlessOfProvenance(bool existingInstall)
    {
        var stateDir = existingInstall ? ExistingInstallStateDir() : NewInstallStateDir();
        var (hash, salt) = AdminAuthService.HashPassword("s3cret-passphrase");
        var auth = Auth(stateDir, hash, salt);

        auth.HasPassword.Should().BeTrue();
        auth.RequiresSetup.Should().BeFalse();
        auth.IsOpenUnprotected.Should().BeFalse();
        auth.IsUnlocked.Should().BeFalse("a configured password is always required");
        auth.Unlock("wrong").Should().BeFalse();
        auth.Unlock("s3cret-passphrase").Should().BeTrue();
    }

    // ── 4. The discriminator itself ───────────────────────────────────────

    [Fact]
    public void Provenance_EmptyStateDirectory_IsANewInstall()
    {
        new InstallProvenanceService(NewInstallStateDir()).IsExistingInstall.Should().BeFalse();
    }

    [Fact]
    public void Provenance_AbsentStateDirectory_IsANewInstall()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqltriage-absent-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        new InstallProvenanceService(dir).IsExistingInstall.Should().BeFalse();
    }

    [Theory]
    [InlineData("user-settings.json")]
    [InlineData("portal-settings.json")]
    [InlineData("public-profile.json")]
    public void Provenance_AnyPriorRunArtefact_IsAnExistingInstall(string artefact)
    {
        var dir = NewInstallStateDir();
        File.WriteAllText(Path.Combine(dir, artefact), "{}");

        new InstallProvenanceService(dir).IsExistingInstall.Should().BeTrue(
            "{0} can only have been written by a previous run of the application", artefact);
    }

    [Fact]
    public void Provenance_VerdictIsStampedAndDurable()
    {
        var dir = NewInstallStateDir();
        new InstallProvenanceService(dir).IsExistingInstall.Should().BeFalse();

        var marker = Path.Combine(dir, InstallProvenanceService.MarkerFileName);
        File.Exists(marker).Should().BeTrue("the verdict is written down so it is decided once");

        // State accrues afterwards — as it will on any new install that gets used.
        File.WriteAllText(Path.Combine(dir, "user-settings.json"), "{}");

        new InstallProvenanceService(dir).IsExistingInstall.Should().BeFalse(
            "a stamped 'new install' must not drift into 'existing' just because the app was used");
    }

    [Fact]
    public void Provenance_ExistingVerdictSurvivesARestart()
    {
        var dir = ExistingInstallStateDir();
        new InstallProvenanceService(dir).IsExistingInstall.Should().BeTrue();
        new InstallProvenanceService(dir).IsExistingInstall.Should().BeTrue();

        var marker = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, InstallProvenanceService.MarkerFileName)));
        marker.RootElement.GetProperty("existingInstall").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Provenance_MarkerIsNotItselfMistakenForPriorRunEvidence()
    {
        var dir = NewInstallStateDir();
        var svc = new InstallProvenanceService(dir);
        svc.IsExistingInstall.Should().BeFalse();

        // The marker file now exists. A second, freshly-constructed service must still say "new"
        // — if the probe counted its own marker as evidence this would flip to true.
        File.Delete(Path.Combine(dir, InstallProvenanceService.MarkerFileName));
        File.WriteAllText(Path.Combine(dir, InstallProvenanceService.MarkerFileName), "not json at all");
        new InstallProvenanceService(dir).IsExistingInstall.Should().BeFalse(
            "a corrupt marker re-derives the verdict, and the marker itself is not evidence");
    }

    // ── 5. The warning is actually RENDERED, not merely available as a flag ──

    /// <summary>
    /// IsOpenUnprotected being true is worthless if no markup reads it. AdminGuard.razor is
    /// copied next to the test binary so this asserts against the real shipped markup.
    /// </summary>
    [Fact]
    public void AdminGuard_RendersThePersistentWarning_WhenTheGateIsOpenAndUnprotected()
    {
        var markup = ReadAdminGuardMarkup();

        markup.Should().Contain("AdminAuth.IsOpenUnprotected",
            "the banner must be driven by the unprotected-gate flag");
        markup.Should().Contain("admin-guard-unprotected-banner",
            "the warning element must exist in the markup");
        markup.Should().Contain("role=\"alert\"",
            "the warning must be announced, not just visually present");
        markup.Should().MatchRegex("NOT protected|not protected",
            "the warning must say the installation is unprotected");
        markup.Should().Contain("Set admin password",
            "the warning must carry the action that fixes it");
    }

    [Fact]
    public void AdminGuard_RendersTheSetupFormAndWithholdsContent_OnANewInstall()
    {
        var markup = ReadAdminGuardMarkup();

        markup.Should().Contain("AdminAuth.RequiresSetup",
            "the setup branch must be driven by RequiresSetup");
        markup.Should().Contain("SetInitialPassword",
            "the setup form must call through to the real setup path");
        // ChildContent must be rendered only in the final else-branch, never alongside the
        // setup overlay — otherwise the admin controls leak out behind the gate.
        var setupIndex = markup.IndexOf("AdminAuth.RequiresSetup", StringComparison.Ordinal);
        var childIndex = markup.IndexOf("@ChildContent", StringComparison.Ordinal);
        setupIndex.Should().BeGreaterThan(-1);
        childIndex.Should().BeGreaterThan(setupIndex,
            "the gated content must come after the setup branch, inside the unlocked branch");
    }

    private static string ReadAdminGuardMarkup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", "AdminGuard.razor");
        File.Exists(path).Should().BeTrue(
            "AdminGuard.razor is copied to the test output by SQLTriage.Tests.csproj; " +
            "if this fails the assertions below would vacuously pass");
        return File.ReadAllText(path);
    }

    // ── 6. Persisting the password must not damage appsettings.json ───────

    [Fact]
    public void PersistToAppSettings_WritesAdminAuth_AndPreservesEverySection()
    {
        var configPath = AppSettingsFile();

        AdminAuthService.PersistToAppSettings("HASH==", "SALT==", configPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;
        root.GetProperty("AdminAuth").GetProperty("Hash").GetString().Should().Be("HASH==");
        root.GetProperty("AdminAuth").GetProperty("Salt").GetString().Should().Be("SALT==");
        root.GetProperty("AllowedHosts").GetString().Should().Be("*", "unrelated sections survive");
        root.GetProperty("AppSettings").GetProperty("QueryTimeoutSeconds").GetInt32().Should().Be(30,
            "nested unrelated sections survive verbatim");
    }

}
