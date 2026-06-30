/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// In-memory <see cref="ISourceReader"/> — the stub that the
    /// production BundleReader (Phase B post-B4) will become once
    /// <c>sqltriage.license</c> decryption lands. Today: tests construct
    /// it directly from a handle → (yaml, sql) map, proving the
    /// dev-vs-bundle byte-identity invariant (#27 v3 B3).
    ///
    /// Same source bytes (whether read from disk via
    /// <see cref="CorpusFileReader"/> or supplied in-memory here) MUST
    /// produce a byte-identical <see cref="SourceCatalogue"/>.
    /// </summary>
    public sealed class BundleReader : ISourceReader
    {
        private readonly IReadOnlyDictionary<string, (string Yaml, string? Sql)> _entries;

        public BundleReader(IReadOnlyDictionary<string, (string Yaml, string? Sql)> entries)
        {
            _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        }

        public IEnumerable<string> EnumerateCheckHandles() =>
            _entries.Keys.OrderBy(k => k, StringComparer.Ordinal); // same ordering as CorpusFileReader

        public string ReadYaml(string handle) => _entries[handle].Yaml;

        public string? ReadSqlFallback(string handle) => _entries[handle].Sql;

        public string SourceLabel(string handle) => $"bundle://{handle}.yaml";
    }
}
