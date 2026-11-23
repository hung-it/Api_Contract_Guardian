# API Contract Guardian

API Contract Guardian is a .NET CLI project for checking backward compatibility between OpenAPI versions in GitLab merge request pipelines. The first milestone provides the CLI entry point, a stable exit-code contract, and repeatable probes against a pinned diff engine.

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
```

| Exit code | Meaning |
|---|---|
| `0` | Success |
| `1` | Invalid command-line arguments |
| `2` | Compatibility policy failed |
| `3` | Invalid or unsupported spec |
| `4` | Diff engine or internal error |
| `5` | Invalid or untrusted policy |

The help and version commands use codes `0` and `1`. Core reserves codes `2` through `5` for the compatibility gate.

## FlowForge contract probe

Use an isolated FlowForge checkout of commit `05d7e7c6830474cc17dbf0d1758da290fa8497c7`. From its `backend` directory, use `uv sync --frozen --python 3.12`, then run `uv run --frozen python <Guardian>/scripts/export-flowforge.py <output-directory>`.

The script exports OpenAPI twice, checks that the bytes match and that each reference resolves inside the document, then writes a second contract with an integer `RunOutput.run_id`. Run `probe-oasdiff.ps1 -Engine $engine -FlowForgeContracts <output-directory>` from Guardian to check the unchanged and breaking comparisons.

## CI

[GitHub](https://github.com/hung-it/Api_Contract_Guardian) is the source repository. Push the same verification commit to [GitLab](https://gitlab.com/hung-it/api_contract_guardian), then confirm that the pipeline ran on the same full SHA. The `week1` job uses a fresh Linux checkout and publishes the CLI, xUnit test results, and engine output as artifacts.
