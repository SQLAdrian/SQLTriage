# Vendor and Dependency Register

**Control mapping:** SOC2 CC9.2 (third-party risk) · FedRAMP SA-12 (supply chain protection)
**Why this exists:** Auditors require evidence that the team knows what external packages and vendors the system depends on, that licenses are understood, and that CVEs are monitored. Without this, CC9.2 is a finding.

Replace `{{placeholder}}` values before first audit. Review this document annually and ad-hoc on: new vendor onboarding, new package addition, or CVE alert against any listed package.

---

## NuGet Package Dependencies

Sourced from `SQLTriage.csproj`. All versions are as pinned in the project file.

| Package | Version | Purpose | License | Last Reviewed |
|---|---|---|---|---|
| MailKit | 4.16.0 | SMTP/IMAP email notification delivery | MIT | {{YYYY-MM-DD}} |
| Microsoft.AspNetCore.Authentication.Google | 8.0.* | OAuth2 sign-in via Google identity | MIT | {{YYYY-MM-DD}} |
| Microsoft.AspNetCore.Authentication.MicrosoftAccount | 8.0.* | OAuth2 sign-in via Microsoft identity | MIT | {{YYYY-MM-DD}} |
| Microsoft.AspNetCore.Components.WebView.Wpf | 8.0.* | Blazor Hybrid WPF host (WebView2 bridge) | MIT | {{YYYY-MM-DD}} |
| Microsoft.SqlServer.DacFx | 162.* | SQL Server schema / DAC operations | {{TBD - confirm license}} | {{YYYY-MM-DD}} |
| Blazor-ApexCharts | 3.* | Time-series and bar chart rendering in Blazor | MIT | {{YYYY-MM-DD}} |
| Radzen.Blazor | 5.* | UI component library (grids, dialogs, inputs) | MIT | {{YYYY-MM-DD}} |
| Microsoft.Data.SqlClient | 6.* | SQL Server connection and query driver | MIT | {{YYYY-MM-DD}} |
| Microsoft.SqlServer.Management.Assessment | 1.* | SQL Best Practices Assessment API | {{TBD - confirm license}} | {{YYYY-MM-DD}} |
| Microsoft.SqlServer.SqlManagementObjects | 172.* | SQL Server Management Object model (SMO) | {{TBD - confirm license}} | {{YYYY-MM-DD}} |
| Microsoft.Data.Sqlite | 8.0.* | Local SQLite cache store | MIT | {{YYYY-MM-DD}} |
| SQLitePCLRaw.bundle_e_sqlcipher | 2.1.10 | SQLCipher encryption bundle for SQLite | Apache 2.0 | {{YYYY-MM-DD}} |
| Microsoft.Extensions.Configuration.Json | 8.0.* | JSON configuration provider (.NET hosting) | MIT | {{YYYY-MM-DD}} |
| Microsoft.Extensions.DependencyInjection | 8.0.* | Dependency injection container | MIT | {{YYYY-MM-DD}} |
| Polly | 8.4.2 | Resilience / circuit-breaker / retry policies | BSD-3-Clause | {{YYYY-MM-DD}} |
| Serilog | 4.* | Structured logging framework | Apache 2.0 | {{YYYY-MM-DD}} |
| Serilog.Extensions.Logging | 8.* | Microsoft.Extensions.Logging bridge for Serilog | Apache 2.0 | {{YYYY-MM-DD}} |
| Serilog.Sinks.File | 6.* | File sink for Serilog | Apache 2.0 | {{YYYY-MM-DD}} |
| Serilog.Sinks.Console | 6.* | Console sink for Serilog | Apache 2.0 | {{YYYY-MM-DD}} |
| Microsoft.Extensions.Hosting.WindowsServices | 8.0.* | Windows Service hosting integration | MIT | {{YYYY-MM-DD}} |
| Azure.Storage.Blobs | 12.* | Azure Blob Storage client (audit export) | MIT | {{YYYY-MM-DD}} |
| QuestPDF | 2024.10.4 | PDF report generation | QuestPDF Community License (free for revenue <1M USD/yr — confirm eligibility annually) | {{YYYY-MM-DD}} |
| MessagePack | 2.5.187 | Binary serialisation for cache payloads | MIT | {{YYYY-MM-DD}} |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | Argon2id password hashing (RBAC credentials) | MIT | {{YYYY-MM-DD}} |

### CVE Monitoring

Two complementary methods — run both before each release and on any CVE disclosure affecting listed packages:

1. **GitHub Dependabot** — enabled on the repository; raises PRs automatically on known CVEs.
2. **CLI scan** — run `dotnet list package --vulnerable` from the solution root. Address any High or Critical findings before shipping.

### Review Cadence

Annual minimum. Log completion in `docs/compliance/sign-off-log.md` with entry type `dependency-review`.

---

## External Vendor List

| Vendor | Role | Data shared | SOC2 / compliance cert | Last reviewed |
|---|---|---|---|---|
| Microsoft | .NET runtime, SQL Server platform, Azure Blob Storage hosting | Audit export files written to customer-controlled Azure Blob container | SOC2 Type II, FedRAMP (Azure) — [Microsoft Trust Center](https://www.microsoft.com/en-us/trust-center) | {{YYYY-MM-DD}} |
| GitHub (Microsoft) | Source code hosting, CI, Dependabot CVE scanning | Source code only; no production data | SOC2 Type II | {{YYYY-MM-DD}} |
| Azure Blob Storage | Audit log export destination | Audit CSV/export files; customer controls the storage account | Covered under Microsoft Azure (above) | {{YYYY-MM-DD}} |
| Anthropic | Claude API — used during development and for optional LLM-assisted features | Development prompts; no production customer data unless explicitly integrated | {{TBD - confirm SOC2 status at time of review}} | {{YYYY-MM-DD}} |
| {{additional-vendor}} | {{role}} | {{data-shared}} | {{cert-or-TBD}} | {{YYYY-MM-DD}} |

### Vendor Review Cadence

Annual minimum, plus ad-hoc on: new vendor onboarding, vendor security incident, or material change to vendor's data-handling terms.

---

## QuestPDF License Note

QuestPDF Community License is free for organisations with annual revenue below USD 1 million. Above that threshold a commercial licence is required. Confirm eligibility at each annual review and record the outcome in the sign-off log.
