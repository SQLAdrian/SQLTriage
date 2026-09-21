/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Tests;

// ── VulnerabilityAssessmentStateService tests ─────────────────────────────────

public class VulnerabilityAssessmentStateTests
{
    [Fact]
    public void InitialState_Empty()
    {
        var state = new VulnerabilityAssessmentStateService();
        Assert.False(state.IsRunning);
        Assert.False(state.HasRun);
        Assert.Empty(state.Results);
        Assert.Empty(state.AssessedServers);
        // FilteredResults moved to the SCOPED VulnerabilityAssessmentViewState on 2026-08-02 —
        // a filtered projection belongs to the viewer, not to the process.
        Assert.Empty(new VulnerabilityAssessmentViewState().FilteredResults);
    }

    [Fact]
    public void ClearResults_ResetsAllFields()
    {
        var state = new VulnerabilityAssessmentStateService
        {
            HasRun = true,
            IsRunning = false,
            IsImported = true,
            ImportedFileName = "test.csv"
        };
        state.Results.Add(new AssessmentResult { CheckId = "test" });
        state.AssessedServers.Add("SQLSRV01");

        state.ClearResults();

        Assert.False(state.HasRun);
        Assert.Empty(state.Results);
        Assert.Empty(state.AssessedServers);
        Assert.False(state.IsImported);
        Assert.Empty(state.ImportedFileName);
    }

    [Fact]
    public void ProgressPercent_NoServers_ReturnsZero()
    {
        var state = new VulnerabilityAssessmentStateService { TotalServers = 0 };
        Assert.Equal(0, state.ProgressPercent);
    }

    [Fact]
    public void ProgressPercent_HalfComplete_Returns50()
    {
        var state = new VulnerabilityAssessmentStateService
        {
            TotalServers = 4,
            CurrentServerIndex = 2
        };
        Assert.Equal(50.0, state.ProgressPercent);
    }

    [Fact]
    public void StateChanged_EventFires_OnNotify()
    {
        var state = new VulnerabilityAssessmentStateService();
        var fired = false;
        state.StateChanged += () => fired = true;
        state.NotifyStateChanged();
        Assert.True(fired);
    }

}

// ── VulnerabilityAssessmentViewState (SCOPED, per circuit) ────────────────────
//
// ScopeTagFilter / SelectedCategories / FilteredResults moved off the singleton on 2026-08-02.
// ScopeTagFilter in particular narrows what the NEXT assessment run actually executes, so on a
// process-wide service one caller could scope another caller's run.

public class VulnerabilityAssessmentViewStateTests
{
    [Fact]
    public void ScopeTagFilter_InitiallyEmpty()
    {
        var view = new VulnerabilityAssessmentViewState();
        Assert.Empty(view.ScopeTagFilter);
    }

    [Fact]
    public void ScopeTagFilter_AddAndRemove()
    {
        var view = new VulnerabilityAssessmentViewState();
        view.ScopeTagFilter.Add("Security");
        Assert.Contains("Security", view.ScopeTagFilter);
        view.ScopeTagFilter.Remove("Security");
        Assert.Empty(view.ScopeTagFilter);
    }

    [Fact]
    public void ScopeTagFilter_CaseInsensitive()
    {
        var view = new VulnerabilityAssessmentViewState();
        view.ScopeTagFilter.Add("security");
        Assert.Contains("Security", view.ScopeTagFilter); // case-insensitive HashSet
    }

    [Fact]
    public void SelectedCategories_CaseInsensitive()
    {
        var view = new VulnerabilityAssessmentViewState();
        view.SelectedCategories.Add("performance");
        Assert.Contains("Performance", view.SelectedCategories);
    }

    /// <summary>Two circuits, two view states — the point of the split.</summary>
    [Fact]
    public void TwoInstances_DoNotShareSelectionOrFilters()
    {
        var a = new VulnerabilityAssessmentViewState();
        var b = new VulnerabilityAssessmentViewState();

        a.SelectedConnectionId = "prod-cluster";
        a.SelectedServerNames.Add("PRODSQL01");
        a.ScopeTagFilter.Add("Security");

        Assert.Null(b.SelectedConnectionId);
        Assert.Empty(b.SelectedServerNames);
        Assert.Empty(b.ScopeTagFilter);
    }
}

// ── AssessmentResult model tests ──────────────────────────────────────────────

public class AssessmentResultTests
{
    [Fact]
    public void AssessmentResult_DefaultsAreEmpty()
    {
        var r = new AssessmentResult();
        Assert.Equal("", r.CheckId);
        Assert.Equal("", r.Severity);
        Assert.Equal("", r.Status);
    }

    [Fact]
    public void AssessmentSummary_CountsAreConsistent()
    {
        var summary = new AssessmentSummary
        {
            TotalChecks = 10,
            PassedChecks = 7,
            FailedChecks = 3
        };
        Assert.Equal(summary.TotalChecks, summary.PassedChecks + summary.FailedChecks);
    }

    [Fact]
    public void AssessmentResult_CanSetAllProperties()
    {
        var r = new AssessmentResult
        {
            CheckId = "WeakPassword",
            DisplayName = "Weak Password Detected",
            Severity = "Error",
            Status = "Failed",
            Category = "Security",
            TargetName = "SQLSRV01",
            TargetType = "Server",
            Message = "Login 'sa' uses a weak password",
            Remediation = "Change password for sa account"
        };

        Assert.Equal("WeakPassword", r.CheckId);
        Assert.Equal("Error", r.Severity);
        Assert.Equal("Failed", r.Status);
        Assert.Equal("Security", r.Category);
    }
}

// ── ServerConnection model tests ──────────────────────────────────────────────

public class ServerConnectionTests
{
    [Fact]
    public void ServerConnection_DefaultHasSqlWatch_IsFalse()
    {
        var conn = new SQLTriage.Data.Models.ServerConnection();
        Assert.False(conn.HasSqlWatch);
    }

    [Fact]
    public void ServerConnection_GetServerList_SingleServer()
    {
        var conn = new SQLTriage.Data.Models.ServerConnection
        {
            ServerNames = "SQLSRV01"
        };
        var list = conn.GetServerList();
        Assert.Single(list);
        Assert.Equal("SQLSRV01", list[0]);
    }

    [Fact]
    public void ServerConnection_GetServerList_MultipleServers()
    {
        var conn = new SQLTriage.Data.Models.ServerConnection
        {
            ServerNames = "SQLSRV01,SQLSRV02,SQLSRV03"
        };
        var list = conn.GetServerList();
        Assert.Equal(3, list.Count);
        Assert.Contains("SQLSRV01", list);
        Assert.Contains("SQLSRV03", list);
    }

    [Fact]
    public void ServerConnection_GetServerList_TrimsWhitespace()
    {
        var conn = new SQLTriage.Data.Models.ServerConnection
        {
            ServerNames = " SQLSRV01 , SQLSRV02 "
        };
        var list = conn.GetServerList();
        Assert.Equal("SQLSRV01", list[0]);
        Assert.Equal("SQLSRV02", list[1]);
    }

    [Fact]
    public void ServerConnection_GetConnectionString_ContainsServerName()
    {
        var conn = new SQLTriage.Data.Models.ServerConnection
        {
            ServerNames = "SQLSRV01",
            UseWindowsAuthentication = true
        };
        var cs = conn.GetConnectionString("SQLSRV01", "master");
        Assert.Contains("SQLSRV01", cs);
        Assert.Contains("master", cs);
    }

    [Fact]
    public void ServerConnection_AuthenticationDisplay_Windows()
    {
        var conn = new SQLTriage.Data.Models.ServerConnection { UseWindowsAuthentication = true };
        Assert.Contains("Windows", conn.AuthenticationDisplay);
    }

    [Fact]
    public void ServerConnection_AuthenticationDisplay_SQL()
    {
        var conn = new SQLTriage.Data.Models.ServerConnection
        {
            UseWindowsAuthentication = false,
            Username = "sa"
        };
        Assert.Contains("SQL", conn.AuthenticationDisplay);
    }
}
