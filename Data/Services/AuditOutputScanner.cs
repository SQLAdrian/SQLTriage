/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// One detected sp_Blitz CSV in the output folder.
    /// </summary>
    public sealed record AuditOutputFile(
        string FilePath,
        string ServerName,
        DateTime LastWriteUtc);

    /// <summary>
    /// Scans the app's <c>output/</c> folder for sp_Blitz CSV exports and reports
    /// the newest file per server. Shared source of truth for output-folder
    /// auto-detect — the Compliance Roadmap (DiagnosticsRoadmap) scans the same
    /// folder for maturity counts; Audit Assessment uses this to offer auto-import
    /// of sp_Blitz findings without a manual drag-drop.
    /// <para>
    /// sp_Blitz files are identified by filename (contains "blitz", not "triage" /
    /// "sqlmagic"), matching DiagnosticsRoadmap's heuristic. Only sp_Blitz-shaped
    /// files are returned — sp_triage/sqlmagic exports are intentionally excluded
    /// because the BlitzFinding parser is sp_Blitz-shaped.
    /// </para>
    /// </summary>
    public class AuditOutputScanner
    {
        private readonly string _outputDir;

        /// <summary>Production ctor: scans <c>{BaseDirectory}/output</c>.</summary>
        public AuditOutputScanner()
            : this(Path.Combine(AppContext.BaseDirectory, "output")) { }

        /// <summary>Test/explicit ctor: scans the given directory.</summary>
        public AuditOutputScanner(string outputDir)
        {
            _outputDir = outputDir;
        }

        /// <summary>The output directory this scanner reads.</summary>
        public string OutputDirectory => _outputDir;

        /// <summary>
        /// Returns the newest sp_Blitz CSV per server in the output folder, newest
        /// first. Empty when the folder is absent or holds no sp_Blitz files.
        /// Unreadable files are skipped (never throws on a single bad file).
        /// </summary>
        public IReadOnlyList<AuditOutputFile> ScanSpBlitzFiles()
        {
            if (!Directory.Exists(_outputDir))
                return Array.Empty<AuditOutputFile>();

            var blitzFiles = Directory
                .GetFiles(_outputDir, "*.csv", SearchOption.TopDirectoryOnly)
                .Where(IsSpBlitzFileName)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<AuditOutputFile>();

            foreach (var file in blitzFiles)
            {
                var server = TryReadServerName(file);
                if (string.IsNullOrWhiteSpace(server)) continue;
                // Newest file already first → keep only the first per server.
                if (!seen.Add(server)) continue;
                results.Add(new AuditOutputFile(file, server, File.GetLastWriteTimeUtc(file)));
            }

            return results;
        }

        /// <summary>sp_Blitz filename heuristic — mirrors DiagnosticsRoadmap.</summary>
        private static bool IsSpBlitzFileName(string path)
        {
            var n = Path.GetFileName(path);
            return n.IndexOf("blitz", StringComparison.OrdinalIgnoreCase) >= 0
                && n.IndexOf("triage", StringComparison.OrdinalIgnoreCase) < 0
                && n.IndexOf("sqlmagic", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>
        /// Reads the first data row's ServerName column. Returns null if the file
        /// is unreadable, empty, or has no ServerName column / value.
        /// </summary>
        private static string? TryReadServerName(string file)
        {
            try
            {
                using var reader = new StreamReader(file);
                var headerLine = reader.ReadLine();
                if (string.IsNullOrEmpty(headerLine)) return null;

                var delim = CsvParser.DetectDelimiter(headerLine);
                var headers = CsvParser.ParseLine(headerLine, delim)
                    .Select(h => h.Trim('\'', '"', ' '))
                    .ToList();

                var serverIdx = headers.FindIndex(h =>
                    h.Equals("ServerName", StringComparison.OrdinalIgnoreCase));
                if (serverIdx < 0) return null;

                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) continue;
                    var cols = CsvParser.ParseLine(line, delim);
                    if (cols.Count > serverIdx)
                    {
                        var v = cols[serverIdx].Trim('\'', '"', ' ');
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                }
                return null;
            }
            catch
            {
                return null; // skip unreadable files
            }
        }
    }
}
