# Week 1 verification

| Task | Evidence |
|---|---|
| T01 | GitHub source repository and GitLab project `89342282` confirmed. Windows x64 reports .NET SDK 10.0.201 and Docker Engine 29.8.1 with Linux containers. GitLab returned HTTP 200 for the project's Container Registry repository list, which was empty at verification. Runner execution is recorded with T05. |
| T02 | Verified the Windows archive SHA-256 and ran `oasdiff v1.38.0`. Validation passed for all three OpenAPI 3.0.3 examples. The unchanged comparison returned zero findings, `GET /health` produced `endpoint-added` with no breaking findings, and `GET /runs` produced `response-property-type-changed` at error severity. |
| T03 | Exported from FlowForge commit `05d7e7c6830474cc17dbf0d1758da290fa8497c7` in an isolated checkout with Python 3.12.14 after `uv sync --frozen --no-dev`. Both OpenAPI 3.1.0 exports had 21 paths, 66 resolvable internal references and SHA-256 `5ad4640d8c68cbbd27a7381c84182284bc5173f6fbf8019bb057ac99597a0b43`. The unchanged comparison returned zero findings. Changing the shared `RunOutput.run_id` from UUID string to integer produced three breaking findings across its consumers, including `GET /api/v1/runs/{run_id}`. |
| T04 | Locked restore, Release build with zero warnings, format check, 5 xUnit cases and all 5 CLI behavior checks passed from a clean local checkout. The CLI checks verified usage, version, argument errors, exit status and output streams against the published artifact. |
| T05 | GitLab pipeline YAML parsed locally. The job defines a clean checkout, locked restore, Release build, format check, xUnit, published CLI tests and pinned engine probes. A server-side pipeline with the matching GitHub SHA is still required. |

The first adapter milestone should cover the OpenAPI 3.1.0 constructs exercised by the FlowForge export and retain the 3.0.3 regression examples. These probes cover the exercised cases for each dialect.
