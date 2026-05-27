/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// Dev-mode source: enumerates *.yaml files in a directory; for each,
    /// reads its content and the same-stem .sql fallback (if present).
    /// Per #27 v3 §3 — the gated dev-mode reads the corpus
    /// <c>dist/</c> directly; this same reader can point at any folder
    /// of source files (today: the corpus repo root).
    /// </summary>
    public sealed class CorpusFileReader : ISourceReader
    {
        private readonly string _dir;

        public CorpusFileReader(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentNullException(nameof(directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"corpus directory not found: {directory}");
            _dir = directory;
        }

        public IEnumerable<string> EnumerateCheckHandles()
        {
            // sorted = deterministic + byte-identity invariant
            return Directory.EnumerateFiles(_dir, "*.yaml", SearchOption.TopDirectoryOnly)
                            .Select(p => Path.GetFileNameWithoutExtension(p) ?? string.Empty)
                            .Where(s => s.Length > 0)
                            .OrderBy(s => s, StringComparer.Ordinal);
        }

        public string ReadYaml(string handle) =>
            File.ReadAllText(Path.Combine(_dir, handle + ".yaml"));

        public string? ReadSqlFallback(string handle)
        {
            var sqlPath = Path.Combine(_dir, handle + ".sql");
            return File.Exists(sqlPath) ? File.ReadAllText(sqlPath) : null;
        }

        public string SourceLabel(string handle) => Path.Combine(_dir, handle + ".yaml");
    }
}
