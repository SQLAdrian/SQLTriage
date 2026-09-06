#!/usr/bin/env python3
"""
update-licensing-pricing.py — Publish-time script that scrapes Microsoft SQL Server
pricing pages for Enterprise and Standard per-core perpetual-license prices,
validates them against sane ranges, and writes sql-licensing-pricing.json for
the app's per-server annual licensing cost estimator.

Assumption: Open Value + Software Assurance (OV+SA) channel pricing only.
Pricing is USD MSRP. NZD conversion happens in C#.

Usage:
    python scripts/update-licensing-pricing.py

Outputs:
    Config/sql-licensing-pricing.json
"""

import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parent.parent
OUTPUT_JSON = ROOT / "Config" / "sql-licensing-pricing.json"

PRICING_URL = "https://www.microsoft.com/en-us/sql-server/sql-server-2022-pricing"
REQUEST_TIMEOUT = 30
MAX_RETRIES = 2

ANCHOR_ENTERPRISE = 15123
ANCHOR_STANDARD = 3945
ANNUAL_SA_FACTOR = 0.5833
MIN_CORES_PER_SERVER = 4
MIN_CORES_PER_VM = 4

ENTERPRISE_RANGE = (10_000, 20_000)
STANDARD_RANGE = (2_000, 6_000)

EDITION_NORMALISATION = {
    "Enterprise Edition": "Enterprise",
    "Enterprise Edition: Core-based Licensing": "Enterprise",
    "Enterprise Edition (64-bit)": "Enterprise",
    "Enterprise Evaluation Edition": "Enterprise",
    "Datacenter": "Enterprise",
    "Datacenter Edition": "Enterprise",
    "Business Intelligence": "Enterprise",
    "Business Intelligence Edition": "Enterprise",
    "Standard Edition": "Standard",
    "Standard Edition (64-bit)": "Standard",
    "Standard Edition for Small Business": "Standard",
    "Workgroup": "Standard",
    "Workgroup Edition": "Standard",
    "Web": "Web",
    "Web Edition": "Web",
    "Express": "Express",
    "Express Edition": "Express",
    "Express Edition with Advanced Services": "Express",
    "Developer": "Developer",
    "Developer Edition": "Developer",
    "Developer Edition (64-bit)": "Developer",
    "Evaluation": "Enterprise",
}

EDITION_CAPS = {
    "Express": {"cores": 4, "memoryMB": 1410, "dbSizeGB": 10},
    "Standard": {"cores": 24, "memoryMB": 131072, "dbSizeGB": None},
    "Enterprise": {"cores": None, "memoryMB": None, "dbSizeGB": None},
}

PASSIVE_REPLICAS = {
    "haReplicasAllowedFree": 1,
    "drReplicasAllowedFree": 1,
    "drInAzureAllowedFree": 1,
    "requiresSA": True,
}

NOTES = [
    "OV+SA annual rate = perpetual price x 0.5833 (amortised over 3 years + 25% SA)",
    "Core licences sold in 2-packs -- total must be even",
    "All physical cores must be licensed on a physical OSE",
    "Hyper-threading does not increase licensed core count for physical OSE",
    "Passive replicas free under OV+SA (max 1 HA + 1 DR + 1 DR-in-Azure)",
]


def _extract_price(text, edition_pattern, label):
    matches = re.findall(edition_pattern, text, re.IGNORECASE)
    for raw_value in matches:
        clean = raw_value.replace(",", "").replace("$", "").strip()
        try:
            price = int(float(clean))
            if price > 0:
                return price
        except (ValueError, OverflowError):
            continue
    return None


def scrape_enterprise(text):
    patterns = [
        r"\$?([\d,]+)\s*per\s*core.*?[Ee]nterprise",
        r"[Ee]nterprise.*?\$?([\d,]+)\s*per\s*core",
        r"[Ee]nterprise\s+Edition.*?\$?([\d,]+)",
        r"[Ee]nterprise.*?price.*?\$?([\d,]+)",
        r"\$?([\d,]{4,6})\b(?!.*Standard)",
        r"\bEnterprise.*?\$([\d,]+)",
    ]
    for pattern in patterns:
        price = _extract_price(text, pattern, "Enterprise")
        if price and ENTERPRISE_RANGE[0] <= price <= ENTERPRISE_RANGE[1]:
            return price
    for pattern in patterns:
        price = _extract_price(text, pattern, "Enterprise")
        if price:
            print(f"  WARNING: Enterprise price ${price} outside sane range "
                  f"(${ENTERPRISE_RANGE[0]}-${ENTERPRISE_RANGE[1]})")
            return None
    return None


def scrape_standard(text):
    patterns = [
        r"\$?([\d,]+)\s*per\s*core.*?[Ss]tandard",
        r"[Ss]tandard.*?\$?([\d,]+)\s*per\s*core",
        r"[Ss]tandard\s+Edition.*?\$?([\d,]+)",
        r"[Ss]tandard.*?price.*?\$?([\d,]+)",
        r"\bStandard.*?\$([\d,]+)",
    ]
    for pattern in patterns:
        price = _extract_price(text, pattern, "Standard")
        if price and STANDARD_RANGE[0] <= price <= STANDARD_RANGE[1]:
            return price
    for pattern in patterns:
        price = _extract_price(text, pattern, "Standard")
        if price:
            print(f"  WARNING: Standard price ${price} outside sane range "
                  f"(${STANDARD_RANGE[0]}-${STANDARD_RANGE[1]})")
            return None
    return None


def fetch_pricing_page():
    headers = {
        "User-Agent": (
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"
        ),
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "en-US,en;q=0.9",
    }
    for attempt in range(MAX_RETRIES + 1):
        try:
            r = requests.get(PRICING_URL, headers=headers, timeout=REQUEST_TIMEOUT)
            if r.status_code == 200:
                return r.text
            print(f"  HTTP {r.status_code} — attempt {attempt + 1}/{MAX_RETRIES + 1}")
        except requests.RequestException as e:
            print(f"  Request error: {e} — attempt {attempt + 1}/{MAX_RETRIES + 1}")
    return None


def build_json(enterprise, standard, source_url):
    now_utc = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "lastUpdated": now_utc,
        "currency": "USD",
        "version": "2022",
        "source": source_url,
        "perpetualPerCoreUSD": {
            "Enterprise": enterprise,
            "Standard": standard,
            "Web": 0,
            "Express": 0,
            "Developer": 0,
        },
        "annualSAFactor": ANNUAL_SA_FACTOR,
        "minimumCoresPerServer": MIN_CORES_PER_SERVER,
        "minimumCoresPerVM": MIN_CORES_PER_VM,
        "editionCaps": EDITION_CAPS,
        "passiveReplicas": PASSIVE_REPLICAS,
        "editionNormalisation": EDITION_NORMALISATION,
        "notes": NOTES,
    }


def validate_output(data):
    issues = []
    if "perpetualPerCoreUSD" not in data:
        issues.append("Missing perpetualPerCoreUSD")
    else:
        for edition in ["Enterprise", "Standard", "Web", "Express", "Developer"]:
            if edition not in data["perpetualPerCoreUSD"]:
                issues.append(f"Missing edition in perpetualPerCoreUSD: {edition}")
    if not isinstance(data.get("annualSAFactor"), (int, float)):
        issues.append("annualSAFactor is not numeric")
    elif data["annualSAFactor"] != ANNUAL_SA_FACTOR:
        issues.append(f"annualSAFactor is {data['annualSAFactor']}, expected {ANNUAL_SA_FACTOR}")
    norm = data.get("editionNormalisation", {})
    if len(norm) < 20:
        issues.append(f"editionNormalisation has {len(norm)} entries, need >=20")
    if "lastUpdated" not in data:
        issues.append("Missing lastUpdated")
    if "source" not in data:
        issues.append("Missing source")
    return issues


def main():
    print("SQL Server Licensing Pricing Scraper")
    print(f"Started: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')}")
    print()

    source_url = PRICING_URL
    enterprise = ANCHOR_ENTERPRISE
    standard = ANCHOR_STANDARD
    scrape_success = False

    print("=== Stage 1: Fetch Microsoft pricing page ===")
    html = fetch_pricing_page()

    if html:
        print(f"  Downloaded {len(html)} bytes")
        print("=== Stage 2: Parse prices ===")
        scraped_enterprise = scrape_enterprise(html)
        scraped_standard = scrape_standard(html)

        if scraped_enterprise and scraped_standard:
            enterprise = scraped_enterprise
            standard = scraped_standard
            scrape_success = True
            print(f"  Enterprise (scraped): ${enterprise}/core")
            print(f"  Standard  (scraped): ${standard}/core")
        else:
            if not scraped_enterprise:
                print("  WARNING: Could not parse Enterprise price from page")
            if not scraped_standard:
                print("  WARNING: Could not parse Standard price from page")
            print("  Falling back to manual cached anchor prices")
    else:
        print("  ERROR: Could not fetch pricing page")
        print("  Falling back to manual cached anchor prices")

    if not scrape_success:
        source_url = "manual-cached-2026-05-11"
        enterprise = ANCHOR_ENTERPRISE
        standard = ANCHOR_STANDARD
        print(f"  Using manual cached: Enterprise=${enterprise}, Standard=${standard}")

    print("\n=== Stage 3: Build and validate JSON ===")
    data = build_json(enterprise, standard, source_url)
    issues = validate_output(data)

    if issues:
        print("  VALIDATION ERRORS:")
        for issue in issues:
            print(f"    - {issue}")
        print("\nERROR: Output JSON fails validation. Aborting to avoid writing broken data.")
        sys.exit(1)

    print("  Validation passed")

    print("\n=== Stage 4: Write JSON ===")
    OUTPUT_JSON.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_JSON, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2, ensure_ascii=False)

    print(f"  Wrote {OUTPUT_JSON.relative_to(ROOT)}")
    norm_count = len(data["editionNormalisation"])
    print(f"  Edition normalisation entries: {norm_count}")

    print("\n" + "=" * 60)
    print("SUMMARY")
    print("=" * 60)
    print(f"  Enterprise:         ${enterprise}/core")
    print(f"  Standard:           ${standard}/core")
    print(f"  Web / Express / Dev: $0 (free)")
    print(f"  annualSAFactor:      {ANNUAL_SA_FACTOR}")
    print(f"  Source:              {source_url}")
    print(f"  Last updated:        {data['lastUpdated']}")
    print(f"  Edition norm entries: {norm_count}")
    print(f"  Output:              {OUTPUT_JSON.relative_to(ROOT)}")
    print("=" * 60)

    if not scrape_success:
        print("\nWARNING: Used manual cached anchor prices. Scraper could not get live data.")
        sys.exit(0)

    print("\nAll prices fetched and validated successfully.")
    return 0


if __name__ == "__main__":
    main()
