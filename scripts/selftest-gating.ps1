# selftest-gating.ps1
# ---------------------------------------------------------------------------
# One-command drift check for the Community Edition gating system.
# Run from the repo root:  powershell -File scripts\selftest-gating.ps1
# Full instructions + what each failure means: .handoff/SELFTEST_GATING.md
#
# Exit 0 = everything holds. Exit 1 = drift detected (read the FAIL lines).
# ---------------------------------------------------------------------------
param(
    [switch]$SkipBuild,
    # Publish/scratch output root for step 4/5 (community publish) and step 6 (curated tree).
    # Defaults to a repo-internal, gitignored path under bin\ - which sits inside this box's
    # antivirus exclusion, unlike %TEMP%. Publishing into %TEMP% made steps 4/5 false-fail (the
    # AV quarantines freshly-written binaries there); the identical gate passes under bin\.
    # Absolute by construction (publish-public's glob-delete needs an absolute staging root).
    [string]$ScratchRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) "bin\_selftest-gating")
)

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
    "Pages\BestPractice.razor", "Pages\ServerConfiguration.razor",
    # The rest of the Server Configuration feature set, licence-bound 2026-08-05: all three drive
    # RemediationRunner.ApplyAsync against a connected production server, the same act
    # ServerConfiguration.razor above was already gated for.
    "Pages\Remediation.razor", "Pages\AgentJobGuard.razor", "Pages\AgentJobSync.razor",
    "Pages\BuildProfile.razor",
    "Pages\RiskReport.razor",
    "Pages\AccessSurface.razor",
    "Pages\SodMatrix.razor",
    "Pages\OffboardingTrace.razor",
    "Pages\PerformanceReport.razor",
    "Pages\CheckValidator.razor", "Pages\RemediationTuner.razor", "Pages\RemediationLab.razor",
    "Pages\TestPlan.razor",
    "Pages\ImportResults.razor",
    "Components\DevTools\PerfLoadWaterfall.razor",
    "Pages\Portal\PublishToPortal.razor",
    "Pages\Portal\PortalStatus.razor",
    "Pages\Portal\ExportPack.razor",
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

# -- 1b. COMPLETENESS: derive the gated .razor set from buildprofile.targets and
#        prove NONE has drifted out of $neverShipFiles (D-0c). The loop above proves
#        every LISTED file is gated; this proves every GATED .razor is LISTED - so a
#        future gated page added to targets but forgotten here cannot slip past Step 1.
#        Scope = the FAIL-CLOSED never-ship groups only: the ItemGroups conditioned on
#        SQLTExcludePremium / SQLTExcludeDevTools (the toggle modules operations/
#        live-monitoring are json-driven and CAN ship, so are deliberately out of scope).
#        Only .razor files that exist ON DISK are required in the list: a Content Remove
#        of a non-existent path is a build no-op that cannot leak (e.g. stale phantom
#        entries), and adding it to the list would trip the Test-Path "missing" fail above.
$gatedGroupNames = 'SQLTExcludePremium', 'SQLTExcludeDevTools'
$listedSet = @{}; foreach ($f in $neverShipFiles) { $listedSet[$f.ToLowerInvariant()] = $true }
$gatedRazor = New-Object System.Collections.Generic.List[string]
foreach ($grp in $gatedGroupNames) {
    # Isolate this group's <ItemGroup Condition="'$(GRP)' == 'true'"> ... </ItemGroup> block.
    $m = [regex]::Match($targets, "<ItemGroup\s+Condition=`"[^`"]*\`$\($grp\)[^`"]*`">(.*?)</ItemGroup>", 'Singleline')
    if (-not $m.Success) { Fail "buildprofile.targets: could not locate the $grp ItemGroup (parser drift - completeness check is blind)"; continue }
    foreach ($rm in [regex]::Matches($m.Groups[1].Value, '<(?:Content|Compile)\s+Remove="([^"]+\.razor)"')) {
        $gatedRazor.Add($rm.Groups[1].Value)
    }
}
foreach ($g in ($gatedRazor | Sort-Object -Unique)) {
    if (-not (Test-Path $g)) { continue }   # phantom/no-op exclusion of a non-existent file; cannot leak
    if (-not $listedSet.ContainsKey($g.ToLowerInvariant())) {
        Fail "$g is a gated .razor in buildprofile.targets but is MISSING from `$neverShipFiles - Step 1 would never verify its canary/exclusion (drift). Add it (with its GatedCanary const)."
    }
}
if ($fails.Count -eq 0) { Ok "completeness: every on-disk gated .razor in buildprofile.targets ($(($gatedRazor | Sort-Object -Unique | Where-Object { Test-Path $_ }).Count)) is present in `$neverShipFiles" }

# -- 1c. PORTAL completeness (D0-scaffold): the portal exclusion group uses DIRECTORY GLOBS
#        (Pages\Portal\**, Components\Portal\**, Data\Services\Portal\**), not per-file literal
#        Removes, so 1b's literal-path parse cannot enumerate it. Enumerate every portal .razor
#        ON DISK instead and require each in $neverShipFiles - so Step 1 verifies its canary +
#        targets exclusion + .publicignore deny, and a FUTURE portal page (D1/D2+) dropped under
#        these trees cannot slip past the self-test. (The glob already makes it fail-closed in the
#        BINARY; this enforces the per-page canary backstop + the source deny.)
$portalDirs = 'Pages\Portal', 'Components\Portal', 'Data\Services\Portal'
$portalCount = 0
foreach ($pd in $portalDirs) {
    if (-not (Test-Path $pd)) { continue }
    foreach ($pf in (Get-ChildItem -Path $pd -Recurse -File -Filter *.razor -ErrorAction SilentlyContinue)) {
        $portalCount++
        $rel = $pf.FullName.Substring($repo.Length + 1)
        if (-not $listedSet.ContainsKey($rel.ToLowerInvariant())) {
            Fail "$rel is a portal .razor on disk but is MISSING from `$neverShipFiles - every portal page must be listed so Step 1 verifies its GatedCanary + targets exclusion + .publicignore deny (drift)."
        }
    }
}
if ($fails.Count -eq 0) { Ok "portal completeness: every on-disk portal .razor ($portalCount) is present in `$neverShipFiles" }

# -- 1d. PORTAL code-behind/service .cs must be Compile-Removed for ALL THREE prefixes
#        (Phase D review R5): 1c only enumerates *.razor, so a Components\Portal\*.razor.cs (or any
#        service .cs) - an ordinary Compile item - with no matching .razor could compile its
#        strings/types into the community DLL undetected. Assert the targets file Compile-Removes
#        the .cs glob under each portal prefix, so the fail-closed-by-location contract holds for
#        code-behind too, not just markup. (This is the structural check that catches the exact
#        gap where Components\Portal had a .razor Content-Remove but no .cs Compile-Remove.)
$portalCsPrefixes = 'Pages\Portal', 'Components\Portal', 'Data\Services\Portal'
foreach ($px in $portalCsPrefixes) {
    $escaped = [Regex]::Escape($px)
    $pattern = 'Compile\s+Remove="' + $escaped + '\\\*\*\\\*\.cs"'
    if ($targets -notmatch $pattern) {
        Fail "buildprofile.targets does not Compile-Remove '$px\**\*.cs' - a code-behind/service .cs under $px would compile into the community DLL (portal fingerprint leak). Add <Compile Remove=`"$px\**\*.cs`" /> to the SQLTExcludePortal group."
    }
}
if ($fails.Count -eq 0) { Ok "portal code-behind: all 3 portal prefixes Compile-Remove **\*.cs in buildprofile.targets" }

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

    # -- 3b. community TEST-project compile guard: no SHIPPED test may bind a gated type -------
    # The test csproj Compile-Removes the gated-module tests under SQLTriageProfile=community
    # (Mcp / RiskAssessmentPdf / AccessSurface / Portal). If a new test news up a gated
    # engine without being added there, the community test build breaks with CS0246 - and worse,
    # that source ships in the public curated tree, advertising the paid API surface. Catch it
    # here (runs after Step 3 reads the FULL obj DLL, so it can safely rebuild obj as community).
    Step "3b/6 community test-project compile guard (no shipped test binds a gated type)"
    & dotnet build "Tests\SQLTriage.Tests\SQLTriage.Tests.csproj" -c Debug -p:SQLTriageProfile=community -nologo -clp:ErrorsOnly -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "community test build broken - a gated type is referenced unfenced by a shipped test (add its .cs to the SQLTriageProfile=community Compile-Remove ItemGroup in SQLTriage.Tests.csproj + deny it in .publicignore)" }
    else { Ok "community test-project compiles - no shipped test binds a gated type" }
}

# -- 4. community publish (the hard gate runs automatically inside it) --------
Step "4/6 community publish + automatic fails-when-violated gate"
$outDir = Join-Path $ScratchRoot "community"
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
& dotnet publish SQLTriage.csproj -c Debug -p:SQLTriageProfile=community -o $outDir -nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "community publish failed - either a compile break or the embedded verify gate fired (re-run without -v quiet for detail)" }
else { Ok "community publish green (verify gate passed inside the publish)" }

# -- 5. belt-and-braces: run the verifier explicitly on the output ------------
Step "5/6 explicit verify over the community output"
# Unconditional. This used to run ONLY when a loose SQLTriage.dll happened to sit in
# the output - which is precisely the shape that does NOT exist in the Release publish
# that actually ships, so the belt-and-braces check quietly skipped itself exactly where
# it was most needed. The guard now decides what the artefact is and fails when it
# cannot scan it; a caller must not pre-decide that for it.
& powershell -NoProfile -File "scripts\verify-community-build.ps1" -PublishDir $outDir
if ($LASTEXITCODE -ne 0) { Fail "explicit community verification failed" }
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue

# -- 6. public-tree gates: positive run + four negative controls ---------------
# Curate a real tree (publish-public -CurateOnly), prove the gates PASS on it,
# then plant (a) a stray file, (b) a gated page source, (c) a commercial string,
# (d) a private-repo pointer, and prove each one FAILS. (a)-(c) are the option-C
# acceptance controls; (d) is the 2026-09-05 private-ref gate.
Step "6/6 public-tree gates (allow-list / canary / language / private-ref) + negative controls"
$pubTree = Join-Path $ScratchRoot "pubtree"
& powershell -NoProfile -ExecutionPolicy Bypass -File "publish-public.ps1" -CurateOnly -CurateOutDir $pubTree -WorkRoot $ScratchRoot | Out-Null
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

        # (d) private-repo pointer in an allowed file -> private-ref gate must fire.
        # (c) leaves its plant in README.md, so the tree is already failing when we get
        # here. Rather than unpick that, (d) asserts on the gate's OUTPUT: a non-zero
        # exit is not enough, a PRIVATE-REF line must name the file we planted into.
        # That makes the control independent of (c) and stricter than (a)-(c).
        #
        # The plant is READ FROM the deny list, not written here. THIS script ships,
        # so a literal pointer in it would put into the public tree the very string
        # gate 5 exists to keep out - and splitting it across a concatenation only
        # hides it from the grep, not from a reader. Reading it also means the
        # control follows the deny list instead of drifting from it. Take the first
        # metacharacter-free entry; the rest are regexes and will not plant as text.
        $refPlant = Get-Content ".handoff\.publicprivateref-deny" |
            Where-Object { $_ -and ($_ -notmatch '^\s*#') -and ($_ -match '\S') } |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -notmatch '[\\^$.|?*+()\[\]{}]' } |
            Select-Object -First 1
        if (-not $refPlant) {
            Fail "NEGATIVE (d): no literal pattern in the private-ref deny list to plant with"
        } else {
            Add-Content (Join-Path $pubTree "CHANGELOG.md") "resume plan: $refPlant"
            $refOut = & powershell -NoProfile -File "scripts\verify-public-tree.ps1" -TreePath $pubTree *>&1
            if ($LASTEXITCODE -eq 0 -or -not ($refOut -match 'PRIVATE-REF: CHANGELOG\.md')) {
                Fail "NEGATIVE (d): planted private-repo pointer was not reported by the private-ref gate"
            } else { Ok "negative (d): private-repo pointer caught by the private-ref gate" }
        }
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
