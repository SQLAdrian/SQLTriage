/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    /// <summary>
    /// In-memory <c>checkId → SQL</c> resolution seam (worklist #27, Phase A —
    /// app-lane ingest split).
    ///
    /// The architectural goal of #27 is that check SQL is never coupled to the
    /// catalogue metadata object at execution time: the executor asks this
    /// store for a check's SQL by id. Phase A populates the store from the
    /// already-loaded combined catalogue (zero corpus-pipeline change). Phase B
    /// will swap the loader for "decrypt per-license entitlement bundle →
    /// populate" — with the executor and every other consumer UNCHANGED,
    /// because the seam is this interface, not the loader.
    ///
    /// Fault-tolerant by construction (doctrine #4): if the store is empty or a
    /// id is missing, the executor falls back to the inline SqlQuery, so Phase
    /// A is behaviour-identical and reversible.
    /// </summary>
    /// <summary>Outcome of a per-query SQL integrity check (S1, worklist).</summary>
    public enum SqlIntegrity
    {
        /// <summary>SQL matches the checksum captured at bundle-load time.</summary>
        Ok,
        /// <summary>No checksum was captured for this id (e.g. inline-fallback SQL).
        /// Treated as allow + log-once (back-compat decision, Adrian 2026-05-26).</summary>
        Missing,
        /// <summary>SQL differs from the captured checksum — block + flag Corrupted.</summary>
        Mismatch
    }

    public class CheckSqlStore
    {
        private volatile IReadOnlyDictionary<string, string> _map =
            new Dictionary<string, string>();

        // checkId → SHA-256 (lowercase hex) of the NORMALISED SQL captured at the
        // moment the SQL entered the runtime map. The SQL itself arrives inside the
        // GCM-authenticated bundle (already tamper-evident on disk); this baseline
        // catches in-memory / post-decrypt tampering between load and execution.
        private volatile IReadOnlyDictionary<string, string> _checksums =
            new Dictionary<string, string>();

        /// <summary>Provenance of the current map (observability, doctrine #14).</summary>
        public string Source { get; private set; } = "unpopulated";

        public int Count => _map.Count;

        /// <summary>
        /// Phase A loader: rebuild the map from the loaded catalogue.
        /// Idempotent — safe to call before every run (atomic swap).
        /// Captures a SHA-256 integrity baseline per check at the same time.
        /// </summary>
        public void PopulateFromCatalogue(IEnumerable<SqlCheck> checks)
        {
            var next = new Dictionary<string, string>();
            var sums = new Dictionary<string, string>();
            foreach (var c in checks)
            {
                if (string.IsNullOrEmpty(c.Id)) continue;
                if (!string.IsNullOrWhiteSpace(c.SqlQuery))
                {
                    next[c.Id] = c.SqlQuery;
                    sums[c.Id] = ComputeHash(c.SqlQuery);
                }
            }
            _map = next;
            _checksums = sums;
            Source = "ingest-split:combined-json";
        }

        /// <summary>Resolve a check's SQL by id. False ⇒ caller falls back to inline.</summary>
        public bool TryGet(string checkId, out string sql)
        {
            if (!string.IsNullOrEmpty(checkId) && _map.TryGetValue(checkId, out var s))
            {
                sql = s;
                return true;
            }
            sql = string.Empty;
            return false;
        }

        /// <summary>
        /// Verifies <paramref name="sqlText"/> against the checksum captured for
        /// <paramref name="checkId"/> at load time. <see cref="SqlIntegrity.Missing"/>
        /// when no baseline exists (inline-fallback SQL or unpopulated store).
        /// </summary>
        public SqlIntegrity Verify(string checkId, string sqlText)
        {
            if (string.IsNullOrEmpty(checkId) || !_checksums.TryGetValue(checkId, out var expected))
                return SqlIntegrity.Missing;
            return ComputeHash(sqlText) == expected ? SqlIntegrity.Ok : SqlIntegrity.Mismatch;
        }

        /// <summary>
        /// Canonical SQL normalisation + SHA-256 (lowercase hex). Defined ONCE here so
        /// capture and verify can never disagree. Normalisation: CRLF/CR → LF, then
        /// trim outer whitespace (interior whitespace is left intact — SQL-significant).
        /// </summary>
        public static string ComputeHash(string sqlText)
        {
            var normalised = (sqlText ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
