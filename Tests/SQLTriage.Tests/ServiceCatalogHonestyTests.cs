/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 2 (2026-08-28) - the /services cards that asserted a fact from a state
// nobody measured.
//
//   pages-r1-02  The Update Service card fell through to a green "Up to date (signed)." for every
//                state that was not a staged or an available update. Two of those states are
//                "updates are disabled in configuration" - the SHIPPED DEFAULT, Updates:Enabled
//                = false in the tracked Config/appsettings.json - and "no check has run yet". So
//                a fresh install claimed a signature verification that never occurred, about a
//                release it had never contacted. /service-management, reading the same service,
//                said the honest thing for the same state.
//   pages-r1-06  ProbeWindowsService returned the SAME tuple from "genuinely not installed" and
//                "the probe failed" (access denied, SCM fault), and both rendered as the red
//                "Not installed (sc create via Service & Updates)." card. The refute pass proved
//                the exception TYPE cannot separate them: ServiceController raises
//                InvalidOperationException for both.
//
// There is no ServiceCatalog test file before this one.

using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class ServiceCatalogHonestyTests
{
    private readonly ITestOutputHelper _out;
    public ServiceCatalogHonestyTests(ITestOutputHelper output) => _out = output;

    // ── pages-r1-02: the update card ─────────────────────────────────────────

    [Fact]
    public void The_shipped_default_config_really_does_disable_updates()
    {
        // The premise the finding rests on, read from the tracked file rather than assumed: if
        // this ever flips, the "every fresh install shows it" reach changes with it.
        var path = Path.Combine(AppContext.BaseDirectory, "Config", "appsettings.json");
        if (!File.Exists(path))
        {
            _out.WriteLine($"{path} not in the test output - premise not re-checked here.");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.TryGetProperty("Updates", out var updates).Should().BeTrue();
        updates.TryGetProperty("Enabled", out var enabled).Should().BeTrue();
        _out.WriteLine($"Config/appsettings.json Updates:Enabled = {enabled}");
        enabled.GetBoolean().Should().BeFalse("this is the shipped default the finding describes");
    }

    [Fact]
    public void Updates_disabled_is_never_reported_as_up_to_date()
    {
        var (health, state) = ServiceCatalog.DescribeUpdateCheck(UpdateCheckState.Disabled, null);

        health.Should().Be(ServiceHealth.Unknown, "nothing was contacted, so nothing is known");
        state.Should().NotContain("Up to date").And.NotContain("signed");
        state.Should().Contain("Updates:Enabled = false").And.Contain("Nothing was contacted");
    }

    [Fact]
    public void A_check_that_has_not_run_is_not_reported_as_up_to_date()
    {
        var (health, state) = ServiceCatalog.DescribeUpdateCheck(UpdateCheckState.NotChecked, null);

        health.Should().Be(ServiceHealth.Unknown);
        state.Should().NotContain("Up to date");
        state.Should().Contain("has not been compared against any release");
    }

    [Fact]
    public void A_failed_check_says_unknown_not_current()
    {
        var (health, state) = ServiceCatalog.DescribeUpdateCheck(UpdateCheckState.Failed, "Request timed out.");

        health.Should().Be(ServiceHealth.Unknown);
        state.Should().Contain("unknown, not confirmed current").And.Contain("Request timed out.");
    }

    [Fact]
    public void Only_a_check_that_answered_may_say_up_to_date_and_it_does_not_claim_a_signature()
    {
        var (health, state) = ServiceCatalog.DescribeUpdateCheck(UpdateCheckState.UpToDate, null);

        health.Should().Be(ServiceHealth.Healthy);
        state.Should().Contain("Up to date");
        // The old sentence asserted "(signed)". A version comparison verifies no signature - the
        // signature check happens when an update is downloaded, which by definition did not happen.
        state.Should().NotContain("signed");
    }

    [Theory]
    [InlineData(UpdateCheckState.Disabled)]
    [InlineData(UpdateCheckState.NotChecked)]
    [InlineData(UpdateCheckState.Failed)]
    public void No_unmeasured_update_state_is_ever_green(UpdateCheckState state)
    {
        var (health, _) = ServiceCatalog.DescribeUpdateCheck(state, "some error");
        health.Should().NotBe(ServiceHealth.Healthy);
        health.Should().NotBe(ServiceHealth.Running);
    }

    // ── pages-r1-06: absent service vs failed probe ──────────────────────────

    [Fact]
    public void Only_error_1060_proves_a_service_is_absent()
    {
        // ERROR_SERVICE_DOES_NOT_EXIST.
        ServiceCatalog.ProvesServiceAbsent(
            new InvalidOperationException("x", new Win32Exception(1060))).Should().BeTrue();

        // ERROR_ACCESS_DENIED - the probe failed; the service may well exist.
        ServiceCatalog.ProvesServiceAbsent(
            new InvalidOperationException("x", new Win32Exception(5))).Should().BeFalse();

        // Unclassifiable: no inner error code to read. Fail to unknown, never to a fact.
        ServiceCatalog.ProvesServiceAbsent(new InvalidOperationException("x")).Should().BeFalse();
        ServiceCatalog.ProvesServiceAbsent(new TimeoutException("x")).Should().BeFalse();
    }

    [Fact]
    public void A_failed_probe_renders_as_unknown_and_never_tells_the_operator_to_create_a_service()
    {
        var (health, state) = ServiceCatalog.DescribeWindowsService(
            new ServiceCatalog.WindowsServiceProbe(
                ServiceCatalog.WindowsServiceProbeState.ProbeFailed, false, null, "Access is denied."));

        health.Should().Be(ServiceHealth.Unknown);
        state.Should().Contain("Could not check the service").And.Contain("Access is denied.");
        state.Should().NotContain("sc create", "that instruction is only true of a service that is genuinely absent");
    }

    [Fact]
    public void A_genuinely_absent_service_keeps_its_original_words()
    {
        var (health, state) = ServiceCatalog.DescribeWindowsService(
            new ServiceCatalog.WindowsServiceProbe(
                ServiceCatalog.WindowsServiceProbeState.NotInstalled, false, null, null));

        health.Should().Be(ServiceHealth.NotInstalled);
        state.Should().Be("Not installed (sc create via Service & Updates).");
    }

    [Fact]
    public void An_installed_service_reports_running_or_stopped()
    {
        ServiceCatalog.DescribeWindowsService(new ServiceCatalog.WindowsServiceProbe(
            ServiceCatalog.WindowsServiceProbeState.Installed, true, "NT SERVICE\\SQLTriage", null))
            .Should().Be((ServiceHealth.Running, "Running as NT SERVICE\\SQLTriage"));

        ServiceCatalog.DescribeWindowsService(new ServiceCatalog.WindowsServiceProbe(
            ServiceCatalog.WindowsServiceProbeState.Installed, false, null, null))
            .Should().Be((ServiceHealth.Stopped, "Installed but stopped."));
    }

    // ── The live half of r1-06: what THIS machine's SCM actually raises ──────

    [Fact]
    public void Live_a_missing_service_really_does_raise_error_1060_on_this_box()
    {
        if (!OperatingSystem.IsWindows())
        {
            _out.WriteLine("Not Windows - SCM probe inert.");
            return;
        }

        Exception? caught = null;
        try
        {
#pragma warning disable CA1416
            using var sc = new System.ServiceProcess.ServiceController("ZZNoSuchServiceHunt");
            _ = sc.Status;
#pragma warning restore CA1416
        }
        catch (Exception ex) { caught = ex; }

        caught.Should().NotBeNull("a service that does not exist cannot report a status");
        _out.WriteLine($"TYPE  = {caught!.GetType().FullName}");
        _out.WriteLine($"MSG   = {caught.Message}");
        _out.WriteLine($"INNER = {caught.InnerException?.GetType().FullName} | {caught.InnerException?.Message}");
        if (caught.InnerException is Win32Exception w) _out.WriteLine($"NATIVE= {w.NativeErrorCode}");

        // This is the whole discriminator. If a future runtime stops carrying the code, this fails
        // here rather than quietly reclassifying every failed probe as "not installed".
        ServiceCatalog.ProvesServiceAbsent(caught).Should().BeTrue();

        // And the classification the card is drawn from.
        var probe = new ServiceCatalog.WindowsServiceProbe(
            ServiceCatalog.ProvesServiceAbsent(caught)
                ? ServiceCatalog.WindowsServiceProbeState.NotInstalled
                : ServiceCatalog.WindowsServiceProbeState.ProbeFailed,
            false, null, caught.Message);
        _out.WriteLine("CARD  = " + ServiceCatalog.DescribeWindowsService(probe).State);
    }
}
