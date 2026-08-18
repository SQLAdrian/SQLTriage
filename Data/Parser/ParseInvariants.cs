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
            @"^SQLT-[A-Z0-9\-]+$",
            RegexOptions.Compiled);

        /// <summary>The strictly-required fields per V2 schema binding.</summary>
        public static readonly string[] RequiredFields =
        {
            "id", "title", "category", "severity", "source",
            "applicability", "result_contract", "framework_mappings", "provenance"
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

        /// <summary>#3 — id matches v2 regex (string, 8-NS enum, 5-digit).</summary>
        public static void RequireV2CheckId(string path, string checkId)
        {
            if (string.IsNullOrEmpty(checkId))
                throw new SourceParseException(path, "id is empty");
            if (!CheckIdV2.IsMatch(checkId))
                throw new SourceParseException(path, checkId,
                    $"id '{checkId}' does not match v2 regex {CheckIdV2}");
        }

        /// <summary>#4 — schema_version validation removed for V2 Markdown frontmatter.</summary>
        public static void RequireSchemaV2(string path, IReadOnlyDictionary<string, YamlNode> map)
        {
            // V2 Markdown schema implicitly denotes version via format, so no explicit schema_version check is needed.
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
