/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

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
    }
}
