/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// Abstraction over the bytes the parser consumes. Two implementations
    /// are envisaged (per #27 v3): <see cref="CorpusFileReader"/> for
    /// dev-mode (reads from a directory) and a future BundleReader for
    /// production (reads from a decrypted <c>sqltriage.license</c>).
    /// Both feed the same <see cref="SourceCatalogueLoader"/>; same bytes
    /// in → byte-identical catalogue out (B3 invariant).
    /// </summary>
    public interface ISourceReader
    {
        /// <summary>Enumerates the full set of check identifiers available
        /// in this source. Identifier here is the SOURCE-side handle —
        /// typically a filename stem — used to fetch the YAML/SQL.</summary>
        IEnumerable<string> EnumerateCheckHandles();

        /// <summary>Read the raw YAML text for a check by its source handle.</summary>
        string ReadYaml(string handle);

        /// <summary>Read the raw .sql body alongside the YAML (or null/empty if absent).</summary>
        string? ReadSqlFallback(string handle);

        /// <summary>Path or label for error messages. Not load-bearing.</summary>
        string SourceLabel(string handle);
    }
}
