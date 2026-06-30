/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationTemplateStore — the registry of templates the gated remediation
 * lane may apply. Build step 3.
 *
 * Persistence mirrors RemediationWeightStore exactly (lock + atomic
 * tmp->delete->move save, schemaVersion, camelCase JSON, graceful load). The
 * difference: templates are SHIPPED, so the store seeds them in code and the
 * optional JSON overlay only adds/overrides. With no file present the lane still
 * has its registered template (MAXDOP).
 *
 * This store is intended to become the single source of truth for "is this key
 * registered?" — SqlSafetyValidator currently hard-codes that set (step 1 seam).
 * Wiring the validator to consult this store is step 4's job; step 3 only stands
 * the store up and proves it.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    public class RemediationTemplateStore
    {
        private readonly ILogger<RemediationTemplateStore> _logger;
        private readonly string _overlayPath;
        private readonly object _lock = new();
        private Dictionary<string, RemediationTemplate> _templates = new(StringComparer.Ordinal);
        // Keys seeded in code (shipped). An overlay file may ADD new templates but must NEVER
        // override a shipped key's command/queries — otherwise anyone who can write Config/
        // could swap a shipped template's DbatoolsCommand for arbitrary PowerShell and drive
        // it through all five gates. Shipped templates are immutable at runtime by design.
        private readonly HashSet<string> _shippedKeys = new(StringComparer.Ordinal);
        // The sp_configure option names SHIPPED templates remediate. An overlay-added
        // Configuration template may ONLY target one of these — it cannot introduce a new
        // (dangerous) setting (e.g. 'clr enabled', 'Ole Automation Procedures'). Self-maintaining:
        // shipping a template for a new setting widens the allow-list by construction.
        private readonly HashSet<string> _shippedConfigNames = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastUpdatedUtc = DateTime.MinValue;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        // overlayPathOverride is for tests only (point the overlay at a temp file); DI uses the
        // single-arg ctor, which reads the shipped Config/ overlay path.
        public RemediationTemplateStore(ILogger<RemediationTemplateStore> logger, string? overlayPathOverride = null)
        {
            _logger = logger;
            _overlayPath = overlayPathOverride
                ?? Path.Combine(AppContext.BaseDirectory, "Config", "remediation-templates.json");
            SeedShippedTemplates();
            lock (_lock)
            {
                foreach (var t in _templates.Values)
                {
                    _shippedKeys.Add(t.Key);
                    if (t.Operation is { } op && !string.IsNullOrWhiteSpace(op.ConfigName))
                        _shippedConfigNames.Add(op.ConfigName);
                }
            }
            LoadOverlay();
        }

        public string OverlayPath => _overlayPath;
        public DateTime LastUpdatedUtc { get { lock (_lock) return _lastUpdatedUtc; } }
        public int Count { get { lock (_lock) return _templates.Count; } }

        /// <summary>
        /// The shipped, bounded, reversible Configuration templates. Each carries a structured
        /// RemediationOperation (the single source the gate classifies and the executor runs).
        /// Adding one here auto-widens the overlay allow-list to its sp_configure setting.
        /// </summary>
        private void SeedShippedTemplates()
        {
            var maxdop = new RemediationTemplate
            {
                Key = "MAXDOP",
                DisplayName = "Max Degree of Parallelism",
                Description = "Set 'max degree of parallelism' to the recommended value via dbatools.",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Configuration, // sp_configure/RECONFIGURE: snapshot-based rollback
                Type = RemediationType.MaxdopParamSniff,
                DbatoolsCommand = "Set-DbaMaxDop",
                // The structured change: the SINGLE SOURCE the gate classifies and the
                // executor runs. MAXDOP is an advanced option, so the rendered batch first
                // enables 'show advanced options'. Value is bound from the 'MaxDop' param,
                // bounds-checked to [0, 64].
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "max degree of parallelism",
                    AdvancedOption = true,
                    ValueParam = "MaxDop",
                    MinValue = 0,
                    MaxValue = 64,
                },
                // Pre-change state: the running MAXDOP value (value_in_use), as a SCALAR
                // (single column) so ExecuteScalar captures the value — not the row's
                // 'name'. This is the rollback target and the verify baseline. Read-only;
                // classifies as Safe.
                SnapshotQuery =
                    "SELECT value_in_use FROM sys.configurations " +
                    "WHERE name = 'max degree of parallelism';",
                // Post-change confirmation: re-read value_in_use.
                VerifyQuery =
                    "SELECT value_in_use FROM sys.configurations " +
                    "WHERE name = 'max degree of parallelism';",
                Reversible = true,
            };

            // Cost Threshold for Parallelism — the companion to MAXDOP. The shipped default
            // of 5 is widely considered too low; the operator picks the target. Advanced option.
            var ctfp = new RemediationTemplate
            {
                Key = "CTFP",
                DisplayName = "Cost Threshold for Parallelism",
                Description = "Set 'cost threshold for parallelism' to the chosen value (the shipped default of 5 is widely considered too low).",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Configuration,
                Type = RemediationType.MaxdopParamSniff,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "cost threshold for parallelism",
                    AdvancedOption = true,
                    ValueParam = "CostThreshold",
                    MinValue = 0,
                    MaxValue = 32767, // SQL Server's valid range for this option
                },
                // Configuration reads are DERIVED from the op (RemediationOpRenderer.TryRenderRead),
                // so no snapshot/verify text is needed here (MAXDOP's are vestigial).
                Reversible = true,
            };

            // Optimize for Ad Hoc Workloads — an always-safe toggle that cuts single-use
            // plan-cache bloat. Bit (0/1). Advanced option; instantly reversible.
            var optAdHoc = new RemediationTemplate
            {
                Key = "OPTIMIZEFORADHOC",
                DisplayName = "Optimize for Ad Hoc Workloads",
                Description = "Enable 'optimize for ad hoc workloads' (1) to reduce single-use plan-cache bloat.",
                RiskClass = RemediationRiskClass.Trivial,
                Kind = RemediationKind.Configuration,
                Type = RemediationType.Config,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "optimize for ad hoc workloads",
                    AdvancedOption = true,
                    ValueParam = "Enabled",
                    MinValue = 0,
                    MaxValue = 1,
                },
                Reversible = true,
            };

            // Max Server Memory — cap uncapped instances (the default 2147483647 MB). The operator
            // supplies the MB cap. NOT an advanced option. A stability fix, not a CPU-work reducer,
            // so its power band is suppressed (ShowPowerBand=false) to avoid overstating a saving.
            var maxmem = new RemediationTemplate
            {
                Key = "MAXSERVERMEMORY",
                DisplayName = "Max Server Memory (MB)",
                Description = "Cap 'max server memory (MB)' so SQL Server doesn't starve the OS (default is uncapped).",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Configuration,
                Type = RemediationType.Config,
                ShowPowerBand = false, // memory cap is a stability fix, not a CPU/I-O-work reduction
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "max server memory (MB)",
                    AdvancedOption = false,
                    ValueParam = "MaxServerMemoryMb",
                    MinValue = 128,          // SQL Server's documented minimum
                    MaxValue = 2147483647,   // SQL Server's max (= uncapped default)
                },
                Reversible = true,
            };

            // Backup Compression Default — compress ad-hoc and maintenance-plan backups by
            // default (smaller, faster restores) without per-backup syntax. Bit (0/1), advanced
            // option, instantly reversible. Compression spends CPU during backup, so its power
            // band is suppressed (not a CPU-work reduction).
            var backupCompression = new RemediationTemplate
            {
                Key = "BACKUPCOMPRESSION",
                DisplayName = "Backup Compression Default",
                Description = "Enable 'backup compression default' (1) so ad-hoc and maintenance-plan backups are compressed without per-backup syntax.",
                RiskClass = RemediationRiskClass.Trivial,
                Kind = RemediationKind.Configuration,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "backup compression default",
                    AdvancedOption = true,
                    ValueParam = "Enabled",
                    MinValue = 0,
                    MaxValue = 1,
                },
                Reversible = true,
            };

            // Default Trace Enabled — SQL Server's lightweight always-on trace of configuration
            // and security events (CIS 5.2). Bit (0/1), advanced option, instantly reversible.
            // Observability, not a CPU-work reduction, so its power band is suppressed.
            var defaultTrace = new RemediationTemplate
            {
                Key = "DEFAULTTRACE",
                DisplayName = "Default Trace Enabled",
                Description = "Enable 'default trace enabled' (1) so SQL Server captures configuration and security events in its lightweight default trace (CIS 5.2).",
                RiskClass = RemediationRiskClass.Trivial,
                Kind = RemediationKind.Configuration,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "default trace enabled",
                    AdvancedOption = true,
                    ValueParam = "Enabled",
                    MinValue = 0,
                    MaxValue = 1,
                },
                Reversible = true,
            };

            // Cross DB Ownership Chaining — disable instance-wide cross-database ownership
            // chaining (CIS 2.3), a privilege-escalation surface rarely intentionally on. Bit
            // (0/1), NOT an advanced option, online, reversible. Security posture, not a CPU
            // reduction, so its power band is suppressed.
            var crossDbOwnership = new RemediationTemplate
            {
                Key = "CROSSDBOWNERSHIP",
                DisplayName = "Cross DB Ownership Chaining",
                Description = "Disable 'cross db ownership chaining' (0) instance-wide to remove a privilege-escalation surface (CIS 2.3).",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Configuration,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "cross db ownership chaining",
                    AdvancedOption = false,
                    ValueParam = "Enabled",
                    MinValue = 0,
                    MaxValue = 1,
                },
                Reversible = true,
            };

            // Ad Hoc Distributed Queries — disable instance-wide OPENROWSET/OPENDATASOURCE ad-hoc
            // access (CIS 2.1), a surface-area reduction. Bit (0/1), advanced option, online,
            // reversible. Behaviour-changing: a workload that legitimately uses OPENROWSET would
            // break, so the operator chooses the target in the UI (and can re-enable). Security
            // posture, not a CPU reduction, so the power band is suppressed.
            var adHocDistributedQueries = new RemediationTemplate
            {
                Key = "ADHOCDISTRIBUTEDQUERIES",
                DisplayName = "Ad Hoc Distributed Queries",
                Description = "Disable 'Ad Hoc Distributed Queries' (0) to remove OPENROWSET/OPENDATASOURCE ad-hoc access (CIS 2.1). Re-enable if a workload legitimately depends on it.",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Configuration,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.SpConfigure,
                    ConfigName = "Ad Hoc Distributed Queries",
                    AdvancedOption = true,
                    ValueParam = "Enabled",
                    MinValue = 0,
                    MaxValue = 1,
                },
                Reversible = true,
            };

            // Add a missing index — the FIRST write past sp_configure. The index spec (database/
            // schema/table/name/key+included columns) is supplied per-request from a missing-index
            // DMV candidate (every identifier charset-guarded AND bracket-quoted by the renderer);
            // the gate classifies the representative CREATE INDEX as Remediation under this key.
            // Transactable kind: rollback is the clean inverse (DROP INDEX the index we created).
            // Behaviour-changing (a new index adds write overhead), so the power band is suppressed
            // and the operator picks the candidate. ShipS the EXACT index from the DMV — never synthesised.
            var addMissingIndex = new RemediationTemplate
            {
                Key = "ADDMISSINGINDEX",
                DisplayName = "Add Missing Index",
                Description = "Create a recommended missing index (CREATE INDEX) from a sys.dm_db_missing_index_* candidate. Rollback drops the index. The operator selects the candidate; the exact index definition ships from the DMV.",
                RiskClass = RemediationRiskClass.Sensitive,
                Kind = RemediationKind.Transactable, // DDL: snapshot=exists?, apply, verify, rollback=DROP
                Type = RemediationType.IndexAddRebuild,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.CreateIndex,
                    // No ConfigName/value — the index identifiers ride request parameters
                    // (Index.Database/Schema/Table/Name/KeyColumns/IncludedColumns), guarded by the renderer.
                },
                Reversible = true,
            };

            lock (_lock)
            {
                _templates[maxdop.Key] = maxdop;
                _templates[ctfp.Key] = ctfp;
                _templates[optAdHoc.Key] = optAdHoc;
                _templates[maxmem.Key] = maxmem;
                _templates[backupCompression.Key] = backupCompression;
                _templates[defaultTrace.Key] = defaultTrace;
                _templates[crossDbOwnership.Key] = crossDbOwnership;
                _templates[adHocDistributedQueries.Key] = adHocDistributedQueries;
                _templates[addMissingIndex.Key] = addMissingIndex;
            }
        }

        private void LoadOverlay()
        {
            try
            {
                if (!File.Exists(_overlayPath))
                {
                    _logger.LogInformation("No remediation template overlay at {Path}; using shipped templates only.", _overlayPath);
                    return;
                }
                var json = File.ReadAllText(_overlayPath);
                var payload = JsonSerializer.Deserialize<TemplatePayload>(json, _jsonOptions);
                if (payload?.Templates == null) return;
                lock (_lock)
                {
                    foreach (var t in payload.Templates)
                    {
                        if (string.IsNullOrWhiteSpace(t.Key)) continue;
                        if (_shippedKeys.Contains(t.Key))
                        {
                            // SECURITY: a shipped template is immutable — the overlay cannot
                            // replace its command/queries. It may only ADD new keys.
                            _logger.LogWarning("Remediation overlay tried to override shipped template '{Key}'; ignored (shipped templates are immutable).", t.Key);
                            continue;
                        }
                        // SECURITY: an overlay-added template must be a Configuration op whose
                        // sp_configure setting a SHIPPED template already remediates. This stops
                        // anyone who can write Config/ from minting an authorised template that
                        // targets a dangerous setting (clr enabled, Ole Automation, etc.) — the
                        // structured-op equivalent of the DbatoolsCommand-swap guarded above.
                        // BOTH the template Kind AND the op's OpKind are pinned: otherwise a
                        // kind=Configuration entry carrying operation.opKind=CreateIndex (on a
                        // shipped ConfigName) would pass this guard yet route to the CreateIndex
                        // executor (which keys off OpKind), defeating the invariant.
                        if (t.Kind != RemediationKind.Configuration
                            || t.Operation is null
                            || t.Operation.OpKind != RemediationOpKind.SpConfigure
                            || string.IsNullOrWhiteSpace(t.Operation.ConfigName)
                            || !_shippedConfigNames.Contains(t.Operation.ConfigName))
                        {
                            _logger.LogWarning("Remediation overlay template '{Key}' rejected: only a Configuration/SpConfigure op targeting a shipped sp_configure setting may be added (got kind={Kind}, opKind={OpKind}, configName='{Cfg}').",
                                t.Key, t.Kind, t.Operation?.OpKind, t.Operation?.ConfigName ?? "(none)");
                            continue;
                        }
                        _templates[t.Key] = t; // overlay may only ADD a constrained, non-shipped template
                    }
                    _lastUpdatedUtc = payload.LastUpdatedUtc;
                }
                _logger.LogInformation("Loaded remediation template overlay ({Count} entries, last updated {Updated:u}); {Total} templates total.",
                    payload.Templates.Count, _lastUpdatedUtc, _templates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load remediation template overlay from {Path}", _overlayPath);
            }
        }

        /// <summary>True if a template with this exact key is registered.</summary>
        public bool IsRegistered(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            lock (_lock) return _templates.ContainsKey(key);
        }

        /// <summary>Returns the registered template for a key, or null.</summary>
        public RemediationTemplate? TryGet(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            lock (_lock) return _templates.TryGetValue(key, out var t) ? t : null;
        }

        /// <summary>Snapshot of all registered templates.</summary>
        public IReadOnlyList<RemediationTemplate> All()
        {
            lock (_lock) return _templates.Values.ToList();
        }

        /// <summary>The registered keys (the authorisation set).</summary>
        public IReadOnlySet<string> RegisteredKeys()
        {
            lock (_lock) return _templates.Keys.ToHashSet(StringComparer.Ordinal);
        }

        // NOTE: templates are shipped/read-only in step 3, so no Save() is exposed
        // here yet. When a later step needs to persist an overlay, copy the atomic
        // tmp->delete->move Save() from RemediationWeightStore verbatim — the
        // TemplatePayload below is already shaped for it (schemaVersion + camelCase).

        private class TemplatePayload
        {
            [JsonPropertyName("schemaVersion")]
            public int SchemaVersion { get; set; } = 1;

            [JsonPropertyName("lastUpdatedUtc")]
            public DateTime LastUpdatedUtc { get; set; }

            [JsonPropertyName("templates")]
            public List<RemediationTemplate> Templates { get; set; } = new();
        }
    }
}
