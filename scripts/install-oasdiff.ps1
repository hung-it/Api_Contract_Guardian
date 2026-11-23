[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '../artifacts/tools'),
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
$version = '1.38.0'
$platform = if ($env:OS -eq 'Windows_NT') { 'windows' } else { 'linux' }
$checksum = @{
    windows = '4509a5d838849ab39c759b6ed95a0b9193b80a69e55b88c6c9e72d7bde21bbcf'
    linux = '613037fd6a1d3362a1b742d6d86c338c0eb5ab3511f4942ce3948e78720ea2da'
}[$platform]
$archiveName = "oasdiff_${version}_${platform}_amd64.tar.gz"
$binary = if ($platform -eq 'windows') { 'oasdiff.exe' } else { 'oasdiff' }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
if (-not $ArchivePath) {
    $ArchivePath = Join-Path $Destination $archiveName
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        $url = "https://github.com/oasdiff/oasdiff/releases/download/v$version/$archiveName"
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $ArchivePath
    }
}

$actual = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
if ($actual -ne $checksum) {
    throw "oasdiff archive SHA-256 mismatch: $actual"
}

tar -xzf $ArchivePath -C $Destination $binary
if ($LASTEXITCODE -ne 0) { throw 'Unable to extract the verified oasdiff archive.' }

Write-Host "Verified oasdiff v$version archive ($platform x64): $checksum"
(Resolve-Path -LiteralPath (Join-Path $Destination $binary)).Path
