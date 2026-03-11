# SQL Health Assessment - Deployment Guide

## System Requirements

### Minimum Requirements
- **OS:** Windows 10 (1809+) or Windows Server 2019+
- **RAM:** 4 GB
- **Disk:** 500 MB free space
- **.NET:** .NET 8.0 Runtime (included in installer)
- **SQL Server:** SQL Server 2016+ (for monitoring)

### Recommended Requirements
- **OS:** Windows 11 or Windows Server 2022
- **RAM:** 8 GB
- **Disk:** 2 GB free space (for logs and cache)
- **SQL Server:** SQL Server 2019+

---

## Installation Methods

### Method 1: Standalone Executable (Recommended for Quick Start)

1. **Download** the latest release from GitHub
2. **Extract** the ZIP file to a folder (e.g., `C:\Program Files\SqlHealthAssessment`)
3. **Run** `SqlHealthAssessment.exe`
4. **Configure** connection string on first launch

### Method 2: MSI Installer (Enterprise Deployment)

1. **Download** `SqlHealthAssessment.msi`
2. **Run** the installer with administrator privileges
3. **Follow** the installation wizard
4. **Configure** during installation or post-install

**Silent Install:**
```cmd
msiexec /i SqlHealthAssessment.msi /quiet /qn /norestart
```

**Silent Install with Custom Path:**
```cmd
msiexec /i SqlHealthAssessment.msi INSTALLDIR="C:\CustomPath" /quiet /qn /norestart
```

---

## Configuration

### Connection String Configuration

**Location:** `appsettings.json`

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=YOUR_SERVER;Database=SQLWATCH;Integrated Security=true;"
  }
}
```

**For SQL Authentication:**
```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=YOUR_SERVER;Database=SQLWATCH;User Id=sa;Password=YOUR_PASSWORD;"
  }
}
```

**Security Best Practice:** Use encrypted connection strings
```json
{
  "ConnectionStrings": {
    "SqlServer": "enc:BASE64_ENCRYPTED_STRING"
  }
}
```

### Performance Configuration

```json
{
  "QueryTimeoutSeconds": 60,
  "MaxQueryRows": 10000,
  "RefreshIntervalSeconds": 35,
  "MaxCacheSizeBytes": 524288000
}
```

### Logging Configuration

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "SqlHealthAssessment": "Debug"
    }
  }
}
```

---

## First-Time Setup

### 1. Deploy SQLWATCH Database

The application requires the SQLWATCH database on the target SQL Server.

**Option A: Automatic Deployment (Recommended)**
1. Navigate to **Database Deploy** page
2. Select target server
3. Click **Deploy SQLWATCH**
4. Wait for completion

**Option B: Manual Deployment**
1. Locate `Dacpacs\SQLWATCH.dacpac`
2. Use SQL Server Management Studio (SSMS)
3. Right-click Databases → Deploy Data-tier Application
4. Select `SQLWATCH.dacpac`

### 2. Configure Server Connections

1. Navigate to **Servers** page
2. Click **+ Add Connection**
3. Enter connection details:
   - **Name:** Display name
   - **Server Names:** Comma-separated list
   - **Authentication:** Windows or SQL
4. Click **Test Connection**
5. Click **Save**

### 3. Verify Installation

1. Navigate to **Instance Overview**
2. Select a server from dropdown
3. Verify dashboards load data
4. Check **Health** page for any issues

---

## Enterprise Deployment

### Group Policy Deployment

1. **Create** a GPO for software installation
2. **Add** `SqlHealthAssessment.msi` to the package
3. **Configure** installation options
4. **Link** GPO to target OUs
5. **Force** update: `gpupdate /force`

### SCCM/Intune Deployment

**SCCM:**
1. Import MSI into SCCM
2. Create application deployment
3. Configure detection method
4. Deploy to device collections

**Intune:**
1. Upload MSI to Intune
2. Create Win32 app
3. Configure requirements and detection
4. Assign to groups

### Centralized Configuration

**Option 1: Shared Configuration File**
```cmd
mklink "C:\Program Files\SqlHealthAssessment\appsettings.json" "\\server\share\config\appsettings.json"
```

**Option 2: Environment Variables**
```cmd
setx SQLHEALTH_CONNECTIONSTRING "Server=PROD;Database=SQLWATCH;Integrated Security=true;"
```

---

## Security Hardening

### 1. Encrypt Connection Strings

Run the encryption utility:
```cmd
SqlHealthAssessment.exe --encrypt-config
```

### 2. File System Permissions

Restrict access to application folder:
```cmd
icacls "C:\Program Files\SqlHealthAssessment" /grant "Domain Users:(RX)" /T
icacls "C:\Program Files\SqlHealthAssessment\appsettings.json" /grant "Administrators:(F)"
```

### 3. Audit Logging

Enable comprehensive audit logging:
```json
{
  "AuditLogRetention": {
    "Enabled": true,
    "RetentionDays": 90
  }
}
```

### 4. Network Security

- Use encrypted connections: `Encrypt=true` in connection string
- Validate SSL certificates: `TrustServerCertificate=false`
- Use Windows Authentication when possible

---

## Troubleshooting

### Application Won't Start

**Check:**
1. .NET 8.0 Runtime installed
2. Windows Event Log for errors
3. `logs\app-*.log` for details

**Solution:**
```cmd
# Repair .NET installation
dotnet-runtime-8.0.24-win-x64.exe /repair
```

### Connection Failures

**Check:**
1. SQL Server accessible from client
2. Firewall allows port 1433
3. SQL Server Browser running (for named instances)
4. Connection string syntax

**Test Connection:**
```cmd
sqlcmd -S SERVER_NAME -E -Q "SELECT @@VERSION"
```

### Performance Issues

**Check:**
1. Query timeout settings
2. Network latency to SQL Server
3. SQLite cache size
4. Memory usage

**Optimize:**
```json
{
  "QueryTimeoutSeconds": 120,
  "MaxCacheSizeBytes": 1073741824,
  "MaxQueryRows": 5000
}
```

### High Memory Usage

**Check:**
1. Cache size configuration
2. Number of concurrent queries
3. Result set sizes

**Solution:**
```json
{
  "MaxCacheSizeBytes": 268435456,
  "CacheEvictionHours": 12,
  "MaxQueryRows": 5000
}
```

---

## Maintenance

### Log Rotation

Logs automatically rotate daily. Retention:
- **Application Logs:** 30 days (configurable)
- **Audit Logs:** 90 days (configurable)

**Manual Cleanup:**
```cmd
forfiles /p "C:\Program Files\SqlHealthAssessment\logs" /s /m *.log /d -30 /c "cmd /c del @path"
```

### Database Maintenance

**SQLite Cache:**
- Automatic VACUUM every 4 hours
- Integrity check every 6 runs
- Manual: Navigate to **Settings** → **Maintenance**

**SQLWATCH Database:**
- Built-in retention policies
- Default: 30 days for metrics
- Configure in SQLWATCH settings

### Updates

**Check for Updates:**
1. Navigate to **About** page
2. Click **Check for Updates**
3. Download and install if available

**Manual Update:**
1. Backup `appsettings.json` and `dashboard-config.json`
2. Stop application
3. Extract new version over existing
4. Restore configuration files
5. Start application

---

## Backup and Recovery

### Backup

**Configuration:**
```cmd
xcopy "C:\Program Files\SqlHealthAssessment\*.json" "\\backup\sqlhealth\config\" /Y
```

**Audit Logs:**
```cmd
xcopy "C:\Program Files\SqlHealthAssessment\audit-logs\*" "\\backup\sqlhealth\audit\" /S /Y
```

**SQLite Cache:**
```cmd
copy "C:\Program Files\SqlHealthAssessment\SqlHealthAssessment.db" "\\backup\sqlhealth\cache\"
```

### Recovery

**Restore Configuration:**
```cmd
xcopy "\\backup\sqlhealth\config\*.json" "C:\Program Files\SqlHealthAssessment\" /Y
```

**Restore Audit Logs:**
```cmd
xcopy "\\backup\sqlhealth\audit\*" "C:\Program Files\SqlHealthAssessment\audit-logs\" /S /Y
```

---

## Uninstallation

### MSI Installer

**GUI:**
1. Control Panel → Programs and Features
2. Select "SQL Health Assessment"
3. Click Uninstall

**Silent:**
```cmd
msiexec /x {PRODUCT_CODE} /quiet /qn /norestart
```

### Standalone

1. Stop application
2. Delete installation folder
3. Remove shortcuts (if any)
4. Clean registry (optional)

**Clean Uninstall:**
```cmd
rd /s /q "C:\Program Files\SqlHealthAssessment"
rd /s /q "%APPDATA%\SqlHealthAssessment"
```

---

## Support

### Documentation
- [README](README.md) — project overview and quick start
- [Changelog](CHANGELOG.md) — version history
- [Contributing](CONTRIBUTING.md) — how to contribute

### Logs Location
- **Application:** `logs\app-YYYY-MM-DD.log`
- **Audit:** `audit-logs\audit-YYYY-MM-DD.jsonl`

### Getting Help
- GitHub Issues: https://github.com/SQLAdrian/SqlHealthAssessment/issues

---

## Appendix

### Firewall Rules

**Inbound (if hosting web interface):**
```cmd
netsh advfirewall firewall add rule name="SQL Health Assessment" dir=in action=allow protocol=TCP localport=5000
```

**Outbound (SQL Server):**
```cmd
netsh advfirewall firewall add rule name="SQL Server" dir=out action=allow protocol=TCP remoteport=1433
```

### Registry Keys

**Installation Path:**
```
HKEY_LOCAL_MACHINE\SOFTWARE\SqlHealthAssessment
```

**User Settings:**
```
HKEY_CURRENT_USER\SOFTWARE\SqlHealthAssessment
```

### File Locations

| Item | Location |
|------|----------|
| Executable | `C:\Program Files\SqlHealthAssessment\SqlHealthAssessment.exe` |
| Configuration | `C:\Program Files\SqlHealthAssessment\appsettings.json` |
| Dashboard Config | `C:\Program Files\SqlHealthAssessment\dashboard-config.json` |
| Logs | `C:\Program Files\SqlHealthAssessment\logs\` |
| Audit Logs | `C:\Program Files\SqlHealthAssessment\audit-logs\` |
| Cache | `C:\Program Files\SqlHealthAssessment\SqlHealthAssessment.db` |
| User Settings | `C:\Program Files\SqlHealthAssessment\user-settings.json` |

---

**Document Version:** 1.0
**Last Updated:** 2026-02-24
**Applies To:** SQL Health Assessment v1.0+
