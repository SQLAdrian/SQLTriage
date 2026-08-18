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

        public OwnerAssignmentStore()
        {
            _filePath = Path.Combine(AppContext.BaseDirectory, "Config", "finding-owners.json");
            _data = ConfigFileHelper.Load<OwnerAssignmentStoreData>(_filePath);
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
                ConfigFileHelper.Save(_filePath, _data);
            }
        }
    }
}
