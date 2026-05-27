/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// Result of a single sp_BLITZ CSV import operation.
/// </summary>
public sealed record SpBlitzImportResult(
    int InsertedCount,
    int SkippedCount,
    Guid ImportId,
    string? Error);

/// <summary>
/// Orchestrates sp_BLITZ CSV import: parse → cache → return result.
/// </summary>
public sealed class SpBlitzImportService
{
    private readonly SpBlitzCache _cache;

    public SpBlitzImportService(SpBlitzCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Parses <paramref name="csv"/>, saves findings to the local cache,
    /// and returns a summary.
    /// </summary>
    public async Task<SpBlitzImportResult> ImportAsync(
        Stream csv,
        string serverLabel,
        CancellationToken ct = default)
    {
        var importId = Guid.NewGuid();
        var importedUtc = DateTime.UtcNow;

        try
        {
            var findings = SpBlitzCsvParser.Parse(csv, serverLabel, importedUtc, importId, out int skipped);
            await _cache.SaveAsync(findings, ct);
            return new SpBlitzImportResult(findings.Count, skipped, importId, null);
        }
        catch (InvalidDataException ex)
        {
            return new SpBlitzImportResult(0, 0, importId, ex.Message);
        }
        catch (Exception ex)
        {
            return new SpBlitzImportResult(0, 0, importId, $"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns all cached findings for the supplied server labels, merged and
    /// ordered by import time descending, then by priority ascending.
    /// </summary>
    public async Task<IReadOnlyList<BlitzFinding>> GetForCurrentAssessmentAsync(
        IEnumerable<string> serverLabels,
        CancellationToken ct = default)
    {
        var labels = serverLabels?.ToList() ?? new List<string>();
        if (labels.Count == 0)
            return Array.Empty<BlitzFinding>();

        var merged = new List<BlitzFinding>();
        foreach (var label in labels)
        {
            var rows = await _cache.LoadByServerAsync(label, ct);
            merged.AddRange(rows);
        }

        return merged
            .OrderByDescending(f => f.ImportedUtc)
            .ThenBy(f => f.Priority)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Removes all findings from one import batch.</summary>
    public Task DeleteImportAsync(Guid importId, CancellationToken ct = default)
        => _cache.DeleteImportAsync(importId, ct);
}
