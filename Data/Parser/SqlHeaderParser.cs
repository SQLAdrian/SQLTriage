/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// Parses the <c>/* CHECK_METADATA … */</c> header block at the top of
    /// a check .sql file (the SQL fallback per B1 §1, used when the YAML
    /// is missing or query_analysis.enhanced_query is empty).
    ///
    /// Header shape (corpus convention):
    /// <code>
    /// /*
    /// CHECK_METADATA
    /// check_id: WAIT_001
    /// title: Top Wait Stats...
    /// category: Performance
    /// ...
    /// */
    /// &lt;T-SQL body&gt;
    /// </code>
    ///
    /// Returns (metadata dict, body) — both are empty/string.Empty if the
    /// file has no header (legacy/raw .sql allowed).
    /// </summary>
    public static class SqlHeaderParser
    {
        // Line-anchored: only top-level lines that look like 'key: value'
        // (alphanumeric key, no leading whitespace). Stops at the first
        // non-matching line — the SQL body begins below.
        private static readonly Regex MetaLine =
            new(@"^([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.+?)\s*$", RegexOptions.Compiled);

        public static (IReadOnlyDictionary<string, string> meta, string body) Parse(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new SourceParseException(path, "file does not exist");

            var text = File.ReadAllText(path);
            return ParseText(text);
        }

        /// <summary>Text-level overload — exposed for unit tests + bundle path.</summary>
        public static (IReadOnlyDictionary<string, string> meta, string body) ParseText(string text)
        {
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return (meta, string.Empty);

            // Look for an opening /* …  */ comment near the start. The
            // basmalah /* In the name of God … */ may precede; tolerate it
            // by scanning ALL leading /* */ blocks for a CHECK_METADATA tag.
            int cursor = 0;
            while (cursor < text.Length)
            {
                int open = text.IndexOf("/*", cursor, StringComparison.Ordinal);
                if (open < 0) break;
                // require the comment to be at the file head or only
                // whitespace/prior comments before it
                if (!IsOnlyWhitespaceOrComments(text, 0, open)) break;
                int close = text.IndexOf("*/", open + 2, StringComparison.Ordinal);
                if (close < 0) break;
                var block = text.Substring(open + 2, close - open - 2);
                if (block.IndexOf("CHECK_METADATA", StringComparison.Ordinal) >= 0)
                {
                    foreach (var raw in block.Split('\n'))
                    {
                        var line = raw.TrimEnd('\r').Trim();
                        if (line.Length == 0 || line.Equals("CHECK_METADATA", StringComparison.Ordinal)) continue;
                        var m = MetaLine.Match(line);
                        if (m.Success) meta[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                    var body = text.Substring(close + 2).TrimStart('\r', '\n', ' ', '\t');
                    return (meta, body);
                }
                cursor = close + 2;
            }
            // No CHECK_METADATA block found — return the full text as body
            return (meta, text);
        }

        private static bool IsOnlyWhitespaceOrComments(string s, int start, int end)
        {
            int i = start;
            while (i < end)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }
                // skip a /* … */ block (e.g. the basmalah)
                if (i + 1 < end && s[i] == '/' && s[i + 1] == '*')
                {
                    int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0 || close >= end) return false;
                    i = close + 2;
                    continue;
                }
                return false;
            }
            return true;
        }
    }
}
