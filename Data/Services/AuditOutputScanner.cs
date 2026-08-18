/* In the name of God, the Merciful, the Compassionate */
/*
 * AuditOutputScanner — single source of truth for discovering + parsing the sp_triage / sp_Blitz
 * CSV files the app writes to <BaseDirectory>/output. Lifted out of DiagnosticsRoadmap.razor (the
 * scan + per-file fired-CheckID parse) so the sp_Blitz dashboard and the Compliance Roadmap share
 * ONE parse path (no second, drifting copy).
 *
 * Key fact about sp_Blitz: a check emits a CSV ROW only when it FIRES (a problem). So a parsed file
 * yields the FIRED set: BlitzCheckID -> fire count. The dashboard derives health from "which of the
 * known sp_Blitz checks did NOT fire", so the fired set is the raw signal; the universe + weights
 * come from the corpus (see BlitzDashboardService).
 *
 * Column conventions (verified against real output):
 *   sp_Blitz CSV : instance = "ServerName"; fired id = "CheckID" col; date ~ col index 4.
 *   sp_triage CSV: instance = "SQLInstance" or "Server"; fired id = "SectionID" col, but only for
 *                  rows whose "Section" starts "sp_Blitz:" (new sqlmagic format: SectionID>0, no prefix).
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

/// <summary>Which upstream tool produced an output CSV.</summary>
public enum AuditFileType { SpTriage, SpBlitz }

/// <summary>
/// A discovered audit file for one SQL instance. <see cref="FiredCheckCounts"/> is populated lazily
/// by <see cref="AuditOutputScanner.LoadFiredChecksAsync"/> (empty until then).
/// </summary>
public sealed class AuditedFile
{
    public string SqlInstance { get; init; } = "";
    public string Domain { get; init; } = "";
    public DateTime AuditDate { get; init; }
    public string FilePath { get; init; } = "";
    public AuditFileType FileType { get; init; } = AuditFileType.SpTriage;

    /// <summary>Fired BlitzCheckID -> number of rows (databases/objects) that fired it.</summary>
    public Dictionary<int, int> FiredCheckCounts { get; set; } = new();
}

public interface IAuditOutputScanner
{
    /// <summary>The directory scanned (<c>&lt;BaseDirectory&gt;/output</c> unless overridden).</summary>
    string OutputDirectory { get; }

    /// <summary>
    /// Discovers the newest CSV per (instance, file-type). Does NOT parse fired rows yet
    /// (call <see cref="LoadFiredChecksAsync"/> for that). Safe to call on a background thread.
    /// </summary>
    Task<IReadOnlyList<AuditedFile>> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Parses one file's fired BlitzCheckIDs into <see cref="AuditedFile.FiredCheckCounts"/> (idempotent).
    /// </summary>
    Task LoadFiredChecksAsync(AuditedFile file, CancellationToken ct = default);
}

public sealed class AuditOutputScanner : IAuditOutputScanner
{
    private readonly ILogger<AuditOutputScanner> _logger;

    public AuditOutputScanner(ILogger<AuditOutputScanner> logger, string? outputDirOverride = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        OutputDirectory = outputDirOverride
            ?? Path.Combine(AppContext.BaseDirectory, "output");
    }

    public string OutputDirectory { get; }

    private static string StripQuotes(string s) => s.Trim('\'', '"', ' ');

    public Task<IReadOnlyList<AuditedFile>> ScanAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<AuditedFile>>(() => Scan(ct), ct);

    private IReadOnlyList<AuditedFile> Scan(CancellationToken ct)
    {
        if (!Directory.Exists(OutputDirectory))
        {
            _logger.LogInformation("[AuditOutputScanner] No output dir at {Dir}.", OutputDirectory);
            return Array.Empty<AuditedFile>();
        }

        var allFiles = Directory.GetFiles(OutputDirectory, "*.csv", SearchOption.TopDirectoryOnly);

        // sp_triage / sqlmagic vs sp_Blitz, by filename (same rule as DiagnosticsRoadmap).
        var triageFiles = allFiles
            .Where(f => { var n = Path.GetFileName(f);
                return n.IndexOf("sp_triage", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("sqlmagic",  StringComparison.OrdinalIgnoreCase) >= 0; })
            .OrderByDescending(File.GetLastWriteTime).ToList();
        var blitzFiles = allFiles
            .Where(f => { var n = Path.GetFileName(f);
                return n.IndexOf("blitz",    StringComparison.OrdinalIgnoreCase) >= 0
                    && n.IndexOf("triage",   StringComparison.OrdinalIgnoreCase) < 0
                    && n.IndexOf("sqlmagic",  StringComparison.OrdinalIgnoreCase) < 0; })
            .OrderByDescending(File.GetLastWriteTime).ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AuditedFile>();

        void ScanGroup(IEnumerable<string> files, AuditFileType fileType)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var reader = new StreamReader(file);
                    var headerLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(headerLine)) continue;

                    var delim   = CsvParser.DetectDelimiter(headerLine);
                    var headers = CsvParser.ParseLine(headerLine, delim).Select(StripQuotes).ToList();

                    int instanceIdx = fileType == AuditFileType.SpTriage
                        ? headers.FindIndex(h => h.Equals("SQLInstance", StringComparison.OrdinalIgnoreCase)
                                              || h.Equals("Server",      StringComparison.OrdinalIgnoreCase))
                        : headers.FindIndex(h => h.Equals("ServerName",  StringComparison.OrdinalIgnoreCase));
                    int dateIdx = fileType == AuditFileType.SpTriage
                        ? headers.FindIndex(h => h.Equals("evaldate", StringComparison.OrdinalIgnoreCase))
                        : 4; // sp_Blitz col 4 is the date (header blank)
                    int domainIdx = headers.FindIndex(h => h.Equals("Domain", StringComparison.OrdinalIgnoreCase));
                    if (instanceIdx < 0) continue;

                    string? sqlInstance = null;
                    string domain = "";
                    DateTime auditDate = File.GetLastWriteTime(file);

                    while (!reader.EndOfStream && sqlInstance == null)
                    {
                        var line = reader.ReadLine();
                        if (string.IsNullOrEmpty(line)) continue;
                        var cols = CsvParser.ParseLine(line, delim);
                        if (cols.Count > instanceIdx)
                        {
                            sqlInstance = StripQuotes(cols[instanceIdx]);
                            if (dateIdx >= 0 && cols.Count > dateIdx &&
                                DateTime.TryParse(StripQuotes(cols[dateIdx]), out var dt))
                                auditDate = dt;
                            if (domainIdx >= 0 && cols.Count > domainIdx)
                                domain = StripQuotes(cols[domainIdx]);
                        }
                    }

                    if (string.IsNullOrEmpty(sqlInstance)) continue;
                    var key = $"{sqlInstance}|{fileType}"; // newest-per-(instance,type) wins (files are date-desc)
                    if (!seen.Add(key)) continue;

                    result.Add(new AuditedFile
                    {
                        SqlInstance = sqlInstance,
                        Domain = domain,
                        AuditDate = auditDate,
                        FilePath = file,
                        FileType = fileType,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AuditOutputScanner] Skipping unreadable file {File}", file);
                }
            }
        }

        ScanGroup(triageFiles, AuditFileType.SpTriage);
        ScanGroup(blitzFiles,  AuditFileType.SpBlitz);

        return result.OrderBy(s => s.SqlInstance).ThenBy(s => s.FileType).ToList();
    }

    public Task LoadFiredChecksAsync(AuditedFile file, CancellationToken ct = default)
        => Task.Run(() => LoadFiredChecks(file, ct), ct);

    private void LoadFiredChecks(AuditedFile file, CancellationToken ct)
    {
        var counts = new Dictionary<int, int>();
        try
        {
            using var reader = new StreamReader(file.FilePath);
            var headerLine = reader.ReadLine();
            if (string.IsNullOrEmpty(headerLine)) { file.FiredCheckCounts = counts; return; }

            var delim   = CsvParser.DetectDelimiter(headerLine);
            var headers = CsvParser.ParseLine(headerLine, delim).Select(StripQuotes).ToList();

            int instanceIdx = file.FileType == AuditFileType.SpTriage
                ? headers.FindIndex(h => h.Equals("SQLInstance", StringComparison.OrdinalIgnoreCase)
                                      || h.Equals("Server",      StringComparison.OrdinalIgnoreCase))
                : headers.FindIndex(h => h.Equals("ServerName",  StringComparison.OrdinalIgnoreCase));

            // sp_Blitz: "CheckID" = BlitzCheckID directly.
            // sp_triage: "SectionID" = BlitzCheckID, only for rows where Section starts "sp_Blitz:"
            //            (new sqlmagic: SectionID>0 directly, no Section prefix).
            int checkIdx = file.FileType == AuditFileType.SpBlitz
                ? headers.FindIndex(h => h.Equals("CheckID",   StringComparison.OrdinalIgnoreCase))
                : headers.FindIndex(h => h.Equals("SectionID", StringComparison.OrdinalIgnoreCase));
            int sectionLblIdx = file.FileType == AuditFileType.SpTriage
                ? headers.FindIndex(h => h.Equals("Section", StringComparison.OrdinalIgnoreCase))
                : -1;
            bool isNewSqlMagic = file.FileType == AuditFileType.SpTriage
                && headers.Any(h => h.Equals("Server", StringComparison.OrdinalIgnoreCase))
                && !headers.Any(h => h.Equals("SQLInstance", StringComparison.OrdinalIgnoreCase));

            if (checkIdx < 0) { file.FiredCheckCounts = counts; return; }

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;
                var cols = CsvParser.ParseLine(line, delim);

                // If the file holds multiple instances, keep only this one's rows.
                if (instanceIdx >= 0 && cols.Count > instanceIdx)
                {
                    var inst = StripQuotes(cols[instanceIdx]);
                    if (!string.IsNullOrEmpty(inst) &&
                        !string.Equals(inst, file.SqlInstance, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (cols.Count <= checkIdx) continue;

                if (file.FileType == AuditFileType.SpTriage && !isNewSqlMagic && sectionLblIdx >= 0)
                {
                    if (cols.Count <= sectionLblIdx) continue;
                    var lbl = StripQuotes(cols[sectionLblIdx]);
                    if (!lbl.StartsWith("sp_Blitz:", StringComparison.OrdinalIgnoreCase)) continue;
                }

                if (int.TryParse(StripQuotes(cols[checkIdx]), out int checkId) && checkId > 0)
                    counts[checkId] = counts.TryGetValue(checkId, out var c) ? c + 1 : 1;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AuditOutputScanner] Error reading audit file {File}", file.FilePath);
        }

        file.FiredCheckCounts = counts;
    }
}
