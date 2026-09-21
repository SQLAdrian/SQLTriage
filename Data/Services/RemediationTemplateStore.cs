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
using System.Threading;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    public class RemediationTemplateStore
    {
        private readonly ILogger<RemediationTemplateStore> _logger;
        private readonly string _overlayPath;
        private readonly object _lock = new();
        private Dictionary<string, RemediationTemplate> _templates = new(StringComparer.Ordinal);
        // Keys THIS store added from a corpus load (task #39) — tracked so a re-load (e.g. the
        // corpus reloads after a bundle change) is idempotent: re-run cleanly removes its own
        // prior additions first rather than accumulating duplicate entries across calls.
        private readonly HashSet<string> _corpusAddedKeys = new(StringComparer.Ordinal);
        public int CorpusOneClickCount { get; private set; }
        public int CorpusDedupedCount { get; private set; }
        // Checks whose remediation.operation block did not parse into a one-click template, each
        // with the parse problem. Held as a LIST, not a bare tally: the count used to be the only
        // trace, and a malformed check was then counted in the page's summary line while appearing
        // in neither UI list (honesty hunt r1-07). The UI now renders these with their reason.
        private List<CorpusMalformedCheck> _corpusMalformed = new();
        // Derived from the list on purpose, so a count that disagrees with what the page can show
        // is not expressible.
        public int CorpusMalformedCount { get { lock (_lock) return _corpusMalformed.Count; } }

        /// <summary>One corpus check whose remediation.operation block could not become a one-click template.</summary>
        public sealed record CorpusMalformedCheck(string CheckId, string Reason);

        /// <summary>Snapshot of the checks the last corpus load rejected as malformed, with their reasons.</summary>
        public IReadOnlyList<CorpusMalformedCheck> CorpusMalformedChecks
        {
            get { lock (_lock) return _corpusMalformed.ToList(); }
        }

        /// <summary>The parse problem recorded for this check id, or null if it was not rejected as malformed.</summary>
        public string? DescribeCorpusMalformed(string? checkId)
        {
            if (string.IsNullOrWhiteSpace(checkId)) return null;
            lock (_lock)
            {
                foreach (var m in _corpusMalformed)
                    if (string.Equals(m.CheckId, checkId, StringComparison.Ordinal)) return m.Reason;
                return null;
            }
        }

        /// <summary>True when the last corpus load rejected this check id as malformed.</summary>
        public bool IsCorpusMalformed(string? checkId) => DescribeCorpusMalformed(checkId) is not null;

        /// <summary>
        /// True when this check belongs in the Remediation page's preview-only list: it carries
        /// corpus remediation guidance but no one-click fix this build will run.
        /// <para>Three ways in — no operation block, <c>auto_fixable: false</c>, or an operation
        /// block THIS STORE rejected as malformed. The third clause is why the predicate lives
        /// here and not inline on the page: the page's filter and the malformed set were exact
        /// logical complements, so a rejected fix was counted in the page's summary line and then
        /// appeared in neither list (honesty hunt r1-07). One function, one answer.</para>
        /// </summary>
        public bool IsPreviewOnlyCheck(SqlCheck? check)
        {
            var remediation = check?.Remediation;
            if (remediation is null) return false;
            return remediation.Operation is null
                   || !remediation.AutoFixable
                   || IsCorpusMalformed(check!.Id);
        }

        // Bumped on every mutation of the template set — including LoadCorpusTemplates, which also
        // mutates existing templates in place (the dedup path appends to a built-in's
        // ResolvesCheckIds). CheckResolutionLookup is a DI singleton that indexes this set, and it
        // keys its index on this number so a runtime corpus load cannot leave a stale index behind
        // (honesty hunt r1-08). Read without the lock on purpose: a reader that samples this BEFORE
        // snapshotting the templates can only ever rebuild once too often, never answer stale.
        private int _generation;

        /// <summary>Increments whenever the registered template set changes. See <see cref="Remediation.CheckResolutionLookup"/>.</summary>
        public int Generation => Volatile.Read(ref _generation);
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
                    // No RecommendedValue: the right MAXDOP depends on cores, NUMA layout and
                    // workload. The app does not know it, so it pre-fills nothing and the
                    // operator supplies the target. Saying "0" here would be a guess wearing a
                    // recommendation's clothes.
                    RecommendedValue = null,
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
                // DEDUPE LINKAGE (Adrian's ruling 2026-09-01, plan item 1.3). These three corpus
                // checks all detect the SAME setting this built-in already fixes one-click. They
                // carry op_kind but auto_fixable:false, so the corpus loader's dedupe path never
                // reaches them — without this wiring a failed MAXDOP check offers no fix even
                // though the fix has shipped for months. Linking adds NO new execution path and NO
                // new risk: it points three findings at one existing, gate-blessed template. Every
                // id below was grepped from the corpus `id:` frontmatter, not guessed, and
                // RemediationDedupeLinkageTests pins all nine so a corpus rename fails a test
                // instead of silently unlinking a fix.
                // ⚠ SQLT-CUSTOM-MAXDOP carries `provenance: custom — needs human sign-off` in the
                // corpus; this linkage does not launder that flag, and the UI must keep showing it.
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-BPCHK-00220-PARALLELISM-MAXDOP",
                    "SQLT-CUSTOM-MAXDOP",
                    "SQLT-FRONTIER-MAXDOP-CXPACKET",
                },
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
                    // No RecommendedValue: the description says the shipped 5 is too low, which
                    // names a direction, not a number. The operator picks the target.
                    RecommendedValue = null,
                },
                // Configuration reads are DERIVED from the op (RemediationOpRenderer.TryRenderRead),
                // so no snapshot/verify text is needed here (MAXDOP's are vestigial).
                Reversible = true,
                // DEDUPE LINKAGE (plan item 1.3) — see the MAXDOP block above for the reasoning.
                // NOTE the id is genuinely truncated in the corpus at "...-DEFAULT-5-IS-O"; that is
                // the real frontmatter value (grepped, and it is also the check's filename), not a
                // paste that lost its tail. SQLT-VA-COST-THRESHOLD-PARALLELISM is a DIFFERENT check
                // on the same setting and is deliberately NOT linked here — it was not in the ruled
                // set of nine, and it disagrees with this one about whether a threshold of 5 fails.
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-CORE-TUNE-COST-THRESHOLD-FOR-PARALLELISM-DEFAULT-5-IS-O",
                },
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
                    RecommendedValue = 1, // the target this template's own description declares
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
                    // No RecommendedValue: the cap is a function of host RAM and what else runs
                    // on the box. Pre-filling MinValue here offered a 128 MB cap on every server.
                    RecommendedValue = null,
                },
                Reversible = true,
                // DEDUPE LINKAGE (plan item 1.3) — see the MAXDOP block above for the reasoning.
                // ⚠ All three are "cap the memory" findings and this template has NO
                // RecommendedValue: the operator types the cap. Linking them does not make the
                // number automatic, and the UI must keep asking for it.
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-BLITZ-MAX-MEMORY-SET-TOO-HIGH",
                    "SQLT-BPCHK-00280-MEMORY-ISSUES-MAXSERVERMEM",
                    "SQLT-VA-MAX-SERVER-MEMORY",
                },
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
                    RecommendedValue = 1, // the target this template's own description declares
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
                    RecommendedValue = 1, // the target this template's own description declares
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
                    RecommendedValue = 0, // the target this template's own description declares
                },
                Reversible = true,
                // DEDUPE LINKAGE (plan item 1.3) — see the MAXDOP block above for the reasoning.
                // The corpus check's own caveat ("deliberate opt-in") is already true of this
                // built-in, which is exactly why linking adds no risk the operator does not already
                // face when clicking the shipped fix.
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-VA-CROSS-DB-OWNERSHIP",
                },
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
                    RecommendedValue = 0, // the target this template's own description declares
                },
                Reversible = true,
                // DEDUPE LINKAGE (plan item 1.3) — see the MAXDOP block above for the reasoning.
                // The corpus check warns this breaks OPENROWSET/OPENDATASOURCE callers. That break
                // ALREADY ships via this built-in and its description says so; linking the finding
                // does not introduce it.
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-VA-AD-HOC-QUERIES-OFF",
                },
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

            // Agent alert + operator pack — the standard SQL Agent alert set: one operator
            // (name + email), sp_add_alert for severities 19-25 and error numbers 823/824/825 (10
            // alerts total), and an email sp_add_notification per alert. Every CREATE is guarded
            // IF NOT EXISTS so re-apply is a no-op. Transactable kind: rollback drops only the
            // alerts/operator the apply created (snapshot-driven — see RemediationOpRenderer).
            // Gated off entirely when SQL Server Agent is unavailable (Express edition has no
            // Agent) via RemediationOpRenderer.AgentAvailabilityProbe — the executor/UI check this
            // before offering the fix, never a broken apply on an Agent-less server. Power band
            // suppressed: this is observability/alerting, not a CPU-work reduction.
            var agentAlertPack = new RemediationTemplate
            {
                Key = "AGENTALERTPACK",
                DisplayName = "Agent Alert + Operator Pack",
                Description = "Create a SQL Agent operator and the standard severity 19-25 + corruption (823/824/825) alerts, each emailing the operator. Idempotent: safe to re-apply. Rollback removes only what this apply created.",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Transactable, // msdb object creation: snapshot=which names exist?, apply, verify, rollback=drop-created-only
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.AgentAlertPack,
                    // No ConfigName/value — the operator name/email ride request parameters
                    // (AgentAlertPack.OperatorName/OperatorEmail), resolved+guarded by the renderer.
                },
                Reversible = true,
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-BPCHK-01390-AGENT-ALERTS-SEVERITY-19",
                    "SQLT-BPCHK-01400-AGENT-ALERTS-SEVERITY-20",
                    "SQLT-BPCHK-01410-AGENT-ALERTS-SEVERITY-21",
                    "SQLT-BPCHK-01420-AGENT-ALERTS-SEVERITY-22",
                    "SQLT-BPCHK-01430-AGENT-ALERTS-SEVERITY-23",
                    "SQLT-BPCHK-01440-AGENT-ALERTS-SEVERITY-24",
                    // Severity 25 has no corresponding corpus check yet (verified by grep — not guessed).
                    "SQLT-BLITZ-NO-OPERATORS",
                    "SQLT-BLITZ-CORRUPTION-ALERTS",
                },
            };

            // Install Ola Hallengren's Maintenance Solution (embedded, checksum-pinned script) —
            // the flagship fix for the corpus's largest failed class: CHECKDB not run, no full
            // backups, log-backup recency, msdb history bloat. Install-if-absent: snapshot checks
            // whether the 4 core procs already exist; if so, apply is a NoOp (the script is never
            // re-run over an existing install). Installs WITHOUT jobs (@CreateJobs='N') — the four
            // schedule tickboxes (CHECKDB/full/diff/log backup) create jobs independently so the
            // operator controls which lanes run. Rollback is a NAMED uninstall: drop the 4 procs +
            // CommandLog + only the jobs this apply's tickboxes created. Power band suppressed:
            // this is reliability/observability tooling, not a CPU-work reduction.
            var installMaintenanceSolution = new RemediationTemplate
            {
                Key = "INSTALLMAINTENANCESOLUTION",
                DisplayName = "Install Maintenance Solution (Ola Hallengren)",
                Description = "Install Ola Hallengren's Maintenance Solution (CommandExecute, DatabaseBackup, " +
                    "DatabaseIntegrityCheck, IndexOptimize + CommandLog) from an embedded, checksum-verified copy " +
                    "of the official MIT-licensed script. Installs the procedures only (no jobs); use the schedule " +
                    "tickboxes to create CHECKDB/backup jobs. If already installed, this is a no-op. Rollback " +
                    "uninstalls only what this apply created.",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Transactable, // proc/table creation: snapshot=procs exist?, apply, verify, rollback=drop-created-only
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.InstallMaintenanceSolution,
                    // No ConfigName/value — @BackupDirectory (for the backup-lane tickboxes) and the
                    // schedule tickbox flags ride request parameters, resolved+guarded by the renderer.
                },
                Reversible = true,
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY",
                    "SQLT-BLITZ-CORRUPTION-CHECKS-NOT-OPTIMAL",
                    "SQLT-BPCHK-00580-NO-FULL-BACKUPS",
                    "SQLT-BLITZ-BACKUP-RECENCY",
                    "SQLT-BLITZ-LOG-BACKUP-RECENCY",
                    "SQLT-BLITZ-MSDB-BACKUP-HISTORY-NOT-PURGED",
                    "SQLT-VA-MSDB-BACKUP-HISTORY-SIZE",
                },
                // S1: creating a schedule job proves nothing until the job has RUN. A verified
                // schedule-lane apply schedules a follow-up check ("job has >=1 successful run in
                // msdb.dbo.sysjobhistory") with an 8-day window — wide enough for CheckDbWeekly's
                // first scheduled run. Install-only applies render no plan and schedule nothing
                // (see DeferredVerifyPlan).
                DeferredVerify = new DeferredVerifySpec(
                    "The created maintenance schedule job has completed at least one successful run.", 8),
            };

            // Backup-NOW — lane S5, the highest-blast-radius template shipped: a gated
            // ONE-SHOT BACKUP DATABASE against the LIVE server. Sensitive risk class (it
            // reads/writes real production data files under the hood, and its command
            // timeout is measured in hours, not seconds). NOT reversible — see
            // RemediationOpKind.BackupDatabaseNow's doc comment: a completed backup only
            // creates a new file, so "rollback" has no meaning here (honest, not a gap).
            // No power/tuning band — this is an availability/DR action, not CPU work.
            var backupDatabaseNow = new RemediationTemplate
            {
                Key = "BACKUPDATABASENOW",
                DisplayName = "Backup Database Now",
                Description = "Run a full BACKUP DATABASE against the live server right now, to an operator-chosen " +
                    "directory. Gated behind a resource check (estimated backup size vs free space on the target " +
                    "drive, with 20% headroom) and a REQUIRED explicit confirmation — this touches a live production " +
                    "database for the duration of the backup. Not reversible (a backup only creates a new file; " +
                    "there is nothing to roll back).",
                RiskClass = RemediationRiskClass.Sensitive,
                Kind = RemediationKind.Transactable, // BACKUP DATABASE is its own atomic server-side operation, same non-XACT shape as CreateIndex
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.BackupDatabaseNow,
                    // No ConfigName/value — database name, backup directory, checksum/system-db
                    // opt-ins, and the confirm token all ride request parameters, resolved+guarded
                    // by BackupCheckDbOpRenderer.
                },
                Reversible = false,
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-BPCHK-00580-NO-FULL-BACKUPS",
                    "SQLT-BLITZ-BACKUP-RECENCY",
                    "SQLT-BLITZ-LOG-BACKUP-RECENCY",
                },
            };

            // CHECKDB-NOW — lane S5's companion: a gated ONE-SHOT DBCC CHECKDB against
            // the LIVE server. Sensitive risk class + hours-scale command timeout, same
            // as Backup-NOW. Never renders WITH TABLOCK (would block the live workload)
            // or a repair option (repair is a separate, human-reviewed decision). NOT
            // reversible — CHECKDB is a read-only consistency pass; there is no change
            // to roll back. No power band — reliability/observability, not CPU work.
            var checkDbNow = new RemediationTemplate
            {
                Key = "CHECKDBNOW",
                DisplayName = "CHECKDB Now",
                Description = "Run DBCC CHECKDB against the live server right now, on an operator-chosen database. " +
                    "Gated behind a resource check (CHECKDB's internal-snapshot space heuristic vs free space on " +
                    "the database's own data volume) and a REQUIRED explicit confirmation — this touches a live " +
                    "production database for the duration of the check. Not reversible (a consistency check makes " +
                    "no changes; there is nothing to roll back). Never runs WITH TABLOCK or a repair option.",
                RiskClass = RemediationRiskClass.Sensitive,
                Kind = RemediationKind.Transactable,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.CheckDbNow,
                    // No ConfigName/value — database name, PHYSICAL_ONLY opt-in, and the confirm
                    // token all ride request parameters, resolved+guarded by BackupCheckDbOpRenderer.
                },
                Reversible = false,
                // NOTE: SQLT-BPCHK-DBCC-CHECKDB-STATUS is DELIBERATELY excluded — it is corpus-ruled
                // "maintenance work, not a state flip" and is already claimed by the review-only
                // MaintenanceGenerator.CheckDb path (CheckResolutionLookup._maintenanceMap). Adding it
                // here would shadow that mapping (CheckResolutionLookup.Resolve checks the one-click
                // template map first) — verified live by CheckResolutionLookupTests.
                // DbccCheckdbStatusCheck_ResolvesToCheckDbGenerator, which this template must not break.
                ResolvesCheckIds = new List<string>
                {
                    "SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY",
                    "SQLT-BLITZ-CORRUPTION-CHECKS-NOT-OPTIMAL",
                },
            };

            // AG PRIMARY GUARD — inject a primary-replica guard as step 1 of an existing Agent
            // job, so scheduled work only does its work on the replica that currently owns the
            // database. Standard risk (msdb DDL on one named job, fully reversible via a named
            // inverse), but priced at ONE credit rather than the derived two: this is applied
            // per job across a whole instance, and a 30-job estate should not cost 60 credits
            // to make failover-safe. No power band — this is a correctness/HA fix, not CPU work.
            var agPrimaryGuard = new RemediationTemplate
            {
                Key = "AGPRIMARYGUARD",
                DisplayName = "AG primary-replica guard (Agent job step 1)",
                Description = "Insert a first step into an existing SQL Agent job that checks " +
                    "sys.fn_hadr_is_primary_replica for an operator-chosen database and quits with failure when " +
                    "this replica is not the primary. Makes a job safe to exist on every replica. Idempotent " +
                    "(re-running is a no-op) and reversible (the inverse removes the injected step and restores " +
                    "the job's original start step). Only offered for jobs whose steps use quit / next-step flow; " +
                    "jobs wired with explicit go-to-step targets are surfaced for manual review instead.",
                RiskClass = RemediationRiskClass.Standard,
                // Transactable, NOT Configuration: in this store Configuration means the
                // sp_configure shape (ConfigName + a bounded integer value). This op carries
                // neither — it is msdb object manipulation with a snapshot-driven named
                // inverse, exactly like AGENTALERTPACK above.
                Kind = RemediationKind.Transactable,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation
                {
                    OpKind = RemediationOpKind.AgentJobPrimaryGuard,
                    // Job name + database name ride request parameters, guarded by AgPrimaryGuardOpRenderer.
                },
                Reversible = true,
                CreditCostOverride = 1,
                ResolvesCheckIds = new List<string>(),
            };

            // SYNC AGENT JOB — recreate one job on an AG secondary from the primary's definition.
            // Standard risk: it writes to msdb on one named job, is snapshot-reversible, and the
            // role resolver refuses unless source is PRIMARY and target SECONDARY in the same AG.
            // Priced per job at 1 for the same reason as the guard — mirroring a 30-job estate
            // should not cost 60 credits.
            var syncAgentJob = new RemediationTemplate
            {
                Key = "SYNCAGENTJOB",
                DisplayName = "Sync Agent job to the AG secondary",
                Description = "Recreate one SQL Agent job on an availability-group secondary from the primary's " +
                    "definition — steps, flow wiring, schedules and enabled state. Drop-and-recreate: the job's run " +
                    "history on the secondary is lost, and the preview says so before approval. Reversible (the " +
                    "secondary's prior definition is captured first). Never deletes jobs that exist only on the secondary.",
                RiskClass = RemediationRiskClass.Standard,
                Kind = RemediationKind.Transactable,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation { OpKind = RemediationOpKind.AgentJobSync },
                Reversible = true,
                CreditCostOverride = 1,
                ResolvesCheckIds = new List<string>(),
            };

            // DELETE EXTRA JOB — a job that exists only on the secondary. Its own key so the sync
            // path can never reach it. Sensitive AND not reversible: msdb keeps no copy of a
            // dropped job, so the derived price (2 + 1) is the honest one; no override.
            var deleteExtraJob = new RemediationTemplate
            {
                Key = "DELETEEXTRAJOB",
                DisplayName = "Delete a job that exists only on the secondary",
                Description = "Drop a SQL Agent job present on the secondary but not on the primary. Opt-in per job, " +
                    "never part of a sync. NOT reversible — msdb retains no copy of a deleted job, so there is nothing " +
                    "to restore from.",
                RiskClass = RemediationRiskClass.Sensitive,
                Kind = RemediationKind.Transactable,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = new RemediationOperation { OpKind = RemediationOpKind.AgentJobDeleteExtra },
                Reversible = false,
                ResolvesCheckIds = new List<string>(),
            };

            lock (_lock)
            {
                _templates[agPrimaryGuard.Key] = agPrimaryGuard;
                _templates[syncAgentJob.Key] = syncAgentJob;
                _templates[deleteExtraJob.Key] = deleteExtraJob;
                _templates[maxdop.Key] = maxdop;
                _templates[ctfp.Key] = ctfp;
                _templates[optAdHoc.Key] = optAdHoc;
                _templates[maxmem.Key] = maxmem;
                _templates[backupCompression.Key] = backupCompression;
                _templates[defaultTrace.Key] = defaultTrace;
                _templates[crossDbOwnership.Key] = crossDbOwnership;
                _templates[adHocDistributedQueries.Key] = adHocDistributedQueries;
                _templates[addMissingIndex.Key] = addMissingIndex;
                _templates[agentAlertPack.Key] = agentAlertPack;
                _templates[installMaintenanceSolution.Key] = installMaintenanceSolution;
                _templates[backupDatabaseNow.Key] = backupDatabaseNow;
                _templates[checkDbNow.Key] = checkDbNow;
            }
        }

        /// <summary>
        /// Task #39 — corpus-fed one-click templates. Parses <see cref="SqlCheck.Remediation"/>
        /// (already parsed tolerantly by SqlCheckBuilder) into <see cref="RemediationTemplate"/>s
        /// per the three-tier contract (corpus-v2/_inventory/REMEDIATION_APP_WIRING_CONTRACT.md):
        /// a check needs <c>AutoFixable == true</c> AND a well-formed <c>Operation</c> to become a
        /// one-click template — everything else (no remediation, preview-only, malformed op) is
        /// simply skipped here (the Remediation UI's preview-only section reads SqlCheck.Remediation
        /// directly, not this store). Dedupes by Operation identity (sp_configure: op_kind+config_name;
        /// db_set_option: op_kind+option_sql+offenders_query), PREFERRING the built-in — a dedup
        /// hit attaches the corpus check id to the built-in's ResolvesCheckIds for linkage instead
        /// of registering a duplicate. Idempotent (safe to call again on a reload) and NEVER throws:
        /// one malformed check degrades to preview-only + a logged warning, never aborts the load.
        /// </summary>
        public void LoadCorpusTemplates(IEnumerable<SqlCheck>? checks)
        {
            if (checks is null) return;
            int oneClick = 0, deduped = 0;
            var malformed = new List<CorpusMalformedCheck>();
            lock (_lock)
            {
                // Idempotent reload: drop this store's own prior corpus additions first (built-ins
                // and any overlay entries are untouched — _corpusAddedKeys only ever holds keys
                // THIS method registered).
                foreach (var key in _corpusAddedKeys) _templates.Remove(key);
                _corpusAddedKeys.Clear();

                var existingIdentities = new Dictionary<string, RemediationTemplate>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in _templates.Values)
                {
                    var id = OperationIdentity(t.Operation);
                    if (id != null) existingIdentities[id] = t;
                }

                foreach (var check in checks)
                {
                    var remediation = check.Remediation;
                    if (remediation is null || !remediation.AutoFixable || remediation.Operation is null)
                        continue; // no remediation, preview-only, or judgment-call — not one-click

                    RemediationTemplate? candidate;
                    string? buildProblem;
                    try { candidate = BuildTemplateFromCheck(check, remediation, _logger, out buildProblem); }
                    catch (Exception ex)
                    {
                        malformed.Add(new CorpusMalformedCheck(check.Id,
                            $"The corpus fix block threw while being parsed: {ex.Message}"));
                        _logger.LogWarning(ex, "Corpus remediation template for check '{CheckId}' malformed; falling back to preview-only.", check.Id);
                        continue;
                    }
                    if (candidate is null)
                    {
                        malformed.Add(new CorpusMalformedCheck(check.Id,
                            buildProblem ?? $"The corpus fix block declares op_kind '{remediation.Operation.OpKind}', which this build cannot render."));
                        _logger.LogWarning("Corpus remediation template for check '{CheckId}' has an unsupported/unrenderable op_kind '{OpKind}'; falling back to preview-only.",
                            check.Id, remediation.Operation.OpKind);
                        continue;
                    }

                    var identity = OperationIdentity(candidate.Operation);
                    if (identity != null && existingIdentities.TryGetValue(identity, out var builtin))
                    {
                        // Dedupe: prefer the existing (built-in or already-registered) template —
                        // attach this check id for finding-linkage only, never register a duplicate.
                        if (!builtin.ResolvesCheckIds.Contains(check.Id))
                            builtin.ResolvesCheckIds.Add(check.Id);
                        deduped++;
                        continue;
                    }

                    _templates[candidate.Key] = candidate;
                    _corpusAddedKeys.Add(candidate.Key);
                    if (identity != null) existingIdentities[identity] = candidate;
                    oneClick++;
                }

                CorpusOneClickCount = oneClick;
                CorpusDedupedCount = deduped;
                _corpusMalformed = malformed;
                // Unconditional: this method removes its own prior additions, adds new ones AND
                // mutates existing built-ins in place on the dedup path. Any of those makes a
                // previously-built reverse index wrong, and "nothing changed" is not cheap to
                // prove here — so the index rebuilds rather than risking a stale answer.
                Interlocked.Increment(ref _generation);
            }
            _logger.LogInformation(
                "Corpus-fed remediation templates: {OneClick} one-click added, {Deduped} deduped to existing templates, {Malformed} malformed (preview-only).",
                oneClick, deduped, malformed.Count);
        }

        // Operation identity for dedup — see REMEDIATION_APP_WIRING_CONTRACT.md §4 rule 4. Null
        // for op kinds with no defined identity (nothing to dedupe against).
        private static string? OperationIdentity(RemediationOperation? op)
        {
            if (op is null) return null;
            return op.OpKind switch
            {
                RemediationOpKind.SpConfigure => $"sp_configure:{(op.ConfigName ?? string.Empty).Trim().ToLowerInvariant()}",
                RemediationOpKind.DbSetOption => $"db_set_option:{(op.OptionSql ?? string.Empty).Trim().ToLowerInvariant()}:{(op.OffendersQuery ?? string.Empty).Trim().ToLowerInvariant()}",
                _ => null,
            };
        }

        // Builds a RemediationTemplate from one corpus check's parsed remediation block. Returns
        // null for an unsupported op_kind (e.g. create_index — reserved, no corpus entries yet) or
        // a structurally incomplete op (missing config_name / option_sql+offenders_query) — the
        // caller treats null exactly like a thrown exception (malformed, preview-only, logged).
        // `problem` names WHICH of those it was: the operator sees that sentence on the page, so
        // a rejected fix is visible with its reason instead of vanishing from both lists.
        private static RemediationTemplate? BuildTemplateFromCheck(SqlCheck check, CheckRemediation remediation, ILogger logger, out string? problem)
        {
            problem = null;
            var src = remediation.Operation!;
            RemediationOperation operation;
            switch ((src.OpKind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "sp_configure":
                    if (string.IsNullOrWhiteSpace(src.ConfigName))
                    {
                        problem = "The corpus fix block declares op_kind 'sp_configure' with no config_name, so there is nothing to set.";
                        return null;
                    }
                    operation = new RemediationOperation
                    {
                        OpKind = RemediationOpKind.SpConfigure,
                        ConfigName = src.ConfigName,
                        AdvancedOption = src.AdvancedOption,
                        ValueParam = src.ValueParam ?? string.Empty,
                        MinValue = src.ValueFixed ?? src.MinValue ?? 0,
                        MaxValue = src.ValueFixed ?? src.MaxValue ?? 0,
                        ValueFixed = src.ValueFixed,
                        // A corpus `value_fixed` IS the authored target, so it is also the
                        // recommendation. Without one the corpus author gave a range only, and
                        // the app has nothing to recommend — null, not the floor.
                        RecommendedValue = src.ValueFixed,
                    };
                    break;
                case "db_set_option":
                    if (string.IsNullOrWhiteSpace(src.OptionSql) || string.IsNullOrWhiteSpace(src.OffendersQuery))
                    {
                        // Hard contract: db_set_option REQUIRES offenders_query. Without it the app
                        // cannot tell which databases are affected, so it will not run the fix.
                        problem = string.IsNullOrWhiteSpace(src.OptionSql)
                            ? "The corpus fix block declares op_kind 'db_set_option' with no option_sql, so there is no statement to run."
                            : "The corpus fix block declares op_kind 'db_set_option' with no offenders_query, so the app cannot tell which databases it would change.";
                        return null;
                    }
                    operation = new RemediationOperation
                    {
                        OpKind = RemediationOpKind.DbSetOption,
                        OptionSql = src.OptionSql,
                        OffendersQuery = src.OffendersQuery,
                    };
                    break;
                default:
                    // Unsupported op_kind (e.g. create_index — reserved, no corpus entries yet).
                    problem = string.IsNullOrWhiteSpace(src.OpKind)
                        ? "The corpus fix block names no op_kind, so this build cannot render it."
                        : $"The corpus fix block declares op_kind '{src.OpKind}', which this build cannot render.";
                    return null;
            }

            return new RemediationTemplate
            {
                Key = check.Id,
                DisplayName = check.Name,
                Description = remediation.CmdletOrTemplate ?? check.Name,
                RiskClass = ParseRiskClass(remediation.RiskClass),
                Kind = RemediationKind.Configuration,
                Type = RemediationType.Config,
                ShowPowerBand = false,
                Operation = operation,
                Reversible = remediation.Reversible,
                CreditCostOverride = ParseCreditCost(remediation.CreditCost, check.Id, logger),
                ResolvesCheckIds = new List<string> { check.Id },
            };
        }

        /// <summary>
        /// Corpus-authored credit price. Out-of-range values are DROPPED (null = derive from
        /// risk class + reversibility) rather than clamped silently: an author who wrote 40
        /// meant something we can't infer, and quietly charging 5 would be a guess. Same
        /// fail-tolerant posture as the rest of the corpus remediation block.
        /// </summary>
        private static int? ParseCreditCost(int? authored, string checkId, ILogger logger)
        {
            if (authored is not int c) return null;
            if (c >= Remediation.RemediationCreditCost.Min && c <= Remediation.RemediationCreditCost.Max)
                return c;

            logger.LogWarning(
                "Corpus remediation for check '{CheckId}' declares credit_cost {Cost}, outside the " +
                "allowed range [{Min},{Max}]; ignoring it and deriving the cost from risk class instead.",
                checkId, c, Remediation.RemediationCreditCost.Min, Remediation.RemediationCreditCost.Max);
            return null;
        }

        private static RemediationRiskClass ParseRiskClass(string? riskClass) => (riskClass ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "trivial" => RemediationRiskClass.Trivial,
            "sensitive" => RemediationRiskClass.Sensitive,
            _ => RemediationRiskClass.Standard,
        };

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
                        Interlocked.Increment(ref _generation);
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
