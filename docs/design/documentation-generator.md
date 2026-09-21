# Documentation Generator + Installation Helper — Design Doc

**Status:** Scaffold only. All methods throw `NotImplementedException`. Next Opus session fills in the bodies.

---

## 1. Goal

Generate a complete, exportable server documentation package from live SQL Server DMV data — covering instance configuration, hardware summary, AG topology, backup history, and security posture — plus a hardening checklist that grades each best-practice setting as pass/fail with remediation guidance. Output can be exported to PDF via the existing `PrintService`. An optional dbatools.io REST integration supplements findings with community-maintained checks.

---

## 2. Data Sources

Inject these into `DocumentationService` (replace the minimal logger-only constructor in the skeleton):

| Dependency | Reason |
|---|---|
| `IDbConnectionFactory` | Open connections per server |
| `ServerConnectionManager` | Resolve server name → connection string |
| `SqlAssessmentService` | Reuse existing security/config check infrastructure |
| `IPrintService` | PDF export in step 5 |
| `IConfiguration` | Read `DbaTools:ApiUrl` optional setting |
| `ILogger<DocumentationService>` | Already present |

No new packages. `HttpClient` for dbatools REST is instantiated directly (mirror `NotificationChannelService.cs` line 44: `new HttpClient { Timeout = TimeSpan.FromSeconds(15) }`).

---

## 3. Method Implementation Steps

### 3a — `GenerateServerDocumentationAsync`

1. Open `DbConnection` via `IDbConnectionFactory.CreateConnection()`.
2. Query `sys.configurations` for all rows; filter to those where `value_in_use <> value` or `value_in_use <> minimum` to show non-default settings. Map to `ConfigRow`.
3. Query `sys.dm_os_sys_info`: `cpu_count`, `physical_memory_kb`. Divide KB → MB. Query `@@VERSION` for `SqlVersion`.
4. Query `sys.dm_os_volume_stats` joined across all user databases for unique volumes. Map to `DiskVolumeRow`.
5. Query `sys.availability_groups ag JOIN sys.availability_replicas ar ON ag.group_id = ar.group_id JOIN sys.dm_hadr_availability_replica_states rs ON ar.replica_id = rs.replica_id`. Map to `AgReplicaRow`. Return empty list if no AGs (wrap in try/catch — SQL error if not Enterprise).
6. Query `msdb.dbo.backupset`: for each `database_name`, select `MAX(backup_finish_date)` grouped by `type` ('D' = Full, 'I' = Diff, 'L' = Log). Map to `BackupSummaryRow`.
7. Security findings — run these discrete checks and produce `SecurityFindingRow` entries:
   - `SA` login enabled: `SELECT is_disabled FROM sys.server_principals WHERE name = 'sa'`
   - Mixed auth: `SELECT SERVERPROPERTY('IsIntegratedSecurityOnly')`
   - `xp_cmdshell` enabled: `SELECT value_in_use FROM sys.configurations WHERE name = 'xp_cmdshell'`
   - Sysadmin count: `SELECT COUNT(*) FROM sys.server_role_members WHERE role_principal_id = SUSER_ID('sysadmin')`
8. Assemble and return `ServerDocumentation` record. Log elapsed time.

### 3b — `RunInstallationChecklistAsync`

1. Open `DbConnection`.
2. For each item in a hardcoded checklist table (compile-time `IReadOnlyList<CheckSpec>`), run a single scalar query and compare to expected value. Populate `InstallationHelperItem`.
3. Minimum checklist items (expand in code, not design doc):
   - MAXDOP (0 = fail, >8 = warn, NUMA-appropriate = pass)
   - Cost threshold for parallelism (<50 = fail)
   - Backup compression default (0 = fail)
   - `xp_cmdshell` (1 = fail)
   - SA login disabled (is_disabled = 0 → fail)
   - Mixed auth mode (0 = pass)
   - Linked server count (>0 = warn)
   - CLR enabled (1 = warn unless intentional)
   - Remote admin connections (0 = warn)
   - Instant file initialization (detected via `sys.dm_server_services` or `DBCC TRACEON`)
4. Return `List<InstallationHelperItem>` sorted: failures first, then warnings, then passes.

### 3c — `RunDbaToolsChecksAsync`

1. Read `IConfiguration["DbaTools:ApiUrl"]`. If null or empty, log debug message and return `new List<DbaToolsFinding>()`.
2. Build `HttpRequestMessage POST {ApiUrl}/api/v1/test-dbainstance` with JSON body `{ "server": serverName }`.
3. Deserialise response array. Each element maps to `DbaToolsFinding` (map `id` → `CheckId`, `name` → `CheckName`, `severity` → `Severity`, `description` → `Finding`, `remediation` → `Remediation`).
4. Catch all exceptions, log warning, return empty list. dbatools is best-effort and optional.

---

## 4. dbatools.io REST Endpoint Shape

dbatools.io does not currently expose a public REST API (as of 2026-05). The intended integration is with a self-hosted dbatools REST wrapper (e.g. `dbatools-web` community project or a PowerShell-based shim). When that endpoint is available, it should return:

```json
[
  {
    "id": "DBAT0001",
    "name": "SA login should be disabled",
    "severity": "High",
    "description": "The SA login is enabled and poses a brute-force risk.",
    "remediation": "ALTER LOGIN [sa] DISABLE"
  }
]
```

`DocumentationService._httpClient` should follow the pattern at `NotificationChannelService.cs:44`:

```csharp
private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
```

Dispose in a finalizer or implement `IAsyncDisposable` on the service if the client is long-lived.

---

## 5. PDF Export Wiring

`IPrintService.PrintHtmlToPdfAsync` (see `Data/Services/IPrintService.cs:34`) accepts an HTML string and an output path. The Razor page should:

1. Serialise the `ServerDocumentation` record to a Razor-component-generated HTML string (use `HtmlRenderer` from `Microsoft.AspNetCore.Components` — already available in Blazor Server mode).
2. Pass that HTML to `PrintHtmlToPdfAsync(html, outputPath, ct)`.
3. Offer the resulting file as a download via `IJSRuntime` or open it with `Process.Start`.

The "Export PDF" button on `/documentation` is already scaffolded (disabled). Wire it up after step 2 is complete.

---

## 6. Test Plan

Use the `SQLTriage.Tests` project. Mirror the fixture pattern from `AuditLogServiceTests.cs` (in-memory SQLite via `liveQueriesCacheStore`).

- **`DocumentationService_GenerateServerDocumentation_ReturnsPopulatedRecord`**: mock `IDbConnectionFactory` to return a test `DataReader` for each DMV query; assert all sections non-empty.
- **`DocumentationService_RunInstallationChecklist_FailsForXpCmdshell`**: inject a mock that returns `1` for the xp_cmdshell query; assert the corresponding item has `Passed = false`.
- **`DocumentationService_RunDbaToolsChecks_ReturnsEmptyWhenUnconfigured`**: pass `IConfiguration` with no `DbaTools:ApiUrl`; assert empty list, no exception.
- **`DocumentationService_RunDbaToolsChecks_MapsResponseCorrectly`**: mock `HttpClient` (via `HttpMessageHandler` override) to return a JSON array; assert finding count and field mapping.

---

## 7. Pickup-Ready Checklist for Next Opus Session

Before starting, verify:

- [ ] `Data/Services/DocumentationService.cs` exists and compiles (skeleton)
- [ ] `Pages/Documentation.razor` at `/documentation` renders without error
- [ ] `Pages/InstallationHelper.razor` at `/installation-helper` renders without error
- [ ] `IDocumentationService` is registered in `Data/ServiceCollectionExtensions.cs`
- [ ] Route constants `Documentation` and `InstallationHelper` are in `Data/RouteConstants.cs`
- [ ] NavMenu entries exist inside `@if (_experimentalMode)` in the Setup section

Implementation order:

1. Replace skeleton constructor with full DI constructor (step 3a above).
2. Implement `GenerateServerDocumentationAsync` (steps 3a.1–3a.8).
3. Build and smoke-test: navigate to `/documentation`, pick a server, click Generate.
4. Implement `RunInstallationChecklistAsync` (step 3b). Smoke-test against a real SQL Server.
5. Wire up PDF export (step 5).
6. Implement `RunDbaToolsChecksAsync` (step 3c). Test with `DbaTools:ApiUrl` absent.
7. Write unit tests (step 6).
8. Enable the nav entries unconditionally once feature is stable (remove `_experimentalMode` gate).

Adrian decision needed before step 5: confirm whether PDF export should open the file in the OS viewer, save to a user-chosen path, or trigger a browser download in Blazor Server fallback mode.
