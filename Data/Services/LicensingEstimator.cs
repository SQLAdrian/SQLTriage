/* In the name of God, the Merciful, the Compassionate */
/*
 * LicensingEstimator — per-server SQL Server OV+SA annual licensing cost estimate.
 *
 * Methodology: .claude/docs/licensing-methodology.md
 * Pricing data: Config/sql-licensing-pricing.json (refreshed by scripts/update-licensing-pricing.py)
 *
 * Voice rule (NEGOTIATION_PRINCIPLES.md): All display copy uses "looks like", "could be"
 *  — never promises. Saving projections include qualitative confidence (Low/Medium/High).
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services
{
    public class LicensingEstimator
    {
        private readonly ILogger<LicensingEstimator> _logger;
        private readonly ServerConnectionManager _connections;
        private readonly IBundleAccessor _bundle;
        private readonly ConcurrentDictionary<string, ServerLicensingFacts> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Lazy-loaded state — invalidated whenever the bundle state changes.
        private readonly object _lock = new();
        private LicensingPricingData? _pricing;
        private bool _loaded;

        public LicensingEstimator(
            ILogger<LicensingEstimator> logger,
            ServerConnectionManager connections,
            IBundleAccessor bundle)
        {
            _logger = logger;
            _connections = connections;
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));

            // Invalidate cached parse whenever a new bundle is loaded.
            _bundle.BundleStateChanged += (_, _) => { lock (_lock) { _loaded = false; _pricing = null; } };
        }

        /// <summary>
        /// Returns the lazily-parsed pricing data from the bundle.
        /// Thread-safe; invalidated by BundleStateChanged.
        /// Returns null when pricing file is absent from the current bundle.
        /// </summary>
        private LicensingPricingData? GetPricing()
        {
            lock (_lock)
            {
                if (_loaded)
                    return _pricing;

                _loaded = true;
                var text = _bundle.GetText("Config/sql-licensing-pricing.json");
                if (text is null)
                {
                    _logger.LogWarning(
                        "sql-licensing-pricing.json not in current bundle (tier={Tier}). Licensing estimates unavailable.",
                        _bundle.Tier);
                    _pricing = null;
                    return null;
                }

                try
                {
                    _pricing = JsonSerializer.Deserialize<LicensingPricingData>(text,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    _logger.LogInformation("Licensing pricing loaded from bundle (last updated {Updated})", _pricing?.LastUpdated);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse sql-licensing-pricing.json from bundle.");
                    _pricing = null;
                }

                return _pricing;
            }
        }

        public bool IsAvailable => GetPricing() != null;

        /// <summary>
        /// Probe a server for the inputs needed to estimate licensing. Cached per server.
        /// </summary>
        public async Task<ServerLicensingFacts?> ProbeServerAsync(string serverName)
        {
            if (_cache.TryGetValue(serverName, out var cached)) return cached;

            try
            {
                var conn = _connections.GetEnabledConnections()
                    .FirstOrDefault(c => c.GetServerList()
                        .Any(s => string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)));
                if (conn == null)
                {
                    _logger.LogDebug("No enabled connection found for {Server}", serverName);
                    return null;
                }

                var connStr = conn.GetConnectionString(serverName, "master") +
                              ";Connect Timeout=5;Application Name=SQLTriage-LicensingProbe";
                using var sql = new SqlConnection(connStr);
                await sql.OpenAsync();

                using var cmd = sql.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        CAST(SERVERPROPERTY('Edition') AS nvarchar(256))         AS Edition,
                        CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(32))   AS ProductVersion,
                        CAST(SERVERPROPERTY('IsClustered') AS int)               AS IsClustered,
                        CAST(SERVERPROPERTY('IsHadrEnabled') AS int)             AS IsHadrEnabled,
                        (SELECT cpu_count FROM sys.dm_os_sys_info)               AS LogicalCpuCount,
                        (SELECT hyperthread_ratio FROM sys.dm_os_sys_info)       AS HyperthreadRatio,
                        (SELECT socket_count FROM sys.dm_os_sys_info)            AS SocketCount,
                        CAST(SERVERPROPERTY('MachineName') AS nvarchar(128))     AS MachineName,
                        CAST(SERVERPROPERTY('ServerName') AS nvarchar(128))      AS ServerInstance";
                cmd.CommandTimeout = 5;

                string edition; string productVersion;
                bool isClustered; bool isHadrEnabled;
                int logicalCpuCount; int hyperthread; int socketCount;

                using (var rdr = await cmd.ExecuteReaderAsync())
                {
                    if (!await rdr.ReadAsync()) return null;
                    edition = rdr.IsDBNull(0) ? "Unknown" : rdr.GetString(0);
                    productVersion = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                    isClustered = !rdr.IsDBNull(2) && rdr.GetInt32(2) == 1;
                    isHadrEnabled = !rdr.IsDBNull(3) && rdr.GetInt32(3) == 1;
                    logicalCpuCount = rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4);
                    hyperthread = rdr.IsDBNull(5) ? 1 : rdr.GetInt32(5);
                    socketCount = rdr.IsDBNull(6) ? 1 : rdr.GetInt32(6);
                }

                // Physical cores = logical / hyperthread ratio. If ratio is 1, no HT.
                // Hyperthreading does NOT increase licensed core count per the licensing guide.
                var physicalCores = hyperthread > 0 ? logicalCpuCount / hyperthread : logicalCpuCount;

                var facts = new ServerLicensingFacts
                {
                    ServerName = serverName,
                    DetectedEdition = edition,
                    NormalisedEdition = NormaliseEdition(edition),
                    ProductVersion = productVersion,
                    IsClustered = isClustered,
                    IsHadrEnabled = isHadrEnabled,
                    LogicalCpuCount = logicalCpuCount,
                    PhysicalCpuCount = physicalCores,
                    SocketCount = socketCount,
                    HyperthreadRatio = hyperthread,
                };

                // If AlwaysOn is enabled, probe AG replicas so the estimator can
                // skip passive secondaries (free under OV+SA per MS licensing guide).
                if (isHadrEnabled)
                {
                    facts.AgReplicas = await ProbeAgReplicasAsync(sql);
                }

                _cache[serverName] = facts;
                _logger.LogDebug("Licensing probe {Server}: edition='{Ed}' cores={C} cluster={Cl} hadr={H} replicas={R}",
                    serverName, edition, physicalCores, isClustered, isHadrEnabled, facts.AgReplicas.Count);
                return facts;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Licensing probe failed for {Server}", serverName);
                return null;
            }
        }

        // Probe AG replica list. Returns empty if AG is enabled but no replicas resolved
        // (e.g. permission denied on the DMV). Best-effort; cheap query.
        private async Task<List<AgReplica>> ProbeAgReplicasAsync(SqlConnection sql)
        {
            var replicas = new List<AgReplica>();
            using var cmd = sql.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    ar.replica_server_name                                AS ReplicaServerName,
                    ISNULL(ars.role_desc, 'UNKNOWN')                      AS RoleDesc,
                    ISNULL(ar.secondary_role_allow_connections_desc, 'NO') AS AllowConnections,
                    CAST(ISNULL(ars.is_local, 0) AS int)                  AS IsLocal,
                    ag.name                                               AS AgName
                FROM sys.availability_replicas ar
                JOIN sys.availability_groups ag
                    ON ag.group_id = ar.group_id
                LEFT JOIN sys.dm_hadr_availability_replica_states ars
                    ON ars.replica_id = ar.replica_id";
            cmd.CommandTimeout = 5;
            try
            {
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    replicas.Add(new AgReplica
                    {
                        ReplicaServerName = rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                        RoleDesc = rdr.IsDBNull(1) ? "UNKNOWN" : rdr.GetString(1),
                        AllowConnections = rdr.IsDBNull(2) ? "NO" : rdr.GetString(2),
                        IsLocal = !rdr.IsDBNull(3) && rdr.GetInt32(3) == 1,
                        AgName = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AG replica probe failed (likely permissions)");
            }
            return replicas;
        }

        /// <summary>
        /// Compute the OV+SA annual licensing cost for a set of probed servers.
        /// AG-aware: when two probed servers belong to the same AG, the secondary
        /// is treated as passive (free under SA) only if AllowConnections = 'NO'.
        /// Active read-replicas (ReadOnly / All) are licensed at full rate and flagged in Notes.
        /// </summary>
        public LicensingEstimate Estimate(IEnumerable<ServerLicensingFacts> servers, double governanceScore)
        {
            var result = new LicensingEstimate();
            var pricing = GetPricing();
            if (pricing == null) return result;

            var serverList = servers.ToList();
            if (serverList.Count == 0) return result;

            // Build a lookup of which probed servers act as primary in an AG.
            // A server is "AG primary" if its own AgReplicas list contains an entry
            // where IsLocal=1 AND RoleDesc='PRIMARY'.
            var primaryServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in serverList)
            {
                if (s.AgReplicas.Any(r => r.IsLocal && string.Equals(r.RoleDesc, "PRIMARY", StringComparison.OrdinalIgnoreCase)))
                    primaryServers.Add(s.ServerName);
            }

            // For each probed server, find the AG role it would play (if any) by
            // matching its name against the replica list of any *other* probed server.
            // This handles the case where the user probes both nodes of an AG.
            var perServer = new List<PerServerLicensing>();
            foreach (var s in serverList)
            {
                AgReplica? roleInSomeAg = null;
                foreach (var other in serverList)
                {
                    if (ReferenceEquals(other, s)) continue;
                    roleInSomeAg = other.AgReplicas.FirstOrDefault(r =>
                        string.Equals(r.ReplicaServerName, s.ServerName, StringComparison.OrdinalIgnoreCase));
                    if (roleInSomeAg != null) break;
                }
                // Also check the server's own self-row (if it knows it's a secondary).
                if (roleInSomeAg == null)
                {
                    roleInSomeAg = s.AgReplicas.FirstOrDefault(r => r.IsLocal);
                }

                var line = ComputePerServer(s, roleInSomeAg);
                perServer.Add(line);
            }

            result.PerServer = perServer;
            result.TotalAnnualCostUSD = perServer.Sum(p => p.AnnualLicensingUSD);

            // Performance-based savings projection
            // Inverse of governance score, capped at 30%
            // Confidence: qualitative tier
            var saveFactor = Math.Min(0.30, (100.0 - Math.Max(0, governanceScore)) / 100.0);
            result.PotentialAnnualSavingUSD = result.TotalAnnualCostUSD * saveFactor;
            result.SavingsConfidence = ComputeConfidence(serverList, governanceScore);

            return result;
        }

        private PerServerLicensing ComputePerServer(ServerLicensingFacts s, AgReplica? agRole = null)
        {
            var line = new PerServerLicensing
            {
                ServerName = s.ServerName,
                Edition = s.NormalisedEdition,
                PhysicalCores = s.PhysicalCpuCount,
                AgRole = agRole?.RoleDesc,
                AgName = agRole?.AgName,
            };

            var pricing = GetPricing();
            if (pricing == null) return line;

            // Passive AG secondary: free under SA per MS licensing guide
            // (only when AllowConnections = 'NO' — readable secondaries must be licensed).
            if (agRole != null
                && string.Equals(agRole.RoleDesc, "SECONDARY", StringComparison.OrdinalIgnoreCase)
                && string.Equals(agRole.AllowConnections, "NO", StringComparison.OrdinalIgnoreCase))
            {
                line.LicensedCores = 0;
                line.AnnualLicensingUSD = 0;
                line.IsPassiveSecondary = true;
                line.Notes = $"Passive AG secondary in '{agRole.AgName}' — free under SA";
                return line;
            }

            // Free editions: zero cost, exit early
            if (s.NormalisedEdition is "Express" or "Developer")
            {
                line.LicensedCores = 0;
                line.AnnualLicensingUSD = 0;
                line.Notes = $"{s.NormalisedEdition} edition — no licensing cost";
                return line;
            }

            // Readable AG secondary: full price, but explain in Notes
            var notes = new List<string>();
            if (agRole != null
                && string.Equals(agRole.RoleDesc, "SECONDARY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(agRole.AllowConnections, "NO", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"Readable AG secondary in '{agRole.AgName}' (AllowConnections={agRole.AllowConnections}) — licensed at full rate");
            }

            // 4-core minimum, rounded up to nearest even number
            var min = pricing.MinimumCoresPerServer;
            var licensedCores = Math.Max(min, RoundUpToEven(s.PhysicalCpuCount));

            // Edition cap clamp
            if (pricing.EditionCaps != null
                && pricing.EditionCaps.TryGetValue(s.NormalisedEdition, out var cap)
                && cap?.Cores.HasValue == true)
            {
                if (licensedCores > cap.Cores.Value)
                {
                    notes.Add($"Detected {s.PhysicalCpuCount} cores but {s.NormalisedEdition} caps at {cap.Cores.Value} — flagged as over-provisioned");
                    licensedCores = cap.Cores.Value;
                }
            }

            line.LicensedCores = licensedCores;

            // Look up per-core perpetual price
            double perCorePrice = 0;
            var priced = pricing.PerpetualPerCoreUSD != null
                && pricing.PerpetualPerCoreUSD.TryGetValue(s.NormalisedEdition, out perCorePrice);
            if (!priced)
            {
                notes.Add($"No pricing for edition '{s.NormalisedEdition}' — defaulted to Standard pricing");
                pricing.PerpetualPerCoreUSD?.TryGetValue("Standard", out perCorePrice);
            }

            var perpetualTotal = licensedCores * perCorePrice;
            line.AnnualLicensingUSD = perpetualTotal * pricing.AnnualSAFactor;
            if (notes.Count > 0) line.Notes = string.Join(" · ", notes);

            return line;
        }

        private static int RoundUpToEven(int n)
        {
            if (n <= 0) return 0;
            return n % 2 == 0 ? n : n + 1;
        }

        private string NormaliseEdition(string detected)
        {
            var pricing = GetPricing();
            if (pricing?.EditionNormalisation == null) return detected;
            if (pricing.EditionNormalisation.TryGetValue(detected, out var normalised))
                return normalised;
            // Fuzzy fallback: substring match
            foreach (var kvp in pricing.EditionNormalisation)
            {
                if (detected.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return "Standard"; // Conservative default — Standard pricing for unknown editions
        }

        private static string ComputeConfidence(List<ServerLicensingFacts> servers, double governanceScore)
        {
            // Per methodology doc — qualitative tiers from cores × score combination.
            var totalCores = servers.Sum(s => s.PhysicalCpuCount);
            if (totalCores < 4) return "None";
            if (totalCores < 8) return "Low";
            if (totalCores < 16 && governanceScore > 70) return "Low";
            if (totalCores >= 16 && governanceScore < 50) return "High";
            return "Medium";
        }
    }

    // ── DTOs ──

    public class ServerLicensingFacts
    {
        public string ServerName { get; set; } = "";
        public string DetectedEdition { get; set; } = "";
        public string NormalisedEdition { get; set; } = "";
        public string ProductVersion { get; set; } = "";
        public bool IsClustered { get; set; }
        public bool IsHadrEnabled { get; set; }
        public int LogicalCpuCount { get; set; }
        public int PhysicalCpuCount { get; set; }
        public int SocketCount { get; set; }
        public int HyperthreadRatio { get; set; }
        public List<AgReplica> AgReplicas { get; set; } = new();
    }

    public class AgReplica
    {
        public string ReplicaServerName { get; set; } = "";
        public string RoleDesc { get; set; } = "";            // PRIMARY | SECONDARY | RESOLVING | UNKNOWN
        public string AllowConnections { get; set; } = "NO";  // NO | READ_ONLY | ALL
        public bool IsLocal { get; set; }
        public string AgName { get; set; } = "";
    }

    public class PerServerLicensing
    {
        public string ServerName { get; set; } = "";
        public string Edition { get; set; } = "";
        public int PhysicalCores { get; set; }
        public int LicensedCores { get; set; }
        public double AnnualLicensingUSD { get; set; }
        public string? Notes { get; set; }
        public string? AgRole { get; set; }       // PRIMARY | SECONDARY | null
        public string? AgName { get; set; }
        public bool IsPassiveSecondary { get; set; }
    }

    public class LicensingEstimate
    {
        public List<PerServerLicensing> PerServer { get; set; } = new();
        public double TotalAnnualCostUSD { get; set; }
        public double PotentialAnnualSavingUSD { get; set; }
        public string SavingsConfidence { get; set; } = "Low";
    }

    // ── Pricing JSON model (matches Config/sql-licensing-pricing.json shape) ──

    public class LicensingPricingData
    {
        public string LastUpdated { get; set; } = "";
        public string Currency { get; set; } = "USD";
        public string Version { get; set; } = "";
        public string Source { get; set; } = "";
        public Dictionary<string, double>? PerpetualPerCoreUSD { get; set; }
        public double AnnualSAFactor { get; set; } = 0.5833;
        public int MinimumCoresPerServer { get; set; } = 4;
        public int MinimumCoresPerVM { get; set; } = 4;
        public Dictionary<string, EditionCapData>? EditionCaps { get; set; }
        public Dictionary<string, string>? EditionNormalisation { get; set; }
    }

    public class EditionCapData
    {
        public int? Cores { get; set; }
        public int? MemoryMB { get; set; }
        public int? DbSizeGB { get; set; }
    }
}
