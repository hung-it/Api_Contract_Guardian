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
$optionalResponse = Write-Variant 'optional-response' { param($spec)
    $spec.components.schemas.TaskResult.required = @('state')
}
$optionalQuery = Write-Variant 'optional-query' { param($spec)
    $spec.paths.'/tasks'.get.parameters += @{ name = 'state'; in = 'query'; schema = @{ type = 'string' } }
}
$optionalBody = Write-Variant 'optional-body' { param($spec)
    $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema.properties |
        Add-Member -NotePropertyName label -NotePropertyValue @{ type = 'string' }
}
$changedMediaType = Write-Variant 'changed-request-media-type' { param($spec)
    $content = $spec.paths.'/tasks'.post.requestBody.content
    $content | Add-Member -NotePropertyName 'application/xml' -NotePropertyValue $content.'application/json'
    $content.PSObject.Properties.Remove('application/json')
}
$closedBody = Write-Variant 'closed-body' { param($spec)
    $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema |
        Add-Member -NotePropertyName additionalProperties -NotePropertyValue $false
}
$typedAdditional = Write-Variant 'typed-additional' { param($spec)
    $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema |
        Add-Member -NotePropertyName additionalProperties -NotePropertyValue @{ type = 'string' }
}
$closedOptional = Write-Variant 'closed-optional' { param($spec)
    $schema = $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema
    $schema | Add-Member -NotePropertyName additionalProperties -NotePropertyValue $false
    $schema.properties | Add-Member -NotePropertyName label -NotePropertyValue @{ type = 'string' }
}
function Write-ObjectBody([string]$Name, [hashtable]$Schema) {
    Write-Variant $Name { param($spec)
        $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema = $Schema
    }
}
$openObject = @{ type = 'object'; properties = @{ label = @{ type = 'string' } } }
$closedObject = $openObject + @{ additionalProperties = $false }
$allOfOpen = Write-ObjectBody 'allof-open' @{ allOf = @($openObject) }
$allOfClosed = Write-ObjectBody 'allof-closed' @{ allOf = @($closedObject) }
$allOfAnnotated = Write-ObjectBody 'allof-annotated' @{ allOf = @($openObject + @{ description = 'Updated' }) }
$capped = @{ required = @('label'); maxProperties = 1 }
$cappedOpen = Write-ObjectBody 'capped-open' ($openObject + $capped)
$cappedClosed = Write-ObjectBody 'capped-closed' ($closedObject + $capped)
$length1 = Write-ObjectBody 'min-length-1' @{ type = 'object'; properties = @{ label = @{ type = 'string'; minLength = 1 } } }
$length3 = Write-ObjectBody 'min-length-3' @{ type = 'object'; properties = @{ label = @{ type = 'string'; minLength = 3 } } }
$lengthAbsent = Write-ObjectBody 'min-length-absent' @{ type = 'object'; properties = @{ label = @{ type = 'string' } } }
$closedBodyReformatted = Join-Path $inputs.FullName 'closed-body-reformatted.json'
$reformatted = Get-Content -Raw -LiteralPath $closedBody | ConvertFrom-Json
$reformatted.info | Add-Member -NotePropertyName description -NotePropertyValue 'Moved source lines'
[IO.File]::WriteAllText($closedBodyReformatted, ($reformatted | ConvertTo-Json -Depth 40 -Compress))
$scalarBody = Write-Variant 'scalar-body' { param($spec)
    $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema = @{ type = 'string' }
}
$closedScalar = Write-Variant 'closed-scalar' { param($spec)
    $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema = @{ type = 'string'; additionalProperties = $false }
}
function Write-ArrayBody([string]$Name, [bool]$AllowExtras) {
    Write-Variant $Name {
        param($spec)
        $spec.paths.'/tasks'.post.requestBody.content.'application/json'.schema = @{
            type = 'object'; properties = @{ nested = @{ type = 'array'; items = @{ '$ref' = '#/components/schemas/Item' } } }
        }
        $spec.components.schemas | Add-Member -NotePropertyName Item -NotePropertyValue @{
            type = 'object'; additionalProperties = $AllowExtras
            properties = @{ child = @{ '$ref' = '#/components/schemas/Item' } }
        }
    }
}
$arrayBody = Write-ArrayBody 'array-of-items' $true
$closedArrayBody = Write-ArrayBody 'array-of-closed-items' $false
$openYaml = Join-Path $inputs.FullName 'open-body.yaml'
$closedYaml = Join-Path $inputs.FullName 'closed-body.yaml'
$yaml = @'
openapi: 3.0.3
info: { title: Workflow Tasks, version: 1.0.0 }
paths:
  /tasks:
    post:
      requestBody:
        content:
          application/json:
            schema: { type: object, additionalProperties: true }
      responses:
        '204': { description: Accepted }
'@
[IO.File]::WriteAllText($openYaml, $yaml)
[IO.File]::WriteAllText($closedYaml, $yaml.Replace('additionalProperties: true', 'additionalProperties: false'))
$relaxed = Join-Path $inputs.FullName 'relaxed-policy.yml'
$infoThreshold = Join-Path $inputs.FullName 'info-policy.yml'
[IO.File]::WriteAllText($relaxed, "version: 1`ncompatibility: {fail_on: [ERR], fail_on_unknown: false}`n")
[IO.File]::WriteAllText($infoThreshold, "version: 1`ncompatibility: {fail_on: [INFO], fail_on_unknown: true}`n")
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
$externalRef = Write-Variant 'external-reference' { param($spec)
    $spec.paths.'/tasks'.get.responses.'200'.content.'application/json'.schema.'$ref' = './other.json'
}
$malformed = Join-Path $inputs.FullName 'malformed.json'
[IO.File]::WriteAllText($malformed, '{"openapi":')

$examples = Join-Path $PSScriptRoot '../examples'
$base30 = Join-Path $examples 'base.yaml'
$added = Join-Path $examples 'compatible.yaml'
$breaking30 = Join-Path $examples 'breaking.yaml'
$linkedExamples = Join-Path $inputs.FullName 'linked examples'
if (-not (Test-Path -LiteralPath $linkedExamples)) {
    $linkType = if ([IO.Path]::DirectorySeparatorChar -eq '\') { 'Junction' } else { 'SymbolicLink' }
    $null = New-Item -ItemType $linkType -Path $linkedExamples -Target (Resolve-Path -LiteralPath $examples).Path
}
$linkedSpec = Join-Path $linkedExamples 'base.yaml'
$reordered = Join-Path $inputs.FullName 'reordered response.yaml'
$reorderedSpec = (Get-Content -Raw -LiteralPath $breaking30) -replace '(?m)(  title: Guardian Example)\r?\n(  version: 1\.0\.0)', ('$2' + [Environment]::NewLine + '$1')
[IO.File]::WriteAllText($reordered, "# Same schema, different order and lines$([Environment]::NewLine)$reorderedSpec")

$goldens = @(
    @{ Name = 'validate-yaml-30'; Args = @('validate', $base30); Code = 0 }
    @{ Name = 'validate-json-31'; Args = @('validate', $base31); Code = 0 }
    @{ Name = 'rooted-validation'; Args = @('validate', $base31, '--input-root', $inputs.FullName); Code = 0 }
    @{ Name = 'outside-input-root'; Args = @('validate', $base30, '--input-root', $inputs.FullName);
       Code = 3; Error = 'outside the input root' }
    @{ Name = 'linked-input-directory'; Args = @('validate', $linkedSpec, '--input-root', $inputs.FullName);
       Code = 3; Error = 'symbolic links or reparse points' }
    @{ Name = 'external-reference'; Args = @('validate', $externalRef); Code = 3; Error = 'internal JSON Pointer' }
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
    @{ Name = 'response-became-optional'; Args = @('compare', '--base', $base31, '--candidate', $optionalResponse, '--policy', $relaxed);
       Code = 2; Status = 'FAIL'; Rule = 'response-property-became-optional'; Severity = 'ERR'; Method = 'GET'; Path = '/tasks' }
    @{ Name = 'request-media-type-removed'; Args = @('compare', '--base', $base31, '--candidate', $changedMediaType, '--policy', $relaxed);
       Code = 2; Status = 'FAIL'; Rule = 'request-body-media-type-removed'; Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'request-media-type-removed-strict'; Args = @('compare', '--base', $base31, '--candidate', $changedMediaType);
       Code = 2; Status = 'FAIL'; Rule = 'request-body-media-type-added'; Severity = 'INFO'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'optional-query'; Args = @('compare', '--base', $base31, '--candidate', $optionalQuery);
       Code = 0; Status = 'PASS'; Rule = 'new-optional-request-parameter'; Severity = 'INFO'; Method = 'GET'; Path = '/tasks' }
    @{ Name = 'optional-query-info-threshold'; Args = @('compare', '--base', $base31, '--candidate', $optionalQuery, '--policy', $infoThreshold);
       Code = 2; Status = 'FAIL'; Rule = 'new-optional-request-parameter'; Severity = 'INFO'; Method = 'GET'; Path = '/tasks' }
    @{ Name = 'optional-body'; Args = @('compare', '--base', $closedBody, '--candidate', $closedOptional);
       Code = 0; Status = 'PASS'; Rule = 'new-optional-request-property'; Severity = 'INFO'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'optional-body-narrowed'; Args = @('compare', '--base', $base31, '--candidate', $optionalBody);
       Code = 2; Status = 'FAIL'; Rule = 'request-body-optional-property-narrowed'; Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'typed-additional-closed'; Args = @('compare', '--base', $typedAdditional, '--candidate', $closedBody);
       Code = 2; Status = 'FAIL'; Rule = 'request-body-additional-properties-disallowed'; Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'allof-closure-unproven'; Args = @('compare', '--base', $allOfOpen, '--candidate', $allOfClosed);
       Code = 2; Status = 'INDETERMINATE'; Rule = 'request-body-compatibility-unproven'; Severity = 'UNKNOWN'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'allof-docs-only'; Args = @('compare', '--base', $allOfOpen, '--candidate', $allOfAnnotated);
       Code = 0; Status = 'PASS'; Empty = $true }
    @{ Name = 'capped-object-already-closed'; Args = @('compare', '--base', $cappedOpen, '--candidate', $cappedClosed);
       Code = 0; Status = 'PASS'; Empty = $true }
    @{ Name = 'min-length-narrowed'; Args = @('compare', '--base', $length1, '--candidate', $length3);
       Code = 2; Status = 'FAIL'; Rule = 'request-property-min-length-increased'; Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'min-length-widened'; Args = @('compare', '--base', $length3, '--candidate', $length1);
       Code = 0; Status = 'PASS' }
    @{ Name = 'min-length-set'; Args = @('compare', '--base', $lengthAbsent, '--candidate', $length3);
       Code = 2; Status = 'FAIL'; Rule = 'request-property-min-length-set'; Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'min-length-unset'; Args = @('compare', '--base', $length3, '--candidate', $lengthAbsent);
       Code = 0; Status = 'PASS'; Rule = 'request-property-min-length-unset'; Severity = 'INFO'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'closed-request-body-json'; Args = @('compare', '--base', $base31, '--candidate', $closedBody,
           '--out', (Join-Path $results.FullName 'closed-body-report'));
       Code = 2; Status = 'FAIL'; FindingCount = 1; Rule = 'request-body-additional-properties-disallowed';
       Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'closed-request-body-yaml'; Args = @('compare', '--base', $openYaml, '--candidate', $closedYaml, '--policy', $relaxed);
       Code = 2; Status = 'FAIL'; FindingCount = 1; Rule = 'request-body-additional-properties-disallowed';
       Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'opened-request-body'; Args = @('compare', '--base', $closedYaml, '--candidate', $openYaml);
       Code = 0; Status = 'PASS'; Empty = $true }
    @{ Name = 'additional-properties-on-scalar'; Args = @('compare', '--base', $scalarBody, '--candidate', $closedScalar);
       Code = 0; Status = 'PASS'; Empty = $true }
    @{ Name = 'closed-request-array-item-through-ref'; Args = @('compare', '--base', $arrayBody, '--candidate', $closedArrayBody);
       Code = 2; Status = 'FAIL'; FindingCount = 1; Rule = 'request-body-additional-properties-disallowed';
       Severity = 'ERR'; Method = 'POST'; Path = '/tasks' }
    @{ Name = 'docs-only'; Args = @('compare', '--base', $base31, '--candidate', $description); Code = 0; Empty = $true }
    @{ Name = 'internal-ref'; Args = @('compare', '--base', $base31, '--candidate', $throughRef); Code = 2;
       Rule = 'response-property-type-changed'; Severity = 'ERR'; Method = 'GET'; Path = '/tasks' }
    @{ Name = 'malformed'; Args = @('validate', $malformed); Code = 3; Error = 'syntax' }
    @{ Name = 'unsupported'; Args = @('validate', $unsupported); Code = 3; Error = 'OpenAPI version' }
    @{ Name = 'mixed-dialects'; Args = @('compare', '--base', $base30, '--candidate', $base31);
       Code = 3; Error = 'same OpenAPI major and minor' }
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
        if ($Case.Status -and $comparison.status -ne $Case.Status) { throw "$($Case.Name) returned an unexpected verdict." }
        if ($Case.ContainsKey('FindingCount') -and $findings.Count -ne $Case.FindingCount) {
            throw "$($Case.Name) returned an unexpected finding count."
        }
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
    $closedBodyFingerprint = $null
    foreach ($golden in $goldens) {
        $fingerprint = Invoke-Case $golden
        if ($golden.Name -eq 'response-type') { $expectedFingerprint = $fingerprint }
        if ($golden.Name -eq 'closed-request-body-json') { $closedBodyFingerprint = $fingerprint }
    }

    $reformattedFingerprint = Invoke-Case @{
        Name = 'closed-request-body-reformatted'; Args = @('compare', '--base', $base31, '--candidate', $closedBodyReformatted);
        Code = 2; Status = 'FAIL'; FindingCount = 1; Rule = 'request-body-additional-properties-disallowed';
        Severity = 'ERR'; Method = 'POST'; Path = '/tasks'
    }
    if ($reformattedFingerprint -ne $closedBodyFingerprint) { throw 'Fingerprint changed after reformatting the closed request body.' }

    $actualFingerprint = Invoke-Case @{
        Name = 'reordered-yaml'; Args = @('compare', '--candidate', $reordered, '--base', $base30); Code = 2;
        Rule = 'response-property-type-changed'; Severity = 'ERR'; Method = 'GET'; Path = '/runs'
    }
    if ($actualFingerprint -ne $expectedFingerprint) { throw 'Fingerprint changed after reordering YAML.' }
    Invoke-Case @{ Name = 'semantic-validation'; Args = @('validate', $semanticError); Code = 3; Error = 'schema-type-unsupported' }
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
