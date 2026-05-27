/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// Parses an sp_BLITZ CSV export (UTF-8, BOM-tolerant) into a list of
/// <see cref="BlitzFinding"/> records.
/// <para>
/// Required CSV columns: <c>Priority</c>, <c>FindingsGroup</c>, <c>Finding</c>,
/// <c>Details</c>. Optional: <c>DatabaseName</c>, <c>URL</c>. The columns
/// <c>QueryPlan</c> and <c>QueryPlanFiltered</c> are ignored when present.
/// </para>
/// </summary>
public static class SpBlitzCsvParser
{
    private static readonly string[] RequiredColumns =
        { "Priority", "FindingsGroup", "Finding", "Details" };

    /// <summary>
    /// Parses an sp_BLITZ CSV stream.
    /// </summary>
    /// <param name="csvStream">Readable stream of the CSV file (UTF-8 with or without BOM).</param>
    /// <param name="serverLabel">Server label to stamp on every parsed finding.</param>
    /// <param name="importedUtc">Import timestamp to stamp on every parsed finding.</param>
    /// <param name="importId">Batch ID that groups all rows in this upload.</param>
    /// <param name="skipped">Number of data rows skipped due to parse failures (non-fatal).</param>
    /// <returns>Parsed findings in input order.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the CSV is missing one or more required columns.
    /// </exception>
    public static IReadOnlyList<BlitzFinding> Parse(
        Stream csvStream,
        string serverLabel,
        DateTime importedUtc,
        Guid importId,
        out int skipped)
    {
        if (csvStream is null) throw new ArgumentNullException(nameof(csvStream));
        if (string.IsNullOrWhiteSpace(serverLabel)) throw new ArgumentException("serverLabel must not be empty.", nameof(serverLabel));

        using var reader = new StreamReader(csvStream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        // Read header line
        var headerRaw = reader.ReadLine();
        if (string.IsNullOrEmpty(headerRaw))
            throw new InvalidDataException("sp_BLITZ CSV is empty — no header row found.");

        var headers = ParseLine(headerRaw);

        // Build column index map (case-insensitive)
        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
            colIndex[headers[i].Trim()] = i;

        // Validate required columns
        foreach (var required in RequiredColumns)
        {
            if (!colIndex.ContainsKey(required))
                throw new InvalidDataException(
                    $"sp_BLITZ CSV missing required column: {required}. " +
                    "Expected at minimum Priority, FindingsGroup, Finding, Details.");
        }

        int idxPriority = colIndex["Priority"];
        int idxFindingsGroup = colIndex["FindingsGroup"];
        int idxFinding = colIndex["Finding"];
        int idxDetails = colIndex["Details"];
        int idxDatabase = colIndex.TryGetValue("DatabaseName", out var di) ? di : -1;
        int idxUrl = colIndex.TryGetValue("URL", out var ui) ? ui : -1;

        var findings = new List<BlitzFinding>();
        int skipCount = 0;
        string? line;
        int lineNumber = 1; // header was line 0

        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;

            // Skip blank rows
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseLine(line);
            if (fields.Count == 0)
                continue;

            // Guard against short rows
            int maxRequired = Math.Max(Math.Max(idxPriority, idxFindingsGroup), Math.Max(idxFinding, idxDetails));
            if (fields.Count <= maxRequired)
            {
                Console.Error.WriteLine($"[SpBlitzCsvParser] Line {lineNumber}: too few columns ({fields.Count}), skipping.");
                skipCount++;
                continue;
            }

            // Priority must be a valid integer 1..255
            if (!int.TryParse(fields[idxPriority].Trim(), out int priority) || priority < 1 || priority > 255)
            {
                Console.Error.WriteLine($"[SpBlitzCsvParser] Line {lineNumber}: invalid Priority '{fields[idxPriority]}', skipping.");
                skipCount++;
                continue;
            }

            string? databaseName = (idxDatabase >= 0 && idxDatabase < fields.Count)
                ? NullIfEmpty(fields[idxDatabase])
                : null;

            string? url = (idxUrl >= 0 && idxUrl < fields.Count)
                ? NullIfEmpty(fields[idxUrl])
                : null;

            findings.Add(new BlitzFinding
            {
                Priority = priority,
                FindingsGroup = fields[idxFindingsGroup].Trim(),
                Finding = fields[idxFinding].Trim(),
                DatabaseName = databaseName,
                Details = fields[idxDetails].Trim(),
                Url = url,
                ServerLabel = serverLabel,
                ImportedUtc = importedUtc,
                ImportId = importId
            });
        }

        skipped = skipCount;
        return findings.AsReadOnly();
    }

    // ── Internal CSV parser ───────────────────────────────────────────────────
    // Handles RFC 4180 quoting: fields may be quoted, double-quote escapes a literal
    // quote inside a quoted field, and line-endings are \r\n or \n.

    internal static List<string> ParseLine(string line)
    {
        var result = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length)
            {
                // Trailing comma: add empty field
                if (result.Count > 0 && line.Length > 0 && line[^1] == ',')
                    result.Add(string.Empty);
                break;
            }

            if (line[i] == '"')
            {
                // Quoted field
                i++;
                var sb = new StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            // Escaped quote inside quoted field
                            sb.Append('"');
                            i += 2;
                        }
                        else
                        {
                            // End of quoted field
                            i++;
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(line[i++]);
                    }
                }
                result.Add(sb.ToString());
                // Skip delimiter
                if (i < line.Length && line[i] == ',')
                    i++;
            }
            else
            {
                // Unquoted field — read to next comma or end
                int start = i;
                while (i < line.Length && line[i] != ',')
                    i++;
                result.Add(line[start..i]);
                // Skip delimiter
                if (i < line.Length && line[i] == ',')
                    i++;
            }
        }

        return result;
    }

    private static string? NullIfEmpty(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
