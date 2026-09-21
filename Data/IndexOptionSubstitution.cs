/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data
{
    /// <summary>The index-build options an operator selects in the plan viewer's No-Pants panel.</summary>
    /// <param name="Online">ONLINE = ON when set. Enterprise-class editions only; the caller gates the checkbox.</param>
    /// <param name="SortInTempDb">SORT_IN_TEMPDB = ON when set.</param>
    /// <param name="Resumable">
    /// Operator's RESUMABLE preference. Only honoured when <paramref name="Online"/> is also set —
    /// SQL Server rejects RESUMABLE = ON with ONLINE = OFF, and the checkbox can hold a stale true
    /// after ONLINE is cleared because it is disabled rather than reset.
    /// </param>
    /// <param name="MaxDop">MAXDOP value, substituted as written.</param>
    /// <param name="Compression">DATA_COMPRESSION value: NONE, ROW, or PAGE.</param>
    /// <param name="OptimizeForSequentialKey">Appends OPTIMIZE_FOR_SEQUENTIAL_KEY = ON when set.</param>
    public readonly record struct IndexOptions(
        bool Online,
        bool SortInTempDb,
        bool Resumable,
        int MaxDop,
        string Compression,
        bool OptimizeForSequentialKey);

    /// <summary>
    /// THE substitution that turns a suggested-index template into the statement that runs.
    ///
    /// <para><b>Why this is its own class (F-B, ruled 2026-09-01).</b> There used to be two
    /// implementations of this rule. This one substituted the operator's live checkbox state on
    /// execute; <c>queryPlanV2.js substituteDefaultOptions()</c> substituted its own hardcoded
    /// literals — ONLINE→OFF, SORT_IN_TEMPDB→ON, MAXDOP→0, DATA_COMPRESSION→NONE, RESUMABLE and
    /// OPTIMIZE_FOR_SEQUENTIAL_KEY dropped — for the sidebar preview and the Copy DDL button. The
    /// C# defaults are ONLINE ON / SORT_IN_TEMPDB ON / MAXDOP 8, so the two disagreed on a freshly
    /// opened modal with nothing touched. Always-on divergence, and the operator read the preview.
    /// </para>
    ///
    /// <para>The JS copy is gone. The preview now calls back into
    /// <c>QueryPlanModal.PreviewIndexDdl</c>, which calls <c>ApplyIndexOptions</c>, which calls
    /// this method — the same call execute makes. Byte-for-byte parity between the previewed and
    /// the executed statement is therefore structural: there is nothing left to keep in step.</para>
    ///
    /// <para>It lives here rather than inside the component so it is assertable without rendering
    /// Blazor. A substitution reachable only through a component is a substitution CI never
    /// exercises across its option combinations, and unexercised-across-combinations is exactly
    /// how a preview came to disagree with execution on the default settings.</para>
    /// </summary>
    public static class IndexOptionSubstitution
    {
        /// <summary>
        /// Every placeholder token this substitution is responsible for, in single-brace form.
        /// Public so a test can assert that no combination of options leaves one behind: a
        /// surviving <c>{MAXDOP}</c> would be sent to the server verbatim and fail at parse time.
        /// </summary>
        public static readonly string[] PlaceholderNames =
        {
            "ONLINE", "SORT_IN_TEMPDB", "MAXDOP", "COMPRESSION", "RESUMABLE", "SEQUENTIAL_KEY"
        };

        /// <summary>
        /// Substitutes the option placeholders, then normalises the statement: trailing comment
        /// lines removed, trimmed, exactly one closing semicolon. The normalisation is part of the
        /// contract, not a cosmetic afterthought — the preview shows it and the server receives it,
        /// so it has to be the same operation on both sides.
        /// </summary>
        public static string Apply(string? ddl, IndexOptions options)
        {
            var text = ddl ?? string.Empty;

            // RESUMABLE is only valid with ONLINE = ON — drop it if online is off, even if the
            // (now-disabled) checkbox still holds a stale true.
            var resumable = options.Resumable && options.Online;

            var online = options.Online ? "ON" : "OFF";
            var sortInTempDb = options.SortInTempDb ? "ON" : "OFF";
            var maxDop = options.MaxDop.ToString();
            var compression = options.Compression ?? string.Empty;
            var resumableClause = resumable ? ",\n        RESUMABLE = ON" : "";
            var sequentialKeyClause = options.OptimizeForSequentialKey ? ",\n        OPTIMIZE_FOR_SEQUENTIAL_KEY = ON" : "";

            // Both brace forms are handled, DOUBLE FIRST. Two notes for the next reader:
            //
            // (1) Nothing emits the double form today. The double pass was added because
            // ExecutionPlanParser's source reads `$"... ONLINE = {{ONLINE}},"` and that looks like
            // a double-brace token — but it is an INTERPOLATED string, so `{{` is an escape and the
            // runtime text is a SINGLE brace. The double pass is therefore defensive, not load-bearing.
            //
            // (2) Order matters, and the order this replaced was wrong. QueryPlanModal ran the
            // single pass first, which turns "{{ONLINE}}" into "{" + "ON" + "}" = "{ON}" — after
            // which the double pass matches nothing and a malformed token reaches the server.
            // Unreachable given (1), so no shipped statement was affected; corrected here because a
            // defensive branch that cannot work is not a defence.
            text = text
                .Replace("{{ONLINE}}", online)
                .Replace("{{SORT_IN_TEMPDB}}", sortInTempDb)
                .Replace("{{MAXDOP}}", maxDop)
                .Replace("{{COMPRESSION}}", compression)
                .Replace("{{RESUMABLE}}", resumableClause)
                .Replace("{{SEQUENTIAL_KEY}}", sequentialKeyClause);

            text = text
                .Replace("{ONLINE}", online)
                .Replace("{SORT_IN_TEMPDB}", sortInTempDb)
                .Replace("{MAXDOP}", maxDop)
                .Replace("{COMPRESSION}", compression)
                .Replace("{RESUMABLE}", resumableClause)
                .Replace("{SEQUENTIAL_KEY}", sequentialKeyClause);

            // Strip trailing comment lines (-- ...) — SQL Server executes them fine but strip for clarity
            var lines = text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))
                .ToList();
            return string.Join('\n', lines).Trim().TrimEnd(';') + ";";
        }
    }
}
