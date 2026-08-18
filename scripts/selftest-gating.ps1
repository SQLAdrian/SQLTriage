# selftest-gating.ps1
# ---------------------------------------------------------------------------
# One-command drift check for the Community Edition gating system.
# Run from the repo root:  powershell -File scripts\selftest-gating.ps1
# Full instructions + what each failure means: .handoff/SELFTEST_GATING.md
#
# Exit 0 = everything holds. Exit 1 = drift detected (read the FAIL lines).
# ---------------------------------------------------------------------------
param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$fails = @()
function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }
function Fail($msg) { $script:fails += $msg; Write-Host "  FAIL $msg" -ForegroundColor Red }
function Ok($msg)   { Write-Host "  OK   $msg" -ForegroundColor Green }

# Canary token assembled from parts (this script ships publicly; the contiguous
# literal must only exist in the gated pages).
$canary = "SQLT-GATED-DNA-" + "NEVERSHIP"

# The never-ship set: premium + dev-tools pages AND commercial/upsell components
# (relative .razor paths). MUST mirror buildprofile.targets.
$neverShipFiles = @(
    "Pages\Premium.razor", "Pages\CapacityConsolidation.razor", "Pages\AdvancedReporting.razor",
    "Pages\BuildProfile.razor",
    "Pages\CheckValidator.razor", "Pages\RemediationTuner.razor", "Pages\TestPlan.razor",
    "Pages\PerfInspector.razor",
    "Components\Shared\ActivateFullAuditCard.razor",
    "Components\Shared\PremiumLockCard.razor",
    "Components\Shared\FullAuditUpsellPill.razor"
)

# -- 1. static sync checks (cheap; run first) ---------------------------------
Step "1/6 static sync: never-ship files <-> buildprofile.targets <-> .publicignore <-> canary"
$targets = Get-Content "buildprofile.targets" -Raw
$pubignore = Get-Content ".handoff\.publicignore" -Raw -ErrorAction SilentlyContinue

foreach ($f in $neverShipFiles) {
    $fwd = $f -replace '\\', '/'
    if (-not (Test-Path $f)) { Fail "$f missing on disk (renamed? update buildprofile.targets + this list)"; continue }
    if ((Get-Content $f -Raw) -notmatch [regex]::Escape($canary)) {
        Fail "$f lacks the GatedCanary const - binary canary scan is blind to this file"
    }
    if ($targets -notmatch [regex]::Escape($f)) {
        Fail "$f not excluded in buildprofile.targets - it would COMPILE INTO the community build"
    }
    if ($pubignore -notmatch [regex]::Escape($fwd)) {
        Fail "$fwd not listed in .handoff/.publicignore - its SOURCE would reach the public repo"
    }
}
if ($fails.Count -eq 0) { Ok "all $($neverShipFiles.Count) never-ship files consistent across the three control points" }

# publish-public.ps1 must carry the community flag + invoke the tree/rebuild gates;
# the canary grep patterns live in verify-public-tree.ps1 (split-token form, since
# that script ships publicly - this check matches the split form, and this script
# avoids the contiguous corpus token for the same reason).
$pubScript = Get-Content "publish-public.ps1" -Raw
if ($pubScript -notmatch "SQLTriageProfile=community")  { Fail "publish-public.ps1 lost the -p:SQLTriageProfile=community flag - public releases would ship FULL binaries" }
if ($pubScript -notmatch "verify-public-tree\.ps1")     { Fail "publish-public.ps1 no longer invokes the tree gates (verify-public-tree.ps1)" }
if ($pubScript -notmatch "Rebuild gate")                { Fail "publish-public.ps1 lost the rebuild gate (community publish from the curated tree)" }
$treeScript = Get-Content "scripts\verify-public-tree.ps1" -Raw
if ($treeScript -notmatch ('SQLT-GATED-DNA-"\s*\+\s*"NEVERSHIP'))   { Fail "verify-public-tree.ps1 lost the gated-page canary pattern (split form)" }
if ($treeScript -notmatch ('SQLT-CORPUS"\s*\+\s*"' + '-DNA-'))      { Fail "verify-public-tree.ps1 lost the corpus DNA canary pattern (split form)" }

# -- 2. full build + test suite (incl. corpus<->parser drift canary) ----------
if ($SkipBuild) {
    Step "2/6 + 3/6 build/test SKIPPED (-SkipBuild)"
} else {
    Step "2/6 full build + test suite (corpus integration test = corpus<->parser drift canary)"
    & dotnet build SQLTriage.sln -nologo -clp:ErrorsOnly -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "full build broken" }
    else {
        $t = & dotnet test SQLTriage.sln --nologo --no-build 2>&1 | Out-String
        if ($t -match "Failed:\s+0,") { Ok "test suite fully green" }
        else { Fail "test suite not green - 0-failed is the contract since 2026-06-12 (1ed3e0c); triage before anything else" }
    }

    # -- 3. canary positive control: full assembly MUST contain the token ----
    Step "3/6 canary positive control (full assembly must contain the token)"
    $fullDll = "obj\Debug\net10.0-windows\win-x64\SQLTriage.dll"
    & powershell -NoProfile -File "scripts\verify-community-build.ps1" -DllPath $fullDll -ExpectFull
    if ($LASTEXITCODE -ne 0) { Fail "canary mechanism broken on full build (token renamed/removed?)" }
}

# -- 4. community publish (the hard gate runs automatically inside it) --------
Step "4/6 community publish + automatic fails-when-violated gate"
$outDir = Join-Path $env:TEMP "sqlt-selftest-community"
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
& dotnet publish SQLTriage.csproj -c Debug -p:SQLTriageProfile=community -o $outDir -nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "community publish failed - either a compile break or the embedded verify gate fired (re-run without -v quiet for detail)" }
else { Ok "community publish green (verify gate passed inside the publish)" }

# -- 5. belt-and-braces: run the verifier explicitly on the output ------------
Step "5/6 explicit verify over the community output"
if (Test-Path (Join-Path $outDir "SQLTriage.dll")) {
    & powershell -NoProfile -File "scripts\verify-community-build.ps1" -DllPath (Join-Path $outDir "SQLTriage.dll") -PublishDir $outDir
    if ($LASTEXITCODE -ne 0) { Fail "explicit community verification failed" }
}
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue

# -- 6. public-tree gates: positive run + three negative controls --------------
# Curate a real tree (publish-public -CurateOnly), prove the gates PASS on it,
# then plant (a) a stray file, (b) a gated page source, (c) a commercial string
# and prove each one FAILS. These are the option-C acceptance controls.
Step "6/6 public-tree gates (allow-list / canary / language) + negative controls"
$pubTree = Join-Path $env:TEMP "sqlt-selftest-pubtree"
& powershell -NoProfile -ExecutionPolicy Bypass -File "publish-public.ps1" -CurateOnly -CurateOutDir $pubTree | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "publish-public -CurateOnly failed - cannot exercise the tree gates" }
else {
    function Invoke-TreeGates { & powershell -NoProfile -File "scripts\verify-public-tree.ps1" -TreePath $pubTree *>&1 | Out-Null; return $LASTEXITCODE }

    if ((Invoke-TreeGates) -ne 0) {
        & powershell -NoProfile -File "scripts\verify-public-tree.ps1" -TreePath $pubTree
        Fail "tree gates FAILED on a clean curated tree - fix before publishing (output above)"
    } else {
        Ok "tree gates pass on a clean curated tree"

        # (a) stray file -> allow-list gate must fire
        $plant = Join-Path $pubTree "zz-stray-plant.tmp"
        New-Item -ItemType File -Path $plant -Force | Out-Null
        if ((Invoke-TreeGates) -eq 0) { Fail "NEGATIVE (a): planted stray file passed the allow-list gate" }
        else { Ok "negative (a): stray file caught by the allow-list gate" }
        Remove-Item $plant -Force

        # (b) gated page source -> canary grep must fire (allow-list alone would pass Pages/*.razor)
        Copy-Item "Pages\Premium.razor" (Join-Path $pubTree "Pages\Premium.razor") -Force
        if ((Invoke-TreeGates) -eq 0) { Fail "NEGATIVE (b): planted gated page source passed the gates" }
        else { Ok "negative (b): gated page source caught (canary grep)" }
        Remove-Item (Join-Path $pubTree "Pages\Premium.razor") -Force

        # (c) commercial string in an allowed file -> language gate must fire
        Add-Content (Join-Path $pubTree "README.md") "premium upsell plant"
        if ((Invoke-TreeGates) -eq 0) { Fail "NEGATIVE (c): planted commercial string passed the language gate" }
        else { Ok "negative (c): commercial string caught by the language gate" }
    }
}
Remove-Item $pubTree -Recurse -Force -ErrorAction SilentlyContinue

# -- verdict -------------------------------------------------------------------
Write-Host ""
if ($fails.Count -gt 0) {
    Write-Host "=== GATING SELF-TEST: $($fails.Count) FAILURE(S) ===" -ForegroundColor Red
    $fails | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "=== GATING SELF-TEST: ALL CLEAR ===" -ForegroundColor Green
exit 0
