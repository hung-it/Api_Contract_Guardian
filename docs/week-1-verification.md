# Week 1 verification

| Task | Evidence |
|---|---|
| T01 | GitHub source repository and GitLab project URL confirmed. Windows x64 reports .NET SDK 10.0.201 and Docker client 29.8.1. The current Windows session reports access denied on the Docker Engine pipe. GitLab runner execution and registry access remain unverified. |
| T02 | The three OpenAPI 3.0.3 fixtures parse as YAML. The compatible fixture changes only the addition of `GET /health`. The breaking fixture changes only the scalar type of `GET /runs`'s `run_id` field. The pinned upstream release and archive hashes are recorded in the installer. Real-engine validation and changelog output remain pending because the release download was unreachable from the shell. |
| T03 | Exported from FlowForge commit `05d7e7c6830474cc17dbf0d1758da290fa8497c7` in an isolated source directory. Used Python 3.12.14 and the cached FlowForge environment after verifying all 40 applicable production dependency versions against the frozen lock export. OpenAPI 3.1.0 has 21 paths and 66 resolvable internal references. Both exports produced SHA-256 `5ad4640d8c68cbbd27a7381c84182284bc5173f6fbf8019bb057ac99597a0b43`. The candidate changes only `RunOutput.run_id` from UUID string to integer. The two engine comparisons remain pending. |
| T04 | Locked restore from the local NuGet cache, Release build with zero warnings, format check, 5 xUnit cases and all 5 CLI behavior checks passed. The CLI checks ran against the published artifact and verified usage, version, argument errors, exit status and output streams. |
| T05 | GitLab pipeline YAML parsed locally. The job defines a clean checkout, locked restore, Release build, format check, xUnit, published CLI tests and pinned engine probes. A server-side pipeline with the matching GitHub SHA is still required. |

The first adapter milestone should cover OpenAPI 3.1.0 for the constructs used by the FlowForge export. The 3.0.3 examples provide a second fixture set. Compatibility behavior for either dialect still needs the real-engine run.
