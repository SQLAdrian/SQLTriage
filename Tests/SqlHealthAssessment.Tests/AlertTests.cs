/* In the name of God, the Merciful, the Compassionate */

using SqlHealthAssessment.Data.Models;
using SqlHealthAssessment.Data.Services;

namespace SqlHealthAssessment.Tests;

// ── AlertDefinition model tests ──────────────────────────────────────────────

public class AlertDefinitionTests
{
    [Fact]
    public void AlertDefinition_DefaultsAreReasonable()
    {
        var alert = new AlertDefinition();
        Assert.True(alert.Enabled);
        Assert.Equal("greater_than", alert.Operator);
        Assert.Equal(60, alert.FrequencySeconds);
        Assert.Null(alert.CooldownMinutes); // null = use global default
    }

    [Fact]
    public void AlertThresholds_WarningAndCritical_Independent()
    {
        var t = new AlertThresholds { Warning = 80, Critical = 90 };
        Assert.Equal(80, t.Warning);
        Assert.Equal(90, t.Critical);
    }

    [Fact]
    public void AlertState_DefaultStatus_IsActive()
    {
        var state = new AlertState();
        Assert.Equal(AlertStatus.Active, state.Status);
        Assert.Equal(1, state.HitCount);
    }

    [Fact]
    public void AlertState_CanTransitionToAcknowledged()
    {
        var state = new AlertState { Status = AlertStatus.Active };
        state.Status = AlertStatus.Acknowledged;
        state.AcknowledgedAt = DateTime.UtcNow;
        Assert.Equal(AlertStatus.Acknowledged, state.Status);
        Assert.NotNull(state.AcknowledgedAt);
    }

    [Fact]
    public void AlertState_CanTransitionToResolved()
    {
        var state = new AlertState { Status = AlertStatus.Active };
        state.Status = AlertStatus.Resolved;
        state.ResolvedAt = DateTime.UtcNow;
        Assert.Equal(AlertStatus.Resolved, state.Status);
        Assert.NotNull(state.ResolvedAt);
    }

    [Theory]
    [InlineData("greater_than", 85.0, 80.0, true)]
    [InlineData("greater_than", 79.0, 80.0, false)]
    [InlineData("less_than",    5.0, 10.0, true)]
    [InlineData("less_than",    15.0, 10.0, false)]
    [InlineData("greater_than", 80.0, 80.0, false)] // not strictly greater
    public void ThresholdBreach_OperatorLogic(string op, double value, double threshold, bool expected)
    {
        var result = op == "less_than"
            ? value < threshold
            : value > threshold;
        Assert.Equal(expected, result);
    }
}

// ── AlertGlobalDefaults tests ─────────────────────────────────────────────────

public class AlertGlobalDefaultsTests
{
    [Fact]
    public void GlobalDefaults_DefaultValues()
    {
        var defaults = new AlertGlobalDefaults();
        Assert.Equal(5, defaults.CooldownMinutes);
        Assert.Equal(24, defaults.AutoAcknowledgeHours);
        Assert.Equal(365, defaults.RetentionDays);
        Assert.True(defaults.Enabled);
    }

    [Fact]
    public void GlobalDefaults_CanBeDisabled()
    {
        var defaults = new AlertGlobalDefaults { Enabled = false };
        Assert.False(defaults.Enabled);
    }
}

// ── AlertHistoryRecord tests ──────────────────────────────────────────────────

public class AlertHistoryRecordTests
{
    [Fact]
    public void AlertHistoryRecord_DefaultStatus_IsActive()
    {
        var record = new AlertHistoryRecord();
        Assert.Equal("Active", record.Status);
        Assert.Equal(1, record.HitCount);
    }

    [Fact]
    public void AlertHistoryRecord_Fields_SetCorrectly()
    {
        var now = DateTime.UtcNow;
        var record = new AlertHistoryRecord
        {
            AlertId = "cpu_sql_usage",
            AlertName = "SQL Server CPU Usage",
            ServerName = "SQLSRV01",
            Severity = "Critical",
            Value = 95.5,
            ThresholdValue = 90.0,
            FirstTriggered = now,
            LastTriggered = now,
            Message = "95.5% above 90.0% critical threshold"
        };

        Assert.Equal("cpu_sql_usage", record.AlertId);
        Assert.Equal(95.5, record.Value);
        Assert.Equal("Critical", record.Severity);
    }
}

// ── AlertNotification tests ───────────────────────────────────────────────────

public class AlertNotificationTests
{
    [Fact]
    public void AlertNotification_HasUniqueId()
    {
        var n1 = new AlertNotification();
        var n2 = new AlertNotification();
        Assert.NotEqual(n1.Id, n2.Id);
    }

    [Fact]
    public void AlertNotification_DefaultIsNotAcknowledged()
    {
        var n = new AlertNotification();
        Assert.False(n.IsAcknowledged);
    }
}
