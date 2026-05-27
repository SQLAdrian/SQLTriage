/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// B1 §4 — the 7 fail-fast invariants the parser MUST reject on.
    /// Each method throws <see cref="SourceParseException"/> on violation;
    /// returns silently on success. No silent defaulting of required fields.
    ///
    /// (Invariant #1 — YAML parse failure — is enforced by
    /// <see cref="YamlCheckParser.LoadMapping"/> itself.)
    /// </summary>
    public static class ParseInvariants
    {
        /// <summary>
        /// 8-NS extended regex per Adrian sign-off 2026-05-20 (DSSTIG
        /// additive). NS enum may grow additively without schema bump;
        /// never remove/rename existing namespaces.
        /// </summary>
        public static readonly Regex CheckIdV2 = new(
            @"^SQLT-(BLITZ|BPCHK|GAPFIL|CORE|TRIAGE|DSCIS|SQLWCH|DSSTIG)-[0-9]{5}$",
            RegexOptions.Compiled);

        /// <summary>The 7 strictly-required fields per agents.md schema binding (corrected 2026-05-20).</summary>
        public static readonly string[] RequiredFields =
        {
            "check_id", "title", "description", "category", "priority",
            "framework_mappings", "query_analysis"
        };

        /// <summary>#2 — every strictly-required field present (and non-empty).</summary>
        public static void RequireFields(string path, IReadOnlyDictionary<string, YamlNode> map)
        {
            foreach (var field in RequiredFields)
            {
                if (!map.TryGetValue(field, out var node))
                    throw new SourceParseException(path, $"missing required field '{field}'");
                if (node is YamlScalarNode s && string.IsNullOrWhiteSpace(s.Value))
                    throw new SourceParseException(path, $"required field '{field}' is empty");
            }
        }

        /// <summary>#3 — check_id matches v2 regex (string, 8-NS enum, 5-digit).</summary>
        public static void RequireV2CheckId(string path, string checkId)
        {
            if (string.IsNullOrEmpty(checkId))
                throw new SourceParseException(path, "check_id is empty");
            if (!CheckIdV2.IsMatch(checkId))
                throw new SourceParseException(path, checkId,
                    $"check_id '{checkId}' does not match v2 regex {CheckIdV2}");
        }

        /// <summary>#4 — schema_version == 2 (the only accepted version post-G1).</summary>
        public static void RequireSchemaV2(string path, IReadOnlyDictionary<string, YamlNode> map)
        {
            if (!map.TryGetValue("schema_version", out var node))
                throw new SourceParseException(path, "missing required field 'schema_version'");
            if (node is not YamlScalarNode s || !int.TryParse(s.Value, out var v))
                throw new SourceParseException(path, $"schema_version not an integer: '{(node as YamlScalarNode)?.Value}'");
            if (v != 2)
                throw new SourceParseException(path, $"schema_version {v} not accepted (only 2)");
        }

        /// <summary>#5 — no check_id collisions across a loaded set. Caller passes accumulated ids.</summary>
        public static void RejectCollision(string path, string checkId, ISet<string> seen)
        {
            if (!seen.Add(checkId))
                throw new SourceParseException(path, checkId,
                    "check_id collision — already seen in another file");
        }

        /// <summary>
        /// #6 — query_analysis.enhanced_query missing AND no .sql fallback
        /// body. Called after both source-paths have been tried.
        /// </summary>
        public static void RequireSqlSource(string path, string checkId, string? yamlSql, string? sqlBody)
        {
            if (string.IsNullOrWhiteSpace(yamlSql) && string.IsNullOrWhiteSpace(sqlBody))
                throw new SourceParseException(path, checkId,
                    "no SQL: query_analysis.enhanced_query absent and no .sql fallback body");
        }

        /// <summary>
        /// #7 — type drift on a required field. Caller passes expected
        /// YamlNode type. (Lightweight runtime check; richer typing in
        /// later slices via the canonical builder.)
        /// </summary>
        public static void RequireType(string path, string checkId, string field, YamlNode node, Type expected)
        {
            if (!expected.IsInstanceOfType(node))
                throw new SourceParseException(path, checkId,
                    $"field '{field}' has wrong type: expected {expected.Name}, got {node.GetType().Name}");
        }
    }
}
