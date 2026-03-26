# Risk Metadata Mapper
# Delegates to map-risk-metadata.js (Node.js) for reliable multi-line CSV parsing
# Run: .\map-risk-metadata.ps1

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
node "$scriptDir\map-risk-metadata.js"
