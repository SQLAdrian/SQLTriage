# Scripts

## Publish-Time Workflows

### update-security-bulletins.py
Scrapes Microsoft MSRC CVRF API and NIST NVD for SQL Server-affecting CVEs
(2020 to current), outputs a JSON bulletin file for the app to surface
CVSS-graded security findings, and maps CVEs to YAML security checks.

**Data Sources:**
- MSRC CVRF API (`https://api.msrc.microsoft.com/cvrf/v3.0/`) — monthly bulletins, XML
- NIST NVD API (`https://services.nvd.nist.gov/rest/json/cves/2.0`) — CVSS scores

**Outputs:**
- `Config/sql-security-bulletins.json` — >=50 CVEs with severity, CVSS, affected versions
- `_NEEDS_HUMAN_REVIEW.md` — ambiguous CVE-to-YAML matches
- Patched `research_output/LLM1_deepseek/check_*.yaml` — `cve_references` and `cve_severity` fields added

**Rate Limits Respected:**
- MSRC: exponential backoff, max 3 retries per month
- NVD: 50 requests per 30-second window (unsigned tier)

**Failure Behavior:**
- If MSRC fetch returns zero CVEs, script aborts and does NOT modify YAMLs
- Missing monthly bulletins are logged as warnings and skipped
- NVD lookup failures are logged; CVEs without scores get severity inferred from title

**Usage:**
```bash
python scripts/update-security-bulletins.py
```

**Dependencies:** `requests`, `pyyaml`

---

### update-licensing-pricing.py
Scrapes Microsoft's SQL Server 2022 pricing page for Enterprise and Standard
per-core perpetual-license prices (USD MSRP), validates them against sane
ranges, and writes a pricing JSON the app uses to compute per-server annual
OV+SA licensing cost.

**Data Sources:**
- Microsoft SQL Server pricing page (`https://www.microsoft.com/en-us/sql-server/sql-server-2022-pricing`)

**Outputs:**
- `Config/sql-licensing-pricing.json` — per-core prices, edition caps, normalisation table, SA factor

**Assumptions:**
- Open Value + Software Assurance (OV+SA) channel pricing only
- Supports Enterprise, Standard, Web, Express, Developer editions
- 4-core minimum per server/VM (rounded up to even)
- Standard cap: 24 cores / 128GB RAM
- Express cap: 4 cores / 1410MB RAM / 10GB DB
- Passive replicas free under OV+SA (1 HA + 1 DR + 1 DR-in-Azure)

**Failure Behavior:**
- If Microsoft pricing page fetch fails or prices are outside sane ranges (Enterprise $10K-$20K, Standard $2K-$6K), falls back to manual cached anchor prices with `source: "manual-cached-2026-05-11"`
- Script always exits 0 — never produces broken JSON
- Scraper output is publish-time only; no runtime fetches

**Usage:**
```bash
python scripts/update-licensing-pricing.py
```

**Dependencies:** `requests`
