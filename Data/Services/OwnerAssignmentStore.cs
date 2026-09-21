/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using SQLTriage.Data;

namespace SQLTriage.Data.Services
{
    /// <summary>One persisted owner / review-by assignment for a finding.</summary>
    public class FindingOwnerAssignment
    {
        public string Owner { get; set; } = string.Empty;
        public DateTime ReviewByUtc { get; set; }
        public string AssignedBy { get; set; } = string.Empty;
        public DateTime AssignedAtUtc { get; set; }
    }

    /// <summary>Serialized container for the assignment store (versioned for forward-compat).</summary>
    public class OwnerAssignmentStoreData
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, FindingOwnerAssignment> Assignments { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Persists per-finding owner + review-by assignments so they survive across
    /// scans and sessions (the Risk Register otherwise derives a single global owner
    /// and a fixed +90d review date at report-build time). Stored as JSON under
    /// Config/finding-owners.json via <see cref="ConfigFileHelper"/> — the same
    /// convention as rbac-users.json; desktop is single-writer so last-write-wins.
    ///
    /// Key = "{server}|{checkId}" (case-insensitive). A server-less assignment is
    /// stored under "*|{checkId}" and acts as an estate-wide default; an exact
    /// (server, checkId) match takes precedence over it.
    /// </summary>
    public class OwnerAssignmentStore
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private OwnerAssignmentStoreData _data;

        /// <summary>
        /// How finding-owners.json came off disk.
        ///
        /// <para><b>Warned about, NOT refused.</b> The line this codebase draws: a write is refused
        /// when it would destroy a SECRET or a SECURITY CONTROL — something that changes what the
        /// install can do or who it trusts — and announced when it would destroy DATA the operator
        /// authored and can author again. This file is the second kind. It is owner names and
        /// review-by dates: governance metadata that no report depends on (an unassigned finding
        /// falls back to the derived owner and a +90 day review), that leaks nothing, and that is
        /// visible in every Risk Register already exported.</para>
        ///
        /// <para>Refusing here would take the Risk Register's owner editor offline for a file whose
        /// damage costs nothing operational — and a guard that blocks harmless work is a guard that
        /// gets removed, taking the intake-SAS and channel-credential protection with it. So this
        /// one says exactly what was lost and when.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        /// <summary>True when finding-owners.json exists and did not load, so no assignment is in force.</summary>
        public bool IsStoreDamaged { get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; } }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        public OwnerAssignmentStore() : this(null) { }

        // Test seam: pathOverride lets a test point at a temp file. Production uses the Config/ path.
        public OwnerAssignmentStore(string? pathOverride)
        {
            _filePath = pathOverride ?? Path.Combine(AppContext.BaseDirectory, "Config", "finding-owners.json");
            _data = ConfigFileHelper.Load<OwnerAssignmentStoreData>(_filePath, null, out _load, out _quarantine);

            if (IsStoreDamaged)
                Serilog.Log.Warning(
                    "[OwnerAssignments] {Path} exists and did not load ({Outcome}). No per-finding owner or "
                    + "review-by date is in force — reports will show the derived default owner instead. The next "
                    + "assignment saved will replace the file with only what is in memory, so repair it first if "
                    + "you want the existing assignments back. {Recovery}",
                    _filePath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
        }

        private const string EstateWildcard = "*";

        private static string BuildKey(string? server, string checkId)
        {
            var s = string.IsNullOrWhiteSpace(server) ? EstateWildcard : server.Trim();
            return $"{s}|{(checkId ?? string.Empty).Trim()}".ToLowerInvariant();
        }

        /// <summary>
        /// Returns the assignment for (server, checkId), falling back to an estate-wide
        /// "*|checkId" assignment when no server-specific one exists. Null if unassigned.
        /// </summary>
        public FindingOwnerAssignment? Get(string? server, string checkId)
        {
            if (string.IsNullOrWhiteSpace(checkId)) return null;
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(server)
                    && _data.Assignments.TryGetValue(BuildKey(server, checkId), out var exact))
                    return exact;
                return _data.Assignments.TryGetValue(BuildKey(null, checkId), out var estate) ? estate : null;
            }
        }

        /// <summary>Sets (or clears, when owner is blank and reviewBy is null) an assignment.</summary>
        public void Set(string? server, string checkId, string? owner, DateTime? reviewByUtc, string assignedBy)
        {
            if (string.IsNullOrWhiteSpace(checkId)) return;
            var key = BuildKey(server, checkId);
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(owner) && reviewByUtc is null)
                {
                    _data.Assignments.Remove(key);
                }
                else
                {
                    _data.Assignments[key] = new FindingOwnerAssignment
                    {
                        Owner = owner?.Trim() ?? string.Empty,
                        ReviewByUtc = reviewByUtc ?? DateTime.UtcNow.Date.AddDays(90),
                        AssignedBy = assignedBy ?? string.Empty,
                        AssignedAtUtc = DateTime.UtcNow,
                    };
                }
                // Announced, not refused. Fires exactly once per damaged load — the successful write
                // makes the store Loaded — and it is the only record of the moment the damaged file
                // stopped existing.
                var replacingDamage = _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;
                ConfigFileHelper.Save(_filePath, _data);
                if (replacingDamage)
                    Serilog.Log.Warning(
                        "[OwnerAssignments] {Path} did not load ({Outcome}) and has now been REPLACED by the "
                        + "{Count} assignment(s) held in memory. Any assignment the damaged file contained is gone "
                        + "from the live file. {Recovery}",
                        _filePath, _load, _data.Assignments.Count,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));

                _load = ConfigLoadOutcome.Loaded;
                _quarantine = null;
            }
        }
    }
}
