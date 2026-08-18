/* In the name of God, the Merciful, the Compassionate */

// Orchestrator for the Risk Assessment report: source → meta → Task.Run(Build).
// Mirrors ReportBundleService's shape. DETACHABLE: registered only in the full
// (non-community) build; the whole RiskReport folder is Compile-Removed otherwise.

#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.RiskReport
{
    public sealed class RiskAssessmentService
    {
        private readonly IRiskAssessmentSource _source;
        private readonly UserSettingsService _userSettings;
        private readonly ILogger<RiskAssessmentService> _logger;

        public RiskAssessmentService(
            IRiskAssessmentSource source,
            UserSettingsService userSettings,
            ILogger<RiskAssessmentService> logger)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
            _logger = logger;
        }

        /// <summary>Load + render the Risk Assessment PDF for one customer domain.</summary>
        public async Task<byte[]> BuildPdfAsync(string domain, RiskReportFilters filters)
        {
            var report = await _source.LoadAsync(domain, filters).ConfigureAwait(false);

            var nowUtc = DateTime.UtcNow;
            var meta = new RiskAssessmentMeta
            {
                Customer = string.IsNullOrWhiteSpace(report.Header.Customer) ? domain : report.Header.Customer,
                GeneratedUtc = nowUtc.ToString("yyyy-MM-ddTHH:mmZ"),
                TimezoneId = TimeZoneInfo.Local.StandardName,
                RunId = Guid.NewGuid().ToString("N")[..8],
                ColorBlind = _userSettings.GetColorBlindMode(),
            };

            _logger.LogInformation("Risk Assessment report: domain={Domain} rows={Rows}", domain, report.Rows.Count);
            return await Task.Run(() => RiskAssessmentPdf.Build(report, meta)).ConfigureAwait(false);
        }
    }
}
