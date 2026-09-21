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

## Outbound Network Calls (Egress Inventory)

Production SQL Servers this app connects to typically have **no egress** (see
`DEPLOYMENT_GUIDE.md`) - this inventory is about calls the *app itself* makes, from the machine
it runs on, not calls it asks a monitored SQL Server to make. Every entry below was found by
grepping `HttpClient`/`GetStringAsync`/`GetAsync`/`PostAsync` usage in non-test source; if a call
site is added, add a row here.

| Call | Where | Trigger | Off switch |
|---|---|---|---|
| GitHub release check (`api.github.com/repos/.../releases/latest`) | `Data/AutoUpdateService.cs` (`CheckForUpdatesAsync`) | Once per app start, 15s after the shell loads (`MainLayout.OnInitializedAsync` → `StartBackgroundCheck`); also re-run on demand from Settings → Updates → "Check for Updates" | `Updates:Enabled=false` (config kill-switch; also toggleable from Settings → Updates as of this change - restart required) |
| Update ZIP + `.sig` download (GitHub release asset URLs) | `Data/AutoUpdateService.cs` (`DownloadUpdateAsync`) | Operator clicks "Download & Apply on Exit" in Settings after a check finds a newer version - never automatic | Same `Updates:Enabled`; also gated on a signature being present at all |
| Script-update check (`api.github.com/repos/.../contents/scripts`) + download | `Data/AutoUpdateService.cs` (`CheckForScriptUpdatesAsync`, `DownloadScriptUpdatesAsync`) | Operator-triggered only (script-update feature) | Same `Updates:Enabled`; see "Residual risk" above - keep this feature off in client builds |
| WebView2 Evergreen runtime installer (`go.microsoft.com/fwlink/...`) | `Data/WebView2Helper.cs` (`TryInstallWebView2Async`) | Operator clicks "Install" on the WebView2-missing error screen (WPF desktop mode only; not reachable in Server mode) | No config toggle - it only fires from that explicit button, and only when the runtime is absent |
| SMTP via Microsoft Graph API (`login.microsoftonline.com` token, then `graph.microsoft.com/v1.0/.../sendMail`) | `Data/Services/NotificationChannelService.cs` | An alert fires and a Graph-mode SMTP channel is configured | Per-channel `Enabled` flag in `config/notification-channels.json` (Settings/Alerting Config UI); unconfigured channels never call out |
| Generic webhook POST (operator-supplied URL) | `Data/Services/NotificationChannelService.cs` | An alert fires and a webhook channel is configured | Per-channel `Enabled` flag, as above |
| PagerDuty Events API (`events.pagerduty.com/v2/enqueue`) | `Data/Services/NotificationChannelService.cs` | An alert fires and a PagerDuty channel is configured | Per-channel `Enabled` flag, as above |
| WhatsApp / Meta Graph API (`graph.facebook.com/.../messages`) | `Data/Services/NotificationChannelService.cs` | An alert fires and a WhatsApp channel is configured | Per-channel `Enabled` flag, as above |
| Portal daily-summary / export upload (Azure Blob Storage, SAS-scoped) | `Data/Services/Portal/*` (`OutboxUploader`, `SasIntakeBlobUploader`, `PortalPublishRunner`) | Licence-gated portal feature; compiled out of the community build. Uses the `Azure.Storage.Blobs` SDK, not `HttpClient` directly, so it is outside this grep's exact match but is a real egress path | No portal enrolment (no client id / intake SAS configured) - inert with nothing configured; feature is licence-bound (DECISIONS 2026-08-05 §5) |
| Portal enrolment redemption (`IPortalEnrolmentClient.RedeemAsync`) | `Data/Services/Portal/IPortalEnrolmentClient.cs` | Would run once per install, only when a signed bundle carries an `enrolmentToken` and no credential is stored yet | **Not currently live** - no implementation is registered in DI (`PortalServiceRegistration.cs` resolves it via `GetService`, not `GetRequiredService`); the interface exists but ships with no transport behind it |
| SMTP (direct, non-Graph) | `Data/Services/NotificationChannelService.cs` (`MailKit.Net.Smtp`) | An alert fires and a direct-SMTP channel is configured | Per-channel `Enabled` flag, as above. Uses raw SMTP sockets, not `HttpClient` - listed here for completeness even though it falls outside the grep this table is built from |
| Loopback self-test (`http://localhost:{port}/_server/health`) | `Data/Services/ServerModeService.cs` | Once, right after Server mode starts Kestrel | Not egress - never leaves the machine; listed for completeness |

**What this inventory is not:** a runtime allow-list or a firewall rule. It is a read of the
source at the point this table was written; a code change that adds a new outbound call site
makes this table stale until it is updated.

