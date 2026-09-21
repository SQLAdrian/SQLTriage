/* In the name of God, the Merciful, the Compassionate */
/*
 * AuditOutputScanner — single source of truth for discovering + parsing the sp_triage / sp_Blitz
 * CSV files the app writes to <BaseDirectory>/output. Lifted out of DiagnosticsRoadmap.razor (the
 * scan + per-file fired-CheckID parse) so the sp_Blitz dashboard and the Compliance Roadmap share
 * ONE parse path (no second, drifting copy). Since 2026-08-24 the Roadmap page calls this class
 * rather than carrying its own copy, so there is one implementation and not three.
 *
 * Key fact about sp_Blitz: a check emits a CSV ROW only when it FIRES (a problem). So a parsed file
 * yields the FIRED set: BlitzCheckID -> fire count. The dashboard derives health from "which of the
 * known sp_Blitz checks did NOT fire", so the fired set is the raw signal; the universe + weights
 * come from the corpus (see BlitzDashboardService).
 *
 * Column conventions (verified against real output):
 *   sp_Blitz CSV : resolved BY NAME through BlitzCsvLayout — see that type for the two shapes on
 *                  disk and why the instance column is the LAST ServerName rather than the first.
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
/// What happened when this file's fired-CheckID set was read.
///
/// <para>WHY A THIRD STATE. An empty fired set has two meanings and they are opposites: "the audit
/// ran and nothing fired" (a clean server) and "this file could not be read" (nothing is known).
/// Both used to produce the same empty dictionary and no log line, so an unreadable CSV scored the
/// server 100%, maturity L5 Governed, every compliance badge covered. A live sp_Blitz run always
/// emits at least its CheckID -1 banner row, so an all-pass result is itself the tell that parsing
/// failed rather than evidence of a healthy server.</para>
///
/// <para>Consumers must branch on this before printing a score. Treating
/// <see cref="Unreadable"/> as <see cref="Parsed"/> is the defect this enum exists to make
/// impossible to reintroduce silently.</para>
/// </summary>
public enum AuditParseOutcome
{
    /// <summary><see cref="AuditOutputScanner.LoadFiredChecksAsync"/> has not run on this file yet.</summary>
    NotParsed,

    /// <summary>The file was read. Its fired set is exactly what FiredCheckCounts holds, empty included.</summary>
    Parsed,

    /// <summary>
    /// The file could not be read as an audit CSV, or it was read and yielded no fired checks at
    /// all. Its fired set is UNKNOWN, not empty. The second case is deliberately in this state and
    /// not in <see cref="Parsed"/>: a file with rows in it that produces no check ids has measured
    /// nothing, and scoring it prints 100% and maturity L5 Governed off no evidence.
    /// </summary>
    Unreadable
}

/// <summary>
/// Where the four columns this scanner needs sit in one sp_Blitz CSV, resolved BY NAME.
///
/// <para>TWO SHAPES EXIST ON DISK, and both have to read the same. Until 2026-08-24 the shipped
/// sp_Blitz output query selected an <c>xp_regread</c> domain and an UNALIASED
/// <c>CONVERT(VARCHAR,CheckDate,120)</c>, and <see cref="Data.DiagnosticScriptRunner.ExportToCsv"/>
/// prepended a <c>ServerName</c> column holding the CONNECTION TARGET, so the file read
/// <c>ServerName,ID,Domain,ServerName,,Priority,…</c> — two columns named ServerName and a nameless
/// date. The Export Pack's D2 descriptor accepts only sp_Blitz's own twelve-column output-table
/// shape, so every pack built since 2026-07-23 shipped WITHOUT sp_Blitz (proved 2026-08-24). The
/// select list now emits that canonical shape; files already in an install's output folder keep the
/// old one, for as long as the operator keeps them.</para>
///
/// <para>THE INSTANCE COLUMN IS THE LAST <c>ServerName</c>, NOT THE FIRST. The prepended column was
/// always column 0, so in the old shape the LAST ServerName is sp_Blitz's own — the machine name the
/// server reports about itself — and in the new shape there is only one, which is also sp_Blitz's
/// own. Taking the last therefore keys OLD and NEW files on the SAME identity, so an upgraded
/// install does not show its existing CSVs as a second, phantom server beside the new ones. It also
/// puts sp_Blitz's key in the same space as sp_triage's <c>SQLInstance</c>, which is what makes
/// BlitzDashboardService's "one report per instance, prefer sp_Blitz" actually merge the two files
/// for one server instead of reporting the connection string and the machine as two servers.</para>
///
/// <para>WHAT THAT CHANGED IN MEANING. The per-instance key used to be the connection string the
/// operator typed (".\NEW2022"); it is now the name SQL Server reports ("MSI\NEW2022"). Nothing
/// persists that key — the scan, the dashboard reports and the roadmap selection are rebuilt from
/// the files on every scan — so there is no stored state to migrate.</para>
///
/// <para>THE DATE IS <c>CheckDate</c> BY NAME, with the old shape's nameless column as the only
/// fallback and the file's own timestamp behind that. A hard-coded ordinal 4 (what both readers used
/// before) points at <c>FindingsGroup</c> in the canonical shape, which parses as no date at all, so
/// every audit would silently have been dated by file mtime.</para>
/// </summary>
internal readonly record struct BlitzCsvLayout(int InstanceIdx, int DateIdx, int DomainIdx, int CheckIdIdx)
{
    internal static BlitzCsvLayout Resolve(IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        int instanceIdx = -1;
        for (int i = 0; i < headers.Count; i++)
            if (string.Equals(headers[i], "ServerName", StringComparison.OrdinalIgnoreCase))
                instanceIdx = i; // last wins — see the type doc

        int dateIdx = IndexOf(headers, "CheckDate");
        if (dateIdx < 0)
        {
            // The old select list's unaliased CONVERT produced exactly one nameless column and it
            // held CheckDate. Nothing else in either shape is nameless.
            for (int i = 0; i < headers.Count && dateIdx < 0; i++)
                if (string.IsNullOrEmpty(headers[i]))
                    dateIdx = i;
        }

        return new BlitzCsvLayout(instanceIdx, dateIdx, IndexOf(headers, "Domain"), IndexOf(headers, "CheckID"));
    }

    private static int IndexOf(IReadOnlyList<string> headers, string name)
    {
        for (int i = 0; i < headers.Count; i++)
            if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

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

    /// <summary>
    /// Whether <see cref="FiredCheckCounts"/> means anything. Read this before scoring: an
    /// <see cref="AuditParseOutcome.Unreadable"/> file has an empty fired set for a reason that is
    /// not "the server is healthy".
    /// </summary>
    public AuditParseOutcome ParseOutcome { get; set; } = AuditParseOutcome.NotParsed;

    /// <summary>
    /// Why the file could not be read, in one operator-readable clause. Empty unless
    /// <see cref="ParseOutcome"/> is <see cref="AuditParseOutcome.Unreadable"/>.
    /// </summary>
    public string ParseFailureReason { get; set; } = "";

    /// <summary>True only when this file's fired set is real evidence and may be scored.</summary>
    public bool IsScorable => ParseOutcome == AuditParseOutcome.Parsed;
}

/// <summary>
/// A CSV in the output folder that discovery could not attribute to a SQL instance, with the reason.
///
/// <para>WHY IT IS RETURNED RATHER THAN SKIPPED. A file whose header carries no server-name column,
/// or whose rows never name one, used to be dropped mid-loop with no log line and no return value,
/// so the page rendered "No audit files found in the output folder" while the file sat there. That
/// reads as "this server was never audited" when the truth is "this server's audit is unreadable",
/// and the two have opposite consequences for a client report. The parse side already had a third
/// state for exactly this (see <see cref="AuditParseOutcome"/>); discovery did not.</para>
/// </summary>
public sealed record UnreadableOutputFile(string FilePath, string Reason);

/// <summary>
/// One scan of the output folder: the files that named a server, and the files that did not.
/// Callers that print "nothing found" must read BOTH, or they print it over a folder with files in it.
/// </summary>
public sealed record AuditScanResult(
    IReadOnlyList<AuditedFile> Files,
    IReadOnlyList<UnreadableOutputFile> Unreadable);

public interface IAuditOutputScanner
{
    /// <summary>The directory scanned (<c>&lt;BaseDirectory&gt;/output</c> unless overridden).</summary>
    string OutputDirectory { get; }

    /// <summary>
    /// Discovers the newest CSV per (instance, file-type), plus every CSV that could not be
    /// attributed to one. Does NOT parse fired rows yet (call <see cref="LoadFiredChecksAsync"/>
    /// for that). Safe to call on a background thread.
    /// </summary>
    Task<AuditScanResult> ScanAsync(CancellationToken ct = default);

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

    /// <summary>The header line, read on its own so the delimiter is known before records stream.</summary>
    private static string? FirstLine(string path)
    {
        using var reader = new StreamReader(path);
        return reader.ReadLine();
    }

    public Task<AuditScanResult> ScanAsync(CancellationToken ct = default)
        => Task.Run(() => Scan(ct), ct);

    private AuditScanResult Scan(CancellationToken ct)
    {
        if (!Directory.Exists(OutputDirectory))
        {
            _logger.LogInformation("[AuditOutputScanner] No output dir at {Dir}.", OutputDirectory);
            return new AuditScanResult(Array.Empty<AuditedFile>(), Array.Empty<UnreadableOutputFile>());
        }

        var allFiles = Directory.GetFiles(OutputDirectory, "*.csv", SearchOption.TopDirectoryOnly);

        // sp_triage / sqlmagic vs sp_Blitz, by filename (the rule SpBlitzDataset's discovery mirrors).
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
        var unreadable = new List<UnreadableOutputFile>();

        // NEVER A SILENT ABSENCE. Every exit from the loop below that is not "this file was read" or
        // "a newer file for the same instance already won" records the file and the reason, so the
        // operator sees a file that could not be attributed instead of an empty server list.
        void CannotAttribute(string file, string reason)
        {
            unreadable.Add(new UnreadableOutputFile(file, reason));
            _logger.LogWarning(
                "[AuditOutputScanner] {File} was not listed as an audited server: {Reason}. "
                + "The file is in the output folder and is not an audit result.",
                file, reason);
        }

        void ScanGroup(IEnumerable<string> files, AuditFileType fileType)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var firstLine = FirstLine(file);
                    if (string.IsNullOrEmpty(firstLine))
                    { CannotAttribute(file, "the file is empty (no header line)"); continue; }
                    var delim = CsvParser.DetectDelimiter(firstLine);

                    using var reader = new StreamReader(file);
                    using var records = CsvParser.ParseRecords(reader, delim).GetEnumerator();
                    if (!records.MoveNext())
                    { CannotAttribute(file, "the file has no header record"); continue; }
                    var headers = records.Current.Select(StripQuotes).ToList();

                    var layout = BlitzCsvLayout.Resolve(headers);
                    int instanceIdx = fileType == AuditFileType.SpTriage
                        ? headers.FindIndex(h => h.Equals("SQLInstance", StringComparison.OrdinalIgnoreCase)
                                              || h.Equals("Server",      StringComparison.OrdinalIgnoreCase))
                        : layout.InstanceIdx;
                    int dateIdx = fileType == AuditFileType.SpTriage
                        ? headers.FindIndex(h => h.Equals("evaldate", StringComparison.OrdinalIgnoreCase))
                        : layout.DateIdx;
                    int domainIdx = fileType == AuditFileType.SpTriage
                        ? headers.FindIndex(h => h.Equals("Domain", StringComparison.OrdinalIgnoreCase))
                        : layout.DomainIdx;
                    if (instanceIdx < 0)
                    {
                        var wanted = fileType == AuditFileType.SpBlitz ? "ServerName" : "SQLInstance or Server";
                        CannotAttribute(file,
                            $"no {wanted} column in the header (columns found: {string.Join(", ", headers)})");
                        continue;
                    }

                    string? sqlInstance = null;
                    string domain = "";
                    DateTime auditDate = File.GetLastWriteTime(file);

                    while (sqlInstance == null && records.MoveNext())
                    {
                        var cols = records.Current;
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

                    if (string.IsNullOrEmpty(sqlInstance))
                    {
                        CannotAttribute(file,
                            "no row names a server, so this file cannot be attributed to an instance");
                        continue;
                    }

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
                    unreadable.Add(new UnreadableOutputFile(file, "the file could not be read: " + ex.Message));
                }
            }
        }

        ScanGroup(triageFiles, AuditFileType.SpTriage);
        ScanGroup(blitzFiles,  AuditFileType.SpBlitz);

        var files = AdoptDomainWithinInstance(result)
            .OrderBy(s => s.SqlInstance).ThenBy(s => s.FileType).ToList();

        return new AuditScanResult(files, unreadable);
    }

    /// <summary>
    /// Gives a domainless file the domain another file for the SAME instance carries in this scan.
    ///
    /// <para>WHY IT IS NEEDED. The canonical sp_Blitz shape has no Domain column: the twelve columns
    /// D2 accepts are sp_Blitz's own output table, and the <c>xp_regread</c> lookup the old select
    /// list bolted on is not one of them. sp_triage still emits a domain, and the Roadmap page groups
    /// servers by it, so without this an instance holding BOTH files would be listed twice — once
    /// under its domain and once under the blank group.</para>
    ///
    /// <para>WHY IT IS NOT A GUESS. The domain adopted was read from a CSV describing the SAME
    /// instance in the SAME output folder, and it is taken only when every file that names a domain
    /// for that instance names the SAME one. Two disagreeing files leave the field empty rather than
    /// pick a winner. An instance whose only file is sp_Blitz has no domain to adopt and keeps an
    /// empty one: the Roadmap lists it in the ungrouped section, and PROVED live 2026-08-24
    /// (Pages/DiagnosticsRoadmap.razor:1941-1943, the <c>_splitByDomain</c> branch of
    /// <c>ExportQuestPdf</c>) - "Export PDF per Domain" filters to servers whose <c>Domain</c> is
    /// non-empty before building its domain list, so a domainless instance is silently skipped and
    /// gets no per-domain PDF at all, with no message saying so. A documented tradeoff, not a
    /// behaviour this lane changes: an operator who wants a PDF for a domainless server uses the
    /// non-split export instead.</para>
    /// </summary>
    internal static List<AuditedFile> AdoptDomainWithinInstance(List<AuditedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var byInstance = files
            .Where(f => !string.IsNullOrEmpty(f.Domain))
            .GroupBy(f => f.SqlInstance, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => f.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var adopted = new List<AuditedFile>(files.Count);
        foreach (var f in files)
        {
            if (!string.IsNullOrEmpty(f.Domain)
                || !byInstance.TryGetValue(f.SqlInstance, out var domains)
                || domains.Count != 1)
            {
                adopted.Add(f);
                continue;
            }

            adopted.Add(new AuditedFile
            {
                SqlInstance = f.SqlInstance,
                Domain = domains[0],
                AuditDate = f.AuditDate,
                FilePath = f.FilePath,
                FileType = f.FileType,
                FiredCheckCounts = f.FiredCheckCounts,
                ParseOutcome = f.ParseOutcome,
                ParseFailureReason = f.ParseFailureReason,
            });
        }

        return adopted;
    }

    public Task LoadFiredChecksAsync(AuditedFile file, CancellationToken ct = default)
        => Task.Run(() => LoadFiredChecks(file, ct), ct);

    /// <summary>
    /// Records that a file could not be read, on the file itself and in the log.
    ///
    /// <para>Every early exit from <see cref="LoadFiredChecks"/> that is NOT "the file was read and
    /// nothing fired" goes through here. Before 2026-08-26 three of them returned an empty
    /// dictionary and said nothing at all, and the roadmap read that silence as a clean bill of
    /// health.</para>
    /// </summary>
    private void MarkUnreadable(AuditedFile file, Dictionary<int, int> counts, string reason)
    {
        file.FiredCheckCounts = counts;
        file.ParseOutcome = AuditParseOutcome.Unreadable;
        file.ParseFailureReason = reason;
        _logger.LogWarning(
            "[AuditOutputScanner] {File} cannot be scored: {Reason}. Its fired-check set is unknown, "
            + "not empty, so no maturity score may be derived from it.",
            file.FilePath, reason);
    }

    private void LoadFiredChecks(AuditedFile file, CancellationToken ct)
    {
        var counts = new Dictionary<int, int>();
        try
        {
            var firstLine = FirstLine(file.FilePath);
            if (string.IsNullOrEmpty(firstLine))
            { MarkUnreadable(file, counts, "the file is empty (no header line)"); return; }
            var delim = CsvParser.DetectDelimiter(firstLine);

            using var reader = new StreamReader(file.FilePath);
            using var records = CsvParser.ParseRecords(reader, delim).GetEnumerator();
            if (!records.MoveNext())
            { MarkUnreadable(file, counts, "the file has no header record"); return; }
            var headers = records.Current.Select(StripQuotes).ToList();

            var layout = BlitzCsvLayout.Resolve(headers);
            int instanceIdx = file.FileType == AuditFileType.SpTriage
                ? headers.FindIndex(h => h.Equals("SQLInstance", StringComparison.OrdinalIgnoreCase)
                                      || h.Equals("Server",      StringComparison.OrdinalIgnoreCase))
                : layout.InstanceIdx;

            // sp_Blitz: "CheckID" = BlitzCheckID directly.
            // sp_triage: "SectionID" = BlitzCheckID, only for rows where Section starts "sp_Blitz:"
            //            (new sqlmagic: SectionID>0 directly, no Section prefix).
            int checkIdx = file.FileType == AuditFileType.SpBlitz
                ? layout.CheckIdIdx
                : headers.FindIndex(h => h.Equals("SectionID", StringComparison.OrdinalIgnoreCase));
            int sectionLblIdx = file.FileType == AuditFileType.SpTriage
                ? headers.FindIndex(h => h.Equals("Section", StringComparison.OrdinalIgnoreCase))
                : -1;
            bool isNewSqlMagic = file.FileType == AuditFileType.SpTriage
                && headers.Any(h => h.Equals("Server", StringComparison.OrdinalIgnoreCase))
                && !headers.Any(h => h.Equals("SQLInstance", StringComparison.OrdinalIgnoreCase));

            if (checkIdx < 0)
            {
                var wanted = file.FileType == AuditFileType.SpBlitz ? "CheckID" : "SectionID";
                MarkUnreadable(file, counts,
                    $"no {wanted} column in the header (columns found: {string.Join(", ", headers)})");
                return;
            }

            int dataRows = 0;
            while (records.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                dataRows++;
                var cols = records.Current;

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

            if (dataRows == 0)
            {
                // A header with nothing under it is not a clean server. sp_Blitz emits its
                // CheckID -1 banner row on every run, and sp_triage emits its section rows, so a
                // real audit file always has at least one record.
                MarkUnreadable(file, counts,
                    "the file has a header and no data rows, and a real audit run always writes at least one row");
                return;
            }

            if (counts.Count == 0)
            {
                // THE ALL-PASS TELL, and the arm that was still open after 2026-08-26 morning. Rows
                // were read and not one of them yielded a check id. An empty fired set from a file
                // with rows in it has three causes and none of them is a healthy server: the CheckID
                // column parsed to nothing (blank or non-numeric values), the rows belong to another
                // instance, or the file carries no sp_Blitz evidence at all (an sp_triage export
                // whose Section labels never start "sp_Blitz:"). Returning Parsed here scores the
                // server 100%, maturity L5 Governed, every check passed, off a file that measured
                // nothing. A live sp_Blitz run always emits rows carrying its own CheckIDs, so an
                // all-pass result is the tell that parsing failed rather than evidence of health.
                var what = file.FileType == AuditFileType.SpBlitz
                    ? "no row carried a usable CheckID"
                    : "no row carried a usable sp_Blitz SectionID";
                MarkUnreadable(file, counts,
                    $"{what} ({dataRows} data row(s) read), so this file holds no fired-check evidence");
                return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AuditOutputScanner] Error reading audit file {File}", file.FilePath);
            MarkUnreadable(file, counts, "the file could not be read: " + ex.Message);
            return;
        }

        file.FiredCheckCounts = counts;
        file.ParseOutcome = AuditParseOutcome.Parsed;
        file.ParseFailureReason = "";
    }
}
