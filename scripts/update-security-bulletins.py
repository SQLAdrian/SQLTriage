#!/usr/bin/env python3
"""
update-security-bulletins.py — Publish-time script that scrapes MSRC CVRF API
and NIST NVD for SQL Server-affecting CVEs (2020 to current), outputs a JSON
bulletin file, maps CVEs to YAML security checks, and flags ambiguous matches
for human review.

Usage:
    python scripts/update-security-bulletins.py

Outputs:
    Config/sql-security-bulletins.json   — >=50 CVEs with severity, CVSS, versions
    _NEEDS_HUMAN_REVIEW.md               — ambiguous YAML <-> CVE matches
    (patched) research_output/LLM1_deepseek/check_*.yaml — cve_references added
"""

import html as _html
import json
import os
import re
import sys
import time
import xml.etree.ElementTree as ET
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

import requests
import yaml

ROOT = Path(__file__).resolve().parent.parent
MSRC_UPDATES = "https://api.msrc.microsoft.com/cvrf/v3.0/updates"
MSRC_CVRF = "https://api.msrc.microsoft.com/cvrf/v3.0/cvrf"
NVD_BASE = "https://services.nvd.nist.gov/rest/json/cves/2.0"
YAML_DIR = ROOT / "research_output" / "LLM1_deepseek"
OUTPUT_JSON = ROOT / "Config" / "sql-security-bulletins.json"
HUMAN_REVIEW = ROOT / "_NEEDS_HUMAN_REVIEW.md"

NVD_RATE_LIMIT = 50
NVD_WINDOW_SEC = 30
CVRF_TIMEOUT = 120
MSRC_MAX_RETRIES = 3
MSRC_BASE_DELAY = 10

CVRF_NS = {
    "cvrf": "http://www.icasi.org/CVRF/schema/cvrf/1.1",
    "vuln": "http://www.icasi.org/CVRF/schema/vuln/1.1",
    "prod": "http://www.icasi.org/CVRF/schema/prod/1.1",
}


def _tag(ns_key, local):
    return f"{{{CVRF_NS[ns_key]}}}{local}"


# ─── HTML table parser for DocumentNotes ─────────────────────────────────────
def _parse_docnotes_table(docnotes_xml):
    """Extract CVEs, tags, and CVSS from the DocumentNotes HTML table."""
    if not docnotes_xml:
        return []
    text = docnotes_xml.text or ""
    results = []
    rows = re.findall(r"<tr>(.*?)</tr>", text, re.DOTALL)
    for row in rows:
        tds = re.findall(r"<td[^>]*>(.*?)</td>", row, re.DOTALL)
        if len(tds) < 3:
            continue
        tag_html = tds[0].strip()
        cve_html = tds[1].strip()
        score_html = tds[2].strip() if len(tds) > 2 else ""

        tag = re.sub(r"<[^>]+>", "", _html.unescape(tag_html)).strip()
        cve = re.sub(r"<[^>]+>", "", _html.unescape(cve_html)).strip()
        score = re.sub(r"<[^>]+>", "", _html.unescape(score_html)).strip()

        if not cve.startswith("CVE-"):
            continue
        try:
            cvss = float(score) if score else None
        except ValueError:
            cvss = None
        results.append({"tag": tag, "cve": cve, "cvss": cvss})
    return results


# ─── CVRF XML parsing ────────────────────────────────────────────────────────
def parse_cvrf(xml_text):
    """
    Parse CVRF XML. Returns (doc_title, release_date, notes_table, vuln_details, products).
      - notes_table: list from _parse_docnotes_table
      - vuln_details: {cve_id: {title, remediation_kb, product_ids}}
      - products: {product_id: product_name}
    """
    root = ET.fromstring(xml_text)

    # DocumentTitle
    dt = root.find(_tag("cvrf", "DocumentTitle"))
    doc_title = dt.text.strip() if dt is not None and dt.text else "Unknown"

    # Release date
    rel_date = ""
    tracking = root.find(_tag("cvrf", "DocumentTracking"))
    if tracking is not None:
        ird = tracking.find(_tag("cvrf", "InitialReleaseDate"))
        if ird is not None and ird.text:
            rel_date = ird.text.strip()[:10]

    # DocumentNotes table
    notes = root.find(_tag("cvrf", "DocumentNotes"))
    notes_table = _parse_docnotes_table(notes) if notes is not None else []

    # ProductTree
    products = {}
    pt = root.find(_tag("cvrf", "ProductTree"))
    if pt is not None:
        for fpn in pt.iter(_tag("cvrf", "FullProductName")):
            pid = fpn.attrib.get("ProductID", "")
            name = fpn.text.strip() if fpn.text else ""
            if pid:
                products[pid] = name

    # Vulnerability details
    vuln_details = {}
    for v in root.findall(_tag("vuln", "Vulnerability")):
        cve_el = v.find(_tag("vuln", "CVE"))
        if cve_el is None or not cve_el.text:
            continue
        cve_id = cve_el.text.strip()

        title_el = v.find(_tag("vuln", "Title"))
        vuln_title = title_el.text.strip() if title_el is not None and title_el.text else ""

        # Remediations for KB
        kb = ""
        fixed_build = ""
        for rem in v.findall(_tag("vuln", "Remediations") + "/" + _tag("vuln", "Remediation")):
            desc_el = rem.find(_tag("vuln", "Description"))
            if desc_el is not None and desc_el.text:
                desc_text = desc_el.text
                kb_match = re.search(r"\b(KB\d+)\b", desc_text, re.IGNORECASE)
                if kb_match:
                    kb = kb_match.group(1)
                ver_match = re.search(r"(\d+\.\d+\.\d+\.\d+)", desc_text)
                if ver_match and not fixed_build:
                    fixed_build = ver_match.group(1)

        # Product IDs
        prod_ids = []
        for pse in v.findall(_tag("vuln", "ProductStatuses")):
            for status_elem in pse:
                for pid_elem in status_elem.findall(_tag("prod", "ProductID")):
                    if pid_elem.text:
                        prod_ids.append(pid_elem.text.strip())

        vuln_details[cve_id] = {
            "title": vuln_title,
            "remediation_kb": kb,
            "fixed_build": fixed_build,
            "product_ids": prod_ids,
        }

    return doc_title, rel_date, notes_table, vuln_details, products


# ─── MSRC fetch chain ────────────────────────────────────────────────────────
def fetch_update_ids():
    """Get all available update IDs from MSRC, filtered to 2020+."""
    try:
        r = requests.get(MSRC_UPDATES, timeout=30)
        r.raise_for_status()
        data = r.json()
        ids = []
        for item in data.get("value", []):
            update_id = item.get("ID", "")
            if not update_id:
                continue
            m = re.match(r"^(\d{4})-(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)$", update_id)
            if m and int(m.group(1)) >= 2020:
                ids.append(update_id)
        return sorted(ids)
    except Exception as e:
        print(f"ERROR fetching update list: {e}")
        return []


def fetch_cvrf(update_id):
    """Fetch a single CVRF document with retry and backoff."""
    url = f"{MSRC_CVRF}/{update_id}"
    for attempt in range(MSRC_MAX_RETRIES):
        try:
            r = requests.get(url, timeout=CVRF_TIMEOUT)
            if r.status_code == 200:
                return r.text
            delay = MSRC_BASE_DELAY * (2 ** attempt)
            print(f"    HTTP {r.status_code} — retry {attempt+1}/{MSRC_MAX_RETRIES} after {delay}s")
            time.sleep(delay)
        except requests.RequestException as e:
            delay = MSRC_BASE_DELAY * (2 ** attempt)
            print(f"    {e} — retry {attempt+1}/{MSRC_MAX_RETRIES} after {delay}s")
            time.sleep(delay)
    return None


# ─── NVD helpers ─────────────────────────────────────────────────────────────
def _nvd_rate_limiter():
    timestamps = []

    def wait():
        nonlocal timestamps
        now = time.time()
        timestamps = [t for t in timestamps if now - t < NVD_WINDOW_SEC]
        if len(timestamps) >= NVD_RATE_LIMIT:
            sleep_time = NVD_WINDOW_SEC - (now - timestamps[0]) + 0.5
            if sleep_time > 0:
                time.sleep(sleep_time)
            now = time.time()
            timestamps = [t for t in timestamps if now - t < NVD_WINDOW_SEC]
        timestamps.append(now)

    return wait


def fetch_nvd_batch(cve_ids):
    if not cve_ids:
        return {}
    rate_wait = _nvd_rate_limiter()
    results = {}
    for i in range(0, len(cve_ids), 20):
        batch = cve_ids[i:i + 20]
        cve_filter = " ".join(batch)
        rate_wait()
        try:
            r = requests.get(NVD_BASE, params={"cveId": cve_filter, "resultsPerPage": min(len(batch), 20)}, timeout=30)
        except requests.RequestException as e:
            print(f"  NVD request failed for batch starting at {i}: {e}")
            continue
        if r.status_code != 200:
            print(f"  NVD HTTP {r.status_code} for batch starting at {i}")
            continue
        for v in r.json().get("vulnerabilities", []):
            c = v.get("cve", {})
            cid = c.get("id", "")
            desc_en = ""
            for d in c.get("descriptions", []):
                if d.get("lang") == "en":
                    desc_en = d.get("value", "")
                    break
            if not desc_en and c.get("descriptions"):
                desc_en = c["descriptions"][0].get("value", "")
            cvss, severity = _extract_cvss(c.get("metrics", {}))
            results[cid] = {"description": desc_en, "cvssScore": cvss, "severity": severity}
    return results


def _extract_cvss(metrics):
    for version in ["cvssMetricV31", "cvssMetricV30", "cvssMetricV2"]:
        entries = metrics.get(version, [])
        if entries:
            cd = entries[0].get("cvssData", {})
            score = cd.get("baseScore")
            sev = cd.get("baseSeverity", "")
            if score is not None:
                sev_label = {"NONE": "None", "LOW": "Low", "MEDIUM": "Medium", "HIGH": "High", "CRITICAL": "Critical"}
                return float(score), sev_label.get(sev, sev)
            if sev:
                sev_label = {"NONE": "None", "LOW": "Low", "MEDIUM": "Medium", "HIGH": "High", "CRITICAL": "Critical"}
                return None, sev_label.get(sev, sev)
    return None, "Unknown"


# ─── Main scrape pipeline ────────────────────────────────────────────────────
def scrape_msrc():
    """
    Returns dict: cve_id -> {title, publishedDate, cvssScore, severity, description,
                              kb, fixedInBuild, affectedVersions, affectedComponents}
    """
    print("=== Stage 1: MSRC CVRF scrape ===")
    update_ids = fetch_update_ids()
    if not update_ids:
        print("ERROR: No update IDs found from MSRC.")
        return {}
    print(f"  Found {len(update_ids)} monthly updates from 2020 onward")

    results = {}
    total_downloaded = 0
    months_with_sql = 0

    for update_id in update_ids:
        print(f"  [{update_id}] downloading...", end=" ", flush=True)
        xml_text = fetch_cvrf(update_id)
        if xml_text is None:
            print("FAILED")
            continue
        total_downloaded += 1

        try:
            doc_title, rel_date, notes_table, vuln_details, products = parse_cvrf(xml_text)
        except ET.ParseError as e:
            print(f"XML parse error: {e}")
            continue

        sql_entries = [n for n in notes_table if "sql server" in n["tag"].lower()]
        if sql_entries:
            if months_with_sql == 0:
                months_with_sql = 1
            else:
                months_with_sql += 1

        extracted = 0
        for entry in sql_entries:
            cve = entry["cve"]
            if cve in results:
                continue
            vd = vuln_details.get(cve, {})
            prod_names = [products.get(pid, "") for pid in vd.get("product_ids", [])]
            sql_prod_names = [p for p in prod_names if "sql" in p.lower()]

            cvss = entry.get("cvss")
            if cvss is not None:
                cvss = float(cvss)

            results[cve] = {
                "title": vd.get("title", ""),
                "publishedDate": rel_date,
                "cvssScore": cvss,
                "severity": _cvss_to_severity(cvss),
                "description": vd.get("title", ""),
                "kb": vd.get("remediation_kb", ""),
                "fixedInBuild": vd.get("fixed_build", ""),
                "affectedVersions": _extract_versions(sql_prod_names),
                "affectedComponents": _extract_components(sql_prod_names),
            }
            extracted += 1
        print(f"{extracted} SQL CVEs")

    print(f"\n  MSRC scan: {total_downloaded} months downloaded, {months_with_sql} with SQL CVEs, {len(results)} unique CVEs")
    return results


def _cvss_to_severity(score):
    if score is None:
        return "Unknown"
    if score >= 9.0:
        return "Critical"
    if score >= 7.0:
        return "High"
    if score >= 4.0:
        return "Medium"
    if score >= 0.1:
        return "Low"
    return "None"


def _extract_versions(product_names):
    versions = set()
    for p in product_names:
        m = re.search(r"(\d{4})", p)
        if m:
            versions.add(m.group(1))
    return sorted(versions)


def _extract_components(product_names):
    known = [
        "Native Client", "OLE DB Driver", "Analysis Services",
        "Integration Services", "Reporting Services", "Master Data Services",
        "Data Quality Services", "Machine Learning Services", "R Services",
        "Full-Text", "Replication", "PolyBase", "Always On",
        "Management Studio", "SSMS", "Azure Connect", "Setup",
        "Remote Code Execution", "Elevation of Privilege",
        "Denial of Service", "Information Disclosure",
    ]
    found = set()
    for p in product_names:
        for k in known:
            if k.lower() in p.lower():
                found.add(k)
    return sorted(found) if found else []


# ─── NVD enrichment ─────────────────────────────────────────────────────────
def enrich_from_nvd(msrc_results):
    print("\n=== Stage 2: NVD enrichment ===")
    cve_ids = list(msrc_results.keys())
    print(f"  Looking up {len(cve_ids)} CVEs from NVD...")
    nvd_data = fetch_nvd_batch(cve_ids)
    for cve_id, info in msrc_results.items():
        nd = nvd_data.get(cve_id, {})
        if nd.get("description"):
            info["description"] = nd["description"]
        if nd.get("cvssScore") is not None:
            info["cvssScore"] = nd["cvssScore"]
        if nd.get("severity") and nd["severity"] != "Unknown":
            info["severity"] = nd["severity"]
    enriched = sum(1 for v in msrc_results.values() if v.get("cvssScore") is not None)
    print(f"  Enriched: {enriched}/{len(cve_ids)} with CVSS scores from NVD")
    return msrc_results


# ─── Build bulletins ─────────────────────────────────────────────────────────
def build_bulletins(msrc_results):
    print("\n=== Stage 3: Building bulletins ===")
    bulletins = []
    for cve_id in sorted(msrc_results.keys()):
        info = msrc_results[cve_id]
        bulletins.append({
            "cve": cve_id,
            "title": info.get("title", ""),
            "publishedDate": info.get("publishedDate", ""),
            "severity": info.get("severity", "Unknown"),
            "cvssScore": info.get("cvssScore"),
            "affectedVersions": info.get("affectedVersions", []),
            "affectedComponents": info.get("affectedComponents", []),
            "kb": info.get("kb", ""),
            "fixedInBuild": info.get("fixedInBuild", ""),
            "description": info.get("description", ""),
            "mappedCheckIds": [],
        })
    print(f"  {len(bulletins)} bulletins built")
    return bulletins


# ─── YAML mapping ────────────────────────────────────────────────────────────
STOP_WORDS = {
    "the", "and", "for", "this", "that", "with", "from", "are", "not",
    "has", "can", "was", "all", "use", "its", "but", "than", "such",
    "when", "will", "also", "been", "each", "more", "only", "other",
    "some", "than", "into", "over", "same", "your", "after", "before",
    "however", "without", "check", "sql", "server", "data", "ensure",
    "which", "have", "should", "about", "does", "must", "used", "they",
    "were", "these", "those", "their",
}

COMPONENT_KEYWORDS = {
    "xp_cmdshell": ["xp_cmdshell", "cmdshell", "command shell"],
    "OLE Automation": ["ole automation", "sp_oacreate", "sp_oamethod"],
    "Native Client": ["native client", "snac", "sqlncli"],
    "OLE DB Driver": ["oledb", "ole db", "msoledbsql"],
    "Analysis Services": ["analysis services", "ssas", "olap"],
    "Integration Services": ["integration services", "ssis", "dts"],
    "Reporting Services": ["reporting services", "ssrs"],
    "RC4": ["rc4", "cipher"],
    "TDE": ["tde", "transparent data encryption", "database encryption key"],
    "Always Encrypted": ["always encrypted", "column encryption key", "column master key"],
    "SSL/TLS": ["ssl", "tls", "certificate encryption", "transport security"],
    "Linked Server": ["linked server", "linked servers"],
    "Full-Text": ["full text", "fulltext", "full-text"],
    "SMO/DMO": ["smo", "dmo", "management objects"],
    "PolyBase": ["polybase", "external table", "hadoop"],
    "R Services": ["r services", "machine learning services"],
    "Python": ["python"],
    "Replication": ["replication", "merge replication", "transactional replication"],
    "FILESTREAM": ["filestream"],
    "Change Tracking": ["change tracking", "change data capture"],
    "Service Broker": ["service broker"],
    "Always On": ["availability group", "availability groups", "failover cluster", "distributed ag"],
    "Extended Events": ["extended events", "xe session"],
    "Trace": ["default trace", "sql trace", "profiler"],
    "Backup": ["backup", "backups", "restore"],
    "Buffer Pool": ["buffer pool", "buffer pool extension"],
}


def _tokenize(text):
    text = text.lower()
    text = re.sub(r"[^\w\s_\-]", " ", text)
    tokens = set()
    for token in text.split():
        token = token.strip("-_")
        if len(token) >= 3 and token not in STOP_WORDS:
            tokens.add(token)
    return tokens


def load_yaml_files():
    yamls = {}
    for yf in sorted(YAML_DIR.glob("check_*.yaml")):
        try:
            with open(yf, "r", encoding="utf-8") as fh:
                data = yaml.safe_load(fh)
            if data and isinstance(data, dict) and "check_id" in data:
                yamls[yf] = data
        except Exception as e:
            print(f"  WARNING: {yf.name}: {e}")
    return yamls


def extract_yaml_keywords(yaml_data):
    keywords = set()
    for field in ["title", "category", "description"]:
        val = yaml_data.get(field, "")
        if isinstance(val, str):
            keywords.update(_tokenize(val))
    cid = yaml_data.get("check_id", "")
    if cid:
        keywords.add(str(cid))
    return keywords


def compute_component_match(bulletin, ykw):
    desc = bulletin.get("description", "")
    title = bulletin.get("title", "")
    components_str = " ".join(bulletin.get("affectedComponents", []))
    combined = f"{title} {desc} {components_str}".lower()
    for comp_name, comp_kw in COMPONENT_KEYWORDS.items():
        if comp_name.lower() in combined:
            matches = 0
            for kw in comp_kw:
                if " " in kw:
                    if _phrase_in_text(kw, combined, ykw):
                        matches += 1
                else:
                    if len(kw) >= 3 and kw in ykw:
                        matches += 1
            if matches >= 1:
                return True, comp_name
    return False, ""


def _phrase_in_text(phrase, combined_text, ykw):
    """Check if a multi-word phrase meaningfully overlaps with YAML text.
    Requires all significant words (len>=3, not filler) to appear in the
    YAML keyword set. Also checks the combined bulletins text as secondary."""
    parts = [p for p in phrase.split() if len(p) >= 3 and p not in {"data", "and", "for", "the", "with", "from", "into"}]
    return parts and all(p in ykw for p in parts)


def map_yamls(bulletins, yaml_files):
    print("\n=== Stage 4: YAML mapping ===")
    ykw_cache = {yp: extract_yaml_keywords(yd) for yp, yd in yaml_files.items()}

    mapped_yamls = {}
    cve_severities = {}
    ambiguous = []
    bulletins_mapped = defaultdict(list)
    sev_order = {"Critical": 4, "High": 3, "Medium": 2, "Low": 1, "None": 0, "Unknown": 0}

    for b in bulletins:
        cve_id = b["cve"]
        full_text = f"{b.get('title', '')} {b.get('description', '')}"
        for yp, yd in yaml_files.items():
            ykw = ykw_cache[yp]
            overlap = len(_tokenize(full_text) & ykw)
            is_comp, comp = compute_component_match(b, ykw)

            if is_comp and overlap >= 1:
                _record_map(mapped_yamls, cve_severities, bulletins_mapped, yp, yd, cve_id, b)
            elif overlap >= 2:
                _record_map(mapped_yamls, cve_severities, bulletins_mapped, yp, yd, cve_id, b)
            elif overlap >= 1 and not is_comp:
                overlapping_terms = _tokenize(full_text) & ykw
                ambiguous.append({
                    "cve": cve_id,
                    "cve_title": b["title"],
                    "yaml_path": str(yp.relative_to(ROOT)),
                    "yaml_check_id": yd.get("check_id", "unknown"),
                    "yaml_title": yd.get("title", ""),
                    "overlapping_keywords": sorted(overlapping_terms),
                    "reason": "1 keyword overlap, no component match",
                })

    for b in bulletins:
        b["mappedCheckIds"] = bulletins_mapped.get(b["cve"], [])

    mapped_count = len(mapped_yamls)
    unique_cves = set()
    for cv_list in mapped_yamls.values():
        unique_cves.update(cv_list)
    print(f"  Mapped {mapped_count} YAMLs to {len(unique_cves)} CVEs, {len(ambiguous)} ambiguous")
    return mapped_yamls, cve_severities, ambiguous, bulletins


def _record_map(mapped_yamls, cve_severities, bulletins_mapped, yp, yd, cve_id, bulletin):
    mapped_yamls.setdefault(yp, []).append(cve_id)
    bulletins_mapped[cve_id].append(yd.get("check_id", yp.stem))
    sev_order = {"Critical": 4, "High": 3, "Medium": 2, "Low": 1, "None": 0, "Unknown": 0}
    curr = cve_severities.get(yp, "Unknown")
    new_sev = bulletin.get("severity", "Unknown")
    if sev_order.get(new_sev, 0) > sev_order.get(curr, 0):
        cve_severities[yp] = new_sev


# ─── Write outputs ───────────────────────────────────────────────────────────
def write_yaml_updates(mapped_yamls, cve_severities):
    print("\n=== Stage 5: Writing YAML updates ===")
    updated = 0
    for yp, cve_list in sorted(mapped_yamls.items()):
        with open(yp, "r", encoding="utf-8") as fh:
            lines = fh.readlines()
        has_cve_refs = any("cve_references:" in l for l in lines)
        has_cve_sev = any("cve_severity:" in l for l in lines)
        if has_cve_refs and has_cve_sev:
            continue

        insert_at = None
        for i, line in enumerate(lines):
            if "check_type:" in line:
                insert_at = i + 1
                break
        if insert_at is None:
            for i, line in enumerate(lines):
                if "priority:" in line:
                    insert_at = i + 1
                    break
        if insert_at is None:
            insert_at = 5

        new_lines = lines[:insert_at]
        if not has_cve_refs:
            cve_str = ", ".join(sorted(set(cve_list)))
            new_lines.append(f"cve_references: [{cve_str}]\n")
        if not has_cve_sev:
            sev = cve_severities.get(yp, "Unknown")
            new_lines.append(f"cve_severity: {sev}\n")
        new_lines.extend(lines[insert_at:])

        with open(yp, "w", encoding="utf-8") as fh:
            fh.writelines(new_lines)
        updated += 1
    print(f"  Updated {updated} YAML files")


def write_json(bulletins):
    print("\n=== Stage 6: Writing JSON ===")
    OUTPUT_JSON.parent.mkdir(parents=True, exist_ok=True)
    output = {
        "lastUpdated": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "sources": {
            "msrc": MSRC_CVRF.rstrip("/cvrf"),
            "nvd": NVD_BASE,
        },
        "bulletins": bulletins,
    }
    with open(OUTPUT_JSON, "w", encoding="utf-8") as fh:
        json.dump(output, fh, indent=2, ensure_ascii=False, default=str)
    print(f"  Wrote {len(bulletins)} bulletins to {OUTPUT_JSON}")


def write_human_review(ambiguous):
    print("\n=== Stage 7: Writing human review ===")
    if not ambiguous:
        with open(HUMAN_REVIEW, "w", encoding="utf-8") as fh:
            fh.write("# _NEEDS_HUMAN_REVIEW.md\n\nNo ambiguous CVE-to-YAML matches found.\n")
        print("  No ambiguous matches")
        return
    lines = [
        "# CVE -> YAML Mapping -- Needs Human Review",
        "",
        f"Generated: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')}",
        f"Total ambiguous matches: {len(ambiguous)}",
        "",
        "These matches had exactly 1 keyword overlap with no direct component match.",
        "A human should verify whether the CVE genuinely applies to the check.",
        "",
        "| CVE | CVE Title | YAML Check | YAML Title | Overlapping Keywords | Reason |",
        "|-----|-----------|------------|------------|---------------------|--------|",
    ]
    for item in ambiguous:
        lines.append(
            f"| {item['cve']} | {item['cve_title'][:80]} | {item['yaml_check_id']} | "
            f"{item['yaml_title'][:60]} | {', '.join(item['overlapping_keywords'][:8])} | "
            f"{item['reason']} |"
        )
    with open(HUMAN_REVIEW, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    print(f"  Wrote {len(ambiguous)} ambiguous matches to {HUMAN_REVIEW}")


def print_summary(bulletins, mapped_yamls, ambiguous):
    print("\n" + "=" * 60)
    print("SUMMARY")
    print("=" * 60)
    print(f"  Total CVEs collected:       {len(bulletins)}")
    print(f"  YAMLs mapped:               {len(mapped_yamls)}")
    print(f"  Ambiguous (needs review):   {len(ambiguous)}")
    sev_counts = defaultdict(int)
    for b in bulletins:
        sev_counts[b.get("severity", "Unknown")] += 1
    for sev in ["Critical", "High", "Medium", "Low", "Unknown"]:
        if sev_counts.get(sev, 0):
            print(f"  {sev + ':':12s} {sev_counts[sev]}")
    with_score = sum(1 for b in bulletins if b.get("cvssScore") is not None)
    print(f"  With CVSS score:            {with_score}")
    print(f"  Output:                     {OUTPUT_JSON.relative_to(ROOT)}")
    print(f"  Human review:               {HUMAN_REVIEW.relative_to(ROOT)}")
    print("=" * 60)


# ─── Main ────────────────────────────────────────────────────────────────────
def main():
    print("SQL Server Security Bulletins Scraper")
    print(f"Started: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')}")
    print()

    msrc_results = scrape_msrc()
    if not msrc_results:
        print("\nERROR: MSRC fetch returned zero CVEs. Aborting to avoid partial mapping.")
        sys.exit(1)

    msrc_results = enrich_from_nvd(msrc_results)
    bulletins = build_bulletins(msrc_results)

    yaml_files = load_yaml_files()
    print(f"  Loaded {len(yaml_files)} YAML checks")
    mapped_yamls, cve_severities, ambiguous, bulletins = map_yamls(bulletins, yaml_files)

    write_yaml_updates(mapped_yamls, cve_severities)
    write_json(bulletins)
    write_human_review(ambiguous)
    print_summary(bulletins, mapped_yamls, ambiguous)

    if len(bulletins) < 50:
        print(f"\nWARNING: Only {len(bulletins)} CVEs collected (target: >=50). May need manual review.")
    mapped_yaml_count = len(mapped_yamls)
    print(f"  Mapped YAML count: {mapped_yaml_count} (target: >=30)")


if __name__ == "__main__":
    main()
