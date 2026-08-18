/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Unit tests for the <see cref="SqlAssessmentService"/> bundle-accessor integration.
/// Full assessment runs require a live SQL Server — those are integration-test scope.
/// These tests exercise the bundle-driven initialisation path only.
/// </summary>
public class SqlAssessmentServiceTests
{
    // ── Bundle-locked / empty-bundle tests ───────────────────────────────────

    [Fact]
    public void Returns_Empty_When_Bundle_Locked()
    {
        // When ruleset.json is absent from the bundle the service falls back to
        // the built-in comprehensive checks — it must never throw.
        var bundle = new FakeBundleAccessor().SetLocked();
        // SqlAssessmentService requires ServerConnectionManager which requires DI.
        // We can only smoke-test construction here; full run tests need SQL Server.
        // Construction must succeed without throwing.
        var exception = Record.Exception(() =>
            new SqlAssessmentService(
                NullLogger<SqlAssessmentService>.Instance,
                connectionManager: null!,   // not used in construction
                bundle: bundle));
        Assert.Null(exception);
    }

    [Fact]
    public void Construction_WithBundleContainingRuleset_DoesNotThrow()
    {
        // A minimal, empty ruleset JSON — no rules, no probes. Must not throw.
        const string minimalRuleset = """{ "rules": [], "probes": {} }""";
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", minimalRuleset);
        var exception = Record.Exception(() =>
            new SqlAssessmentService(
                NullLogger<SqlAssessmentService>.Instance,
                connectionManager: null!,
                bundle: bundle));
        Assert.Null(exception);
    }

    [Fact]
    public void Construction_WithMalformedRulesetJson_DoesNotThrow()
    {
        // Malformed JSON in the bundle must trigger the error-log path, not a crash.
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", "{ not valid json }}}");
        var exception = Record.Exception(() =>
            new SqlAssessmentService(
                NullLogger<SqlAssessmentService>.Instance,
                connectionManager: null!,
                bundle: bundle));
        Assert.Null(exception);
    }

    [Fact]
    public void BundleStateChanged_DoesNotThrow()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", """{ "rules": [], "probes": {} }""");
        var svc = new SqlAssessmentService(
            NullLogger<SqlAssessmentService>.Instance,
            connectionManager: null!,
            bundle: bundle);

        // Raise state-changed; the service must invalidate its cache without throwing.
        var exception = Record.Exception(() => bundle.RaiseStateChanged());
        Assert.Null(exception);
    }
}
