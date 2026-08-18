/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SQLTriage.Data.Models;
using YamlDotNet.RepresentationModel;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// B2 Slice 4 — the orchestrator. Walks the <see cref="ISourceReader"/>,
    /// parses each YAML (and its .sql fallback), invokes the Slice-3
    /// builder, aggregates a <see cref="SourceCatalogue"/> (id → SqlCheck,
    /// FrameworkMappings side-index, derivations, integrity hash).
    ///
    /// Deterministic: same source bytes → byte-identical catalogue (the B3
    /// dev-vs-bundle invariant is grounded here — the integrity hash is
    /// over the canonical-serialised checks in source-handle order).
    /// </summary>
    public sealed class SourceCatalogueLoader : ICheckCatalogueLoader
    {
        public SourceCatalogue Load(ISourceReader source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            var checks = new Dictionary<string, SqlCheck>(StringComparer.Ordinal);
            var mappings = new Dictionary<string, IReadOnlyList<FrameworkMapping>>(StringComparer.Ordinal);
            var derivations = new List<DerivationInfo>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var skipped = new List<SkippedCheck>();

            using var sha = SHA256.Create();

            foreach (var handle in source.EnumerateCheckHandles())
            {
                var yamlPath = source.SourceLabel(handle);

                // Parse via the YamlCheckParser pipeline (Slice 1). We load
                // text first so the same bytes are hashable for the
                // byte-identity invariant.
                var yamlText = source.ReadYaml(handle);
                var sqlFallback = source.ReadSqlFallback(handle);

                // Integrity hash: feed the source-ordered (handle, sha256-of-bytes)
                // pair into the rolling hash for EVERY handle (including skipped
                // ones) so the byte-identity invariant stays deterministic
                // regardless of per-check parse outcome.
                AddToHash(sha, handle);
                AddToHash(sha, yamlText);
                if (sqlFallback != null) AddToHash(sha, sqlFallback);

                // Per-check resilience: a single malformed check (bad id, encoding,
                // missing field) is skipped + recorded, never aborts the whole
                // catalogue. This keeps the rest of the corpus loadable.
                try
                {
                    IReadOnlyDictionary<string, YamlNode> mapping;
                    try
                    {
                        mapping = ParseMappingFromText(yamlText, yamlPath);
                    }
                    catch (SourceParseException) { throw; }
                    catch (Exception ex)
                    {
                        throw new SourceParseException(yamlPath, "yaml parse failed", ex);
                    }

                    // .sql fallback may itself be a CHECK_METADATA-headered file —
                    // strip the header here so the SQL body is "pure" T-SQL.
                    string? sqlBody = sqlFallback;
                    if (!string.IsNullOrEmpty(sqlFallback))
                    {
                        var (_, body) = SqlHeaderParser.ParseText(sqlFallback);
                        if (!string.IsNullOrWhiteSpace(body)) sqlBody = body;
                    }

                    var result = SqlCheckBuilder.Build(mapping, yamlPath, sqlBody, seenIds);
                    checks[result.Check.Id] = result.Check;
                    derivations.AddRange(result.Derivations);

                    // FrameworkMappings side-index (Slice 4 lands the model bridge
                    // until SqlCheck grows the property natively)
                    if (mapping.TryGetValue("framework_mappings", out var fm) && fm is YamlSequenceNode seq)
                        mappings[result.Check.Id] = ExtractFrameworkMappings(seq);
                }
                catch (SourceParseException ex)
                {
                    skipped.Add(new SkippedCheck(handle, ex.Message));
                }
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var integrity = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

            return new SourceCatalogue(checks, mappings, derivations, integrity, skipped);
        }

        // ───────── helpers ─────────

        /// <summary>
        /// THE V2 document parser: splits Markdown frontmatter from body and
        /// injects the synthetic fields the builder consumes (description from
        /// `## Intent`, query_sql from `## Query`, remediation from
        /// `## Remediation`). Public so tests exercise the exact production
        /// parse path (plain YAML without frontmatter also passes through).
        /// </summary>
        public static IReadOnlyDictionary<string, YamlNode> ParseMappingFromText(string text, string label)
        {
            var yamlText = text;
            var markdownBody = string.Empty;

            // Extract frontmatter block if present (starts with ---, ends with ---)
            if (text.StartsWith("---\n") || text.StartsWith("---\r\n"))
            {
                var endIndex = text.IndexOf("\n---", 3);
                if (endIndex > 0)
                {
                    yamlText = text.Substring(0, endIndex);
                    var bodyStartIndex = endIndex + 4;
                    if (bodyStartIndex < text.Length)
                    {
                        markdownBody = text.Substring(bodyStartIndex);
                    }
                }
            }

            var stream = new YamlStream();
            using var reader = new StringReader(yamlText);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
                throw new SourceParseException(label, "empty yaml document");
            if (stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new SourceParseException(label, "root node is not a mapping");

            var map = new Dictionary<string, YamlNode>(root.Children.Count, StringComparer.Ordinal);
            foreach (var (k, v) in root.Children)
            {
                if (k is YamlScalarNode key && key.Value is { } s) map[s] = v;
            }

            // If we have a Markdown body, extract sections into synthetic YAML nodes
            if (!string.IsNullOrWhiteSpace(markdownBody))
            {
                var intentMatch = System.Text.RegularExpressions.Regex.Match(markdownBody, @"^## Intent\s*(.+?)(?=^## |\z)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);
                if (intentMatch.Success)
                    map["description"] = new YamlScalarNode(intentMatch.Groups[1].Value.Trim());

                var queryMatch = System.Text.RegularExpressions.Regex.Match(markdownBody, @"^## Query\s*```sql\s*(.+?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);
                if (queryMatch.Success)
                    map["query_sql"] = new YamlScalarNode(queryMatch.Groups[1].Value.Trim());

                var remediationMatch = System.Text.RegularExpressions.Regex.Match(markdownBody, @"^## Remediation\s*(.+?)(?=^## |\z)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);
                if (remediationMatch.Success)
                    map["remediation"] = new YamlScalarNode(remediationMatch.Groups[1].Value.Trim());

                // '## Business Impact' = the client-facing voice (CIO/management), kept distinct
                // from '## Intent' (description) so reports never surface Intent's oracle-derivation
                // notes to a business audience.
                var businessImpactMatch = System.Text.RegularExpressions.Regex.Match(markdownBody, @"^## Business Impact\s*(.+?)(?=^## |\z)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);
                if (businessImpactMatch.Success)
                    map["business_impact"] = new YamlScalarNode(businessImpactMatch.Groups[1].Value.Trim());

                // '## ELI5 Explanation' and '## ELI5 Remediation' for S-Tier non-technical stakeholders
                var eli5DescMatch = System.Text.RegularExpressions.Regex.Match(markdownBody, @"^## ELI5 Explanation\s*(.+?)(?=^## |\z)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);
                if (eli5DescMatch.Success)
                    map["eli5_description"] = new YamlScalarNode(eli5DescMatch.Groups[1].Value.Trim());

                var eli5RemMatch = System.Text.RegularExpressions.Regex.Match(markdownBody, @"^## ELI5 Remediation\s*(.+?)(?=^## |\z)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);
                if (eli5RemMatch.Success)
                    map["eli5_remediation"] = new YamlScalarNode(eli5RemMatch.Groups[1].Value.Trim());
            }

            return map;
        }

        private static IReadOnlyList<FrameworkMapping> ExtractFrameworkMappings(YamlSequenceNode seq)
        {
            var list = new List<FrameworkMapping>(seq.Children.Count);
            foreach (var item in seq.Children.OfType<YamlMappingNode>())
            {
                string? framework = null, controlId = null, controlName = null, mappingType = null;
                var extras = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in item.Children)
                {
                    if (k is not YamlScalarNode ks || ks.Value is null) continue;
                    var val = (v as YamlScalarNode)?.Value;
                    switch (ks.Value)
                    {
                        case "framework": framework = val; break;
                        case "control_id": controlId = val; break;
                        case "control_name": controlName = val; break;
                        case "mapping_type": mappingType = val; break;
                        default: if (val != null) extras[ks.Value] = val; break;
                    }
                }
                list.Add(new FrameworkMapping(
                    framework ?? string.Empty,
                    controlId ?? string.Empty,
                    controlName, mappingType, extras));
            }
            return list;
        }

        private static void AddToHash(HashAlgorithm sha, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
    }

    /// <summary>
    /// Public contract per B2 plan. Implemented by
    /// <see cref="SourceCatalogueLoader"/>; future BundleLoader will
    /// implement the same interface.
    /// </summary>
    public interface ICheckCatalogueLoader
    {
        SourceCatalogue Load(ISourceReader source);
    }
}
