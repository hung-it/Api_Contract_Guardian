[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Engine,
    [string]$Cli = (Join-Path $PSScriptRoot '../artifacts/guardian/guardian.dll'),
    [string]$FlowForgeContracts,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/contract-tests')
)

$ErrorActionPreference = 'Stop'
$Engine = (Resolve-Path -LiteralPath $Engine).Path
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$inputs = New-Item -Type Directory -Force -Path (Join-Path $OutputDirectory 'input fixtures')
$results = New-Item -Type Directory -Force -Path (Join-Path $OutputDirectory 'results')
$fixture31 = Join-Path $PSScriptRoot '../tests/fixtures/openapi-3.1.json'
$original31 = Get-Content -Raw -LiteralPath $fixture31
$base31 = Join-Path $inputs.FullName 'baseline.json'
[IO.File]::WriteAllText($base31, $original31)

function Write-Variant([string]$Name, [scriptblock]$Change) {
    $spec = ConvertFrom-Json -InputObject $original31
    & $Change $spec
    $path = Join-Path $inputs.FullName "$Name.json"
    [IO.File]::WriteAllText($path, ($spec | ConvertTo-Json -Depth 40))
    $path
}

$requiredQuery = Write-Variant 'required-query' { param($spec)
    $spec.paths.'/tasks'.get.parameters[0].required = $true
}
$requiredBody = Write-Variant 'required-body' { param($spec)
    $schema = $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema
    $schema.properties | Add-Member -NotePropertyName label -NotePropertyValue @{ type = 'string' }
    $schema.required = @('mode', 'label')
}
$narrowedEnum = Write-Variant 'narrowed-enum' { param($spec)
    $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema.properties.mode.enum = @('quick')
}
$removedRequired = Write-Variant 'removed-required-response' { param($spec)
    $schema = $spec.components.schemas.TaskResult
    $schema.properties.PSObject.Properties.Remove('task_id')
    $schema.required = @('state')
}
$throughRef = Write-Variant 'change-through-reference' { param($spec)
    $spec.components.schemas.TaskResult.properties.task_id.type = 'integer'
}
$description = Write-Variant 'description-only' { param($spec)
    $spec.paths.'/tasks'.get.responses.'200'.description = 'Returns one workflow task'
}
$unsupported = Write-Variant 'unsupported' { param($spec) $spec.openapi = '3.2.0' }
$semanticError = Write-Variant 'semantically-invalid' { param($spec)
    $spec.components.schemas.TaskResult.properties.task_id.type = 'nonstandard'
}
$malformedChangelog = Write-Variant 'malformed-changelog' { param($spec) $spec.info.title = 'malformed-changelog' }
$unmappedChangelog = Write-Variant 'unmapped-changelog' { param($spec) $spec.info.title = 'unmapped-changelog' }
$malformed = Join-Path $inputs.FullName 'malformed.json'
[IO.File]::WriteAllText($malformed, '{"openapi":')

$examples = Join-Path $PSScriptRoot '../examples'
$base30 = Join-Path $examples 'base.yaml'
$added = Join-Path $examples 'compatible.yaml'
$breaking30 = Join-Path $examples 'breaking.yaml'
$reordered = Join-Path $inputs.FullName 'reordered response.yaml'
$reorderedSpec = (Get-Content -Raw -LiteralPath $breaking30) -replace '(?m)(  title: Guardian Example)\r?\n(  version: 1\.0\.0)', ('$2' + [Environment]::NewLine + '$1')
[IO.File]::WriteAllText($reordered, "# Same schema, different order and lines$([Environment]::NewLine)$reorderedSpec")

$goldens = @(
    @{ Name = 'validate-yaml-30'; Args = @('validate', $base30); Code = 0 }
    @{ Name = 'validate-json-31'; Args = @('validate', $base31); Code = 0 }
    @{ Name = 'unchanged'; Args = @('compare', '--base', $base30, '--candidate', $base30); Code = 0; Empty = $true }
    @{ Name = 'endpoint-added'; Args = @('compare', '--base', $base30, '--candidate', $added); Code = 0;
       Rule = 'endpoint-added'; Severity = 'INFO'; Method = 'GET'; Path = '/health' }
    @{ Name = 'endpoint-removed'; Args = @('compare', '--base', $added, '--candidate', $base30); Code = 2;
       Rule = 'api-path-removed-without-deprecation'; Severity = 'ERR'; Method = 'GET'; Path = '/health' }
    @{ Name = 'required-query'; Args = @('compare', '--base', $base31, '--candidate', $requiredQuery); Code = 2;
       Rule = 'request-parameter-became-required'; Severity = 'ERR'; Method = 'GET'; Path = '/tasks' }
    @{ Name = 'required-body'; Args = @('compare', '--base', $base31, '--candidate', $requiredBody); Code = 2;
       Rule = 'new-required-request-property'; Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'enum-narrowed'; Args = @('compare', '--base', $base31, '--candidate', $narrowedEnum); Code = 2;
       Rule = 'request-property-enum-value-removed'; Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'response-type'; Args = @('compare', '--base', $base30, '--candidate', $breaking30); Code = 2;
       Rule = 'response-property-type-changed'; Severity = 'ERR'; Method = 'GET'; Path = '/runs' }
    @{ Name = 'response-required-removed'; Args = @('compare', '--base', $base31, '--candidate', $removedRequired); Code = 2;
       Rule = 'response-required-property-removed'; Severity = 'ERR'; Method = 'GET'; Path = '/tasks' }
    @{ Name = 'docs-only'; Args = @('compare', '--base', $base31, '--candidate', $description); Code = 0; Empty = $true }
    @{ Name = 'internal-ref'; Args = @('compare', '--base', $base31, '--candidate', $throughRef); Code = 2;
       Rule = 'response-property-type-changed'; Severity = 'ERR'; Method = 'GET'; Path = '/tasks' }
    @{ Name = 'malformed'; Args = @('validate', $malformed); Code = 3; Error = 'syntax' }
    @{ Name = 'unsupported'; Args = @('validate', $unsupported); Code = 3; Error = 'OpenAPI version' }
    @{ Name = 'engine-failure'; Args = @('validate', $base31); Code = 4; Error = 'GUARDIAN_OASDIFF_PATH'; MissingEngine = $true }
)

function Invoke-Case([hashtable]$Case) {
    $arguments = $Case.Args
    $log = Join-Path $results.FullName ($Case.Name + '.stderr.txt')
    $env:GUARDIAN_OASDIFF_PATH = if ($Case.MissingEngine) {
        Join-Path $inputs.FullName 'missing-oasdiff'
    } elseif ($Case.TestEngine) {
        $Case.TestEngine
    } else {
        $Engine
    }
    $ErrorActionPreference = 'Continue'
    $stdout = (& dotnet $Cli @arguments 2> $log) -join "`n"
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $stderr = Get-Content -Raw -LiteralPath $log
    if ($exitCode -ne $Case.Code) {
        throw "$($Case.Name) exited $exitCode, expected $($Case.Code). $stderr $stdout"
    }

    if ($Case.Error) {
        if ($stdout -or $stderr -notmatch $Case.Error) { throw "$($Case.Name) produced an unexpected diagnostic." }
    }
    elseif ($arguments[0] -eq 'validate') {
        if ($stderr -or $stdout -notmatch 'OpenAPI spec is valid') { throw "$($Case.Name) did not validate." }
    }
    else {
        [IO.File]::WriteAllText((Join-Path $results.FullName ($Case.Name + '.json')), $stdout)
        $comparison = ConvertFrom-Json -InputObject $stdout
        if ($stderr -or $comparison.engine -ne 'oasdiff' -or $comparison.engine_version -ne '1.38.0') {
            throw "$($Case.Name) returned an unexpected result envelope."
        }
        $findings = @($comparison.findings)
        if ($Case.Empty -and $findings.Count -ne 0) { throw "$($Case.Name) expected zero findings." }
        if ($Case.Rule) {
            $matching = @($findings | Where-Object {
                $_.rule_id -eq $Case.Rule -and $_.severity -eq $Case.Severity -and $_.method -eq $Case.Method -and $_.path -eq $Case.Path
            })
            if ($matching.Count -ne 1 -or $matching[0].fingerprint -notmatch '^sha256:[0-9a-f]{64}$') {
                throw "$($Case.Name) did not return the expected finding. $stdout"
            }
            $matching[0].fingerprint
        }
    }
    Write-Host "PASS $($Case.Name)"
}

$previousEngine = $env:GUARDIAN_OASDIFF_PATH
try {
    $expectedFingerprint = $null
    foreach ($golden in $goldens) {
        $fingerprint = Invoke-Case $golden
        if ($golden.Name -eq 'response-type') { $expectedFingerprint = $fingerprint }
    }

    $actualFingerprint = Invoke-Case @{
        Name = 'reordered-yaml'; Args = @('compare', '--candidate', $reordered, '--base', $base30); Code = 2;
        Rule = 'response-property-type-changed'; Severity = 'ERR'; Method = 'GET'; Path = '/runs'
    }
    if ($actualFingerprint -ne $expectedFingerprint) { throw 'Fingerprint changed after reordering YAML.' }
    Invoke-Case @{ Name = 'semantic-validation'; Args = @('validate', $semanticError); Code = 3; Error = 'schema-type-unsupported' }
    Invoke-Case @{ Name = 'mixed-dialects'; Args = @('compare', '--base', $base30, '--candidate', $base31); Code = 3; Error = 'same OpenAPI major and minor' }
    Invoke-Case @{ Name = 'duplicate-option'; Args = @('compare', '--base', $base30, '--base', $base30); Code = 1; Error = 'Invalid arguments' }

    $testEngineName = if ([IO.Path]::DirectorySeparatorChar -eq '\') { 'Guardian.TestEngine.exe' } else { 'Guardian.TestEngine' }
    $testEngine = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../tests/Guardian.TestEngine/bin/Release/net10.0/$testEngineName")).Path
    Invoke-Case @{
        Name = 'malformed-engine-json'; Args = @('compare', '--base', $base31, '--candidate', $malformedChangelog);
        Code = 4; Error = 'invalid changelog JSON'; TestEngine = $testEngine
    }
    $null = Invoke-Case @{
        Name = 'unknown-rule'; Args = @('compare', '--base', $base31, '--candidate', $unmappedChangelog);
        Code = 2; Rule = 'unmapped-rule'; Severity = 'UNKNOWN'; TestEngine = $testEngine
    }

    if ($FlowForgeContracts) {
        $flowBase = Join-Path $FlowForgeContracts 'base.json'
        $flowCandidate = Join-Path $FlowForgeContracts 'breaking.json'
        Invoke-Case @{ Name = 'flowforge-unchanged'; Args = @('compare', '--base', $flowBase, '--candidate', $flowBase); Code = 0; Empty = $true }
        $null = Invoke-Case @{
            Name = 'flowforge-breaking'; Args = @('compare', '--base', $flowBase, '--candidate', $flowCandidate); Code = 2;
            Rule = 'response-property-type-changed'; Severity = 'ERR'; Method = 'GET'; Path = '/api/v1/runs/{run_id}'
        }
    }
    Write-Host "PASS $($goldens.Count) golden cases and CLI boundary checks"
}
finally {
    $env:GUARDIAN_OASDIFF_PATH = $previousEngine
}
