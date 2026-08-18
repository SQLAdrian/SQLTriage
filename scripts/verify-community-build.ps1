# verify-community-build.ps1
# ---------------------------------------------------------------------------
# FAILS-WHEN-VIOLATED gate for the community build profile (2026-06-12).
# Invoked automatically by buildprofile.targets after every community publish;
# also runnable by hand (see .handoff/SELFTEST_GATING.md).
#
# Checks (any violation => exit 1 => the publish FAILS):
#   1. The gated-page canary literal (carried by every premium/dev-tools page;
#      see the GatedCanary const in those pages) must NOT appear in the compiled
#      assembly. (.NET string metadata is UTF-16LE; we scan both encodings.)
#   2. No gated/IP config may exist in the publish output (consolidation model,
#      queries/ruleset/governance-weights/control_mappings/roadmap-mapping).
#   3. buildprofile.json must not ship.
#   4. Config\free-bundle.dat MUST be present (the community catalog).
#
# -ExpectFull inverts check 1 (canary MUST be present) — used by the self-test
# to prove the canary actually works on a full build, not just "grep found
# nothing because the token was renamed".
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$DllPath,
    [string]$PublishDir,
    [switch]$ExpectFull
)

$ErrorActionPreference = "Stop"
# Token assembled from parts so this script (which ships in the public repo) never
# contains the contiguous literal - otherwise publish-public.ps1's canary grep
# would trip on its own tooling. The contiguous form lives ONLY in the gated pages.
$canary = "SQLT-GATED-DNA-" + "NEVERSHIP"
$failures = @()

function Find-BytesInFile([string]$Path, [byte[]]$Needle) {
    $data = [System.IO.File]::ReadAllBytes($Path)
    $limit = $data.Length - $Needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        $match = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($data[$i + $j] -ne $Needle[$j]) { $match = $false; break }
        }
        if ($match) { return $true }
    }
    return $false
}

# -- 1. canary scan over the compiled assembly --------------------------------
# Scan the PUBLISHED assembly when it exists (the actual shipped bytes) AND the
# intermediate. obj\ is shared between profiles, so the intermediate alone can be
# stale/wrong-profile; the publish-dir copy is authoritative for what ships.
$scanTargets = @()
if ($PublishDir) {
    $published = Join-Path $PublishDir "SQLTriage.dll"
    if (Test-Path $published) { $scanTargets += $published }
}
if (Test-Path $DllPath) { $scanTargets += $DllPath }
if ($scanTargets.Count -eq 0) {
    Write-Host "VERIFY FAIL: no assembly found ($DllPath / publish dir)" -ForegroundColor Red
    exit 1
}
$utf16 = [System.Text.Encoding]::Unicode.GetBytes($canary)
$utf8  = [System.Text.Encoding]::UTF8.GetBytes($canary)
$found = $false
foreach ($t in $scanTargets) {
    if ((Find-BytesInFile $t $utf16) -or (Find-BytesInFile $t $utf8)) { $found = $true; $DllPath = $t; break }
}

if ($ExpectFull) {
    if (-not $found) {
        $failures += "canary '$canary' NOT found in full-profile assembly $DllPath - the canary mechanism itself is broken (token renamed/removed?)."
    }
} else {
    if ($found) {
        $failures += "canary '$canary' found in COMMUNITY assembly $DllPath - a gated (premium/dev-tools) page was compiled in. buildprofile.targets exclusion is broken or a page was added without an exclusion entry."
    }
}

# -- 2..4. publish-tree checks (community only, when a publish dir is given) --
if (-not $ExpectFull -and $PublishDir -and (Test-Path $PublishDir)) {
    $forbidden = @(
        "consolidation-model*.json",
        "queries.json", "governance-weights.json", "control_mappings.json",
        "roadmap-mapping.json", "roadmap-aliases.json",
        "buildprofile.json", "buildprofile.targets"
    )
    foreach ($pat in $forbidden) {
        $hits = Get-ChildItem -Path $PublishDir -Recurse -File -Filter $pat -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\BPScripts\\' }  # BPScripts\ruleset.json = public MS VA ruleset, allowed
        foreach ($h in $hits) { $failures += "forbidden file in community publish output: $($h.FullName)" }
    }
    # Config\ruleset.json specifically (BPScripts copy is fine)
    if (Test-Path (Join-Path $PublishDir "Config\ruleset.json")) {
        $failures += "forbidden file in community publish output: Config\ruleset.json (gated IP)"
    }
    if (-not (Test-Path (Join-Path $PublishDir "Config\free-bundle.dat"))) {
        $failures += "Config\free-bundle.dat MISSING from community publish output - the app would boot with an empty catalog."
    }
}

# -- verdict -------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "=== COMMUNITY BUILD VERIFICATION FAILED ($($failures.Count) violation(s)) ===" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

$mode = if ($ExpectFull) { "full-profile (canary present as expected)" } else { "community (no canary, no gated assets, free bundle present)" }
Write-Host "verify-community-build: OK - $mode" -ForegroundColor Green
exit 0
