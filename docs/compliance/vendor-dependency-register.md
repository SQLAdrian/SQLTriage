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
| SQLitePCLRaw.bundle_e_sqlcipher | 2.1.10 (pinned exact) | SQLCipher encryption for the local SQLite stores. Ships SQLCipher 4.5.2 over SQLite 3.39.2. Automatic scanning cannot see this row: see "SQLCipher stack" below. | Apache 2.0 | 2026-09-02 |
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

Two methods run against this repository. Each is written here with its reach, because neither covers every package listed above.

1. **GitHub Dependabot.** Vulnerability alerts are enabled on the repository, verified 2026-09-02. It raises an alert where a published advisory names the package. Two alerts have ever been raised here: GHSA-hv8m-jj95-wg3x against MessagePack and GHSA-9j88-vvj5-vhgr against MailKit, both now in state `fixed`. There is no `.github/dependabot.yml`, so no automatic version-update pull requests are opened. Alerting and updating are separate features and only alerting is on.
2. **CLI scan.** From the solution root, before each release:

   ```
   dotnet restore SQLTriage.sln -r win-x64 --locked-mode
   dotnet list SQLTriage.sln package --vulnerable --include-transitive --no-restore
   ```

   Address any High or Critical finding before shipping. ⚠ `--no-restore` is not optional. Measured 2026-09-02: without it the scan runs its own restore, with no `--locked-mode` and no runtime identifier, and silently rewrites the committed lock files. On that run it floated `Azure.Storage.Blobs` from 12.29.1 to 12.29.2 and dropped the entire `net10.0-windows7.0/win-x64` target from the test project's lock, 94 lines. CI restores with `--locked-mode`, so a scan run the short way leaves a tree whose next push fails NU1004. With the two commands above the tree stays byte-identical.

**What neither method covers.** Both read the same advisory database. A package with no published advisory reports clean, and a clean report from either method is not evidence that the package is current or unaffected. Risk that lives in a vendored native library, rather than in an advisory filed against the NuGet package, is invisible to both. The SQLCipher stack is that case exactly, and it is tracked by hand in the next section.

### SQLCipher stack: what is actually monitored

**Shipped level.** `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.10 carries SQLCipher **4.5.2** over SQLite **3.39.2**. Proved 2026-09-02 two ways. The strings `4.5.2` and `3.39.2` are both embedded in the shipped native (`runtimes/win-x64/native/e_sqlcipher.dll`, 1,852,928 bytes). SQLCipher's own changelog records release 4.5.2, August 2022, as the one that moved to SQLite 3.39.2. The package version is pinned exact in `SQLTriage.csproj` because the cipher stack is version-coherence sensitive: a floated resolve once made the plain provider win and every store came back unencrypted.

**What automatic monitoring sees here: nothing.** Proved 2026-09-02 by querying the GitHub Advisory Database directly. No advisory has ever been published against `SQLitePCLRaw.bundle_e_sqlcipher` or its transitive `SQLitePCLRaw.lib.e_sqlcipher`. The SQLite defects fixed after 3.39.2 are published by SQLite, against SQLite, and no advisory maps them onto this NuGet package. So `dotnet list package --vulnerable` reports this row clean and Dependabot raises nothing on it. A clean scan is no information about this row.

**What is monitored, and by whom.** A person, on the triggers below. The residual is recorded in the internal decision log dated 2026-09-02 under the heading "CIPHER STACK RULED". That entry enumerates the SQLite CVEs open at 3.39.2 and states the reachability argument that makes the position defensible today: each one needs an attacker who can inject arbitrary SQL or who controls a database file the app opens, and neither path exists in the shipped product.

**Manual re-review triggers.** Any one of these voids the assessment above. On any of them, re-read https://sqlite.org/cves.html against the shipped SQLite level and record the outcome:

- **A new input path that SQLite reads.** Today every store the app opens is one the app wrote, at a fixed path, and a test fails closed on a store that has not been ruled on.
- **Any wiring of a method that runs caller-supplied SQL against a local store.** Two such methods existed with zero callers and were deleted on 2026-09-02. A third, narrower path was recorded here on 2026-09-03 and remained wired: a table builder that interpolated result-set column names, chosen by the panel's query, into the DDL and the INSERT column list it ran against the encrypted store. Values were parameterised and table names were sanitised; column names were not. **That surface was deleted in full on 2026-09-05**, along with its three host registrations and the desktop startup pass that ran it. `Tests/SQLTriage.Tests/LiveQueriesPanelProvisioningRemovedTests.cs` fails if any of it returns: the service by type name or by method name, its source file, or the statement shape itself on any single line of product source. Read that file for what the shape guard does and does not measure. No path in the product now builds a local DDL statement out of an identifier chosen by a monitored server.
- **Any customer-supplied file opened by SQLite.** One customer-supplied file exists, `rag.db`, and SQLite never opens it. The service that handles it checks only that the file exists and reads its size.
- **The result of the pending replacement spike**, which is comparing SQLite3 Multiple Ciphers against official Zetetic builds. Its outcome may retire this row.

**Cadence when no trigger fires.** Read the SQLite CVE list against the shipped level at each release. Log it in `docs/compliance/sign-off-log.md` as `dependency-review`.

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
