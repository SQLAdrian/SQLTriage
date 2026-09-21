/* In the name of God, the Merciful, the Compassionate */

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Common;
using SQLTriage.Data.Models;
using Smo = Microsoft.SqlServer.Management.Smo;
using SmoServer = Microsoft.SqlServer.Management.Smo.Server;

namespace SQLTriage.Data.Services;

/// <summary>
/// Captures, persists, and diffs server-level configuration documentation
/// using SMO. Outputs JSON snapshots under output/serverdocs/. Renders via
/// /server-docs. No schema-level scrape (intentionally — schema docs are
/// out of scope for v1).
/// </summary>
public class ServerDocumentationService
{
    private const int SnapshotsToKeep = 10;
    private const int DefaultTimeoutSeconds = 30;

    private readonly ILogger<ServerDocumentationService> _logger;
    private readonly ServerConnectionManager _connections;
    private readonly string _storeDir;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ServerDocumentationService(
        ILogger<ServerDocumentationService> logger,
        ServerConnectionManager connections)
    {
        _logger = logger;
        _connections = connections;
        _storeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "serverdocs");
        Directory.CreateDirectory(_storeDir);
    }

    // ───────────────────────── Public API ─────────────────────────

    /// <summary>Capture a fresh snapshot, persist it, prune old, and return it.</summary>
    public async Task<ServerDocSnapshot> CaptureAsync(string serverName, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var snapshot = new ServerDocSnapshot
        {
            Server = serverName,
            CapturedAtUtc = DateTime.UtcNow,
        };

        var connStr = ResolveConnectionString(serverName);
        if (connStr == null)
        {
            snapshot.CaptureWarnings.Add($"No connection configured for '{serverName}'.");
            return snapshot;
        }

        try
        {
            await Task.Run(() =>
            {
                using var sqlConn = new SqlConnection(connStr);
                sqlConn.Open();
                var serverConn = new Microsoft.SqlServer.Management.Common.ServerConnection(sqlConn) { StatementTimeout = DefaultTimeoutSeconds };
                var smo = new SmoServer(serverConn);
                TuneInitFields(smo);

                snapshot.Sections.Add(BuildInstanceSection(smo, snapshot.CaptureWarnings));
                snapshot.Sections.Add(BuildSecuritySection(smo, snapshot.CaptureWarnings));
                snapshot.Sections.Add(BuildHaDrSection(smo, snapshot.CaptureWarnings));
                snapshot.Sections.Add(BuildAgentSection(smo, snapshot.CaptureWarnings));
                snapshot.Sections.Add(BuildDatabasesSection(smo, snapshot.CaptureWarnings));
                snapshot.Sections.Add(BuildResourceSection(smo, snapshot.CaptureWarnings));
                snapshot.Sections.Add(BuildLinkedServersSection(smo, snapshot.CaptureWarnings));
                snapshot.Sections.Add(BuildTraceFlagsSection(smo, snapshot.CaptureWarnings));
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server documentation capture failed for {Server}", serverName);
            snapshot.CaptureWarnings.Add($"Capture failed: {ex.Message}");
        }

        snapshot.CaptureDurationMs = (int)sw.ElapsedMilliseconds;
        await PersistAsync(snapshot, ct);
        Prune(serverName);
        return snapshot;
    }

    /// <summary>
    /// Capture multiple servers in parallel, throttled by <paramref name="parallelism"/>.
    /// Each completion fires <paramref name="onCompleted"/> so the caller can update UI.
    /// One server's failure does not abort the rest — <see cref="CaptureAsync"/> already
    /// swallows its own exceptions into snapshot warnings.
    /// </summary>
    public async Task<List<ServerDocSnapshot>> CaptureManyAsync(
        IEnumerable<string> serverNames,
        int parallelism = 4,
        Func<string, ServerDocSnapshot, Task>? onCompleted = null,
        CancellationToken ct = default)
    {
        using var sem = new SemaphoreSlim(Math.Max(1, parallelism));
        var tasks = serverNames.Select(async srv =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var snap = await CaptureAsync(srv, ct);
                if (onCompleted != null) await onCompleted(srv, snap);
                return snap;
            }
            finally
            {
                sem.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    public async Task<ServerDocSnapshot?> LoadLatestAsync(string serverName, CancellationToken ct = default)
    {
        var files = EnumerateSnapshotFiles(serverName).ToList();
        if (files.Count == 0) return null;
        return await LoadFileAsync(files[0], ct);
    }

    /// <summary>Returns capture timestamps for a server, newest first.</summary>
    public IReadOnlyList<DateTime> ListSnapshotTimestamps(string serverName)
    {
        return EnumerateSnapshotFiles(serverName)
            .Select(ParseTimestampFromFileName)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .ToList();
    }

    /// <summary>List all servers that have at least one snapshot on disk.</summary>
    public IReadOnlyList<string> ListServersWithSnapshots()
    {
        if (!Directory.Exists(_storeDir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_storeDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!.Split("__")[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Diff latest snapshot against the most recent prior snapshot. Returns empty if &lt;2 snapshots exist.</summary>
    public async Task<List<ServerDocChange>> DiffLatestAsync(string serverName, CancellationToken ct = default)
    {
        var files = EnumerateSnapshotFiles(serverName).Take(2).ToList();
        if (files.Count < 2) return new List<ServerDocChange>();

        var current = await LoadFileAsync(files[0], ct);
        var prior = await LoadFileAsync(files[1], ct);
        if (current == null || prior == null) return new List<ServerDocChange>();

        return DiffSnapshots(prior, current);
    }

    // ───────────────────── Persistence helpers ─────────────────────

    private async Task PersistAsync(ServerDocSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var fileName = $"{SafeFileName(snapshot.Server)}__{snapshot.CapturedAtUtc:yyyyMMddTHHmmss}Z.json";
            var path = Path.Combine(_storeDir, fileName);
            var tmp = path + ".tmp";
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, snapshot, _jsonOptions, ct);
            }
            File.Move(tmp, path, overwrite: true);
            _logger.LogInformation("Server doc snapshot saved → {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist server doc snapshot for {Server}", snapshot.Server);
        }
    }

    private async Task<ServerDocSnapshot?> LoadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ServerDocSnapshot>(fs, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load snapshot {Path}", path);
            return null;
        }
    }

    private IEnumerable<string> EnumerateSnapshotFiles(string serverName)
    {
        if (!Directory.Exists(_storeDir)) yield break;
        var prefix = SafeFileName(serverName) + "__";
        foreach (var f in Directory.EnumerateFiles(_storeDir, prefix + "*.json")
                                    .OrderByDescending(f => f, StringComparer.Ordinal))
            yield return f;
    }

    private void Prune(string serverName)
    {
        try
        {
            var files = EnumerateSnapshotFiles(serverName).Skip(SnapshotsToKeep).ToList();
            foreach (var f in files) File.Delete(f);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Snapshot prune failed for {Server}", serverName);
        }
    }

    private string? ResolveConnectionString(string serverName)
    {
        foreach (var conn in _connections.GetConnections())
        {
            if (conn.GetServerList().Contains(serverName, StringComparer.OrdinalIgnoreCase))
                return conn.GetConnectionString(serverName, "master");
        }
        return null;
    }

    private static string SafeFileName(string s) =>
        string.Join("_", s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    private static DateTime? ParseTimestampFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split("__");
        if (parts.Length < 2) return null;
        var ts = parts[1].TrimEnd('Z');
        return DateTime.TryParseExact(ts, "yyyyMMddTHHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt) ? dt : null;
    }

    // ───────────────────── SMO performance tune ─────────────────────

    private static void TuneInitFields(SmoServer smo)
    {
        // Pre-load just the fields we read, in one batch, to avoid N+1 round trips.
        smo.SetDefaultInitFields(typeof(Smo.Login),
            nameof(Smo.Login.Name), nameof(Smo.Login.LoginType), nameof(Smo.Login.IsDisabled),
            nameof(Smo.Login.IsLocked), nameof(Smo.Login.PasswordPolicyEnforced),
            nameof(Smo.Login.PasswordExpirationEnabled), nameof(Smo.Login.CreateDate));
        smo.SetDefaultInitFields(typeof(Smo.Database),
            nameof(Smo.Database.Name), nameof(Smo.Database.RecoveryModel), nameof(Smo.Database.Status),
            nameof(Smo.Database.Owner), nameof(Smo.Database.CompatibilityLevel), nameof(Smo.Database.Collation),
            nameof(Smo.Database.IsSystemObject), nameof(Smo.Database.CreateDate));
        smo.SetDefaultInitFields(typeof(Smo.Agent.Job),
            nameof(Smo.Agent.Job.Name), nameof(Smo.Agent.Job.IsEnabled), nameof(Smo.Agent.Job.OwnerLoginName),
            nameof(Smo.Agent.Job.LastRunOutcome), nameof(Smo.Agent.Job.LastRunDate));
        smo.SetDefaultInitFields(typeof(Smo.LinkedServer),
            nameof(Smo.LinkedServer.Name), nameof(Smo.LinkedServer.ProductName),
            nameof(Smo.LinkedServer.ProviderName), nameof(Smo.LinkedServer.DataSource));
    }

    // ───────────────────── Section builders ─────────────────────

    private static DocSection BuildInstanceSection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "instance", Title = "Instance Configuration", Icon = "fa-solid fa-server" };
        try
        {
            s.Rows.Add(new DocRow { Label = "Server name", Value = smo.Information.NetName ?? "" });
            s.Rows.Add(new DocRow { Label = "Instance", Value = smo.InstanceName ?? "(default)" });
            s.Rows.Add(new DocRow { Label = "Version", Value = smo.Information.VersionString ?? "" });
            s.Rows.Add(new DocRow { Label = "Edition", Value = smo.Information.Edition ?? "" });
            s.Rows.Add(new DocRow { Label = "Product level", Value = smo.Information.ProductLevel ?? "" });
            s.Rows.Add(new DocRow { Label = "Collation", Value = smo.Information.Collation ?? "" });
            s.Rows.Add(new DocRow { Label = "OS version", Value = smo.Information.OSVersion ?? "" });
            s.Rows.Add(new DocRow { Label = "Platform", Value = smo.Information.Platform ?? "" });
            s.Rows.Add(new DocRow { Label = "Processors", Value = smo.Information.Processors.ToString() });
            s.Rows.Add(new DocRow { Label = "Physical memory", Value = $"{smo.Information.PhysicalMemory:N0} MB" });
            s.Rows.Add(new DocRow { Label = "Login mode", Value = smo.Settings.LoginMode.ToString() });
            s.Rows.Add(new DocRow { Label = "Default backup dir", Value = smo.Settings.BackupDirectory ?? "" });

            var cfgRows = new List<List<string>>();
            foreach (Smo.ConfigProperty cfg in smo.Configuration.Properties)
            {
                cfgRows.Add(new List<string>
                {
                    cfg.DisplayName ?? "",
                    cfg.ConfigValue.ToString(),
                    cfg.RunValue.ToString(),
                    (cfg.Minimum.ToString()) + "–" + (cfg.Maximum.ToString()),
                });
            }
            s.Collections.Add(new DocCollection
            {
                Title = "sp_configure",
                Columns = new() { "Setting", "Config value", "Run value", "Range" },
                Rows = cfgRows.OrderBy(r => r[0]).ToList(),
            });

            s.SummaryLine = $"{smo.Information.Edition} · {cfgRows.Count} settings";

            if (smo.Settings.LoginMode == Smo.ServerLoginMode.Mixed)
            {
                s.Severity = DocSeverity.Attention;
                s.AttentionReasons.Add("Login mode is Mixed (SQL + Windows authentication).");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Instance: {ex.Message}");
        }
        return s;
    }

    private static DocSection BuildSecuritySection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "security", Title = "Security", Icon = "fa-solid fa-lock" };
        try
        {
            var logins = new List<Smo.Login>();
            foreach (Smo.Login l in smo.Logins) logins.Add(l);

            // sysadmin enumeration via ListMembers() can be expensive; cache once.
            var sysadminNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sa = smo.Roles["sysadmin"];
                if (sa != null)
                {
                    foreach (var m in sa.EnumServerRoleMembers())
                        sysadminNames.Add(m!);
                }
            }
            catch (Exception ex) { warnings.Add($"Security/sysadmin: {ex.Message}"); }

            var rows = logins
                .OrderByDescending(l => sysadminNames.Contains(l.Name))
                .ThenBy(l => l.Name)
                .Select(l => new List<string>
                {
                    l.Name,
                    l.LoginType.ToString(),
                    l.IsDisabled ? "disabled" : "enabled",
                    sysadminNames.Contains(l.Name) ? "sysadmin" : "",
                    l.CreateDate.ToString("yyyy-MM-dd"),
                })
                .ToList();

            s.Collections.Add(new DocCollection
            {
                Title = "Logins",
                Columns = new() { "Name", "Type", "Status", "Role", "Created" },
                Rows = rows,
            });

            s.Rows.Add(new DocRow { Label = "Total logins", Value = logins.Count.ToString() });
            s.Rows.Add(new DocRow { Label = "Sysadmin count", Value = sysadminNames.Count.ToString() });
            s.Rows.Add(new DocRow { Label = "Disabled logins", Value = logins.Count(l => l.IsDisabled).ToString() });
            s.Rows.Add(new DocRow { Label = "Windows logins", Value = logins.Count(l => l.LoginType is Smo.LoginType.WindowsUser or Smo.LoginType.WindowsGroup).ToString() });
            s.Rows.Add(new DocRow { Label = "SQL logins", Value = logins.Count(l => l.LoginType == Smo.LoginType.SqlLogin).ToString() });

            // Server audit specifications (just the names + state, not the action defs)
            var auditRows = new List<List<string>>();
            try
            {
                foreach (Smo.ServerAuditSpecification spec in smo.ServerAuditSpecifications)
                    auditRows.Add(new() { spec.Name, spec.Enabled ? "enabled" : "disabled", spec.AuditName ?? "" });
            }
            catch (Exception ex) { warnings.Add($"Security/audit: {ex.Message}"); }
            if (auditRows.Count > 0)
            {
                s.Collections.Add(new DocCollection
                {
                    Title = "Server audit specifications",
                    Columns = new() { "Name", "State", "Audit" },
                    Rows = auditRows,
                });
            }

            s.SummaryLine = $"{logins.Count} logins · {sysadminNames.Count} sysadmin";
            if (sysadminNames.Count > 5)
            {
                s.Severity = DocSeverity.Attention;
                s.AttentionReasons.Add($"{sysadminNames.Count} principals are sysadmin (typical estate has ≤ 5).");
            }
            if (logins.Any(l => l.LoginType == Smo.LoginType.SqlLogin && !l.PasswordPolicyEnforced))
            {
                s.Severity = DocSeverity.Attention;
                s.AttentionReasons.Add("One or more SQL logins do not enforce password policy.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Security: {ex.Message}");
        }
        return s;
    }

    private static DocSection BuildHaDrSection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "hadr", Title = "HA / DR", Icon = "fa-solid fa-shuffle" };
        try
        {
            var ags = new List<Smo.AvailabilityGroup>();
            try { foreach (Smo.AvailabilityGroup ag in smo.AvailabilityGroups) ags.Add(ag); }
            catch (Exception ex) { warnings.Add($"HA/DR/AGs: {ex.Message}"); }

            s.Rows.Add(new DocRow { Label = "AlwaysOn enabled", Value = smo.IsHadrEnabled ? "yes" : "no" });
            s.Rows.Add(new DocRow { Label = "Availability groups", Value = ags.Count.ToString() });

            foreach (var ag in ags)
            {
                var replicaRows = new List<List<string>>();
                try
                {
                    foreach (Smo.AvailabilityReplica r in ag.AvailabilityReplicas)
                    {
                        var connMode = r.ConnectionModeInSecondaryRole.ToString();
                        var readable = connMode.Contains("All", StringComparison.OrdinalIgnoreCase) ? "Yes"
                                     : connMode.Contains("Read", StringComparison.OrdinalIgnoreCase) ? "Read-only"
                                     : "No";
                        replicaRows.Add(new()
                        {
                            r.Name ?? "",
                            r.AvailabilityMode.ToString(),
                            r.FailoverMode.ToString(),
                            readable,
                            connMode,
                        });
                    }
                }
                catch (Exception ex) { warnings.Add($"HA/DR/{ag.Name}/replicas: {ex.Message}"); }

                s.Collections.Add(new DocCollection
                {
                    Title = $"AG: {ag.Name}",
                    Columns = new() { "Replica", "Availability mode", "Failover", "Readable secondary", "Secondary role conns" },
                    Rows = replicaRows,
                });
            }

            // Endpoints — useful for AG / mirroring / Service Broker
            var epRows = new List<List<string>>();
            try
            {
                foreach (Smo.Endpoint ep in smo.Endpoints)
                {
                    epRows.Add(new()
                    {
                        ep.Name ?? "",
                        ep.EndpointType.ToString(),
                        ep.ProtocolType.ToString(),
                        ep.EndpointState.ToString(),
                    });
                }
            }
            catch (Exception ex) { warnings.Add($"HA/DR/endpoints: {ex.Message}"); }
            if (epRows.Count > 0)
            {
                s.Collections.Add(new DocCollection
                {
                    Title = "Endpoints",
                    Columns = new() { "Name", "Type", "Protocol", "State" },
                    Rows = epRows,
                });
            }

            s.SummaryLine = ags.Count > 0
                ? $"{ags.Count} AG · {ags.Sum(a => a.AvailabilityReplicas.Count)} replicas"
                : "no AGs configured";
        }
        catch (Exception ex)
        {
            warnings.Add($"HA/DR: {ex.Message}");
        }
        return s;
    }

    private static DocSection BuildAgentSection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "agent", Title = "SQL Agent", Icon = "fa-solid fa-clock" };
        try
        {
            var jobs = new List<Smo.Agent.Job>();
            try { foreach (Smo.Agent.Job j in smo.JobServer.Jobs) jobs.Add(j); }
            catch (Exception ex) { warnings.Add($"Agent/jobs: {ex.Message}"); }

            var jobRows = jobs
                .OrderBy(j => j.Name)
                .Select(j => new List<string>
                {
                    j.Name ?? "",
                    j.IsEnabled ? "enabled" : "disabled",
                    j.OwnerLoginName ?? "",
                    j.LastRunOutcome.ToString(),
                    j.LastRunDate == DateTime.MinValue ? "" : j.LastRunDate.ToString("yyyy-MM-dd HH:mm"),
                })
                .ToList();
            s.Collections.Add(new DocCollection
            {
                Title = "Jobs",
                Columns = new() { "Name", "State", "Owner", "Last outcome", "Last run" },
                Rows = jobRows,
            });

            var opRows = new List<List<string>>();
            try
            {
                foreach (Smo.Agent.Operator op in smo.JobServer.Operators)
                    opRows.Add(new() { op.Name ?? "", op.EmailAddress ?? "", op.Enabled ? "enabled" : "disabled" });
            }
            catch (Exception ex) { warnings.Add($"Agent/operators: {ex.Message}"); }
            if (opRows.Count > 0)
            {
                s.Collections.Add(new DocCollection
                {
                    Title = "Operators",
                    Columns = new() { "Name", "Email", "State" },
                    Rows = opRows,
                });
            }

            var failed = jobs.Count(j => j.LastRunOutcome == Smo.Agent.CompletionResult.Failed);
            s.SummaryLine = $"{jobs.Count} job{(jobs.Count == 1 ? "" : "s")}" + (failed > 0 ? $" · {failed} last-failed" : "");
            if (failed > 0)
            {
                s.Severity = DocSeverity.Attention;
                s.AttentionReasons.Add($"{failed} job(s) reported Failed on their last run.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Agent: {ex.Message}");
        }
        return s;
    }

    private static DocSection BuildDatabasesSection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "databases", Title = "Databases", Icon = "fa-solid fa-database" };
        try
        {
            var dbs = new List<Smo.Database>();
            foreach (Smo.Database d in smo.Databases) dbs.Add(d);

            var rows = dbs
                .OrderBy(d => d.IsSystemObject ? 0 : 1)
                .ThenBy(d => d.Name)
                .Select(d => new List<string>
                {
                    d.Name,
                    d.IsSystemObject ? "system" : "user",
                    d.Status.ToString(),
                    d.RecoveryModel.ToString(),
                    d.CompatibilityLevel.ToString(),
                    d.Owner ?? "",
                    d.Collation ?? "",
                })
                .ToList();
            s.Collections.Add(new DocCollection
            {
                Title = "All databases",
                Columns = new() { "Name", "Kind", "Status", "Recovery", "Compat", "Owner", "Collation" },
                Rows = rows,
            });

            var user = dbs.Count(d => !d.IsSystemObject);
            var simple = dbs.Count(d => !d.IsSystemObject && d.RecoveryModel == Smo.RecoveryModel.Simple);
            s.Rows.Add(new DocRow { Label = "User databases", Value = user.ToString() });
            s.Rows.Add(new DocRow { Label = "System databases", Value = dbs.Count(d => d.IsSystemObject).ToString() });
            s.Rows.Add(new DocRow { Label = "Simple recovery", Value = simple.ToString(), Hint = "Outside of staging / scratch, Simple usually means no point-in-time recovery." });

            s.SummaryLine = $"{user} user DB · {dbs.Count(d => d.IsSystemObject)} system";
        }
        catch (Exception ex)
        {
            warnings.Add($"Databases: {ex.Message}");
        }
        return s;
    }

    private static DocSection BuildResourceSection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "resource", Title = "Resource Governor & Audits", Icon = "fa-solid fa-gauge-high" };
        try
        {
            try
            {
                var rg = smo.ResourceGovernor;
                s.Rows.Add(new DocRow { Label = "Resource Governor", Value = rg.Enabled ? "enabled" : "disabled" });
                s.Rows.Add(new DocRow { Label = "Classifier function", Value = rg.ClassifierFunction ?? "(none)" });

                var pools = new List<List<string>>();
                foreach (Smo.ResourcePool p in rg.ResourcePools)
                {
                    pools.Add(new()
                    {
                        p.Name,
                        $"{p.MinimumCpuPercentage}% / {p.MaximumCpuPercentage}%",
                        $"{p.MinimumMemoryPercentage}% / {p.MaximumMemoryPercentage}%",
                    });
                }
                if (pools.Count > 0)
                {
                    s.Collections.Add(new DocCollection
                    {
                        Title = "Resource pools",
                        Columns = new() { "Name", "CPU min/max", "Memory min/max" },
                        Rows = pools,
                    });
                }
            }
            catch (Exception ex) { warnings.Add($"Resource/governor: {ex.Message}"); }

            try
            {
                var auditRows = new List<List<string>>();
                foreach (Smo.Audit a in smo.Audits)
                {
                    auditRows.Add(new()
                    {
                        a.Name ?? "",
                        a.Enabled ? "enabled" : "disabled",
                        a.DestinationType.ToString(),
                        a.RetentionDays.ToString(),
                    });
                }
                if (auditRows.Count > 0)
                {
                    s.Collections.Add(new DocCollection
                    {
                        Title = "Audits",
                        Columns = new() { "Name", "State", "Destination", "Retention (days)" },
                        Rows = auditRows,
                    });
                }
                s.SummaryLine = $"{auditRows.Count} audit{(auditRows.Count == 1 ? "" : "s")}";

                if (auditRows.Any() && auditRows.All(r => r[1] == "disabled"))
                {
                    s.Severity = DocSeverity.Attention;
                    s.AttentionReasons.Add("All defined audits are currently disabled.");
                }
            }
            catch (Exception ex) { warnings.Add($"Resource/audits: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            warnings.Add($"Resource: {ex.Message}");
        }
        return s;
    }

    private static DocSection BuildLinkedServersSection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "linked", Title = "Linked Servers", Icon = "fa-solid fa-link" };
        try
        {
            var rows = new List<List<string>>();
            foreach (Smo.LinkedServer ls in smo.LinkedServers)
            {
                rows.Add(new()
                {
                    ls.Name ?? "",
                    ls.ProductName ?? "",
                    ls.ProviderName ?? "",
                    ls.DataSource ?? "",
                });
            }
            s.Collections.Add(new DocCollection
            {
                Title = "Linked servers",
                Columns = new() { "Name", "Product", "Provider", "Data source" },
                Rows = rows,
            });
            s.SummaryLine = rows.Count == 0 ? "none" : $"{rows.Count} linked";
        }
        catch (Exception ex)
        {
            warnings.Add($"Linked servers: {ex.Message}");
        }
        return s;
    }

    private static DocSection BuildTraceFlagsSection(SmoServer smo, List<string> warnings)
    {
        var s = new DocSection { Id = "traceflags", Title = "Trace Flags", Icon = "fa-solid fa-flag" };
        try
        {
            var flags = smo.EnumActiveGlobalTraceFlags();
            var rows = new List<List<string>>();
            if (flags?.Rows != null)
            {
                foreach (System.Data.DataRow r in flags.Rows)
                {
                    rows.Add(new()
                    {
                        r["TraceFlag"]?.ToString() ?? "",
                        (r["Status"]?.ToString() == "1") ? "ON" : "off",
                        (r["Global"]?.ToString() == "1") ? "global" : "session",
                    });
                }
            }
            s.Collections.Add(new DocCollection
            {
                Title = "Active trace flags",
                Columns = new() { "Flag", "Status", "Scope" },
                Rows = rows,
            });
            s.SummaryLine = rows.Count == 0 ? "none active" : $"{rows.Count} active";
        }
        catch (Exception ex)
        {
            warnings.Add($"Trace flags: {ex.Message}");
        }
        return s;
    }

    // ───────────────────── Diff ─────────────────────

    private static List<ServerDocChange> DiffSnapshots(ServerDocSnapshot prior, ServerDocSnapshot current)
    {
        var changes = new List<ServerDocChange>();
        var priorById = prior.Sections.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var sec in current.Sections)
        {
            if (!priorById.TryGetValue(sec.Id, out var pSec)) continue;

            // Compare ordered rows by Label.
            var pRows = pSec.Rows.ToDictionary(r => r.Label, StringComparer.OrdinalIgnoreCase);
            foreach (var r in sec.Rows)
            {
                if (pRows.TryGetValue(r.Label, out var pr))
                {
                    if (!string.Equals(pr.Value, r.Value, StringComparison.Ordinal))
                    {
                        changes.Add(new ServerDocChange
                        {
                            SectionId = sec.Id,
                            SectionTitle = sec.Title,
                            Field = r.Label,
                            OldValue = pr.Value,
                            NewValue = r.Value,
                        });
                    }
                }
            }

            // Compare collections by Title + row identity (first column).
            var pCollsByTitle = pSec.Collections.ToDictionary(c => c.Title, StringComparer.OrdinalIgnoreCase);
            foreach (var coll in sec.Collections)
            {
                if (!pCollsByTitle.TryGetValue(coll.Title, out var pColl)) continue;
                var pById = pColl.Rows.Where(r => r.Count > 0).ToDictionary(r => r[0], StringComparer.OrdinalIgnoreCase);
                var cById = coll.Rows.Where(r => r.Count > 0).ToDictionary(r => r[0], StringComparer.OrdinalIgnoreCase);

                foreach (var added in cById.Keys.Except(pById.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    changes.Add(new ServerDocChange
                    {
                        SectionId = sec.Id,
                        SectionTitle = sec.Title,
                        Field = $"{coll.Title} / {added}",
                        OldValue = "",
                        NewValue = "added",
                    });
                }
                foreach (var removed in pById.Keys.Except(cById.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    changes.Add(new ServerDocChange
                    {
                        SectionId = sec.Id,
                        SectionTitle = sec.Title,
                        Field = $"{coll.Title} / {removed}",
                        OldValue = "present",
                        NewValue = "removed",
                    });
                }
                foreach (var key in cById.Keys.Intersect(pById.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    var pRow = pById[key];
                    var cRow = cById[key];
                    if (!pRow.SequenceEqual(cRow, StringComparer.Ordinal))
                    {
                        changes.Add(new ServerDocChange
                        {
                            SectionId = sec.Id,
                            SectionTitle = sec.Title,
                            Field = $"{coll.Title} / {key}",
                            OldValue = string.Join(" · ", pRow.Skip(1)),
                            NewValue = string.Join(" · ", cRow.Skip(1)),
                        });
                    }
                }
            }
        }
        return changes;
    }
}

public class ServerDocChange
{
    public string SectionId { get; set; } = "";
    public string SectionTitle { get; set; } = "";
    public string Field { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
}
