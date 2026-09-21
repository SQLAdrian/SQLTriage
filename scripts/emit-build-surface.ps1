# emit-build-surface.ps1
# ---------------------------------------------------------------------------
# Emits a BUILD SURFACE file: the set of Compile/Content items and the
# DefineConstants that MSBuild ACTUALLY evaluates for a given profile, plus a
# provenance header saying which project, profile, configuration and RID
# produced it.
#
# It is the STANDALONE emitter (tests, hand runs). The authoritative emitter is
# buildprofile.targets' own SQLTriageCommunitySurfaceCensus target, which writes
# the same format from the evaluation of the build being gated - so the census
# reads the item list of THAT build, not of a look-alike evaluation.
#
# Both emitters write the same v1 format, consumed by census-community-surface.ps1:
#   P|<name>=<value>          provenance / properties  (DefineConstants ';' -> ',')
#   C|<identity>              a Content item whose extension is .razor
#   S|<identity>              a Compile item
#
# Evaluation only: -getItem runs no targets, writes no build output, and does not
# restore, so this never touches packages.lock.json.
# ---------------------------------------------------------------------------
param(
    [string]$ProjectFile,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$Profile = "community",
    [string]$Configuration = "Debug",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

if (-not $ProjectFile) { $ProjectFile = Join-Path (Split-Path -Parent $PSScriptRoot) "SQLTriage.csproj" }
if (-not (Test-Path $ProjectFile)) { Write-Host "emit-build-surface: project not found: $ProjectFile" -ForegroundColor Red; exit 1 }
$ProjectFile = (Resolve-Path $ProjectFile).Path

$tmp = [System.IO.Path]::GetTempFileName()
try {
    # PS 5.1: never 2>&1 on a native command - it turns stderr into a terminating error.
    & dotnet msbuild $ProjectFile -nologo `
        "-p:SQLTriageProfile=$Profile" "-p:Configuration=$Configuration" "-p:RuntimeIdentifier=$RuntimeIdentifier" `
        -getItem:Content -getItem:Compile -getProperty:DefineConstants | Out-File -FilePath $tmp -Encoding utf8
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "emit-build-surface: dotnet msbuild evaluation FAILED (exit $code) for profile '$Profile'" -ForegroundColor Red
        Get-Content $tmp | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    $json = (Get-Content $tmp -Raw) | ConvertFrom-Json
} finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

$defines = ""
if ($json.Properties) { $defines = [string]$json.Properties.DefineConstants }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# sqltriage-build-surface v1")
$lines.Add("P|Producer=emit-build-surface.ps1")
$lines.Add("P|ProjectFullPath=$ProjectFile")
$lines.Add("P|SQLTriageProfile=$Profile")
$lines.Add("P|Configuration=$Configuration")
$lines.Add("P|RuntimeIdentifier=$RuntimeIdentifier")
$lines.Add("P|DefineConstants=" + $defines.Replace(';', ','))
$lines.Add("P|EmittedUtc=" + (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))

$nRazor = 0
foreach ($c in $json.Items.Content) {
    if ($c.Identity -and $c.Identity.ToLowerInvariant().EndsWith(".razor")) { $lines.Add("C|" + $c.Identity); $nRazor++ }
}
$nCs = 0
foreach ($c in $json.Items.Compile) {
    if ($c.Identity) { $lines.Add("S|" + $c.Identity); $nCs++ }
}

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
Set-Content -Path $OutFile -Value $lines -Encoding ASCII
Write-Host "emit-build-surface: $OutFile  profile=$Profile razor=$nRazor compile=$nCs"
exit 0
