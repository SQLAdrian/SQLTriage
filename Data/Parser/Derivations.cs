/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// Per-check derivation info — emitted when a field defaulted (not
    /// present in YAML). Aggregated by the loader, flushed to ILogger /
    /// AuditDiagnosticSink. Triggers the >50% banner per B1 §6.
    /// </summary>
    public sealed record DerivationInfo(string CheckId, string Field, string Source, string Rationale);

    /// <summary>
    /// B1 §3 — pure derivation rules for the gappy load-bearing fields.
    /// All methods deterministic + idempotent (same input → same output).
    /// </summary>
    public static class Derivations
    {
        private static readonly Regex ExpectedIntInProse =
            new(@"(?:expected|returns)\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// ExpectedValue (B1 §3.1) — only 35% of YAMLs carry expected_result.
        /// Rule: parse explicit int if present, else default 0 (binary
        /// 0=good convention; dominant ~90% of catalogue).
        /// </summary>
        public static (int value, DerivationInfo? info) DeriveExpectedValue(
            string checkId, string? expectedResult, string sql)
        {
            if (!string.IsNullOrWhiteSpace(expectedResult))
            {
                var m = ExpectedIntInProse.Match(expectedResult);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed))
                    return (parsed, null); // YAML-sourced
            }
            return (0, new DerivationInfo(checkId, "ExpectedValue", "derived",
                "default 0 (binary 0=good convention; B1 §3.1)"));
        }

        /// <summary>
        /// ExecutionType (B1 §3.2) — only 0.1% of YAMLs carry execution_type.
        /// Inspect SQL shape: text-result path → Scalar; CASE-WHEN scalar → Binary;
        /// multi-row + RowCountCondition → RowCount; else Scalar (safe default).
        /// </summary>
        public static (string value, DerivationInfo? info) DeriveExecutionType(
            string checkId, string sql, string? resultInterpretation, string? rowCountCondition)
        {
            if (!string.IsNullOrEmpty(resultInterpretation) &&
                resultInterpretation.Contains("Pass", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(sql ?? "", @"SELECT\s+['""]PASS['""]", RegexOptions.IgnoreCase))
            {
                return ("Scalar", new DerivationInfo(checkId, "ExecutionType", "derived",
                    "text-result path (SELECT 'PASS')"));
            }
            if (!string.IsNullOrEmpty(sql) &&
                Regex.IsMatch(sql, @"SELECT\s+CASE\s+WHEN.+THEN\s+1\s+ELSE\s+0\s+END",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                return ("Binary", new DerivationInfo(checkId, "ExecutionType", "derived",
                    "binary CASE-WHEN scalar shape"));
            }
            if (!string.IsNullOrEmpty(rowCountCondition))
            {
                return ("RowCount", new DerivationInfo(checkId, "ExecutionType", "derived",
                    "RowCountCondition present"));
            }
            return ("Scalar", new DerivationInfo(checkId, "ExecutionType", "derived",
                "safe default (Scalar)"));
        }

        /// <summary>
        /// ScoreWeight (B1 §3.3) — only 24% of YAMLs carry score_weight.
        /// Derivation: severity baseline × effort multiplier, clamp [1,50].
        /// Locked table per Adrian sign-off 2026-05-20.
        /// </summary>
        public static (int value, DerivationInfo? info) DeriveScoreWeight(
            string checkId, int? scoreWeightYaml, string severity, string? effortEstimate)
        {
            if (scoreWeightYaml is int sw && sw > 0)
                return (Math.Clamp(sw, 1, 50), null);

            var baseline = (severity ?? "").Trim().ToLowerInvariant() switch
            {
                "critical" => 25,
                "high" => 15,
                "medium" => 10,
                "low" => 5,
                _ => 10  // unknown severity → medium baseline
            };
            var multiplier = (effortEstimate ?? "").Trim().ToLowerInvariant() switch
            {
                "high" => 2.0,
                "medium" => 1.5,
                _ => 1.0   // low / absent / unknown
            };
            var derived = Math.Clamp((int)Math.Round(baseline * multiplier), 1, 50);
            return (derived, new DerivationInfo(checkId, "ScoreWeight", "derived",
                $"severity={severity} × effort={effortEstimate ?? "low/unknown"} = {derived}"));
        }

        /// <summary>
        /// IsBad (B1 §3.4) — 96% of YAMLs carry `bad`. Default false
        /// (informational, not costing) when absent.
        /// </summary>
        public static (bool value, DerivationInfo? info) DeriveIsBad(string checkId, bool? badYaml)
        {
            if (badYaml.HasValue) return (badYaml.Value, null);
            return (false, new DerivationInfo(checkId, "IsBad", "derived",
                "default false (informational; B1 §3.4)"));
        }

        // Detect single-quoted verdict tokens anywhere in the SQL body (after
        // line-comment strip). Covers BOTH shapes seen in the corpus:
        //   SELECT 'PASS' AS result           (direct)
        //   SELECT CASE WHEN ... THEN 'PASS' ELSE 'INFO' END AS result   (case-expr)
        //   SET @r = 'PASS'; SELECT @r        (variable assignment)
        // The earlier SELECT-anchored pattern missed the CASE-expr shape and
        // produced 158 runtime null-results in the post-G1 audit.
        private static readonly Regex LineComment = new(@"--[^\n]*", RegexOptions.Compiled);
        private static readonly Regex TextResultToken = new(
            @"'(PASS|FAIL|INFO|WARN|SKIP)'",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// ResultInterpretation (B1 §3 — added 2026-05-21 to fix the
        /// text-result classification bug: 473/522 audit errors after the
        /// B3 parser flip were "'PASS' was not in a correct format" — text-result
        /// SQL routed through the numeric executor path because no
        /// ResultInterpretation hint existed).
        ///
        /// Rule: if the SQL emits any literal verdict tokens via
        /// `SELECT 'PASS'/'INFO'/'FAIL'/'WARN'/'SKIP'`, return the canonical
        /// PassFail / PassWarnFail / PassInfo / PassFailSkip name so the
        /// executor's text path activates. Else null → numeric path.
        /// </summary>
        public static (string? value, DerivationInfo? info) DeriveResultInterpretation(string checkId, string sql)
        {
            if (string.IsNullOrEmpty(sql)) return (null, null);
            // Strip line-comments first so `-- check PASS rate` style mentions
            // don't trigger; the verdict tokens we care about are SQL string
            // literals in executable code (SELECT/CASE/SET contexts).
            var stripped = LineComment.Replace(sql, "");
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in TextResultToken.Matches(stripped))
                tokens.Add(m.Groups[1].Value.ToUpperInvariant());
            if (tokens.Count == 0) return (null, null);

            string interp =
                  tokens.Contains("WARN") && tokens.Contains("FAIL") ? "PassWarnFail"
                : tokens.Contains("FAIL") && tokens.Contains("SKIP") ? "PassFailSkip"
                : tokens.Contains("INFO") && !tokens.Contains("FAIL") ? "PassInfo"
                : tokens.Contains("FAIL") ? "PassFail"
                : "PassFail"; // PASS + (INFO|SKIP|WARN) alone → conservative PassFail

            return (interp, new DerivationInfo(checkId, "ResultInterpretation", "derived",
                $"SQL emits text verdict tokens {{{string.Join(",", tokens)}}} → {interp}"));
        }
    }
}
