/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Honesty-hunt 2026-08-25, Platform lane, platform-r1-07. <c>IsDbatoolsAvailable</c> used to be
/// true whenever the <c>dbatools</c> folder merely EXISTED. <c>DownloadDbatoolsAsync</c> creates
/// that folder before it runs Save-Module, and a failed download left it behind empty — so the app
/// rendered a green "dbatools module found" tick with the Download (retry) button hidden. The
/// availability check now reads contents: an empty folder is the failed-download debris it rejects.
/// </summary>
public class PowerShellDbatoolsAvailabilityTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "sqlt-dbatools-avail-" + Guid.NewGuid().ToString("N"));

    public PowerShellDbatoolsAvailabilityTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void An_empty_dbatools_folder_is_not_reported_as_the_module()
    {
        // THE DEFECT, pinned: the old check (Directory.Exists) returned true here, driving the green
        // tick over a failed download. Revert the check to folder-existence and this goes red.
        var folder = Path.Combine(_scratch, "dbatools");
        Directory.CreateDirectory(folder);

        PowerShellService.DbatoolsFolderHasModule(folder).Should().BeFalse(
            "an empty folder is what a failed Save-Module leaves behind, not an installed module");
    }

    [Fact]
    public void A_non_empty_dbatools_folder_is_reported_as_the_module()
    {
        // The CONTROL: Save-Module lays the module out beneath the folder, so a non-empty folder is
        // the real thing. Without this the fix could pass by always returning false.
        var folder = Path.Combine(_scratch, "dbatools");
        Directory.CreateDirectory(Path.Combine(folder, "dbatools", "2.1.0"));
        File.WriteAllText(Path.Combine(folder, "dbatools", "2.1.0", "dbatools.psd1"), "@{}");

        PowerShellService.DbatoolsFolderHasModule(folder).Should().BeTrue(
            "a folder that actually holds the module must still read as available");
    }

    [Fact]
    public void A_missing_dbatools_folder_is_not_reported_as_the_module()
    {
        var folder = Path.Combine(_scratch, "does-not-exist");

        PowerShellService.DbatoolsFolderHasModule(folder).Should().BeFalse(
            "a folder that was never created cannot be an installed module");
    }
}
