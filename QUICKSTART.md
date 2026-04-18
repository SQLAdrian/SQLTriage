<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# SQL Health Assessment — Quick Start

Get from zero to a live health snapshot in under 5 minutes.

> **Requirements:** Windows 10 version 1809 or later · SQL Server 2016 or later  
> .NET runtime and WebView2 are bundled — **nothing to install**.

---

## Step 1 — Download and Run

1. Go to the [Releases page](https://github.com/SQLAdrian/SqlHealthAssessment/releases/latest).
2. Download `LiveMonitor.exe`.
3. Run it — no install wizard, no UAC prompt for normal use.

> **SmartScreen warning?** Click **More info → Run anyway**. The exe is self-contained and unsigned (signing cert costs ~$500/yr). Source is MIT licensed and fully auditable.

---

## Step 2 — Add a SQL Server

The **Add Server** dialog opens automatically on first run.

| Field | What to enter |
|---|---|
| **Server Name** | Hostname, IP, or `HOST\INSTANCE` |
| **Auth** | Windows Auth (recommended) or SQL login |
| **Database** | Leave blank — uses `master` |

Click **Test Connection** then **Save**.

> You can add as many servers as you need. All connections are stored locally, encrypted with Windows DPAPI.

Screenshot: ![Add Server](docs/screenshots/1-addserver.jpg)

---

## Step 3 — Run a Quick Check

Press **Ctrl+Q** or click **Quick Check** in the sidebar.

- Select a server (or "All Enabled Servers").
- Click **Run Checks**.
- Results appear in seconds — colour-coded by severity.

Each check links to a fix recommendation. Export to PDF with **Ctrl+P**.

Screenshot: ![Quick Check](docs/screenshots/2-quickcheck.jpg)

---

## Step 4 — Open Live Monitor

Press **Ctrl+2** or click **Live Monitor**.

Shows: active sessions · wait stats · top queries by CPU/reads · blocking chains.

Refresh interval is configurable (default: 30s). No agents, no collectors — queries run on demand.

Screenshot: ![Live Sessions](docs/screenshots/4-live-sessions.jpg)

---

## Step 5 — Set Up Alerts (Optional)

Go to **Alerting** to enable threshold-based alerts:

1. Enable the alerts you care about (CPU, blocking, long-running queries, failed jobs…).
2. Configure notification channels under **Alerting Config** (SMTP email, Teams webhook).
3. Alerts fire as toast notifications and are logged to the Alert History tab.

Screenshot: ![Alerting](docs/screenshots/14-alerting.jpg)

---

## Common Questions

### Do I need SQLWATCH?

No. SQLWATCH is optional and adds historical trend data to the dashboards. The app works fully without it.

To install SQLWATCH, go to **Database Deploy** — it handles the entire deployment.

### Do I need SQL Server Agent?

No. The app queries DMVs directly. No agent jobs, no extended events collectors, no linked servers.

### Can I run it as a Windows Service?

Yes. Go to **Service Management** and install it as a Windows service. This enables the web (server) mode — useful for shared team access on a jump box.

### Something looks wrong — where's the log?

Go to **Settings → Debug Logging** and toggle it on. The log file opens from the same menu.

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+Q | Quick Check |
| Ctrl+R | Run / Refresh |
| Ctrl+P | Print / PDF export |
| Ctrl+2–9 | Navigate to dashboard panels |
| Ctrl+D | Toggle dark mode |

---

## Getting Help

- [GitHub Issues](https://github.com/SQLAdrian/SqlHealthAssessment/issues) — bug reports
- [GitHub Discussions](https://github.com/SQLAdrian/SqlHealthAssessment/discussions) — questions and ideas
- [sqldba.org](https://sqldba.org) — blog and SQL Server resources
- Email: [adrian@sqldba.org](mailto:adrian@sqldba.org)

---

[← Back to README](README.md) · [Full Screenshots →](https://sqladrian.github.io/SqlHealthAssessment/)

