param([Parameter(Mandatory)][string]$Engine)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$enginePath = (Resolve-Path $Engine).Path
$cli = Join-Path $root 'artifacts/guardian/guardian.dll'
$folder = (New-Item -ItemType Directory -Force (Join-Path $root 'artifacts/policy-tests')).FullName
$baseline = Join-Path $root 'examples/base.yaml'
$breaking = Join-Path $root 'examples/breaking.yaml'
$compatible = Join-Path $root 'examples/compatible.yaml'
$testEngineName = if ([IO.Path]::DirectorySeparatorChar -eq '\') { 'Guardian.TestEngine.exe' } else { 'Guardian.TestEngine' }
$testEngine = Join-Path $root "tests/Guardian.TestEngine/bin/Release/net10.0/$testEngineName"
$unknownCandidate = Join-Path $folder 'unknown.yaml'
$badOutputCandidate = Join-Path $folder 'bad-output.yaml'
$text = Get-Content -Raw $baseline
[IO.File]::WriteAllText($unknownCandidate, $text + "`nx-test: unmapped-changelog`n")
[IO.File]::WriteAllText($badOutputCandidate, $text + "`nx-test: malformed-changelog`n")

function New-Policy([string]$Name, [string]$Levels = 'ERR', [string]$Unknown = 'true',
    [string]$Override = '', [string]$Version = '1') {
    $file = Join-Path $folder "$Name.yml"
    $yaml = "version: $Version" + "`ncompatibility: {fail_on: [$Levels], fail_on_unknown: $Unknown}`n"
    if ($Override) { $yaml += "rules: {$Override}`n" }
    [IO.File]::WriteAllText($file, $yaml)
    $file
}

$strict = New-Policy 'strict'
$warn = New-Policy 'warn' 'WARN' 'true' 'endpoint-added: WARN'
$lower = New-Policy 'lower' 'ERR' 'true' 'response-property-type-changed: INFO'
$raise = New-Policy 'raise' 'ERR' 'true' 'endpoint-added: ERR'
$relaxed = New-Policy 'relaxed' 'ERR' 'false'
$badVersion = New-Policy 'bad-version' 'ERR' 'true' '' '2'
$badRule = New-Policy 'bad-rule' 'ERR' 'true' 'not-mapped: INFO'
$missing = Join-Path $folder 'missing'
$cases = @(
    @{ Name = 'strict-err'; Policy = $strict; Candidate = $breaking; Code = 2; Status = 'FAIL'; Severity = 'ERR' }
    @{ Name = 'warn-threshold'; Policy = $warn; Candidate = $compatible; Code = 2; Status = 'FAIL'; Severity = 'WARN' }
    @{ Name = 'downgrade'; Policy = $lower; Candidate = $breaking; Code = 0; Status = 'PASS'; Severity = 'INFO' }
    @{ Name = 'upgrade'; Policy = $raise; Candidate = $compatible; Code = 2; Status = 'FAIL'; Severity = 'ERR' }
    @{ Name = 'unknown-strict'; Policy = $strict; Candidate = $unknownCandidate; TestEngine = $true; Code = 2; Status = 'INDETERMINATE'; Severity = 'UNKNOWN' }
    @{ Name = 'unknown-relaxed'; Policy = $relaxed; Candidate = $unknownCandidate; TestEngine = $true; Code = 0; Status = 'PASS'; Severity = 'UNKNOWN' }
    @{ Name = 'unsupported-policy-version'; Policy = $badVersion; Candidate = $compatible; Code = 5; Status = 'INDETERMINATE'; Diagnostic = 'INVALID_POLICY' }
    @{ Name = 'unknown-rule-override'; Policy = $badRule; Candidate = $compatible; Code = 5; Status = 'INDETERMINATE'; Diagnostic = 'INVALID_POLICY' }
    @{ Name = 'missing-policy'; Policy = "$missing.yml"; Candidate = $compatible; Code = 5; Status = 'INDETERMINATE'; Diagnostic = 'INVALID_POLICY' }
    @{ Name = 'missing-baseline'; Policy = $strict; Base = "$missing.yaml"; Candidate = $compatible; Code = 3; Status = 'INDETERMINATE'; Diagnostic = 'INVALID_SPEC' }
    @{ Name = 'engine-failure'; Policy = $strict; Candidate = $badOutputCandidate; TestEngine = $true; Code = 4; Status = 'INDETERMINATE'; Diagnostic = 'ENGINE_FAILURE' }
)

$previousEngine = $env:GUARDIAN_OASDIFF_PATH
try {
    foreach ($case in $cases) {
        $env:GUARDIAN_OASDIFF_PATH = if ($case.TestEngine) { $testEngine } else { $enginePath }
        $out = Join-Path $folder $case.Name
        $base = if ($case.Base) { $case.Base } else { $baseline }
        $stderr = Join-Path $folder ($case.Name + '.stderr.txt')
        $ErrorActionPreference = 'Continue'
        $output = & dotnet $cli compare --base $base --candidate $case.Candidate --policy $case.Policy --out $out 2> $stderr
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($code -ne $case.Code) { throw "$($case.Name): got exit $code. $(Get-Content -Raw $stderr)" }

        $json = Get-Content -Raw (Join-Path $out 'report.json')
        $report = ConvertFrom-Json $json
        $markdown = Get-Content -Raw (Join-Path $out 'report.md')
        if ($report.schema_version -ne '1.0' -or $report.status -ne $case.Status -or
            -not $markdown.Contains(('**' + $case.Status + '**'))) {
            throw "$($case.Name) returned inconsistent reports."
        }
        if ($case.Diagnostic) {
            if ($output -or $report.diagnostics[0].code -ne $case.Diagnostic -or
                -not $markdown.Contains($case.Diagnostic.Replace('_', '&#95;'))) {
                throw "$($case.Name) returned an unexpected diagnostic."
            }
        } else {
            $expectedHash = 'sha256:' + (Get-FileHash $case.Policy).Hash.ToLowerInvariant()
            if ($json.TrimEnd() -cne $output -or $report.findings[0].severity -ne $case.Severity -or
                $report.provenance.policy_sha256 -cne $expectedHash -or -not $markdown.Contains($expectedHash)) {
                throw "$($case.Name) returned unexpected findings or provenance."
            }
        }
        Write-Host "PASS policy $($case.Name)"
    }
    $strictOut = Join-Path $folder 'strict-err'
    $env:GUARDIAN_OASDIFF_PATH = $enginePath
    $before = Get-FileHash (Join-Path $strictOut 'report.json'), (Join-Path $strictOut 'report.md')
    $null = & dotnet $cli compare --base $baseline --candidate $breaking --policy $strict --out $strictOut
    if ($LASTEXITCODE -ne 2) { throw 'Repeat comparison did not return exit 2.' }
    $after = Get-FileHash (Join-Path $strictOut 'report.json'), (Join-Path $strictOut 'report.md')
    if (Compare-Object $before $after -Property Hash, Path) { throw 'Repeated reports differ.' }
} finally {
    $env:GUARDIAN_OASDIFF_PATH = $previousEngine
}
Write-Host "PASS $($cases.Count) policy and report golden cases"
