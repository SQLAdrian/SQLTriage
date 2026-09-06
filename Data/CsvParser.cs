/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SQLTriage.Data
{
    /// <summary>
    /// Shared CSV/DSV parsing utilities used by DiagnosticsRoadmap, VulnerabilityAssessment, and tests.
    /// </summary>
    public static class CsvParser
    {
        /// <summary>
        /// Detects the field delimiter from the header line.
        /// Tilde-delimited (sqlmagic) files are detected first; otherwise comma is assumed.
        /// </summary>
        public static char DetectDelimiter(string headerLine)
            => headerLine.Contains('~') ? '~' : ',';

        /// <summary>
        /// Parses a single line of a CSV or DSV file into a list of field values.
        /// Handles RFC 4180 quoting (comma-delimited) and plain tilde-delimited files.
        /// </summary>
        public static List<string> ParseLine(string line, char delimiter = ',')
        {
            var result = new List<string>();
            if (delimiter != ',')
            {
                // Non-comma files (e.g. tilde-delimited sqlmagic) are plain-split;
                // strip any stray quotes literally since SQL Server raw output has none.
                foreach (var part in line.Split(delimiter))
                    result.Add(part.Trim().Trim('"').Trim('\''));
                return result;
            }

            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    // Quoted field
                    i++;
                    var sb = new System.Text.StringBuilder();
                    while (i < line.Length)
                    {
                        if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                        }
                        else if (line[i] == '"') { i++; break; }
                        else sb.Append(line[i++]);
                    }
                    result.Add(sb.ToString());
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ',') i++;
                    result.Add(line[start..i]);
                }

                if (i < line.Length && line[i] == ',') i++;
            }

            // A trailing comma means one more empty field
            if (line.Length > 0 && line[^1] == ',')
                result.Add("");

            return result;
        }

        /// <summary>
        /// Streams RFC 4180 RECORDS - not lines - out of a reader, first record first.
        ///
        /// <para>WHY A SECOND READER EXISTS. <see cref="ParseLine"/> is given one line and cannot see
        /// past a newline, so a caller that reads line by line SPLITS any record holding a quoted
        /// newline into fragments and then reads columns out of the pieces. sp_Blitz writes fields
        /// that legally hold newlines: <c>Details</c> carries error-log text, and since 2026-08-24
        /// the app's own sp_Blitz CSV also carries the <c>QueryPlan</c> / <c>QueryPlanFiltered</c>
        /// columns the Export Pack descriptor requires (plan XML; measured at 66,099 characters on
        /// .\NEW2022, no newlines that day, but nothing stops one). The old line-by-line scan simply
        /// mis-read such a file, quietly.</para>
        ///
        /// <para>Non-comma files - the tilde-delimited sqlmagic export - are unquoted by construction,
        /// so one line is one record there and <see cref="ParseLine"/> still does the work.</para>
        ///
        /// <para>Blank lines yield nothing, which is what the line-based callers did with them. A
        /// leading byte-order mark is dropped. The reader is consumed lazily, so a caller may stop
        /// early without reading the rest of the file.</para>
        /// </summary>
        public static IEnumerable<List<string>> ParseRecords(TextReader reader, char delimiter = ',')
        {
            if (reader is null) yield break;

            if (delimiter != ',')
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    yield return ParseLine(line, delimiter);
                }
                yield break;
            }

            var field = new StringBuilder();
            var row = new List<string>();
            bool inQuotes = false;
            bool sawContent = false;
            bool atStart = true;
            int read;

            while ((read = reader.Read()) >= 0)
            {
                char ch = (char)read;
                if (atStart)
                {
                    atStart = false;
                    if (ch == '\uFEFF') continue;  // byte-order mark
                }

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (reader.Peek() == '"') { reader.Read(); field.Append('"'); continue; }
                        inQuotes = false;
                        continue;
                    }
                    field.Append(ch);
                    sawContent = true;
                    continue;
                }

                switch (ch)
                {
                    case '"':
                        inQuotes = true;
                        sawContent = true;
                        continue;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        sawContent = true;
                        continue;
                    case '\r':
                        continue;
                    case '\n':
                        if (!sawContent && row.Count == 0 && field.Length == 0) continue;
                        row.Add(field.ToString());
                        field.Clear();
                        yield return row;
                        row = new List<string>();
                        sawContent = false;
                        continue;
                    default:
                        field.Append(ch);
                        sawContent = true;
                        continue;
                }
            }

            if (sawContent || row.Count > 0 || field.Length > 0)
            {
                row.Add(field.ToString());
                yield return row;
            }
        }
    }
}
