# In the name of God, the Merciful, the Compassionate
# install-hooks.ps1 — wires repo-local Git hooks to tools/ scripts.
# Usage: pwsh tools/install-hooks.ps1

$ErrorActionPreference = 'Stop'
$repoRoot  = Split-Path -Parent $PSScriptRoot
$hooksDir  = Join-Path $repoRoot '.git/hooks'
$hookPath  = Join-Path $hooksDir 'pre-commit'

if (-not (Test-Path $hooksDir)) {
    throw "Not a git repo (no .git/hooks at $hooksDir)"
}

# Wrapper invokes the tracked script so updates flow via git pull.
$wrapper = @'
#!/usr/bin/env bash
exec "$(git rev-parse --show-toplevel)/tools/pre-commit-basmalah.sh"
'@

Set-Content -Path $hookPath -Value $wrapper -Encoding ASCII -NoNewline
Write-Host "Installed: $hookPath"
Write-Host "Test it: git commit -m 'test' on a file missing basmalah — expect block."
