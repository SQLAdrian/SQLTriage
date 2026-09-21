# census-community-surface.ps1
# ---------------------------------------------------------------------------
# THE INVARIANT THIS FILE MEASURES - stated first, because the next narrow agent
# will change the thing below it and needs the rule, not the instance:
#
#   ** The community profile boundary is verified by something that measures the
#      artefact that SHIPS, and an artefact that cannot be measured FAILS. **
#
# This is the PRIMARY gate (Adrian's ruling 2026-09-11 18:40, option C: a
# compile-time source/route census, with the output scans as corroboration).
# It answers, from the code and from MSBuild's own evaluation of the build being
# gated: WHICH sources is the community profile actually compiling, and does any
# never-ship route literal survive into them? It runs BEFORE the compiler emits a
# byte, so compression, single-file bundling and a shared obj\ cannot blind it.
#
# WHAT RUNS IT, AND WHAT GOES RED WHEN IT DOES NOT:
#   - buildprofile.targets, target SQLTriageCommunitySurfaceCensus,
#     BeforeTargets="CoreCompile", community profile only, no ContinueOnError:
#     every community build AND publish runs it and FAILS on a violation.
#   - Tests\SQLTriage.Tests\CommunitySurfaceCensusTests.cs pins BOTH the wiring
#     and the reddening. If someone deletes the target, the test
#     The_census_is_wired_into_the_community_build goes red. If someone makes the
#     census pass on a known-bad tree, The_census_reddens_when_a_never_ship_source_is_compiled_in
#     goes red. Those test names are asserted to EXIST by
#     Every_test_name_cited_in_the_census_sources_exists - an unverified test name
#     in a comment is how a guard decays into prose.
#
# FAIL-CLOSED, ALWAYS. Absent surface file, empty item list, a surface that is not
# a community evaluation, a parser that stops matching, a dev tree with nothing
# marked never-ship: each is a LOUD FAILURE, never a skip. "Skip it" is how the
# byte-scan guard came to pass a 174 MB full-profile build as community.
#
# PUBLIC-TREE SAFETY: this script ships to the public repo (.handoff/.publicallow
# allows scripts/*.ps1), so it carries NO gated route literal, NO gated page name
# and never the contiguous canary token (assembled from parts below, same reason
# verify-community-build.ps1 does it). The never-ship set is derived from the tree
# at run time. In a curated public tree there are no never-ship sources at all;
# the census says so explicitly and passes - and FAILS if it finds one there.
#
# (!!) AND NOTE WHAT THAT MEANS FOR THE ".handoff\ IS ABSENT" BRANCH, because the
# obvious worry is that deleting .handoff\ buys a free pass: it does not. Deleting
# it in a DEV tree leaves the 26 canary-bearing sources exactly where they were, and
# the public-tree branch then fails LOUDLY on their presence. The two branches are
# not "strict" and "lenient"; they are two ways of failing, and there is no third
# way of passing. Pinned by
# The_census_passes_a_curated_public_tree_and_fails_one_carrying_gated_sources.
#
# BLIND SPOT, NAMED RATHER THAN LEFT TO BE DISCOVERED: the never-ship set is the
# set of source files carrying the gated canary. A NEW gated page added without
# the canary is invisible here - exactly as it is invisible to the byte scan,
# which looks for the same token. This census does not close that hole; it closes
# the one where a canary-bearing page is compiled in anyway, and the one where a
# never-ship route literal survives in a file that IS compiled.
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$SurfaceFile,
    [string]$RepoRoot,
    [string]$JsonOut,
    # Provenance, applied to the census's OWN input. The build target mints a token
    # per invocation, writes it into the surface and passes it here, so the census
    # reads the file THIS build wrote - never a stale copy, never a concurrent
    # build's. Pinning the surface by path alone would be the same defect this lane
    # exists to fix, one layer up.
    [string]$ExpectToken
)

$ErrorActionPreference = "Stop"
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$failures = New-Object System.Collections.Generic.List[string]
$notes    = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$m) { $script:failures.Add($m) }

# -- 0. the surface file: ABSENT or EMPTY is a FAILURE, never a skip -----------
if (-not (Test-Path $SurfaceFile)) {
    Write-Host "CENSUS FAIL: build surface file ABSENT: $SurfaceFile" -ForegroundColor Red
    Write-Host "  The census cannot state what the community profile compiles, so it must not pass." -ForegroundColor Red
    exit 1
}
$surfaceLines = @(Get-Content $SurfaceFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' -and -not $_.StartsWith('#') })
if ($surfaceLines.Count -eq 0) {
    Write-Host "CENSUS FAIL: build surface file is EMPTY: $SurfaceFile" -ForegroundColor Red
    exit 1
}

$props = @{}
$contentRazor   = New-Object System.Collections.Generic.List[string]
$razorComponent = New-Object System.Collections.Generic.List[string]
$compileCs      = New-Object System.Collections.Generic.List[string]
foreach ($l in $surfaceLines) {
    if ($l.StartsWith('P|')) {
        $kv = $l.Substring(2)
        $i = $kv.IndexOf('=')
        if ($i -ge 0) { $props[$kv.Substring(0, $i)] = $kv.Substring($i + 1) }
    }
    # C| = a Content item with extension .razor; R| = a RazorComponent item, which the Razor SDK
    # derives FROM Content inside a real build and which the evaluation-only emitter cannot see.
    # Both are counted separately for the report and merged for scanning: R| is additive
    # defence-in-depth, so a page that reached the compiler as a RazorComponent without a matching
    # Content item is still caught.
    elseif ($l.StartsWith('C|')) { $contentRazor.Add($l.Substring(2)) }
    elseif ($l.StartsWith('R|')) { $razorComponent.Add($l.Substring(2)) }
    elseif ($l.StartsWith('S|')) { $compileCs.Add($l.Substring(2)) }
}

# Known and deliberate: two community builds running concurrently in the SAME worktree write the
# same surface path, so one of them can see the other's token and FAIL. That is the safe direction -
# a spurious red, never a green rendered about a file this build did not write - and it is the exact
# failure mode the byte-scan guard had backwards (defect 1: a concurrent build's bytes read as this
# build's, verdict rendered, publish reddened or passed on the wrong file).
if ($ExpectToken) {
    $seen = [string]$props['BuildToken']
    if ($seen -ne $ExpectToken) {
        Write-Host "CENSUS FAIL: surface file $SurfaceFile carries BuildToken '$seen', not this build's '$ExpectToken'." -ForegroundColor Red
        Write-Host "  It is a stale or foreign surface. The census refuses to render a verdict about a file it cannot tie to this build." -ForegroundColor Red
        exit 1
    }
}

# -- 1. PROVENANCE, not name: prove this surface is a COMMUNITY evaluation -----
# The whole lane exists because a guard trusted a path that merely TENDED to hold
# the right file. A surface file is trusted only when it SAYS what it is and the
# saying is checked.
$profileName = [string]$props['SQLTriageProfile']
$defines = [string]$props['DefineConstants']
$symbols = @($defines -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
if ($profileName -ne 'community') {
    Write-Host "CENSUS FAIL: surface declares SQLTriageProfile='$profileName', not 'community' ($SurfaceFile)" -ForegroundColor Red
    Write-Host "  This census only applies to a community evaluation. It will not pass on a surface it cannot vouch for." -ForegroundColor Red
    exit 1
}
if ($symbols -notcontains 'SQLT_COMMUNITY') {
    Write-Host "CENSUS FAIL: surface declares profile 'community' but DefineConstants has no SQLT_COMMUNITY symbol." -ForegroundColor Red
    Write-Host "  DefineConstants seen: '$defines'" -ForegroundColor Red
    exit 1
}
if ($contentRazor.Count -eq 0 -and $razorComponent.Count -eq 0 -and $compileCs.Count -eq 0) {
    Write-Host "CENSUS FAIL: surface lists ZERO Compile and ZERO Content items - an empty census cannot pass." -ForegroundColor Red
    exit 1
}

$compiledSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($x in $contentRazor)   { [void]$compiledSet.Add($x.Replace('/', '\')) }
foreach ($x in $razorComponent) { [void]$compiledSet.Add($x.Replace('/', '\')) }
foreach ($x in $compileCs)      { [void]$compiledSet.Add($x.Replace('/', '\')) }

# -- 2. the tree census: which sources are marked NEVER-SHIP ------------------
# Assembled from parts: the contiguous token must never appear in a file that
# ships to the public repo.
$canary = "SQLT-GATED-DNA-" + "NEVERSHIP"
$excludedDirs = @('bin', 'obj', '.git', '.vs', 'node_modules', 'Tests', 'tools', 'publish', 'release',
                  'evidence', 'corpus', 'lib', 'llmck', 'packages', 'TestResults')
function Get-ProductionSources([string]$root) {
    $out = New-Object System.Collections.Generic.List[string]
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($root)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        foreach ($sub in [System.IO.Directory]::GetDirectories($dir)) {
            $name = Split-Path $sub -Leaf
            if ($excludedDirs -contains $name) { continue }
            $stack.Push($sub)
        }
        foreach ($f in [System.IO.Directory]::GetFiles($dir)) {
            $e = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
            if ($e -eq '.razor' -or $e -eq '.cs') { $out.Add($f.Substring($root.Length).TrimStart('\')) }
        }
    }
    return $out
}
$RepoRoot = (Resolve-Path $RepoRoot).Path
$sources = Get-ProductionSources $RepoRoot
if ($sources.Count -eq 0) {
    Write-Host "CENSUS FAIL: no .razor/.cs production sources found under $RepoRoot - the enumerator is broken or the root is wrong." -ForegroundColor Red
    exit 1
}

$textCache = @{}
function Get-SourceText([string]$rel) {
    if (-not $script:textCache.ContainsKey($rel)) {
        $script:textCache[$rel] = [System.IO.File]::ReadAllText((Join-Path $RepoRoot $rel))
    }
    return $script:textCache[$rel]
}

$neverShip = New-Object System.Collections.Generic.List[string]
foreach ($rel in $sources) {
    if ((Get-SourceText $rel).Contains($canary)) { $neverShip.Add($rel) }
}

$devTree = Test-Path (Join-Path $RepoRoot '.handoff')

# -- 3. CHECK A: no never-ship source may be in the community item set --------
# This is the load-bearing check and it does not read buildprofile.targets at all:
# the marking lives in the source, the item set comes from MSBuild's evaluation of
# the build being gated. Delete an exclusion and this goes red on the next build.
foreach ($f in $neverShip) {
    if ($compiledSet.Contains($f)) {
        Add-Failure "never-ship source COMPILED INTO THE COMMUNITY PROFILE: $f (it carries the gated canary; buildprofile.targets has no effective exclusion for it)"
    }
}

# -- 4. the route census: never-ship routes derived FROM THE CODE -------------
$pageRx = New-Object System.Text.RegularExpressions.Regex '@page\s+"([^"]+)"'
$neverShipRoutes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$templated       = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$pageDirectives  = 0
foreach ($rel in $neverShip) {
    foreach ($m in $pageRx.Matches((Get-SourceText $rel))) {
        $pageDirectives++
        $r = $m.Groups[1].Value
        # A route template with a parameter cannot be literal-matched; it is reported,
        # never silently dropped.
        if ($r.Contains('{')) { [void]$templated.Add($r) } else { [void]$neverShipRoutes.Add($r) }
    }
}

# -- 5. compile-time pruning model: #if regions and BuildModules const branches -
function Test-SymbolExpression([string]$expr) {
    $e = $expr
    $c = $e.IndexOf('//'); if ($c -ge 0) { $e = $e.Substring(0, $c) }
    $e = $e.Trim()
    if ($e -eq '') { return $null }
    $rx = New-Object System.Text.RegularExpressions.Regex '[A-Za-z_][A-Za-z0-9_]*'
    # -Unique BEFORE the sort, deliberately: "Sort-Object -Property Length -Unique" dedupes by the
    # SORT KEY, so two different symbols of equal length would silently lose one of them.
    $tokens = @($rx.Matches($e) | ForEach-Object { $_.Value } | Select-Object -Unique | Sort-Object -Property Length -Descending)
    $work = $e
    foreach ($t in $tokens) {
        $val = '0'; if ($symbols -contains $t) { $val = '1' }
        $work = [System.Text.RegularExpressions.Regex]::Replace($work, ('(?<![A-Za-z0-9_])' + [regex]::Escape($t) + '(?![A-Za-z0-9_])'), $val)
    }
    # Only the shapes this tree actually uses: A, !A, A && B, A || B, parentheses.
    if ($work -notmatch '^[01\s\(\)!&\|]+$') { return $null }
    $work = $work.Replace('&&', ' -and ').Replace('||', ' -or ').Replace('!', ' -not ').Replace('1', '$true').Replace('0', '$false')
    try { return [bool](Invoke-Expression $work) } catch { return $null }
}

# Per-line liveness under the #if/#else/#endif tree. Directive lines themselves are dead.
function Get-LineLivenessMap([string]$body, [ref]$parseErrors) {
    $lines = $body -split "`n"
    $live = New-Object 'bool[]' $lines.Count
    $stack = New-Object System.Collections.Generic.Stack[bool]
    $stack.Push($true)
    $taken = New-Object System.Collections.Generic.Stack[bool]
    $taken.Push($true)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $s = $lines[$i].Trim()
        if ($s -match '^#\s*(if|elif|else|endif)\b(.*)$') {
            $kw = $Matches[1]; $rest = $Matches[2]
            if ($kw -eq 'if') {
                $parent = $stack.Peek()
                $v = Test-SymbolExpression $rest
                if ($null -eq $v) { $parseErrors.Value.Add("unparsable #if expression on line $($i + 1): '$($rest.Trim())'"); $v = $true }
                $stack.Push(($parent -and $v)); $taken.Push([bool]$v)
            }
            elseif ($kw -eq 'elif') {
                [void]$stack.Pop(); $wasTaken = $taken.Pop()
                $parent = $stack.Peek()
                $v = Test-SymbolExpression $rest
                if ($null -eq $v) { $parseErrors.Value.Add("unparsable #elif expression on line $($i + 1): '$($rest.Trim())'"); $v = $true }
                $stack.Push(($parent -and (-not $wasTaken) -and $v)); $taken.Push([bool]($wasTaken -or $v))
            }
            elseif ($kw -eq 'else') {
                [void]$stack.Pop(); $wasTaken = $taken.Pop()
                $parent = $stack.Peek()
                $stack.Push(($parent -and (-not $wasTaken))); $taken.Push($true)
            }
            else {
                if ($stack.Count -gt 1) { [void]$stack.Pop(); [void]$taken.Pop() }
                else { $parseErrors.Value.Add("#endif with no matching #if on line $($i + 1)") }
            }
            $live[$i] = $false
        }
        else { $live[$i] = $stack.Peek() }
    }
    if ($stack.Count -ne 1) { $parseErrors.Value.Add("unbalanced #if/#endif (depth $($stack.Count - 1) at end of file)") }
    return $live
}

# BuildModules.* const values under THIS surface's symbols, read from the source of
# truth rather than restated here.
$bmPath = Join-Path $RepoRoot 'Data\BuildModules.cs'
$bmValues = @{}
if (-not (Test-Path $bmPath)) {
    Add-Failure "Data\BuildModules.cs is ABSENT - the compile-time pruning model cannot be built, so the census cannot pass."
}
else {
    $bmErrors = New-Object System.Collections.Generic.List[string]
    $bmText = [System.IO.File]::ReadAllText($bmPath)
    $bmLines = $bmText -split "`n"
    $bmLive = Get-LineLivenessMap $bmText ([ref]$bmErrors)
    foreach ($e in $bmErrors) { Add-Failure "Data\BuildModules.cs: $e" }
    $constRx = New-Object System.Text.RegularExpressions.Regex 'const\s+bool\s+(\w+)\s*=\s*(true|false)\s*;'
    for ($i = 0; $i -lt $bmLines.Count; $i++) {
        if (-not $bmLive[$i]) { continue }
        $m = $constRx.Match($bmLines[$i])
        if ($m.Success) { $bmValues[$m.Groups[1].Value] = ($m.Groups[2].Value -eq 'true') }
    }
}

# -- 6. CHECK B: no never-ship route literal survives in a compiled source ----
# An occurrence the compiler removes is not a leak: a line inside a dead #if
# region, or markup inside an @if (BuildModules.X) block whose X is a const false.
# Both are modelled explicitly; anything the model cannot parse is a FAILURE, not
# an assumption.
$guardRx = New-Object System.Text.RegularExpressions.Regex '@?\bif\s*\(\s*(!?)\s*BuildModules\.(?:Reports\.)?(\w+)\s*\)'
$tokenChar = New-Object System.Text.RegularExpressions.Regex '[-A-Za-z0-9_]'
$blankOut = { param($m) ([System.Text.RegularExpressions.Regex]::Replace($m.Value, '[^\n]', ' ')) }

function Remove-SourceComments([string]$t, [string]$ext) {
    if ($ext -eq '.cs') {
        $t = [System.Text.RegularExpressions.Regex]::Replace($t, '/\*[\s\S]*?\*/', $script:blankOut)
        $t = [System.Text.RegularExpressions.Regex]::Replace($t, '//[^\n]*', $script:blankOut)
    } else {
        $t = [System.Text.RegularExpressions.Regex]::Replace($t, '@\*[\s\S]*?\*@', $script:blankOut)
        $t = [System.Text.RegularExpressions.Regex]::Replace($t, '<!--[\s\S]*?-->', $script:blankOut)
        $t = [System.Text.RegularExpressions.Regex]::Replace($t, '/\*[\s\S]*?\*/', $script:blankOut)
        $t = [System.Text.RegularExpressions.Regex]::Replace($t, '(?m)^[ \t]*//[^\n]*', $script:blankOut)
    }
    return $t
}

$violations = New-Object System.Collections.Generic.List[string]
$prunedHits = New-Object System.Collections.Generic.List[string]
$parseFail  = New-Object System.Collections.Generic.List[string]
$guardsSeen = 0
$scannedFiles = 0
$routeList = @($neverShipRoutes)

foreach ($rel in $sources) {
    if (-not $compiledSet.Contains($rel)) { continue }
    $ext = [System.IO.Path]::GetExtension($rel).ToLowerInvariant()
    $body = Remove-SourceComments (Get-SourceText $rel) $ext
    $scannedFiles++

    $lineLive = $null
    if ($ext -eq '.cs') {
        $errs = New-Object System.Collections.Generic.List[string]
        $lineLive = Get-LineLivenessMap $body ([ref]$errs)
        foreach ($e in $errs) { $parseFail.Add(($rel + ': ' + $e)) }
    }

    # const-pruned spans: @if (BuildModules.X) { ... } where X is a const false
    $pruned = New-Object System.Collections.Generic.List[int[]]
    foreach ($g in $guardRx.Matches($body)) {
        $guardsSeen++
        $neg = ($g.Groups[1].Value -eq '!')
        $name = $g.Groups[2].Value
        if (-not $bmValues.ContainsKey($name)) {
            $parseFail.Add(($rel + ': BuildModules.' + $name + ' is referenced but has no const value under this profile - the BuildModules reader is stale.'))
            continue
        }
        $v = $bmValues[$name]
        if ($neg) { $v = -not $v }
        if ($v) { continue }
        $j = $g.Index + $g.Length
        while ($j -lt $body.Length -and ($body[$j] -eq ' ' -or $body[$j] -eq "`t" -or $body[$j] -eq "`r" -or $body[$j] -eq "`n")) { $j++ }
        if ($j -ge $body.Length -or $body[$j] -ne '{') {
            $parseFail.Add(($rel + ': @if (BuildModules.' + $name + ') at offset ' + $g.Index + ' is not followed by a block - cannot prove what the compiler prunes.'))
            continue
        }
        $depth = 0; $k = $j
        while ($k -lt $body.Length) {
            if ($body[$k] -eq '{') { $depth++ }
            elseif ($body[$k] -eq '}') { $depth--; if ($depth -eq 0) { break } }
            $k++
        }
        if ($depth -ne 0) {
            $parseFail.Add(($rel + ': unbalanced braces after @if (BuildModules.' + $name + ') - cannot prove what the compiler prunes.'))
            continue
        }
        $pruned.Add(@($g.Index, $k))
    }

    foreach ($route in $routeList) {
        $start = 0
        while ($true) {
            $i = $body.IndexOf($route, $start, [System.StringComparison]::Ordinal)
            if ($i -lt 0) { break }
            $start = $i + 1
            $after = $i + $route.Length
            if ($after -lt $body.Length -and $tokenChar.IsMatch([string]$body[$after])) { continue }
            $lineNo = 1
            for ($p = 0; $p -lt $i; $p++) { if ($body[$p] -eq "`n") { $lineNo++ } }
            $inPruned = $false
            foreach ($sp in $pruned) { if ($i -ge $sp[0] -and $i -le $sp[1]) { $inPruned = $true; break } }
            $deadIf = $false
            if ($null -ne $lineLive -and ($lineNo - 1) -lt $lineLive.Count) { $deadIf = -not $lineLive[$lineNo - 1] }
            if ($inPruned) { $prunedHits.Add(($rel + ':' + $lineNo + ' ' + $route + ' (inside an @if (BuildModules.*) block the compiler prunes)')) }
            elseif ($deadIf) { $prunedHits.Add(($rel + ':' + $lineNo + ' ' + $route + ' (inside a dead #if region)')) }
            else { $violations.Add(($rel + ':' + $lineNo + " never-ship route literal '" + $route + "' in a COMPILED community source, in live code")) }
        }
    }
}
foreach ($v in $violations) { Add-Failure $v }
foreach ($p in $parseFail) { Add-Failure ("census parser could not model the code (fail-closed): " + $p) }

# -- 7. NON-VACUITY, named, and it is a guard and not a count -----------------
# An empty census that passes is the false-green shape this arc has produced five
# times. Each clause below states WHAT it found when it fires.
if ($devTree) {
    if ($neverShip.Count -eq 0) {
        Add-Failure ("NON-VACUOUS CENSUS FAILED: this is a dev tree (.handoff\ present) yet NOT ONE source file carries the never-ship marker. " +
                     "Scanned $($sources.Count) .razor/.cs files under $RepoRoot. Either the marker was renamed - in which case this census AND " +
                     "the byte scan are both blind - or the gated pages are gone.")
    }
    if ($neverShipRoutes.Count -eq 0 -and $templated.Count -eq 0) {
        Add-Failure ("NON-VACUOUS CENSUS FAILED: $($neverShip.Count) never-ship source(s) were found but the @page reader returned ZERO routes from them. " +
                     "The route parser has stopped matching; it saw $pageDirectives directive(s).")
    }
    if ($guardsSeen -eq 0) {
        Add-Failure ("NON-VACUOUS CENSUS FAILED: not one @if (BuildModules.*) block was found across $scannedFiles compiled source(s). " +
                     "The compile-time pruning model is the only reason a live-looking literal is ever excused here; if its reader stops " +
                     "matching, every excused hit becomes an unexamined hit.")
    }
    if ($bmValues.Count -eq 0) {
        Add-Failure "NON-VACUOUS CENSUS FAILED: Data\BuildModules.cs yielded no const values under this profile - the reader is broken."
    }
    else {
        $falseConsts = @($bmValues.Keys | Where-Object { -not $bmValues[$_] })
        if ($falseConsts.Count -eq 0) {
            Add-Failure ("NON-VACUOUS CENSUS FAILED: every BuildModules const reads TRUE under a community surface. " +
                         "Either the #if reader is inverted or this is not a community evaluation after all.")
        }
    }
}
else {
    # Curated public tree: there is nothing gated here by construction. That is a
    # positive measurement, not a skip - and a never-ship source found HERE is the
    # worst case there is.
    if ($neverShip.Count -gt 0) {
        Add-Failure ("PUBLIC TREE CARRIES NEVER-SHIP SOURCES: .handoff\ is absent (curated public tree) yet $($neverShip.Count) source file(s) " +
                     "carry the never-ship marker: " + ($neverShip -join ', '))
    }
    $notes.Add("public tree (.handoff\ absent): 0 never-ship sources present, so the profile boundary has nothing to leak here.")
}

# -- 8. verdict: print what was scanned, ALWAYS, pass or fail ----------------
$producer = [string]$props['Producer']
$proj     = [string]$props['ProjectFullPath']
Write-Host ""
Write-Host "census-community-surface: surface $SurfaceFile"
Write-Host "  producer=$producer project=$proj profile=$profileName"
Write-Host "  items: $($contentRazor.Count) .razor Content, $($razorComponent.Count) RazorComponent, $($compileCs.Count) Compile ($($compiledSet.Count) distinct); scanned $scannedFiles compiled source file(s) on disk"
Write-Host "  never-ship sources: $($neverShip.Count); never-ship routes: $($neverShipRoutes.Count); templated (not literal-scannable): $($templated.Count)"
Write-Host "  compile-time guards modelled: $guardsSeen @if (BuildModules.*) block(s); pruned occurrences: $($prunedHits.Count)"
foreach ($n in $notes) { Write-Host "  note: $n" }
if ($templated.Count -gt 0) {
    Write-Host "  CENSUS-IMPRECISION(CommunitySurfaceCensusTests): templated never-ship routes, reported as a SET and not literal-scannable:" -ForegroundColor Yellow
    foreach ($t in ($templated | Sort-Object)) { Write-Host "    $t" -ForegroundColor Yellow }
}
if ($prunedHits.Count -gt 0) {
    Write-Host "  CENSUS-IMPRECISION(CommunitySurfaceCensusTests): never-ship route literals present in compiled sources but removed by the compiler (a SET, not a count):" -ForegroundColor DarkGray
    foreach ($p in ($prunedHits | Sort-Object)) { Write-Host "    $p" -ForegroundColor DarkGray }
}

if ($JsonOut) {
    $report = [ordered]@{
        surfaceFile = $SurfaceFile; producer = $producer; project = $proj; profile = $profileName
        symbols = $symbols; contentRazor = $contentRazor.Count; razorComponent = $razorComponent.Count; compile = $compileCs.Count
        scannedFiles = $scannedFiles; devTree = $devTree; guardsSeen = $guardsSeen
        neverShipSources = @($neverShip); neverShipRoutes = @($neverShipRoutes | Sort-Object)
        templatedRoutes = @($templated | Sort-Object); prunedHits = @($prunedHits)
        failures = @($failures)
    }
    $outDir = Split-Path -Parent $JsonOut
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    ($report | ConvertTo-Json -Depth 5) | Set-Content -Path $JsonOut -Encoding ASCII
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "=== COMMUNITY SURFACE CENSUS FAILED ($($failures.Count) violation(s)) ===" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
Write-Host "census-community-surface: OK - no never-ship source compiled in, no never-ship route literal in live community code" -ForegroundColor Green
exit 0
