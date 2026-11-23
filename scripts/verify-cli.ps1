[CmdletBinding()]
param(
    [string]$Cli = (Join-Path $PSScriptRoot '../artifacts/guardian/guardian.dll')
)

$ErrorActionPreference = 'Stop'
$Cli = (Resolve-Path -LiteralPath $Cli).Path

# Windows PowerShell records native stderr as an error even when redirected.
$stderrFile = New-TemporaryFile
try {
    foreach ($case in @(
        @{ Name = 'no arguments'; Arguments = @(); Code = 0; Output = 'Usage: guardian' }
        @{ Name = '--help'; Arguments = @('--help'); Code = 0; Output = 'Usage: guardian' }
        @{ Name = 'version'; Arguments = @('version'); Code = 0; Output = '^0\.1\.0$' }
        @{ Name = 'unknown command'; Arguments = @('unknown'); Code = 1; Error = 'Invalid arguments' }
        @{ Name = 'extra argument'; Arguments = @('version', 'extra'); Code = 1; Error = 'Invalid arguments' }
    )) {
        $arguments = $case.Arguments
        $ErrorActionPreference = 'Continue'
        $stdout = (& dotnet $Cli @arguments 2> $stderrFile.FullName) -join "`n"
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        $stderr = Get-Content -Raw -LiteralPath $stderrFile.FullName
        if ($code -ne $case.Code) { throw "$($case.Name) exited $code, expected $($case.Code)." }
        if ($case.Code -eq 0) {
            if ($stdout -notmatch $case.Output -or $stderr) { throw "$($case.Name) produced unexpected output." }
        } elseif ($stdout -or $stderr -notmatch $case.Error) {
            throw "$($case.Name) did not write the expected error to stderr."
        }
        Write-Host "PASS $($case.Name)"
    }
} finally {
    Remove-Item -LiteralPath $stderrFile.FullName
}
