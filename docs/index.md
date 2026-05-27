---
layout: default
title: SQLTriage — Open SQL Server Audit & Compliance Assessment
description: 600+ framework-mapped audit checks for SQL Server. NIST, CIS, STIG, ISO 27001, SOC 2, PCI-DSS, HIPAA. The auditable open alternative to Microsoft SQL Vulnerability Assessment.
---
<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

<section class="hero">
  <div class="container hero-inner">
    <span class="hero-eyebrow">Audit · Compliance · Performance</span>
    <h1>The audit your CIO can actually defend.</h1>
    <p class="lede">
      <strong>600+ framework-mapped checks</strong> across NIST 800-53, CIS Benchmarks, DISA STIG,
      ISO 27001, SOC 2, PCI-DSS, HIPAA. The open alternative to Microsoft SQL Vulnerability
      Assessment — deeper coverage, every finding cited, every control traceable.
    </p>
    <div class="cta-row">
      <a class="btn btn-primary" href="https://github.com/SQLAdrian/SQLTriage/releases">Download for Windows</a>
      <a class="btn btn-ghost" href="https://github.com/SQLAdrian/SQLTriage">View on GitHub</a>
    </div>
    <div class="hero-meta">
      <div class="hero-meta-item">
        <span class="num">600+</span>
        <span class="label">Audit checks</span>
      </div>
      <div class="hero-meta-item">
        <span class="num">8</span>
        <span class="label">Compliance frameworks</span>
      </div>
      <div class="hero-meta-item">
        <span class="num">0</span>
        <span class="label">Agent installations</span>
      </div>
      <div class="hero-meta-item">
        <span class="num">$0</span>
        <span class="label">Per-server licensing</span>
      </div>
    </div>
  </div>
</section>

<section class="section" id="what-it-is">
  <div class="container">
    <div class="section-eyebrow">// what it is</div>
    <h2 class="section-title">A SQL Server audit tool that produces evidence, not opinions.</h2>
    <p class="section-lede">
      SQLTriage runs read-only audits across your SQL Server estate, maps every finding to the
      specific clauses your auditor cares about, and emits a board-ready PDF with every
      citation traceable to a published source. While the audit is running, it doubles as a
      monitoring dashboard — but the audit is the headline.
    </p>
    <div class="feature-grid">
      <div class="card">
        <h3>Framework-mapped findings</h3>
        <p>Each finding cites the specific NIST 800-53, CIS, STIG, ISO 27001, SOC 2, PCI-DSS, HIPAA, or NIS2 control it satisfies. No generic "configuration management" labels.</p>
      </div>
      <div class="card">
        <h3>Evidence-grade citations</h3>
        <p>Every check links to its authoritative source — Microsoft Learn, Brent Ozar, SQLSkills, the CIS Benchmark itself. Auditors verify; we cite.</p>
      </div>
      <div class="card">
        <h3>Maturity roadmap</h3>
        <p>5-level DBA maturity model. See exactly where each instance sits and the next step to advance.</p>
      </div>
      <div class="card">
        <h3>Boardroom PDF</h3>
        <p>Cover page, executive summary, framework coverage table, integrity hash, signature page. Ready for the audit committee, not a wall of YAML.</p>
      </div>
      <div class="card">
        <h3>Read-only by default</h3>
        <p>Every check is a SELECT against DMVs. No writes, no schema changes, no agent on the SQL Server. Connect, scan, leave.</p>
      </div>
      <div class="card">
        <h3>Live monitoring companion</h3>
        <p>While the audit is queued, watch wait stats, blocking chains, query plans, and dashboards. The "while you wait" surface, not the headline.</p>
      </div>
    </div>
  </div>
</section>

<section class="section" id="frameworks">
  <div class="container">
    <div class="section-eyebrow">// frameworks</div>
    <h2 class="section-title">Eight industry frameworks. Per-control evidence.</h2>
    <p class="section-lede">
      Every check declares which framework controls it satisfies — not as a topical label, but
      as a direct match between the failure mode and the control's actual published requirement.
    </p>
    <div class="framework-grid">
      <span class="fw-chip"><strong>NIST</strong>800-53 Rev 5</span>
      <span class="fw-chip"><strong>CIS</strong>SQL Server Benchmark</span>
      <span class="fw-chip"><strong>DISA</strong>STIG</span>
      <span class="fw-chip"><strong>ISO</strong>27001:2022</span>
      <span class="fw-chip"><strong>SOC 2</strong>Trust Services Criteria</span>
      <span class="fw-chip"><strong>PCI-DSS</strong>v4.0</span>
      <span class="fw-chip"><strong>HIPAA</strong>Security Rule</span>
      <span class="fw-chip"><strong>NIST CSF</strong>2.0</span>
    </div>
    <div class="panel accent">
      <p><strong>The CIO question:</strong> "When my auditor pulls finding 47 at random and looks up the cited NIST control, will the control text actually say what we claim it says?"</p>
      <p>That question shaped the entire check corpus. Mappings are reviewed against the framework's published control language — not topical adjacency, not template reuse.</p>
    </div>
  </div>
</section>

<section class="section" id="checks">
  <div class="container">
    <div class="section-eyebrow">// checks</div>
    <h2 class="section-title">600+ checks across security, performance, configuration, and recovery.</h2>
    <p class="section-lede">Drawn from authoritative sources, validated end-to-end on SQL Server 2017 and 2022.</p>
    <div class="stat-grid">
      <div class="stat-tile">
        <div class="num">240+</div>
        <div class="label">Security &amp; vulnerability</div>
        <div class="detail">Authentication, authorisation, encryption, surface area, weak passwords, auditing, data protection</div>
      </div>
      <div class="stat-tile">
        <div class="num">205+</div>
        <div class="label">Performance</div>
        <div class="detail">Query optimiser, plan cache, memory pressure, TempDB, statistics, index health, wait stats</div>
      </div>
      <div class="stat-tile">
        <div class="num">178+</div>
        <div class="label">Configuration</div>
        <div class="detail">Instance config, trace flags, database settings, NUMA, SQL Agent, service accounts</div>
      </div>
      <div class="stat-tile">
        <div class="num">40+</div>
        <div class="label">HA / Backup / Recovery</div>
        <div class="detail">Availability Groups, backup strategy, recovery model, backup verification, immutable backups</div>
      </div>
    </div>
    <h3 style="margin-top:2rem;">Check sources</h3>
    <ul class="source-list">
      <li><strong>Microsoft SQL Assessment API</strong> <span class="count">— 220 checks</span> — the official Microsoft recommended ruleset for SQL Server</li>
      <li><strong>Microsoft Learn</strong> <span class="count">— 96 checks</span> — documented best practices from Microsoft product documentation</li>
      <li><strong><a href="https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit">Brent Ozar / sp_Blitz</a></strong> <span class="count">— ~71 checks</span> — community-validated checks from the SQL Server First Responder Kit</li>
      <li><strong>Microsoft SQL Assessment Plus</strong> <span class="count">— 33 checks</span> — extended ruleset for enterprise configurations</li>
      <li><strong><a href="https://www.sqlskills.com">SQLSkills</a></strong> <span class="count">— 14 checks</span> — Paul Randal and Kimberly Tripp's SQL Server guidance</li>
      <li><strong>CIS Benchmark for SQL Server</strong> <span class="count">— 6+ checks</span> — Center for Internet Security hardening rules</li>
    </ul>
    <p class="muted" style="margin-top:1rem;">Checks are evaluated read-only against live DMVs — nothing is written to your SQL Server.</p>
  </div>
</section>

<section class="section" id="how-it-compares">
  <div class="container">
    <div class="section-eyebrow">// how it compares</div>
    <h2 class="section-title">SQLTriage vs the audit-first alternatives.</h2>
    <p class="section-lede">
      Most "SQL Server audit" tools fall into one of three buckets: Microsoft's own VA (narrow security
      scope), open community scripts (no framework mapping, no UI), or commercial monitoring suites
      (audit isn't the focus). SQLTriage is the productised open option in the audit-first lane.
    </p>
    <div class="compare-wrap">
      <table class="compare-table">
        <thead>
          <tr>
            <th></th>
            <th class="col-self">SQLTriage</th>
            <th>Microsoft SQL VA</th>
            <th>sp_Blitz</th>
            <th>SQLWATCH</th>
            <th>Redgate / SentryOne</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>Cost</td>
            <td class="col-self yes">Free</td>
            <td class="yes">Free (with SSMS)</td>
            <td class="yes">Free</td>
            <td class="yes">Free</td>
            <td class="no">$$$/server</td>
          </tr>
          <tr>
            <td>Audit checks</td>
            <td class="col-self yes">600+</td>
            <td class="partial">~70</td>
            <td class="partial">~275</td>
            <td class="no">Monitoring-only</td>
            <td class="partial">Varies</td>
          </tr>
          <tr>
            <td>Framework mapping</td>
            <td class="col-self yes">8 frameworks</td>
            <td class="no">None</td>
            <td class="no">None</td>
            <td class="no">None</td>
            <td class="no">Limited</td>
          </tr>
          <tr>
            <td>Cited evidence per finding</td>
            <td class="col-self yes">Yes</td>
            <td class="no">No</td>
            <td class="partial">Inline only</td>
            <td class="no">No</td>
            <td class="no">No</td>
          </tr>
          <tr>
            <td>Boardroom PDF output</td>
            <td class="col-self yes">Yes</td>
            <td class="partial">SSMS export</td>
            <td class="no">Result grid only</td>
            <td class="no">Dashboards</td>
            <td class="partial">Yes</td>
          </tr>
          <tr>
            <td>Maturity roadmap</td>
            <td class="col-self yes">5-level model</td>
            <td class="no">None</td>
            <td class="no">None</td>
            <td class="no">None</td>
            <td class="no">None</td>
          </tr>
          <tr>
            <td>Live monitoring (companion)</td>
            <td class="col-self yes">Yes</td>
            <td class="no">No</td>
            <td class="no">No</td>
            <td class="yes">Primary use</td>
            <td class="yes">Primary use</td>
          </tr>
          <tr>
            <td>Agent on SQL Server</td>
            <td class="col-self yes">No agent</td>
            <td class="yes">No agent</td>
            <td class="yes">Stored proc</td>
            <td class="partial">SQL Agent jobs</td>
            <td class="no">Agent required</td>
          </tr>
          <tr>
            <td>UI</td>
            <td class="col-self yes">Blazor desktop + service</td>
            <td class="partial">SSMS only</td>
            <td class="no">SSMS results grid</td>
            <td class="yes">Power BI</td>
            <td class="yes">Web</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</section>

<section class="section" id="screenshots">
  <div class="container">
    <div class="section-eyebrow">// in the app</div>
    <h2 class="section-title">CIO Dashboard, Compliance Map, Maturity Roadmap, Findings Detail.</h2>
    <p class="section-lede">The audit surface comes first. Monitoring views live one click deeper.</p>
    <div class="screenshot-grid">
      <div class="screenshot">
        <img src="screenshots/16-risk-register-compliance-mapping-ISO-SOC2-NIST.jpg" alt="Compliance Map — framework coverage and gap analysis across ISO 27001, SOC 2, NIST">
        <div class="caption"><strong>Compliance Map</strong>Framework coverage table with gap analysis across ISO 27001, SOC 2, NIST</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/13-maturity-roadmap.jpg" alt="Maturity Roadmap — 5-level DBA maturity framework across all servers">
        <div class="caption"><strong>Maturity Roadmap</strong>5-level DBA maturity framework, per-server progress tracking</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/9-run-full-sql-audits.jpg" alt="Full SQL Audit — comprehensive health check across all connected instances">
        <div class="caption"><strong>Full SQL Audit</strong>Comprehensive health check across all connected instances</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/10-microsoft-sql-vulnerability-assessment.jpg" alt="Vulnerability Assessment — 600+ checks with framework mapping">
        <div class="caption"><strong>Vulnerability Assessment</strong>600+ checks with severity ratings and remediation guidance</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/8-servers-easily-monitor-50-instances-or-more.jpg" alt="Multi-Server — audit and monitor 50+ SQL Server instances from one place">
        <div class="caption"><strong>Multi-Server</strong>Audit and monitor 50+ SQL Server instances from one place</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/4-live-sessions.jpg" alt="Live Sessions — active sessions, blocking chains, top queries">
        <div class="caption"><strong>Live Sessions <span style="color:var(--text-muted);font-weight:400;">(companion)</span></strong>Active sessions, blocking chains, top queries</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/15-query-plan-viewer.jpg" alt="Query Plan Viewer — interactive graphical execution plan with per-operator cost">
        <div class="caption"><strong>Query Plan Viewer <span style="color:var(--text-muted);font-weight:400;">(companion)</span></strong>Interactive graphical plan with per-operator cost and missing-index hints</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/3-wait-state.jpg" alt="Wait Stats — real-time and historical wait category analysis">
        <div class="caption"><strong>Wait Stats <span style="color:var(--text-muted);font-weight:400;">(companion)</span></strong>Real-time and historical wait category analysis</div>
      </div>
      <div class="screenshot">
        <img src="screenshots/14-alerting.jpg" alt="Alerting — 80 alerts with IQR dynamic baselines and escalation policies">
        <div class="caption"><strong>Alerting <span style="color:var(--text-muted);font-weight:400;">(companion)</span></strong>80 alerts with IQR dynamic baselines and escalation policies</div>
      </div>
    </div>
  </div>
</section>

<section class="section" id="quickstart">
  <div class="container">
    <div class="section-eyebrow">// quickstart</div>
    <h2 class="section-title">From download to first audit in under 90 seconds.</h2>
    <ol class="qs">
      <li>Download the latest release from GitHub. Single self-contained executable — .NET runtime and WebView2 are bundled.</li>
      <li>Run <code>SQLTriage.exe</code>. Onboarding wizard prompts you to add your first server.</li>
      <li>Add an SQL Server instance — Windows Auth, SQL Auth, or Azure AD. Connection is read-only by default.</li>
      <li>Hit <strong>Run Quick Check</strong> — ~30 seconds across the 40 highest-priority audit controls.</li>
      <li>Review findings on the CIO Dashboard. Export the boardroom PDF when ready.</li>
    </ol>
    <p class="muted">For 24/7 estate-wide audit history, install in Windows Service mode and access dashboards from any browser via Kestrel HTTPS.</p>
  </div>
</section>

<section class="section" id="who-its-for">
  <div class="container">
    <div class="section-eyebrow">// who it's for</div>
    <h2 class="section-title">Built for the people who sign the audit report.</h2>
    <div class="feature-grid">
      <div class="card">
        <h3>Senior DBAs / DBA leads</h3>
        <p>Replace 200+ ad-hoc scripts with a single audit. Framework-mapped output you can hand to InfoSec without reformatting.</p>
      </div>
      <div class="card">
        <h3>Database consultants</h3>
        <p>Standardise audit deliverables across clients. Reproducible methodology, cited evidence, white-label PDF output.</p>
      </div>
      <div class="card">
        <h3>InfoSec &amp; compliance teams</h3>
        <p>Get SQL Server findings in a format that maps cleanly onto your existing GRC tooling. NIST controls in, NIST controls out.</p>
      </div>
      <div class="card">
        <h3>CIOs &amp; technology leaders</h3>
        <p>"How mature is our SQL estate?" answered in a 30-page PDF that survives an auditor's spot-check. Board-ready, not toolkit-shaped.</p>
      </div>
    </div>
  </div>
</section>

<section class="section">
  <div class="container">
    <div class="section-eyebrow">// faq</div>
    <h2 class="section-title">Common questions.</h2>
    <div class="panel">
      <h3>Does SQLTriage write to my SQL Server?</h3>
      <p>No. Every check is read-only against DMVs. There is no agent, no schema change, no extended event session created. Connect, scan, leave.</p>
    </div>
    <div class="panel">
      <h3>How is this different from Microsoft SQL Vulnerability Assessment?</h3>
      <p>Microsoft VA covers ~70 checks with no framework mapping, no remediation evidence trail, and no maturity model. SQLTriage covers 600+ checks each cited to a public source and mapped to NIST/CIS/STIG/ISO/SOC 2/PCI/HIPAA/NIS2 controls. The boardroom PDF is the output.</p>
    </div>
    <div class="panel">
      <h3>Is the framework mapping defensible to an auditor?</h3>
      <p>Yes — that's the whole design. Each control mapping cites the specific clause from the published framework. No "topical adjacency," no copy-paste boilerplate. If your auditor pulls a finding at random and looks up the cited control, the control text says what we claim it says.</p>
    </div>
    <div class="panel">
      <h3>Does it really run as a Windows Service?</h3>
      <p>Yes. Headless service mode exposes dashboards via Kestrel HTTPS for remote browser access — useful for 24/7 monitoring of multi-server estates. Or run it as a desktop app for one-off audits.</p>
    </div>
    <div class="panel">
      <h3>Azure SQL?</h3>
      <p>Azure SQL Managed Instance is fully supported. Azure SQL Database (PaaS) has partial support — checks that require server-level DMV access are skipped automatically with a friendly notice.</p>
    </div>
    <div class="panel">
      <h3>License?</h3>
      <p>GNU GPL v3. Free for commercial use, no per-server fees, no subscription, no feature tiers. Source on GitHub.</p>
    </div>
    <div class="panel">
      <h3>Why is my alert firing constantly?</h3>
      <p>Alerts use IQR-based dynamic baselines. If a metric is genuinely outside its historical 25–75 percentile range, the alert fires. To tune: open the alert's edit modal, increase <code>NextAlertDelayMinutes</code> to suppress repeated firings, or enable Dry-Run mode (Settings → Alerts) to see what <em>would</em> fire without actually firing.</p>
    </div>
    <div class="panel">
      <h3>Do I need SQLWATCH?</h3>
      <p>No. Most pages work without it. Only the long-term historical dashboards (capacity trends, week-over-week comparisons) benefit from SQLWATCH. Live diagnostics, alerts, and the query plan viewer all work without it.</p>
    </div>
    <div class="panel">
      <h3>How do I move my saved server credentials to another machine?</h3>
      <p>Settings → Server Credentials → Export. This produces an <code>.lmcreds</code> file protected with a passphrase you choose. On the target machine, Settings → Server Credentials → Import, supply the file and passphrase.</p>
    </div>
    <div class="panel">
      <h3>Where are the logs?</h3>
      <p><code>logs/app-YYYYMMDD.log</code> next to the exe. Older logs are auto-rotated (kept 14 days). Set "Debug logging" in Settings to capture verbose output.</p>
    </div>
    <div class="panel">
      <h3>Have more questions?</h3>
      <p><a href="https://github.com/SQLAdrian/SQLTriage/discussions">Ask on GitHub Discussions</a> · <a href="https://github.com/SQLAdrian/SQLTriage/blob/main/QUICKSTART.md#frequently-asked-questions">Full FAQ in QUICKSTART.md</a> · <a href="https://github.com/SQLAdrian/SQLTriage/issues/new?template=bug_report.md">Report a bug</a></p>
    </div>
  </div>
</section>

<section class="section">
  <div class="container">
    <div class="panel accent" style="text-align:center;padding:2.5rem 1.5rem;">
      <h2 style="margin-bottom:0.85rem;">Ready to audit your SQL Server estate?</h2>
      <p class="muted" style="margin-bottom:1.5rem;font-size:var(--step-1);">Single download. No agent. No licensing. Your auditor will have nothing to argue with.</p>
      <div class="cta-row" style="justify-content:center;">
        <a class="btn btn-primary" href="https://github.com/SQLAdrian/SQLTriage/releases">Download for Windows</a>
        <a class="btn btn-ghost" href="https://github.com/SQLAdrian/SQLTriage">Source on GitHub</a>
      </div>
    </div>
  </div>
</section>
