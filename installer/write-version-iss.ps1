# Generates installer\version.iss from Config\version.json so iscc.exe
# can pick up AppVersion / BuildNumber without parsing JSON itself.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$v = Get-Content (Join-Path $root 'Config\version.json') -Raw | ConvertFrom-Json
$out = Join-Path $PSScriptRoot 'version.iss'
$content = "#define AppVersion `"$($v.version)`"`r`n#define BuildNumber `"$($v.buildNumber)`""
Set-Content -Path $out -Value $content -Encoding utf8
