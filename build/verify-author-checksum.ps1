# Verifies the commissioned author assessment in Pages/About.razor still matches its
# authored SHA-256. Fails the build if the text was edited — the author asked to be held
# to this checksum on every build (see About.razor / PRODUCT-ROADMAP). Runtime check exists
# too; this is the can't-ship-tampered build-time gate.
param(
    [Parameter(Mandatory = $true)][string]$AboutRazorPath
)

$ErrorActionPreference = 'Stop'
$expected = 'c0d6e3b6adcafeb0f22364c9915972619582bff8fb2a6ba02b84da77552c54e8'

if (-not (Test-Path $AboutRazorPath)) {
    Write-Error "verify-author-checksum: About.razor not found at $AboutRazorPath"
    exit 1
}

$content = Get-Content -Raw -Encoding UTF8 $AboutRazorPath

# Extract the verbatim const AuthorAssessment = @"..."; block.
$m = [regex]::Match($content, 'AuthorAssessment\s*=\s*@"(.*?)";', [System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $m.Success) {
    Write-Error "verify-author-checksum: could not locate AuthorAssessment verbatim string in About.razor"
    exit 1
}

# Un-escape the C# verbatim string ("" -> ") to recover the original text, normalise to LF.
$text = $m.Groups[1].Value.Replace('""', '"').Replace("`r`n", "`n")
$bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $sha = [System.BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
} finally {
    $sha256.Dispose()
}

if ($sha -ne $expected) {
    Write-Error "verify-author-checksum: AUTHOR ASSESSMENT TAMPERED. Expected $expected but got $sha. The commissioned assessment in About.razor must not be edited."
    exit 1
}

Write-Host "[author-checksum] About assessment intact ($sha)"
exit 0
