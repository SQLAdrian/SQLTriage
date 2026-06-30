/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationOpRenderer — the SINGLE renderer that turns a structured
 * RemediationOperation into the exact T-SQL the gate classifies and the executor
 * runs. One pure function, one source of truth: because the runner's gate and the
 * executor both render from the SAME op, the safety gate provably vets WHAT RUNS.
 * (The prior design classified a fixed, unrelated probe — "show advanced options"
 *  — while the executor ran a separate dbatools command the gate never inspected.)
 *
 * Injection-free by construction: the configuration NAME is shipped (validated to a
 * safe charset and single-quote escaped) and the only bound parameter is an integer
 * constrained to the op's [MinValue, MaxValue]. No untrusted text reaches the SQL.
 */

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationOpRenderer
    {
        // sp_configure option names are letters/digits/spaces/parens only (e.g. "max degree of
        // parallelism", "max server memory (MB)"). Reject anything else — a defence against a
        // crafted overlay template (the overlay may ADD non-shipped templates, so its op text is
        // semi-trusted). Parens are harmless inside the single-quoted, escaped string literal.
        private static readonly Regex SafeConfigName =
            new(@"^[A-Za-z0-9 ()]{1,128}$", RegexOptions.Compiled);

        // ── CreateIndex op: parameter keys + identifier guard ───────────────────
        // The index spec rides request parameters (a missing-index DMV candidate), not the
        // template — same model as the bounded integer value. Every identifier is charset-guarded
        // THEN bracket-quoted, so the rendered DDL is injection-free by construction.
        public const string IndexDatabaseParam = "Index.Database";
        public const string IndexSchemaParam   = "Index.Schema";
        public const string IndexTableParam    = "Index.Table";
        public const string IndexNameParam     = "Index.Name";
        public const string IndexKeyColumnsParam      = "Index.KeyColumns";      // comma-separated, >= 1
        public const string IndexIncludedColumnsParam = "Index.IncludedColumns"; // comma-separated, optional

        // A single SQL identifier we are willing to emit: letters/digits/underscore/space/$/#/@ only.
        // Deliberately conservative — it rejects '.', '[', ']', ''', ';', '-', etc., which is exactly
        // what stops a crafted column/object name from breaking out of the brackets. Missing-index
        // recommendations always have ordinary names; weird names are rejected, not escaped-and-hoped.
        // \A ... \z (not ^ ... $): in .NET, $ also matches BEFORE a trailing newline, so ^...$
        // would accept "Orders\n". \z anchors to the absolute end — no trailing-newline slip.
        private static readonly Regex SafeIdentifier =
            new(@"\A[A-Za-z0-9_@$# ]{1,128}\z", RegexOptions.Compiled);

        /// <summary>
        /// True if a value is a renderable SQL identifier under the guard charset. Public so the
        /// candidate-sourcing layer can pre-filter (drop) candidates whose identifiers would be
        /// rejected at render — the SAME charset, one source of truth — rather than offering a
        /// candidate that always errors, or one a lossy transport would mangle into a different index.
        /// </summary>
        public static bool IsSafeIdentifier(string? value) => SafeIdentifier.IsMatch((value ?? string.Empty).Trim());

        // Charset-guard then bracket-quote (belt + braces: the regex already rejects ']').
        private static bool TryQuoteIdentifier(string? raw, out string quoted, out string error)
        {
            quoted = string.Empty; error = string.Empty;
            var t = (raw ?? string.Empty).Trim();
            if (!SafeIdentifier.IsMatch(t)) { error = $"Unsafe or empty SQL identifier: '{raw}'."; return false; }
            quoted = "[" + t.Replace("]", "]]") + "]";
            return true;
        }

        // Charset-guard then emit an N'...' literal (for the sys.* exists-read predicate).
        private static bool TryQuoteLiteral(string? raw, out string literal, out string error)
        {
            literal = string.Empty; error = string.Empty;
            var t = (raw ?? string.Empty).Trim();
            if (!SafeIdentifier.IsMatch(t)) { error = $"Unsafe or empty SQL identifier: '{raw}'."; return false; }
            literal = "N'" + t.Replace("'", "''") + "'";
            return true;
        }

        private static List<string> SplitColumns(string? csv) =>
            (csv ?? string.Empty)
                .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .ToList();

        /// <summary>The fully-guarded index spec resolved from request parameters.</summary>
        public sealed class IndexSpec
        {
            public string Database = "", Schema = "", Table = "", Name = "";
            public List<string> KeyColumns = new();
            public List<string> IncludedColumns = new();
        }

        /// <summary>
        /// Resolves + guards every identifier of a CreateIndex spec from request parameters.
        /// Fails closed if any identifier is unsafe, the database/schema/table/name is missing,
        /// or there is no key column. This is the single guard the renders below depend on.
        /// </summary>
        public static bool TryResolveIndexSpec(IReadOnlyDictionary<string, string> parameters, out IndexSpec spec, out string error)
        {
            spec = new IndexSpec(); error = string.Empty;
            if (parameters is null) { error = "No index parameters supplied."; return false; }

            string Get(string k) => parameters.TryGetValue(k, out var v) ? v : string.Empty;

            foreach (var (val, label, target) in new[]
            {
                (Get(IndexDatabaseParam), "database", 0),
                (Get(IndexSchemaParam),   "schema",   1),
                (Get(IndexTableParam),    "table",    2),
                (Get(IndexNameParam),     "index name", 3),
            })
            {
                if (string.IsNullOrWhiteSpace(val)) { error = $"Missing index {label}."; return false; }
                // Validate each is a safe identifier (quoting happens at render time).
                if (!SafeIdentifier.IsMatch(val.Trim())) { error = $"Unsafe index {label}: '{val}'."; return false; }
                switch (target) { case 0: spec.Database = val.Trim(); break; case 1: spec.Schema = val.Trim(); break; case 2: spec.Table = val.Trim(); break; default: spec.Name = val.Trim(); break; }
            }

            spec.KeyColumns = SplitColumns(Get(IndexKeyColumnsParam));
            spec.IncludedColumns = SplitColumns(Get(IndexIncludedColumnsParam));
            if (spec.KeyColumns.Count == 0) { error = "An index needs at least one key column."; return false; }
            foreach (var c in spec.KeyColumns.Concat(spec.IncludedColumns))
                if (!SafeIdentifier.IsMatch(c)) { error = $"Unsafe index column: '{c}'."; return false; }
            return true;
        }

        /// <summary>Renders the CREATE INDEX DDL from a guarded spec. Plain (non-unique, nonclustered).</summary>
        public static bool TryRenderCreateIndex(IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveIndexSpec(parameters, out var s, out error)) return false;

            if (!TryQuoteIdentifier(s.Schema, out var schema, out error)) return false;
            if (!TryQuoteIdentifier(s.Table, out var table, out error)) return false;
            if (!TryQuoteIdentifier(s.Name, out var name, out error)) return false;

            var keys = new List<string>();
            foreach (var c in s.KeyColumns) { if (!TryQuoteIdentifier(c, out var q, out error)) return false; keys.Add(q); }
            var incs = new List<string>();
            foreach (var c in s.IncludedColumns) { if (!TryQuoteIdentifier(c, out var q, out error)) return false; incs.Add(q); }

            var sb = new StringBuilder();
            sb.Append("CREATE INDEX ").Append(name).Append(" ON ").Append(schema).Append('.').Append(table)
              .Append(" (").Append(string.Join(", ", keys)).Append(')');
            if (incs.Count > 0) sb.Append(" INCLUDE (").Append(string.Join(", ", incs)).Append(')');
            sb.Append(';');
            sql = sb.ToString();
            return true;
        }

        /// <summary>Renders the DROP INDEX rollback from a guarded spec (the clean inverse of CREATE).</summary>
        public static bool TryRenderDropIndex(IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveIndexSpec(parameters, out var s, out error)) return false;
            if (!TryQuoteIdentifier(s.Schema, out var schema, out error)) return false;
            if (!TryQuoteIdentifier(s.Table, out var table, out error)) return false;
            if (!TryQuoteIdentifier(s.Name, out var name, out error)) return false;
            sql = $"DROP INDEX {name} ON {schema}.{table};";
            return true;
        }

        /// <summary>
        /// Renders the read that confirms whether the named index exists on the table — the
        /// snapshot (already-present?) and the post-apply verify. Identifiers go in as guarded
        /// N'...' literals against sys.* catalog views (a read; Validate-Safe).
        /// </summary>
        public static bool TryRenderIndexExistsRead(IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty;
            if (!TryResolveIndexSpec(parameters, out var s, out error)) return false;
            if (!TryQuoteLiteral(s.Schema, out var schema, out error)) return false;
            if (!TryQuoteLiteral(s.Table, out var table, out error)) return false;
            if (!TryQuoteLiteral(s.Name, out var name, out error)) return false;
            sql =
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i " +
                "INNER JOIN sys.objects o ON i.object_id = o.object_id " +
                "INNER JOIN sys.schemas sc ON o.schema_id = sc.schema_id " +
                $"WHERE sc.name = {schema} AND o.name = {table} AND i.name = {name}) THEN 1 ELSE 0 END;";
            return true;
        }

        /// <summary>
        /// Resolves and bounds-checks the target value from the request parameters.
        /// Returns false with a human error when the op's value parameter is missing,
        /// not an integer, or outside [MinValue, MaxValue].
        /// </summary>
        public static bool TryResolveValue(
            RemediationOperation op, IReadOnlyDictionary<string, string> parameters,
            out int value, out string error)
        {
            value = 0; error = string.Empty;
            if (op is null) { error = "No operation on the template."; return false; }
            if (string.IsNullOrWhiteSpace(op.ValueParam)) { error = "Operation has no value parameter."; return false; }
            if (parameters is null || !parameters.TryGetValue(op.ValueParam, out var raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = $"Operation requires an integer '{op.ValueParam}' parameter.";
                return false;
            }
            if (value < op.MinValue || value > op.MaxValue)
            {
                error = $"'{op.ValueParam}' value {value} is outside the allowed range [{op.MinValue}, {op.MaxValue}].";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Renders the apply T-SQL for a (bounds-checked) value. Returns false with an
        /// error if the op is malformed (unknown kind / unsafe config name).
        /// </summary>
        public static bool TryRender(RemediationOperation op, int value, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            if (op is null) { error = "No operation."; return false; }
            switch (op.OpKind)
            {
                case RemediationOpKind.SpConfigure:
                    if (string.IsNullOrWhiteSpace(op.ConfigName) || !SafeConfigName.IsMatch(op.ConfigName))
                    {
                        error = $"Unsafe or empty sp_configure option name: '{op.ConfigName}'.";
                        return false;
                    }
                    var name = op.ConfigName.Replace("'", "''");
                    var v = value.ToString(CultureInfo.InvariantCulture);
                    var sb = new StringBuilder();
                    if (op.AdvancedOption)
                        sb.Append("EXEC sp_configure 'show advanced options', 1; RECONFIGURE; ");
                    sb.Append("EXEC sp_configure '").Append(name).Append("', ").Append(v).Append("; RECONFIGURE;");
                    sql = sb.ToString();
                    return true;
                default:
                    error = $"Unsupported operation kind '{op.OpKind}'.";
                    return false;
            }
        }

        /// <summary>
        /// Renders the read query that captures/verifies the op's current value — DERIVED from
        /// the op (single-statement, no free-form template text). For a Configuration template
        /// the executor uses this instead of any template-supplied snapshot/verify SQL, so an
        /// overlay cannot smuggle a write through the read path (the prior risk: Validate()
        /// exempts a whole batch if any `SELECT FROM sys.` is present). Injection-free: the
        /// config name is charset-guarded and single-quote escaped.
        /// </summary>
        public static bool TryRenderRead(RemediationOperation op, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            if (op is null) { error = "No operation."; return false; }
            switch (op.OpKind)
            {
                case RemediationOpKind.SpConfigure:
                    if (string.IsNullOrWhiteSpace(op.ConfigName) || !SafeConfigName.IsMatch(op.ConfigName))
                    {
                        error = $"Unsafe or empty sp_configure option name: '{op.ConfigName}'.";
                        return false;
                    }
                    sql = "SELECT value_in_use FROM sys.configurations WHERE name = '"
                        + op.ConfigName.Replace("'", "''") + "';";
                    return true;
                default:
                    error = $"Unsupported operation kind '{op.OpKind}'.";
                    return false;
            }
        }

        /// <summary>
        /// Renders a value-independent representative form for the GATE's classification.
        /// The safety classification of an sp_configure write does not depend on the
        /// integer value, so the gate classifies this representative rendering (the op's
        /// lower bound); the executor renders + runs the real, bounds-checked value via
        /// <see cref="TryRender"/>. Returns false only if the op itself is malformed.
        /// </summary>
        public static bool TryRenderForClassification(RemediationOperation op, out string sql, out string error)
        {
            // The safety classification of CREATE INDEX does not depend on which index — the
            // statement SHAPE is what's classified (a write, gated only by a registered key). So
            // the gate classifies a fixed representative CREATE INDEX; the executor renders the
            // real, fully-guarded identifiers from request parameters via TryRenderCreateIndex.
            if (op is not null && op.OpKind == RemediationOpKind.CreateIndex)
            {
                sql = "CREATE INDEX [ix_remediation_representative] ON [dbo].[t] ([c]);";
                error = string.Empty;
                return true;
            }
            return TryRender(op, op?.MinValue ?? 0, out sql, out error);
        }
    }
}
