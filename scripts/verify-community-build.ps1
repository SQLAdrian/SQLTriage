# verify-community-build.ps1
# ---------------------------------------------------------------------------
# FAILS-WHEN-VIOLATED gate for the community build profile (2026-06-12).
# Invoked automatically by buildprofile.targets after every community publish;
# also runnable by hand (see .handoff/SELFTEST_GATING.md).
#
# THE INVARIANT (added 2026-09-11, lane community-verifier-scans-the-wrong-file):
#
#   A guard must scan the artefact that SHIPS, prove that artefact is the one
#   this publish produced, and FAIL LOUDLY when it cannot be scanned at all.
#   ABSENT, EMPTY and UNPARSEABLE are failures - never skips.
#
# For eight weeks this script pinned its scan target BY NAME
# (obj\<cfg>\<tfm>\<rid>\SQLTriage.dll plus "a SQLTriage.dll in the publish
# dir"), re-read each path per check, and treated a missing published copy as a
# silent degrade to the intermediate. Consequences, all measured 2026-09-11:
# a concurrent full-profile build reddened a clean community publish with 61
# route hits and ZERO canary hits (two reads, two files, one verdict); the
# -ExpectFull positive control was satisfiable by the WRONG file; and a genuine
# 174 MB FULL-profile Release single-file build passed as community, exit 0,
# because in the ship configuration there is no loose DLL to scan at all.
#
# WHAT REPLACED IT: scan targets now come from the SHIP MANIFEST that
# buildprofile.targets writes from MSBuild's own item lists - @(FilesToBundle),
# the exact files handed to the bundler, and @(ResolvedFileToPublish), the exact
# files copied into the publish directory - each hashed by the GetFileHash task
# in the same build. Every file this build produced must then be FOUND, BY
# SHA-256, inside the artefact that ships. A compressed single-file bundle is
# inflated and scanned; a bundle that cannot be parsed is a failure, not a pass.
#
# THE TESTS THAT HOLD THIS, by their real names, verified to exist
# (Tests\SQLTriage.Tests\ShipArtifactTests.cs; a citation naming a test that
# does not exist is caught by
# CommunitySurfaceCensusTests.Every_test_name_cited_in_the_census_sources_exists):
#   The_ship_manifest_is_emitted_by_the_community_publish_target
#   The_guard_refuses_a_publish_dir_with_no_ship_manifest
#   The_guard_refuses_a_manifest_whose_token_does_not_match
#   The_guard_reddens_when_a_produced_file_is_not_inside_the_shipped_artefact
#   The_guard_reddens_on_an_unrecognised_bundle_major
#   The_guard_reddens_on_an_entry_that_claims_bytes_beyond_the_file
#   The_expect_full_control_names_one_artefact_and_cannot_be_satisfied_by_another
#   The_instrument_self_control_must_be_able_to_display_a_positive
#   A_community_verdict_is_refused_when_nothing_binds_the_artefact_to_a_build
#   The_verdict_names_only_the_checks_that_ran
#   The_guard_reddens_on_an_undeclared_executable_in_the_publish_directory
#   An_undeclared_file_in_the_publish_directory_is_still_scanned
#   The_publish_work_root_default_can_load_the_artefact_resolver
#
# Checks (any violation => exit 1 => the publish FAILS):
#   1. The gated-page canary literal (carried by every premium/dev-tools page;
#      see the GatedCanary const in those pages) must NOT appear anywhere in the
#      shipped artefact. (.NET string metadata is UTF-16LE; we scan both
#      encodings.)
#   1b. No gated route literal (.handoff/gated-routes.txt) may survive into the
#      files this build produced.
#   2. No gated/IP config may exist in the publish output (consolidation model,
#      queries/ruleset/governance-weights/control_mappings/roadmap-mapping).
#   3. buildprofile.json must not ship.
#   4. Config\free-bundle.dat MUST be present (the community catalog).
#
# -ExpectFull inverts check 1 (canary MUST be present) - used by the self-test
# to prove the canary actually works on a full build, not just "grep found
# nothing because the token was renamed". It names ONE artefact: a positive
# control satisfiable by "one of these files" is not a control.
# ---------------------------------------------------------------------------
param(
    [string]$DllPath,
    [string]$PublishDir,
    [switch]$ExpectFull,
    # The ship manifest for the publish under test. Defaults to the sibling file
    # buildprofile.targets writes beside the publish directory, so a caller that
    # knows the publish dir needs no new argument.
    [string]$ShipManifest,
    # The per-invocation token the publish minted. When supplied, a manifest
    # carrying any other token is REFUSED - it describes a different build.
    [string]$ExpectShipToken
)

$ErrorActionPreference = "Stop"
# Token assembled from parts so this script (which ships in the public repo) never
# contains the contiguous literal - otherwise publish-public.ps1's canary grep
# would trip on its own tooling. The contiguous form lives ONLY in the gated pages.
$canary = "SQLT-GATED-DNA-" + "NEVERSHIP"
$failures = @()

# The bundle reader, the ship-manifest reader and the multi-pattern scanner.
# ABSENT IS A FAILURE: this file ships alongside us (.publicallow: scripts\*.ps1)
# and without it the guard could only pretend to scan.
$libPath = Join-Path $PSScriptRoot "shipartifact.ps1"
if (-not (Test-Path -LiteralPath $libPath)) {
    Write-Host "VERIFY FAIL: scripts\shipartifact.ps1 is ABSENT next to this script - the artefact resolver cannot run, so no verdict can be rendered." -ForegroundColor Red
    exit 1
}
try {
    . $libPath
} catch {
    # A guard that cannot load its own instrument must say so in words its reader
    # can act on. MEASURED 2026-09-11: an anti-malware AMSI hook can refuse to PARSE
    # this resolver at all from some temporary directories ("This script contains
    # malicious content and has been blocked by your antivirus software") while the
    # same bytes load fine from the repo - the C# it compiles at runtime looks like a
    # loader to a fast scanner. The direction is safe (nothing is certified), but
    # without this message the failure reads as a syntax error in our own code.
    Write-Host "VERIFY FAIL: could not load $libPath - the artefact resolver is unavailable, so NO verdict can be rendered (fail-closed)." -ForegroundColor Red
    Write-Host ("  underlying error: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host "  If that mentions malicious content, an anti-malware AMSI hook blocked the script; exclude the repo's scripts folder or run the publish from an excluded path." -ForegroundColor Red
    # The publish driver does not wait until here to discover that: its work root
    # defaults to a folder beside the repo rather than a shared temporary directory,
    # and it loads this resolver from under that root before any build starts. Held by
    # ShipArtifactTests.The_publish_work_root_default_can_load_the_artefact_resolver.
    exit 1
}

function Resolve-DefaultManifestPath([string]$Dir) {
    if ([string]::IsNullOrWhiteSpace($Dir)) { return $null }
    $trimmed = $Dir.TrimEnd('\', '/')
    return "$trimmed.sqltriage-ship-manifest.txt"
}

# --- needles -----------------------------------------------------------------
# The canary is one needle, matched anywhere. Route literals are boundary-aware:
# a hit counts only when the character AFTER it is not [-A-Za-z0-9_], so a longer
# token that merely CONTAINS a needle - a config filename built from the same word
# as the route - does not trip the route guard.
#
# The worked example that used to sit here spelled out a real gated route. This
# script SHIPS TO THE PUBLIC REPO, which is why the needle list lives in .handoff/
# and why the canary is assembled from parts; an illustrative literal is a
# fingerprint too. (Pin P7 of the 2026-09-11 lane; it was already violated here
# before this lane, twice.)
$routes = @()
$routeListState = 'not-consulted'
$gatedRoutesFile = $null
if (-not $ExpectFull) {
    # Public-safety: .handoff/ is stripped from the PUBLIC mirror and the gated
    # pages/routes never existed there, so in a public tree there is nothing to
    # scan and the route guard must SKIP - otherwise a community build invoked
    # from the public source (this script and buildprofile.targets both ship)
    # would break on the missing list. Fail-closed ONLY when .handoff/ IS present
    # (the dev repo) but the list has been deleted, renamed or emptied.
    $handoffDir      = Join-Path $PSScriptRoot "..\.handoff"
    $gatedRoutesFile = Join-Path $handoffDir "gated-routes.txt"
    if (-not (Test-Path $handoffDir)) {
        $routeListState = 'skipped-public-tree'
        Write-Host "verify-community-build: gated-route scan skipped (.handoff/ absent - public tree)" -ForegroundColor DarkGray
    }
    elseif (-not (Test-Path $gatedRoutesFile)) {
        $routeListState = 'missing'
        $failures += "gated-route list missing at $gatedRoutesFile - D-0a fingerprint guard cannot run (fail-closed; .handoff/ is present so the list must exist)."
    }
    else {
        $allLines = @(Get-Content $gatedRoutesFile)
        $routes = @($allLines |
                  ForEach-Object { $_.Trim() } |
                  Where-Object { $_ -ne '' -and -not $_.StartsWith('#') })
        # EMPTY IS A FAILURE, NEVER A SKIP (defect 4 of the 2026-09-11 lane
        # community-verifier-scans-the-wrong-file). A present-but-empty list used to
        # pass vacuously: the foreach body never ran, nothing was printed, and the
        # D-0a fingerprint guard silently became a no-op.
        #
        # NON-VACUITY IS A NAMED GUARD, NOT A COUNT - a count measures the pattern,
        # not the code. What holds this list COMPLETE against the code is
        # Tests\SQLTriage.Tests\CommunitySurfaceCensusTests.cs, test
        # Gated_routes_list_covers_every_never_ship_route_the_census_derives, which
        # derives the never-ship routes from the source tree and fails when one is
        # missing here. That test found three missing entries the day it was written.
        if ($routes.Count -eq 0) {
            $routeListState = 'empty'
            $comments = @($allLines | Where-Object { $_.Trim().StartsWith('#') }).Count
            $blanks   = @($allLines | Where-Object { $_.Trim() -eq '' }).Count
            $failures += "gated-route list at $gatedRoutesFile yields ZERO effective routes ($($allLines.Count) line(s) read: $comments comment, $blanks blank, 0 usable). The D-0a fingerprint guard would scan nothing and pass vacuously - fail-closed instead."
        } else {
            $routeListState = 'loaded'
        }
    }
}

$canarySpec = @(@{ Text = $canary; Standalone = $false })
$fullSpec   = @($canarySpec) + @($routes | ForEach-Object { @{ Text = $_; Standalone = $true } })
$canarySet  = New-SqltNeedleSet $canarySpec
$fullSet    = New-SqltNeedleSet $fullSpec

# --- instrument self-control --------------------------------------------------
# A control that cannot reproduce cannot refute. Every zero below is only
# meaningful once the scanner has been shown able to display a positive IN THIS
# RUN, with THESE needles - including the route needles, which are read from a
# file that may have been edited since the scanner was last exercised.
$instrumentFault = Test-SqltScannerInstrument $fullSet 'canary + route needles'
if ($instrumentFault) { $failures += $instrumentFault }

# --- artefact resolution ------------------------------------------------------
# Returns the units to scan. Each unit is a buffer plus a PROVENANCE sentence
# saying why that buffer is the authoritative one. There is exactly one place
# this decision is made - a second caller re-inventing it is the class of defect
# this lane was opened for.
$scanUnits = New-Object 'System.Collections.Generic.List[object]'
$provenanceMode = 'UNBOUND'
$provenanceNotes = New-Object 'System.Collections.Generic.List[string]'
$bindingChecked = 0
$bindingMatched = 0
$resolverFault = $null
# Every publish-directory-relative path that became a scan unit. The converse
# invariant below reads it: a file in the publish directory that is in here has
# been scanned, and one that is not has not.
$publishSeen = @{}
# Manifest rows and on-disk enumeration DISAGREE ABOUT SEPARATORS: MSBuild hands back
# a mix - measured on one real Release manifest, 'config.default/appsettings.Production.json'
# alongside 'docs.default\compliance\...'. Comparing them raw counted 11 declared files as
# undeclared on a clean publish, and the first run of
# ShipArtifactTests.An_undeclared_file_in_the_publish_directory_is_still_scanned went red on
# exactly that. One spelling, both sides.
function ConvertTo-SqltPublishKey([string]$Rel) { return $Rel.Replace([char]47, [char]92).TrimStart([char]92).ToLowerInvariant() }
$undeclaredCount = 0
$undeclaredBytes = 0
$treeChecksRan = $false

function Add-ScanUnit([string]$Name, [byte[]]$Bytes, [string]$Provenance, [bool]$Owned) {
    $scanUnits.Add([pscustomobject]@{ Name = $Name; Bytes = $Bytes; Length = $Bytes.Length; Provenance = $Provenance; Owned = $Owned })
}

try {
    $manifest = $null
    $manifestPath = $ShipManifest
    if ([string]::IsNullOrWhiteSpace($manifestPath)) { $manifestPath = Resolve-DefaultManifestPath $PublishDir }

    # BRANCH ORDER IS PART OF THE CONTRACT. -ExpectFull is the positive control:
    # it resolves to exactly ONE named artefact and must never fall through to the
    # community resolver, which reasons over a SET. The old script had a single
    # "does any of these contain the token" expression serving both, fail-SAFE in
    # one direction and fail-UNSAFE in the other.
    if ($ExpectFull) {
        # ---- the positive control ------------------------------------------
        # ONE artefact, named. The old control asked "does ANY of these targets
        # contain the canary", then stopped at the first hit - so a canary-free
        # published assembly beside a canary-bearing obj\ intermediate satisfied
        # it. A control satisfiable by the wrong file validates nothing.
        $target = $null
        if ($PublishDir) {
            if (Test-Path -LiteralPath $manifestPath) {
                $m2 = Read-SqltShipManifest $manifestPath $ExpectShipToken
                $provenanceMode = 'BOUND'
                $owned = @(Get-SqltOwnedRecords $m2.Published) + @(Get-SqltOwnedRecords $m2.Bundled)
                $asm = @($owned | Where-Object { $_.RelativePath -like '*.dll' })
                if ($asm.Count -ne 1) { throw "-ExpectFull: the manifest beside $PublishDir names $($asm.Count) produced assemblies; a positive control must name exactly ONE artefact." }
                $target = Join-Path $PublishDir $asm[0].RelativePath
            } else {
                $target = Join-Path $PublishDir "SQLTriage.dll"
            }
            if (-not (Test-Path -LiteralPath $target)) { throw "-ExpectFull: the named artefact $target is ABSENT. The control cannot fall back to another file - that is what made it a false green." }
        }
        elseif ($DllPath) {
            if (-not (Test-Path -LiteralPath $DllPath)) { throw "-ExpectFull: the named artefact $DllPath is ABSENT." }
            $target = $DllPath
        }
        else { throw "-ExpectFull: no artefact named (pass -DllPath or -PublishDir). A control with no target is not a control." }
        $bytes = [System.IO.File]::ReadAllBytes($target)
        $provenanceNotes.Add("named artefact $target ($($bytes.Length) bytes, SHA-256 $((Get-SqltSha256 $bytes $bytes.Length).Substring(0,16))...)")
        Add-ScanUnit $target $bytes "named explicitly as the ONE artefact this control must find the canary in" $true
    }
    elseif ($PublishDir) {
        # A community publish MUST declare what it produced. "There was no
        # manifest so we scanned whatever DLL was lying around" is the behaviour
        # that let a full-profile Release build pass as community.
        $manifest = Read-SqltShipManifest $manifestPath $ExpectShipToken
        $provenanceMode = 'BOUND'
    }
    elseif ($ShipManifest) {
        # A manifest says what a build PRODUCED; a community verdict is about what
        # SHIPS. With no publish directory there is no artefact to scan and checks
        # 2-4 cannot run, so anything green here would assert what it never measured.
        # Before 2026-09-11 this path did fail closed, but only by accident, several
        # hundred lines later, as "Cannot bind argument to parameter 'Path' because
        # it is an empty string" - a reader learns nothing from that.
        throw "-ShipManifest without -PublishDir is REFUSED in community mode: the manifest names what this build produced, but there is no publish directory to find those files in, and publish-tree checks 2-4 cannot run. Pass -PublishDir."
    }

    if ($manifest) {
        $declaredProfile = [string]$manifest.Props['SQLTriageProfile']
        if (-not $ExpectFull -and $declaredProfile -ne 'community') {
            throw "ship manifest at $($manifest.Path) declares SQLTriageProfile='$declaredProfile', not 'community'. This guard renders community verdicts; it will not certify a publish it cannot see was community."
        }
        $provenanceNotes.Add("manifest $($manifest.Path) (token $($manifest.Props['ShipToken']), profile $declaredProfile, configuration $($manifest.Props['Configuration']))")

        $bundledOwned   = Get-SqltOwnedRecords $manifest.Bundled
        $publishedOwned = Get-SqltOwnedRecords $manifest.Published
        if (($bundledOwned.Count + $publishedOwned.Count) -eq 0) {
            throw "ship manifest at $($manifest.Path) declares NO file that this build produced (every entry carries a NuGetPackageId). A manifest that binds nothing cannot certify anything."
        }

        $singleFile = ($manifest.Bundled.Count -gt 0)
        $declaredSingleFile = ([string]$manifest.Props['PublishSingleFile'] -eq 'true')
        if ($declaredSingleFile -ne $singleFile) {
            throw "ship manifest at $($manifest.Path) says PublishSingleFile='$($manifest.Props['PublishSingleFile'])' but lists $($manifest.Bundled.Count) bundled file(s). The manifest contradicts itself - refusing to guess which shape shipped."
        }

        if ($singleFile) {
            # ---- ARM A: the shipped bytes -------------------------------------
            # The Release artefact is ONE compressed file. A raw byte scan of it
            # returns the same answer for a clean build and a gated one (measured
            # on three real artefacts 2026-09-11: raw hits 0/0/0 while extraction
            # gave 1/0/1), so the payload is inflated and scanned entry by entry.
            #
            # NOTHING here is selected by name. Every entry is scanned, and the
            # entries that matter are identified by the SHA-256 the build
            # recorded. That is deliberate: this bundle carries TWO images of this
            # application - SQLTriage.dll and SQLTriage.r2r.dll, 11.5 MB and
            # 228 MB in the publish this was written against - and picking one of
            # them by filename would be the very defect this lane exists to fix.
            $exeName = [string]$manifest.Props['PublishedSingleFileName']
            if ([string]::IsNullOrWhiteSpace($exeName)) { throw "ship manifest lists bundled files but names no single-file artefact (PublishedSingleFileName empty)." }
            $exePath = Join-Path $PublishDir $exeName
            $bundle = Read-SqltBundle $exePath
            if ($bundle.Entries.Count -eq 0) { throw "bundle in $exePath declares ZERO entries - an empty bundle cannot be the artefact that ships." }
            $provenanceNotes.Add("bundle in $exePath (major $($bundle.Major).$($bundle.Minor), id $($bundle.BundleId), $($bundle.Entries.Count) entries, $($bundle.SignatureMatches) signature match(es))")

            # @(FilesToBundle) is the CANDIDATE list, not the ingested list: the
            # bundler hands some of them back (@(_FilesExcludedFromBundle)) to be
            # published loose beside the exe, and those reappear in
            # @(ResolvedFileToPublish). Measured 2026-09-11 on a real Release
            # publish: 39 produced files, 5 inside the bundle, 34 loose. So the
            # rule is not "every candidate is in the bundle" - which reddened a
            # clean publish - but "every produced file is SOMEWHERE in what
            # ships, and was scanned there": inside the bundle by the SHA-256 of
            # an inflated entry, or beside it in the publish directory by the
            # SHA-256 of the file on disk. Found in NEITHER is a failure.
            $ownedRecords = @()
            $ownedSeen = @{}
            foreach ($r in (@($bundledOwned) + @($publishedOwned))) {
                $k = $r.RelativePath.ToLowerInvariant() + '|' + $r.Sha256
                if (-not $ownedSeen.ContainsKey($k)) { $ownedSeen[$k] = $true; $ownedRecords += $r }
            }
            $wanted = @{}
            foreach ($r in $ownedRecords) {
                # The single-file artefact IS the bundle. Its pre-bundle hash (the
                # bare apphost) can never equal the exe's, and demanding it would
                # be asking the artefact to contain itself. It is bound instead by
                # having been PARSED here and by carrying everything else.
                if ($r.RelativePath -ieq $exeName) { continue }
                if (-not $wanted.ContainsKey($r.Sha256)) { $wanted[$r.Sha256] = $r }
            }
            $bindingChecked = $wanted.Count
            if ($bindingChecked -eq 0) { throw "ship manifest declares no produced file other than $exeName itself - nothing binds the payload to this build." }

            # The host image: the exe as it sits, including the two entries the
            # bundler stores uncompressed and everything outside the payload.
            # NOTE: this unit holds the whole exe until the scan runs, so do NOT
            # "free" $bundle.Bytes here - the unit is the only reference that
            # matters and dropping the other one frees nothing. Entries are
            # inflated from DISK (Expand-SqltBundleEntry), so the whole-file
            # buffer is not needed again.
            Add-ScanUnit ("host image of " + $exeName) $bundle.Bytes "the exe as it sits in the publish directory, parsed as a bundle by this run" $false
            $publishSeen[(ConvertTo-SqltPublishKey $exeName)] = $true

            $seen = @{}
            $managed = 0
            foreach ($e in $bundle.Entries) {
                $bytes = Expand-SqltBundleEntry $bundle $e
                $sha = Get-SqltSha256 $bytes $bytes.Length
                $owned = $wanted.ContainsKey($sha)
                if ($owned) { $seen[$sha] = $true }
                if (Test-SqltManagedAssembly $bytes $bytes.Length) { $managed++ }
                $prov = if ($owned) {
                    "bundle entry $($e.Index); SHA-256 matches a file this build produced ($($wanted[$sha].FullPath))"
                } else {
                    "bundle entry $($e.Index) of the shipped exe"
                }
                Add-ScanUnit ("bundle:" + $e.RelativePath) $bytes $prov $owned
            }
            $provenanceNotes.Add("$managed managed image(s) among the $($bundle.Entries.Count) bundle entries")

            # Whatever the bundler did not take must be on disk beside the exe,
            # with the bytes this build produced - and is scanned there.
            foreach ($sha in @($wanted.Keys)) {
                if ($seen.ContainsKey($sha)) { continue }
                $r = $wanted[$sha]
                $p = Join-Path $PublishDir $r.RelativePath
                if (-not (Test-Path -LiteralPath $p)) {
                    throw "BINDING FAILED: produced file '$($r.RelativePath)' [$($sha.Substring(0,16))] is in NEITHER $exePath (no bundle entry inflates to its SHA-256) NOR the publish directory. REFUSING to certify an artefact that cannot be tied to this build."
                }
                $actual = Get-SqltFileSha256 $p
                if ($actual -ne $sha) {
                    throw "BINDING FAILED: '$($r.RelativePath)' beside $exeName hashes $actual, but this build produced $sha."
                }
                $seen[$sha] = $true
                Add-ScanUnit ("publish:" + $r.RelativePath) ([System.IO.File]::ReadAllBytes($p)) "shipped loose beside the single-file artefact, SHA-256 verified against what this build produced" $true
                $publishSeen[(ConvertTo-SqltPublishKey $r.RelativePath)] = $true
            }
            $bindingMatched = @($wanted.Keys | Where-Object { $seen.ContainsKey($_) }).Count
        }
        else {
            # ---- the loose shape (Debug publishes, and the curated-tree rebuild)
            # Scan the PUBLISHED copies, not the intermediate: the intermediate
            # lives in obj\<cfg>\<tfm>\<rid>\, which is shared mutable state
            # between the community and full profiles and is what a concurrent
            # full build rewrote underneath this guard.
            $ownedSet = @{}
            foreach ($r in $publishedOwned) { $ownedSet[$r.RelativePath.ToLowerInvariant()] = $true }
            foreach ($r in $manifest.Published) {
                $p = Join-Path $PublishDir $r.RelativePath
                $isOwned = $ownedSet.ContainsKey($r.RelativePath.ToLowerInvariant())
                if ($isOwned) {
                    $bindingChecked++
                    if (-not (Test-Path -LiteralPath $p)) { throw "BINDING FAILED: produced file '$($r.RelativePath)' is declared shipped but ABSENT from $PublishDir." }
                    $actual = Get-SqltFileSha256 $p
                    if ($actual -ne $r.Sha256) { throw "BINDING FAILED: '$($r.RelativePath)' in $PublishDir hashes $actual, but this build produced $($r.Sha256)." }
                    $bindingMatched++
                }
                if (-not (Test-Path -LiteralPath $p)) { continue }
                $prov = if ($isOwned) { "copied into the publish directory by this build, SHA-256 verified against what the build produced" } else { "third-party file copied into the publish directory by this build" }
                Add-ScanUnit ("publish:" + $r.RelativePath) ([System.IO.File]::ReadAllBytes($p)) $prov $isOwned
                $publishSeen[(ConvertTo-SqltPublishKey $r.RelativePath)] = $true
            }
        }

        # ---- THE CONVERSE INVARIANT: everything that SHIPS was scanned --------
        # The binding above runs one way: every file this build PRODUCED must be
        # inside what ships. It says nothing about a file that is in the publish
        # directory and in NO manifest row. MEASURED 2026-09-11 by the cold gate:
        # a canary-bearing SQLTriage.dll dropped beside a clean single-file exe
        # was never a scan unit in either branch, and the guard reported
        # "scanned 524 unit(s) ... binding 48/48 ... OK - community", exit 0.
        # Only the eight filename globs of checks 2-4 would have caught it, and
        # SQLTriage.dll is not one of them.
        #
        # MEASURED BEFORE IMPLEMENTING, on two real community publishes of this
        # tree (Debug loose: 697 files, 153 declared; Release single-file: 588
        # files, 43 declared): the undeclared remainder is 544/545 static web
        # assets - .css .js .br .gz .png .woff2 .jpg .html .ico .json .md - and
        # the ONLY undeclared code-shaped file in either shape is the single-file
        # exe itself, which the manifest names in PublishedSingleFileName and
        # which was parsed as the bundle above. So both halves below are safe on a
        # clean publish, and neither is a count pinned from a regex:
        #   (a) every undeclared file is SCANNED for the canary, so "the token is
        #       nowhere in what ships" is measured over the directory, not over
        #       the subset the manifest happens to list;
        #   (b) an undeclared file that is CODE-SHAPED is a FAILURE - unprovenanced
        #       executable bytes in a directory we are about to certify.
        # What this still does NOT do: route-literal scanning over undeclared
        # files. Route needles are boundary-aware matches tuned for produced
        # assemblies, and over 13 MB of static CSS their behaviour is UNMEASURED,
        # so claiming that coverage would be the same unearned assertion this lane
        # exists to remove. Held by
        # ShipArtifactTests.The_guard_reddens_on_an_undeclared_executable_in_the_publish_directory
        # and ShipArtifactTests.An_undeclared_file_in_the_publish_directory_is_still_scanned.
        if (-not $ExpectFull -and $PublishDir -and (Test-Path -LiteralPath $PublishDir)) {
            $codeShaped = @('.dll', '.exe', '.ps1', '.psm1', '.so', '.dylib', '.node', '.com', '.scr', '.bat', '.cmd', '.vbs', '.msi')
            $pubRoot = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\', '/')
            foreach ($f in (Get-ChildItem -LiteralPath $pubRoot -Recurse -File -Force)) {
                $rel = $f.FullName.Substring($pubRoot.Length + 1)
                if ($publishSeen.ContainsKey((ConvertTo-SqltPublishKey $rel))) { continue }
                $undeclaredCount++
                $undeclaredBytes += $f.Length
                if ($codeShaped -contains $f.Extension.ToLowerInvariant()) {
                    $failures += "UNDECLARED EXECUTABLE IN THE PUBLISH DIRECTORY: '$rel' ($($f.Length) bytes) is about to ship, but the ship manifest declares no such file and it is not the single-file artefact this build named. Unprovenanced code in a directory we are asked to certify is REFUSED - clean the publish directory and republish, or find out what put it there."
                }
                Add-ScanUnit ("publish-undeclared:" + $rel) ([System.IO.File]::ReadAllBytes($f.FullName)) "present in the publish directory but NOT declared by the ship manifest - scanned anyway, because everything that ships is scanned" $false
            }
            if ($undeclaredCount -gt 0) {
                $provenanceNotes.Add("$undeclaredCount file(s) in the publish directory ($undeclaredBytes bytes) are not declared by the ship manifest - scanned as undeclared")
            }
        }
    }
    elseif ($DllPath -and -not $ExpectFull) {
        # ---- REFUSED: there is no such thing as an unbound community verdict --
        # This branch used to scan the named file, print "UNBOUND", mark it as a
        # file this build produced, and then render the ordinary green verdict.
        # MEASURED 2026-09-11 by the cold gate: -DllPath over full-r2r.dll - the
        # 240 MB ReadyToRun image extracted from a genuine 174 MB FULL-profile
        # single-file exe - printed
        #     "verify-community-build: OK - community (no canary, no gated assets,
        #      free bundle present)", exit 0.
        # Every clause after the dash was false: no publish directory existed, so
        # checks 2-4 never ran, and nothing tied the file to any build. A verdict
        # whose words outrun its evidence is the defect this lane exists to remove,
        # so community mode now refuses the parameter outright. -ExpectFull still
        # takes -DllPath: that is a POSITIVE control over one named artefact, it
        # asserts the canary is PRESENT, and it can never certify anything clean.
        # Held by
        # ShipArtifactTests.A_community_verdict_is_refused_when_nothing_binds_the_artefact_to_a_build.
        throw "-DllPath is REFUSED in community mode. A community verdict says an artefact is clean; that is a claim about what SHIPPED, and a path handed in on the command line is bound to no build. Pass -PublishDir (the publish this run produced, bound by its ship manifest). To scan one named file, use -ExpectFull, which only ever asserts the canary is PRESENT."
    }
    elseif (-not $ExpectFull) {
        throw "nothing to scan: pass -PublishDir (the normal path, bound by the ship manifest). -DllPath is a positive control only, and requires -ExpectFull."
    }
} catch {
    $resolverFault = $_.Exception.Message
}

if ($resolverFault) { $failures += "ARTEFACT RESOLUTION FAILED: $resolverFault" }

# NON-VACUITY: a guard that scanned nothing must never report a pass. Each of
# these is a named condition with its own message, not a threshold on a count.
if (-not $resolverFault) {
    if ($scanUnits.Count -eq 0) { $failures += "NOTHING WAS SCANNED: the resolver produced zero units. An empty scan cannot pass." }
    else {
        $totalBytes = ($scanUnits | Measure-Object -Property Length -Sum).Sum
        if ($totalBytes -le 0) { $failures += "NOTHING WAS SCANNED: $($scanUnits.Count) unit(s) totalling zero bytes." }
        if (-not $ExpectFull -and @($scanUnits | Where-Object { $_.Owned }).Count -eq 0) {
            $failures += "NOTHING THIS BUILD PRODUCED WAS SCANNED: $($scanUnits.Count) unit(s), none of them a file this build produced. A verdict over third-party bytes alone says nothing about the profile boundary."
        }
    }
}

# --- checks 1 and 1b: one buffer, every needle, one pass ----------------------
# Defect 1 dies as a CLASS here, not as an instance: each buffer is read once and
# every check reasons over that same buffer, so no two checks can disagree about
# what they scanned.
$canaryHits = 0
$canaryWhere = @()
foreach ($u in $scanUnits) {
    $set = if ($u.Owned) { $fullSet } else { $canarySet }
    $hits = Measure-SqltNeedles -Data $u.Bytes -Length $u.Length -NeedleSet $set
    if ($hits[$canary] -gt 0) { $canaryHits += $hits[$canary]; $canaryWhere += $u.Name }
    if ($u.Owned) {
        foreach ($r in $routes) {
            if ($hits[$r] -gt 0) {
                $failures += "gated route literal found in COMMUNITY artefact $($u.Name) ($($hits[$r]) standalone hit(s)) - a #if-fenced gated route leaked back in. RouteConstants / a call-site lost its !SQLT_NO_<MODULE> fence, or a new gated page/link was added without one. [$($u.Provenance)]"
            }
        }
    }
}

if ($ExpectFull) {
    if ($canaryHits -eq 0 -and -not $resolverFault) {
        $failures += "canary '$canary' NOT found in the named full-profile artefact $($scanUnits[0].Name) - the canary mechanism itself is broken (token renamed/removed?), or that artefact is not a full build."
    }
} else {
    if ($canaryHits -gt 0) {
        $failures += "canary '$canary' found in the COMMUNITY artefact ($canaryHits hit(s) across $($canaryWhere.Count) unit(s): $(($canaryWhere | Select-Object -First 5) -join ', ')) - a gated (premium/dev-tools) page was compiled in. buildprofile.targets exclusion is broken or a page was added without an exclusion entry."
    }
}

# -- 2..4. publish-tree checks (community only, when a publish dir is given) --
if (-not $ExpectFull -and $PublishDir -and (Test-Path $PublishDir)) {
    $treeChecksRan = $true
    $forbidden = @(
        "consolidation-model*.json",
        "queries.json", "governance-weights.json", "control_mappings.json",
        "roadmap-mapping.json", "roadmap-aliases.json",
        "buildprofile.json", "buildprofile.targets"
    )
    foreach ($pat in $forbidden) {
        $hits = Get-ChildItem -Path $PublishDir -Recurse -File -Filter $pat -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\BPScripts(\.default)?\\' }  # BPScripts\ruleset.json = public MS VA ruleset, allowed; .default from the 2026-09-11 payload rename
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
# SAY WHAT WAS SCANNED, ALWAYS - on success too. A green line that names the
# artefact and its provenance is the difference between "the guard passed" and
# "the guard ran"; for eight weeks this printed neither, and nobody could tell
# from the output that it had been reading an obj\ intermediate all along.
$totalScanned = 0
if ($scanUnits.Count -gt 0) { $totalScanned = ($scanUnits | Measure-Object -Property Length -Sum).Sum }
$ownedCount = @($scanUnits | Where-Object { $_.Owned }).Count
Write-Host ("verify-community-build: PROVENANCE " + $provenanceMode)
foreach ($n in $provenanceNotes) { Write-Host ("  * " + $n) }
Write-Host ("verify-community-build: scanned " + $scanUnits.Count + " unit(s), " + $totalScanned + " byte(s); " + $ownedCount + " produced by this build")
if ($bindingChecked -gt 0) {
    Write-Host ("verify-community-build: binding " + $bindingMatched + "/" + $bindingChecked + " produced file(s) found in the shipped artefact by SHA-256")
}
Write-Host ("verify-community-build: route needles " + $routes.Count + " (" + $routeListState + ")")
foreach ($u in ($scanUnits | Where-Object { $_.Owned } | Select-Object -First 12)) {
    Write-Host ("  - " + $u.Name + " (" + $u.Length + " bytes) <- " + $u.Provenance)
}
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "=== COMMUNITY BUILD VERIFICATION FAILED ($($failures.Count) violation(s)) ===" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

# A VERDICT MUST NEVER ASSERT A CHECK IT DID NOT RUN. The old line said "no
# canary, no gated assets, free bundle present" unconditionally - including on the
# run where $PublishDir was absent and checks 2-4 (the forbidden-file globs, the
# Config\ruleset.json check and the free-bundle check) never executed. Measured
# 2026-09-11 by the cold gate. The clauses below are assembled from what actually
# ran, so the words and the evidence cannot drift apart.
# Held by ShipArtifactTests.The_verdict_names_only_the_checks_that_ran.
$mode = if ($ExpectFull) {
    "full-profile (canary present as expected)"
} elseif ($treeChecksRan) {
    "community (no canary in $($scanUnits.Count) scanned unit(s); no gated assets, free bundle present in $PublishDir)"
} else {
    "community (no canary in $($scanUnits.Count) scanned unit(s)); publish-tree checks 2-4 NOT RUN - no publish directory was given, so this verdict says nothing about gated assets or the free bundle"
}
Write-Host "verify-community-build: OK - $mode" -ForegroundColor Green
exit 0
