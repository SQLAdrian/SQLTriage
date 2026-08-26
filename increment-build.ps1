# increment-build.ps1
#
# Build number is DERIVED from git, not accumulated in this file, so it is monotonic and
# survives `git reset --hard`. The old scheme stored a per-build counter in Config/version.json
# (git-tracked): every ClaudeBuildFolder sync reset that file to the committed value, so build
# numbers jumped BACKWARDS after a sync (observed 2026-07-23: 3190 -> 2986). Deriving from the
# git commit count removes that whole class of bug:
#
#     buildNumber = BuildBase + (commit count reachable from HEAD)
#
#   - monotonic  : each commit raises the count, so the number only grows on new work.
#   - reset-proof: `reset --hard` restores HEAD, so the count is unchanged; the next build
#                  recomputes the SAME number, overwriting whatever value the tracked file was
#                  reverted to. The committed buildNumber is now just a cosmetic seed.
#   - churn-free : version.json never has to be committed to carry the counter forward.
#
# BuildBase keeps the derived number above the pre-migration high-water (3190): at migration
# HEAD had 860 commits, so 2440 + 860 = 3300.
# Caveat: a git history rewrite that DROPS commits would lower the count. That is rare and
# operator-controlled; raise BuildBase if it ever happens.

$versionFile = "Config\version.json"
$BuildBase   = 2440
$vf = (Resolve-Path $versionFile).ProviderPath

# Read the commit count WITHOUT letting native stderr abort the build: PS 5.1 under
# EAP=Stop turns a native command's stderr into a terminating error. Redirect stderr and
# judge on the exit code only.
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$raw   = & git rev-list --count HEAD 2>$null
$gitOk = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEAP

$today   = Get-Date -Format "yyyy-MM-dd"
# Surgical field edits preserve the file's exact formatting AND its UTF-8 content (the whatsnew
# array carries an em dash). Read/write UTF-8 explicitly; write WITHOUT a BOM (System.Text.Json
# rejects a BOM).
$content = [System.IO.File]::ReadAllText($vf, [System.Text.Encoding]::UTF8)

if ($gitOk -and "$raw".Trim() -match '^\d+$') {
    $count       = [int]("$raw".Trim())
    $buildNumber = $BuildBase + $count
    $content = [regex]::Replace($content, '("buildNumber"\s*:\s*)\d+', ('${1}' + $buildNumber))
    Write-Host "Build number: $buildNumber  (base $BuildBase + $count commits)" -ForegroundColor Green
} else {
    # Not a git checkout (source export / detached build): do NOT regress. Keep the existing
    # number, refresh only the date, and say so.
    if ($content -match '"buildNumber"\s*:\s*(\d+)') { $buildNumber = $Matches[1] } else { $buildNumber = "unchanged" }
    Write-Host "increment-build: git unavailable - kept buildNumber $buildNumber (no regression)." -ForegroundColor Yellow
}

$content = [regex]::Replace($content, '("buildDate"\s*:\s*")[^"]*(")', ('${1}' + $today + '${2}'))
[System.IO.File]::WriteAllText($vf, $content, (New-Object System.Text.UTF8Encoding $false))

$ver = if ($content -match '"version"\s*:\s*"([^"]+)"') { $Matches[1] } else { "0.92.5" }
Write-Host "To trigger a release: git tag v$ver && git push origin v$ver" -ForegroundColor Cyan
