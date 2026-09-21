/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.RepresentationModel;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// #27 v3 — Slice 1: minimal YAML reader for the source corpus.
    /// Loads one .yaml file and exposes the top-level mapping as a
    /// case-sensitive dictionary. Schema-binding contract aware
    /// (rejects non-mapping roots; preserves scalar value typing via
    /// the YamlNode types). Pure I/O + parse — no merging, no
    /// derivation, no fail-fast invariants (those land in Slice 2+).
    ///
    /// Trade-off vs <c>YamlDotNet.Serialization.Deserializer</c>: we
    /// use the RepresentationModel so we can later attach line/column
    /// provenance to validation errors (the model preserves them).
    /// </summary>
    public static class YamlCheckParser
    {
        /// <summary>
        /// Parse the top-level mapping of a YAML file into a string-keyed
        /// dictionary of <see cref="YamlNode"/>. Caller resolves leaf
        /// values via the node type (scalar/sequence/mapping).
        /// </summary>
        /// <exception cref="SourceParseException">
        /// Thrown if the file does not parse, is empty, or its root is
        /// not a mapping (the schema requires a top-level dict).
        /// </exception>
        public static IReadOnlyDictionary<string, YamlNode> LoadMapping(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                throw new SourceParseException(path, "file does not exist");

            var stream = new YamlStream();
            try
            {
                using var reader = new StreamReader(path);
                stream.Load(reader);
            }
            catch (Exception ex)
            {
                throw new SourceParseException(path, $"yaml parse failed: {ex.Message}", ex);
            }

            if (stream.Documents.Count == 0)
                throw new SourceParseException(path, "empty yaml document");

            if (stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new SourceParseException(path, "root node is not a mapping (schema requires top-level dict)");

            var result = new Dictionary<string, YamlNode>(root.Children.Count, StringComparer.Ordinal);
            foreach (var (k, v) in root.Children)
            {
                if (k is YamlScalarNode key && key.Value is { } keyStr)
                    result[keyStr] = v;
            }
            return result;
        }
    }
}
