/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// Live DMV queries powering the /disk-io page (F2). Per-server inventory of
/// physical files, drive volumes, and IO stats. Cumulative since-startup
/// counters from <c>sys.dm_io_virtual_file_stats</c> are misleading —
/// <see cref="GetSnapshotAsync"/> samples twice with a short gap and returns
/// the DELTA so the page reflects current pressure rather than month-old
/// totals.
///
/// Design memory: `project_disk_io_page`. Mirrors the CodeHotspots service
/// shape — pure DMV reads, no caching here (the sampler service handles
/// background sampling separately).
/// </summary>
public sealed class DiskIoService
{
    private readonly ILogger<DiskIoService>? _log;

    public DiskIoService(ILogger<DiskIoService>? log = null)
    {
        _log = log;
    }

    public sealed class FileRow
    {
        public int DatabaseId { get; set; }
        public int FileId { get; set; }
        public string DatabaseName { get; set; } = "";
        public string LogicalName { get; set; } = "";
        public string PhysicalPath { get; set; } = "";
        public string TypeDesc { get; set; } = "";          // ROWS / LOG / FILESTREAM
        public string DriveLetter { get; set; } = "";       // "C:", "D:", "F:" — or "" if not Windows-style
        public long SizeBytes { get; set; }                  // total file size
        public long DeltaReads { get; set; }                 // num_of_reads delta over the sample window
        public long DeltaWrites { get; set; }
        public long DeltaReadBytes { get; set; }
        public long DeltaWriteBytes { get; set; }
        public double AvgReadStallMs { get; set; }           // (delta io_stall_read_ms) / max(1, deltaReads)
        public double AvgWriteStallMs { get; set; }
        public double SampleWindowSeconds { get; set; }
        public double Iops => SampleWindowSeconds > 0 ? (DeltaReads + DeltaWrites) / SampleWindowSeconds : 0;
        public double MbPerSec => SampleWindowSeconds > 0
            ? (DeltaReadBytes + DeltaWriteBytes) / SampleWindowSeconds / 1024.0 / 1024.0
            : 0;
    }

    public sealed class DriveRow
    {
        public string DriveLetter { get; set; } = "";        // "C:"
        public string VolumeMountPoint { get; set; } = "";
        public long TotalBytes { get; set; }
        public long AvailableBytes { get; set; }
        public double PctUsed => TotalBytes > 0 ? (TotalBytes - AvailableBytes) * 100.0 / TotalBytes : 0;
    }

    public sealed class Snapshot
    {
        public DateTime SampledAt { get; set; }
        public double WindowSeconds { get; set; }
        public List<DriveRow> Drives { get; set; } = new();
        public List<FileRow> Files { get; set; } = new();
    }

    /// <summary>
    /// Returns one current snapshot. Samples <c>dm_io_virtual_file_stats</c>
    /// twice with <paramref name="windowSeconds"/> between samples and emits
    /// per-file deltas. Drives + file inventory come from a single query
    /// joined to <c>sys.master_files</c> and
    /// <c>sys.dm_os_volume_stats</c>.
    /// </summary>
    public async Task<Snapshot> GetSnapshotAsync(
        ServerConnection conn, string server, double windowSeconds = 3.0, CancellationToken ct = default)
    {
        if (windowSeconds < 1) windowSeconds = 1;
        if (windowSeconds > 30) windowSeconds = 30;

        var sample1 = await SampleVfsAsync(conn, server, ct);
        await Task.Delay(TimeSpan.FromSeconds(windowSeconds), ct);
        var sample2 = await SampleVfsAsync(conn, server, ct);

        // Inventory query (drives + master_files) — cheap, run once after the wait
        var (drives, inventory) = await GetInventoryAsync(conn, server, ct);

        // Stitch deltas onto the inventory rows
        foreach (var inv in inventory)
        {
            var key = (inv.DatabaseId, inv.FileId);
            if (sample1.TryGetValue(key, out var s1) && sample2.TryGetValue(key, out var s2))
            {
                inv.DeltaReads = Math.Max(0, s2.NumReads - s1.NumReads);
                inv.DeltaWrites = Math.Max(0, s2.NumWrites - s1.NumWrites);
                inv.DeltaReadBytes = Math.Max(0, s2.ReadBytes - s1.ReadBytes);
                inv.DeltaWriteBytes = Math.Max(0, s2.WriteBytes - s1.WriteBytes);
                var deltaReadStall = Math.Max(0, s2.ReadStallMs - s1.ReadStallMs);
                var deltaWriteStall = Math.Max(0, s2.WriteStallMs - s1.WriteStallMs);
                inv.AvgReadStallMs = inv.DeltaReads > 0 ? (double)deltaReadStall / inv.DeltaReads : 0;
                inv.AvgWriteStallMs = inv.DeltaWrites > 0 ? (double)deltaWriteStall / inv.DeltaWrites : 0;
                inv.SampleWindowSeconds = windowSeconds;
            }
        }

        return new Snapshot
        {
            SampledAt = DateTime.UtcNow,
            WindowSeconds = windowSeconds,
            Drives = drives,
            Files = inventory
        };
    }

    private sealed record VfsRow(long NumReads, long NumWrites, long ReadBytes, long WriteBytes, long ReadStallMs, long WriteStallMs);

    private static async Task<Dictionary<(int dbid, int fileid), VfsRow>> SampleVfsAsync(
        ServerConnection conn, string server, CancellationToken ct)
    {
        const string sql = @"
SELECT
    vfs.database_id, vfs.file_id,
    vfs.num_of_reads, vfs.num_of_writes,
    vfs.num_of_bytes_read, vfs.num_of_bytes_written,
    vfs.io_stall_read_ms, vfs.io_stall_write_ms
FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs;";

        var map = new Dictionary<(int, int), VfsRow>();
        await using var c = new SqlConnection(conn.GetConnectionString(server, "master"));
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 15 };
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            // dm_io_virtual_file_stats returns database_id + file_id as smallint;
            // GetInt32 fails with InvalidCastException. Convert via GetValue.
            var key = (Convert.ToInt32(rdr.GetValue(0)), Convert.ToInt32(rdr.GetValue(1)));
            map[key] = new VfsRow(
                Convert.ToInt64(rdr.GetValue(2)),
                Convert.ToInt64(rdr.GetValue(3)),
                Convert.ToInt64(rdr.GetValue(4)),
                Convert.ToInt64(rdr.GetValue(5)),
                Convert.ToInt64(rdr.GetValue(6)),
                Convert.ToInt64(rdr.GetValue(7))
            );
        }
        return map;
    }

    private static async Task<(List<DriveRow> drives, List<FileRow> files)> GetInventoryAsync(
        ServerConnection conn, string server, CancellationToken ct)
    {
        // master_files joined with dm_os_volume_stats per (db, file) — the volume
        // function needs a db_id+file_id so we CROSS APPLY through master_files.
        const string sql = @"
SELECT
    mf.database_id, mf.file_id, DB_NAME(mf.database_id) AS db_name,
    mf.name AS logical_name, mf.physical_name, mf.type_desc, CAST(mf.size AS BIGINT) * 8 * 1024 AS size_bytes,
    vol.volume_mount_point, vol.total_bytes, vol.available_bytes
FROM sys.master_files mf
OUTER APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vol
WHERE mf.type IN (0, 1)  -- ROWS, LOG (skip FILESTREAM/FULLTEXT for v1)
ORDER BY DB_NAME(mf.database_id), mf.type_desc, mf.name;";

        var files = new List<FileRow>();
        var drives = new Dictionary<string, DriveRow>(StringComparer.OrdinalIgnoreCase);

        await using var c = new SqlConnection(conn.GetConnectionString(server, "master"));
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 15 };
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var physical = rdr.IsDBNull(4) ? "" : rdr.GetString(4);
            var driveLetter = ExtractDriveLetter(physical);
            var mount = rdr.IsDBNull(7) ? "" : rdr.GetString(7);
            var totalBytes = rdr.IsDBNull(8) ? 0 : Convert.ToInt64(rdr.GetValue(8));
            var availBytes = rdr.IsDBNull(9) ? 0 : Convert.ToInt64(rdr.GetValue(9));

            files.Add(new FileRow
            {
                // master_files.file_id is smallint — Convert via GetValue for safety
                DatabaseId = Convert.ToInt32(rdr.GetValue(0)),
                FileId = Convert.ToInt32(rdr.GetValue(1)),
                DatabaseName = rdr.IsDBNull(2) ? "(unknown)" : rdr.GetString(2),
                LogicalName = rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                PhysicalPath = physical,
                TypeDesc = rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                DriveLetter = driveLetter,
                SizeBytes = rdr.IsDBNull(6) ? 0 : Convert.ToInt64(rdr.GetValue(6))
            });

            // Cache the drive once per distinct mount point
            var driveKey = string.IsNullOrEmpty(driveLetter) ? (mount ?? "?") : driveLetter;
            if (!string.IsNullOrEmpty(driveKey) && !drives.ContainsKey(driveKey))
            {
                drives[driveKey] = new DriveRow
                {
                    DriveLetter = driveLetter,
                    VolumeMountPoint = mount ?? "",
                    TotalBytes = totalBytes,
                    AvailableBytes = availBytes
                };
            }
        }

        return (drives.Values.OrderBy(d => d.DriveLetter).ToList(), files);
    }

    /// <summary>Best-effort drive-letter extraction. "C:\Data\foo.mdf" → "C:".
    /// Returns empty string for UNC paths, mount-only paths, or anything we can't parse.</summary>
    private static string ExtractDriveLetter(string physicalPath)
    {
        if (string.IsNullOrEmpty(physicalPath) || physicalPath.Length < 2) return "";
        if (physicalPath[1] == ':' && char.IsLetter(physicalPath[0]))
            return char.ToUpperInvariant(physicalPath[0]) + ":";
        return "";
    }
}
