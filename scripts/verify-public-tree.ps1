# verify-public-tree.ps1
# ---------------------------------------------------------------------------
# FAILS-WHEN-VIOLATED gates over a curated public-publish tree (2026-06-12,
# publish-hardening session). Invoked by publish-public.ps1 before any push and
# by scripts/selftest-gating.ps1 (negative tests). Runnable by hand:
#   powershell -File scripts\verify-public-tree.ps1 -TreePath <curated-tree>
#
# Five gates (any violation => exit 1):
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
#   4. CLIENT-NAME GREP: real/former client org names, domains, and hostnames
#      must never appear. The deny patterns live in .handoff/.publicclient-deny
#      (never shipped, read from the DEV repo like the allow files) so this
#      script - which itself ships - never contains the literals it polices.
#      One allow: the "CycloneDX" SBOM tool name is stripped from a line
#      before matching, so a line naming both the tool and a client still
#      fails. No path-based allow-list - a real client name has no legitimate
#      reason to be in the public tree.
#   5. PRIVATE-REF GREP: pointers into the private strategy repo - its name,
#      its decision log, its handoff, buildcheck and lock folders - must never
#      appear. Deny patterns live in .handoff/.publicprivateref-deny (never
#      shipped, read from the DEV repo) for the same reason as gate 4: holding
#      the terms outside this script is what keeps the script, which ships,
#      from flagging itself. No path allow-list and no self-exemption - a
#      private-repo pointer has no legitimate reason to be in the public tree,
#      and an allow-list nobody can explain is the failure mode this gate set
#      exists to avoid. Added 2026-09-05: a tree curated from dev main carried
#      nine such pointers in eight files, and not one of the four earlier gates
#      looks for them.
#
# The rule files all live in .handoff/ (never shipped) and are read from the
# DEV repo, not from the tree under test.
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$TreePath,
    [string]$AllowFile,
    [string]$LangAllowFile,
    [string]$ClientDenyFile,
    [string]$PrivateRefDenyFile
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
if (-not $AllowFile)     { $AllowFile     = Join-Path $repo ".handoff\.publicallow" }
if (-not $LangAllowFile) { $LangAllowFile = Join-Path $repo ".handoff\.publiclang-allow" }
if (-not $ClientDenyFile) { $ClientDenyFile = Join-Path $repo ".handoff\.publicclient-deny" }
if (-not $PrivateRefDenyFile) { $PrivateRefDenyFile = Join-Path $repo ".handoff\.publicprivateref-deny" }

if (-not (Test-Path $TreePath))  { Write-Host "VERIFY-TREE FAIL: tree not found: $TreePath" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $AllowFile)) { Write-Host "VERIFY-TREE FAIL: allow-list not found: $AllowFile - refusing to publish without it." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $LangAllowFile)) { Write-Host "VERIFY-TREE FAIL: language allow-list not found: $LangAllowFile" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $ClientDenyFile)) { Write-Host "VERIFY-TREE FAIL: client-name deny-list not found: $ClientDenyFile - refusing to publish without it." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $PrivateRefDenyFile)) { Write-Host "VERIFY-TREE FAIL: private-ref deny-list not found: $PrivateRefDenyFile - refusing to publish without it." -ForegroundColor Red; exit 1 }

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
# Locked term set (Adrian 2026-06-12, option C) plus the extras from the
# clean-repo checklist held in the private strategy repo. Neither that repo nor
# its files are named here - gate 5 polices those names, this script ships, and
# a checker that trips itself is no checker. Allow entries in .publiclang-allow:
# "<pattern-name> :: <glob> :: <reason>".
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

# -- 4. CLIENT-NAME gate ----------------------------------------------------------
# Deny patterns come from .handoff/.publicclient-deny (never shipped) so this
# script - which ships - never contains a client literal. The "CycloneDX" SBOM
# tool name is stripped BEFORE matching (match-level, not line-level), so a line
# naming both the tool and a real client still fails on the client name.
# Each rule is wrapped in its own non-capturing group before the join, and read
# through Read-RuleLines so trailing whitespace and blank lines cannot change a
# pattern's meaning. Without both, one malformed line silently rewrites the whole
# gate: a trailing space narrows the term, and a leading inline option such as
# (?-i) leaks into every alternative after it and turns the gate green.
$clientNamePattern = (Read-RuleLines $ClientDenyFile | ForEach-Object { '(?:' + $_ + ')' }) -join '|'
if (-not $clientNamePattern) { Write-Host "VERIFY-TREE FAIL: client-name deny-list is empty: $ClientDenyFile" -ForegroundColor Red; exit 1 }
$clientNameHits = $textFiles | Select-String -Pattern $clientNamePattern -ErrorAction SilentlyContinue
foreach ($m in $clientNameHits) {
    $stripped = $m.Line -replace '(?i)cyclonedx', ''
    if ($stripped -notmatch $clientNamePattern) { continue }   # only the SBOM tool name matched
    $rel = Get-RelPath $m.Path
    $snippet = $m.Line.Trim()
    if ($snippet.Length -gt 100) { $snippet = $snippet.Substring(0, 100) + "..." }
    $failures += "CLIENT-NAME: ${rel}:$($m.LineNumber) - $snippet"
}

# -- 5. PRIVATE-REF gate ----------------------------------------------------------
# Deny patterns come from .handoff/.publicprivateref-deny (never shipped) so this
# script - which ships - never contains a pointer to the private strategy repo and
# so never flags itself. No allow-list and no self-exemption: nothing in the public
# tree has a legitimate reason to name the private repo, its decision log, or its
# handoff, buildcheck and lock folders. Every pattern in the deny file carries its
# own reason, including why the lock-folder one needs a letter boundary - name that
# folder here and the checker flags itself, which is the point of gate 4's design
# and the reason this one copies it.
$privateRefPattern = (Read-RuleLines $PrivateRefDenyFile | ForEach-Object { '(?:' + $_ + ')' }) -join '|'
if (-not $privateRefPattern) { Write-Host "VERIFY-TREE FAIL: private-ref deny-list is empty: $PrivateRefDenyFile" -ForegroundColor Red; exit 1 }
$privateRefHits = $textFiles | Select-String -Pattern $privateRefPattern -ErrorAction SilentlyContinue
foreach ($m in $privateRefHits) {
    $rel = Get-RelPath $m.Path
    $snippet = $m.Line.Trim()
    if ($snippet.Length -gt 100) { $snippet = $snippet.Substring(0, 100) + "..." }
    $failures += "PRIVATE-REF: ${rel}:$($m.LineNumber) - $snippet"
}

# -- verdict ---------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "=== PUBLIC-TREE VERIFICATION FAILED ($($failures.Count) line(s)) ===" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "verify-public-tree: OK - $($allFiles.Count) files allow-listed; no secrets/canaries; no commercial language; no client names; no private-repo pointers" -ForegroundColor Green
exit 0
