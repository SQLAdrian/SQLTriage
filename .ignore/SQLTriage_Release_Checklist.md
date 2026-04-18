<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# SQLTriage v1.0 Release Readiness Checklist

**Target Release:** SQLTriage v1.0.0  
**Target Date:** [TBD — after all tasks complete]  
**Status:** Pre-release validation plan  
**Last Updated:** 2026-04-18

---

## 📋 Release Philosophy

**Do NOT release until ALL items are green.**  
This tool will be judged on first impression. One broken thing = lost user + bad review.

**Gate policy:** No task is "done" until verified in this checklist.

---

## 🔴 PHASE 1: BUILD QUALITY (BLOCKER)

### Build & Compile
- [ ] `dotnet build SQLTriage.sln -c Release` completes with **ZERO warnings**
- [ ] All CA1416 warnings suppressed or fixed (platform compatibility)
- [ ] All CS8602/CS8604 null warnings resolved
- [ ] Submodule (PerformanceStudio) builds without errors
- [ ] Published single-file EXE runs without missing DLL errors
- [ ] EXE size reasonable (<150 MB self-contained)

### Code Quality
- [ ] Unit test project `SQLTriage.Tests/` exists
- [ ] Test coverage ≥ 80% (measured by Coverlet)
- [ ] All unit tests pass locally
- [ ] CI/CD pipeline passes on `main` branch without manual intervention
- [ ] No `TODO` or `FIXME` comments in production code
- [ ] No `Console.WriteLine` or `Debug.WriteLine` left in production code

### Dependencies
- [ ] All NuGet packages are stable versions (no pre-release)
- [ ] `DeltaCompressionDotNet` compatibility confirmed (or replaced)
- [ ] License compatibility verified (GPL v3 compliance if including submodule)

---

## 🟠 PHASE 2: BRAND & DOCS (BLOCKER)

### Brand Consistency
- [ ] Solution file renamed: `SQLTriage.sln`
- [ ] Project file renamed: `SQLTriage.csproj`
- [ ] Output assembly: `SQLTriage.dll` / `SQLTriage.exe`
- [ ] All UI strings updated (search for "Health Assessment", "SQLTriage")
- [ ] Title bar shows "SQLTriage v1.0.0"
- [ ] About dialog shows correct version and branding
- [ ] Favicon and app icon replaced with SQLTriage logo
- [ ] No SqlHealthAssessment references in visible UI (can remain in comments/logs)

### README.md
- [ ] Title: **SQLTriage — SQL Server Monitoring & Governance Tool**
- [ ] Tagline: *Reduce audit preparation from days to minutes.*
- [ ] Badges: version 1.0.0, build date, license (GPL v3)
- [ ] Screenshot links all work (16 images render)
- [ ] Quick Start section matches actual first-run experience
- [ ] All file paths correct (Config/appsettings.json, not root)
- [ ] No "coming soon" placeholders
- [ ] MSI installer section accurate (or removed if not ready)
- [ ] WebView2 section clearly states "optional — server mode fallback works"

### DEPLOYMENT_GUIDE.md
- [ ] All steps tested on clean Windows 11 VM
- [ ] Prerequisites section accurate (WebView2 optional, .NET 8 bundled)
- [ ] MSI install instructions match actual MSI (if built)
- [ ] Silent install command verified: `msiexec /i SQLTriage.msi /quiet /qn`
- [ ] GPO section either accurate or removed if templates not ready
- [ ] Service mode installation steps verified
- [ ] Uninstall instructions provided

### QUICKSTART.md
- [ ] Step-by-step from download to first dashboard
- [ ] Includes both WebView2 and Server Mode paths
- [ ] Permission checker guidance included
- [ ] SQLWATCH deployment explained as optional
- [ ] All screenshots current (match actual UI)

### Website (docs/index.md)
- [ ] H1: "SQLTriage — Lightweight SQL Server Monitoring & Governance Tool"
- [ ] Value prop bullets match v1.0 features
- [ ] Screenshots directory: `wwwroot/screenshots/` has all 16 images
- [ ] Feature comparison table accurate (vs SolarWinds, Redgate)
- [ ] Download button links to correct GitHub release
- [ ] No references to "v0.85" or old branding
- [ ] "Sample Governance Report" PDF link works
- [ ] Meta description: "SQLTriage is a lightweight, agentless SQL Server monitoring tool..."

---

## 🟡 PHASE 3: INSTALLER & DEPLOYMENT

### MSI Installer (if releasing MSI)
- [ ] MSI file: `SQLTriage-v1.0.0-win-x64.msi`
- [ ] Installs to `C:\Program Files\SQLTriage\` by default
- [ ] Add/Remove Programs entry shows correct name/version/publisher
- [ ] Silent install works: `msiexec /i SQLTriage.msi /quiet /qn`
- [ ] Uninstall removes all files and registry keys cleanly
- [ ] Upgrade path: v0.85.2 → v1.0.0 upgrades without manual uninstall
- [ ] Windows Service install option available and tested
- [ ] File associations (if any) registered correctly
- [ ] No hardcoded paths (uses `[ProgramFilesFolder]` etc.)

### Zip Distribution (alternative)
- [ ] ZIP file: `SQLTriage-v1.0.0-win-x64.zip`
- [ ] Extracts to `SQLTriage-1.0.0/` folder
- [ ] `SQLTriage.exe` runs directly from extracted folder
- [ ] No missing DLL errors on fresh Windows 11 VM
- [ ] README.txt inside zip with 3-step quick start

### Code Signing (deferrable to v1.0.1)
- [ ] EV or OV code signing certificate installed
- [ ] EXE and MSI signed with valid timestamp
- [ ] `signtool verify /pa SQLTriage.exe` returns "Successfully verified"
- [ ] GitHub release assets signed (if workflow configured)
- [ ] SmartScreen reputation building plan documented (if not signed)

### Prerequisites Handling
- [ ] WebView2 detection graceful (shows fallback message, not error)
- [ ] .NET 8 Desktop Runtime check (if not self-contained)
- [ ] Clear error if prerequisites missing with fix instructions

---

## 🟢 PHASE 4: CORE FEATURES

### Governance Dashboard (Task #17)
- [ ] Page loads at `/governance` (or `/dashboard/governance`)
- [ ] Executive Snapshot displays: Risk Level, Maturity %, Downtime Exposure, Audit Readiness, Cost Optimization
- [ ] Values populated from actual server data (not dummy data)
- [ ] Risk Register table shows real findings with RiskId (R-01, R-03, etc.)
- [ ] Each risk links back to VA check or health check
- [ ] Dashboard refreshes without errors on connection change
- [ ] Load time < 2 seconds with 50-server dataset
- [ ] No JavaScript errors in browser console

### Governance Report Export (Task #18)
- [ ] "Export Governance Report (PDF)" button present on Governance page
- [ ] PDF generates successfully (no exceptions)
- [ ] PDF contains all 3 sections: Executive Snapshot, Drift/Security, Action Plan
- [ ] PASS/WARN/FAIL status colors correct (green/amber/red)
- [ ] "Cost of Downtime" calculator works (user enters revenue/hr)
- [ ] PDF filename: `SQLTriage-Governance-Report-{Server}-{Date}.pdf`
- [ ] PDF size reasonable (<500 KB)
- [ ] Sample report PDF exists at `wwwroot/samples/` and downloads

### Error Messages (Task #19)
- [ ] All MessageBox.Show calls replaced with structured error dialogs
- [ ] Every error shows: What → Why → Fix → Governance Impact
- [ ] Copy error details button present on all error dialogs
- [ ] Errors logged to file with full stack trace
- [ ] Tested: SQL Agent stopped shows compliance gap message
- [ ] Tested: Permission denied shows exact T-SQL grant command

### AI/ML Predictive Capacity Planning (Task #23)
- [ ] `PredictiveService` loads ONNX models from `Data/Models/` without errors
- [ ] Capacity page (`/capacity`) renders with 7-day forecast chart (dashed line + confidence band)
- [ ] Dashboard forecast widget shows CPU, blocking, storage trends with color-coded indicators
- [ ] Anomaly detection fires for metrics > 3σ from baseline (verified on historical data)
- [ ] Predictive alerts integrate with `NotificationChannelService` (email/Teams test passed)
- [ ] ONNX model files versioned and documented (`manifest.json` present)
- [ ] Fallback behavior: service degrades gracefully if model missing
- [ ] Forecast accuracy validated: CPU MAPE < 15% on holdout set

### Onboarding Flow (Task #20)
- [ ] First launch (no servers) redirects to `/onboarding`
- [ ] Step 1: Network scan finds local SQL instances (or manual entry option)
- [ ] Step 2: SQLWATCH deployment toggle present and tested
- [ ] Step 3: Confirmation page with "Go to SQLTriage" button
- [ ] Onboarding skip works after completion
- [ ] Total time to first data ≤ 5 minutes (user testing)
- [ ] No manual JSON editing required during onboarding

### Permission Checker (Task #21)
- [ ] "Test Connection" failure shows specific missing permissions
- [ ] T-SQL grant commands displayed with copy button
- [ ] Works for: VIEW SERVER STATE, VIEW DATABASE STATE, db_owner
- [ ] Handles Azure SQL differences (no sysadmin)
- [ ] Detection accurate (no false positives/negatives)

### Settings Page — No Manual JSON (Task #22)
- [ ] All config keys from `appsettings.json` have UI controls
- [ ] Changes saved without restart (hot reload where possible)
- [ ] Numeric inputs have min/max validation
- [ ] Tooltips explain each setting
- [ ] JSON editor (if kept) validates before save
- [ ] "Reset to Default" button works for dashboard config

### Manual JSON Editing Elimination
**Verify by grep:** No user-facing instructions say "edit appsettings.json"
- [ ] README does not tell users to edit JSON
- [ ] In-app help does not say "edit config file"
- [ ] All configuration via Settings UI only

---

## 🔵 PHASE 5: ENTERPRISE READINESS

### RBAC Implementation (Task #25)
- [ ] Roles table in SQLite: Admin, DBA, ReadOnly, Auditor, Operator
- [ ] User management page (`/admin/users`) works
- [ ] Page guards active: non-Admins cannot access `/admin/*`
- [ ] Auditors can view audit log but not change settings
- [ ] Operators can ack alerts but not modify config
- [ ] Password hashing uses CredentialProtector (not plaintext)
- [ ] Role changes audit-logged with who/when
- [ ] Exportable user access report (for auditors)

### AD/LDAP Integration (Task #26)
- [ ] "Use Windows Authentication" button on login page
- [ ] LDAP bind works with domain credentials
- [ ] AD group to role mapping configurable
- [ ] Failed auth attempts logged
- [ ] Fallback to local auth if AD unavailable (configurable)

### GPO ADMX Templates (Task #27)
- [ ] ADMX and ADML files in `Deployment/ADMX/`
- [ ] MSI includes ADMX/ADML in install
- [ ] GPO import tested: `gpupdate /force` applies registry keys
- [ ] Registry keys read at startup: `HKLM\Software\Policies\SQLTriage\`
- [ ] Policies override user settings ( tested )
- [ ] Deployment guide documents GPO deployment step-by-step

### Tamper-Proof Audit Logging (Task #28)
- [ ] AuditLog table has PreviousHash + CurrentHash + Signature columns
- [ ] Each insert calculates HMAC chain correctly
- [ ] `VerifyIntegrity()` returns false if log tampered
- [ ] App refuses to start if audit log tamper detected (option to continue with warning)
- [ ] Audit export includes signatures (for forensic verification)
- [ ] Optional SIEM forwarder configurable

### CI/CD Pipeline (Task #29)
- [ ] GitHub Actions workflow passes on every push to `main`
- [ ] Submodule checkout uses `actions/checkout@v4` with `submodules: recursive`
- [ ] PerformanceStudio pinned to specific commit SHA (no `--remote`)
- [ ] Build produces artifacts: SQLTriage-v1.0.0-win-x64.zip
- [ ] Artifacts uploaded to GitHub release automatically on tag push
- [ ] Optional: MSI artifact included if built
- [ ] Code coverage report generated (if tests present)

### Unit Test Suite (Task #30)
- [ ] `SQLTriage.Tests/` project builds
- [ ] Tests cover:
  - [ ] `RateLimiter` logic
  - [ ] `UserSettingsService` get/set
  - [ ] `CredentialProtector` encrypt/decrypt roundtrip
  - [ ] `DashboardConfigService.Validate()` valid/invalid cases
  - [ ] At least one service with mocked DB connection
- [ ] `dotnet test` passes locally
- [ ] CI runs tests and fails on test failure
- [ ] Coverage report ≥ 80% (lines)
- [ ] Tests run in < 2 minutes

---

## 🟣 PHASE 6: UX POLISH

### Rate Limit Badge (Task #7)
- [ ] Badge visible in bottom-right of MainLayout
- [ ] Shows "⚠ throttled" when rate limit active
- [ ] Updates in real-time via timer or event
- [ ] Does not block UI

### Background Refresh (Task #6)
- [ ] Sessions table never blanks during refresh
- [ ] Spinner icon appears next to refresh timestamp
- [ ] User can scroll while data loading
- [ ] No race conditions with rapid refresh clicks

### Dashboard JSON Validation (Task #8)
- [ ] JSON editor in DashboardEditor shows red border on invalid JSON
- [ ] Error message displays line/column of parse error
- [ ] Save button disabled while JSON invalid
- [ ] "Reset to Default" button restores `dashboard-config.default.json`
- [ ] Default config file shipped in `Config/` folder

### PDF/Excel Export (Task #9)
- [ ] Full Audit page has "Export PDF" and "Export CSV" buttons
- [ ] Vulnerability Assessment page has export buttons
- [ ] Exported CSV opens in Excel with proper column headers
- [ ] PDF matches PrintService styling (headers, page breaks)
- [ ] Exported file names include server name and timestamp

### FAQ + Support (Task #13)
- [ ] QUICKSTART.md has expanded FAQ section
- [ ] In-app Help page links to GitHub Discussions
- [ ] About page has "Report a bug" button (opens GitHub issues)
- [ ] FAQ covers: alert tuning, SQLWATCH optional, service mode, credential migration, log location

### Query Plan Version Probe (Task #14)
- [ ] ONLINE checkbox disabled for SQL < 2005 or Express edition
- [ ] RESUMABLE checkbox disabled for SQL < 2017
- [ ] Azure SQL detected and options hidden
- [ ] Version display in UI (e.g., "SQL Server 2019" shown)

### UI Theme Refresh — Rolls Royce (Task #24)
- [ ] Tailwind CSS installed, configured (`tailwind.config.js` with custom palette)
- [ ] Google Fonts imported (Playfair Display for headings, Inter for body)
- [ ] Glassmorphism effects applied (MainLayout sidebar, cards: `backdrop-blur` + semi-transparent backgrounds)
- [ ] All buttons use Tailwind classes with `transition-all duration-300 ease-out`
- [ ] Data tables updated: row striping, hover elevation (`hover:bg-bg-tertiary`), sticky headers
- [ ] Charts themed with custom colors, anti-aliased curves, smooth gradient fills
- [ ] Core pages (Dashboard, Governance, Settings, Onboarding) fully migrated to new theme
- [ ] No console errors, all animations smooth at 60fps
- [ ] Legacy `app.css` still loads (fallback), but new theme overrides via Tailwind

---

## 🦊 PHASE 7: ADVANCED FEATURES (POST-V1.0)

These are NOT required for v1.0 release. Document as "coming in v1.1":

- [ ] draw.io/SVG export of Environment View (Task #15)
- [ ] SQL Server CPU & Latency Benchmark (Task #16)
- [ ] Documentation Generator + Installation Helper (Task #31)
- [ ] Linux/macOS support (cross-platform)
- [ ] Docker container
- [ ] Kubernetes Helm chart
- [ ] Cloud-native features (Azure/AWS monitoring)
- [ ] RBAC: AD group mapping (basic works, advanced later)
- [ ] Custom dashboard marketplace

---

## 🧪 BETA VALIDATION

Before GA release, run beta with **5-10 external DBAs**:

### Beta Tester Checklist
Each tester must:
- [ ] Download and install on clean Windows 11 VM
- [ ] Complete onboarding without support requests
- [ ] Add at least 2 SQL Server instances
- [ ] View Governance Dashboard and interpret results
- [ ] Export Governance Report PDF
- [ ] Trigger at least one error and verify helpful message
- [ ] Run Quick Check and Full Audit
- [ ] Provide feedback via GitHub Discussions

### Beta Success Criteria
- [ ] 0 critical bugs reported (showstoppers)
- [ ] ≤ 2 medium bugs (workarounds exist)
- [ ] All testers reach "first data" in ≤ 10 minutes
- [ ] All testers understand Governance Dashboard meaning
- [ ] No confusion about WebView2 vs Server Mode
- [ ] Installer/uninstaller clean on all tester machines

---

## 🚀 FINAL LAUNCH GATES

### Gate 1: Code Complete
- [ ] All v1.0 scope tasks from WORKFILE_remaining.md completed
- [ ] No open PRs against `main` branch
- [ ] `main` branch builds cleanly and passes all tests

### Gate 2: Documentation Complete
- [ ] README accurate and complete
- [ ] DEPLOYMENT_GUIDE reflects actual v1.0
- [ ] QUICKSTART tested by someone unfamiliar with project
- [ ] Website (docs/index.md) updated to SQLTriage branding
- [ ] Sample Governance Report PDF uploaded
- [ ] CHANGELOG.md written (new file)

### Gate 3: Release Artifacts Ready
- [ ] GitHub release draft created with:
  - [ ] SQLTriage-v1.0.0-win-x64.zip
  - [ ] SQLTriage-v1.0.0-win-x64.msi (if ready)
  - [ ] Release notes (what's new, known issues, upgrade notes)
  - [ ] CHANGELOG.md linked
- [ ] Release assets tested by downloading fresh install
- [ ] Version number consistent everywhere (EXE, About, README, release tag)

### Gate 4: Marketing Assets Ready
- [ ] Homepage (sqldba.org) updated with SQLTriage copy
- [ ] 3-5 screenshots with current UI uploaded
- [ ] "Sample Governance Report" link working
- [ ] Social media posts drafted (Twitter, Reddit, LinkedIn)
- [ ] dev.to / SQLServerCentral article ready (optional but recommended)

### Gate 5: Support Infrastructure
- [ ] GitHub Discussions enabled
- [ ] Issue templates present (bug report, feature request)
- [ ] Code of conduct file present
- [ ] Contributing.md (if accepting PRs)
- [ ] License file (GPL v3) present and correct

---

## 📊 SUCCESS METRICS

**Launch Week Targets:**
- Downloads: 100+
- GitHub Stars: 25+
- Issues opened: ≤ 3 (non-critical)
- Discord/Discussions active users: 10+
- Positive mentions on Reddit (r/SQLServer)

**30-Day Targets:**
- Downloads: 500+
- GitHub Stars: 100+
- Active installations (pings/telemetry if added): 200+
- Zero critical security bugs reported
- At least 1 external contribution (PR or issue with fix)

---

## ⚠️ POST-LAUNCH HOTFIX PROCESS

If critical bug found in v1.0.0:

1. Create hotfix branch from `main`: `git checkout -b hotfix/v1.0.1`
2. Fix bug, add test, commit
3. Bump patch version in `Config/version.json`: `"1.0.1"`
4. Merge to `main` via PR
5. Create release: `git tag v1.0.1 && git push origin v1.0.1`
6. GitHub Actions builds and publishes v1.0.1
7. Update release notes with "Critical bug fix" section
8. Announce on Discussions / social channels

**No hotfixes without:**
- Regression test added
- Issue reference in commit message (`Fixes #123`)
- UAT by at least 1 beta tester before publish

---

## 🗓️ RELEASE DAY CHECKLIST

**T-24 Hours:**
- [ ] Pre-draft GitHub release (private)
- [ ] Notify beta testers of upcoming launch
- [ ] Prepare social media posts (schedule for launch time)

**Launch Day (T=0):**
- [ ] Merge last PR to `main`
- [ ] Verify CI passes on final commit
- [ ] Create GitHub release (public)
- [ ] Upload assets
- [ ] Publish release notes
- [ ] Post to r/SQLServer (during US business hours)
- [ ] Post to r/sysadmin
- [ ] Post to LinkedIn (targeted at DBA managers)
- [ ] Tweet / Boost on social platforms
- [ ] Update sqldba.org homepage with download links

**T+24 Hours:**
- [ ] Monitor Discussions for questions
- [ ] Triage any issues opened
- [ ] Thank early adopters publicly
- [ ] Collect feedback for v1.0.1 planning

---

## 🎯 SIGN-OFF

| Role | Name | Sign-off Date | Notes |
|------|------|---------------|-------|
| Lead Developer | [Name] | [ ] | Code quality gate |
| QA Lead | [Name] | [ ] | All tests passed |
| Product Owner | [Name] | [ ] | Feature complete per PRD |
| Release Manager | [Name] | [ ] | Artifacts verified |

**Release approved when all 4 sign-offs complete.**

---

**Document version:** 1.0  
**Next update:** After v1.0 launch → lessons learned for v1.1 planning

