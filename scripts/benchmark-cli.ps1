[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Engine,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
    [string]$Cli = (Join-Path $PSScriptRoot '../artifacts/guardian/guardian.dll'),
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/benchmark'),
    [ValidateRange(0, 100)][int]$Warmup = 5,
    [ValidateRange(1, 1000)][int]$Samples = 30
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:GUARDIAN_OASDIFF_PATH = (Resolve-Path -LiteralPath $Engine).Path
$Cli = (Resolve-Path -LiteralPath $Cli).Path
if (-not $IsLinux -or -not (Test-Path -LiteralPath '/usr/bin/time')) {
    throw 'Run the benchmark on Linux with GNU time installed.'
}
$folder = (New-Item -ItemType Directory -Force -Path $Output).FullName
$guardianVersion = & dotnet $Cli version
if ($LASTEXITCODE -ne 0) { throw 'Could not identify Guardian.' }
$engineVersion = & $env:GUARDIAN_OASDIFF_PATH --version
if ($LASTEXITCODE -ne 0) { throw 'Could not identify oasdiff.' }
$sdkVersion = & dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the .NET SDK.' }

function Write-Spec([int]$Count, [bool]$Breaking, [string]$File) {
    $paths = [ordered]@{}
    foreach ($operation in 1..$Count) {
        $responseId = if ($Breaking -and $operation -eq 1) { @{ type = 'integer' } }
        else { [ordered]@{ type = 'string'; format = 'uuid' } }
        $paths["/items/$operation"] = @{ get = @{ responses = @{ '200' = [ordered]@{
            description = 'Item'
            content = @{ 'application/json' = @{ schema = [ordered]@{
                type = 'object'
                properties = @{ id = $responseId }
            } } }
        } } } }
    }
    $spec = [ordered]@{ openapi = '3.1.0'; info = [ordered]@{ title = 'Guardian benchmark'; version = '1' }; paths = $paths }
    [IO.File]::WriteAllText($File, ($spec | ConvertTo-Json -Depth 16 -Compress) + "`n")
}

function Read-SystemFile([string]$File) {
    if (Test-Path -LiteralPath $File) { [IO.File]::ReadAllText($File).Trim() }
}

$rows = [Collections.Generic.List[object]]::new()
$workloads = foreach ($count in 10, 100, 500) {
    $workload = (New-Item -ItemType Directory -Force -Path (Join-Path $folder "$count")).FullName
    $baseline = Join-Path $workload 'base.json'
    $candidate = Join-Path $workload 'candidate.json'
    Write-Spec $count $false $baseline
    Write-Spec $count $true $candidate
    $reportFolder = Join-Path $workload 'report'
    $memoryFile = Join-Path $workload 'rss.txt'
    $reportPath = Join-Path $reportFolder 'report.json'
    $stderrFile = Join-Path $workload 'stderr.log'
    foreach ($sample in 1..($Warmup + $Samples)) {
        $phase = if ($sample -le $Warmup) { 'warmup' } else { 'sample' }
        $number = if ($phase -eq 'warmup') { $sample } else { $sample - $Warmup }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $stdout = & /usr/bin/time -q -f '%M' -o $memoryFile dotnet $Cli compare --base $baseline `
            --candidate $candidate --out $reportFolder 2> $stderrFile
        $code = $LASTEXITCODE
        $watch.Stop()
        $stderr = Read-SystemFile $stderrFile
        $failure = if ($code -ne 2 -or $stderr) { "Expected exit 2 and no diagnostic. Exit $code. $stderr" }
        else {
            try {
                $json = Read-SystemFile $reportPath
                $report = $json | ConvertFrom-Json
                if (($stdout -join "`n").Trim() -ne $json -or $report.status -ne 'FAIL' -or
                    $report.findings.Count -ne 1 -or $report.findings[0].rule_id -ne 'response-property-type-changed' -or
                    $report.findings[0].path -ne '/items/1' -or $report.findings[0].method -ne 'GET' -or
                    -not (Test-Path -LiteralPath (Join-Path $reportFolder 'report.md'))) {
                    'Expected one GET /items/1 response type finding in a FAIL report.'
                }
            } catch { "Could not verify the report. $($_.Exception.Message)" }
        }
        $rss = 0L
        if (-not [long]::TryParse((Read-SystemFile $memoryFile), [ref]$rss) -or $rss -le 0) {
            $failure = "$failure Missing peak RSS.".Trim()
        }
        $rows.Add([pscustomobject]@{
            operations = $count; phase = $phase; run = $number
            elapsed_ms = [math]::Round($watch.Elapsed.TotalMilliseconds, 3); peak_rss_kib = $rss
            exit_code = $code; error = $failure
        })
        if ($failure) { Write-Warning "$count operations, $phase $number. $failure" }
    }

    $measured = @($rows | Where-Object { $_.operations -eq $count -and $_.phase -eq 'sample' -and -not $_.error })
    $times = @($measured.elapsed_ms | Sort-Object)
    $middle = [int][math]::Floor($times.Count / 2)
    $median = if ($times.Count % 2 -eq 1) { $times[$middle] }
    elseif ($times.Count) { ($times[$middle - 1] + $times[$middle]) / 2 }
    [pscustomobject]@{
        operations = $count
        baseline_sha256 = (Get-FileHash -LiteralPath $baseline).Hash.ToLowerInvariant()
        candidate_sha256 = (Get-FileHash -LiteralPath $candidate).Hash.ToLowerInvariant()
        successful_samples = $times.Count
        median_ms = if ($times.Count) { [math]::Round($median, 3) }
        p95_ms = if ($times.Count) { $times[[int][math]::Ceiling($times.Count * 0.95) - 1] }
        max_peak_rss_kib = if ($times.Count) { ($measured.peak_rss_kib | Measure-Object -Maximum).Maximum }
    }
}

$rows | Export-Csv -NoTypeInformation -Path (Join-Path $folder 'samples.csv')
$errors = @($rows | Where-Object error).Count
$result = [ordered]@{
    source_sha = $SourceSha; started_workloads = @(10, 100, 500); warmup_per_workload = $Warmup
    samples_per_workload = $Samples; failed_runs = $errors; recorded_utc = [DateTime]::UtcNow.ToString('o')
    guardian = $guardianVersion; oasdiff = $engineVersion; dotnet_sdk = $sdkVersion
    os = Read-SystemFile '/etc/os-release'; architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    kernel = & uname -r
    cpu = (Get-Content /proc/cpuinfo | Select-String '^model name' | Select-Object -First 1).Line
    memory = (Get-Content /proc/meminfo | Select-String '^MemTotal' | Select-Object -First 1).Line
    cpu_limit = Read-SystemFile '/sys/fs/cgroup/cpu.max'; memory_limit_bytes = Read-SystemFile '/sys/fs/cgroup/memory.max'
    workloads = @($workloads)
}
$result | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $folder 'benchmark.json')
$workloads | Format-Table operations, successful_samples, median_ms, p95_ms, max_peak_rss_kib
if ($errors) { throw "$errors benchmark runs failed. See samples.csv." }
