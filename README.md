# API Contract Guardian

API Contract Guardian is a .NET CLI that validates local OpenAPI documents and detects backward compatibility changes with a pinned diff engine.

## Quick start

Install the .NET 10.0.201 SDK specified by `global.json`. Run from the repository root.

```text
dotnet restore Guardian.slnx --locked-mode
dotnet build Guardian.slnx -c Release --no-restore -warnaserror
dotnet format Guardian.slnx --verify-no-changes --no-restore
dotnet test Guardian.slnx -c Release --no-build --logger trx --results-directory artifacts/tests
dotnet publish src/Guardian.Cli -c Release --no-build -o artifacts/guardian
dotnet artifacts/guardian/guardian.dll --help
dotnet artifacts/guardian/guardian.dll version
```

Run the CLI smoke tests and OpenAPI probes in PowerShell with `tar` available. The installer verifies the archive checksum before extraction. To use an existing release archive on an offline machine, pass `-ArchivePath` to the installer.

```powershell
& ./scripts/verify-cli.ps1
$engine = & ./scripts/install-oasdiff.ps1
& ./scripts/probe-oasdiff.ps1 -Engine $engine
& ./scripts/verify-contracts.ps1 -Engine $engine
```

Set `GUARDIAN_OASDIFF_PATH` to the absolute path of the installed engine. Then run Guardian against a spec or a pair of specs.

```powershell
$env:GUARDIAN_OASDIFF_PATH = $engine
dotnet artifacts/guardian/guardian.dll validate examples/base.yaml
dotnet artifacts/guardian/guardian.dll compare --base examples/base.yaml --candidate examples/breaking.yaml
```

Add `--policy .guardian.yml --out artifacts/reports` to write JSON and Markdown reports. The v1 policy uses a severity threshold from `compatibility.fail_on` (for example, `[WARN]` blocks WARN and ERR). `compatibility.fail_on_unknown` controls whether an unknown finding blocks the comparison. Optional `rules` set an effective severity for known rule IDs before evaluation. The built-in policy blocks ERR and UNKNOWN.

Both report formats include the verdict, input hashes when available, and engine and Guardian versions. CI provides baseline and candidate commits and its pipeline ID. See the [JSON report schema](schemas/report-v1.schema.json).

The CLI accepts single-file UTF-8 JSON, YAML or YML using OpenAPI 3.0.x or 3.1.x. Compare uses pairs from the same minor version. Input limits are 5 MiB, 64 levels and 2,048 references per file. References use internal JSON Pointers and the default schema dialect. The supported YAML subset uses unique scalar keys and plain YAML without aliases, merge keys or explicit tags. Reusable `components.pathItems` and dynamic schema references are outside the current support matrix.

Compare writes one JSON object to stdout with the engine version and sorted findings. Findings include the rule, severity, endpoint, source coordinates when available, and a stable fingerprint. The fingerprint excludes the message and source coordinates. Invalid input and engine failures write a diagnostic to stderr.

| Exit code | Meaning |
|---|---|
| `0` | Success |
| `1` | Invalid command-line arguments |
| `2` | Compare found a breaking or unknown change |
| `3` | Invalid or unsupported spec |
| `4` | Diff engine or internal error |
| `5` | Invalid or missing explicit policy |

The help and version commands use codes `0` and `1`. With `--out`, validation, engine and policy failures produce an `INDETERMINATE` report with a diagnostic.

## FlowForge contract probe

Use an isolated FlowForge checkout of commit `05d7e7c6830474cc17dbf0d1758da290fa8497c7`. From its `backend` directory, use `uv sync --frozen --python 3.12`, then run `uv run --frozen python <Guardian>/scripts/export-flowforge.py <output-directory>`.

The script exports OpenAPI twice, checks that the bytes match and that each reference resolves inside the document, then writes a second contract with an integer `RunOutput.run_id`. Run `verify-contracts.ps1 -Engine $engine -FlowForgeContracts <output-directory>` from Guardian to check the unchanged and breaking comparisons through the published CLI.

## CI

[GitHub](https://github.com/hung-it/Api_Contract_Guardian) is the source repository. Push the same verification commit to [GitLab](https://gitlab.com/hung-it/api_contract_guardian), then confirm that the pipeline ran on the same full SHA. The `verify` job uses a fresh Linux checkout and publishes the CLI, xUnit results, engine probes and Guardian comparison results as artifacts.

The protected `main` branch publishes the Linux AMD64 CLI image on changes to the build inputs. The detached merge-request demo runs `api_contract_guard` using a registry digest. The job reads the baseline and policy from one fetched commit of its protected target and reads the candidate from the pipeline commit. Open both reports in the job's `reports/` artifact to inspect the verdict, source commits and input hashes.

The trust demonstrated here covers the source of the baseline and policy. Independent enforcement of the job configuration and required merge checks are Week 4 acceptance criteria.
