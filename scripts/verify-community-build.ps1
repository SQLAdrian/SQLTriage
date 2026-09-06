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

# Boundary-aware count of a needle in a byte buffer: a hit only counts when the byte(s)
# immediately AFTER the match do NOT continue a token ([-A-Za-z0-9_]). This distinguishes a
# STANDALONE route literal (e.g. the "/consolidation" route) from the same text appearing as a
# substring of a longer, legitimate token (e.g. the config path "consolidation-model.json"),
# which must NOT trip the fingerprint guard. $stride is 2 for UTF-16LE, 1 for UTF-8.
function Count-StandaloneMatches([byte[]]$Data, [byte[]]$Needle, [int]$Stride) {
    $limit = $Data.Length - $Needle.Length
    $count = 0
    for ($i = 0; $i -le $limit; $i++) {
        $match = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Data[$i + $j] -ne $Needle[$j]) { $match = $false; break }
        }
        if (-not $match) { continue }
        $ni = $i + $Needle.Length
        $nextIsTokenChar = $false
        if ($Stride -eq 2) {
            # UTF-16LE: next char is a token char only if high byte is 0 and low byte is [-A-Za-z0-9_]
            if (($ni + 1) -lt $Data.Length -and $Data[$ni + 1] -eq 0) {
                $c = [char]$Data[$ni]; if ($c -match '[-A-Za-z0-9_]') { $nextIsTokenChar = $true }
            }
        } else {
            if ($ni -lt $Data.Length) {
                $c = [char]$Data[$ni]; if ($c -match '[-A-Za-z0-9_]') { $nextIsTokenChar = $true }
            }
        }
        if (-not $nextIsTokenChar) { $count++ }
    }
    return $count
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

# -- 1b. gated-route-literal scan over the compiled assembly (D-0a regression guard) ----------
# Locks the D-0a fix: the premium/dev-tools route literals (RouteConstants consts + call-sites,
# now #if-fenced) must NOT reappear in the community assembly. The forbidden routes are sourced
# from .handoff/gated-routes.txt (excluded from the public mirror) rather than hardcoded here,
# because THIS script ships to the public repo (.publicallow: scripts/*.ps1) and hardcoded route
# literals would themselves be a public fingerprint. Community mode only; matching is
# boundary-aware so "Config/consolidation-model.json" does not false-positive "/consolidation".
if (-not $ExpectFull) {
    # Public-safety (mirrors the canary check, which passes when the gated token is simply absent):
    # .handoff/ is stripped from the PUBLIC mirror and the gated pages/routes never existed there,
    # so in a public tree there is nothing to scan and the guard must SKIP — otherwise a community
    # build invoked from the public source (this script + buildprofile.targets both ship to public)
    # would break on the missing list. Fail-closed ONLY when .handoff/ IS present (the dev repo) but
    # the list has been deleted/renamed — that is tampering/accident, not the public case.
    $handoffDir      = Join-Path $PSScriptRoot "..\.handoff"
    $gatedRoutesFile = Join-Path $handoffDir "gated-routes.txt"
    if (-not (Test-Path $handoffDir)) {
        Write-Host "verify-community-build: gated-route scan skipped (.handoff/ absent - public tree)" -ForegroundColor DarkGray
    }
    elseif (-not (Test-Path $gatedRoutesFile)) {
        $failures += "gated-route list missing at $gatedRoutesFile - D-0a fingerprint guard cannot run (fail-closed; .handoff/ is present so the list must exist)."
    }
    else {
        $routes = Get-Content $gatedRoutesFile |
                  ForEach-Object { $_.Trim() } |
                  Where-Object { $_ -ne '' -and -not $_.StartsWith('#') }
        foreach ($t in $scanTargets) {
            $data = [System.IO.File]::ReadAllBytes($t)
            foreach ($route in $routes) {
                $u16 = [System.Text.Encoding]::Unicode.GetBytes($route)
                $u8  = [System.Text.Encoding]::UTF8.GetBytes($route)
                $hits = (Count-StandaloneMatches $data $u16 2) + (Count-StandaloneMatches $data $u8 1)
                if ($hits -gt 0) {
                    $failures += "gated route literal found in COMMUNITY assembly $t ($hits standalone hit(s)) - a #if-fenced gated route leaked back in. RouteConstants / a call-site lost its !SQLT_NO_<MODULE> fence, or a new gated page/link was added without one."
                }
            }
        }
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
