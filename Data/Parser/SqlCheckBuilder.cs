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

            var checkId = Scalar(yaml, "check_id");
            ParseInvariants.RequireV2CheckId(yamlPath, checkId);
            ParseInvariants.RejectCollision(yamlPath, checkId, seenIds);

            // type drift checks on the load-bearing structured fields
            ParseInvariants.RequireType(yamlPath, checkId, "framework_mappings",
                yaml["framework_mappings"], typeof(YamlSequenceNode));
            ParseInvariants.RequireType(yamlPath, checkId, "query_analysis",
                yaml["query_analysis"], typeof(YamlMappingNode));

            // SQL: prefer yaml.query_analysis.enhanced_query; fall back to .sql body
            var qaMap = (YamlMappingNode)yaml["query_analysis"];
            var yamlSql = ScalarOrNull(qaMap, "enhanced_query");
            ParseInvariants.RequireSqlSource(yamlPath, checkId, yamlSql, sqlFallbackBody);
            var sql = !string.IsNullOrWhiteSpace(yamlSql) ? yamlSql! : sqlFallbackBody!;

            var yamlResultInterpretation = ScalarOrNull(yaml, "result_interpretation");
            var rowCountCondition = ScalarOrNull(yaml, "row_count_condition");
            var expectedResultProse = Scalar(yaml, "description"); // expected_result is optional; use description as a softer hint when absent
            if (yaml.TryGetValue("expected_result", out var er) && er is YamlScalarNode ers)
                expectedResultProse = ers.Value ?? expectedResultProse;

            // ResultInterpretation: YAML wins; else derive from text-verdict tokens in SQL.
            // (Fixes 2026-05-21 audit-run bug: text-result checks were routed through the
            // numeric executor path → "input string 'PASS' was not in a correct format".)
            string? resultInterpretation = yamlResultInterpretation;
            DerivationInfo? riInfo = null;
            if (string.IsNullOrEmpty(resultInterpretation))
            {
                (resultInterpretation, riInfo) = Derivations.DeriveResultInterpretation(checkId, sql);
            }

            var (expected, expectedInfo) = Derivations.DeriveExpectedValue(checkId, expectedResultProse, sql);
            var (execType, execTypeInfo) = Derivations.DeriveExecutionType(checkId, sql, resultInterpretation, rowCountCondition);
            var (scoreWeight, scoreInfo) = Derivations.DeriveScoreWeight(
                checkId,
                IntOrNull(yaml, "score_weight"),
                Scalar(yaml, "priority"),
                ScalarOrNull(yaml, "effort_estimate"));
            var (isBad, isBadInfo) = Derivations.DeriveIsBad(checkId, BoolOrNull(yaml, "bad"));

            var infos = new List<DerivationInfo>(5);
            if (riInfo is { } e0) infos.Add(e0);
            if (expectedInfo is { } e1) infos.Add(e1);
            if (execTypeInfo is { } e2) infos.Add(e2);
            if (scoreInfo is { } e3) infos.Add(e3);
            if (isBadInfo is { } e4) infos.Add(e4);

            var check = new SqlCheck
            {
                Id = checkId,
                Name = Scalar(yaml, "title"),
                Description = Scalar(yaml, "description"),
                Category = Scalar(yaml, "category"),
                Severity = Scalar(yaml, "priority"),
                SqlQuery = sql,
                ExpectedValue = expected,
                ExecutionType = execType,
                ScoreWeight = scoreWeight,
                IsBad = isBad,
                ResultInterpretation = resultInterpretation,
                RowCountCondition = rowCountCondition,
                Enabled = true,
                Source = ScalarOrNull(yaml, "source"),
                RecommendedAction = ScalarOrNull(yaml, "recommended_action"),
            };

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
