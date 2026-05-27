/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;

namespace SQLTriage.Tests;

// ── ConfigFileHelper + UserSettings round-trip tests ─────────────────────────

public class ConfigFileHelperTests
{
    [Fact]
    public void Load_MissingFile_ReturnsNewInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var result = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path);
        Assert.NotNull(result);
        Assert.Equal("default", result.SelectedTheme);
        Assert.Equal(15, result.RefreshIntervalSeconds);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var settings = new UserSettingsService.UserSettings
        {
            SelectedTheme = "rolls-royce",
            RefreshIntervalSeconds = 30,
            AutoRefresh = false,
            ChartDataPointCap = 5000,
            ZoomLevel = 125,
            NoPantsMode = true
        };

        ConfigFileHelper.Save(path, settings);
        var loaded = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path);

        Assert.Equal("rolls-royce", loaded.SelectedTheme);
        Assert.Equal(30, loaded.RefreshIntervalSeconds);
        Assert.False(loaded.AutoRefresh);
        Assert.Equal(5000, loaded.ChartDataPointCap);
        Assert.Equal(125, loaded.ZoomLevel);
        Assert.True(loaded.NoPantsMode);
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(dir, "nested", "settings.json");
        ConfigFileHelper.Save(path, new UserSettingsService.UserSettings());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_AtomicWrite_DoesNotCorruptOnCrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        ConfigFileHelper.Save(path, new UserSettingsService.UserSettings { SelectedTheme = "amg" });
        Assert.False(File.Exists(path + ".tmp")); // temp file should be moved away
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNewInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        File.WriteAllText(path, "this is not json {{{");
        var result = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path);
        Assert.NotNull(result);
        Assert.Equal("default", result.SelectedTheme); // defaults
    }

    [Fact]
    public void Load_EmptyFile_ReturnsNewInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        File.WriteAllText(path, "   ");
        var result = ConfigFileHelper.Load<UserSettingsService.UserSettings>(path);
        Assert.NotNull(result);
        Assert.Equal("default", result.SelectedTheme);
    }
}

// ── ChartThemeService tests ──────────────────────────────────────────────────

public class ChartThemeServiceTests
{
    private class FakeUserSettings : IUserSettingsService
    {
        private string _theme = "default";
        public string GetSelectedTheme() => _theme;
        public void SetSelectedTheme(string theme) { _theme = theme; OnSelectedThemeChanged?.Invoke(theme); }
        public event Action<string>? OnSelectedThemeChanged;

        // Minimal stubs for remaining interface members
        public UserSettingsService.UserSettings GetSettings() => new();
        public int GetRefreshInterval() => 15;
        public void SetRefreshInterval(int seconds) { }
        public bool GetAutoRefresh() => true;
        public void SetAutoRefresh(bool enabled) { }
        public int GetDefaultTimeRange() => 60;
        public void SetDefaultTimeRange(int minutes) { }
        public bool GetShowDiagnosticPane() => false;
        public void SetShowDiagnosticPane(bool enabled) { }
        public string GetDefaultDashboardId() => "";
        public void SetDefaultDashboardId(string dashboardId) { }
        public string GetDataSource() => "master";
        public void SetDataSource(string source) { }
        public int GetZoomLevel() => 150;
        public void SetZoomLevel(int zoomPercent) { }
        public bool GetDebugLogging() => false;
        public void SetDebugLogging(bool enabled) { }
        public bool GetAnonymiseServerNames() => false;
        public void SetAnonymiseServerNames(bool enabled) { }
        public bool GetUseV2PlanIcons() => false;
        public void SetUseV2PlanIcons(bool enabled) { }
        public bool GetShowVaQueries() => false;
        public void SetShowVaQueries(bool enabled) { }
        public bool GetNoPantsMode() => false;
        public void SetNoPantsMode(bool enabled) { }
        public bool GetNoPantsDisclaimerAccepted() => false;
        public void SetNoPantsDisclaimerAccepted(bool accepted) { }
        public bool GetAlertBaselineEnabled() => true;
        public void SetAlertBaselineEnabled(bool enabled) { }
        public event Action<bool>? OnAlertBaselineEnabledChanged;
        public bool GetAlertBaselinePerServer() => true;
        public void SetAlertBaselinePerServer(bool enabled) { }
        public bool GetExperimentalMode() => false;
        public void SetExperimentalMode(bool enabled) { }
        public bool GetOnboardingComplete() => false;
        public void SetOnboardingComplete(bool complete) { }
        public string GetLastSeenVersion() => "";
        public void SetLastSeenVersion(string version) { }
        public void UpdateAutoExportSettings(bool auditCsv, bool auditJson, bool auditPdf, bool quickCheckCsv, bool quickCheckPdf, bool vaCsv, bool vaPdf) { }
        public UserSettingsService.UserSettings.VaScheduleSnapshot GetVaSchedule() => new(new UserSettingsService.UserSettings());
        public void SaveVaSchedule(bool enabled, string type, string time, int dayOfWeek, int dayOfMonth) { }
        public void UpdateVaScheduleLastRun(DateTime utcNow) { }
        public UserSettingsService.UserSettings.RoadmapScheduleSnapshot GetRoadmapSchedule() => new(new UserSettingsService.UserSettings());
        public void SaveRoadmapSchedule(bool enabled, string type, string time, int dayOfWeek, int dayOfMonth, bool splitByDomain) { }
        public void UpdateRoadmapScheduleLastRun(DateTime utcNow) { }
        public int GetMaxHeavyConcurrent() => 5;
        public void SetMaxHeavyConcurrent(int limit) { }
        public int GetMaxLightConcurrent() => 10;
        public void SetMaxLightConcurrent(int limit) { }
        public int GetMaxConcurrentPerServer() => 3;
        public void SetMaxConcurrentPerServer(int limit) { }
        public bool GetEnableBurstMode() => false;
        public void SetEnableBurstMode(bool enabled) { }
        public double GetBurstConcurrencyMultiplier() => 2.0;
        public void SetBurstConcurrencyMultiplier(double mult) { }
        public int GetBurstDurationSec() => 60;
        public void SetBurstDurationSec(int sec) { }
        public event Action<int>? OnZoomChanged;
        public event Action<bool>? OnDebugLoggingChanged;
        public event Action<bool>? OnNoPantsModeChanged;
        public event Action<bool>? OnExperimentalModeChanged;
        public event Action<bool>? OnShowMaturityRoadmapChanged;

        // Threshold highlighting stubs
        public bool GetThresholdsEnabled() => false;
        public void SetThresholdsEnabled(bool enabled) { }
        public int GetThresholdCpuMs() => 80000;
        public void SetThresholdCpuMs(int ms) { }
        public int GetThresholdWaitTimeMs() => 1000;
        public void SetThresholdWaitTimeMs(int ms) { }
        public int GetThresholdMemoryMb() => 0;
        public void SetThresholdMemoryMb(int mb) { }
        public int GetThresholdReadsKb() => 0;
        public void SetThresholdReadsKb(int kb) { }
        public int GetThresholdWritesKb() => 0;
        public void SetThresholdWritesKb(int kb) { }
        public int GetThresholdDurationMs() => 5000;
        public void SetThresholdDurationMs(int ms) { }
    }

    [Fact]
    public void GetCurrentPalette_DefaultTheme_ReturnsExpectedColors()
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        var palette = service.GetCurrentPalette();
        Assert.Equal("#4ec9b0", palette.Success);
        Assert.Equal("#ce9178", palette.Warning);
        Assert.Equal("#f44747", palette.Critical);
        Assert.Equal("#6a6a6a", palette.Neutral);
    }

    [Fact]
    public void GetCurrentPalette_RollsRoyceTheme_ReturnsExpectedColors()
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        fake.SetSelectedTheme("rolls-royce");
        var palette = service.GetCurrentPalette();
        Assert.Equal("#6bbd99", palette.Success);
        Assert.Equal("#e7a978", palette.Warning);
        Assert.Equal("#d97b7b", palette.Critical);
        Assert.Equal("#8a8a9a", palette.Neutral);
    }

    [Fact]
    public void GetCurrentPalette_UnknownTheme_FallsBackToDefault()
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        fake.SetSelectedTheme("nonexistent-theme");
        var palette = service.GetCurrentPalette();
        Assert.Equal("#4ec9b0", palette.Success); // default
    }

    [Fact]
    public void OnChartThemeChanged_Fires_WhenSettingsChange()
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        bool fired = false;
        service.OnChartThemeChanged += () => fired = true;
        fake.SetSelectedTheme("amg");
        Assert.True(fired);
    }

    [Theory]
    [InlineData("default", false, 0)]
    [InlineData("rolls-royce", true, 12)]
    [InlineData("amg", false, 0)]
    public void GlassProperties_VaryByTheme(string theme, bool expectedGlass, int expectedBlur)
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        fake.SetSelectedTheme(theme);
        Assert.Equal(expectedGlass, service.IsGlassEnabled);
        Assert.Equal(expectedBlur, service.GlassBlurPx);
    }

    [Fact]
    public void GetLineOptions_ReturnsConfiguredOptions()
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        var opts = service.GetLineOptions<object>("CPU Usage", "%");
        Assert.NotNull(opts);
        Assert.NotEmpty(opts.Colors);
        Assert.Equal("CPU Usage", opts.Title.Text);
        Assert.Equal("%", opts.Yaxis[0].Title.Text);
        Assert.False(opts.Chart.Toolbar.Show);
    }

    [Fact]
    public void GetDonutOptions_ReturnsConfiguredOptions()
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        var opts = service.GetDonutOptions<object>("Storage");
        Assert.NotNull(opts);
        Assert.Equal("Storage", opts.Title.Text);
        Assert.NotEmpty(opts.Colors);
    }

    [Fact]
    public void GetBarOptions_ReturnsConfiguredOptions()
    {
        var fake = new FakeUserSettings();
        var service = new ChartThemeService(fake);
        var opts = service.GetBarOptions<object>("Wait Types", "ms");
        Assert.NotNull(opts);
        Assert.Equal("Wait Types", opts.Title.Text);
        Assert.Equal("ms", opts.Xaxis.Title.Text);
        Assert.True(opts.PlotOptions.Bar.Horizontal);
    }
}

// ── SqlQueryRepository tests ─────────────────────────────────────────────────
// NOTE: These tests write .sql files to the test output directory because
// SqlQueryRepository still reads SQL bodies from Data/Sql/*.sql on disk.
// Only queries.json metadata is now sourced from the bundle.

public class SqlQueryRepositoryTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _sqlDir;
    private readonly List<string> _tempFiles = new();

    public SqlQueryRepositoryTests()
    {
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _sqlDir = Path.Combine(_baseDir, "Data", "Sql");
        Directory.CreateDirectory(_sqlDir);
    }

    private SqlQueryRepository MakeRepo(string queriesJson)
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/queries.json", queriesJson);
        return new SqlQueryRepository(NullLogger<SqlQueryRepository>.Instance, bundle);
    }

    private async Task<string> WriteSqlFileAsync(string fileName, string sql)
    {
        var path = Path.Combine(_sqlDir, fileName);
        await File.WriteAllTextAsync(path, sql);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public async Task Get_ExistingQuery_ReturnsDefinition()
    {
        await WriteSqlFileAsync("test-health-check.sql", "SELECT 1 AS health");

        var repo = MakeRepo("""{"queries":{}}""");
        await repo.ReloadAsync();

        var query = repo.Get("test-health-check");
        Assert.NotNull(query);
        Assert.Equal("SELECT 1 AS health", query!.Sql);
        Assert.Equal("test-health-check", query.Id);
    }

    [Fact]
    public async Task GetAll_ReturnsAllQueries()
    {
        await WriteSqlFileAsync("q1.sql", "SELECT 1");
        await WriteSqlFileAsync("q2.sql", "SELECT 2");

        var repo = MakeRepo("""{"queries":{}}""");
        await repo.ReloadAsync();

        var all = repo.GetAll();
        Assert.True(all.Count >= 2);
        Assert.Contains("q1", all.Keys);
        Assert.Contains("q2", all.Keys);
    }

    [Fact]
    public async Task GetByTag_CategoryFilter_ReturnsMatchingQueries()
    {
        await WriteSqlFileAsync("security-check.sql", "SELECT 1");

        var json = """{"queries":{"security-check":{"category":"Security","severity":"HIGH"}}}""";
        var repo = MakeRepo(json);
        await repo.ReloadAsync();

        var security = repo.GetByTag("security");
        Assert.NotEmpty(security);
        Assert.All(security, q => Assert.Equal("security-check", q.Id));
    }

    [Fact]
    public async Task GetQuickChecks_ReturnsOnlyQuickQueries()
    {
        await WriteSqlFileAsync("quick-a.sql", "SELECT 1");
        await WriteSqlFileAsync("slow-a.sql", "SELECT 2");

        var json = """{"queries":{"quick-a":{"quick":true},"slow-a":{"quick":false}}}""";
        var repo = MakeRepo(json);
        await repo.ReloadAsync();

        var quick = repo.GetQuickChecks();
        Assert.Single(quick);
        Assert.Equal("quick-a", quick[0].Id);
    }

    [Fact]
    public async Task Get_MissingQuery_ReturnsNull()
    {
        var repo = MakeRepo("""{"queries":{}}""");
        await repo.ReloadAsync();
        Assert.Null(repo.Get("nonexistent"));
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }
}
