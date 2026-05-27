/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

// ────────────────────────────────────────────────────────────────────────────
//  POCOs
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Full documentation snapshot for one SQL Server instance.
/// Populated by <see cref="IDocumentationService.GenerateServerDocumentationAsync"/>.
/// Fields intentionally simple so they serialise cleanly to PDF via PrintService.
/// </summary>
public sealed record ServerDocumentation
{
    /// <summary>Server name as stored in ServerConnectionManager.</summary>
    public string ServerName { get; init; } = "";

    /// <summary>UTC timestamp when this snapshot was generated.</summary>
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    // ── Instance config (sp_configure rows that differ from default) ──────
    /// <summary>sp_configure key/value pairs where running_value differs from default.</summary>
    public List<ConfigRow> InstanceConfig { get; init; } = new();

    // ── Hardware / OS ─────────────────────────────────────────────────────
    /// <summary>Total logical CPU count from sys.dm_os_sys_info.</summary>
    public int CpuCount { get; init; }

    /// <summary>Total physical RAM (MB) from sys.dm_os_sys_info.</summary>
    public long PhysicalMemoryMb { get; init; }

    /// <summary>SQL Server max memory setting (MB).</summary>
    public long MaxServerMemoryMb { get; init; }

    /// <summary>SQL Server edition and version string.</summary>
    public string SqlVersion { get; init; } = "";

    // ── Disk / data files ─────────────────────────────────────────────────
    /// <summary>Logical volumes visible to SQL Server (sys.dm_os_volume_stats).</summary>
    public List<DiskVolumeRow> DiskVolumes { get; init; } = new();

    // ── AG status ─────────────────────────────────────────────────────────
    /// <summary>Availability Group replicas from sys.availability_replicas + dm_hadr_availability_replica_states.</summary>
    public List<AgReplicaRow> AgReplicas { get; init; } = new();

    // ── Backup history ────────────────────────────────────────────────────
    /// <summary>Most recent full / diff / log backup per database (msdb.dbo.backupset).</summary>
    public List<BackupSummaryRow> BackupSummary { get; init; } = new();

    // ── Security findings ─────────────────────────────────────────────────
    /// <summary>High-level security findings (sysadmins, orphaned users, xp_cmdshell, etc.).</summary>
    public List<SecurityFindingRow> SecurityFindings { get; init; } = new();
}

/// <summary>One sp_configure row.</summary>
public sealed record ConfigRow(string Name, object? RunningValue, object? DefaultValue, string Description);

/// <summary>One logical disk volume visible to SQL Server.</summary>
public sealed record DiskVolumeRow(string VolumePath, long TotalMb, long AvailableMb, int UsedPercent);

/// <summary>One AG replica row.</summary>
public sealed record AgReplicaRow(string GroupName, string ReplicaServer, string Role, string SyncState, bool IsLocal);

/// <summary>Most-recent backup per database per type.</summary>
public sealed record BackupSummaryRow(string DatabaseName, string BackupType, DateTime? LastBackupUtc, double SizeMb);

/// <summary>One security finding.</summary>
public sealed record SecurityFindingRow(string Category, string Finding, string Severity, string? Recommendation);

// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One item in the hardening / installation checklist produced by
/// <see cref="IDocumentationService.RunInstallationChecklistAsync"/>.
/// </summary>
public sealed record InstallationHelperItem
{
    /// <summary>Short name (e.g. "Instant File Initialization enabled").</summary>
    public string CheckName { get; init; } = "";

    /// <summary>Category bucket (e.g. "Security", "Performance", "Setup").</summary>
    public string Category { get; init; } = "";

    /// <summary>Whether the check passed.</summary>
    public bool Passed { get; init; }

    /// <summary>Current detected value on this server.</summary>
    public string? CurrentValue { get; init; }

    /// <summary>Best-practice recommended value.</summary>
    public string? RecommendedValue { get; init; }

    /// <summary>Link or short explanation of why this matters.</summary>
    public string? Guidance { get; init; }
}

// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One finding returned by the optional dbatools.io REST integration
/// (see design doc § 4: dbatools.io REST endpoint shape).
/// </summary>
public sealed record DbaToolsFinding
{
    /// <summary>Check identifier as returned by the dbatools API (e.g. "DBAT0001").</summary>
    public string CheckId { get; init; } = "";

    /// <summary>Human-readable check name.</summary>
    public string CheckName { get; init; } = "";

    /// <summary>Severity level: Critical, High, Medium, Low, Informational.</summary>
    public string Severity { get; init; } = "";

    /// <summary>Short description of the finding.</summary>
    public string Finding { get; init; } = "";

    /// <summary>dbatools recommended remediation action.</summary>
    public string? Remediation { get; init; }

    /// <summary>Raw JSON payload from the dbatools API response, for debugging.</summary>
    public string? RawJson { get; init; }
}

// ────────────────────────────────────────────────────────────────────────────
//  Interface
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Generates server documentation packages and installation / hardening
/// checklists from live SQL Server DMV data.
/// See <c>docs/design/documentation-generator.md</c> for the full implementation spec.
/// </summary>
public interface IDocumentationService
{
    /// <summary>
    /// Builds a complete <see cref="ServerDocumentation"/> snapshot for the
    /// given server: sp_configure values, hardware summary, AG status, backup
    /// history, and high-level security findings.
    /// </summary>
    /// <param name="serverName">Key matching ServerConnectionManager.GetConnections().</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ServerDocumentation> GenerateServerDocumentationAsync(string serverName, CancellationToken ct = default);

    /// <summary>
    /// Runs the hardening checklist for the given server and returns each
    /// check with pass/fail status and best-practice guidance.
    /// </summary>
    /// <param name="serverName">Key matching ServerConnectionManager.GetConnections().</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<InstallationHelperItem>> RunInstallationChecklistAsync(string serverName, CancellationToken ct = default);

    /// <summary>
    /// Calls the dbatools.io REST endpoint (when configured) and maps the
    /// response to a list of <see cref="DbaToolsFinding"/> records.
    /// Returns an empty list when the endpoint is not configured.
    /// </summary>
    /// <param name="serverName">Server to check — passed as a query parameter to the API.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<DbaToolsFinding>> RunDbaToolsChecksAsync(string serverName, CancellationToken ct = default);
}

// ────────────────────────────────────────────────────────────────────────────
//  Implementation skeleton
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Skeleton implementation. All methods throw <see cref="NotImplementedException"/>
/// with a reference to the step in docs/design/documentation-generator.md
/// that covers the implementation detail.
///
/// Inject: IDbConnectionFactory, ServerConnectionManager, IPrintService,
///         SqlAssessmentService, ILogger&lt;DocumentationService&gt;,
///         IConfiguration (for dbatools endpoint URL).
/// </summary>
public sealed class DocumentationService : IDocumentationService
{
    // TODO (step 1 in design doc): inject IDbConnectionFactory, ServerConnectionManager,
    //      IPrintService, SqlAssessmentService, ILogger, IConfiguration.
    // TODO (step 1): create a private HttpClient field (mirror NotificationChannelService.cs:44 pattern)
    //               for the dbatools REST call. No new packages — System.Net.Http.

    private readonly ILogger<DocumentationService> _logger;

    /// <summary>
    /// Temporary minimal constructor so DI does not break at startup.
    /// Replace with full constructor per design doc step 1.
    /// </summary>
    public DocumentationService(ILogger<DocumentationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<ServerDocumentation> GenerateServerDocumentationAsync(string serverName, CancellationToken ct = default)
    {
        // v2: see docs/design/documentation-generator.md step 2
        // Steps:
        //   2a. Open connection via IDbConnectionFactory
        //   2b. Run sp_configure (non-default rows only)
        //   2c. Query sys.dm_os_sys_info for CPU / RAM
        //   2d. Query sys.dm_os_volume_stats for all user databases
        //   2e. Query sys.availability_replicas + dm_hadr_availability_replica_states
        //   2f. Query msdb.dbo.backupset for last backup per db per type
        //   2g. Run security checks (sysadmin count, xp_cmdshell, SA login, mixed auth)
        //   2h. Assemble and return ServerDocumentation record
        throw new NotImplementedException("v2: see docs/design/documentation-generator.md step 2");
    }

    /// <inheritdoc/>
    public Task<List<InstallationHelperItem>> RunInstallationChecklistAsync(string serverName, CancellationToken ct = default)
    {
        // v2: see docs/design/documentation-generator.md step 3
        // Steps:
        //   3a. Open connection via IDbConnectionFactory
        //   3b. For each checklist item, run its DMV query and compare result to expected value
        //   3c. Populate InstallationHelperItem(Passed, CurrentValue, RecommendedValue, Guidance)
        //   3d. Return ordered list (failures first)
        // Checklist items include: IFI, trace flags, max DOP, cost threshold, backup compression,
        //   SA login status, mixed auth mode, linked servers count, CLR enabled, xp_cmdshell
        throw new NotImplementedException("v2: see docs/design/documentation-generator.md step 3");
    }

    /// <inheritdoc/>
    public Task<List<DbaToolsFinding>> RunDbaToolsChecksAsync(string serverName, CancellationToken ct = default)
    {
        // v2: see docs/design/documentation-generator.md step 4
        // Steps:
        //   4a. Read dbatools API base URL from IConfiguration["DbaTools:ApiUrl"]
        //   4b. If null/empty, return empty list (feature is optional)
        //   4c. POST to {ApiUrl}/api/v1/test-dbainstance with serverName as JSON body
        //   4d. Deserialise response array → List<DbaToolsFinding>
        //   4e. Log errors and swallow (dbatools is best-effort)
        // Mirror the HttpClient pattern in NotificationChannelService.cs line 44
        throw new NotImplementedException("v2: see docs/design/documentation-generator.md step 4");
    }
}
