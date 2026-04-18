<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Support

## Getting Help

### Documentation

- **README**: [README.md](README.md) — Quick start and overview
- **Deployment Guide**: [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md) — Installation and configuration
- **Contributing**: [CONTRIBUTING.md](CONTRIBUTING.md) — How to contribute
- **Changelog**: [CHANGELOG.md](CHANGELOG.md) — Version history

### Common Issues

#### Connection Problems

1. Verify SQL Server is running and accessible
2. Check Windows Firewall (port 1433)
3. Enable "Trust Server Certificate" in Settings for encrypted connections
4. Review logs: `logs/app-*.log`

#### SQLWATCH Not Found

1. Deploy SQLWATCH via **Database Deploy** page
2. Or run manual scripts: `SQLWATCH_db/01_CreateSQLWATCHDB.sql` and `02_PostSQLWATCHDBcreate.sql`
3. Verify permissions: VIEW SERVER STATE, VIEW DATABASE STATE, db_owner on SQLWATCH

#### Performance Issues

1. Increase refresh interval in Settings
2. Reduce MaxQueryRows in `appsettings.json`
3. Enable rate limiting
4. Check SQL Server performance (CPU, memory, disk)

### Community Support

- **GitHub Issues**: [Report bugs or request features](https://github.com/SQLAdrian/SqlHealthAssessment/issues)
- **Discussions**: [Ask questions and share ideas](https://github.com/SQLAdrian/SqlHealthAssessment/discussions)

### Professional Support

This is a community-driven open-source project. For enterprise support, consider:

- **SQLWATCH**: [sqlwatch.io](https://sqlwatch.io)
- **Brent Ozar Unlimited**: [brentozar.com](https://www.brentozar.com)
- **Erik Darling Data**: [erikdarling.com](https://erikdarling.com)

## Reporting Issues

When reporting issues, please include:

- Application version (see About page)
- SQL Server version
- Operating system
- Steps to reproduce
- Relevant log entries from `logs/app-*.log`
- Screenshots (if applicable)

Use the [bug report template](.github/ISSUE_TEMPLATE/bug_report.yml) for structured reporting.

## Feature Requests

Have an idea? Use the [feature request template](.github/ISSUE_TEMPLATE/feature_request.yml)!

## Security Issues

**Do not report security vulnerabilities through public issues.**

See [SECURITY.md](SECURITY.md) for responsible disclosure procedures.

