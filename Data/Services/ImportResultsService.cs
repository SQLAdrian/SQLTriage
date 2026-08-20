/* In the name of God, the Merciful, the Compassionate */
/*
 * ImportResultsService — #84 results-import round-trip.
 *
 * GOAL: Adrian runs checks on a remote machine (usually via the community/redacted binary —
 * the capture tool), exports the results JSON (QuickCheckResultStore.WriteRun's own file
 * shape), and IMPORTS it here, into his full/dev build, to generate reports — no live
 * connection to those servers required. CIO/Governance/report bundles read purely from
 * QuickCheckResultStore (via CheckExecutionService.GetResults), so landing an imported run into
 * the store feeds them for free — see QuickCheckResultStore.WriteRun. The /audit results grid
 * (Pages/QuickCheck.razor) additionally discovers imported-only servers from the store and
 * badges their rows "Imported" (its instance list used to be limited to configured live
 * connections, which is why imports historically skipped it — #84 follow-up, 2026-07-17).
 *
 * PROVENANCE: every landed result is stamped ImportedAtUtc + ImportSourceFile before it hits the
 * store (see below), so the grid can badge it and the newest-run-wins precedence has a timestamp
 * to compare — see ImportProvenance.
 *
 * Used by both Pages/ImportResults.razor (full-build-only UI) and the optional --import CLI
 * flag (Cli/CliImportHost.cs), so the validate -> enrich -> land path has exactly one
 * implementation.
 *
 * ENRICH-ON-IMPORT: a redacted community capture may ship CheckResult rows with the
 * narrative/costing fields stripped (Description, RecommendedAction, BusinessImpact,
 * Eli5Description, Eli5Remediation, EffortHours, ScoreWeight) — those are populated at
 * execution time from the check DEFINITION (CheckExecutionService.ExecuteSingleCheckAsync),
 * not carried by the raw check outcome. This service re-joins each imported CheckResult to
 * the LOCAL full corpus by CheckId (CheckRepositoryService) and backfills only the fields
 * that are empty/default on the imported row — a file's own non-empty value always wins.
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>Outcome of importing one file (one server's run).</summary>
    public sealed class ImportFileOutcome
    {
        public bool Success { get; init; }

        /// <summary>Set when Success is false — a human-readable reason, never a raw exception dump.</summary>
        public string? Error { get; init; }

        /// <summary>Set when the import succeeded but something is worth flagging (e.g. SchemaVersion mismatch).</summary>
        public string? Warning { get; init; }

        public string FileName { get; init; } = "";
        public string? ServerName { get; init; }

        /// <summary>Total CheckResult rows in the file.</summary>
        public int ResultCount { get; init; }

        /// <summary>Rows that had >=1 empty/default enrichable field AND got >=1 field filled from the local corpus.</summary>
        public int EnrichedCount { get; init; }

        /// <summary>
        /// Rows that had >=1 empty/default enrichable field but could NOT be filled — either the
        /// CheckId doesn't exist in the local corpus (a check the community redaction stripped
        /// entirely, or a stale/renamed id), or it does but the corpus itself has nothing for the
        /// missing field(s). Surfaced honestly rather than silently leaving gaps in the report.
        /// </summary>
        public int UnenrichedCount { get; init; }
    }

    /// <summary>Outcome of importing a batch (multiple files and/or a directory of files).</summary>
    public sealed class ImportBatchOutcome
    {
        public IReadOnlyList<ImportFileOutcome> Files { get; init; } = Array.Empty<ImportFileOutcome>();

        public int SucceededCount => Files.Count(f => f.Success);
        public int FailedCount => Files.Count(f => !f.Success);
        public int TotalResultsImported => Files.Where(f => f.Success).Sum(f => f.ResultCount);
        public int TotalEnriched => Files.Where(f => f.Success).Sum(f => f.EnrichedCount);
        public int TotalUnenriched => Files.Where(f => f.Success).Sum(f => f.UnenrichedCount);
    }

    public sealed class ImportResultsService
    {
        private readonly QuickCheckResultStore _store;
        private readonly CheckRepositoryService _checkRepo;
        private readonly ILogger<ImportResultsService> _logger;

        public ImportResultsService(
            QuickCheckResultStore store,
            CheckRepositoryService checkRepo,
            ILogger<ImportResultsService> logger)
        {
            _store = store;
            _checkRepo = checkRepo;
            _logger = logger;
        }

        /// <summary>
        /// Validate + enrich + land a results JSON document already read into memory (the Import
        /// page reads the browser-picked file into a string before calling this; tests call it
        /// directly). Never throws — a malformed file comes back as Success=false with a clear
        /// Error, nothing is written to the store.
        /// </summary>
        public ImportFileOutcome ImportFile(string fileName, string jsonContent)
        {
            var payload = _store.TryParseRunPayload(jsonContent, out var error, out var warning);
            if (payload is null)
            {
                _logger.LogWarning("Import rejected for {File}: {Error}", fileName, error);
                return new ImportFileOutcome { Success = false, FileName = fileName, Error = error };
            }

            var (enriched, unenriched) = EnrichResults(payload.Results);

            // Stamp import provenance on every row BEFORE it lands, so it persists through the
            // store round-trip and the /audit grid can badge it "Imported" (with date + source
            // file) — a reader must never mistake an imported result for a locally-executed one.
            // Unconditional overwrite: even a file that was itself previously imported/exported now
            // truthfully reflects THIS import (its provenance is "entered this build's store now").
            var importedAtUtc = DateTime.UtcNow;
            foreach (var r in payload.Results)
            {
                r.ImportedAtUtc = importedAtUtc;
                r.ImportSourceFile = fileName;
            }

            // Land it: same store, same file-naming/retention WriteRun already gives every other
            // run. The result is honest at every surface — its truthful Pass/Fail verdict is
            // untouched, and its imported provenance now travels with it.
            _store.WriteRun(payload.ServerName, payload.Results);

            _logger.LogInformation(
                "Imported {Count} result(s) for {Server} from {File} ({Enriched} enriched, {Unenriched} could not be enriched)",
                payload.Results.Count, payload.ServerName, fileName, enriched, unenriched);

            return new ImportFileOutcome
            {
                Success = true,
                Warning = warning,
                FileName = fileName,
                ServerName = payload.ServerName,
                ResultCount = payload.Results.Count,
                EnrichedCount = enriched,
                UnenrichedCount = unenriched,
            };
        }

        /// <summary>Reads a file off disk (CLI / folder-import path) and imports it.</summary>
        public ImportFileOutcome ImportFile(string path)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                return new ImportFileOutcome
                {
                    Success = false,
                    FileName = Path.GetFileName(path),
                    Error = $"Could not read file: {ex.Message}",
                };
            }

            return ImportFile(Path.GetFileName(path), json);
        }

        /// <summary>
        /// Multi-file / multi-server import: each entry in <paramref name="paths"/> may be a
        /// results JSON file OR a directory (its top-level *.json files are imported — not
        /// recursive, so a stray unrelated JSON file one level down is never picked up by
        /// accident). One bad file never blocks the rest of the batch.
        /// </summary>
        public ImportBatchOutcome ImportPaths(IEnumerable<string> paths)
        {
            var files = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                    files.AddRange(Directory.GetFiles(p, "*.json", SearchOption.TopDirectoryOnly));
                else
                    files.Add(p); // let ImportFile(path) report a uniform "could not read" for a bad path
            }

            var outcomes = files.Select(ImportFile).ToList();
            return new ImportBatchOutcome { Files = outcomes };
        }

        /// <summary>
        /// Backfill empty/default narrative + costing fields on each result from the local full
        /// corpus, joined by CheckId. A file's own non-empty value is NEVER overwritten — only
        /// gaps get filled. Returns (rows with >=1 field filled, rows that needed a fill but
        /// couldn't get one). Exercised indirectly through <see cref="ImportFile(string, string)"/>
        /// — see ImportResultsServiceTests for the enrichment cases.
        /// </summary>
        private (int EnrichedCount, int UnenrichedCount) EnrichResults(IReadOnlyList<CheckResult> results)
        {
            var enriched = 0;
            var unenriched = 0;

            foreach (var r in results)
            {
                if (!NeedsEnrichment(r)) continue;

                var corpus = _checkRepo.GetCheckById(r.CheckId);
                if (corpus is null)
                {
                    unenriched++;
                    continue;
                }

                var filled = 0;
                filled += FillString(() => r.Description, v => r.Description = v, corpus.Description);
                filled += FillString(() => r.RecommendedAction, v => r.RecommendedAction = v, corpus.RecommendedAction);
                filled += FillString(() => r.BusinessImpact, v => r.BusinessImpact = v, corpus.BusinessImpact);
                filled += FillString(() => r.Eli5Description, v => r.Eli5Description = v, corpus.Eli5Description);
                filled += FillString(() => r.Eli5Remediation, v => r.Eli5Remediation = v, corpus.Eli5Remediation);

                if (r.EffortHours <= 0 && corpus.EffortHours > 0)
                {
                    r.EffortHours = corpus.EffortHours;
                    filled++;
                }
                if (r.ScoreWeight <= 0 && corpus.ScoreWeight > 0)
                {
                    r.ScoreWeight = corpus.ScoreWeight;
                    filled++;
                }

                if (filled > 0) enriched++;
                else unenriched++; // found the check, but the corpus had nothing for the missing field(s) either
            }

            return (enriched, unenriched);
        }

        private static bool NeedsEnrichment(CheckResult r) =>
            string.IsNullOrWhiteSpace(r.Description)
            || string.IsNullOrWhiteSpace(r.RecommendedAction)
            || string.IsNullOrWhiteSpace(r.BusinessImpact)
            || string.IsNullOrWhiteSpace(r.Eli5Description)
            || string.IsNullOrWhiteSpace(r.Eli5Remediation)
            || r.EffortHours <= 0
            || r.ScoreWeight <= 0;

        private static int FillString(Func<string?> get, Action<string?> set, string? corpusValue)
        {
            if (!string.IsNullOrWhiteSpace(get())) return 0;      // file already has it — never overwrite
            if (string.IsNullOrWhiteSpace(corpusValue)) return 0;  // corpus has nothing either
            set(corpusValue);
            return 1;
        }
    }
}
