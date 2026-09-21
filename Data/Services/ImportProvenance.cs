/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// #84 results-import: the single source of truth for reasoning about IMPORTED runs — is a
    /// result/run imported, when was it imported, and does an imported run win the /audit grid over
    /// a server's local run. Promoted out of the QuickCheck page's @code so the precedence rule is
    /// unit-testable in isolation (see ImportProvenanceTests) instead of buried in a .razor.
    ///
    /// Provenance lives ON the result rows (<see cref="CheckResult.ImportedAtUtc"/> /
    /// <see cref="CheckResult.ImportSourceFile"/>, stamped by <see cref="ImportResultsService"/>),
    /// so it survives the QuickCheckResultStore round-trip and flows through the same
    /// <see cref="CheckExecutionService.GetResults"/> path local runs use — no separate counting
    /// path (that is how skip-as-pass was born).
    /// </summary>
    public static class ImportProvenance
    {
        /// <summary>A single result is imported iff it carries an import timestamp.</summary>
        public static bool IsImported(CheckResult r) => r.ImportedAtUtc != null;

        /// <summary>
        /// A run (a server's result set) is imported when ANY of its rows carries import
        /// provenance. In the both-local-and-imported case, <see cref="CheckExecutionService.GetResults"/>
        /// returns the newest run's rows first and may append older-run rows only to fill genuine
        /// per-check coverage gaps; when the newest run is the imported one, its rows are stamped,
        /// so Any() correctly reports the run as imported.
        /// </summary>
        public static bool RunIsImported(IEnumerable<CheckResult> rows) =>
            rows != null && rows.Any(IsImported);

        /// <summary>
        /// The import time of a run: the most recent <see cref="CheckResult.ImportedAtUtc"/> across
        /// its imported rows, or null if none is imported. Used as the run's timestamp for the
        /// newest-wins comparison below and for the restored-run summary.
        /// </summary>
        public static DateTime? RunImportTime(IEnumerable<CheckResult> rows)
        {
            if (rows == null) return null;
            DateTime? max = null;
            foreach (var r in rows)
                if (r.ImportedAtUtc is DateTime t && (max == null || t > max)) max = t;
            return max;
        }

        /// <summary>
        /// Newest-run-wins precedence for a single instance: an imported run displaces a server's
        /// local run on the /audit grid ONLY when it is strictly newer (or the local run's time is
        /// unknown). A tie keeps the local (incumbent) run. This is the same "newest run wins"
        /// rule local-vs-local already follows via the store; it guarantees an imported run is
        /// never silently merged into a local run's counts — one run wins outright, never both.
        /// </summary>
        public static bool ImportedRunWins(DateTime importedRunTimeUtc, DateTime? localRunTimeUtc) =>
            localRunTimeUtc is null || importedRunTimeUtc > localRunTimeUtc.Value;
    }
}
