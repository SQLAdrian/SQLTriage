/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Persists governance and compliance scores to SQLite for historical trending.
    /// Each snapshot is checksummed with HMAC-SHA256; a separate integrity table
    /// stores the chain so tampering is detectable.
    /// </summary>
    public class GovernanceHistoryService : IDisposable
    {
        private readonly ILogger<GovernanceHistoryService> _logger;
        private readonly string _connectionString;
        private readonly byte[] _hmacKey;
        private readonly System.Timers.Timer _purgeTimer;
        private readonly int _retentionDays;
        private string _lastSignature = string.Empty;
        private readonly object _writeLock = new();

        public bool IntegrityBroken { get; private set; }

        public GovernanceHistoryService(ILogger<GovernanceHistoryService> logger, int retentionDays = 365, string? dbDir = null)
        {
            _logger = logger;
            _retentionDays = retentionDays;
            var resolvedDir = dbDir ?? AppDomain.CurrentDomain.BaseDirectory;
            var dbPath = Path.Combine(resolvedDir, "governance-history.db");
            _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";

            // Load or generate HMAC key
            var keyPath = Path.Combine(resolvedDir, ".governance-hmac-key");
            if (File.Exists(keyPath))
            {
                _hmacKey = File.ReadAllBytes(keyPath);
            }
            else
            {
                _hmacKey = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(keyPath, _hmacKey);
            }

            InitializeSchema();
            VerifyIntegrityChain();
            // DE-C2: purge legacy rows whose recorded_at has a '+00:00' suffix.
            // These rows were written by earlier builds using DateTime.UtcNow.ToString("o").
            // They silently never matched the SQLite datetime() purge because of the suffix.
            PurgeLegacyOffsetRows();

            _purgeTimer = new System.Timers.Timer(TimeSpan.FromHours(24).TotalMilliseconds);
            _purgeTimer.Elapsed += (_, _) => PurgeOldRecords();
            _purgeTimer.Start();
        }

        private void InitializeSchema()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA foreign_keys=ON;
                    -- DE-C3: MUST be set before any tables are created (no-op on existing DBs with auto_vacuum=NONE;
                    -- those will get auto_vacuum on the next fresh install or SQLCipher migration).
                    PRAGMA auto_vacuum=INCREMENTAL;

                    CREATE TABLE IF NOT EXISTS governance_history (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name     TEXT NOT NULL,
                        recorded_at     TEXT NOT NULL DEFAULT (datetime('now')),
                        overall_score   REAL NOT NULL,
                        band            TEXT NOT NULL,
                        security_score  REAL,
                        performance_score REAL,
                        reliability_score REAL,
                        compliance_score  REAL,
                        cost_score      REAL,
                        total_findings  INTEGER,
                        passed_findings INTEGER,
                        failed_findings INTEGER,
                        is_indicative   INTEGER DEFAULT 1
                    );

                    CREATE INDEX IF NOT EXISTS idx_gov_hist_server_time
                        ON governance_history(server_name, recorded_at);

                    CREATE TABLE IF NOT EXISTS compliance_history (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name     TEXT NOT NULL,
                        recorded_at     TEXT NOT NULL DEFAULT (datetime('now')),
                        framework       TEXT NOT NULL,
                        control_id      TEXT NOT NULL,
                        control_name    TEXT NOT NULL,
                        checks_total    INTEGER,
                        checks_passed   INTEGER,
                        coverage_pct    REAL
                    );

                    CREATE INDEX IF NOT EXISTS idx_comp_hist_server_fw_time
                        ON compliance_history(server_name, framework, recorded_at);

                    CREATE TABLE IF NOT EXISTS check_results (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name     TEXT NOT NULL,
                        recorded_at     TEXT NOT NULL DEFAULT (datetime('now')),
                        check_id        TEXT NOT NULL,
                        check_name      TEXT NOT NULL,
                        category        TEXT NOT NULL,
                        severity        TEXT NOT NULL,
                        passed          INTEGER NOT NULL,
                        actual_value    REAL,
                        expected_value  INTEGER,
                        message         TEXT,
                        duration_ms     INTEGER
                    );

                    CREATE INDEX IF NOT EXISTS idx_check_results_server_time
                        ON check_results(server_name, recorded_at);

                    CREATE INDEX IF NOT EXISTS idx_check_results_server_id
                        ON check_results(server_name, check_id);

                    CREATE TABLE IF NOT EXISTS report_integrity (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        report_type     TEXT NOT NULL,
                        report_id       INTEGER NOT NULL,
                        recorded_at     TEXT NOT NULL DEFAULT (datetime('now')),
                        payload_hash    TEXT NOT NULL,
                        previous_hash   TEXT,
                        chain_hash      TEXT NOT NULL,
                        server_name     TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_integrity_type_id
                        ON report_integrity(report_type, report_id);

                    -- Health Score & Risk Rating v1 (Strategic #7)
                    CREATE TABLE IF NOT EXISTS health_score_history (
                        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name         TEXT NOT NULL,
                        recorded_date       TEXT NOT NULL,           -- YYYY-MM-DD UTC date key
                        composite_score     INTEGER NOT NULL,
                        perf_score          INTEGER,
                        compliance_score    INTEGER,
                        security_score      INTEGER,
                        resource_score      INTEGER,
                        blocking_score      INTEGER
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS idx_health_score_server_date
                        ON health_score_history(server_name, recorded_date);

                    -- P3: frozen first-assessment baseline (the ""gospel""). One ACTIVE
                    -- baseline per server; re-baselining marks the old one superseded and
                    -- inserts a new active row. Transitions are computed baseline-vs-latest.
                    CREATE TABLE IF NOT EXISTS baselines (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name     TEXT NOT NULL,
                        captured_at     TEXT NOT NULL DEFAULT (datetime('now')),
                        reason          TEXT,                 -- why re-baselined (milestone note); NULL for the first
                        composite_score INTEGER NOT NULL,     -- health score frozen at baseline time
                        total_checks    INTEGER NOT NULL,
                        passed_checks   INTEGER NOT NULL,
                        failed_checks   INTEGER NOT NULL,
                        is_active       INTEGER NOT NULL DEFAULT 1
                    );

                    CREATE INDEX IF NOT EXISTS idx_baselines_server_active
                        ON baselines(server_name, is_active);

                    -- The per-check pass/fail snapshot frozen with a baseline. Keyed by
                    -- baseline so re-baselining never mutates prior frozen sets.
                    CREATE TABLE IF NOT EXISTS baseline_check_results (
                        baseline_id     INTEGER NOT NULL,
                        check_id        TEXT NOT NULL,
                        check_name      TEXT NOT NULL,
                        category        TEXT NOT NULL,
                        severity        TEXT NOT NULL,
                        passed          INTEGER NOT NULL,
                        effort_hours    REAL NOT NULL DEFAULT 0,
                        PRIMARY KEY (baseline_id, check_id),
                        FOREIGN KEY (baseline_id) REFERENCES baselines(id) ON DELETE CASCADE
                    );
                ";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("Governance history database initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize governance history database");
            }
        }

        /// <summary>
        /// Record a governance score snapshot. Computes HMAC-SHA256 chain hash
        /// and stores integrity record in report_integrity table.
        /// </summary>
        public long RecordGovernanceScore(string serverName, GovernanceScore score)
        {
            long historyId;
            string recordedAt;

            lock (_writeLock)
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var transaction = conn.BeginTransaction();

                // Insert governance snapshot
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO governance_history
                            (server_name, overall_score, band, security_score, performance_score,
                             reliability_score, compliance_score, cost_score,
                             total_findings, passed_findings, failed_findings, is_indicative)
                        VALUES
                            (@server, @overall, @band, @sec, @perf, @rel, @comp, @cost,
                             @total, @passed, @failed, @indicative);
                        SELECT last_insert_rowid();
                    ";
                    cmd.Parameters.AddWithValue("@server", serverName);
                    cmd.Parameters.AddWithValue("@overall", score.Overall);
                    cmd.Parameters.AddWithValue("@band", score.Band.ToString());
                    cmd.Parameters.AddWithValue("@sec", GetCategoryScore(score, "Security"));
                    cmd.Parameters.AddWithValue("@perf", GetCategoryScore(score, "Performance"));
                    cmd.Parameters.AddWithValue("@rel", GetCategoryScore(score, "Reliability"));
                    cmd.Parameters.AddWithValue("@comp", GetCategoryScore(score, "Compliance"));
                    cmd.Parameters.AddWithValue("@cost", GetCategoryScore(score, "Cost"));
                    cmd.Parameters.AddWithValue("@total", score.TotalFindings);
                    cmd.Parameters.AddWithValue("@passed", score.PassedFindings);
                    cmd.Parameters.AddWithValue("@failed", score.FailedFindings);
                    cmd.Parameters.AddWithValue("@indicative", score.IsIndicative ? 1 : 0);
                    historyId = (long)(cmd.ExecuteScalar() ?? 0);
                }

                // Get recorded_at timestamp
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "SELECT recorded_at FROM governance_history WHERE id = @id";
                    cmd.Parameters.AddWithValue("@id", historyId);
                    recordedAt = cmd.ExecuteScalar()?.ToString() ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                }

                // Compute integrity hash
                var payload = JsonSerializer.Serialize(new
                {
                    id = historyId,
                    server = serverName,
                    score.Overall,
                    band = score.Band.ToString(),
                    total = score.TotalFindings,
                    passed = score.PassedFindings,
                    failed = score.FailedFindings,
                    recordedAt
                });

                var payloadHash = ComputeHmacHex(payload);
                var chainInput = _lastSignature + payloadHash;
                var chainHash = ComputeHmacHex(chainInput);

                // Insert integrity record
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO report_integrity
                            (report_type, report_id, recorded_at, payload_hash, previous_hash, chain_hash, server_name)
                        VALUES
                            ('governance', @reportId, @recordedAt, @payloadHash, @prevHash, @chainHash, @server);
                    ";
                    cmd.Parameters.AddWithValue("@reportId", historyId);
                    cmd.Parameters.AddWithValue("@recordedAt", recordedAt);
                    cmd.Parameters.AddWithValue("@payloadHash", payloadHash);
                    cmd.Parameters.AddWithValue("@prevHash", _lastSignature.Length > 0 ? _lastSignature : DBNull.Value);
                    cmd.Parameters.AddWithValue("@chainHash", chainHash);
                    cmd.Parameters.AddWithValue("@server", serverName);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                _lastSignature = chainHash;
            }

            _logger.LogInformation("Governance score recorded for {Server}: {Score:F1} ({Band})",
                serverName, score.Overall, score.Band);
            return historyId;
        }

        /// <summary>
        /// Record compliance coverage snapshot for a specific framework.
        /// </summary>
        public void RecordComplianceCoverage(string serverName, string framework,
            string controlId, string controlName, int checksTotal, int checksPassed)
        {
            var coveragePct = checksTotal > 0 ? (double)checksPassed / checksTotal * 100.0 : 0;

            lock (_writeLock)
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO compliance_history
                        (server_name, framework, control_id, control_name, checks_total, checks_passed, coverage_pct)
                    VALUES
                        (@server, @framework, @controlId, @controlName, @total, @passed, @pct);
                ";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@framework", framework);
                cmd.Parameters.AddWithValue("@controlId", controlId);
                cmd.Parameters.AddWithValue("@controlName", controlName);
                cmd.Parameters.AddWithValue("@total", checksTotal);
                cmd.Parameters.AddWithValue("@passed", checksPassed);
                cmd.Parameters.AddWithValue("@pct", coveragePct);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Get governance trend data for charting.
        /// </summary>
        public List<GovernanceTrendPoint> GetTrend(string serverName, int days = 90)
        {
            var results = new List<GovernanceTrendPoint>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT recorded_at, overall_score, band, security_score, performance_score,
                           reliability_score, compliance_score, cost_score,
                           total_findings, passed_findings, failed_findings
                    FROM governance_history
                    WHERE server_name = @server
                      AND recorded_at >= datetime('now', @days)
                    ORDER BY recorded_at ASC;
                ";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@days", $"-{days} days");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new GovernanceTrendPoint
                    {
                        RecordedAt = reader.GetString(0),
                        OverallScore = reader.GetDouble(1),
                        Band = reader.GetString(2),
                        SecurityScore = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                        PerformanceScore = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                        ReliabilityScore = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        ComplianceScore = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                        CostScore = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                        TotalFindings = reader.GetInt32(8),
                        PassedFindings = reader.GetInt32(9),
                        FailedFindings = reader.GetInt32(10)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read governance trend for {Server}", serverName);
            }
            return results;
        }

        /// <summary>
        /// Get weekly averages for trend chart.
        /// </summary>
        public List<GovernanceWeeklyAverage> GetWeeklyAverages(string serverName, int weeks = 12)
        {
            var results = new List<GovernanceWeeklyAverage>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT strftime('%Y-W%W', recorded_at) AS week,
                           AVG(overall_score) AS avg_score,
                           AVG(security_score) AS avg_security,
                           AVG(performance_score) AS avg_performance,
                           AVG(reliability_score) AS avg_reliability,
                           AVG(compliance_score) AS avg_compliance,
                           AVG(cost_score) AS avg_cost,
                           MIN(overall_score) AS min_score,
                           MAX(overall_score) AS max_score,
                           COUNT(*) AS samples
                    FROM governance_history
                    WHERE server_name = @server
                      AND recorded_at >= datetime('now', @days)
                    GROUP BY week
                    ORDER BY week ASC;
                ";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@days", $"-{weeks * 7} days");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new GovernanceWeeklyAverage
                    {
                        Week = reader.GetString(0),
                        AvgScore = reader.GetDouble(1),
                        AvgSecurity = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                        AvgPerformance = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                        AvgReliability = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                        AvgCompliance = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                        AvgCost = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                        MinScore = reader.GetDouble(7),
                        MaxScore = reader.GetDouble(8),
                        Samples = reader.GetInt32(9)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read weekly averages for {Server}", serverName);
            }
            return results;
        }

        /// <summary>
        /// Record a single check result for persistence across restarts.
        /// </summary>
        public void RecordCheckResult(string serverName, CheckResult result)
        {
            try
            {
                lock (_writeLock)
                {
                    using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO check_results
                            (server_name, check_id, check_name, category, severity, passed, actual_value, expected_value, message, duration_ms)
                        VALUES
                            (@server, @cid, @cname, @cat, @sev, @passed, @actual, @expected, @msg, @dur);
                    ";
                    cmd.Parameters.AddWithValue("@server", serverName);
                    cmd.Parameters.AddWithValue("@cid", result.CheckId);
                    cmd.Parameters.AddWithValue("@cname", result.CheckName);
                    cmd.Parameters.AddWithValue("@cat", result.Category ?? "");
                    cmd.Parameters.AddWithValue("@sev", result.Severity ?? "");
                    cmd.Parameters.AddWithValue("@passed", result.Passed ? 1 : 0);
                    cmd.Parameters.AddWithValue("@actual", result.ActualValue);
                    cmd.Parameters.AddWithValue("@expected", result.ExpectedValue);
                    cmd.Parameters.AddWithValue("@msg", (result.Message ?? "").Substring(0, Math.Min(500, (result.Message ?? "").Length)));
                    cmd.Parameters.AddWithValue("@dur", (int)result.DurationMs);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Load latest check results for a server (from most recent run within 7 days).
        /// Returns empty list if no data.
        /// </summary>
        public List<CheckResult> LoadLatestCheckResults(string serverName, int daysBack = 7)
        {
            var results = new List<CheckResult>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    WITH latest AS (
                        SELECT recorded_at FROM check_results
                        WHERE server_name = @server
                          AND recorded_at >= datetime('now', @days)
                        ORDER BY id DESC LIMIT 1
                    )
                    SELECT check_id, check_name, category, severity, passed, actual_value, expected_value, message
                    FROM check_results cr
                    INNER JOIN latest l ON cr.recorded_at = l.recorded_at
                    WHERE cr.server_name = @server
                    ORDER BY check_id;
                ";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@days", $"-{daysBack} days");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new CheckResult
                    {
                        CheckId = reader.GetString(0),
                        CheckName = reader.GetString(1),
                        Category = reader.GetString(2),
                        Severity = reader.GetString(3),
                        Passed = reader.GetInt32(4) == 1,
                        ActualValue = reader.IsDBNull(5) ? 0 : (int)reader.GetDouble(5),
                        ExpectedValue = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        Message = reader.IsDBNull(7) ? "" : reader.GetString(7)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load check results for {Server}", serverName);
            }
            return results;
        }

        /// <summary>
        /// Return the recorded history for a single check across all servers, ordered by time.
        /// Used by the per-check trend drill-down (/checks/trend/{checkId}).
        /// </summary>
        public List<CheckHistoryPoint> GetCheckHistory(string checkId, int days = 90)
        {
            var points = new List<CheckHistoryPoint>();
            if (string.IsNullOrWhiteSpace(checkId)) return points;
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT server_name, recorded_at, passed, actual_value, severity, check_name, category
                    FROM check_results
                    WHERE check_id = @cid
                      AND recorded_at >= datetime('now', @days)
                    ORDER BY recorded_at ASC;
                ";
                cmd.Parameters.AddWithValue("@cid", checkId);
                cmd.Parameters.AddWithValue("@days", $"-{days} days");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    points.Add(new CheckHistoryPoint
                    {
                        Server = reader.GetString(0),
                        RecordedAt = reader.GetString(1),
                        Passed = reader.GetInt32(2) == 1,
                        ActualValue = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3),
                        Severity = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        CheckName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                        Category = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read check history for {CheckId}", checkId);
            }
            return points;
        }

        /// <summary>
        /// Verify the integrity chain. Returns list of any broken links.
        /// </summary>
        public List<IntegrityViolation> VerifyIntegrityChain()
        {
            var violations = new List<IntegrityViolation>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, report_type, report_id, payload_hash, previous_hash, chain_hash, server_name, recorded_at
                    FROM report_integrity
                    ORDER BY id ASC;
                ";

                string expectedPrevious = string.Empty;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var reportType = reader.GetString(1);
                    var reportId = reader.GetInt64(2);
                    var payloadHash = reader.GetString(3);
                    var previousHash = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var chainHash = reader.GetString(5);
                    var serverName = reader.IsDBNull(6) ? "" : reader.GetString(6);
                    var recordedAt = reader.IsDBNull(7) ? "" : reader.GetString(7);

                    // Verify previous hash matches
                    if (previousHash != expectedPrevious)
                    {
                        violations.Add(new IntegrityViolation
                        {
                            IntegrityId = id,
                            ReportType = reportType,
                            ReportId = reportId,
                            ServerName = serverName,
                            RecordedAt = recordedAt,
                            ExpectedPrevious = expectedPrevious,
                            ActualPrevious = previousHash,
                            Message = "Previous hash mismatch — chain broken at this point"
                        });
                    }

                    // Verify chain hash
                    var expectedChain = ComputeHmacHex(expectedPrevious + payloadHash);
                    if (expectedChain != chainHash)
                    {
                        violations.Add(new IntegrityViolation
                        {
                            IntegrityId = id,
                            ReportType = reportType,
                            ReportId = reportId,
                            ServerName = serverName,
                            RecordedAt = recordedAt,
                            Message = "Chain hash mismatch — record may have been tampered with"
                        });
                    }

                    expectedPrevious = chainHash;
                }

                _lastSignature = expectedPrevious;
                IntegrityBroken = violations.Count > 0;

                if (IntegrityBroken)
                    _logger.LogWarning("Governance integrity chain has {Count} violation(s)", violations.Count);
                else
                    _logger.LogInformation("Governance integrity chain verified — {Count} records, no violations",
                        violations.Count > 0 ? "broken" : "intact");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify governance integrity chain");
            }
            return violations;
        }

        /// <summary>
        /// DE-C2: One-shot startup sweep that removes rows written by earlier builds using
        /// DateTime.UtcNow.ToString("o"), which appends "+00:00". Those rows never matched
        /// the SQLite <c>datetime('now', '-N days')</c> purge because SQLite's datetime()
        /// compares lexically and the suffix breaks the comparison.
        /// Safe to re-run — rows already purged are gone, no duplicate effect.
        /// Gated by a .vacuum_marker table so it only runs once per install.
        /// </summary>
        private void PurgeLegacyOffsetRows()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);

                // Create the once-run marker table if it doesn't exist.
                using (var mk = conn.CreateCommand())
                {
                    mk.CommandText = "CREATE TABLE IF NOT EXISTS _legacy_offset_purge_marker (ran_at TEXT);";
                    mk.ExecuteNonQuery();
                }

                // Check if we've already run.
                using (var chk = conn.CreateCommand())
                {
                    chk.CommandText = "SELECT COUNT(*) FROM _legacy_offset_purge_marker;";
                    var already = Convert.ToInt32(chk.ExecuteScalar());
                    if (already > 0) return;
                }

                // Delete rows with the ISO-8601 offset suffix.
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM governance_history  WHERE recorded_at LIKE '%+00:00';
                    DELETE FROM compliance_history  WHERE recorded_at LIKE '%+00:00';
                    DELETE FROM check_results       WHERE recorded_at LIKE '%+00:00';
                    DELETE FROM report_integrity    WHERE recorded_at LIKE '%+00:00';
                ";
                var deleted = cmd.ExecuteNonQuery();
                _logger.LogInformation(
                    "[DE-C2] Legacy offset-suffix purge: removed {Count} rows with '+00:00' recorded_at",
                    deleted);

                // Mark as done.
                using var mark = conn.CreateCommand();
                mark.CommandText = "INSERT INTO _legacy_offset_purge_marker (ran_at) VALUES (@now);";
                mark.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                mark.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DE-C2] Legacy offset-suffix purge failed (non-fatal)");
            }
        }

        private void PurgeOldRecords()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                var daysParam = $"-{_retentionDays} days";

                // DE-C5: Execute each DELETE separately so ExecuteNonQuery() returns the
                // actual rowcount for that table (batched multi-statement returns only the
                // last statement's count, making the log misleading).
                int totalDeleted = 0;
                foreach (var table in new[] { "governance_history", "compliance_history", "check_results", "report_integrity" })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {table} WHERE recorded_at < datetime('now', @days);";
                    cmd.Parameters.AddWithValue("@days", daysParam);
                    totalDeleted += cmd.ExecuteNonQuery();
                }

                if (totalDeleted > 0)
                    _logger.LogInformation("Purged {Count} old governance history records", totalDeleted);
                // DE-C3: reclaim space after purge.
                using var vac = conn.CreateCommand();
                vac.CommandText = "PRAGMA incremental_vacuum;";
                vac.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to purge old governance history");
            }
        }

        private string ComputeHmacHex(string input)
        {
            using var hmac = new HMACSHA256(_hmacKey);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static double GetCategoryScore(GovernanceScore score, string dimension)
        {
            return score.Categories.TryGetValue(dimension, out var cat) ? cat.CappedScore : 0;
        }

        // ── Health Score & Risk Rating (Strategic #7) ──────────────────────────

        /// <summary>
        /// Persists a daily health score snapshot (once per UTC day per server).
        /// Uses INSERT OR REPLACE so a re-run on the same day overwrites the earlier value.
        /// </summary>
        public void RecordHealthScore(string serverName, int compositeScore, HealthScoreBreakdown breakdown)
        {
            try
            {
                var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
                lock (_writeLock)
                {
                    using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO health_score_history
                            (server_name, recorded_date, composite_score,
                             perf_score, compliance_score, security_score,
                             resource_score, blocking_score)
                        VALUES
                            (@srv, @date, @comp,
                             @perf, @compl, @sec,
                             @res, @blk);";
                    cmd.Parameters.AddWithValue("@srv", serverName);
                    cmd.Parameters.AddWithValue("@date", dateKey);
                    cmd.Parameters.AddWithValue("@comp", compositeScore);
                    cmd.Parameters.AddWithValue("@perf", breakdown.Performance.Score);
                    cmd.Parameters.AddWithValue("@compl", breakdown.Compliance.Score);
                    cmd.Parameters.AddWithValue("@sec", breakdown.Security.Score);
                    cmd.Parameters.AddWithValue("@res", breakdown.Resource.Score);
                    cmd.Parameters.AddWithValue("@blk", breakdown.Blocking.Score);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record health score for {Server}", serverName);
            }
        }

        /// <summary>
        /// Returns the most recent composite health score recorded on or after
        /// <paramref name="fromDate"/>, or null if no row exists.
        /// </summary>
        public async Task<int?> GetLatestHealthScoreAsync(string serverName, DateTime fromDate)
        {
            try
            {
                var dateKey = fromDate.ToString("yyyy-MM-dd");
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT composite_score
                    FROM health_score_history
                    WHERE server_name   = @srv
                      AND recorded_date >= @date
                    ORDER BY recorded_date DESC
                    LIMIT 1;";
                cmd.Parameters.AddWithValue("@srv", serverName);
                cmd.Parameters.AddWithValue("@date", dateKey);
                var result = await cmd.ExecuteScalarAsync();
                return result == null || result == DBNull.Value
                    ? (int?)null
                    : Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetLatestHealthScoreAsync failed for {Server}", serverName);
                return null;
            }
        }

        /// <summary>
        /// Returns the last <paramref name="days"/> daily health score snapshots for a server,
        /// oldest-first, for trend sparkline rendering.
        /// </summary>
        public List<HealthScoreTrendPoint> GetHealthScoreTrend(string serverName, int days = 30)
        {
            var rows = new List<HealthScoreTrendPoint>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT recorded_date, composite_score,
                           perf_score, compliance_score, security_score,
                           resource_score, blocking_score
                    FROM health_score_history
                    WHERE server_name   = @srv
                      AND recorded_date >= date('now', @days)
                    ORDER BY recorded_date ASC;";
                cmd.Parameters.AddWithValue("@srv", serverName);
                cmd.Parameters.AddWithValue("@days", $"-{days} days");
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    rows.Add(new HealthScoreTrendPoint
                    {
                        RecordedDate = rdr.GetString(0),
                        CompositeScore = rdr.GetInt32(1),
                        PerfScore = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
                        ComplianceScore = rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3),
                        SecurityScore = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4),
                        ResourceScore = rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5),
                        BlockingScore = rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetHealthScoreTrend failed for {Server}", serverName);
            }
            return rows;
        }

        // ── P3: Baseline ("gospel") + Fail→Pass / Pass→Fail transitions ────────

        /// <summary>
        /// Freezes a baseline for a server from the supplied check results. The first
        /// baseline is the immutable "gospel" first assessment; later calls are explicit
        /// re-baselines (milestones) — they supersede the previous active baseline rather
        /// than mutate it. Pass a <paramref name="reason"/> on re-baseline; leave it null
        /// for the very first capture. Returns the new baseline id, or 0 on failure.
        ///
        /// Idempotent on intent only — calling twice creates two baselines; callers should
        /// gate the first capture with <see cref="HasBaseline"/> and reserve later calls
        /// for deliberate re-baseline actions.
        /// </summary>
        public long RecordBaseline(string serverName, int compositeScore,
            IReadOnlyList<CheckResult> results, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return 0;
            results ??= System.Array.Empty<CheckResult>();
            var passed = results.Count(r => r.Passed);
            var failed = results.Count(r => !r.Passed);

            try
            {
                lock (_writeLock)
                {
                    using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                    using var tx = conn.BeginTransaction();

                    // Supersede any currently-active baseline for this server.
                    using (var sup = conn.CreateCommand())
                    {
                        sup.Transaction = tx;
                        sup.CommandText = "UPDATE baselines SET is_active = 0 WHERE server_name = @srv AND is_active = 1;";
                        sup.Parameters.AddWithValue("@srv", serverName);
                        sup.ExecuteNonQuery();
                    }

                    long baselineId;
                    using (var ins = conn.CreateCommand())
                    {
                        ins.Transaction = tx;
                        ins.CommandText = @"
                            INSERT INTO baselines
                                (server_name, reason, composite_score, total_checks, passed_checks, failed_checks, is_active)
                            VALUES
                                (@srv, @reason, @comp, @total, @passed, @failed, 1);
                            SELECT last_insert_rowid();";
                        ins.Parameters.AddWithValue("@srv", serverName);
                        ins.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
                        ins.Parameters.AddWithValue("@comp", compositeScore);
                        ins.Parameters.AddWithValue("@total", results.Count);
                        ins.Parameters.AddWithValue("@passed", passed);
                        ins.Parameters.AddWithValue("@failed", failed);
                        baselineId = (long)(ins.ExecuteScalar() ?? 0L);
                    }

                    using (var ins = conn.CreateCommand())
                    {
                        ins.Transaction = tx;
                        ins.CommandText = @"
                            INSERT OR REPLACE INTO baseline_check_results
                                (baseline_id, check_id, check_name, category, severity, passed, effort_hours)
                            VALUES
                                (@bid, @cid, @cname, @cat, @sev, @passed, @effort);";
                        var pBid = ins.Parameters.Add("@bid", SqliteType.Integer);
                        var pCid = ins.Parameters.Add("@cid", SqliteType.Text);
                        var pName = ins.Parameters.Add("@cname", SqliteType.Text);
                        var pCat = ins.Parameters.Add("@cat", SqliteType.Text);
                        var pSev = ins.Parameters.Add("@sev", SqliteType.Text);
                        var pPass = ins.Parameters.Add("@passed", SqliteType.Integer);
                        var pEffort = ins.Parameters.Add("@effort", SqliteType.Real);
                        pBid.Value = baselineId;
                        foreach (var r in results)
                        {
                            if (string.IsNullOrWhiteSpace(r.CheckId)) continue;
                            pCid.Value = r.CheckId;
                            pName.Value = r.CheckName ?? "";
                            pCat.Value = r.Category ?? "";
                            pSev.Value = r.Severity ?? "";
                            pPass.Value = r.Passed ? 1 : 0;
                            pEffort.Value = r.EffortHours;
                            ins.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                    _logger.LogInformation(
                        "Baseline {Id} recorded for {Server}: {Passed}/{Total} passed, score {Score}{Reason}",
                        baselineId, serverName, passed, results.Count, compositeScore,
                        reason == null ? " (first/gospel)" : $" (re-baseline: {reason})");
                    return baselineId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record baseline for {Server}", serverName);
                return 0;
            }
        }

        /// <summary>True when an active baseline already exists for the server.</summary>
        public bool HasBaseline(string serverName)
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM baselines WHERE server_name = @srv AND is_active = 1;";
                cmd.Parameters.AddWithValue("@srv", serverName);
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HasBaseline failed for {Server}", serverName);
                return false;
            }
        }

        /// <summary>
        /// Returns the active baseline summary for a server, or null if none has been
        /// frozen yet. The check-level snapshot is loaded lazily via the transition API.
        /// </summary>
        public BaselineSummary? GetActiveBaseline(string serverName)
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, captured_at, reason, composite_score, total_checks, passed_checks, failed_checks
                    FROM baselines
                    WHERE server_name = @srv AND is_active = 1
                    ORDER BY id DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@srv", serverName);
                using var rdr = cmd.ExecuteReader();
                if (!rdr.Read()) return null;
                return new BaselineSummary
                {
                    BaselineId = rdr.GetInt64(0),
                    ServerName = serverName,
                    CapturedAt = rdr.GetString(1),
                    Reason = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    CompositeScore = rdr.GetInt32(3),
                    TotalChecks = rdr.GetInt32(4),
                    PassedChecks = rdr.GetInt32(5),
                    FailedChecks = rdr.GetInt32(6),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetActiveBaseline failed for {Server}", serverName);
                return null;
            }
        }

        /// <summary>
        /// Computes the rock-solid (no-estimation) transitions between the server's active
        /// baseline and a current set of check results: which checks moved Fail→Pass
        /// (resolved), Pass→Fail (regressed), newly-appeared failing, and disappeared.
        /// Health-delta = current composite − baseline composite. Returns null if the
        /// server has no active baseline.
        /// </summary>
        public CheckTransitionResult? ComputeTransitions(
            string serverName, IReadOnlyList<CheckResult> current, int currentCompositeScore)
        {
            var baseline = GetActiveBaseline(serverName);
            if (baseline == null) return null;
            current ??= System.Array.Empty<CheckResult>();

            var baselineMap = LoadBaselineChecks(baseline.BaselineId);
            // Last-writer-wins de-dup so a check that appears twice in a run doesn't double-count.
            var currentMap = new Dictionary<string, CheckResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in current)
            {
                if (string.IsNullOrWhiteSpace(r.CheckId)) continue;
                currentMap[r.CheckId] = r;
            }

            var result = new CheckTransitionResult
            {
                ServerName = serverName,
                BaselineId = baseline.BaselineId,
                BaselineCapturedAt = baseline.CapturedAt,
                BaselineCompositeScore = baseline.CompositeScore,
                CurrentCompositeScore = currentCompositeScore,
            };

            foreach (var (checkId, cur) in currentMap)
            {
                if (baselineMap.TryGetValue(checkId, out var wasPassed))
                {
                    if (!wasPassed && cur.Passed)
                        result.Resolved.Add(ToTransition(checkId, cur));        // Fail → Pass
                    else if (wasPassed && !cur.Passed)
                        result.Regressed.Add(ToTransition(checkId, cur));       // Pass → Fail
                }
                else if (!cur.Passed)
                {
                    // Check not in baseline (added to corpus, or first time evaluated) and failing now.
                    result.NewlyFailing.Add(ToTransition(checkId, cur));
                }
            }

            // Checks present+failing at baseline that are no longer in the current run —
            // can't assert resolved (might just not have run), so report separately.
            foreach (var (checkId, wasPassed) in baselineMap)
            {
                if (!wasPassed && !currentMap.ContainsKey(checkId))
                    result.DisappearedFailing.Add(new CheckTransition { CheckId = checkId });
            }

            return result;
        }

        private static CheckTransition ToTransition(string checkId, CheckResult r) => new()
        {
            CheckId = checkId,
            CheckName = r.CheckName ?? "",
            Category = r.Category ?? "",
            Severity = r.Severity ?? "",
            EffortHours = r.EffortHours,
        };

        private Dictionary<string, bool> LoadBaselineChecks(long baselineId)
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT check_id, passed FROM baseline_check_results WHERE baseline_id = @bid;";
                cmd.Parameters.AddWithValue("@bid", baselineId);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    map[rdr.GetString(0)] = rdr.GetInt32(1) == 1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LoadBaselineChecks failed for baseline {Id}", baselineId);
            }
            return map;
        }

        public void Dispose()
        {
            _purgeTimer?.Dispose();
        }
    }

    // ── Models ──────────────────────────────────────────────────────

    public class HealthScoreTrendPoint
    {
        public string RecordedDate { get; set; } = "";
        public int CompositeScore { get; set; }
        public int PerfScore { get; set; }
        public int ComplianceScore { get; set; }
        public int SecurityScore { get; set; }
        public int ResourceScore { get; set; }
        public int BlockingScore { get; set; }
    }

    public class GovernanceTrendPoint
    {
        public string RecordedAt { get; set; } = "";
        public double OverallScore { get; set; }
        public string Band { get; set; } = "";
        public double? SecurityScore { get; set; }
        public double? PerformanceScore { get; set; }
        public double? ReliabilityScore { get; set; }
        public double? ComplianceScore { get; set; }
        public double? CostScore { get; set; }
        public int TotalFindings { get; set; }
        public int PassedFindings { get; set; }
        public int FailedFindings { get; set; }
    }

    public class GovernanceWeeklyAverage
    {
        public string Week { get; set; } = "";
        public double AvgScore { get; set; }
        public double AvgSecurity { get; set; }
        public double AvgPerformance { get; set; }
        public double AvgReliability { get; set; }
        public double AvgCompliance { get; set; }
        public double AvgCost { get; set; }
        public double MinScore { get; set; }
        public double MaxScore { get; set; }
        public int Samples { get; set; }
    }

    public class CheckHistoryPoint
    {
        public string Server { get; set; } = "";
        public string RecordedAt { get; set; } = "";
        public bool Passed { get; set; }
        public double? ActualValue { get; set; }
        public string Severity { get; set; } = "";
        public string CheckName { get; set; } = "";
        public string Category { get; set; } = "";
    }

    /// <summary>Summary of a frozen baseline (the "gospel" or a re-baseline milestone).</summary>
    public class BaselineSummary
    {
        public long BaselineId { get; set; }
        public string ServerName { get; set; } = "";
        public string CapturedAt { get; set; } = "";
        public string? Reason { get; set; }
        public int CompositeScore { get; set; }
        public int TotalChecks { get; set; }
        public int PassedChecks { get; set; }
        public int FailedChecks { get; set; }
    }

    /// <summary>One check that changed state between baseline and the current run.</summary>
    public class CheckTransition
    {
        public string CheckId { get; set; } = "";
        public string CheckName { get; set; } = "";
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public double EffortHours { get; set; }
    }

    /// <summary>
    /// Baseline-vs-current transitions. Resolved (Fail→Pass) and Regressed (Pass→Fail)
    /// are the rock-solid signals; NewlyFailing and DisappearedFailing are reported
    /// separately because they aren't true transitions of a baseline check.
    /// </summary>
    public class CheckTransitionResult
    {
        public string ServerName { get; set; } = "";
        public long BaselineId { get; set; }
        public string BaselineCapturedAt { get; set; } = "";
        public int BaselineCompositeScore { get; set; }
        public int CurrentCompositeScore { get; set; }

        public List<CheckTransition> Resolved { get; set; } = new();          // Fail → Pass
        public List<CheckTransition> Regressed { get; set; } = new();         // Pass → Fail
        public List<CheckTransition> NewlyFailing { get; set; } = new();      // not in baseline, failing now
        public List<CheckTransition> DisappearedFailing { get; set; } = new();// failing at baseline, absent now

        /// <summary>Current composite score minus baseline composite score. Positive = improvement.</summary>
        public int HealthDelta => CurrentCompositeScore - BaselineCompositeScore;
    }

    public class IntegrityViolation
    {
        public long IntegrityId { get; set; }
        public string ReportType { get; set; } = "";
        public long ReportId { get; set; }
        public string ServerName { get; set; } = "";
        public string RecordedAt { get; set; } = "";
        public string ExpectedPrevious { get; set; } = "";
        public string ActualPrevious { get; set; } = "";
        public string Message { get; set; } = "";
    }
}
