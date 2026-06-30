/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using SQLTriage.Data.Models;
using YamlDotNet.RepresentationModel;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// B2 Slice 3 — builds one <see cref="SqlCheck"/> from (yaml map +
    /// optional .sql fallback body + mapping resolver context).
    /// Applies the B1 §2 field map, invokes <see cref="ParseInvariants"/>
    /// for fail-fast gates, applies <see cref="Derivations"/> for the
    /// gappy fields, and emits <see cref="DerivationInfo"/> records.
    ///
    /// FrameworkMappings handling is deferred to Slice 4 (Loader builds a
    /// side-index — not a property of <see cref="SqlCheck"/> in the
    /// existing model).
    /// </summary>
    public static class SqlCheckBuilder
    {
        public sealed record BuildResult(SqlCheck Check, IReadOnlyList<DerivationInfo> Derivations);

        /// <summary>
        /// Build one check. <paramref name="yamlPath"/> is used only for
        /// exception context; YAML content comes from <paramref name="yaml"/>.
        /// <paramref name="sqlFallbackBody"/> may be null (no .sql file alongside).
        /// <paramref name="seenIds"/> tracks ids across the load for collision detection.
        /// </summary>
        public static BuildResult Build(
            IReadOnlyDictionary<string, YamlNode> yaml,
            string yamlPath,
            string? sqlFallbackBody,
            ISet<string> seenIds)
        {
            ParseInvariants.RequireFields(yamlPath, yaml);
            ParseInvariants.RequireSchemaV2(yamlPath, yaml);

            var checkId = Scalar(yaml, "id");
            ParseInvariants.RequireV2CheckId(yamlPath, checkId);
            ParseInvariants.RejectCollision(yamlPath, checkId, seenIds);

            // type drift checks on the load-bearing structured fields
            ParseInvariants.RequireType(yamlPath, checkId, "framework_mappings",
                yaml["framework_mappings"], typeof(YamlSequenceNode));

            // Execution method: default (null/empty) = T-SQL; "host-probe" = an OS/AD probe via
            // probe_key (no `## Query` SQL). The app dispatches on Method before any SQL is touched.
            var method = ScalarOrNull(yaml, "method");
            var probeKey = ScalarOrNull(yaml, "probe_key");
            bool isHostProbe = string.Equals(method, "host-probe", StringComparison.OrdinalIgnoreCase);

            // V2 SQL is extracted from Markdown `## Query` block (synthetic field 'query_sql')
            // or falls back to .sql sidecar for salvaged V1 checks. Host-probe checks carry no SQL
            // (they run a probe) — they require a probe_key instead.
            var sql = ScalarOrNull(yaml, "query_sql") ?? sqlFallbackBody;
            if (string.IsNullOrWhiteSpace(sql) && !isHostProbe)
            {
                throw new SourceParseException(yamlPath, checkId, "no SQL: query block absent in Markdown body");
            }
            if (isHostProbe && string.IsNullOrWhiteSpace(probeKey))
            {
                throw new SourceParseException(yamlPath, checkId, "method:host-probe requires a probe_key");
            }

            var resultContract = ScalarOrNull(yaml, "result_contract");
            var resultInterpretation = resultContract == "verdict" ? "PassFail" : null;

            // Optional expected_result / row_count logic is mostly obsoleted by the `verdict` contract, 
            // but we'll provide dummy values if needed by executor derivations
            var expectedResultProse = string.Empty; 
            var rowCountCondition = (string?)null;

            int expected; DerivationInfo? expectedInfo;
            string? execType; DerivationInfo? execTypeInfo;
            if (isHostProbe)
            {
                // No SQL to derive from; the host-probe dispatch ignores ExpectedValue/ExecutionType.
                expected = 0; expectedInfo = null; execType = "scalar"; execTypeInfo = null;
            }
            else
            {
                (expected, expectedInfo) = Derivations.DeriveExpectedValue(checkId, expectedResultProse, sql);
                (execType, execTypeInfo) = Derivations.DeriveExecutionType(checkId, sql, resultInterpretation, rowCountCondition);
            }

            var effortHours = DoubleOrNull(yaml, "remediation_effort_hours");
            var effortBucket = EffortBucketFromHours(effortHours);

            var (scoreWeight, scoreInfo) = Derivations.DeriveScoreWeight(
                checkId,
                null, // No score_weight in V2
                Scalar(yaml, "severity"),
                effortBucket);

            // V2 corpus carries `bad: 1` in frontmatter (sourced from AllCheckTable Bad);
            // read it so IsBad reflects the curated flag instead of defaulting false.
            var (isBad, isBadInfo) = Derivations.DeriveIsBad(checkId, BoolOrNull(yaml, "bad"));

            var infos = new List<DerivationInfo>(5);
            if (expectedInfo is { } e1) infos.Add(e1);
            if (execTypeInfo is { } e2) infos.Add(e2);
            if (scoreInfo is { } e3) infos.Add(e3);
            if (isBadInfo is { } e4) infos.Add(e4);

            var check = new SqlCheck
            {
                Id = checkId,
                DisplayId = ScalarOrNull(yaml, "display_id") ?? string.Empty,
                Name = Scalar(yaml, "title"),
                // The intent is passed from the markdown parsing as a synthetic field into the mapping
                Description = Scalar(yaml, "description"),
                // '## Business Impact' section — synthetic field from the markdown parser (client voice).
                BusinessImpact = ScalarOrNull(yaml, "business_impact"),
                Eli5Description = ScalarOrNull(yaml, "eli5_description"),
                Category = Scalar(yaml, "category"),
                Severity = Scalar(yaml, "severity"),
                SqlQuery = sql ?? string.Empty,
                Method = method,
                ProbeKey = probeKey,
                ExpectedValue = expected,
                ExecutionType = execType,
                ScoreWeight = scoreWeight,
                EffortHours = effortHours ?? 0,
                IsBad = isBad,
                ResultInterpretation = resultInterpretation,
                RowCountCondition = rowCountCondition,
                Enabled = true,
                Source = ScalarOrNull(yaml, "source"), // Wait, V2 `source` is a map. Let's map its nested `ref`
                RecommendedAction = ScalarOrNull(yaml, "remediation"), // Synthetically passed from Markdown
                Eli5Remediation = ScalarOrNull(yaml, "eli5_remediation"),

                // Supported engine editions are nested in applicability
                SupportedEngineEditions = new List<int>()
            };

            // V2 mapping for `source` and `applicability`
            if (yaml.TryGetValue("source", out var srcNode) && srcNode is YamlMappingNode srcMap)
            {
                check.Source = ScalarOrNull(srcMap, "ref");
                check.LegacyIds = new List<string>();
                if (!string.IsNullOrWhiteSpace(check.Source))
                {
                    check.LegacyIds.Add(check.Source);
                    check.Sources.Add(check.Source);
                }
            }
            
            if (yaml.TryGetValue("applicability", out var appNode) && appNode is YamlMappingNode appMap)
            {
                check.SupportedEngineEditions = IntSequenceOrEmpty(appMap, "engine_editions");
            }

            return new BuildResult(check, infos);
        }

        // ───────── helpers ─────────

        private static string Scalar(IReadOnlyDictionary<string, YamlNode> map, string key)
        {
            if (map.TryGetValue(key, out var node) && node is YamlScalarNode s && s.Value is { } v)
                return v;
            return string.Empty;
        }

        private static string? ScalarOrNull(IReadOnlyDictionary<string, YamlNode> map, string key)
        {
            if (map.TryGetValue(key, out var node) && node is YamlScalarNode s) return s.Value;
            return null;
        }

        private static string? ScalarOrNull(YamlMappingNode map, string key)
        {
            foreach (var (k, v) in map.Children)
                if (k is YamlScalarNode ks && ks.Value == key && v is YamlScalarNode vs)
                    return vs.Value;
            return null;
        }

        private static int? IntOrNull(IReadOnlyDictionary<string, YamlNode> map, string key)
        {
            var s = ScalarOrNull(map, key);
            return int.TryParse(s, out var n) ? n : null;
        }

        private static double? DoubleOrNull(IReadOnlyDictionary<string, YamlNode> map, string key)
        {
            var s = ScalarOrNull(map, key);
            // corpus values are fractional hours (e.g. 0.5, 1, 2, 4, 8) — parse invariantly.
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        }

        // Bucket numeric remediation effort (hours) into the legacy low/medium/high
        // effort tier that DeriveScoreWeight's multiplier table expects. Returns null
        // when no hours are present so the caller can fall back to `effort_estimate`.
        // Thresholds chosen against the corpus distribution (0.5–8h): <2h trivial,
        // 2–<4h moderate, ≥4h substantial.
        private static string? EffortBucketFromHours(double? hours)
        {
            if (hours is not double h || h <= 0) return null;
            return h >= 4 ? "high" : h >= 2 ? "medium" : "low";
        }

        private static List<string> ScalarSequenceOrEmpty(IReadOnlyDictionary<string, YamlNode> map, string key)
        {
            var result = new List<string>();
            if (map.TryGetValue(key, out var node) && node is YamlSequenceNode seq)
            {
                foreach (var child in seq.Children)
                {
                    if (child is YamlScalarNode s && !string.IsNullOrEmpty(s.Value))
                        result.Add(s.Value);
                }
            }
            return result;
        }

        private static List<int> IntSequenceOrEmpty(IReadOnlyDictionary<string, YamlNode> map, string key)
        {
            var result = new List<int>();
            if (map.TryGetValue(key, out var node) && node is YamlSequenceNode seq)
            {
                foreach (var child in seq.Children)
                    if (child is YamlScalarNode s && int.TryParse(s.Value, out var n))
                        result.Add(n);
            }
            return result;
        }

        private static List<int> IntSequenceOrEmpty(YamlMappingNode map, string key)
        {
            var result = new List<int>();
            foreach (var (k, v) in map.Children)
            {
                if (k is YamlScalarNode ks && ks.Value == key && v is YamlSequenceNode seq)
                {
                    foreach (var child in seq.Children)
                        if (child is YamlScalarNode s && int.TryParse(s.Value, out var n))
                            result.Add(n);
                    break;
                }
            }
            return result;
        }

        private static bool? BoolOrNull(IReadOnlyDictionary<string, YamlNode> map, string key)
        {
            var s = ScalarOrNull(map, key);
            if (string.IsNullOrEmpty(s)) return null;
            // accept yaml booleans + numeric 0/1
            if (bool.TryParse(s, out var b)) return b;
            if (int.TryParse(s, out var n)) return n != 0;
            return s.Equals("yes", StringComparison.OrdinalIgnoreCase) ? true
                 : s.Equals("no", StringComparison.OrdinalIgnoreCase) ? false
                 : null;
        }
    }
}
