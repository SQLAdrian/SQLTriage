/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SQLTriage.Data
{
    /// <summary>
    /// Verbose per-check audit diagnostic sink. Emits one JSON line per executed
    /// check to <c>logs/audit-diag-yyyyMMdd.jsonl</c> (alongside the Serilog file).
    ///
    /// Purpose: the app window collapses {legit-finding, INFO, WARN, empty-result,
    /// swallowed-error} all into "FAIL val=0". This sink captures the untruncated
    /// raw outcome + the check's execution contract so the 600+ red results can be
    /// mechanically triaged into corrupt-SQL / runtime-error / empty / legit / info.
    ///
    /// Pure observation: never mutates execution, never throws into the caller.
    /// Append-only, process-wide lock (≈700 lines/run — negligible).
    /// </summary>
    public static class AuditDiagnosticSink
    {
        private static readonly object _gate = new();

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = false
        };

        /// <summary>Set false to disable (wired from CheckExecution:DiagnosticJsonl).</summary>
        public static bool Enabled { get; set; } = true;

        public static void Record(
            string serverName,
            SQLTriage.Data.Models.SqlCheck check,
            SQLTriage.Data.Models.CheckResult result)
        {
            if (!Enabled) return;

            try
            {
                var sql = check.SqlQuery ?? string.Empty;
                var firstLine = FirstMeaningfulLine(sql);

                var line = new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    server = serverName,
                    id = check.Id,
                    category = check.Category,
                    severity = check.Severity,
                    execType = check.ExecutionType,
                    resultInterpretation = check.ResultInterpretation,
                    rowCountCondition = check.RowCountCondition,
                    expected = check.ExpectedValue,
                    actual = result.ActualValue,
                    passed = result.Passed,
                    // Message holds resultText verbatim on the text path,
                    // or "Check failed: got X, expected Y" on the numeric path.
                    message = result.Message,
                    error = result.ErrorMessage,
                    durationMs = result.DurationMs,
                    sqlLen = sql.Length,
                    sqlFirstLine = firstLine,
                    sqlSha8 = Sha8(sql)
                };

                var path = LogPath();
                lock (_gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path,
                        JsonSerializer.Serialize(line, _json) + Environment.NewLine,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch
            {
                // Diagnostic must never break an audit run. Swallow deliberately.
            }
        }

        private static string LogPath() =>
            Path.Combine(AppContext.BaseDirectory, "logs",
                $"audit-diag-{DateTime.Now:yyyyMMdd}.jsonl");

        private static string FirstMeaningfulLine(string sql)
        {
            foreach (var raw in sql.Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("/*") || l.StartsWith("*") || l.StartsWith("--")) continue;
                return l.Length > 200 ? l[..200] : l;
            }
            return string.Empty;
        }

        private static string Sha8(string sql)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
            return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
        }
    }
}
