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

When using SQLTriage:

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
- **Signed software updates** (see below)

## Software Update Integrity

The in-app updater downloads a release ZIP and applies it over the install. To prevent a
compromised update channel (release, DNS, CDN, or a hostile proxy) from delivering malicious
code, updates are cryptographically verified:

- **Detached signature.** Each release ships a `<zip>.sig` signature over the SHA-256 of the
  ZIP, produced by the SQLTriage code-signing private key. The app embeds only the matching
  **public** key (`Resources/update-signing-public.pem`) and verifies every download against it.
- **Hard fail, no override.** A missing, malformed, or invalid signature aborts the update and
  deletes the download. There is no user bypass — this applies to both the automatic and the
  manual (air-gap) update paths. The signature is re-verified at apply time to close any
  swap-on-disk window.
- **Endpoint pinning.** The update endpoint is pinned to a compile-time host; a tampered
  `version.json` cannot repoint the updater elsewhere.
- **Kill-switch.** `Updates:Enabled` in `config/appsettings.json` (default `true`) disables the
  entire update subsystem. Client/test builds ship it `false`.
- **Authenticode.** Release executables are Authenticode-signed with the same certificate.

### Residual risk — script updates (tracked)

The optional "script update" feature pulls `*.sql` files from the GitHub `scripts/` folder.
These files are **not individually signed**; the feature is gated only by `Updates:Enabled`.
Builds shipped to clients run with `Updates:Enabled=false`, so the path is inert. A future
release will sign a scripts manifest (or remove the live-pull feature) before it is enabled in
client builds. Do not enable script updates in a client/production build until then.

