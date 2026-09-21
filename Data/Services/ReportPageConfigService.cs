/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:ReportPageConfigService.Class — loads and persists report page section layouts from JSON
    /// <summary>
    /// Loads and persists per-page report-section layout from Config/report-pages.json.
    /// Creates a seeded default file on first run covering the three main audit pages.
    /// </summary>
    public class ReportPageConfigService
    {
        private readonly ILogger<ReportPageConfigService> _logger;
        private readonly string _configPath;
        private ReportPagesRoot _root;

        /// <summary>
        /// What actually happened to <c>report-pages.json</c> on the way in. Cached at construction
        /// because <see cref="Save"/>'s refusal is keyed off it, and re-reading the file at write time
        /// would answer a different question than the one the in-memory layout descends from.
        /// </summary>
        private ConfigLoadOutcome _loadOutcome = ConfigLoadOutcome.Missing;

        /// <summary>
        /// How <see cref="Load"/> resolved the layout currently in memory. Exposed because
        /// "the operator must be told" has to be inspectable rather than only log-shaped — and because
        /// a pin that asserts a REFUSAL needs to be able to say which state the refusal came from.
        /// </summary>
        public ConfigLoadOutcome LoadOutcome => _loadOutcome;

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
            : this(logger, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "report-pages.json"))
        {
        }

        /// <summary>
        /// The DISK constructor with its path supplied — the production one delegates to it. Added
        /// 2026-09-12 (lane I1-census) for the same reason <see cref="DashboardConfigService"/> grew one
        /// on 2026-09-11: the only constructor hard-coded <c>AppDomain.CurrentDomain.BaseDirectory</c>,
        /// so the three arms of <see cref="Load"/> could be asserted only by READING the source. It runs
        /// exactly what production runs, so what the pins exercise is the production path and not a copy
        /// of it.
        /// </summary>
        internal ReportPageConfigService(ILogger<ReportPageConfigService> logger, string configPath)
        {
            _logger = logger;
            _configPath = configPath;
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

        /// <summary>
        /// Reads the layout, and DECIDES PER ARM whether the operator's path may be written.
        ///
        /// <para><b>THE INVARIANT (I1): a customer's configuration must never be silently replaced by a
        /// built-in default.</b></para>
        ///
        /// <para><b>WHAT WAS WRONG BEFORE 2026-09-12 (lane I1-census).</b> This method ended with an
        /// unconditional <c>SaveRoot(BuildDefaults())</c> that THREE arms reached, not one: the file was
        /// absent; <c>Deserialize</c> returned null; and — the one that costs data — the <c>catch</c>, so
        /// ANY read failure, a transient IO error or a half-written file included, persisted this class's
        /// built-in layout over whatever the operator had. <c>SaveRoot</c> is a bare
        /// <c>Directory.CreateDirectory</c> + <c>File.WriteAllText</c> with no backup and no
        /// <c>.rejected-</c> copy, so the damaged file was not merely replaced, it was GONE. That is
        /// strictly worse than <see cref="DashboardConfigService"/>, which copies aside first.</para>
        ///
        /// <para><b>THE ARMS, AND WHY THEY DIFFER.</b> <see cref="ConfigLoadOutcome.Missing"/> is
        /// deliberately NOT damage and IS seeded: no <c>report-pages.json</c> ships in the payload (unlike
        /// <c>dashboard-config.json</c>, which is embedded in the assembly), so there is no better
        /// available default to run from and nothing on disk to lose. <see cref="ConfigLoadOutcome.Empty"/>
        /// and <see cref="ConfigLoadOutcome.Unreadable"/> WRITE NOTHING: the layout is served from memory
        /// for this process and the bytes on disk are left exactly as they are.</para>
        ///
        /// <para><b>The read is <see cref="ConfigFileHelper"/>'s, not a fourth copy of one.</b> That gets
        /// this store the house behaviour it did not have: whitespace is damage rather than "nothing
        /// configured", a file that will not parse is copied aside to <c>.rejected-&lt;utc&gt;</c> before
        /// anything else happens, and the operator is given
        /// <see cref="ConfigFileHelper.DescribeStoreRecovery"/>'s register — conditioned on the copy that
        /// was ACTUALLY taken, never on the one usually taken.</para>
        ///
        /// <para>The pins are <c>Tests\SQLTriage.Tests\ReportPageConfigI1Tests.cs</c>, which exercise this
        /// method through the internal disk constructor rather than reading the source.</para>
        /// </summary>
        private ReportPagesRoot Load()
        {
            var loaded = ConfigFileHelper.Load<ReportPagesRoot>(
                _configPath, SerializerOptions, out _loadOutcome, out var quarantinedPath);

            if (_loadOutcome == ConfigLoadOutcome.Loaded)
                return loaded;

            var defaults = BuildDefaults();

            if (_loadOutcome == ConfigLoadOutcome.Missing)
            {
                // A genuinely fresh install. Nothing ships at this path and nothing is on disk to lose,
                // so seeding it is the honest answer rather than a replacement.
                _logger.LogInformation(
                    "[ReportPages] {Path} does not exist. Seeding the built-in layout, because this product "
                    + "ships no report-pages.json for it to be read from.", _configPath);
                SaveRoot(defaults);
                return defaults;
            }

            // Empty or Unreadable. The operator HAS a file and this process could not read it. Serving
            // defaults from memory is survivable; writing them over that file is the silent replacement
            // this method exists to refuse, so nothing is written here and Save() refuses too.
            _logger.LogError(
                "[ReportPages] {Path} {State}. The report layout is being served from this build's "
                + "built-in defaults for this session. NOTHING HAS BEEN WRITTEN: the file on disk is "
                + "exactly as it was. {Recovery}",
                _configPath,
                _loadOutcome == ConfigLoadOutcome.Empty ? "exists but is empty" : "could not be read",
                ConfigFileHelper.DescribeStoreRecovery(_loadOutcome, quarantinedPath));

            return defaults;
        }

        /// <summary>
        /// Persists the in-memory layout — and REFUSES when that layout is this build's defaults wearing
        /// the operator's name.
        ///
        /// <para>Without this the fix in <see cref="Load"/> would last exactly until the first edit. Load
        /// leaves the damaged file alone and serves defaults; the page then renders those defaults as if
        /// they were the operator's, and one drag of one section would call through here and persist them
        /// — the same loss, one click later, with the evidence gone. That is the measured
        /// Settings ▸ Access Control defect in a second store; see <see cref="StoreWriteIntent"/>.</para>
        ///
        /// <para>The predicate is <see cref="ConfigFileHelper.WouldOverwriteUnreadStore"/>, the shared one,
        /// rather than a local re-statement of it — so this store's refusal cannot drift from the rest.</para>
        /// </summary>
        private void Save()
        {
            if (ConfigFileHelper.WouldOverwriteUnreadStore(_loadOutcome, StoreWriteIntent.FromLoadedStore))
            {
                _logger.LogError(
                    "[ReportPages] REFUSED to save {Path}. This process could not read that file at "
                    + "startup, so what is in memory is this build's defaults and not your layout. Saving "
                    + "it would overwrite a file SQLTriage never read. Nothing was written.", _configPath);
                return;
            }

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
                    Title   = "Audit Assessment",
                    Route   = "/audit",
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
