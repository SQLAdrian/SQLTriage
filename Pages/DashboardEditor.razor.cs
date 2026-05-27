using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
namespace SQLTriage.Pages;
public partial class DashboardEditor
{
    private DashboardConfigRoot EditedConfig = new();
    private DashboardDefinition? SelectedDashboard;
    private PanelDefinition? SelectedPanel;
    private string StatusMessage = "";
    private bool IsSuccess;
    private string QueryTestResult = "";
    private bool QueryTestIsError;
    private DataTable? QueryResultTable;
    private string QueryResultSource = "";
    private string? QueryDdlPreview;
    private bool IsQueryRunning;

    // Add Dashboard Dialog
    private bool ShowAddDashboardDialog;
    private string NewDashboardTitle = "";
    private string NewDashboardRoute = "";

    // Edit Dashboard Dialog
    private bool ShowEditDashboardDialog;
    private DashboardDefinition? EditingDashboard;
    private string EditDashboardTitle = "";
    private string EditDashboardRoute = "";
    private string EditDashboardDefaultDatabase = "";

    private bool ShowPreview;
    private bool ShowImport;
    private string ImportJson = "";
    private (bool valid, string? error) _importValidation = (true, null);
    private System.Timers.Timer? _autoSaveTimer;
    private bool _hasUnsavedChanges;

    // Undo/Redo stacks
    private Stack<DashboardConfigRoot> UndoStack = new();
    private Stack<DashboardConfigRoot> RedoStack = new();
    private const int MaxUndoSteps = 50;

    private bool CanUndo => UndoStack.Count > 0;
    private bool CanRedo => RedoStack.Count > 0;

    // Test query parameters
    private DateTime TestTimeFrom = DateTime.Now.AddHours(-1);
    private DateTime TestTimeTo = DateTime.Now;
    private string TestSqlInstance = "";

    protected override void OnInitialized()
    {
        try
        {
            LoadConfig();

            // Auto-save timer: save 2 seconds after last edit
            _autoSaveTimer = new System.Timers.Timer(2000);
            _autoSaveTimer.AutoReset = false;
            _autoSaveTimer.Elapsed += async (s, e) => await InvokeAsync(AutoSave);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initializing dashboard editor");
            StatusMessage = $"Error initializing editor: {ex.Message}";
            IsSuccess = false;
        }
    }

    private void LoadConfig()
    {
        EditedConfig = new DashboardConfigRoot
        {
            Version = ConfigService.Config.Version,
            Dashboards = ConfigService.Config.Dashboards.Select(d => new DashboardDefinition
            {
                Id = d.Id,
                Title = d.Title,
                NavTitle = d.NavTitle,
                NavIcon = d.NavIcon,
                NavCategory = d.NavCategory,
                NavOrder = d.NavOrder,
                Route = d.Route,
                Enabled = d.Enabled,
                Source = d.Source,
                DefaultDatabase = d.DefaultDatabase,
                ShowAllOption = d.ShowAllOption,
                Panels = d.Panels.Select(p => ClonePanel(p)).ToList()
            }).ToList(),
            SupportQueries = new Dictionary<string, QueryPair>(ConfigService.Config.SupportQueries)
        };
    }

    private PanelDefinition ClonePanel(PanelDefinition original)
    {
        return new PanelDefinition
        {
            Id = original.Id,
            Title = original.Title,
            Description = original.Description,
            Enabled = original.Enabled,
            Source = original.Source,
            PanelType = original.PanelType,
            ChartType = original.ChartType,
            Height = original.Height,
            RefreshIntervalSeconds = original.RefreshIntervalSeconds,
            Layout = new PanelLayout
            {
                Column = original.Layout.Column,
                Order = original.Layout.Order,
                SpanColumns = original.Layout.SpanColumns
            },
            Query = new QueryPair
            {
                Source = original.Query.Source,
                SqlServer = original.Query.SqlServer
            },
            DefaultDatabase = original.DefaultDatabase,
            StatUnit = original.StatUnit,
            StatThresholdKey = original.StatThresholdKey,
            BarGaugeThresholdKey = original.BarGaugeThresholdKey,
            BarGaugeUnitSuffix = original.BarGaugeUnitSuffix,
            ValueFormat = original.ValueFormat,
            ColorThresholds = original.ColorThresholds.Select(c => new ColorThresholdRule
            {
                Operator = c.Operator,
                Value = c.Value,
                Color = c.Color,
                Label = c.Label
            }).ToList(),
            DataGridIsClickable = original.DataGridIsClickable,
            DataGridMaxRows = original.DataGridMaxRows,
            DataGridTopRows = original.DataGridTopRows
        };
    }

    private void RefreshPreview()
    {
        StateHasChanged();
    }

    private void SaveState()
    {
        var snapshot = new DashboardConfigRoot
        {
            Version = EditedConfig.Version,
            Dashboards = EditedConfig.Dashboards.Select(d => new DashboardDefinition
            {
                Id = d.Id,
                Title = d.Title,
                NavTitle = d.NavTitle,
                NavIcon = d.NavIcon,
                NavCategory = d.NavCategory,
                NavOrder = d.NavOrder,
                Route = d.Route,
                Enabled = d.Enabled,
                Source = d.Source,
                DefaultDatabase = d.DefaultDatabase,
                ShowAllOption = d.ShowAllOption,
                Panels = d.Panels.Select(p => ClonePanel(p)).ToList()
            }).ToList(),
            SupportQueries = new Dictionary<string, QueryPair>(EditedConfig.SupportQueries)
        };

        UndoStack.Push(snapshot);
        if (UndoStack.Count > MaxUndoSteps)
        {
            var temp = UndoStack.ToArray().Take(MaxUndoSteps).ToArray();
            UndoStack = new Stack<DashboardConfigRoot>(temp.Reverse());
        }
        RedoStack.Clear();

        // Mark as having unsaved changes and trigger auto-save timer
        _hasUnsavedChanges = true;
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Start();
    }

    private void Undo()
    {
        if (!CanUndo) return;
        RedoStack.Push(CloneConfig(EditedConfig));
        EditedConfig = UndoStack.Pop();
        RefreshSelection();
    }

    private void Redo()
    {
        if (!CanRedo) return;
        UndoStack.Push(CloneConfig(EditedConfig));
        EditedConfig = RedoStack.Pop();
        RefreshSelection();
    }

    private DashboardConfigRoot CloneConfig(DashboardConfigRoot original)
    {
        return new DashboardConfigRoot
        {
            Version = original.Version,
            Dashboards = original.Dashboards.Select(d => new DashboardDefinition
            {
                Id = d.Id,
                Title = d.Title,
                NavTitle = d.NavTitle,
                NavIcon = d.NavIcon,
                NavCategory = d.NavCategory,
                NavOrder = d.NavOrder,
                Route = d.Route,
                Enabled = d.Enabled,
                Source = d.Source,
                DefaultDatabase = d.DefaultDatabase,
                ShowAllOption = d.ShowAllOption,
                Panels = d.Panels.Select(p => ClonePanel(p)).ToList()
            }).ToList(),
            SupportQueries = new Dictionary<string, QueryPair>(original.SupportQueries)
        };
    }

    private void RefreshSelection()
    {
        if (SelectedDashboard != null)
        {
            SelectedDashboard = EditedConfig.Dashboards.FirstOrDefault(d => d.Id == SelectedDashboard.Id);
            if (SelectedDashboard != null && SelectedPanel != null)
            {
                SelectedPanel = SelectedDashboard.Panels.FirstOrDefault(p => p.Id == SelectedPanel.Id);
            }
        }
    }

    private void SelectDashboard(DashboardDefinition dashboard)
    {
        SelectedDashboard = dashboard;
        SelectedPanel = dashboard.Panels.FirstOrDefault();
    }

    private void SelectPanel(PanelDefinition panel)
    {
        SelectedPanel = panel;
    }

    private void AddDashboard()
    {
        NewDashboardTitle = "New Dashboard";
        NewDashboardRoute = "/new-dashboard";
        ShowAddDashboardDialog = true;
    }

    private void ConfirmAddDashboard()
    {
        if (string.IsNullOrWhiteSpace(NewDashboardTitle))
        {
            StatusMessage = "Dashboard name is required";
            IsSuccess = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(NewDashboardRoute))
        {
            StatusMessage = "Page name (route) is required";
            IsSuccess = false;
            return;
        }

        // Ensure route starts with /
        if (!NewDashboardRoute.StartsWith("/"))
        {
            NewDashboardRoute = "/" + NewDashboardRoute;
        }

        SaveState();
        var newDashboard = new DashboardDefinition
        {
            Id = $"dashboard-{DateTime.Now:yyyyMMddHHmmss}",
            Title = NewDashboardTitle.Trim(),
            Route = NewDashboardRoute.Trim(),
            Enabled = true,
            Panels = new List<PanelDefinition>()
        };
        EditedConfig.Dashboards.Add(newDashboard);
        SelectDashboard(newDashboard);
        ShowAddDashboardDialog = false;
        NewDashboardTitle = "";
        NewDashboardRoute = "";
    }

    private void CancelAddDashboard()
    {
        ShowAddDashboardDialog = false;
        NewDashboardTitle = "";
        NewDashboardRoute = "";
    }

    private void EditDashboard(DashboardDefinition dashboard)
    {
        try
        {
            EditingDashboard = dashboard;
            EditDashboardTitle = dashboard.Title;
            EditDashboardRoute = dashboard.Route;
            EditDashboardDefaultDatabase = dashboard.DefaultDatabase ?? "";
            ShowEditDashboardDialog = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error editing dashboard {DashboardId}", dashboard?.Id);
            StatusMessage = $"Error editing dashboard: {ex.Message}";
            IsSuccess = false;
        }
    }

    private void ConfirmEditDashboard()
    {
        try
        {
            if (EditingDashboard == null) return;

            if (string.IsNullOrWhiteSpace(EditDashboardTitle))
            {
                StatusMessage = "Dashboard name is required";
                IsSuccess = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(EditDashboardRoute))
            {
                StatusMessage = "Page name (route) is required";
                IsSuccess = false;
                return;
            }

            // Ensure route starts with /
            if (!EditDashboardRoute.StartsWith("/"))
            {
                EditDashboardRoute = "/" + EditDashboardRoute;
            }

            SaveState();
            EditingDashboard.Title = EditDashboardTitle.Trim();
            EditingDashboard.Route = EditDashboardRoute.Trim();
            EditingDashboard.DefaultDatabase = string.IsNullOrWhiteSpace(EditDashboardDefaultDatabase) ? null : EditDashboardDefaultDatabase.Trim();

            ShowEditDashboardDialog = false;
            EditingDashboard = null;
            EditDashboardTitle = "";
            EditDashboardRoute = "";
            EditDashboardDefaultDatabase = "";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error confirming dashboard edit");
            StatusMessage = $"Error saving dashboard: {ex.Message}";
            IsSuccess = false;
        }
    }

    private void CancelEditDashboard()
    {
        ShowEditDashboardDialog = false;
        EditingDashboard = null;
        EditDashboardTitle = "";
        EditDashboardRoute = "";
        EditDashboardDefaultDatabase = "";
    }

    private void DeleteDashboard(DashboardDefinition dashboard)
    {
        SaveState();
        EditedConfig.Dashboards.Remove(dashboard);
        if (SelectedDashboard == dashboard)
        {
            SelectedDashboard = EditedConfig.Dashboards.FirstOrDefault();
            SelectedPanel = SelectedDashboard?.Panels.FirstOrDefault();
        }
    }

    private void AddPanel()
    {
        if (SelectedDashboard == null) return;
        SaveState();
        var newPanel = new PanelDefinition
        {
            Id = $"panel-{DateTime.Now:yyyyMMddHHmmss}",
            Title = "New Panel",
            PanelType = "TimeSeries",
            ChartType = "Line",
            Height = 250,
            Layout = new PanelLayout { Column = 1, Order = SelectedDashboard.Panels.Count }
        };
        SelectedDashboard.Panels.Add(newPanel);
        SelectPanel(newPanel);
    }

    private void DuplicatePanel(PanelDefinition panel)
    {
        if (SelectedDashboard == null) return;
        SaveState();
        var duplicate = ClonePanel(panel);
        duplicate.Id = $"{panel.Id}-copy";
        duplicate.Title = $"{panel.Title} (Copy)";
        SelectedDashboard.Panels.Add(duplicate);
        SelectPanel(duplicate);
    }

    private void DeletePanel(PanelDefinition panel)
    {
        if (SelectedDashboard == null) return;
        SaveState();
        SelectedDashboard.Panels.Remove(panel);
        SelectedPanel = SelectedDashboard.Panels.FirstOrDefault();
    }

    private void MovePanelUp(PanelDefinition panel)
    {
        if (SelectedDashboard == null) return;
        SaveState();
        var index = SelectedDashboard.Panels.IndexOf(panel);
        if (index > 0)
        {
            SelectedDashboard.Panels.RemoveAt(index);
            SelectedDashboard.Panels.Insert(index - 1, panel);
            UpdatePanelOrders(SelectedDashboard.Panels);
        }
    }

    private void MovePanelDown(PanelDefinition panel)
    {
        if (SelectedDashboard == null) return;
        SaveState();
        var index = SelectedDashboard.Panels.IndexOf(panel);
        if (index < SelectedDashboard.Panels.Count - 1)
        {
            SelectedDashboard.Panels.RemoveAt(index);
            SelectedDashboard.Panels.Insert(index + 1, panel);
            UpdatePanelOrders(SelectedDashboard.Panels);
        }
    }

    private PanelDefinition? _draggedPanel;
    private DashboardDefinition? _draggedPanelDashboard;
    private DashboardDefinition? _draggedDashboard;
    private PanelDefinition? _dragOverPanel;
    private DashboardDefinition? _dragOverDashboard;

    private void OnDragStart(PanelDefinition panel)
    {
        _draggedPanel = panel;
        _draggedPanelDashboard = SelectedDashboard;
        _draggedDashboard = null;
    }

    private void OnDashboardDragStart(DashboardDefinition dashboard)
    {
        _draggedDashboard = dashboard;
        _draggedPanel = null;
        _draggedPanelDashboard = null;
    }

    private void OnDragOver(PanelDefinition panel)
    {
        _dragOverPanel = panel;
    }

    private void OnDragLeave(PanelDefinition panel)
    {
        if (_dragOverPanel == panel)
        {
            _dragOverPanel = null;
        }
    }

    private void OnDashboardDragOver(DashboardDefinition dashboard)
    {
        _dragOverDashboard = dashboard;
    }

    private void OnDashboardDragLeave(DashboardDefinition dashboard)
    {
        if (_dragOverDashboard == dashboard)
        {
            _dragOverDashboard = null;
        }
    }

    private void OnDashboardDrop(DashboardDefinition targetDashboard)
    {
        // ── Case 1: reorder dashboards ────────────────────────────────────────
        if (_draggedDashboard != null)
        {
            if (_draggedDashboard.Id == targetDashboard.Id)
            {
                _draggedDashboard = null;
                _dragOverDashboard = null;
                return;
            }

            SaveState();

            var dashboards = EditedConfig.Dashboards;
            var fromIndex = dashboards.IndexOf(_draggedDashboard);
            var toIndex = dashboards.IndexOf(targetDashboard);
            if (fromIndex >= 0 && toIndex >= 0)
            {
                dashboards.RemoveAt(fromIndex);
                dashboards.Insert(toIndex, _draggedDashboard);
                UpdateDashboardOrders(dashboards);
            }

            _draggedDashboard = null;
            _dragOverDashboard = null;
            return;
        }

        // ── Case 2: move panel to a different dashboard ───────────────────────
        if (_draggedPanel == null || _draggedPanelDashboard == null)
            return;

        if (_draggedPanelDashboard.Id == targetDashboard.Id)
            return;

        SaveState();

        _draggedPanelDashboard.Panels.Remove(_draggedPanel);
        UpdatePanelOrders(_draggedPanelDashboard.Panels);

        targetDashboard.Panels.Add(_draggedPanel);
        UpdatePanelOrders(targetDashboard.Panels);

        SelectDashboard(targetDashboard);
        SelectPanel(_draggedPanel);

        _draggedPanel = null;
        _draggedPanelDashboard = null;
        _dragOverDashboard = null;
    }

    private void OnDrop(PanelDefinition targetPanel)
    {
        if (_draggedPanel == null || _draggedPanelDashboard == null || targetPanel == null)
            return;

        if (_draggedPanel.Id == targetPanel.Id)
            return;

        SaveState();

        var targetDashboard = EditedConfig.Dashboards.FirstOrDefault(d => d.Panels.Contains(targetPanel));
        if (targetDashboard == null)
            return;

        _draggedPanelDashboard.Panels.Remove(_draggedPanel);
        UpdatePanelOrders(_draggedPanelDashboard.Panels);

        var targetIndex = targetDashboard.Panels.IndexOf(targetPanel);
        targetDashboard.Panels.Insert(targetIndex, _draggedPanel);
        UpdatePanelOrders(targetDashboard.Panels);

        if (_draggedPanelDashboard.Id != targetDashboard.Id)
        {
            SelectDashboard(targetDashboard);
        }
        SelectPanel(_draggedPanel);

        _draggedPanel = null;
        _draggedPanelDashboard = null;
        _dragOverPanel = null;
    }

    private void UpdatePanelOrders(List<PanelDefinition> panels)
    {
        for (int i = 0; i < panels.Count; i++)
            panels[i].Layout.Order = i;
    }

    private void UpdateDashboardOrders(List<DashboardDefinition> dashboards)
    {
        for (int i = 0; i < dashboards.Count; i++)
            dashboards[i].NavOrder = i;
    }

    private void AddColorThreshold()
    {
        if (SelectedPanel == null) return;
        SaveState();
        SelectedPanel.ColorThresholds.Add(new ColorThresholdRule
        {
            Operator = ">=",
            Value = 80,
            Color = "#ff9800",
            Label = "Warning"
        });
    }

    private void RemoveColorThreshold(ColorThresholdRule rule)
    {
        if (SelectedPanel == null) return;
        SaveState();
        SelectedPanel.ColorThresholds.Remove(rule);
    }

    /// <summary>
    /// Clears previous query test results before running a new test.
    /// </summary>
    private void ClearQueryResults()
    {
        QueryTestResult = "";
        QueryTestIsError = false;
        QueryResultTable = null;
        QueryResultSource = "";
        QueryDdlPreview = null;
    }

    /// <summary>
    /// Executes the SQL Server query and shows the results in a data grid
    /// so the user can manually confirm the data.
    /// </summary>
    private async Task TestSqlServerQuery()
    {
        if (SelectedPanel == null || string.IsNullOrWhiteSpace(SelectedPanel.Query.SqlServer))
        {
            ClearQueryResults();
            QueryTestResult = "Please enter a SQL Server query";
            QueryTestIsError = true;
            return;
        }

        ClearQueryResults();
        QueryTestResult = "Executing SQL Server query...";
        QueryResultSource = "SQL Server";
        IsQueryRunning = true;
        StateHasChanged();

        try
        {
            // Create dashboard filter with test parameters
            var filter = new DashboardFilter
            {
                TimeFrom = TestTimeFrom,
                TimeTo = TestTimeTo,
                Instances = string.IsNullOrWhiteSpace(TestSqlInstance)
                    ? Array.Empty<string>()
                    : TestSqlInstance.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };

            var (success, message, results) = await liveQueriesTableService.ExecuteSqlServerQueryAsync(
                ConnectionFactory, SelectedPanel.Query.SqlServer, filter);

            QueryTestResult = message;
            QueryTestIsError = !success;
            QueryResultTable = results;
        }
        catch (Exception ex)
        {
            QueryTestResult = $"Error: {ex.Message}";
            QueryTestIsError = true;
        }
        finally
        {
            IsQueryRunning = false;
        }
    }

    /// <summary>
    /// Validates the SQL Server query, generates and shows the CREATE TABLE DDL,
    /// creates/updates the SQLite cache table, and inserts initial data.
    /// </summary>
    private async Task ValidateliveQueriesQuery()
    {
        if (SelectedPanel == null || string.IsNullOrWhiteSpace(SelectedPanel.Query.SqlServer))
        {
            ClearQueryResults();
            QueryTestResult = "Please enter a SQL Server query first";
            QueryTestIsError = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedPanel.Id))
        {
            ClearQueryResults();
            QueryTestResult = "Please save the panel first (panel ID is required for table name)";
            QueryTestIsError = true;
            return;
        }

        ClearQueryResults();
        QueryTestResult = "Generating DDL and creating table...";
        IsQueryRunning = true;
        StateHasChanged();

        try
        {
            // Step 1: Generate and display the DDL so the user can review it
            var (ddlOk, ddlMsg, ddl) = await liveQueriesTableService.GenerateCreateTableDdlAsync(
                ConnectionFactory, SelectedPanel.Id, SelectedPanel.Query.SqlServer);

            if (ddlOk && !string.IsNullOrEmpty(ddl))
            {
                QueryDdlPreview = ddl;
            }
            else if (!ddlOk)
            {
                QueryTestResult = ddlMsg;
                QueryTestIsError = true;
                return;
            }

            // Step 2: Actually create/update the table and insert data
            var serverName = ".";
            if (ConnectionFactory is SqlServerConnectionFactory sqlFactory)
            {
                serverName = sqlFactory.ServerName ?? ".";
            }

            var (success, message) = await liveQueriesTableService.ValidateAndCreateTableAsync(
                ConnectionFactory,
                SelectedPanel.Id,
                SelectedPanel.Query.SqlServer,
                serverName);

            QueryTestResult = message;
            QueryTestIsError = !success;
        }
        catch (Exception ex)
        {
            QueryTestResult = $"Error: {ex.Message}";
            QueryTestIsError = true;
        }
        finally
        {
            IsQueryRunning = false;
        }
    }

    private void PreviewPanel()
    {
        ShowPreview = true;
    }

    private void ClosePreview()
    {
        ShowPreview = false;
    }

    private void ShowImportDialog()
    {
        ShowImport = true;
        ImportJson = "";
        _importValidation = (true, null);
    }

    private void CloseImportDialog()
    {
        ShowImport = false;
    }

    private void OnImportJsonChanged(Microsoft.AspNetCore.Components.ChangeEventArgs e)
    {
        ImportJson = e.Value?.ToString() ?? "";
        _importValidation = string.IsNullOrWhiteSpace(ImportJson)
            ? (true, null)
            : ConfigService.ValidateJson(ImportJson);
    }

    private void ResetConfigToDefault()
    {
        ConfigService.ResetToDefault();
        LoadConfig();
        StatusMessage = "Configuration reset to default.";
        IsSuccess = true;
    }

    private void ImportConfig()
    {
        try
        {
            SaveState();
            var imported = JsonSerializer.Deserialize<DashboardConfigRoot>(ImportJson);
            if (imported != null)
            {
                EditedConfig = imported;
                SelectedDashboard = EditedConfig.Dashboards.FirstOrDefault();
                SelectedPanel = SelectedDashboard?.Panels.FirstOrDefault();
                StatusMessage = "Configuration imported successfully!";
                IsSuccess = true;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
            IsSuccess = false;
        }
        CloseImportDialog();
    }

    private void ExportConfig()
    {
        var json = JsonSerializer.Serialize(EditedConfig, new JsonSerializerOptions { WriteIndented = true });
        // In a real app, we'd trigger a file download
        StatusMessage = $"Exported {json.Length} characters to clipboard (simulated)";
        IsSuccess = true;
        System.Windows.Clipboard.SetText(json);
    }

    private async Task SaveAll()
    {
        try
        {
            SaveState();

            ConfigService.UpdateConfig(EditedConfig);

            StatusMessage = "Configuration saved successfully!";
            IsSuccess = true;
            _hasUnsavedChanges = false;
            _autoSaveTimer?.Stop();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            IsSuccess = false;
        }
        await Task.CompletedTask;
    }

    private void TriggerAutoSave()
    {
        _hasUnsavedChanges = true;
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Start();
    }

    private async Task AutoSave()
    {
        if (!_hasUnsavedChanges) return;

        try
        {
            ConfigService.UpdateConfig(EditedConfig);
            _hasUnsavedChanges = false;
            StatusMessage = "Auto-saved";
            IsSuccess = true;
            StateHasChanged();

            // Clear status message after 2 seconds
            await Task.Delay(2000);
            StatusMessage = "";
            StateHasChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Auto-save failed: {ex.Message}";
            IsSuccess = false;
            StateHasChanged();
        }
    }

    public void Dispose()
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;
    }
}
