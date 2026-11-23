[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Engine,
    [string]$FlowForgeContracts,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/oasdiff')
)

$ErrorActionPreference = 'Stop'
$Engine = (Resolve-Path -LiteralPath $Engine).Path
$version = & $Engine --version
if ($LASTEXITCODE -ne 0 -or $version -notmatch '\b1\.38\.0\b') {
    throw "Expected oasdiff 1.38.0. Received: $version"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Set-Content -LiteralPath (Join-Path $OutputDirectory 'version.txt') -Value $version

function Test-Spec([string]$Path) {
    & $Engine validate --fail-on ERR $Path
    if ($LASTEXITCODE -ne 0) { throw "Validation failed for $Path." }
}

function Compare-Spec([string]$Base, [string]$Candidate, [string]$Name) {
    $json = (& $Engine changelog --format json $Base $Candidate) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "oasdiff exited $LASTEXITCODE for $Name." }
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $OutputDirectory "$Name.json") -Value $json
    if ($json.Trim() -eq 'null') { return }
    if (-not $json.TrimStart().StartsWith('[')) { throw "Expected a JSON array for $Name." }
    foreach ($finding in (ConvertFrom-Json -InputObject $json)) {
        $finding
    }
}

$examples = Join-Path $PSScriptRoot '../examples'
$baseline = Join-Path $examples 'base.yaml'
$compatible = Join-Path $examples 'compatible.yaml'
$breaking = Join-Path $examples 'breaking.yaml'
Test-Spec $baseline
Test-Spec $compatible
Test-Spec $breaking

if (@(Compare-Spec $baseline $baseline 'unchanged').Count -ne 0) {
    throw 'Expected no findings when comparing the sample contract with itself.'
}

$changes = @(Compare-Spec $baseline $compatible 'compatible')
if (($changes | Where-Object { $_.level -ge 3 }) -or -not ($changes | Where-Object {
    $_.id -eq 'endpoint-added' -and $_.operation -eq 'GET' -and $_.path -eq '/health'
})) { throw 'Expected a compatible GET /health addition.' }

$changes = @(Compare-Spec $baseline $breaking 'breaking')
if (-not ($changes | Where-Object {
    $_.id -eq 'response-property-type-changed' -and $_.level -eq 3 -and
    $_.operation -eq 'GET' -and $_.path -eq '/runs' -and $_.text -match '\brun_id\b'
})) { throw 'Expected a breaking run_id type change in GET /runs.' }

if ($FlowForgeContracts) {
    $flowBase = Join-Path $FlowForgeContracts 'base.json'
    $flowBreaking = Join-Path $FlowForgeContracts 'breaking.json'
    Test-Spec $flowBase
    Test-Spec $flowBreaking
    if (@(Compare-Spec $flowBase $flowBase 'flowforge-unchanged').Count -ne 0) {
        throw 'Expected no findings when comparing the FlowForge contract with itself.'
    }
    $changes = @(Compare-Spec $flowBase $flowBreaking 'flowforge-breaking')
    if (-not ($changes | Where-Object {
        $_.id -eq 'response-property-type-changed' -and $_.level -eq 3 -and
        $_.operation -eq 'GET' -and $_.path -eq '/api/v1/runs/{run_id}' -and $_.text -match '\brun_id\b'
    })) { throw 'Expected a breaking run_id type change in GET /api/v1/runs/{run_id}.' }
}

Write-Host 'PASS OpenAPI probes'
