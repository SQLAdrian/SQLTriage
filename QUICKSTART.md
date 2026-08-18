<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Quick Start Guide

## First Time Setup

1. Download SQLTriage from [GitHub Releases](https://github.com/SQLAdrian/SQLTriage/releases)
2. Run the installer or extract the ZIP
3. Launch SQLTriage
4. Complete the onboarding wizard

## Adding Your First Server

> Before you start: ensure the SQL account has the minimum permissions SQLTriage needs.
> See [Minimum-Privilege SQL Login](docs/min-privilege-sql-login.md) for the T-SQL setup script.

1. Go to **Servers** page
2. Click **Add Server**
3. Enter your SQL Server instance name (e.g., `SERVERNAME` or `SERVERNAME\INSTANCENAME`)
4. Enter credentials if not using Windows Authentication
5. Click **Test Connection**
6. Click **Save**

## Basic Monitoring

- **Live Monitor** (`Ctrl+2`): Real-time sessions, wait stats, blocking chains
- **Quick Check** (`Ctrl+Q`): 30-second health snapshot
- **Full Audit**: Comprehensive diagnostic scripts

## Frequently Asked Questions

### Alerting

**Q: Why is my alert firing constantly?**

A: Alerts use IQR (Interquartile Range) baselines that learn your server's normal behavior over time. If you're seeing constant alerts:

1. Check the **Alerting → Alert History** page to see what's triggering
2. Adjust the alert threshold in **Settings → Alerting**
3. Use the "dry-run mode" in alert configuration to test without notifications
4. The baseline adapts automatically, but you can reset it in **Alerting → Baselines**

**Q: How do I adjust the alert delay?**

A: In **Settings → Alerting**, set `NextAlertDelayMinutes` to prevent alert spam. This creates a cooldown period after each alert fires.

### SQLWATCH Integration

**Q: Is SQLWATCH required?**

A: No, SQLWATCH is optional. Most features work without it:
- Live Monitor: Works without SQLWATCH
- Quick Check: Works without SQLWATCH
- Full Audit: Works without SQLWATCH
- Historical dashboards: Requires SQLWATCH for trend data

To enable historical data, deploy SQLWATCH via the **Database Deploy** page.

### Service Mode Setup

**Q: How do I run SQLTriage as a Windows Service?**

A: During installation, check "Install Windows Service". Or manually:

1. Open command prompt as Administrator
2. Navigate to SQLTriage installation folder
3. Run: `SQLTriage.exe --install-service`
4. Start the service: `net start SQLTriage`
5. Access via browser at `http://localhost:5000` (default port)

The service runs headless with web access for remote monitoring.

### Credential Management

**Q: How do I migrate credentials between machines?**

A: Use the credential export/import feature:

1. Go to **Settings → Server Credentials**
2. Click **Export Credentials**
3. Save the `.lmcreds` file (PBKDF2 encrypted)
4. On the new machine, click **Import Credentials**
5. Enter the passphrase to decrypt

This securely transfers all server connections.

### Debugging and Logs

**Q: Where are the log files located?**

A: Logs are in `logs/app-YYYYMMDD.log` next to the SQLTriage.exe file.

For debugging:
- Enable debug logging in **Settings → General**
- Check the log file for detailed error information
- Include relevant log entries when reporting issues

## Support

- **Documentation**: [Full docs](https://github.com/SQLAdrian/SQLTriage/tree/main/docs)
- **Issues**: [Report bugs](https://github.com/SQLAdrian/SQLTriage/issues)
- **Discussions**: [Community help](https://github.com/SQLAdrian/SQLTriage/discussions)
- **Report a Bug**: [Bug report template](https://github.com/SQLAdrian/SQLTriage/issues/new?template=bug_report.md)

## Next Steps

- Explore the **Environment View** for topology visualization
- Set up **Alerting** for automated notifications
- Run **Vulnerability Assessment** for security checks
- Configure **Dashboard Editor** for custom layouts</content>
<parameter name="filePath">QUICKSTART.md