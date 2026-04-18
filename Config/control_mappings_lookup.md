<!-- In the name of God, the Merciful, the Compassionate -->

# Agent Guide: Mapping YAML `evidence_based_findings` → Compliance Control IDs

**Purpose:** Instructions for the agent pass that populates `Config/queries.json` from
`research_output/01_new_checks/consolidated_checks.sql`. Specifically: how to derive the
`controls: [...]` array per check entry.

---

## Source fields in consolidated_checks.sql YAML blocks

Each check's YAML contains one or more of:

```yaml
evidence_based_findings:
  - source: "Microsoft Learn"
    title: "Server memory configuration options"
    url: "https://learn.microsoft.com/..."
    relevance_score: 0.95
validation_sources:
  - organisation: "Microsoft"
    relevance_score: 0.9
  - organisation: "Brent Ozar"
    relevance_score: 0.85
```

---

## Mapping strategy (priority order)

### 1. Title keyword → control ID (fastest, covers ~70%)

Match `evidence_based_findings[].title` or `title:` (check title) against these keywords:

| Keyword match (case-insensitive) | Controls to assign |
|---|---|
| memory / max server memory / buffer pool | `NIST-CM-6`, `CIS-3.1` |
| backup / restore / recovery | `SOC2-CC7.4`, `ISO27001-A.12.3`, `NIST-CP-9`, `HIPAA-164.312.c` |
| encryption / TDE / transparent data | `SOC2-CC6.7`, `HIPAA-164.312.a.2`, `ISO27001-A.10.1`, `NIST-SC-28` |
| audit / auditing / audit log | `SOC2-CC7.2`, `HIPAA-164.312.b`, `ISO27001-A.12.4`, `NIST-AU-2` |
| login / password / authentication | `SOC2-CC6.1`, `HIPAA-164.312.d`, `ISO27001-A.9.4`, `NIST-IA-5` |
| permission / privilege / role / sysadmin | `SOC2-CC6.3`, `ISO27001-A.9.2`, `NIST-AC-6`, `CIS-5.1` |
| surface area / xp_cmdshell / clr / ole | `SOC2-CC6.6`, `CIS-2.1`, `NIST-CM-7`, `STIG-V-79069` |
| patch / version / update / vulnerability | `SOC2-CC7.1`, `ISO27001-A.12.6`, `NIST-SI-2`, `CIS-6.1` |
| linked server / remote / cross-db | `SOC2-CC6.6`, `NIST-AC-17`, `CIS-3.13` |
| agent / sql agent / job | `SOC2-CC6.6`, `NIST-CM-6`, `CIS-3.2` |
| tempdb / disk / space / capacity | `SOC2-A1.2`, `NIST-CP-2` |
| query store / plan / performance | `SOC2-A1.1`, `NIST-SI-12` |
| sa account / blank password / default | `SOC2-CC6.1`, `CIS-3.3`, `NIST-IA-5`, `STIG-V-79063` |
| certificate / key / key management | `SOC2-CC6.7`, `ISO27001-A.10.1`, `NIST-SC-12` |
| trace / event / xevent | `SOC2-CC7.2`, `NIST-AU-3`, `ISO27001-A.12.4` |
| compliance / pci / hipaa | `PCI-DSS-8.2`, `HIPAA-164.308.a`, `SOC2-CC9.1` |
| network / firewall / endpoint | `SOC2-CC6.6`, `NIST-SC-7`, `CIS-9.2` |

### 2. `validation_sources[].organisation` → framework hint

| Organisation | Likely frameworks |
|---|---|
| Microsoft | NIST, CIS, SOC2 |
| Brent Ozar / sp_Blitz | CIS, NIST |
| CIS Benchmarks | CIS-* |
| DISA / STIG | STIG-* |
| HIPAA / HHS | HIPAA-* |
| PCI SSC | PCI-DSS-* |
| ISO / IEC | ISO27001-* |

### 3. Category fallback (last resort)

If no keyword matches and no organisation hint:

| YAML `category:` | Default controls |
|---|---|
| Security | `SOC2-CC6.1`, `NIST-AC-2` |
| Authentication | `SOC2-CC6.1`, `NIST-IA-5`, `CIS-3.3` |
| Authorization | `SOC2-CC6.3`, `NIST-AC-6` |
| Encryption | `SOC2-CC6.7`, `ISO27001-A.10.1` |
| Auditing | `SOC2-CC7.2`, `HIPAA-164.312.b` |
| Compliance | `SOC2-CC9.1`, `ISO27001-A.18.1` |
| Configuration | `CIS-1.1`, `NIST-CM-6` |
| Backup | `SOC2-CC7.4`, `ISO27001-A.12.3` |
| Performance | `SOC2-A1.1` |
| Patching | `NIST-SI-2`, `CIS-6.1` |
| Surface_Area | `CIS-2.1`, `NIST-CM-7` |
| (all others) | `SOC2-CC6.1` |

---

## Output format

Each `queries.json` entry `controls` field should be a **deduplicated array**, max 5 IDs:

```json
"controls": ["SOC2-CC6.1", "HIPAA-164.312.d", "ISO27001-A.9.4", "NIST-IA-5", "CIS-3.3"]
```

**Rules:**
- Deduplicate (a keyword may match multiple rows above — merge, don't repeat)
- Cap at 5 IDs per check (pick highest-relevance frameworks: SOC2 > ISO27001 > NIST > HIPAA > CIS > STIG)
- Prefer IDs with section numbers (e.g., `SOC2-CC6.1` over just `SOC2`)
- If zero keywords match and category fallback gives only 1 ID, that's fine — don't invent more

---

## Full framework ID reference (abbreviated)

| Framework | Example IDs used above | Notes |
|---|---|---|
| SOC2 (2017 TSC) | CC6.1, CC6.3, CC6.6, CC6.7, CC7.1, CC7.2, CC7.4, A1.1, A1.2, CC9.1 | Common Criteria + Availability |
| HIPAA Security Rule | 164.308.a, 164.312.a.2, 164.312.b, 164.312.c, 164.312.d | Part 164 Subpart C |
| ISO 27001:2022 | A.9.2, A.9.4, A.10.1, A.12.3, A.12.4, A.12.6, A.18.1 | Annex A controls |
| NIST 800-53 Rev 5 | AC-2, AC-6, AC-17, AU-2, AU-3, CM-6, CM-7, CP-2, CP-9, IA-5, SC-7, SC-12, SC-28, SI-2, SI-12 | Control families |
| CIS SQL Server | 1.1, 2.1, 3.1, 3.2, 3.3, 3.13, 5.1, 6.1, 9.2 | CIS Microsoft SQL Server Benchmark |
| PCI-DSS v4 | 8.2, ... | Requirement 8 (access control) most relevant |
| DISA STIG | V-79063, V-79069, ... | SQL Server 2016 Instance STIG IDs |

Full mappings in `Config/control_mappings.json` (29 frameworks, 10 regions).

---

## Agent pass checklist

Before writing each `queries.json` entry:

1. Extract YAML block for check
2. Read `title:` + `evidence_based_findings[].title` keywords
3. Run keyword match table (§1 above) — collect all matching control IDs
4. If <2 IDs found: check `validation_sources[].organisation` (§2)
5. If still <1 ID: use category fallback (§3)
6. Deduplicate, cap at 5, sort by framework priority
7. Write to `"controls": [...]`
8. Compute `sourceYamlHash`: SHA256 of the raw YAML block bytes
