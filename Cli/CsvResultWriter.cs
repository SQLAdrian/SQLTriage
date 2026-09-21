/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Cli;

/// <summary>
/// Generic CSV writer for a run's <see cref="CheckResult"/> set. There is no existing generic
/// CSV export in the app (ReportBundleService / ComplianceMap build their own HTML; the
/// QuestPDF report builders are PDF-only) — this fills that gap for --format csv.
/// Deterministic, timestamped filename in the given output directory.
///
/// 2026-07-19 — the exported shape changed in three ways, all deliberate:
///   • A <c>Status</c> column now sits between Severity and Passed. Every column after Severity
///     therefore shifts one place right; a consumer that reads by POSITION rather than by header
///     name needs updating. This was chosen over appending Status at the end because the whole
///     point is that a reader must not stop at <c>Passed</c> (below), and the last column of a
///     15-column sheet is off-screen in Excel.
///   • Servers the operator asked for that produced no rows are emitted as DATA ROWS carrying
///     <c>Status=NOT_ASSESSED</c>, not as a <c>#</c> comment line above the header. The header is
///     unconditionally on line 1. See the <c>unassessedServers</c> parameter for the measurement
///     that motivated the change.
///   • <c>Passed</c> keeps its RAW <see cref="CheckResult.Passed"/> value and is no longer the
///     verdict. CheckExecutionService sets Passed=true for SKIP and INFO results alike, so a
///     not-applicable Critical check exported as Passed=True with nothing to say otherwise.
///     Status carries the real bucket (see <see cref="CliResultState"/>); Passed is retained
///     unchanged so an existing consumer's values do not silently change meaning.
///
///     2026-07-20 sweep — this now applies to WARN too: a check that could not fully assess the
///     server exports as <c>Status=WARN,Passed=True</c>. That pairing is DELIBERATE and pinned by
///     CsvResultWriter_WarnRowCarriesWarnStatus_WhilePassedStaysRaw, but note the open tension: a
///     consumer that filters on <c>Passed=True</c> — the obvious thing to do — counts WARN, SKIP
///     and INFO rows as passes, which is the false-clean this file's own Status column exists to
///     prevent. It also sits awkwardly beside the NOT_ASSESSED rows below, which leave Passed
///     EMPTY precisely because "False would assert a check that ran and did not pass" — the same
///     argument applies to a WARN row asserting True. Reversing it is a machine-readable contract
///     change and belongs to Adrian, not to this sweep; raised, not silently altered.
///   • The file is written with a UTF-8 BOM. Excel decodes a BOM-less UTF-8 CSV as ANSI on an
///     English-Windows default, which mangles the non-ASCII characters the corpus messages use
///     (e.g. the em-dash in "SKIP — ...").
///
///   2026-08-25 (honesty-hunt platform-r2-01) — Actual/Expected only mean a measurement on the
///     NUMERIC contract, where <c>result.Passed = ActualValue == ExpectedValue</c>. On the VERDICT
///     contract (the bulk of the corpus) ExpectedValue is the definition's numeric field left at
///     its 0 default — the check never consults it — and ActualValue is the check's own count
///     column, not a measurement of the noun in the message. Shipping a raw "…,1,0" for a verdict
///     row read as a threshold comparison that never happened and contradicted the message's own
///     stated threshold. Two changes, both mirroring <c>FindingTranslator.MeasurementSuffix</c> and
///     the JSON export: (a) a <c>Verdict</c> column is APPENDED at the end (existing column
///     positions are unchanged, so a reader that maps by position is unaffected); (b) on the verdict
///     contract ExpectedValue is left EMPTY and ActualValue is emitted only when the check supplied a
///     count (&gt; 0), so the pair no longer manufactures a false measurement. Numeric-contract rows
///     are byte-identical to before and carry an empty Verdict.
/// </summary>
public static class CsvResultWriter
{
    private const string Header =
        "Server,CheckId,CheckName,Category,Severity,Status,Passed,IsAccepted,IsCorrupted,ErrorMessage,Message,ActualValue,ExpectedValue,DurationMs,ExecutedAtUtc,Verdict";

    /// <summary>Status value for a requested server that produced no rows. Deliberately NOT a
    /// <see cref="FindingState"/>: those six describe a check that ran, and no check ran here, so
    /// widening that enum would put a non-verdict into the PDF's state lookup too. Screaming-case
    /// single token to match the vocabulary <see cref="CliResultState.Label"/> already emits.</summary>
    private const string NotAssessedStatus = "NOT_ASSESSED";

    // Derived from Header so a column added to Header cannot leave the synthetic rows too narrow.
    private static readonly string[] HeaderFields = Header.Split(',');
    private static readonly int FieldCount  = HeaderFields.Length;
    private static readonly int ServerIndex = Array.IndexOf(HeaderFields, "Server");
    private static readonly int StatusIndex = Array.IndexOf(HeaderFields, "Status");

    /// <summary>Writes <paramref name="results"/> to <c>audit_&lt;UTC timestamp&gt;.csv</c> under
    /// <paramref name="outDir"/> and returns the full path written.</summary>
    /// <param name="unassessedServers">Servers the operator asked for that produced no rows —
    /// unreachable at preflight, or excluded by the seat licence (the two are already merged into
    /// one list by <c>CliAuditHost</c>, which is why a row below claims no reason: the caller does
    /// not pass one, and inventing one would be worse than omitting it).
    ///
    /// Each is emitted as a normal 15-field DATA ROW directly beneath the header, with
    /// <c>Status=NOT_ASSESSED</c> and <c>Server</c> set to the name. The intent is unchanged — a
    /// CSV holding 2 servers' worth of rows for a 3-server request must not read as a complete
    /// estate — but the mechanism is no longer a <c>#</c> comment line above the header, which
    /// broke parsers that (correctly) take line 1 as the header. Measured 2026-07-19 on a
    /// 15-column fixture: Python <c>csv.DictReader</c> read the comment line AS the header and
    /// reported ONE field named "# Servers not assessed (no rows below): SQL02; SQL03", with
    /// <c>Server</c> resolving to None across every data row; the same file without the comment
    /// line gave the expected 15 fieldnames. (Windows PowerShell 5.1 <c>Import-Csv</c> happens to
    /// survive it — measured, it skips <c>#</c> lines that precede the header — so PowerShell was
    /// NOT the consumer at risk. Excel's rendering of the comment line was not tested.)
    ///
    /// Rows carry only what is known: Server, Status, and nothing else. Passed/IsAccepted/
    /// IsCorrupted are left EMPTY rather than False, because False would assert a check that ran
    /// and did not pass; ExecutedAtUtc is empty because nothing executed. They sort first so a
    /// human opening the file sees them without scrolling, and a filter or pivot on Status
    /// surfaces them. A run with nothing unassessed emits no such rows, so the common case is
    /// byte-identical to before.</param>
    public static string Write(
        IReadOnlyList<CheckResult> results, string outDir, DateTime runTimestampUtc,
        IReadOnlyList<string>? unassessedServers = null)
    {
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"audit_{runTimestampUtc:yyyyMMdd_HHmmssZ}.csv");

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        if (unassessedServers is { Count: > 0 })
        {
            foreach (var server in unassessedServers)
            {
                // Built as a full-width field array rather than a hand-counted run of commas, so
                // the row cannot drift out of step with Header: FieldCount is asserted against
                // the header below. Csv() quotes a server name containing a comma — the old
                // comment line did not, so a name like "SRV,1" widened that line by a field too.
                var fields = new string[FieldCount];
                Array.Fill(fields, string.Empty);
                fields[ServerIndex] = Csv(server);
                fields[StatusIndex] = NotAssessedStatus;
                sb.Append(string.Join(",", fields)).Append('\n');
            }
        }
        foreach (var r in results)
        {
            // See the 2026-08-25 note above. On the verdict contract the fabricated Expected is
            // dropped and the count is shown only when the check supplied one; on the numeric
            // contract both stay verbatim. The raw corpus Verdict token goes in its own column
            // (empty for numeric rows), matching the machine-token convention the Status column
            // already uses (WARN, not the human-facing "Partial").
            var isVerdict    = !string.IsNullOrWhiteSpace(r.Verdict);
            var actualCell   = isVerdict
                ? (r.ActualValue > 0 ? r.ActualValue.ToString() : string.Empty)
                : r.ActualValue.ToString();
            var expectedCell = isVerdict ? string.Empty : r.ExpectedValue.ToString();
            var verdictCell  = isVerdict ? Csv(r.Verdict!) : string.Empty;

            sb.Append(Csv(r.InstanceName)).Append(',')
              .Append(Csv(r.CheckId)).Append(',')
              .Append(Csv(r.CheckName)).Append(',')
              .Append(Csv(r.Category)).Append(',')
              .Append(Csv(r.Severity)).Append(',')
              .Append(CliResultState.Label(CliResultState.Of(r))).Append(',')
              .Append(r.Passed).Append(',')
              .Append(r.IsAccepted).Append(',')
              .Append(r.IsCorrupted).Append(',')
              .Append(Csv(r.ErrorMessage ?? string.Empty)).Append(',')
              .Append(Csv(r.Message)).Append(',')
              .Append(actualCell).Append(',')
              .Append(expectedCell).Append(',')
              .Append(r.DurationMs).Append(',')
              .Append(r.ExecutedAt.ToString("o")).Append(',')
              .Append(verdictCell)
              .Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>Quotes a field iff it contains a comma, quote, or newline (RFC 4180 minimal).</summary>
    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}

/// <summary>
/// The one place the CLI turns a <see cref="CheckResult"/> into a single mutually-exclusive
/// bucket, shared by the CSV Status column and the --format pdf findings report so the two
/// artifacts from one run cannot disagree.
///
/// The cascade order mirrors QuickCheck.razor's status badge (Error → Skip → Info → Accepted →
/// Pass → Fail) and delegates the Skip/Info predicates to <see cref="CheckClassification"/>,
/// the shared definition governance, /dba, /cio and the audit page already use. Before this,
/// the CLI carried its OWN skip test (a "SKIP" Message-prefix check) that the corpus does not
/// actually signal with — corpus checks carry Verdict=="SKIP" — so it matched nothing and both
/// exports were a flat Pass/Fail.
/// </summary>
internal static class CliResultState
{
    // Ruling #4 (2026-07-20): Warn inserted after Skip and BEFORE Info. A WARN verdict on a check
    // whose declared Severity happens to be "Info" would otherwise be absorbed by the Info arm and
    // the "could not fully assess" signal would vanish — the whole point of the state.
    public static FindingState Of(CheckResult r) =>
          (r.IsCorrupted || !string.IsNullOrEmpty(r.ErrorMessage)) ? FindingState.Error
        : CheckClassification.IsSkip(r)  ? FindingState.Skipped
        : CheckClassification.IsWarn(r)  ? FindingState.Warn
        : CheckClassification.IsInfo(r)  ? FindingState.Info
        : (!r.Passed && r.IsAccepted)    ? FindingState.Accepted
        : r.Passed                       ? FindingState.Pass
                                         : FindingState.Fail;

    public static string Label(FindingState s) => s switch
    {
        FindingState.Pass     => "PASS",
        FindingState.Fail     => "FAIL",
        FindingState.Error    => "ERROR",
        FindingState.Skipped  => "SKIP",
        FindingState.Info     => "INFO",
        FindingState.Accepted => "ACCEPTED",
        // The corpus token itself. The human-facing PDF says "Partial"; this column is a machine
        // token and must round-trip the verdict the corpus emitted. NOT_ASSESSED is already taken
        // (a server that produced no results at all) and means something different.
        FindingState.Warn     => "WARN",
        _                     => "UNKNOWN",
    };
}
