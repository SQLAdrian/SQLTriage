# verify-public-tree.ps1
# ---------------------------------------------------------------------------
# FAILS-WHEN-VIOLATED gates over a curated public-publish tree (2026-06-12,
# publish-hardening session). Invoked by publish-public.ps1 before any push and
# by scripts/selftest-gating.ps1 (negative tests). Runnable by hand:
#   powershell -File scripts\verify-public-tree.ps1 -TreePath <curated-tree>
#
# Three gates (any violation => exit 1):
#   1. ALLOW-LIST: every file in the tree must match a glob in
#      .handoff/.publicallow. The .publicignore deny-list runs first (in
#      publish-public.ps1) as defense in depth, but THIS is the gate - new
#      files do not ship until someone consciously allows them.
#   2. SECRET/CANARY GREP: known secret patterns + the two leak canaries
#      (gated-page token, corpus DNA token). Tokens are assembled from parts
#      so this script (which itself ships in the public repo) never contains
#      the contiguous literals.
#   3. COMMERCIAL-LANGUAGE: premium/license/Stripe/unlock/upsell/activation/
#      BIP39/pricing etc. must not appear except where
#      .handoff/.publiclang-allow explicitly allows the (pattern, path) pair
#      with a reason. Keeps engagement/upsell language out of the public repo.
#
# The two allow files live in .handoff/ (never shipped) and are read from the
# DEV repo, not from the tree under test.
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$TreePath,
    [string]$AllowFile,
    [string]$LangAllowFile
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
if (-not $AllowFile)     { $AllowFile     = Join-Path $repo ".handoff\.publicallow" }
if (-not $LangAllowFile) { $LangAllowFile = Join-Path $repo ".handoff\.publiclang-allow" }

if (-not (Test-Path $TreePath))  { Write-Host "VERIFY-TREE FAIL: tree not found: $TreePath" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $AllowFile)) { Write-Host "VERIFY-TREE FAIL: allow-list not found: $AllowFile - refusing to publish without it." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $LangAllowFile)) { Write-Host "VERIFY-TREE FAIL: language allow-list not found: $LangAllowFile" -ForegroundColor Red; exit 1 }

$treeFull = (Resolve-Path $TreePath).Path.TrimEnd('\')
$failures = @()

# Binary/asset extensions skipped by the grep gates (gate 1 covers ALL files).
$binaryExt = @('.png','.jpg','.jpeg','.gif','.ico','.bmp','.svg','.dll','.exe','.pdb',
               '.db','.dat','.zip','.7z','.tar','.gz','.br','.woff','.woff2','.ttf',
               '.eot','.otf','.pfx','.snk','.wasm','.mp4','.aesgcm')

function Get-RelPath([string]$Full) {
    return $Full.Substring($treeFull.Length).TrimStart('\','/') -replace '\\', '/'
}

function ConvertTo-GlobRegex([string]$Glob) {
    # Forward-slash globs; ** = any depth, * = within a segment, ? = one char.
    $escaped = [regex]::Escape($Glob)
    # '**/' matches ZERO or more directories (gitignore semantics), so
    # 'Data/**/*.cs' covers both Data/X.cs and Data/Sub/X.cs.
    $escaped = $escaped -replace '\\\*\\\*/', '<<ANYDIR>>'
    $escaped = $escaped -replace '\\\*\\\*', '<<ANY>>'
    $escaped = $escaped -replace '\\\*', '[^/]*'
    $escaped = $escaped -replace '\\\?', '[^/]'
    $escaped = $escaped -replace '<<ANYDIR>>', '(?:.*/)?'
    $escaped = $escaped -replace '<<ANY>>', '.*'
    return "^(?i)$escaped$"
}

function Read-RuleLines([string]$Path) {
    return Get-Content $Path | Where-Object { $_ -and ($_ -notmatch '^\s*#') -and ($_ -match '\S') } | ForEach-Object { $_.Trim() }
}

$allFiles = Get-ChildItem -Path $treeFull -Recurse -File -Force
$textFiles = $allFiles | Where-Object { $_.Length -lt 10MB -and $binaryExt -notcontains $_.Extension.ToLowerInvariant() }

# -- 1. ALLOW-LIST gate --------------------------------------------------------
$allowRegexes = @(Read-RuleLines $AllowFile | ForEach-Object { ConvertTo-GlobRegex $_ })
if ($allowRegexes.Count -eq 0) { Write-Host "VERIFY-TREE FAIL: allow-list is empty - nothing may publish." -ForegroundColor Red; exit 1 }

$strays = @()
foreach ($f in $allFiles) {
    $rel = Get-RelPath $f.FullName
    $ok = $false
    foreach ($rx in $allowRegexes) { if ($rel -match $rx) { $ok = $true; break } }
    if (-not $ok) { $strays += $rel }
}
if ($strays.Count -gt 0) {
    $failures += "ALLOW-LIST: $($strays.Count) file(s) not covered by .publicallow (add a conscious entry there, or exclude via .publicignore):"
    $strays | Sort-Object | ForEach-Object { $failures += "  stray: $_" }
}

# -- 2. SECRET / CANARY grep ----------------------------------------------------
# Canary tokens assembled from parts: this script ships in the public tree and
# must never trip the gate on itself. Contiguous literals live only in gated
# sources (page canary) and the private corpus repo (.PRIVATE-CANARY).
$gatedCanary  = "SQLT-GATED-DNA-" + "NEVERSHIP"
$corpusCanary = "SQLT-CORPUS" + "-DNA-"
$secretPatterns = @(
    'AKIA[0-9A-Z]{16}'                                   # AWS access key
    'ghp_[A-Za-z0-9]{30,}'                               # GitHub PAT
    'github_pat_[A-Za-z0-9_]{20,}'                       # GitHub fine-grained PAT
    '-----BEGIN (RSA|OPENSSH|EC|DSA|PGP) PRIVATE KEY-----'
    'xoxb-[A-Za-z0-9-]{20,}'                             # Slack bot
    ('DefaultEndpointsProtocol=https;' + 'AccountKey=')  # Azure storage (split: this file ships)
    'SharedAccessKey=[A-Za-z0-9+/=]{20,}'                # Azure SAS
    'adrian\.sullivan@pure-ip\.com'                      # known leak (G5 audit)
    [regex]::Escape($gatedCanary)                        # gated-source canary
    [regex]::Escape($corpusCanary)                       # corpus-repo leak canary
)
foreach ($pat in $secretPatterns) {
    $found = $textFiles | Select-String -Pattern $pat -List -ErrorAction SilentlyContinue
    foreach ($m in $found) {
        $failures += "SECRET/CANARY: pattern '$pat' in $(Get-RelPath $m.Path):$($m.LineNumber)"
    }
}

# -- 3. COMMERCIAL-LANGUAGE gate -------------------------------------------------
# Locked term set (Adrian 2026-06-12, option C) + sqltriage-meta/CLEAN-REPO-CHECKLIST
# extras. Allow entries in .publiclang-allow: "<pattern-name> :: <glob> :: <reason>".
$langPatterns = @(
    @{ Name = 'premium';    Regex = 'premium';                        CaseSensitive = $false }
    @{ Name = 'license';    Regex = 'licen[cs]'  + 'e';               CaseSensitive = $false }
    @{ Name = 'stripe';     Regex = '\bStripe\b|sk_live_|pk_live_';   CaseSensitive = $true }
    @{ Name = 'unlock';     Regex = 'unlock';                         CaseSensitive = $false }
    @{ Name = 'upsell';     Regex = 'upsell';                         CaseSensitive = $false }
    @{ Name = 'activation'; Regex = 'activation';                     CaseSensitive = $false }
    @{ Name = 'bip39';      Regex = 'bip39|\bwordlist\b';             CaseSensitive = $false }
    @{ Name = 'issuer';     Regex = 'license-issuer|licence-issuer';  CaseSensitive = $false }
    @{ Name = 'currency';   Regex = 'NZ\$';                           CaseSensitive = $true }
    @{ Name = 'pricing';    Regex = 'pricing';                        CaseSensitive = $false }
    @{ Name = 'consmodel';  Regex = 'consolidation[ -]model';         CaseSensitive = $false }
)

$langAllow = @()
foreach ($line in (Read-RuleLines $LangAllowFile)) {
    $parts = $line -split '\s*::\s*'
    if ($parts.Count -lt 3) {
        $failures += "LANGUAGE: malformed .publiclang-allow line (need 'pattern :: glob :: reason'): $line"
        continue
    }
    $langAllow += @{ Name = $parts[0]; Regex = ConvertTo-GlobRegex $parts[1] }
}

foreach ($p in $langPatterns) {
    $hits = if ($p.CaseSensitive) {
        $textFiles | Select-String -Pattern $p.Regex -CaseSensitive -ErrorAction SilentlyContinue
    } else {
        $textFiles | Select-String -Pattern $p.Regex -ErrorAction SilentlyContinue
    }
    foreach ($m in $hits) {
        $rel = Get-RelPath $m.Path
        $allowed = $false
        foreach ($a in $langAllow) {
            if ($a.Name -eq $p.Name -and $rel -match $a.Regex) { $allowed = $true; break }
        }
        if (-not $allowed) {
            $snippet = $m.Line.Trim()
            if ($snippet.Length -gt 100) { $snippet = $snippet.Substring(0, 100) + "..." }
            $failures += "LANGUAGE [$($p.Name)]: ${rel}:$($m.LineNumber) - $snippet"
        }
    }
}

# -- verdict ---------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "=== PUBLIC-TREE VERIFICATION FAILED ($($failures.Count) line(s)) ===" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "verify-public-tree: OK - $($allFiles.Count) files allow-listed; no secrets/canaries; no commercial language" -ForegroundColor Green
exit 0
