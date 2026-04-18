<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.x     | :white_check_mark: |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please report them via:

1. **GitHub Security Advisories** (preferred): Use the "Security" tab in the repository
2. **Email**: Contact the maintainers directly through GitHub

### What to Include

- Type of vulnerability
- Full paths of source file(s) related to the vulnerability
- Location of the affected source code (tag/branch/commit or direct URL)
- Step-by-step instructions to reproduce the issue
- Proof-of-concept or exploit code (if possible)
- Impact of the issue

### Response Timeline

- **Initial Response**: Within 48 hours
- **Status Update**: Within 7 days
- **Fix Timeline**: Depends on severity (Critical: 7 days, High: 14 days, Medium: 30 days)

## Security Best Practices

When using SQL Health Assessment:

- **Credentials**: Use Windows Authentication when possible
- **Least Privilege**: Grant only required permissions (VIEW SERVER STATE, VIEW DATABASE STATE)
- **Network**: Use encrypted connections (TrustServerCertificate only in trusted environments)
- **Updates**: Keep the application and .NET runtime up to date
- **Logs**: Review audit logs regularly (logs/app-*.log)
- **Access**: Restrict application access to authorized DBAs only

## Known Security Features

- DPAPI credential encryption (Windows Data Protection API)
- Parameterized queries (SQL injection prevention)
- Comprehensive audit logging
- Rate limiting for query execution
- No plain-text password storage

