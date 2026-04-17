---
layout: default
title: LiveMonitor — Free SQL Server Monitoring
---

<style>
  .hero        { text-align: center; padding: 2rem 0 1.5rem; }
  .hero h1     { font-size: 2.4rem; margin-bottom: 0.3rem; color: #00ff00; }
  .hero .sub   { font-size: 1.05rem; color: #aaa; margin-bottom: 1.4rem; }
  .btn         { display: inline-block; padding: 0.6rem 1.6rem; border: 2px solid #00ff00;
                 color: #00ff00; border-radius: 4px; text-decoration: none;
                 font-family: monospace; font-size: 1rem; margin: 0.3rem 0.4rem;
                 transition: background 0.15s, color 0.15s; }
  .btn:hover   { background: #00ff00; color: #000; text-decoration: none; }
  .btn.ghost   { border-color: #555; color: #aaa; }
  .btn.ghost:hover { background: #555; color: #fff; }
  .badge-row   { text-align: center; margin: 0.8rem 0 2rem; }
  .section-label { color: #00ff00; font-size: 0.75rem; letter-spacing: 0.15em;
                   text-transform: uppercase; margin: 2.5rem 0 0.6rem; }
  .feature-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
                  gap: 1rem; margin: 1rem 0 2rem; }
  .feature-card { border: 1px solid #333; border-radius: 6px; padding: 1rem 1.1rem;
                  background: #0d0d0d; }
  .feature-card h3 { margin: 0 0 0.4rem; font-size: 0.95rem; color: #00ff00; }
  .feature-card p  { margin: 0; font-size: 0.85rem; color: #bbb; line-height: 1.5; }
  .screenshot-row  { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
                     gap: 1rem; margin: 1rem 0 2rem; }
  .screenshot-box  { border: 1px solid #333; border-radius: 6px; background: #0d0d0d;
                     padding: 0.6rem; text-align: center; }
  .screenshot-box img  { width: 100%; border-radius: 4px; }
  .screenshot-box span { display: block; font-size: 0.78rem; color: #666;
                         margin-top: 0.4rem; font-style: italic; }
  .compare-table   { width: 100%; border-collapse: collapse; font-size: 0.88rem; margin: 1rem 0 2rem; }
  .compare-table th { color: #00ff00; border-bottom: 1px solid #333;
                      padding: 0.5rem 0.8rem; text-align: left; }
  .compare-table td { border-bottom: 1px solid #1a1a1a; padding: 0.45rem 0.8rem; color: #ccc; }
  .compare-table tr:hover td { background: #0d0d0d; }
  .yes { color: #00ff00; }
  .no  { color: #555; }
  .qs  { counter-reset: qs; list-style: none; padding: 0; margin: 1rem 0 2rem; }
  .qs li { counter-increment: qs; padding: 0.5rem 0 0.5rem 2.8rem; position: relative;
           border-left: 2px solid #1a1a1a; margin-bottom: 0.4rem; color: #ccc;
           font-size: 0.9rem; }
  .qs li::before { content: counter(qs); position: absolute; left: -1px;
                   top: 0.5rem; background: #000; border: 1px solid #00ff00;
                   color: #00ff00; width: 1.6rem; height: 1.6rem; border-radius: 3px;
                   display: flex; align-items: center; justify-content: center;
                   font-size: 0.78rem; font-weight: bold; }
  .credits-table  { width: 100%; border-collapse: collapse; font-size: 0.82rem; margin: 0.8rem 0 2rem; }
  .credits-table td { padding: 0.4rem 0.8rem; border-bottom: 1px solid #1a1a1a; color: #aaa; }
  .credits-table td:first-child { color: #00ff00; white-space: nowrap; }
  footer.site-footer { margin-top: 3rem; padding-top: 1.5rem;
                       border-top: 1px solid #1a1a1a; color: #444;
                       font-size: 0.78rem; text-align: center; }
</style>

<div class="hero">
  <h1>&gt; LiveMonitor_</h1>
  <div class="sub">Free SQL Server monitoring for Windows DBAs.<br>
  No agents. No per-server licensing. Single exe.</div>
  <a class="btn" href="https://github.com/SQLAdrian/SqlHealthAssessment/releases">⬇ Download</a>
  <a class="btn ghost" href="https://github.com/SQLAdrian/SqlHealthAssessment">GitHub Repo</a>
</div>

<div class="badge-row">
  <img src="https://img.shields.io/badge/version-0.85.2-00ff00?style=flat-square&labelColor=000" alt="Version 0.85.2">
  <img src="https://img.shields.io/badge/Platform-Windows-0078d4?style=flat-square" alt="Windows">
  <img src="https://img.shields.io/badge/SQL%20Server-2016%2B-CC2927?style=flat-square" alt="SQL Server 2016+">
  <img src="https://img.shields.io/badge/License-GPLv3-blue?style=flat-square" alt="GPL v3">
  <img src="https://img.shields.io/badge/Price-Free-00ff00?style=flat-square&labelColor=000" alt="Free">
  <img src="https://github.com/SQLAdrian/SqlHealthAssessment/actions/workflows/codeql.yml/badge.svg?style=flat-square" alt="CodeQL">
</div>

---

<div class="section-label">// what it is</div>

**LiveMonitor** is a Windows desktop application — Blazor UI running inside WPF — that monitors multiple SQL Server instances in real time. It also runs as a headless **Windows Service** for 24/7 monitoring with remote browser access.

No software is installed on your SQL Servers. Everything runs from a single exe on your workstation or a dedicated monitoring host. .NET runtime and WebView2 are bundled — nothing to install separately.

---

<div class="section-label">// demo</div>

<div style="text-align:center;margin:1rem 0 2rem;">
  <img src="demo.gif" alt="SQL Health Assessment — live demo" style="max-width:100%;border-radius:8px;border:1px solid #333;">
</div>

<div class="section-label">// screenshots</div>

<div class="screenshot-row">
  <div class="screenshot-box">
    <img src="screenshots/1-addserver.jpg" alt="Add Server — connect to any SQL Server instance in seconds">
    <span>Add Server — connect to any SQL Server instance in seconds</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/2-whats-new.jpg" alt="What's New — in-app release notes and update notifications">
    <span>What's New — in-app release notes and update notifications</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/3-wait-state.jpg" alt="Wait Stats — real-time and historical wait category analysis">
    <span>Wait Stats — real-time and historical wait category analysis</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/4-live-sessions.jpg" alt="Live Sessions — active sessions, blocking chains, top queries">
    <span>Live Sessions — active sessions, blocking chains, top queries</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/5-database-health.jpg" alt="Database Health — per-database metrics, growth trends, backup status">
    <span>Database Health — per-database metrics, growth trends, backup status</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/6-instance-overview.jpg" alt="Instance Overview — key metrics dashboard with time-series charts">
    <span>Instance Overview — key metrics dashboard with time-series charts</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/7-environment-map.jpg" alt="Environment Map — force-directed topology graph across all servers">
    <span>Environment Map — force-directed topology graph across all servers</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/8-servers-easily-monitor-50-instances-or-more.jpg" alt="Multi-Server — monitor 50+ SQL Server instances from one place">
    <span>Multi-Server — monitor 50+ SQL Server instances from one place</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/9-run-full-sql-audits.jpg" alt="Full SQL Audit — comprehensive health check across all connected instances">
    <span>Full SQL Audit — comprehensive health check across all connected instances</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/10-microsoft-sql-vulnerability-assessment.jpg" alt="Vulnerability Assessment — Microsoft SQL Assessment API with 472+ checks">
    <span>Vulnerability Assessment — Microsoft SQL Assessment API with 472+ checks</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/11-upload-results-securely-to-azure-blob-storage.jpg" alt="Azure Blob Export — securely upload audit results to Azure Blob Storage">
    <span>Azure Blob Export — securely upload audit results to Azure Blob Storage</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/12-multiple-notification-channels.jpg" alt="Notification Channels — Email, Teams, Slack, PagerDuty, ServiceNow, Webhooks, WhatsApp">
    <span>Notification Channels — Email, Teams, Slack, PagerDuty, ServiceNow, Webhooks, WhatsApp</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/13-maturity-roadmap.jpg" alt="Maturity Roadmap — 5-level DBA maturity framework across all servers">
    <span>Maturity Roadmap — 5-level DBA maturity framework across all servers</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/14-alerting.jpg" alt="Alerting — 80 alerts with IQR dynamic baselines and escalation policies">
    <span>Alerting — 80 alerts with IQR dynamic baselines and escalation policies</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/15-query-plan-viewer.jpg" alt="Query Plan Viewer — interactive graphical execution plan with per-operator cost %">
    <span>Query Plan Viewer — interactive graphical execution plan with per-operator cost %</span>
  </div>
  <div class="screenshot-box">
    <img src="screenshots/16-risk-register-compliance-mapping-ISO-SOC2-NIST.jpg" alt="Risk Register — compliance mapping to ISO 27001, SOC 2, and NIST frameworks">
    <span>Risk Register — compliance mapping to ISO 27001, SOC 2, and NIST frameworks</span>
  </div>
</div>

---

<div class="section-label">// features</div>

<div class="feature-grid">
  <div class="feature-card">
    <h3>⚡ Live Dashboards</h3>
    <p>Active sessions, blocking chains, top CPU/IO queries, wait stats — refreshing every 30 seconds. Switch servers instantly.</p>
  </div>
  <div class="feature-card">
    <h3>🔍 Execution Plan Viewer</h3>
    <p>Interactive graphical plan with per-operator cost %, predicate detail, missing index warnings, and implicit conversion flags.</p>
  </div>
  <div class="feature-card">
    <h3>🔔 Smart Alerting</h3>
    <p>69 built-in alerts with dynamic IQR baselines that learn your server's normal behaviour. 7 channels: Email, Teams, Slack, PagerDuty, ServiceNow, Webhooks, WhatsApp.</p>
  </div>
  <div class="feature-card">
    <h3>🛡️ Vulnerability Assessment</h3>
    <p>500+ security checks against your SQL Server configuration. Exportable results with severity ratings and remediation guidance.</p>
  </div>
  <div class="feature-card">
    <h3>📊 Health Checks</h3>
    <p>Quick Check (30 seconds) and Full Audit (comprehensive deep-dive). Covers configuration, security, backups, fragmentation, and missing indexes.</p>
  </div>
  <div class="feature-card">
    <h3>🗺️ Maturity Roadmap</h3>
    <p>Maps sp_Blitz and sp_triage output to a 5-level maturity framework across multiple servers. PDF export for management reporting.</p>
  </div>
  <div class="feature-card">
    <h3>🖥️ Multi-Server</h3>
    <p>Monitor all your SQL Server instances from one place. Tag servers by environment (Production, Staging, DR) and filter across views.</p>
  </div>
  <div class="feature-card">
    <h3>🔧 Windows Service Mode</h3>
    <p>Run headless for 24/7 monitoring. Access dashboards from any browser on the network via Kestrel HTTPS.</p>
  </div>
  <div class="feature-card">
    <h3>☁️ Azure Blob Export</h3>
    <p>Auto-upload audit results as CSV to Azure Blob Storage. Supports SAS tokens, User Delegation SAS, and AzCopy fallback.</p>
  </div>
  <div class="feature-card">
    <h3>🌐 Environment Map</h3>
    <p>Force-directed topology graph showing all servers, their AG relationships, and connection health at a glance.</p>
  </div>
  <div class="feature-card">
    <h3>🔐 Role-Based Access</h3>
    <p>In Service mode: Admin, Operator, and Viewer roles. OAuth login via Google or Microsoft. Desktop mode is always Admin.</p>
  </div>
</div>

---

<div class="section-label">// health checks &amp; compliance coverage</div>

<style>
  .check-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px,1fr));
                gap: 0.8rem; margin: 1rem 0 1.5rem; }
  .check-card { border: 1px solid #1e3a1e; border-radius: 6px; padding: 0.9rem 1rem;
                background: #050f05; }
  .check-card .count { font-size: 1.6rem; font-weight: bold; color: #00ff00;
                       font-family: monospace; line-height: 1; }
  .check-card .label { font-size: 0.8rem; color: #aaa; margin-top: 0.25rem; }
  .check-card .detail { font-size: 0.75rem; color: #555; margin-top: 0.4rem; line-height: 1.4; }
  .source-list { font-size: 0.82rem; color: #aaa; margin: 0.5rem 0 1.5rem;
                 padding-left: 1.2rem; line-height: 1.8; }
  .source-list li { margin: 0; }
  .source-list a { color: #00cc00; }
</style>

**472 checks** drawn from 9 industry sources, covering security hardening, performance best practices, HA/DR readiness, and configuration hygiene.

<div class="check-grid">
  <div class="check-card">
    <div class="count">240</div>
    <div class="label">Security &amp; Vulnerability</div>
    <div class="detail">Authentication, authorisation, encryption, surface area, weak passwords, auditing, data protection</div>
  </div>
  <div class="check-card">
    <div class="count">205</div>
    <div class="label">Performance</div>
    <div class="detail">Query optimiser, plan cache, CPU/memory pressure, TempDB, statistics, index health, wait stats</div>
  </div>
  <div class="check-card">
    <div class="count">178</div>
    <div class="label">Configuration</div>
    <div class="detail">Instance config, trace flags, database settings, NUMA, SQL Agent, service accounts</div>
  </div>
  <div class="check-card">
    <div class="count">40</div>
    <div class="label">HA / Backup / Recovery</div>
    <div class="detail">Availability Groups, backup strategy, recovery model, backup verification, immutable backups</div>
  </div>
</div>

**Sources &amp; frameworks:**

<ul class="source-list">
  <li><strong>Microsoft SQL Assessment API</strong> (220 checks) — the official Microsoft recommended ruleset for SQL Server</li>
  <li><strong>Microsoft Learn</strong> (96 checks) — documented best practices from Microsoft product documentation</li>
  <li><strong><a href="https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit">Brent Ozar / sp_Blitz</a></strong> (~71 checks) — community-validated checks from the SQL Server First Responder Kit</li>
  <li><strong>Microsoft SQL Assessment Plus</strong> (33 checks) — extended ruleset for enterprise configurations</li>
  <li><strong><a href="https://www.sqlskills.com">SQLSkills</a></strong> (14 checks) — checks derived from Paul Randal and Kimberly Tripp's SQL Server guidance</li>
  <li><strong>CIS Benchmark for SQL Server</strong> (6 checks) — Center for Internet Security hardening rules (TRUSTWORTHY, BUILTIN\Administrators, CONTROL SERVER)</li>
</ul>

> Checks are evaluated read-only against live DMVs — nothing is written to your SQL Server.

---

<div class="section-label">// how it compares</div>

<table class="compare-table">
  <thead>
    <tr>
      <th></th>
      <th>LiveMonitor</th>
      <th>sp_Blitz</th>
      <th>SQLWATCH</th>
      <th>SolarWinds DPA</th>
      <th>Redgate SQL Monitor</th>
      <th>Idera SQL DM</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>Cost</td>
      <td class="yes">Free</td>
      <td class="yes">Free</td>
      <td class="yes">Free</td>
      <td class="no">$$$/server</td>
      <td class="no">$$$/server</td>
      <td class="no">$$$/server</td>
    </tr>
    <tr>
      <td>Desktop UI</td>
      <td class="yes">✓ Blazor/WPF</td>
      <td class="no">SSMS only</td>
      <td class="yes">✓ Grafana</td>
      <td class="yes">✓ Web</td>
      <td class="yes">✓ Web</td>
      <td class="yes">✓ Desktop</td>
    </tr>
    <tr>
      <td>Execution plan viewer</td>
      <td class="yes">✓ Interactive</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
    </tr>
    <tr>
      <td>Alerting + notifications</td>
      <td class="yes">✓ 7 channels</td>
      <td class="no">✗</td>
      <td>Limited</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
    </tr>
    <tr>
      <td>Vulnerability assessment</td>
      <td class="yes">✓ 472 checks</td>
      <td class="yes">~250 checks</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td>Limited</td>
      <td>Limited</td>
    </tr>
    <tr>
      <td>CIS Benchmark checks</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr>
      <td>Runs as Windows Service</td>
      <td class="yes">✓</td>
      <td class="no">✗</td>
      <td>Partial</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
    </tr>
    <tr>
      <td>Agent on SQL Server</td>
      <td class="yes">Not required</td>
      <td class="yes">Not required</td>
      <td class="no">Required</td>
      <td class="no">Required</td>
      <td class="no">Required</td>
      <td class="no">Required</td>
    </tr>
    <tr>
      <td>Azure SQL MI support</td>
      <td class="yes">✓</td>
      <td>Limited</td>
      <td>Limited</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
    </tr>
    <tr>
      <td>Maturity roadmap</td>
      <td class="yes">✓ 5-level</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
    <tr>
      <td>Role-based access</td>
      <td class="yes">✓ Admin/Operator/Viewer</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
      <td class="yes">✓</td>
    </tr>
    <tr>
      <td>Open source</td>
      <td class="yes">✓ GPL v3</td>
      <td class="yes">✓ MIT</td>
      <td class="yes">✓ Apache 2</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
      <td class="no">✗</td>
    </tr>
  </tbody>
</table>

<p style="font-size:0.75rem;color:#444;margin-top:-0.5rem;">
  sp_Blitz check count per Brent Ozar Unlimited documentation (~250 T-SQL health checks).
  Commercial tool feature claims based on publicly available documentation; verify current capabilities before purchase.
</p>

---

<div class="section-label">// download</div>

<style>
  .dl-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px,1fr));
             gap: 1rem; margin: 1rem 0 1.5rem; }
  .dl-card  { border: 1px solid #1e3a1e; border-radius: 6px; padding: 1rem 1.1rem;
              background: #050f05; }
  .dl-card h3 { margin: 0 0 0.3rem; font-size: 0.95rem; color: #00ff00; }
  .dl-card p  { margin: 0 0 0.7rem; font-size: 0.82rem; color: #aaa; line-height: 1.5; }
  .dl-card a  { color: #00ff00; font-size: 0.85rem; }
  .version-note { font-size: 0.8rem; color: #555; margin: -0.5rem 0 1.5rem;
                  border-left: 2px solid #1e3a1e; padding-left: 0.8rem; }
</style>

<p class="version-note">
  Current release: <strong style="color:#aaa">v0.85.2</strong> — production-quality, actively developed.
  Suitable for production DBA use. Both options below are self-contained: .NET runtime and WebView2 are bundled.
</p>

<div class="dl-grid">
  <div class="dl-card">
    <h3>⬇ LiveMonitor-Setup.exe</h3>
    <p>Guided installer. Start menu shortcut, optional Windows Service install, upgrade detection, "Launch now" on finish. Recommended for most users.</p>
    <a href="https://github.com/SQLAdrian/SqlHealthAssessment/releases/latest">Download from Releases →</a>
  </div>
  <div class="dl-card">
    <h3>📦 LiveMonitor.zip</h3>
    <p>Extract to any folder (e.g. <code>C:\Tools\LiveMonitor</code>) and run <code>LiveMonitor.exe</code>. No installation required. Good for air-gapped or portable use.</p>
    <a href="https://github.com/SQLAdrian/SqlHealthAssessment/releases/latest">Download from Releases →</a>
  </div>
</div>

<div class="section-label">// quick start</div>

<ol class="qs">
  <li>Download from the <a href="https://github.com/SQLAdrian/SqlHealthAssessment/releases/latest">Releases page</a> — Setup.exe or ZIP</li>
  <li>Run it — no prerequisites to install separately</li>
  <li>Go to <strong>Servers → Add Server</strong>, enter your SQL Server name, click <strong>Test → Save</strong></li>
  <li>Open <strong>Live Monitor</strong> <kbd>Ctrl+2</kbd> to see live sessions, wait stats, and top queries</li>
  <li>Run a <strong>Quick Check</strong> <kbd>Ctrl+Q</kbd> for an instant health snapshot</li>
</ol>

The built-in onboarding wizard guides you through the setup on first run. SQLWATCH is optional — it unlocks additional historical dashboards but is not required to start monitoring.

**SQL Server permissions needed:** `VIEW SERVER STATE` · `VIEW DATABASE STATE` — no sysadmin, no agent install on the SQL Server.

---

<div class="section-label">// built on the shoulders of giants</div>

<table class="credits-table">
  <tr><td><a href="https://github.com/marcingminski/sqlwatch">SQLWATCH</a></td><td>Marcin Gminski — SQL Server monitoring framework and data foundation</td></tr>
  <tr><td><a href="https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit">sp_Blitz</a></td><td>Brent Ozar Unlimited — SQL Server First Responder Kit</td></tr>
  <tr><td><a href="https://sqldba.org">sp_triage</a></td><td>Adrian Sullivan — SQL health check scripts</td></tr>
  <tr><td><a href="https://github.com/erikdarlingdata/DarlingData">PerformanceMonitor</a></td><td>Erik Darling — Performance Monitor framework and diagnostic queries</td></tr>
  <tr><td><a href="https://github.com/MadeiraData/MadeiraToolbox">MadeiraToolbox</a></td><td>Eitan Blumin — SQL Server maintenance and best-practice scripts</td></tr>
  <tr><td><a href="https://github.com/microsoft/tigertoolbox">TigerToolbox</a></td><td>Pedro Lopes (Microsoft) — SQL Server tools and utilities</td></tr>
  <tr><td><a href="https://ola.hallengren.com">Ola Hallengren</a></td><td>SQL Server Maintenance Solution — industry-standard backup, integrity check, and index maintenance scripts</td></tr>
  <tr><td><a href="https://github.com/microsoft/tigertoolbox">Glenn Berry</a></td><td>SQL Server Diagnostic Information queries — DMV-based diagnostic scripts used widely in the community</td></tr>
  <tr><td><a href="https://github.com/ktaranov/sqlserver-kit">Konstantin Taranov</a></td><td>sqlserver-kit — curated SQL Server scripts and best practices collection</td></tr>
  <tr><td><a href="https://github.com/wayneSheffield">Wayne Sheffield</a></td><td>SQL Server community scripts for performance and configuration analysis</td></tr>
  <tr><td><a href="https://github.com/tboggiano">Tracy Boggiano</a></td><td>SQL Server community scripts and DBA toolkit contributions</td></tr>
  <tr><td><a href="https://github.com/timothyford">Tim Ford</a></td><td>SQL Agent Jobs and SQL Server community diagnostic scripts</td></tr>
</table>

---

<div class="section-label">// get involved</div>

- [Report a bug](https://github.com/SQLAdrian/SqlHealthAssessment/issues/new?template=bug_report.md)
- [Request a feature](https://github.com/SQLAdrian/SqlHealthAssessment/issues/new?template=feature_request.md)
- [Read the deployment guide](https://github.com/SQLAdrian/SqlHealthAssessment/blob/main/DEPLOYMENT_GUIDE.md)
- [Contributing](https://github.com/SQLAdrian/SqlHealthAssessment/blob/main/CONTRIBUTING.md)

Released under the [GNU General Public License v3.0](https://github.com/SQLAdrian/SqlHealthAssessment/blob/main/LICENSE.txt).

<footer class="site-footer">
  LiveMonitor · Free SQL Server monitoring · Built by a DBA, for DBAs
</footer>
