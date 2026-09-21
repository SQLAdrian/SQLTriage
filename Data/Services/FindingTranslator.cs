/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Translates a technical <see cref="CheckResult"/> into three audience renderings
    /// (DBA, IT Manager, Executive). Results are cached and invalidated when governance
    /// weights are reloaded.
    /// </summary>
    public interface IFindingTranslator
    {
        /// <summary>
        /// Translate a single check result. Returns cached result if available.
        /// </summary>
        Task<FindingTranslation> TranslateAsync(
            CheckResult result,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Translate a batch of check results (e.g. the failing findings shown on a page),
        /// preserving input order. Each result goes through <see cref="TranslateAsync"/> (cached).
        /// </summary>
        Task<List<FindingTranslation>> TranslateFindingsAsync(
            IEnumerable<CheckResult> results,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Bust the translation cache (e.g. after a vulnerability assessment re-run).
        /// </summary>
        void InvalidateCache();
    }

    public sealed class FindingTranslator : IFindingTranslator
    {
        private readonly IMemoryCache _cache;
        private readonly ISqlQueryRepository _queryRepo;
        private readonly IGovernanceWeightsProvider _weightsProvider;
        private readonly ILogger<FindingTranslator> _logger;
        private int _version;

        public FindingTranslator(
            IMemoryCache cache,
            ISqlQueryRepository queryRepo,
            IGovernanceWeightsProvider weightsProvider,
            ILogger<FindingTranslator> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _queryRepo = queryRepo ?? throw new ArgumentNullException(nameof(queryRepo));
            _weightsProvider = weightsProvider ?? throw new ArgumentNullException(nameof(weightsProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Wire cache-bust to provider's WeightsChanged event.
            // Phase 2: WeightsChanged is never fired (no file watcher, no bundle yet).
            // Phase 3 will fire it when a new bundle is decrypted via IBundleAccessor.
            _weightsProvider.WeightsChanged += (_, _) =>
            {
                Interlocked.Increment(ref _version);
                _logger.LogInformation("Governance weights changed — translation cache version bumped to {Version}", _version);
            };
        }

        public Task<FindingTranslation> TranslateAsync(CheckResult result, CancellationToken cancellationToken = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var cacheKey = $"finding:{result.CheckId}:{result.InstanceName}:{_version}";
            if (_cache.TryGetValue(cacheKey, out FindingTranslation? cached) && cached != null)
            {
                _logger.LogDebug("Cache hit for {CheckId} on {Instance}", result.CheckId, result.InstanceName);
                return Task.FromResult(cached);
            }

            var meta = _queryRepo.Get(result.CheckId);
            var translation = Render(result, meta);

            _cache.Set(cacheKey, translation, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(10),
                Size = 1
            });

            return Task.FromResult(translation);
        }

        public async Task<List<FindingTranslation>> TranslateFindingsAsync(IEnumerable<CheckResult> results, CancellationToken cancellationToken = default)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            var list = new List<FindingTranslation>();
            foreach (var r in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(await TranslateAsync(r, cancellationToken).ConfigureAwait(false));
            }
            return list;
        }

        public void InvalidateCache()
        {
            Interlocked.Increment(ref _version);
            _logger.LogInformation("Translation cache manually invalidated — version {Version}", _version);
        }

        // ─────────────────────── Renderers ───────────────────────

        private FindingTranslation Render(CheckResult r, SqlQueryDefinition? meta)
        {
            // Try specific renderer; fall back to generic
            var renderer = _specificRenderers.TryGetValue(r.CheckId, out var fn) ? fn : RenderGeneric;
            return renderer(r, meta);
        }

        /// <summary>
        /// 2026-07-21 (persona board, FIX 4). Renders the numeric tail of a finding's technical
        /// detail — or renders nothing, when the numbers do not mean what the prose beside them
        /// would make a reader think.
        ///
        /// The defect: this method's caller appended a bare "(Actual: N, Expected: M)" to EVERY
        /// finding, whatever contract produced it. /governance therefore shipped the line
        ///
        ///     "No enabled Server Audit Specifications found ... (Actual: 1, Expected: 0)"
        ///
        /// — a sentence asserting none exist, with evidence attached apparently saying one does.
        ///
        /// The layer that is wrong is THIS TEMPLATE, not the comparison and not the check's data.
        /// Traced through CheckExecutionService.ExecuteSingleCheckAsync: for a verdict-contract
        /// check (ResultInterpretation PassFail / PassWarnFail / PassInfo / PassFailSkip /
        /// verdict) the SQL returns a verdict string plus an OPTIONAL "count" column, and
        /// ActualValue is set to <c>countValue ?? 0</c> while ExpectedValue is the check
        /// definition's numeric field, left at its 0 default because that path never consults it.
        /// So for those checks the pair means "the check's own count column returned 1; a numeric
        /// comparison, which this check never performed, would have wanted 0". It is NOT a
        /// measurement of the noun in the message. Both numbers are individually correct and the
        /// pair is meaningless in that position — which is exactly why printing it unlabelled
        /// created a contradiction out of two true values.
        ///
        /// The numeric contract is different: there ActualValue is genuinely what the server
        /// returned and ExpectedValue is genuinely the threshold it was compared against
        /// (<c>result.Passed = result.ActualValue == check.ExpectedValue</c>), so Actual/Expected
        /// is an honest, meaningful pair and is kept verbatim.
        ///
        /// Verdict-contract results therefore render their VERDICT, which is the assertion the
        /// check actually made, and the count only when the check supplied one — never framed as
        /// an "Actual" measurement of anything.
        /// </summary>
        internal static string MeasurementSuffix(CheckResult r)
        {
            if (r == null) return "";

            // Verdict-contract result: Verdict is only ever populated on that path
            // (CheckExecutionService sets result.Verdict in the non-numeric branch).
            if (!string.IsNullOrWhiteSpace(r.Verdict))
            {
                var verdict = r.Verdict.Trim().ToUpperInvariant();
                // "Partial" is the product's name for WARN on every user-facing surface.
                var shown = verdict == "WARN" ? "PARTIAL" : verdict;
                return r.ActualValue > 0
                    ? $" (Verdict: {shown}; the check's count column returned {r.ActualValue})"
                    : $" (Verdict: {shown})";
            }

            // Numeric-contract result: Actual really was compared against Expected.
            return $" (Actual: {r.ActualValue}, Expected: {r.ExpectedValue})";
        }

        private static FindingTranslation RenderGeneric(CheckResult r, SqlQueryDefinition? meta)
        {
            var category = meta?.Category ?? r.Category;
            var severity = meta?.Severity ?? r.Severity;
            var govCategory = MapToGovernanceDimension(category);

            var riskLevel = GetBusinessRisk(severity);
            var effort = GetRemediationEffort(severity);
            var cost = GetEstimatedCost(severity);
            var controls = meta?.Controls?.Any() == true
                ? string.Join(", ", meta.Controls)
                : DefaultControlsFor(govCategory);

            return new FindingTranslation
            {
                CheckId = r.CheckId,
                CheckName = r.CheckName,
                InstanceName = r.InstanceName,
                Passed = r.Passed,
                Verdict = r.Verdict,
                Dba = new FindingDba
                {
                    CheckId = r.CheckId,
                    Title = r.CheckName,
                    TechnicalDetails = $"Category: {category}. Severity: {severity}. {r.Message}{MeasurementSuffix(r)}",
                    TSqlRemediation = string.IsNullOrWhiteSpace(r.RecommendedAction)
                        ? $"Review {category.ToLowerInvariant()} configuration and re-run the check."
                        : r.RecommendedAction,
                    RawData = $"DurationMs={r.DurationMs}, ExecutedAt={r.ExecutedAt:O}, ErrorMessage={r.ErrorMessage ?? "(none)"}"
                },
                ItManager = new FindingItManager
                {
                    BusinessCategory = govCategory,
                    SlaImpact = severity.ToUpperInvariant() switch
                    {
                        "CRITICAL" => "Immediate SLA breach risk. P1 incident may be required.",
                        "HIGH" => "Degraded service within 24h if unaddressed.",
                        "MEDIUM" => "Monitor and schedule remediation within 1 week.",
                        _ => "Low operational impact. Track in backlog."
                    },
                    RemediationEffort = effort,
                    RequiresChangeControl = severity.ToUpperInvariant() is "CRITICAL" or "HIGH",
                    RecommendedAction = string.IsNullOrWhiteSpace(r.RecommendedAction)
                        ? $"Schedule {govCategory.ToLowerInvariant()} review for {r.InstanceName}."
                        : r.RecommendedAction
                },
                Executive = new FindingExecutive
                {
                    // #47 (2026-07-15): some checks return a Message identical to the check's
                    // own (desired-state) title regardless of PASS/FAIL — appending that raw
                    // Message here produced "A critical compliance issue was detected on X:
                    // <title asserting the control IS in place>". FindingPresentation renders
                    // the genuine Message when one exists, else an explicit failure framing.
                    // Ruling #4 (2026-07-20): the Warn arm MUST sit ahead of the raw r.Passed arm.
                    // WARN rides Passed=true, so before this the executive narrative for a check
                    // that could not see the whole server read "The <check> check on <server>
                    // passed. No action required." — a partially-blind run reported to the client
                    // as verified-clean, in the single most quotable sentence in the deliverable.
                    // Neither a pass nor a finding: state plainly that the check could not complete.
                    PlainLanguageSummary = CheckClassification.IsWarn(r)
                        ? $"The {r.CheckName.ToLowerInvariant()} check on {r.InstanceName} could not be fully assessed, so no conclusion was reached about this control. {WarnCause(r)}"
                        : r.Passed
                        ? $"The {r.CheckName.ToLowerInvariant()} check on {r.InstanceName} passed. No action required."
                        : $"A {severity.ToLowerInvariant()} {govCategory.ToLowerInvariant()} issue was detected on {r.InstanceName}: {FindingPresentation.ProblemStatement(r)}",
                    BusinessRisk = riskLevel,
                    EstimatedMonthlyCost = cost,
                    ComplianceControls = controls,
                    RecommendedAction = CheckClassification.IsWarn(r)
                        ? "Re-run this check with an account that can read the whole instance, then re-assess."
                        : r.Passed
                        ? "No action required."
                        : severity.ToUpperInvariant() switch
                        {
                            "CRITICAL" => "Escalate to executive leadership. Budget emergency remediation.",
                            "HIGH" => "Request department head review. Allocate resources for remediation.",
                            "MEDIUM" => "Include in next operational review. Assign owner and timeline.",
                            _ => "No immediate executive action required."
                        }
                }
            };
        }

        // ─── Specific renderers (populated incrementally from .ignore/checks) ───

        private readonly Dictionary<string, Func<CheckResult, SqlQueryDefinition?, FindingTranslation>> _specificRenderers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["VA-001"] = RenderBackupValidation,
            ["TRIAGE_002"] = RenderLogSpace,
            ["TRIAGE_016"] = RenderInvalidLogins,
        };

        private static FindingTranslation RenderBackupValidation(CheckResult r, SqlQueryDefinition? meta)
        {
            var t = RenderGeneric(r, meta);
            t.Dba.TSqlRemediation = @"
-- Identify databases without recent backups
SELECT d.name, MAX(b.backup_finish_date) AS last_backup
FROM sys.databases d
LEFT JOIN msdb.dbo.backupset b ON b.database_name = d.name
WHERE d.recovery_model = 1 AND d.state = 0 AND d.database_id > 4
GROUP BY d.name
HAVING MAX(b.backup_finish_date) < DATEADD(HOUR, -2, GETDATE());

-- Initiate full backup if missing
-- BACKUP DATABASE [?] TO DISK = N'...' WITH CHECKSUM, COMPRESSION;";
            t.ItManager.BusinessCategory = "Reliability";
            t.ItManager.SlaImpact = "RPO violation risk. Data loss window may exceed recovery objectives.";
            // Ruling #4: Warn ahead of r.Passed — see RenderGeneric. RenderGeneric already set an
            // honest Warn summary; overwriting it with the pass text here would reinstate the exact
            // false-clean for the three checks that have a specific renderer.
            t.Executive.PlainLanguageSummary = CheckClassification.IsWarn(r)
                ? t.Executive.PlainLanguageSummary
                : r.Passed
                ? "Backup validation passed. Recovery objectives are within acceptable windows."
                : $"Backup gaps detected on {r.InstanceName}. Recent transaction-log backups are missing, creating data-loss exposure.";
            t.Executive.BusinessRisk = "High — unrecoverable data loss in outage scenario.";
            return t;
        }

        private static FindingTranslation RenderLogSpace(CheckResult r, SqlQueryDefinition? meta)
        {
            var t = RenderGeneric(r, meta);
            t.Dba.TSqlRemediation = @"
-- Shrink only if log chain is broken and backups are confirmed
-- First: BACKUP LOG [?] TO DISK = N'...'
-- Then: DBCC SHRINKFILE([?]_Log, 1024);";
            // Ruling #4: Warn ahead of r.Passed — see RenderBackupValidation.
            t.Executive.PlainLanguageSummary = CheckClassification.IsWarn(r)
                ? t.Executive.PlainLanguageSummary
                : r.Passed
                ? "Transaction log space is within normal parameters."
                : $"Transaction log consumption on {r.InstanceName} is abnormal. Disk exhaustion could halt write operations.";
            return t;
        }

        private static FindingTranslation RenderInvalidLogins(CheckResult r, SqlQueryDefinition? meta)
        {
            var t = RenderGeneric(r, meta);
            t.Dba.TSqlRemediation = @"
-- List orphaned Windows logins
SELECT dp.name, dp.type_desc
FROM sys.database_principals dp
LEFT JOIN sys.server_principals sp ON dp.sid = sp.sid
WHERE sp.sid IS NULL AND dp.type IN ('U','G');";
            t.ItManager.BusinessCategory = "Security";
            t.ItManager.SlaImpact = "Authentication failures may lock out legitimate users or violate access policy.";
            t.Executive.BusinessRisk = "Moderate — stale credentials indicate identity-hygiene gaps.";
            t.Executive.ComplianceControls = "SOC2-CC6.1, ISO27001-A.9.2";
            return t;
        }

        // ─── Helpers ───

        /// <summary>
        /// The check's own account of WHY it could not fully assess the target, for the executive
        /// narrative. Ruling #4 (2026-07-20): the corpus writes that reason into Message when it
        /// downgrades a PASS to WARN ("Could not read 3 of 14 databases (insufficient
        /// permissions)."). We surface the check's words rather than inventing a cause — a
        /// fabricated reason in a client deliverable is worse than a generic one. Falls back to a
        /// non-committal sentence when the check supplied no message.
        /// </summary>
        private static string WarnCause(CheckResult r) =>
            string.IsNullOrWhiteSpace(r.Message)
                ? "The check reported no detail about what it could not read."
                : r.Message.Trim();

        private static string MapToGovernanceDimension(string checkCategory)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Security"] = "Security",
                ["Authentication"] = "Security",
                ["Authorization"] = "Security",
                ["Encryption"] = "Security",
                ["Auditing"] = "Compliance",
                ["Compliance"] = "Compliance",
                ["Data_Protection"] = "Compliance",
                ["Surface_Area"] = "Security",
                ["Configuration"] = "Reliability",
                ["DefaultRuleset"] = "Reliability",
                ["Backup"] = "Reliability",
                ["Monitoring"] = "Reliability",
                ["Reliability"] = "Reliability",
                ["Performance"] = "Performance",
                ["Memory"] = "Performance",
                ["Network"] = "Performance",
                ["Patching"] = "Compliance",
                ["Cost"] = "Cost",
                ["Custom"] = "Reliability"
            };
            return map.TryGetValue(checkCategory, out var dim) ? dim : "Reliability";
        }

        private static string GetBusinessRisk(string severity)
        {
            return severity.ToUpperInvariant() switch
            {
                "CRITICAL" => "Critical — immediate threat to data integrity, availability, or regulatory standing.",
                "HIGH" => "High — significant operational or compliance exposure if not remediated promptly.",
                "MEDIUM" => "Moderate — manageable risk within standard change windows.",
                "LOW" => "Low — minor deviation from best practice.",
                _ => "Informational — no material business risk."
            };
        }

        private static string GetRemediationEffort(string severity)
        {
            return severity.ToUpperInvariant() switch
            {
                "CRITICAL" => "4–8 hours. May require out-of-hours maintenance window.",
                "HIGH" => "2–4 hours. Schedule within next business day.",
                "MEDIUM" => "1–2 hours. Include in next maintenance cycle.",
                "LOW" => "< 1 hour. Can be applied during standard operations.",
                _ => "Minimal. Document and close."
            };
        }

        private static string GetEstimatedCost(string severity)
        {
            return severity.ToUpperInvariant() switch
            {
                "CRITICAL" => "$5,000+ / month (downtime, data loss, regulatory fines).",
                "HIGH" => "$2,000–5,000 / month (SLA penalties, productivity loss).",
                "MEDIUM" => "$500–2,000 / month (incremental support overhead).",
                "LOW" => "<$500 / month (minor operational friction).",
                _ => "$0 — informational only."
            };
        }

        private static string DefaultControlsFor(string govCategory)
        {
            return govCategory switch
            {
                "Security" => "SOC2-CC6.1, ISO27001-A.9.2, NIST-AC-2",
                "Compliance" => "SOC2-CC7.2, ISO27001-A.18.1",
                "Performance" => "ITIL Capacity Management",
                "Reliability" => "SOC2-CC7.4, ISO27001-A.12.3",
                "Cost" => "FinOps Tagging Policy",
                _ => "General IT Governance"
            };
        }
    }
}
