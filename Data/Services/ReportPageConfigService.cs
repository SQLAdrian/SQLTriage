/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Loads and persists per-page report-section layout from Config/report-pages.json.
    /// Creates a seeded default file on first run covering the three main audit pages.
    /// </summary>
    public class ReportPageConfigService
    {
        private readonly ILogger<ReportPageConfigService> _logger;
        private readonly string _configPath;
        private ReportPagesRoot _root;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
        };

        public event Action? OnConfigChanged;

        public ReportPagesRoot Root => _root;

        public ReportPageConfigService(ILogger<ReportPageConfigService> logger)
        {
            _logger = logger;
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "report-pages.json");
            _root = Load();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Returns the page definition for the given route, or null if not found.</summary>
        public ReportPageDefinition? GetPage(string route) =>
            _root.Pages.FirstOrDefault(p => p.Route.Equals(route, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns the ordered, enabled sections for a page.
        /// Returns an empty list if the page is not configured.
        /// </summary>
        public List<ReportSection> GetSections(string route)
        {
            var page = GetPage(route);
            if (page == null) return new List<ReportSection>();
            return page.Sections
                .Where(s => s.Enabled)
                .OrderBy(s => s.Order)
                .ToList();
        }

        /// <summary>Adds or replaces a section on the given page and saves.</summary>
        public void UpsertSection(string pageId, ReportSection section)
        {
            var page = _root.Pages.FirstOrDefault(p => p.Id == pageId);
            if (page == null) return;

            var existing = page.Sections.FirstOrDefault(s => s.Id == section.Id);
            if (existing != null)
                page.Sections.Remove(existing);
            page.Sections.Add(section);

            // Re-number order by current list position
            NormaliseOrder(page);
            Save();
        }

        /// <summary>Removes a section from the given page and saves.</summary>
        public void DeleteSection(string pageId, string sectionId)
        {
            var page = _root.Pages.FirstOrDefault(p => p.Id == pageId);
            if (page == null) return;

            page.Sections.RemoveAll(s => s.Id == sectionId);
            NormaliseOrder(page);
            Save();
        }

        /// <summary>Moves a section up (-1) or down (+1) in the order and saves.</summary>
        public void MoveSection(string pageId, string sectionId, int direction)
        {
            var page = _root.Pages.FirstOrDefault(p => p.Id == pageId);
            if (page == null) return;

            var ordered = page.Sections.OrderBy(s => s.Order).ToList();
            var idx = ordered.FindIndex(s => s.Id == sectionId);
            if (idx < 0) return;

            var swapIdx = idx + direction;
            if (swapIdx < 0 || swapIdx >= ordered.Count) return;

            (ordered[idx].Order, ordered[swapIdx].Order) = (ordered[swapIdx].Order, ordered[idx].Order);
            Save();
        }

        /// <summary>Replaces the entire page list and saves.</summary>
        public void UpdateRoot(ReportPagesRoot newRoot)
        {
            _root = newRoot;
            Save();
            OnConfigChanged?.Invoke();
        }

        // ── Persistence ───────────────────────────────────────────────────────

        private ReportPagesRoot Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var loaded = JsonSerializer.Deserialize<ReportPagesRoot>(json, SerializerOptions);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load report page config");
            }

            var defaults = BuildDefaults();
            SaveRoot(defaults);
            return defaults;
        }

        private void Save()
        {
            SaveRoot(_root);
            OnConfigChanged?.Invoke();
        }

        private void SaveRoot(ReportPagesRoot root)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                File.WriteAllText(_configPath, JsonSerializer.Serialize(root, SerializerOptions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save report page config");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void NormaliseOrder(ReportPageDefinition page)
        {
            int i = 0;
            foreach (var s in page.Sections.OrderBy(s => s.Order))
                s.Order = i++;
        }

        // ── Default seed config ───────────────────────────────────────────────

        private static ReportPagesRoot BuildDefaults() => new()
        {
            Version = 1,
            Pages = new List<ReportPageDefinition>
            {
                new()
                {
                    Id      = "quick-check",
                    Title   = "Quick Check",
                    Route   = "/quickcheck",
                    PageType= "audit",
                    Sections = new List<ReportSection>
                    {
                        new() { Id="qc-server",   Title="Server Selection", NativeKey="server-selection", SectionType="native", Order=0 },
                        new() { Id="qc-summary",  Title="Summary Cards",    NativeKey="summary-cards",    SectionType="native", Order=1 },
                        new() { Id="qc-results",  Title="Results Grid",     NativeKey="results-grid",     SectionType="native", Order=2 },
                        new() { Id="qc-diag",     Title="Diagnostics",      NativeKey="diagnostics",      SectionType="native", Order=3 },
                    }
                },
                new()
                {
                    Id      = "vulnerability-assessment",
                    Title   = "Vulnerability Assessment",
                    Route   = "/vulnerabilityassessment",
                    PageType= "audit",
                    Sections = new List<ReportSection>
                    {
                        new() { Id="va-toolbar",   Title="Toolbar",          NativeKey="toolbar",          SectionType="native", Order=0 },
                        new() { Id="va-statcards", Title="Summary Cards",    NativeKey="stat-cards",       SectionType="native", Order=1 },
                        new() { Id="va-treemap",   Title="Treemap",          NativeKey="treemap",          SectionType="native", Order=2 },
                        new() { Id="va-filter",    Title="Category Filter",  NativeKey="category-filter",  SectionType="native", Order=3 },
                        new() { Id="va-results",   Title="Results Table",    NativeKey="results-table",    SectionType="native", Order=4 },
                    }
                },
                new()
                {
                    Id      = "full-audit",
                    Title   = "Full Audit",
                    Route   = "/fullaudit",
                    PageType= "audit",
                    Sections = new List<ReportSection>
                    {
                        new() { Id="fa-server",   Title="Server Selection", NativeKey="server-selection", SectionType="native", Order=0 },
                        new() { Id="fa-progress", Title="Progress",         NativeKey="progress",         SectionType="native", Order=1 },
                        new() { Id="fa-summary",  Title="Summary",          NativeKey="summary",          SectionType="native", Order=2 },
                        new() { Id="fa-results",  Title="Results",          NativeKey="results",          SectionType="native", Order=3 },
                    }
                },
            }
        };
    }
}
